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
