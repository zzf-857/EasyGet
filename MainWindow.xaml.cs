using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using EasyGet.Services;
using EasyGet.ViewModels;

namespace EasyGet;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly ConfigService _configService;

    public MainWindow(MainViewModel viewModel, ConfigService configService)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _configService = configService;
        DataContext = _viewModel;

        Loaded += async (_, _) =>
        {
            await _viewModel.InitializeAsync();
            RestoreWindowState();
        };

        Closing += async (_, _) =>
        {
            SaveWindowState();
            await _configService.SaveAsync();
        };
    }

    private void RestoreWindowState()
    {
        var ws = _configService.Config.Window;
        if (!double.IsNaN(ws.Left) && !double.IsNaN(ws.Top))
        {
            Left = ws.Left;
            Top = ws.Top;
        }
        if (ws.Width > 0) Width = ws.Width;
        if (ws.Height > 0) Height = ws.Height;
    }

    private void SaveWindowState()
    {
        if (WindowState == System.Windows.WindowState.Normal)
        {
            _configService.Config.Window.Left = Left;
            _configService.Config.Window.Top = Top;
            _configService.Config.Window.Width = Width;
            _configService.Config.Window.Height = Height;
        }
    }
}