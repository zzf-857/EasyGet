using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using EasyGet.Models;
using EasyGet.ViewModels;

namespace EasyGet.Views;

public partial class BatchDownloadView : System.Windows.Controls.UserControl
{
    public BatchDownloadView()
    {
        InitializeComponent();
    }

    public bool TryHandleQueueShortcut(Key key)
    {
        if (QueueList.SelectedItem is not DownloadTask task
            || DataContext is not BatchDownloadViewModel vm)
        {
            return false;
        }

        if (key == Key.Space)
        {
            if (task.Status == DownloadStatus.Downloading
                && vm.PauseTaskCommand.CanExecute(task.Id))
            {
                vm.PauseTaskCommand.Execute(task.Id);
                return true;
            }

            if (task.Status == DownloadStatus.Paused
                && vm.ResumeTaskCommand.CanExecute(task.Id))
            {
                vm.ResumeTaskCommand.Execute(task.Id);
                return true;
            }

            return false;
        }

        if (key == Key.Delete && vm.CancelTaskCommand.CanExecute(task.Id))
        {
            vm.CancelTaskCommand.Execute(task.Id);
            return true;
        }

        return false;
    }

    private void OverflowButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { ContextMenu: { } menu } button)
            return;

        menu.PlacementTarget = button;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void UserControl_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop) || e.Data.GetDataPresent(System.Windows.DataFormats.Text))
        {
            e.Effects = System.Windows.DragDropEffects.Copy;
            DragDropOverlay.Visibility = Visibility.Visible;
            DragDropOverlay.Opacity = 1;
        }
        else
        {
            e.Effects = System.Windows.DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void UserControl_DragLeave(object sender, System.Windows.DragEventArgs e)
    {
        DragDropOverlay.Visibility = Visibility.Collapsed;
        DragDropOverlay.Opacity = 0;
        e.Handled = true;
    }

    private void UserControl_Drop(object sender, System.Windows.DragEventArgs e)
    {
        DragDropOverlay.Visibility = Visibility.Collapsed;
        DragDropOverlay.Opacity = 0;

        string textToImport = "";

        if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
        {
            var files = (string[])e.Data.GetData(System.Windows.DataFormats.FileDrop);
            if (files != null && files.Length > 0)
            {
                var txtFiles = files.Where(f => Path.GetExtension(f).ToLowerInvariant() == ".txt").ToList();
                if (txtFiles.Any())
                {
                    try
                    {
                        textToImport = string.Join("\n", txtFiles.Select(f => File.ReadAllText(f)));
                    }
                    catch { }
                }
            }
        }
        else if (e.Data.GetDataPresent(System.Windows.DataFormats.Text))
        {
            textToImport = (string)e.Data.GetData(System.Windows.DataFormats.Text);
        }

        if (!string.IsNullOrEmpty(textToImport))
        {
            if (DataContext is BatchDownloadViewModel vm)
            {
                vm.ImportText(textToImport);
            }
        }
        e.Handled = true;
    }
}
