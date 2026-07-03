using EasyGet.ViewModels;

namespace EasyGet.Views;

public partial class DouyinView : System.Windows.Controls.UserControl
{
    public DouyinView()
    {
        InitializeComponent();
    }

    private async void DouyinView_Loaded(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is DouyinViewModel vm)
        {
            await vm.LoadDouyinWorkspaceCommand.ExecuteAsync(null);
        }
    }
}
