using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace EasyGet.Behaviors;

public static class LayoutDiagnostics
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled",
        typeof(bool),
        typeof(LayoutDiagnostics),
        new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject element)
        => (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value)
        => element.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
#if DEBUG
        if (dependencyObject is not FrameworkElement root)
            return;

        if ((bool)e.NewValue)
            Attach(root);
        else
            Detach(root);
#endif
    }

#if DEBUG
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State",
        typeof(DiagnosticState),
        typeof(LayoutDiagnostics));

    private const double OverflowTolerance = 1.5;

    private static void Attach(FrameworkElement root)
    {
        if (root.GetValue(StateProperty) is DiagnosticState)
            return;

        var state = new DiagnosticState(root);
        root.SetValue(StateProperty, state);
        root.Loaded += state.OnLoaded;
        root.Unloaded += state.OnUnloaded;
        root.LayoutUpdated += state.OnLayoutUpdated;
    }

    private static void Detach(FrameworkElement root)
    {
        if (root.GetValue(StateProperty) is not DiagnosticState state)
            return;

        root.Loaded -= state.OnLoaded;
        root.Unloaded -= state.OnUnloaded;
        root.LayoutUpdated -= state.OnLayoutUpdated;
        root.ClearValue(StateProperty);
    }

    private static IReadOnlyList<string> FindIssues(FrameworkElement root)
    {
        var issues = new List<string>();
        Visit(root, root, issues);
        return issues;
    }

    private static void Visit(DependencyObject current, FrameworkElement root, ICollection<string> issues)
    {
        if (current is FrameworkElement element
            && element.Visibility == Visibility.Visible
            && element.IsArrangeValid
            && element.ActualWidth > 0
            && element.ActualHeight > 0)
        {
            CheckMeasuredText(element, issues);
            CheckNearestFrameBounds(element, root, issues);
        }

        var childCount = VisualTreeHelper.GetChildrenCount(current);
        for (var index = 0; index < childCount; index++)
            Visit(VisualTreeHelper.GetChild(current, index), root, issues);
    }

    private static void CheckMeasuredText(FrameworkElement element, ICollection<string> issues)
    {
        switch (element)
        {
            case TextBlock textBlock when !string.IsNullOrWhiteSpace(textBlock.Text):
                CheckTextBlock(textBlock, issues);
                break;
            case ButtonBase button when button.Content is string buttonText:
                CheckControlText(button, buttonText, button.Padding, issues);
                break;
            case ComboBox comboBox when comboBox.SelectionBoxItem is not null:
                CheckControlText(comboBox, comboBox.SelectionBoxItem.ToString() ?? string.Empty,
                    new Thickness(10, 0, 30, 0), issues);
                break;
            case TextBox textBox when !textBox.AcceptsReturn:
                CheckControlText(textBox, string.IsNullOrEmpty(textBox.Text) ? "Ag" : textBox.Text,
                    textBox.Padding, issues, checkWidth: false);
                break;
        }
    }

    private static void CheckTextBlock(TextBlock textBlock, ICollection<string> issues)
    {
        var formatted = CreateFormattedText(
            textBlock,
            textBlock.Text,
            textBlock.FontFamily,
            textBlock.FontStyle,
            textBlock.FontWeight,
            textBlock.FontStretch,
            textBlock.FontSize,
            textBlock.Foreground);

        if (textBlock.TextWrapping == TextWrapping.NoWrap)
        {
            if (textBlock.TextTrimming == TextTrimming.None
                && formatted.WidthIncludingTrailingWhitespace > textBlock.ActualWidth + OverflowTolerance)
            {
                issues.Add($"untrimmed text overflow: {Describe(textBlock)} needs {formatted.WidthIncludingTrailingWhitespace:F1}px, has {textBlock.ActualWidth:F1}px");
            }

            if (formatted.Height > textBlock.ActualHeight + OverflowTolerance)
                issues.Add($"vertical text clipping: {Describe(textBlock)} needs {formatted.Height:F1}px, has {textBlock.ActualHeight:F1}px");

            return;
        }

        formatted.MaxTextWidth = Math.Max(1, textBlock.ActualWidth);
        if (textBlock.TextTrimming == TextTrimming.None
            && formatted.Height > textBlock.ActualHeight + OverflowTolerance)
        {
            issues.Add($"wrapped text clipping: {Describe(textBlock)} needs {formatted.Height:F1}px, has {textBlock.ActualHeight:F1}px");
        }
    }

    private static void CheckControlText(
        Control control,
        string text,
        Thickness reservedSpace,
        ICollection<string> issues,
        bool checkWidth = true)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        var formatted = CreateFormattedText(
            control,
            text,
            control.FontFamily,
            control.FontStyle,
            control.FontWeight,
            control.FontStretch,
            control.FontSize,
            control.Foreground);
        var availableWidth = control.ActualWidth - reservedSpace.Left - reservedSpace.Right;
        var availableHeight = control.ActualHeight - reservedSpace.Top - reservedSpace.Bottom;

        if (checkWidth && formatted.WidthIncludingTrailingWhitespace > availableWidth + OverflowTolerance)
            issues.Add($"control text overflow: {Describe(control)} needs {formatted.WidthIncludingTrailingWhitespace:F1}px, has {availableWidth:F1}px");

        if (formatted.Height > availableHeight + OverflowTolerance)
            issues.Add($"control text clipping: {Describe(control)} needs {formatted.Height:F1}px, has {availableHeight:F1}px");
    }

    private static void CheckNearestFrameBounds(FrameworkElement element, FrameworkElement root, ICollection<string> issues)
    {
        if (element is not TextBlock && element is not Control)
            return;

        var frame = FindNearestFrame(element, root);
        if (frame is null || ReferenceEquals(frame, element))
            return;

        try
        {
            var bounds = element.TransformToAncestor(frame)
                .TransformBounds(new Rect(new Point(), element.RenderSize));
            var frameBounds = new Rect(new Point(), frame.RenderSize);
            frameBounds.Inflate(OverflowTolerance, OverflowTolerance);

            if (!frameBounds.Contains(bounds))
                issues.Add($"element outside frame: {Describe(element)} bounds {bounds} exceed {Describe(frame)} {frame.RenderSize}");
        }
        catch (InvalidOperationException)
        {
            // The visual tree can change between traversal and transformation.
        }
    }

    private static FrameworkElement? FindNearestFrame(DependencyObject element, FrameworkElement root)
    {
        var parent = VisualTreeHelper.GetParent(element);
        while (parent is not null && !ReferenceEquals(parent, root))
        {
            if (parent is Border border
                && (border.BorderThickness != default || border.ClipToBounds))
            {
                return border;
            }

            if (parent is Control control)
                return control;

            parent = VisualTreeHelper.GetParent(parent);
        }

        return null;
    }

    private static FormattedText CreateFormattedText(
        Visual visual,
        string text,
        FontFamily family,
        FontStyle style,
        FontWeight weight,
        FontStretch stretch,
        double size,
        Brush foreground)
    {
        return new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(family, style, weight, stretch),
            size,
            foreground,
            VisualTreeHelper.GetDpi(visual).PixelsPerDip);
    }

    private static string Describe(FrameworkElement element)
    {
        var name = !string.IsNullOrWhiteSpace(element.Name)
            ? element.Name
            : AutomationProperties.GetName(element);
        if (string.IsNullOrWhiteSpace(name) && element is ContentControl contentControl)
            name = contentControl.Content?.ToString();

        return string.IsNullOrWhiteSpace(name)
            ? element.GetType().Name
            : $"{element.GetType().Name}[{name}]";
    }

    private sealed class DiagnosticState(FrameworkElement root)
    {
        private bool _scanQueued;
        private string _lastFingerprint = string.Empty;

        public void OnLoaded(object sender, RoutedEventArgs e) => QueueScan();

        public void OnUnloaded(object sender, RoutedEventArgs e)
        {
            _scanQueued = false;
            _lastFingerprint = string.Empty;
        }

        public void OnLayoutUpdated(object? sender, EventArgs e) => QueueScan();

        private void QueueScan()
        {
            if (_scanQueued || !root.IsLoaded)
                return;

            _scanQueued = true;
            root.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
            {
                _scanQueued = false;
                if (!root.IsLoaded)
                    return;

                var issues = FindIssues(root);
                var fingerprint = string.Join('\n', issues);
                if (fingerprint == _lastFingerprint)
                    return;

                _lastFingerprint = fingerprint;
                if (issues.Count == 0)
                {
                    Debug.WriteLine("[LayoutDiagnostics] No clipping or framed-control overflow detected.");
                    return;
                }

                var message = new StringBuilder()
                    .Append("[LayoutDiagnostics] ")
                    .Append(issues.Count)
                    .AppendLine(" potential layout issue(s):");
                foreach (var issue in issues)
                    message.Append("  - ").AppendLine(issue);
                Debug.WriteLine(message.ToString());
            });
        }
    }
#endif
}
