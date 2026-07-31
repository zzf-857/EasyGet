using System.Windows;
using System.Windows.Input;

namespace EasyGet.Views;

public partial class PerformanceRecommendationDialog : Window
{
    public PerformanceRecommendationDialog()
    {
        InitializeComponent();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
        => Close();
}
