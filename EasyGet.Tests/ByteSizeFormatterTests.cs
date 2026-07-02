using EasyGet.Services;
using Xunit;

namespace EasyGet.Tests;

public class ByteSizeFormatterTests
{
    [Theory]
    [InlineData(-1L, "0 B")]
    [InlineData(0L, "0 B")]
    [InlineData(1023L, "1023 B")]
    [InlineData(1024L, "1 KB")]
    [InlineData(1024L * 1024L * 1536L, "1.5 GB")]
    public void FormatClampZero_FormatsBytesUsingExistingUnits(long bytes, string expected)
    {
        Assert.Equal(expected, ByteSizeFormatter.FormatClampZero(bytes));
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(0L)]
    public void FormatOrUnknown_UsesUnknownTextForNonPositiveValues(long bytes)
    {
        Assert.Equal("大小未知", ByteSizeFormatter.FormatOrUnknown(bytes));
    }

    [Fact]
    public void FormatClampZero_AppendsSuffixWhenProvided()
    {
        Assert.Equal("1 KB 可用", ByteSizeFormatter.FormatClampZero(1024, " 可用"));
    }
}
