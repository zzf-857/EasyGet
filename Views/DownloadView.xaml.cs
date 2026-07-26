using System.Collections.Specialized;
using EasyGet.ViewModels;

namespace EasyGet.Views;

public partial class DownloadView : System.Windows.Controls.UserControl
{
    private DownloadViewModel? _observedViewModel;

    public DownloadView()
    {
        InitializeComponent();
        Loaded += DownloadView_Loaded;
        Unloaded += DownloadView_Unloaded;
        DataContextChanged += DownloadView_DataContextChanged;
    }

    private void DownloadView_Loaded(object sender, System.Windows.RoutedEventArgs e)
        => ObserveViewModel(DataContext as DownloadViewModel);

    private void DownloadView_Unloaded(object sender, System.Windows.RoutedEventArgs e)
        => ObserveViewModel(null);

    private void DownloadView_DataContextChanged(
        object sender,
        System.Windows.DependencyPropertyChangedEventArgs e)
    {
        if (IsLoaded)
            ObserveViewModel(e.NewValue as DownloadViewModel);
    }

    private void ObserveViewModel(DownloadViewModel? viewModel)
    {
        if (ReferenceEquals(_observedViewModel, viewModel))
            return;

        if (_observedViewModel is not null)
            _observedViewModel.LogLines.CollectionChanged -= LogLines_CollectionChanged;

        _observedViewModel = viewModel;
        if (_observedViewModel is not null)
            _observedViewModel.LogLines.CollectionChanged += LogLines_CollectionChanged;
    }

    private void LogLines_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_observedViewModel is null || _observedViewModel.LogLines.Count == 0)
            return;

        LogTextBox.Dispatcher.BeginInvoke(() =>
        {
            if (IsLoaded)
                LogTextBox.ScrollToEnd();
        });
    }
}
