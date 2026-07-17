using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using EasyGet.Models;
using EasyGet.ViewModels;

namespace EasyGet.Views;

public partial class HistoryView : System.Windows.Controls.UserControl
{
    private const string HistoryItemsDataFormat = "EasyGet.HistoryItems";
    private Point _historyDragStart;
    private HistoryViewModel? _observedViewModel;
    private readonly DispatcherTimer _scrollResetTimer;

    public HistoryView()
    {
        InitializeComponent();
        _scrollResetTimer = new DispatcherTimer(DispatcherPriority.ContextIdle, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(140)
        };
        _scrollResetTimer.Tick += (_, _) =>
        {
            _scrollResetTimer.Stop();
            FindVisualChild<ScrollViewer>(HistoryList)?.ScrollToTop();
        };
    }

    private async void HistoryView_Loaded(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is HistoryViewModel vm)
        {
            ObserveViewModel(vm);
            await vm.LoadHistory();
            ScrollHistoryToTop();
        }
    }

    private void HistoryView_Unloaded(object sender, RoutedEventArgs e)
    {
        _scrollResetTimer.Stop();
        if (_observedViewModel is not null)
            _observedViewModel.PropertyChanged -= HistoryViewModel_PropertyChanged;
        _observedViewModel = null;
    }

    private void ObserveViewModel(HistoryViewModel viewModel)
    {
        if (ReferenceEquals(_observedViewModel, viewModel))
            return;

        if (_observedViewModel is not null)
            _observedViewModel.PropertyChanged -= HistoryViewModel_PropertyChanged;
        _observedViewModel = viewModel;
        _observedViewModel.PropertyChanged += HistoryViewModel_PropertyChanged;
    }

    private void HistoryViewModel_PropertyChanged(
        object? sender,
        System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(HistoryViewModel.SelectedFolderId)
            or nameof(HistoryViewModel.SelectedBatchKey)
            or nameof(HistoryViewModel.SelectedMediaFilter)
            or nameof(HistoryViewModel.SearchKeyword))
        {
            ScrollHistoryToTop();

            if (e.PropertyName is not nameof(HistoryViewModel.SearchKeyword))
                AnimateContentTransition();
        }
    }

    private void ScrollHistoryToTop()
    {
        _scrollResetTimer.Stop();
        _scrollResetTimer.Start();
        Dispatcher.BeginInvoke(
            DispatcherPriority.ContextIdle,
            new Action(() => FindVisualChild<ScrollViewer>(HistoryList)?.ScrollToTop()));
    }

    private void AnimateContentTransition()
    {
        Dispatcher.BeginInvoke(
            DispatcherPriority.Render,
            new Action(() =>
            {
                var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
                HistoryContentHost.BeginAnimation(
                    OpacityProperty,
                    new DoubleAnimation(0.58, 1, TimeSpan.FromMilliseconds(220))
                    {
                        EasingFunction = easing
                    });
                HistoryContentTranslate.BeginAnimation(
                    TranslateTransform.YProperty,
                    new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(220))
                    {
                        EasingFunction = easing
                    });
            }));
    }

    private void NewFolderPopup_Opened(object? sender, EventArgs e)
    {
        NewFolderTextBox.Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(() =>
            {
                NewFolderTextBox.Focus();
                NewFolderTextBox.SelectAll();
            }));
    }

    private void NewFolderToggle_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized)
            return;
        NewFolderPopup.IsOpen = true;
    }

    private void NewFolderToggle_Unchecked(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized)
            return;
        NewFolderPopup.IsOpen = false;
    }

    private void NewFolderPopup_Closed(object? sender, EventArgs e)
    {
        if (!IsInitialized)
            return;
        NewFolderToggle.IsChecked = false;
    }

    private void CancelNewFolderButton_Click(object sender, RoutedEventArgs e)
    {
        NewFolderToggle.IsChecked = false;
    }

    private void CreateFolderButton_Click(object sender, RoutedEventArgs e)
    {
        NewFolderToggle.IsChecked = false;
    }

    private void NewFolderTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not HistoryViewModel vm)
            return;

        if (e.Key == Key.Escape)
        {
            NewFolderToggle.IsChecked = false;
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Enter || !vm.CreateFolderCommand.CanExecute(null))
            return;

        vm.CreateFolderCommand.Execute(null);
        NewFolderToggle.IsChecked = false;
        e.Handled = true;
    }

    private static T? FindVisualChild<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
                return match;
            var descendant = FindVisualChild<T>(child);
            if (descendant is not null)
                return descendant;
        }

        return null;
    }

    private void HistoryCard_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _historyDragStart = e.GetPosition(this);
    }

    private void HistoryCard_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed
            || sender is not FrameworkElement element
            || element.DataContext is not DownloadHistory history
            || DataContext is not HistoryViewModel vm)
        {
            return;
        }

        var current = e.GetPosition(this);
        if (Math.Abs(current.X - _historyDragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(current.Y - _historyDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var ids = vm.PrepareHistoryDrag(history.Id).ToArray();
        if (ids.Length == 0)
            return;

        var data = new DataObject();
        data.SetData(HistoryItemsDataFormat, ids);
        DragDrop.DoDragDrop(element, data, DragDropEffects.Move);
        e.Handled = true;
    }

    private void HistoryFolder_DragOver(object sender, DragEventArgs e)
    {
        var canAcceptDrop = sender is FrameworkElement
        {
            DataContext: HistoryFolder { CanAcceptDrop: true }
        };
        e.Effects = e.Data.GetDataPresent(HistoryItemsDataFormat) && canAcceptDrop
            ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void HistoryFolder_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(HistoryItemsDataFormat)
            || e.Data.GetData(HistoryItemsDataFormat) is not long[] ids
            || ids.Length == 0
            || DataContext is not HistoryViewModel vm
            || sender is not FrameworkElement element)
        {
            e.Handled = true;
            return;
        }

        var folderId = element.DataContext is HistoryFolder folder
            ? folder.Id
            : element.Tag is long longId
                ? longId
                : long.TryParse(element.Tag?.ToString(), out var parsed)
                    ? parsed
                    : -1;
        if (folderId >= 0)
            await vm.MoveItemsToFolderAsync(ids, folderId);

        e.Handled = true;
    }

    private void FolderRenameTextBox_IsVisibleChanged(
        object sender,
        DependencyPropertyChangedEventArgs e)
    {
        if (sender is not TextBox textBox || e.NewValue is not true)
            return;

        textBox.Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(() =>
            {
                textBox.Focus();
                textBox.SelectAll();
            }));
    }

    private void FolderRenameTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: HistoryFolder folder }
            || DataContext is not HistoryViewModel vm)
        {
            return;
        }

        System.Windows.Input.ICommand? command = e.Key switch
        {
            Key.Enter => vm.SaveRenameFolderCommand,
            Key.Escape => vm.CancelRenameFolderCommand,
            _ => null
        };
        if (command is null || !command.CanExecute(folder))
            return;

        command.Execute(folder);
        e.Handled = true;
    }
}
