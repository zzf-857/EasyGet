using Xunit;
using System.Diagnostics;

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
    public void DouyinSidecarBuildScriptStagesSmokeOnlyReleaseSkeleton()
    {
        var root = TestRepositoryPaths.Root;
        var scriptPath = Path.Combine(root, "scripts", "build-douyin-sidecar.ps1");

        Assert.True(File.Exists(scriptPath), "Expected scripts/build-douyin-sidecar.ps1 to exist.");

        var script = File.ReadAllText(scriptPath);
        Assert.Contains("[CmdletBinding()]", script, StringComparison.Ordinal);
        Assert.Contains("Configuration", script, StringComparison.Ordinal);
        Assert.Contains("Runtime", script, StringComparison.Ordinal);
        Assert.Contains("OutputRoot", script, StringComparison.Ordinal);
        Assert.Contains("SkipBuild", script, StringComparison.Ordinal);
        Assert.Contains("EasyGet.DouyinSidecar.spec", script, StringComparison.Ordinal);
        Assert.Contains("PyInstaller", script, StringComparison.Ordinal);
        Assert.Contains("artifacts\\sidecar", script, StringComparison.Ordinal);
        Assert.Contains("EasyGet.DouyinSidecar.exe", script, StringComparison.Ordinal);
        Assert.Contains("THIRD_PARTY_NOTICES.md", script, StringComparison.Ordinal);
        Assert.Contains("douyin-downloader-promax-LICENSE.txt", script, StringComparison.Ordinal);
        Assert.Contains("APACHE-2.0.txt", script, StringComparison.Ordinal);
        Assert.Contains("sidecar-version.json", script, StringComparison.Ordinal);
        Assert.Contains("Get-FileHash -Algorithm SHA256", script, StringComparison.Ordinal);
        Assert.Contains("smokeOnly = $true", script, StringComparison.Ordinal);
        Assert.Contains("selfContainedRealDownload = $false", script, StringComparison.Ordinal);
        Assert.Contains("DOUYIN_DOWNLOADER_PROMAX_ROOT", script, StringComparison.Ordinal);
        Assert.DoesNotContain(@"F:\AI\AIMadeupTools\05_ThirdPartyRepos", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("packagingMode = \"pyinstaller-onefile-smoke-only\"", script, StringComparison.Ordinal);
        Assert.Contains("$bundlesThirdPartyRuntime", script, StringComparison.Ordinal);
        Assert.Contains("runtimeRequiresExternalPython = (-not $bundlesThirdPartyRuntime)", script, StringComparison.Ordinal);
        Assert.Contains("excludedOptionalFeatures", script, StringComparison.Ordinal);
        Assert.Contains("artifactSizeBytes", script, StringComparison.Ordinal);
        Assert.Contains("licenseInventoryPath = \"licenses/python-dependency-license-inventory.md\"", script, StringComparison.Ordinal);
        Assert.Contains("importSelfTest", script, StringComparison.Ordinal);
        Assert.Contains("--self-test-imports", script, StringComparison.Ordinal);
        Assert.Contains("commit = $thirdPartyCommit", script, StringComparison.Ordinal);
        Assert.Contains("dirty = $thirdPartyDirty", script, StringComparison.Ordinal);
    }

    [Fact]
    public void DouyinSidecarReleaseSkeletonIncludesSpecAndAttributionFiles()
    {
        var root = TestRepositoryPaths.Root;
        var specPath = Path.Combine(root, "tools", "douyin-sidecar", "EasyGet.DouyinSidecar.spec");
        var noticesPath = Path.Combine(root, "tools", "douyin-sidecar", "THIRD_PARTY_NOTICES.md");
        var licensePath = Path.Combine(root, "tools", "douyin-sidecar", "licenses", "douyin-downloader-promax-LICENSE.txt");
        var apacheLicensePath = Path.Combine(root, "tools", "douyin-sidecar", "licenses", "APACHE-2.0.txt");
        var licenseInventoryPath = Path.Combine(root, "tools", "douyin-sidecar", "licenses", "python-dependency-license-inventory.md");

        Assert.True(File.Exists(specPath), "Expected PyInstaller spec to exist.");
        Assert.True(File.Exists(noticesPath), "Expected third-party notices to exist.");
        Assert.True(File.Exists(licensePath), "Expected douyin-downloader-promax license copy to exist.");
        Assert.True(File.Exists(apacheLicensePath), "Expected Apache-2.0 license text to exist.");
        Assert.True(File.Exists(licenseInventoryPath), "Expected Python dependency license inventory to exist.");

        var spec = File.ReadAllText(specPath);
        var notices = File.ReadAllText(noticesPath);
        var license = File.ReadAllText(licensePath);
        var apacheLicense = File.ReadAllText(apacheLicensePath);
        var licenseInventory = File.ReadAllText(licenseInventoryPath);

        Assert.Contains("sidecar.py", spec, StringComparison.Ordinal);
        Assert.Contains("EasyGet.DouyinSidecar", spec, StringComparison.Ordinal);
        Assert.Contains("smoke-only", spec, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("real download self-contained runtime is not closed", spec, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DOUYIN_DOWNLOADER_PROMAX_ROOT", spec, StringComparison.Ordinal);
        Assert.Contains("pathex", spec, StringComparison.Ordinal);
        Assert.Contains("bundled_source_roots", spec, StringComparison.Ordinal);
        Assert.Contains("datas.append", spec, StringComparison.Ordinal);
        Assert.Contains("third_party_hiddenimports", spec, StringComparison.Ordinal);
        Assert.Contains("core.api_client", spec, StringComparison.Ordinal);
        Assert.Contains("storage.database", spec, StringComparison.Ordinal);
        Assert.Contains("playwright", spec, StringComparison.Ordinal);
        Assert.Contains("server", spec, StringComparison.Ordinal);
        Assert.Contains("whisper", spec, StringComparison.Ordinal);
        Assert.Contains("transcribe", spec, StringComparison.Ordinal);
        Assert.Contains(".cookies.json", spec, StringComparison.Ordinal);
        Assert.DoesNotContain(@"F:\AI\AIMadeupTools\05_ThirdPartyRepos", spec, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("douyin-downloader-promax", notices, StringComparison.Ordinal);
        Assert.Contains("MIT License", notices, StringComparison.Ordinal);
        Assert.Contains("smoke-only", notices, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("utils/xbogus.py", notices, StringComparison.Ordinal);
        Assert.Contains("utils/abogus.py", notices, StringComparison.Ordinal);
        Assert.Contains("Apache-2.0", notices, StringComparison.Ordinal);
        Assert.Contains("licenses/python-dependency-license-inventory.md", notices, StringComparison.Ordinal);
        Assert.Contains("MIT License", license, StringComparison.Ordinal);
        Assert.Contains("Copyright (c) 2026 jiji262", license, StringComparison.Ordinal);
        Assert.Contains("Apache License", apacheLicense, StringComparison.Ordinal);
        Assert.Contains("Version 2.0", apacheLicense, StringComparison.Ordinal);
        foreach (var packageName in new[] { "aiohttp", "httpx", "aiofiles", "aiosqlite", "rich", "PyYAML", "python-dateutil", "gmssl", "certifi" })
        {
            Assert.Contains(packageName, licenseInventory, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Contains("optional extras are not included", licenseInventory, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("playwright", licenseInventory, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("openai-whisper", licenseInventory, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WindowsPublishScriptStagesAndSmokesDouyinSidecarUnlessSkipped()
    {
        var root = TestRepositoryPaths.Root;
        var scriptPath = Path.Combine(root, "scripts", "publish-win-x64.ps1");
        var script = File.ReadAllText(scriptPath);

        Assert.Contains("SkipDouyinSidecar", script, StringComparison.Ordinal);
        Assert.Contains("build-douyin-sidecar.ps1", script, StringComparison.Ordinal);
        Assert.Contains("sidecars\\douyin", script, StringComparison.Ordinal);
        Assert.Contains("EasyGet.DouyinSidecar.exe", script, StringComparison.Ordinal);
        Assert.Contains("THIRD_PARTY_NOTICES.md", script, StringComparison.Ordinal);
        Assert.Contains("licenses\\douyin-downloader-promax-LICENSE.txt", script, StringComparison.Ordinal);
        Assert.Contains("licenses\\APACHE-2.0.txt", script, StringComparison.Ordinal);
        Assert.Contains("licenses\\python-dependency-license-inventory.md", script, StringComparison.Ordinal);
        Assert.Contains("sidecar-version.json", script, StringComparison.Ordinal);
        Assert.Contains("Test-DouyinSidecarSmoke", script, StringComparison.Ordinal);
        Assert.Contains("--emit-sample", script, StringComparison.Ordinal);
        Assert.Contains("RunDouyinImportSelfTest", script, StringComparison.Ordinal);
        Assert.Contains("DouyinSidecarPython", script, StringComparison.Ordinal);
        Assert.Contains("Python = $DouyinSidecarPython", script, StringComparison.Ordinal);
        Assert.Contains("Test-DouyinSidecarImportSelfTest", script, StringComparison.Ordinal);
        Assert.Contains("--self-test-imports", script, StringComparison.Ordinal);
        Assert.Contains("DOUYIN_DOWNLOADER_PROMAX_ROOT", script, StringComparison.Ordinal);
        Assert.Contains("Push-Location $smokeDir", script, StringComparison.Ordinal);
        Assert.Contains("Remove-Item Env:\\DOUYIN_DOWNLOADER_PROMAX_ROOT", script, StringComparison.Ordinal);
        Assert.DoesNotContain("$env:DOUYIN_DOWNLOADER_PROMAX_ROOT = $resolvedThirdPartyRoot", script, StringComparison.Ordinal);
        Assert.Contains("ConvertFrom-Json", script, StringComparison.Ordinal);
        Assert.Contains(".event -ne \"success\"", script, StringComparison.Ordinal);
        Assert.Contains("RequireDouyinSidecar:(-not $SkipDouyinSidecar)", script, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsPublishScriptSupportsOptionalDouyinRealDownloadSmokeGate()
    {
        var root = TestRepositoryPaths.Root;
        var scriptPath = Path.Combine(root, "scripts", "publish-win-x64.ps1");
        var script = File.ReadAllText(scriptPath);

        Assert.Contains("RunDouyinRealDownloadSmoke", script, StringComparison.Ordinal);
        Assert.Contains("DouyinRealSmokeUrl", script, StringComparison.Ordinal);
        Assert.Contains("DouyinCookieEnvVar", script, StringComparison.Ordinal);
        Assert.Contains("DouyinCookieFile", script, StringComparison.Ordinal);
        Assert.Contains("Cannot specify both -DouyinCookieEnvVar and -DouyinCookieFile", script, StringComparison.Ordinal);
        Assert.Contains("Douyin real download smoke requires -DouyinRealSmokeUrl", script, StringComparison.Ordinal);
        Assert.Contains("Douyin real download smoke requires -DouyinCookieEnvVar or -DouyinCookieFile", script, StringComparison.Ordinal);
        Assert.Contains("Test-DouyinSidecarRealDownloadSmoke", script, StringComparison.Ordinal);
        Assert.Contains("sidecars\\douyin", script, StringComparison.Ordinal);
        Assert.Contains("EasyGet.DouyinSidecar.exe", script, StringComparison.Ordinal);
        Assert.Contains("--cookie-env", script, StringComparison.Ordinal);
        Assert.Contains("--cookie-file", script, StringComparison.Ordinal);
        Assert.Contains("Push-Location $smokeCwd", script, StringComparison.Ordinal);
        Assert.Contains("Remove-Item Env:\\DOUYIN_DOWNLOADER_PROMAX_ROOT", script, StringComparison.Ordinal);
        Assert.Contains("manifest/output_file_path", script, StringComparison.Ordinal);
        Assert.Contains("file size was not greater than zero", script, StringComparison.Ordinal);
        Assert.Contains("outside smoke output dir", script, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsPublishScriptRejectsDouyinRealDownloadSmokeWithoutUrlBeforePublishing()
    {
        var root = TestRepositoryPaths.Root;
        var scriptPath = Path.Combine(root, "scripts", "publish-win-x64.ps1");

        var result = RunPowerShellScript(scriptPath, "-SkipTests", "-SkipZip", "-SkipDouyinSidecar", "-RunDouyinRealDownloadSmoke", "-DouyinCookieEnvVar", "EASYGET_TEST_COOKIE");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Douyin real download smoke requires -DouyinRealSmokeUrl", result.CombinedOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet restore", result.CombinedOutput, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WindowsPublishScriptRejectsDouyinRealDownloadSmokeWithoutCookieSourceBeforePublishing()
    {
        var root = TestRepositoryPaths.Root;
        var scriptPath = Path.Combine(root, "scripts", "publish-win-x64.ps1");

        var result = RunPowerShellScript(scriptPath, "-SkipTests", "-SkipZip", "-SkipDouyinSidecar", "-RunDouyinRealDownloadSmoke", "-DouyinRealSmokeUrl", "https://example.invalid/douyin-smoke");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Douyin real download smoke requires -DouyinCookieEnvVar or -DouyinCookieFile", result.CombinedOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet restore", result.CombinedOutput, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WindowsPublishScriptRejectsDouyinRealDownloadSmokeWithBothCookieSourcesBeforePublishing()
    {
        var root = TestRepositoryPaths.Root;
        var scriptPath = Path.Combine(root, "scripts", "publish-win-x64.ps1");
        var cookieFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(cookieFile, "raw-cookie-value-that-must-not-leak");

            var result = RunPowerShellScript(scriptPath, "-SkipTests", "-SkipZip", "-SkipDouyinSidecar", "-RunDouyinRealDownloadSmoke", "-DouyinRealSmokeUrl", "https://example.invalid/douyin-smoke", "-DouyinCookieEnvVar", "EASYGET_TEST_COOKIE", "-DouyinCookieFile", cookieFile);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("Cannot specify both -DouyinCookieEnvVar and -DouyinCookieFile", result.CombinedOutput, StringComparison.Ordinal);
            Assert.DoesNotContain("raw-cookie-value-that-must-not-leak", result.CombinedOutput, StringComparison.Ordinal);
            Assert.DoesNotContain(cookieFile, result.CombinedOutput, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(cookieFile);
        }
    }

    [Fact]
    public void WindowsPublishScriptMaintainsConservativeDouyinRealDownloadManifestDefaults()
    {
        var root = TestRepositoryPaths.Root;
        var scriptPath = Path.Combine(root, "scripts", "publish-win-x64.ps1");
        var script = File.ReadAllText(scriptPath);

        Assert.Contains("realDownloadVerified = $false", script, StringComparison.Ordinal);
        Assert.Contains("realDownloadVerifiedAtUtc = $null", script, StringComparison.Ordinal);
        Assert.Contains("realDownloadSmokeUrlHash = $null", script, StringComparison.Ordinal);
        Assert.Contains("realDownloadSmokeUsedCookieSource = $null", script, StringComparison.Ordinal);
        Assert.Contains("selfContainedRealDownload = ($runtimeBundled -and $importSelfTestPassed -and $realDownloadSmokePassed)", script, StringComparison.Ordinal);
        Assert.DoesNotContain("[string]$RealDownloadVerifiedAtUtc", script, StringComparison.Ordinal);
        Assert.DoesNotContain("[string]$RealDownloadSmokeUrlHash", script, StringComparison.Ordinal);
        Assert.DoesNotContain("[string]$RealDownloadSmokeUsedCookieSource", script, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsPublishScriptStoresDouyinRealDownloadSmokeMetadataWithoutRawUrlOrCookie()
    {
        var root = TestRepositoryPaths.Root;
        var scriptPath = Path.Combine(root, "scripts", "publish-win-x64.ps1");
        var script = File.ReadAllText(scriptPath);

        Assert.Contains("Get-DouyinRealSmokeUrlHash", script, StringComparison.Ordinal);
        Assert.Contains("realDownloadSmokeUrlHash = $realDownloadSmokeUrlHash", script, StringComparison.Ordinal);
        Assert.Contains("realDownloadSmokeUsedCookieSource = $realDownloadSmokeUsedCookieSource", script, StringComparison.Ordinal);
        Assert.Contains("\"env:$CookieEnvVar\"", script, StringComparison.Ordinal);
        Assert.Contains("\"file:$([System.IO.Path]::GetFileName($CookieFile))\"", script, StringComparison.Ordinal);
        Assert.DoesNotContain("\"env:$DouyinCookieEnvVar\"", script, StringComparison.Ordinal);
        Assert.DoesNotContain("\"file:$([System.IO.Path]::GetFileName($DouyinCookieFile))\"", script, StringComparison.Ordinal);
        Assert.Contains("Redact-DouyinSecretText", script, StringComparison.Ordinal);
        Assert.DoesNotContain("realDownloadSmokeUrl = $DouyinRealSmokeUrl", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Get-Content -Raw -LiteralPath $DouyinCookieFile", script, StringComparison.Ordinal);
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

    private static (int ExitCode, string CombinedOutput) RunPowerShellScript(string scriptPath, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start PowerShell.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        return (process.ExitCode, stdout + Environment.NewLine + stderr);
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
    public void InstallerScriptWritesSha256HashesToUpdateManifest()
    {
        var root = TestRepositoryPaths.Root;
        var scriptPath = Path.Combine(root, "scripts", "build-installer.ps1");
        var script = File.ReadAllText(scriptPath);

        Assert.Contains("Get-FileHash -Algorithm SHA256 -LiteralPath $setupPath", script, StringComparison.Ordinal);
        Assert.Contains("Get-FileHash -Algorithm SHA256 -LiteralPath $zipPath", script, StringComparison.Ordinal);
        Assert.Contains("setupSha256 = $setupHash.Hash", script, StringComparison.Ordinal);
        Assert.Contains("zipSha256 = $zipHash.Hash", script, StringComparison.Ordinal);
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
    public void GitHubReleaseWorkflowVerifiesManifestSha256Hashes()
    {
        var root = TestRepositoryPaths.Root;
        var workflowPath = Path.Combine(root, ".github", "workflows", "release.yml");
        var workflow = File.ReadAllText(workflowPath);

        Assert.Contains("Get-FileHash -Algorithm SHA256 -LiteralPath $setup.FullName", workflow, StringComparison.Ordinal);
        Assert.Contains("Get-FileHash -Algorithm SHA256 -LiteralPath $zip.FullName", workflow, StringComparison.Ordinal);
        Assert.Contains("$manifest.setupSha256", workflow, StringComparison.Ordinal);
        Assert.Contains("$manifest.zipSha256", workflow, StringComparison.Ordinal);
        Assert.Contains("Manifest setup hash does not match generated installer.", workflow, StringComparison.Ordinal);
        Assert.Contains("Manifest zip hash does not match generated portable zip.", workflow, StringComparison.Ordinal);
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
