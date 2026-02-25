using System.Collections.Specialized;
using EasyGet.ViewModels;

namespace EasyGet.Views;

public partial class DownloadView : System.Windows.Controls.UserControl
{
    public DownloadView()
    {
        InitializeComponent();

        Loaded += (_, _) =>
        {
            if (DataContext is DownloadViewModel vm)
            {
                ((INotifyCollectionChanged)vm.LogLines).CollectionChanged += (_, _) =>
                {
                    if (vm.LogLines.Count > 0)
                    {
                        LogList.ScrollIntoView(vm.LogLines[^1]);
                    }
                };
            }
        };
    }
}
