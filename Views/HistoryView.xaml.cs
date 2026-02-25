using EasyGet.ViewModels;

namespace EasyGet.Views;

public partial class HistoryView : System.Windows.Controls.UserControl
{
    public HistoryView()
    {
        InitializeComponent();
    }

    private async void HistoryView_Loaded(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is HistoryViewModel vm)
        {
            await vm.LoadHistory();
        }
    }
}
