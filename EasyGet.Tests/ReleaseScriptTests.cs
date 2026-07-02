using Xunit;

namespace EasyGet.Tests;

public class ReleaseScriptTests
{
    [Fact]
    public void WindowsPublishScriptRunsTestsPublishesAndChecksExecutable()
    {
        var root = GetRepositoryRoot();
        var scriptPath = Path.Combine(root, "scripts", "publish-win-x64.ps1");

        Assert.True(File.Exists(scriptPath), "Expected scripts/publish-win-x64.ps1 to exist.");

        var script = File.ReadAllText(scriptPath);
        Assert.Contains("[CmdletBinding()]", script, StringComparison.Ordinal);
        Assert.Contains("dotnet test", script, StringComparison.Ordinal);
        Assert.Contains("\"publish\"", script, StringComparison.Ordinal);
        Assert.Contains("dotnet @publishArgs", script, StringComparison.Ordinal);
        Assert.Contains("--self-contained", script, StringComparison.Ordinal);
        Assert.Contains("EasyGet.exe", script, StringComparison.Ordinal);
        Assert.Contains("Compress-Archive", script, StringComparison.Ordinal);
        Assert.Contains("SkipZip", script, StringComparison.Ordinal);
        Assert.Contains("Version", script, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallerScriptBuildsVersionedSetupExecutable()
    {
        var root = GetRepositoryRoot();
        var scriptPath = Path.Combine(root, "scripts", "build-installer.ps1");
        var innoPath = Path.Combine(root, "scripts", "EasyGet.iss");

        Assert.True(File.Exists(scriptPath), "Expected scripts/build-installer.ps1 to exist.");
        Assert.True(File.Exists(innoPath), "Expected scripts/EasyGet.iss to exist.");

        var script = File.ReadAllText(scriptPath);
        Assert.Contains("ISCC", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MyAppVersion", script, StringComparison.Ordinal);
        Assert.Contains("Get-Content -Raw -Encoding UTF8", script, StringComparison.Ordinal);
        Assert.Contains("Version mismatch", script, StringComparison.Ordinal);
        Assert.Contains("easyget-update.json", script, StringComparison.Ordinal);
        Assert.Contains("ConvertTo-Json", script, StringComparison.Ordinal);
        var innoScript = File.ReadAllText(innoPath);
        Assert.Contains("EasyGet-Setup-v", innoScript, StringComparison.Ordinal);
        Assert.Contains("CloseApplications=yes", innoScript, StringComparison.Ordinal);
        Assert.Contains("RestartApplications=no", innoScript, StringComparison.Ordinal);
    }

    [Fact]
    public void GitHubReleaseWorkflowPublishesInstallerZipAndManifest()
    {
        var root = GetRepositoryRoot();
        var workflowPath = Path.Combine(root, ".github", "workflows", "release.yml");

        Assert.True(File.Exists(workflowPath), "Expected .github/workflows/release.yml to exist.");

        var workflow = File.ReadAllText(workflowPath);
        Assert.Contains("tags:", workflow, StringComparison.Ordinal);
        Assert.Contains("v*", workflow, StringComparison.Ordinal);
        Assert.Contains("Validate release version", workflow, StringComparison.Ordinal);
        Assert.Contains("Version mismatch", workflow, StringComparison.Ordinal);
        Assert.Contains("dotnet test", workflow, StringComparison.Ordinal);
        Assert.Contains("build-installer.ps1", workflow, StringComparison.Ordinal);
        Assert.Contains("EasyGet-Setup-v$version.exe", workflow, StringComparison.Ordinal);
        Assert.Contains("EasyGet-Setup-${{ github.ref_name }}.exe", workflow, StringComparison.Ordinal);
        Assert.Contains("EasyGet-win-x64-Release.zip", workflow, StringComparison.Ordinal);
        Assert.Contains("easyget-update.json", workflow, StringComparison.Ordinal);
        Assert.Contains("softprops/action-gh-release", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void ManualPackagingGuideMentionsPublishScript()
    {
        var root = GetRepositoryRoot();
        var guidePath = Path.Combine(root, "00Readme", "手动打包说明.md");
        var guide = File.ReadAllText(guidePath);

        Assert.Contains("scripts\\publish-win-x64.ps1", guide, StringComparison.Ordinal);
    }

    private static string GetRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "EasyGet.csproj")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find repository root from test output directory.");
    }
}
