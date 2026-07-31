namespace EasyGet.Views;

public partial class SettingsView : System.Windows.Controls.UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }

    private void ShowPerformanceDetailsButton_Click(
        object sender,
        System.Windows.RoutedEventArgs e)
    {
        var dialog = new PerformanceRecommendationDialog
        {
            DataContext = DataContext
        };
        if (System.Windows.Window.GetWindow(this) is { } owner)
            dialog.Owner = owner;

        dialog.ShowDialog();
    }

    private void Hyperlink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri)
            {
                UseShellExecute = true
            });
            e.Handled = true;
        }
        catch (System.Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SettingsView] Open link failed: {ex.Message}");
        }
    }
}
