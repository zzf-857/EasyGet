using System.Windows;
using System.Windows.Controls;

namespace EasyGet.Controls;

/// <summary>
/// Lazily creates each page view once and reuses it when navigation returns to
/// the same view-model instance.
/// </summary>
public sealed class CachedPageHost : ContentControl
{
    private readonly Dictionary<object, object> _pageViews =
        new(ReferenceEqualityComparer.Instance);

    public static readonly DependencyProperty PageProperty = DependencyProperty.Register(
        nameof(Page),
        typeof(object),
        typeof(CachedPageHost),
        new PropertyMetadata(null, OnPageChanged));

    public object? Page
    {
        get => GetValue(PageProperty);
        set => SetValue(PageProperty, value);
    }

    private static void OnPageChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs e)
    {
        ((CachedPageHost)dependencyObject).ShowPage(e.NewValue);
    }

    private void ShowPage(object? page)
    {
        if (page is null)
        {
            Content = null;
            return;
        }

        if (!_pageViews.TryGetValue(page, out var view))
        {
            var template = TryFindResource(new DataTemplateKey(page.GetType())) as DataTemplate;
            view = template?.LoadContent() ?? page;
            if (view is FrameworkElement element)
                element.DataContext = page;

            _pageViews.Add(page, view);
        }

        Content = view;
    }
}
