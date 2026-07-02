using System.Globalization;
using System.Windows.Media;
using EasyGet.Converters;
using EasyGet.Models;
using Xunit;

namespace EasyGet.Tests;

public class CommonConvertersTests
{
    [Fact]
    public void PlatformToColorConverter_ReusesFrozenBrushesCaseInsensitively()
    {
        var converter = new PlatformToColorConverter();

        var first = Assert.IsAssignableFrom<Brush>(
            converter.Convert("YouTube", typeof(Brush), null!, CultureInfo.InvariantCulture));
        var second = Assert.IsAssignableFrom<Brush>(
            converter.Convert("youtube", typeof(Brush), null!, CultureInfo.InvariantCulture));

        Assert.Same(first, second);
        Assert.True(first.IsFrozen);
    }

    [Fact]
    public void StatusToColorConverter_ReusesFrozenBrushes()
    {
        var converter = new StatusToColorConverter();

        var first = Assert.IsAssignableFrom<Brush>(
            converter.Convert(DownloadStatus.Completed, typeof(Brush), null!, CultureInfo.InvariantCulture));
        var second = Assert.IsAssignableFrom<Brush>(
            converter.Convert(DownloadStatus.Completed, typeof(Brush), null!, CultureInfo.InvariantCulture));

        Assert.Same(first, second);
        Assert.True(first.IsFrozen);
    }
}
