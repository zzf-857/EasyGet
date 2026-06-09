using System.Buffers.Binary;
using System.Xml.Linq;
using Xunit;

namespace EasyGet.Tests;

public class AssetTests
{
    [Fact]
    public void ApplicationIconAssetsUseModernEasyGetLogo()
    {
        var root = GetRootPath();
        var pngPath = Path.Combine(root, "Assets", "app.png");
        var icoPath = Path.Combine(root, "Assets", "app.ico");
        var sourcePath = Path.Combine(root, "Assets", "app-icon-source.svg");

        Assert.True(File.Exists(pngPath), "Assets/app.png should exist for README and preview surfaces.");
        Assert.True(File.Exists(icoPath), "Assets/app.ico should exist for the Windows executable.");
        Assert.True(File.Exists(sourcePath), "The app icon should keep an editable SVG source in the repository.");

        var pngBytes = File.ReadAllBytes(pngPath);
        AssertPngSize(pngBytes, 256, 256);
        Assert.True(
            pngBytes.Length >= 12_000,
            $"Assets/app.png should be a richer modern logo export, not the old flat mark. Actual size: {pngBytes.Length} bytes.");

        var source = File.ReadAllText(sourcePath);
        Assert.Contains("EasyGet App Icon", source, StringComparison.Ordinal);
        Assert.Contains("linearGradient", source, StringComparison.Ordinal);
        Assert.Contains("download", source, StringComparison.OrdinalIgnoreCase);

        var project = XDocument.Load(Path.Combine(root, "EasyGet.csproj"));
        var applicationIcon = project
            .Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "ApplicationIcon")
            ?.Value;

        Assert.Equal(@"Assets\app.ico", applicationIcon);
    }

    private static void AssertPngSize(byte[] bytes, int expectedWidth, int expectedHeight)
    {
        var pngSignature = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
        Assert.True(bytes.AsSpan(0, pngSignature.Length).SequenceEqual(pngSignature));

        var width = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4));
        var height = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4));

        Assert.Equal(expectedWidth, width);
        Assert.Equal(expectedHeight, height);
    }

    private static string GetRootPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EasyGet.csproj")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the EasyGet project root.");
    }
}
