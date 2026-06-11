using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace EasyGet.Behaviors;

public static class Motion
{
    public static readonly DependencyProperty PageEnterProperty =
        DependencyProperty.RegisterAttached(
            "PageEnter",
            typeof(bool),
            typeof(Motion),
            new PropertyMetadata(false, OnPageEnterChanged));

    public static void SetPageEnter(DependencyObject element, bool value)
    {
        element.SetValue(PageEnterProperty, value);
    }

    public static bool GetPageEnter(DependencyObject element)
    {
        return (bool)element.GetValue(PageEnterProperty);
    }

    private static void OnPageEnterChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is not FrameworkElement element)
            return;

        element.Loaded -= OnPageEnterLoaded;
        if (e.NewValue is true)
            element.Loaded += OnPageEnterLoaded;
    }

    private static void OnPageEnterLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || !GetPageEnter(element))
            return;

        var translate = EnsureTranslateTransform(element);
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = new Duration(TimeSpan.FromMilliseconds(160));

        element.Opacity = 0;
        translate.Y = 10;

        element.BeginAnimation(
            UIElement.OpacityProperty,
            new DoubleAnimation(1, duration)
            {
                EasingFunction = easing
            });

        translate.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(0, duration)
            {
                EasingFunction = easing
            });
    }

    // ========== AnimateRemove 附加属性 ==========

    public static readonly DependencyProperty AnimateRemoveProperty =
        DependencyProperty.RegisterAttached(
            "AnimateRemove",
            typeof(bool),
            typeof(Motion),
            new PropertyMetadata(false, OnAnimateRemoveChanged));

    public static readonly DependencyProperty RemoveCommandProperty =
        DependencyProperty.RegisterAttached(
            "RemoveCommand",
            typeof(System.Windows.Input.ICommand),
            typeof(Motion),
            new PropertyMetadata(null));

    public static readonly DependencyProperty RemoveParameterProperty =
        DependencyProperty.RegisterAttached(
            "RemoveParameter",
            typeof(object),
            typeof(Motion),
            new PropertyMetadata(null));

    public static void SetAnimateRemove(DependencyObject element, bool value)
    {
        element.SetValue(AnimateRemoveProperty, value);
    }

    public static bool GetAnimateRemove(DependencyObject element)
    {
        return (bool)element.GetValue(AnimateRemoveProperty);
    }

    public static void SetRemoveCommand(DependencyObject element, System.Windows.Input.ICommand value)
    {
        element.SetValue(RemoveCommandProperty, value);
    }

    public static System.Windows.Input.ICommand GetRemoveCommand(DependencyObject element)
    {
        return (System.Windows.Input.ICommand)element.GetValue(RemoveCommandProperty);
    }

    public static void SetRemoveParameter(DependencyObject element, object value)
    {
        element.SetValue(RemoveParameterProperty, value);
    }

    public static object GetRemoveParameter(DependencyObject element)
    {
        return element.GetValue(RemoveParameterProperty);
    }

    private static void OnAnimateRemoveChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is not System.Windows.Controls.Button button)
            return;

        button.Click -= OnRemoveButtonClick;
        if (e.NewValue is true)
            button.Click += OnRemoveButtonClick;
    }

    private static void OnRemoveButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button || !GetAnimateRemove(button))
            return;

        var parentItem = FindAncestor<System.Windows.Controls.ListBoxItem>(button);
        if (parentItem == null)
        {
            ExecuteRemoveCommand(button);
            return;
        }

        var translate = EnsureTranslateTransform(parentItem);
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = new Duration(TimeSpan.FromMilliseconds(180));

        var fadeAnim = new DoubleAnimation(0, duration) { EasingFunction = easing };
        var slideAnim = new DoubleAnimation(-50, duration) { EasingFunction = easing };

        fadeAnim.Completed += (s, ev) =>
        {
            ExecuteRemoveCommand(button);
            parentItem.Dispatcher.BeginInvoke(new Action(() =>
            {
                var listBox = FindAncestor<System.Windows.Controls.ListBox>(parentItem);
                if (listBox != null && listBox.Items.Contains(parentItem.DataContext))
                {
                    var durationFast = new Duration(TimeSpan.FromMilliseconds(150));
                    parentItem.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, durationFast) { EasingFunction = easing });
                    translate.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, durationFast) { EasingFunction = easing });
                }
            }));
        };

        parentItem.BeginAnimation(UIElement.OpacityProperty, fadeAnim);
        translate.BeginAnimation(TranslateTransform.XProperty, slideAnim);
    }

    private static void ExecuteRemoveCommand(System.Windows.Controls.Button button)
    {
        var command = GetRemoveCommand(button);
        if (command != null)
        {
            var parameter = GetRemoveParameter(button);
            if (command.CanExecute(parameter))
            {
                command.Execute(parameter);
            }
        }
    }

    private static T? FindAncestor<T>(DependencyObject current) where T : DependencyObject
    {
        do
        {
            if (current is T ancestor)
                return ancestor;
            current = VisualTreeHelper.GetParent(current);
        }
        while (current != null);
        return null;
    }

    private static TranslateTransform EnsureTranslateTransform(UIElement element)
    {
        if (element.RenderTransform is TranslateTransform translate)
            return translate;

        if (element.RenderTransform is TransformGroup group)
        {
            var existingTranslate = group.Children.OfType<TranslateTransform>().FirstOrDefault();
            if (existingTranslate is not null)
                return existingTranslate;

            translate = new TranslateTransform();
            group.Children.Add(translate);
            return translate;
        }

        translate = new TranslateTransform();
        if (element.RenderTransform is not null && element.RenderTransform != Transform.Identity)
        {
            var existing = element.RenderTransform;
            group = new TransformGroup();
            group.Children.Add(existing);
            group.Children.Add(translate);
            element.RenderTransform = group;
            return translate;
        }

        element.RenderTransform = translate;
        return translate;
    }
}
