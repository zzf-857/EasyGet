using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using EasyGet.Models;
using EasyGet.ViewModels;

namespace EasyGet.Views;

public partial class HistoryView : System.Windows.Controls.UserControl
{
    private const string HistoryItemsDataFormat = "EasyGet.HistoryItems";
    private Point _historyDragStart;

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
        e.Effects = e.Data.GetDataPresent(HistoryItemsDataFormat)
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
