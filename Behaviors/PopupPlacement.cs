using System.Windows;
using System.Windows.Controls.Primitives;

namespace EasyGet.Behaviors;

/// <summary>
/// Lets a control choose the placement of a popup defined inside its template.
/// </summary>
public static class PopupPlacement
{
    public static readonly DependencyProperty PlacementProperty = DependencyProperty.RegisterAttached(
        "Placement",
        typeof(PlacementMode),
        typeof(PopupPlacement),
        new PropertyMetadata(PlacementMode.Bottom));

    public static readonly DependencyProperty VerticalOffsetProperty = DependencyProperty.RegisterAttached(
        "VerticalOffset",
        typeof(double),
        typeof(PopupPlacement),
        new PropertyMetadata(0d));

    public static PlacementMode GetPlacement(DependencyObject element)
        => (PlacementMode)element.GetValue(PlacementProperty);

    public static void SetPlacement(DependencyObject element, PlacementMode value)
        => element.SetValue(PlacementProperty, value);

    public static double GetVerticalOffset(DependencyObject element)
        => (double)element.GetValue(VerticalOffsetProperty);

    public static void SetVerticalOffset(DependencyObject element, double value)
        => element.SetValue(VerticalOffsetProperty, value);
}
