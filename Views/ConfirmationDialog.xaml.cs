using System.Windows;

namespace EasyGet.Views;

public partial class ConfirmationDialog : Window
{
    public string DialogTitle { get; }
    public string DialogMessage { get; }
    public string ConfirmText { get; }
    public bool IsDestructive { get; }

    public ConfirmationDialog(
        string title,
        string message,
        string confirmText,
        bool isDestructive)
    {
        DialogTitle = title;
        DialogMessage = message;
        ConfirmText = confirmText;
        IsDestructive = isDestructive;
        InitializeComponent();
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
        => DialogResult = true;

    private void CancelButton_Click(object sender, RoutedEventArgs e)
        => DialogResult = false;
}
