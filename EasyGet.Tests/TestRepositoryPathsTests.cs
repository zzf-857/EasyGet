using Xunit;

namespace EasyGet.Tests;

public class TestRepositoryPathsTests
{
    [Fact]
    public void Root_FindsProjectDirectoryFromTestOutput()
    {
        var projectPath = Path.Combine(TestRepositoryPaths.Root, "EasyGet.csproj");

        Assert.True(File.Exists(projectPath));
    }

    [Fact]
    public void GetRootPath_ReturnsExistingFilesAndDirectories()
    {
        Assert.True(File.Exists(TestRepositoryPaths.GetRootPath("MainWindow.xaml")));
        Assert.True(Directory.Exists(TestRepositoryPaths.GetRootPath("Views")));
    }

    [Fact]
    public void GetViewAndThemePathsResolveKnownFiles()
    {
        Assert.True(File.Exists(TestRepositoryPaths.GetViewPath("DownloadView.xaml")));
        Assert.True(File.Exists(TestRepositoryPaths.GetThemePath("Generic.xaml")));
    }
}
