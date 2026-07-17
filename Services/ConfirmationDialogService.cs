using System.Windows;
using EasyGet.Views;

namespace EasyGet.Services;

/// <summary>
/// 使用 EasyGet 主题显示模态确认，避免回退到与应用割裂的系统 MessageBox。
/// </summary>
public static class ConfirmationDialogService
{
    public static bool Show(string message, string title)
    {
        var app = Application.Current;
        var owner = app?.MainWindow;
        if (app is null || owner is null || !owner.IsLoaded)
            return true;

        bool ShowCore()
        {
            var dialog = new ConfirmationDialog(
                NormalizeTitle(title),
                message,
                ResolveConfirmText(title),
                IsDestructiveAction(title));
            dialog.Owner = owner;
            return dialog.ShowDialog() == true;
        }

        return owner.Dispatcher.CheckAccess()
            ? ShowCore()
            : owner.Dispatcher.Invoke(ShowCore);
    }

    internal static string NormalizeTitle(string? title)
        => string.IsNullOrWhiteSpace(title) ? "确认操作" : title.Trim();

    internal static bool IsDestructiveAction(string? title)
        => title?.Contains("删除", StringComparison.Ordinal) == true
           || title?.Contains("清空", StringComparison.Ordinal) == true
           || title?.Contains("停止", StringComparison.Ordinal) == true
           || title?.Contains("取消", StringComparison.Ordinal) == true;

    internal static string ResolveConfirmText(string? title)
    {
        if (title?.Contains("清空", StringComparison.Ordinal) == true)
            return "清空记录";
        if (title?.Contains("删除", StringComparison.Ordinal) == true)
            return "删除记录";
        if (title?.Contains("停止", StringComparison.Ordinal) == true
            || title?.Contains("取消", StringComparison.Ordinal) == true)
        {
            return "停止任务";
        }

        return "确认";
    }
}
