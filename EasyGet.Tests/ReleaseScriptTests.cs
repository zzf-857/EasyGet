using Xunit;

namespace EasyGet.Tests;

public class ReleaseScriptTests
{
    [Fact]
    public void WindowsPublishScriptRunsTestsPublishesAndChecksExecutable()
    {
        var root = TestRepositoryPaths.Root;
        var scriptPath = Path.Combine(root, "scripts", "publish-win-x64.ps1");

        Assert.True(File.Exists(scriptPath), "Expected scripts/publish-win-x64.ps1 to exist.");

        var script = File.ReadAllText(scriptPath);
        Assert.Contains("[CmdletBinding()]", script, StringComparison.Ordinal);
        Assert.Contains("dotnet test", script, StringComparison.Ordinal);
        Assert.Contains("\"publish\"", script, StringComparison.Ordinal);
        Assert.Contains("dotnet @publishArgs", script, StringComparison.Ordinal);
        Assert.Contains("--self-contained", script, StringComparison.Ordinal);
        Assert.Contains("/p:PublishSingleFile=false", script, StringComparison.Ordinal);
        Assert.Contains("/p:PublishReadyToRun=false", script, StringComparison.Ordinal);
        Assert.DoesNotContain("/p:IncludeNativeLibrariesForSelfExtract=true", script, StringComparison.Ordinal);
        Assert.Contains("Remove-Item -LiteralPath $publishDirFullPath -Recurse -Force", script, StringComparison.Ordinal);
        Assert.Contains("/p:DebugType=none", script, StringComparison.Ordinal);
        Assert.Contains("/p:DebugSymbols=false", script, StringComparison.Ordinal);
        Assert.Contains("Remove-Item -Force", script, StringComparison.Ordinal);
        Assert.Contains("EasyGet.exe", script, StringComparison.Ordinal);
        Assert.Contains("Compress-Archive", script, StringComparison.Ordinal);
        Assert.Contains("SkipZip", script, StringComparison.Ordinal);
        Assert.Contains("Version", script, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsPublishScriptDoesNotRestoreTestProjectWhenTestsAreSkipped()
    {
        var root = TestRepositoryPaths.Root;
        var scriptPath = Path.Combine(root, "scripts", "publish-win-x64.ps1");
        var script = File.ReadAllText(scriptPath);

        var skipTestsBlockIndex = script.IndexOf("if (-not $SkipTests)", StringComparison.Ordinal);
        var testProjectRestoreIndex = script.IndexOf("dotnet restore $testProjectPath", StringComparison.Ordinal);
        var testCommandIndex = script.IndexOf("dotnet test $testProjectPath", StringComparison.Ordinal);

        Assert.True(skipTestsBlockIndex >= 0, "Expected publish script to guard tests with -SkipTests.");
        Assert.True(testProjectRestoreIndex > skipTestsBlockIndex,
            "Restoring the test project should be inside the -SkipTests guard so release packaging does not repeat test restore.");
        Assert.True(testProjectRestoreIndex < testCommandIndex,
            "The test project should be restored immediately before running tests.");
    }

    [Fact]
    public void WindowsPublishScriptRestoresRuntimeAssetsBeforeNoRestorePublish()
    {
        var root = TestRepositoryPaths.Root;
        var scriptPath = Path.Combine(root, "scripts", "publish-win-x64.ps1");
        var script = File.ReadAllText(scriptPath);

        Assert.Contains("dotnet restore $projectPath -r $Runtime", script, StringComparison.Ordinal);
        Assert.Contains("\"--no-restore\"", script, StringComparison.Ordinal);

        var restoreIndex = script.IndexOf("dotnet restore $projectPath -r $Runtime", StringComparison.Ordinal);
        var publishIndex = script.IndexOf("dotnet @publishArgs", StringComparison.Ordinal);
        Assert.True(restoreIndex >= 0, "Expected publish script to restore runtime-specific assets.");
        Assert.True(publishIndex >= 0, "Expected publish script to invoke dotnet publish through publishArgs.");
        Assert.True(restoreIndex < publishIndex,
            "Runtime-specific restore should happen before dotnet publish --no-restore.");
    }

    [Fact]
    public void WindowsPublishScriptVerifiesPortableZipContents()
    {
        var root = TestRepositoryPaths.Root;
        var scriptPath = Path.Combine(root, "scripts", "publish-win-x64.ps1");
        var script = File.ReadAllText(scriptPath);

        Assert.Contains("Test-PortableZipContent", script, StringComparison.Ordinal);
        Assert.Contains("[System.IO.Compression.ZipFile]::OpenRead($ZipPath)", script, StringComparison.Ordinal);
        Assert.Contains("$zip.Dispose()", script, StringComparison.Ordinal);
        Assert.Contains("FullName -eq \"EasyGet.exe\"", script, StringComparison.Ordinal);
        Assert.Contains("$blockedZipEntryPatterns", script, StringComparison.Ordinal);
        Assert.Contains("WildcardPattern", script, StringComparison.Ordinal);
        Assert.Contains("Portable zip smoke check failed", script, StringComparison.Ordinal);

        var compressArchiveIndex = script.IndexOf("Compress-Archive", StringComparison.Ordinal);
        var zipContentCheckIndex = script.IndexOf("Test-PortableZipContent -ZipPath $zipPath", StringComparison.Ordinal);
        Assert.True(compressArchiveIndex >= 0, "Expected the publish script to create a portable zip.");
        Assert.True(zipContentCheckIndex > compressArchiveIndex,
            "Portable zip contents should be verified after Compress-Archive writes the asset.");
    }

    [Fact]
    public void WindowsPublishScriptPrunesDiagnosticsOnlyRuntimeFiles()
    {
        var root = TestRepositoryPaths.Root;
        var publishScriptPath = Path.Combine(root, "scripts", "publish-win-x64.ps1");
        var innoPath = Path.Combine(root, "scripts", "EasyGet.iss");

        var publishScript = File.ReadAllText(publishScriptPath);
        var innoScript = File.ReadAllText(innoPath);

        var diagnosticPatterns = new[]
        {
            "*.pdb",
            "createdump.exe",
            "mscordaccore*.dll",
            "mscordbi.dll",
            "Microsoft.DiaSymReader.Native.amd64.dll"
        };

        foreach (var pattern in diagnosticPatterns)
        {
            Assert.Contains(pattern, publishScript, StringComparison.Ordinal);
            Assert.Contains(pattern, innoScript, StringComparison.Ordinal);
        }

        Assert.Contains("$publishPrunePatterns", publishScript, StringComparison.Ordinal);
        Assert.Contains("$blockedZipEntryPatterns", publishScript, StringComparison.Ordinal);
        Assert.Contains("WildcardPattern", publishScript, StringComparison.Ordinal);
        Assert.Contains("diagnostic/runtime debugging file was included", publishScript, StringComparison.Ordinal);
        Assert.Contains(@"Excludes: ""*.pdb,createdump.exe,mscordaccore*.dll,mscordbi.dll,Microsoft.DiaSymReader.Native.amd64.dll,System.Windows.Forms.Design*.dll,System.Design.dll,System.Drawing.Design.dll""", innoScript, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsPublishScriptPrunesDesignTimeWindowsDesktopAssemblies()
    {
        var root = TestRepositoryPaths.Root;
        var publishScriptPath = Path.Combine(root, "scripts", "publish-win-x64.ps1");
        var innoPath = Path.Combine(root, "scripts", "EasyGet.iss");

        var publishScript = File.ReadAllText(publishScriptPath);
        var innoScript = File.ReadAllText(innoPath);

        var designTimePatterns = new[]
        {
            "System.Windows.Forms.Design*.dll",
            "System.Design.dll",
            "System.Drawing.Design.dll"
        };

        foreach (var pattern in designTimePatterns)
        {
            Assert.Contains(pattern, publishScript, StringComparison.Ordinal);
            Assert.Contains(pattern, innoScript, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("System.Windows.Forms.dll,", innoScript, StringComparison.Ordinal);
        Assert.DoesNotContain("WindowsFormsIntegration.dll", innoScript, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallerScriptBuildsVersionedSetupExecutable()
    {
        var root = TestRepositoryPaths.Root;
        var scriptPath = Path.Combine(root, "scripts", "build-installer.ps1");
        var innoPath = Path.Combine(root, "scripts", "EasyGet.iss");

        Assert.True(File.Exists(scriptPath), "Expected scripts/build-installer.ps1 to exist.");
        Assert.True(File.Exists(innoPath), "Expected scripts/EasyGet.iss to exist.");

        var script = File.ReadAllText(scriptPath);
        Assert.Contains("ISCC", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MyAppVersion", script, StringComparison.Ordinal);
        Assert.Contains("Get-Content -Raw -Encoding UTF8", script, StringComparison.Ordinal);
        Assert.Contains("ForEach-Object { $_.Version }", script, StringComparison.Ordinal);
        Assert.Contains("Version mismatch", script, StringComparison.Ordinal);
        Assert.Contains("easyget-update.json", script, StringComparison.Ordinal);
        Assert.Contains("ConvertTo-Json", script, StringComparison.Ordinal);
        var innoScript = File.ReadAllText(innoPath);
        Assert.Contains("EasyGet-Setup-v", innoScript, StringComparison.Ordinal);
        Assert.Contains("CloseApplications=yes", innoScript, StringComparison.Ordinal);
        Assert.Contains("RestartApplications=no", innoScript, StringComparison.Ordinal);
        Assert.Contains("Compression=lzma2/ultra64", innoScript, StringComparison.Ordinal);
        Assert.Contains(@"Excludes: ""*.pdb,createdump.exe,mscordaccore*.dll,mscordbi.dll,Microsoft.DiaSymReader.Native.amd64.dll,System.Windows.Forms.Design*.dll,System.Design.dll,System.Drawing.Design.dll""", innoScript, StringComparison.Ordinal);
        Assert.DoesNotContain(@"\{#MyAppExeName}""; DestDir", innoScript, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallerScriptRejectsConfigurationsThatDoNotMatchFixedInnoSourcePath()
    {
        var root = TestRepositoryPaths.Root;
        var scriptPath = Path.Combine(root, "scripts", "build-installer.ps1");
        var innoPath = Path.Combine(root, "scripts", "EasyGet.iss");

        var script = File.ReadAllText(scriptPath);
        var innoScript = File.ReadAllText(innoPath);

        Assert.Contains(@"Source: ""..\artifacts\publish\Release\win-x64\*""", innoScript, StringComparison.Ordinal);
        Assert.Contains("$Configuration -ne \"Release\" -or $Runtime -ne \"win-x64\"", script, StringComparison.Ordinal);
        Assert.Contains("Installer packaging supports only Release/win-x64", script, StringComparison.Ordinal);

        var contractCheckIndex = script.IndexOf("Installer packaging supports only Release/win-x64", StringComparison.Ordinal);
        var publishInvocationIndex = script.IndexOf("& $publishScript", StringComparison.Ordinal);
        Assert.True(contractCheckIndex >= 0, "Expected build-installer.ps1 to reject unsupported installer inputs.");
        Assert.True(publishInvocationIndex >= 0, "Expected build-installer.ps1 to invoke the publish script.");
        Assert.True(contractCheckIndex < publishInvocationIndex,
            "Unsupported installer inputs should be rejected before publishing so stale Inno assets cannot be packaged.");
    }

    [Fact]
    public void InstallerScriptRemovesStaleVersionedSetupArtifactsBeforeRunningInnoSetup()
    {
        var root = TestRepositoryPaths.Root;
        var scriptPath = Path.Combine(root, "scripts", "build-installer.ps1");
        var script = File.ReadAllText(scriptPath);

        var cleanupIndex = script.IndexOf("EasyGet-Setup-v*.exe", StringComparison.Ordinal);
        var isccIndex = script.IndexOf("& $iscc", StringComparison.Ordinal);

        Assert.True(cleanupIndex >= 0, "Expected installer build script to clean stale versioned setup files.");
        Assert.True(isccIndex >= 0, "Expected installer build script to invoke ISCC.");
        Assert.True(cleanupIndex < isccIndex, "Stale setup files should be removed before Inno Setup writes the current installer.");
        Assert.Contains("Get-ChildItem -LiteralPath $releaseDir -Filter \"EasyGet-Setup-v*.exe\"", script, StringComparison.Ordinal);
        Assert.Contains("Remove-Item -Force", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ProjectRestrictsReleaseSatelliteResources()
    {
        var root = TestRepositoryPaths.Root;
        var projectPath = Path.Combine(root, "EasyGet.csproj");
        var project = File.ReadAllText(projectPath);

        Assert.Contains("<SatelliteResourceLanguages>zh-Hans</SatelliteResourceLanguages>", project, StringComparison.Ordinal);
        Assert.Contains("<DebugType>none</DebugType>", project, StringComparison.Ordinal);
        Assert.Contains("<DebugSymbols>false</DebugSymbols>", project, StringComparison.Ordinal);
        Assert.Contains("<PublishReadyToRun>false</PublishReadyToRun>", project, StringComparison.Ordinal);
    }

    [Fact]
    public void ProjectDoesNotDirectlyUseWinFormsFolderDialogs()
    {
        var root = TestRepositoryPaths.Root;
        var projectPath = Path.Combine(root, "EasyGet.csproj");
        var project = File.ReadAllText(projectPath);
        var sourceFiles = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path =>
                !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                && !path.Contains($"{Path.DirectorySeparatorChar}artifacts{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                && !path.Contains($"{Path.DirectorySeparatorChar}EasyGet.Tests{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Select(File.ReadAllText);

        Assert.DoesNotContain("<UseWindowsForms>true</UseWindowsForms>", project, StringComparison.Ordinal);
        Assert.All(sourceFiles, source => Assert.DoesNotContain("System.Windows.Forms", source, StringComparison.Ordinal));
        Assert.Contains("Microsoft.Win32.OpenFolderDialog", string.Join(Environment.NewLine, sourceFiles), StringComparison.Ordinal);
    }

    [Fact]
    public void GitHubReleaseWorkflowPublishesInstallerZipAndManifest()
    {
        var root = TestRepositoryPaths.Root;
        var workflowPath = Path.Combine(root, ".github", "workflows", "release.yml");

        Assert.True(File.Exists(workflowPath), "Expected .github/workflows/release.yml to exist.");

        var workflow = File.ReadAllText(workflowPath);
        Assert.Contains("tags:", workflow, StringComparison.Ordinal);
        Assert.Contains("v*", workflow, StringComparison.Ordinal);
        Assert.Contains("Validate release version", workflow, StringComparison.Ordinal);
        Assert.Contains("ForEach-Object { $_.Version }", workflow, StringComparison.Ordinal);
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
    public void GitHubReleaseWorkflowVerifiesGeneratedManifestInsteadOfRecreatingIt()
    {
        var root = TestRepositoryPaths.Root;
        var workflowPath = Path.Combine(root, ".github", "workflows", "release.yml");
        var workflow = File.ReadAllText(workflowPath);

        Assert.Contains("Verify update manifest", workflow, StringComparison.Ordinal);
        Assert.Contains("ConvertFrom-Json", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("Create update manifest", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("ConvertTo-Json", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void ManualPackagingGuideMentionsPublishScript()
    {
        var root = TestRepositoryPaths.Root;
        var guidePath = Path.Combine(root, "00Readme", "手动打包说明.md");
        var guide = File.ReadAllText(guidePath);

        Assert.Contains("scripts\\publish-win-x64.ps1", guide, StringComparison.Ordinal);
    }
}
