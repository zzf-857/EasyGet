using EasyGet.Services;
using Xunit;

namespace EasyGet.Tests;

public class ConfirmationDialogServiceTests
{
    [Theory]
    [InlineData("确认删除批次记录", "删除记录")]
    [InlineData("确认清空记录", "清空记录")]
    [InlineData("确认停止未完成任务", "停止任务")]
    [InlineData("确认操作", "确认")]
    public void ResolveConfirmText_UsesActionSpecificChineseLabel(string title, string expected)
        => Assert.Equal(expected, ConfirmationDialogService.ResolveConfirmText(title));

    [Theory]
    [InlineData("确认删除批次记录", true)]
    [InlineData("确认清空记录", true)]
    [InlineData("确认停止任务", true)]
    [InlineData("保存设置", false)]
    public void IsDestructiveAction_RecognizesDestructiveOperations(string title, bool expected)
        => Assert.Equal(expected, ConfirmationDialogService.IsDestructiveAction(title));
}
