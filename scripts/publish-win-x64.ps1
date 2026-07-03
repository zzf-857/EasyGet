[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [string]$Runtime = "win-x64",

    [bool]$SelfContained = $true,

    [switch]$SkipTests,

    [switch]$SkipZip,

    [switch]$SkipDouyinSidecar,

    [switch]$RunDouyinImportSelfTest,

    [switch]$RunDouyinRealDownloadSmoke,

    [string]$DouyinRealSmokeUrl = "",

    [string]$DouyinCookieEnvVar = "",

    [string]$DouyinCookieFile = "",

    [string]$DouyinSidecarPython = "python",

    [string]$DouyinThirdPartyRepoRoot = $env:DOUYIN_DOWNLOADER_PROMAX_ROOT,

    [string]$Version = "",

    [string]$OutputRoot = "artifacts\publish"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$projectPath = Join-Path $repoRoot "EasyGet.csproj"
$testProjectPath = Join-Path $repoRoot "EasyGet.Tests\EasyGet.Tests.csproj"
$sidecarBuildScriptPath = Join-Path $repoRoot "scripts\build-douyin-sidecar.ps1"

if (-not [string]::IsNullOrWhiteSpace($DouyinCookieEnvVar) -and -not [string]::IsNullOrWhiteSpace($DouyinCookieFile)) {
    throw "Cannot specify both -DouyinCookieEnvVar and -DouyinCookieFile. Choose exactly one Cookie source for Douyin real download smoke."
}

if ($RunDouyinRealDownloadSmoke) {
    if ([string]::IsNullOrWhiteSpace($DouyinRealSmokeUrl)) {
        throw "Douyin real download smoke requires -DouyinRealSmokeUrl."
    }

    if ([string]::IsNullOrWhiteSpace($DouyinCookieEnvVar) -and [string]::IsNullOrWhiteSpace($DouyinCookieFile)) {
        throw "Douyin real download smoke requires -DouyinCookieEnvVar or -DouyinCookieFile."
    }

    if ($SkipDouyinSidecar) {
        throw "Douyin real download smoke requires the staged Douyin sidecar; remove -SkipDouyinSidecar."
    }

    if (-not [string]::IsNullOrWhiteSpace($DouyinCookieEnvVar)) {
        $cookieValue = [System.Environment]::GetEnvironmentVariable($DouyinCookieEnvVar)
        if ([string]::IsNullOrWhiteSpace($cookieValue)) {
            throw "Douyin real download smoke Cookie environment variable is not set or empty: $DouyinCookieEnvVar"
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($DouyinCookieFile)) {
        if (-not (Test-Path -LiteralPath $DouyinCookieFile -PathType Leaf)) {
            throw "Douyin real download smoke Cookie file was not found: $([System.IO.Path]::GetFileName($DouyinCookieFile))"
        }

        $cookieFileInfo = Get-Item -LiteralPath $DouyinCookieFile
        if ($cookieFileInfo.Length -le 0) {
            throw "Douyin real download smoke Cookie file is empty: $($cookieFileInfo.Name)"
        }
    }
}

if (-not [System.IO.Path]::IsPathRooted($OutputRoot)) {
    $OutputRoot = Join-Path $repoRoot $OutputRoot
}

$publishDir = Join-Path $OutputRoot "$Configuration\$Runtime"
$selfContainedValue = $SelfContained.ToString().ToLowerInvariant()

$outputRootFullPath = [System.IO.Path]::GetFullPath($OutputRoot).TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
$publishDirFullPath = [System.IO.Path]::GetFullPath($publishDir)
if (-not $publishDirFullPath.StartsWith($outputRootFullPath + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to clean publish directory outside output root: $publishDirFullPath"
}

if (Test-Path -LiteralPath $publishDirFullPath) {
    Write-Host "[EasyGet] Clean $publishDirFullPath"
    Remove-Item -LiteralPath $publishDirFullPath -Recurse -Force
}

Write-Host "[EasyGet] Restore"
dotnet restore $projectPath -r $Runtime

if (-not $SkipTests) {
    dotnet restore $testProjectPath
    Write-Host "[EasyGet] Test"
    dotnet test $testProjectPath -c $Configuration --no-restore
}

Write-Host "[EasyGet] Publish $Configuration $Runtime"
$publishArgs = @(
    "publish",
    $projectPath,
    "-c", $Configuration,
    "-r", $Runtime,
    "--no-restore",
    "--self-contained", $selfContainedValue,
    "-o", $publishDir,
    "/p:PublishSingleFile=false",
    "/p:PublishReadyToRun=false",
    "/p:DebugType=none",
    "/p:DebugSymbols=false"
)

if (-not [string]::IsNullOrWhiteSpace($Version)) {
    $publishArgs += "/p:Version=$Version"
    $publishArgs += "/p:FileVersion=$Version.0"
    $publishArgs += "/p:AssemblyVersion=$Version.0"
}

dotnet @publishArgs

$publishPrunePatterns = @(
    "*.pdb",
    "createdump.exe",
    "mscordaccore*.dll",
    "mscordbi.dll",
    "Microsoft.DiaSymReader.Native.amd64.dll",
    "System.Windows.Forms.Design*.dll",
    "System.Design.dll",
    "System.Drawing.Design.dll"
)

foreach ($pattern in $publishPrunePatterns) {
    Get-ChildItem -LiteralPath $publishDir -Filter $pattern -Recurse -File -ErrorAction SilentlyContinue |
        Remove-Item -Force
}

$exePath = Join-Path $publishDir "EasyGet.exe"
if (-not (Test-Path $exePath)) {
    throw "Publish smoke check failed: EasyGet.exe was not found at $exePath"
}

$exeInfo = Get-Item $exePath
if ($exeInfo.Length -le 0) {
    throw "Publish smoke check failed: EasyGet.exe is empty."
}

Write-Host "[EasyGet] Smoke check passed: $exePath"

function Test-RequiredReleaseFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$Description
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Description was not found at $Path"
    }

    $item = Get-Item -LiteralPath $Path
    if ($item.Length -le 0) {
        throw "$Description is empty at $Path"
    }
}

function Test-DouyinSidecarSmoke {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ExePath
    )

    $smokeDir = Join-Path ([System.IO.Path]::GetTempPath()) ("easyget-douyin-sidecar-smoke-" + [System.Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $smokeDir -Force | Out-Null
    try {
        $stderrPath = Join-Path $smokeDir "stderr.txt"
        $smokeArgs = @(
            "--url", "https://www.douyin.com/video/7604129988555574538",
            "--output-dir", $smokeDir,
            "--emit-sample"
        )

        $stdoutLines = & $ExePath @smokeArgs 2> $stderrPath
        $exitCode = $LASTEXITCODE
        $stderr = ""
        if (Test-Path -LiteralPath $stderrPath) {
            $stderr = Get-Content -Raw -LiteralPath $stderrPath
        }

        if ($exitCode -ne 0) {
            throw "Douyin sidecar smoke failed with exit code $exitCode. stderr: $stderr"
        }

        $events = @()
        foreach ($line in @($stdoutLines)) {
            if ([string]::IsNullOrWhiteSpace($line)) {
                continue
            }

            try {
                $events += ($line | ConvertFrom-Json)
            }
            catch {
                throw "Douyin sidecar smoke failed: stdout line is not JSON: $line"
            }
        }

        if ($events.Count -eq 0) {
            throw "Douyin sidecar smoke failed: no stdout JSON events were emitted."
        }

        if ($events[-1].event -ne "success") {
            throw "Douyin sidecar smoke failed: final event was '$($events[-1].event)', expected 'success'."
        }
    }
    finally {
        if (Test-Path -LiteralPath $smokeDir) {
            Remove-Item -LiteralPath $smokeDir -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

function Test-DouyinSidecarImportSelfTest {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ExePath,

        [Parameter(Mandatory = $true)]
        [string]$ThirdPartyRepoRoot
    )

    if ([string]::IsNullOrWhiteSpace($ThirdPartyRepoRoot)) {
        throw "Douyin import self-test requires -DouyinThirdPartyRepoRoot or DOUYIN_DOWNLOADER_PROMAX_ROOT."
    }

    if (-not (Test-Path -LiteralPath $ThirdPartyRepoRoot -PathType Container)) {
        throw "Douyin import self-test third-party root does not exist: $ThirdPartyRepoRoot"
    }

    $resolvedThirdPartyRoot = (Resolve-Path -LiteralPath $ThirdPartyRepoRoot).Path
    $smokeDir = Join-Path ([System.IO.Path]::GetTempPath()) ("easyget-douyin-sidecar-import-self-test-" + [System.Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $smokeDir -Force | Out-Null
    $previousThirdPartyRoot = $env:DOUYIN_DOWNLOADER_PROMAX_ROOT
    $locationPushed = $false

    try {
        # Build-time root proves which source tree was bundled. Runtime import
        # self-test must not use that external path, otherwise it would not
        # prove the onefile can find its bundled douyin-downloader-promax tree.
        Remove-Item Env:\DOUYIN_DOWNLOADER_PROMAX_ROOT -ErrorAction SilentlyContinue
        Push-Location $smokeDir
        $locationPushed = $true

        $stderrPath = Join-Path $smokeDir "stderr.txt"
        $selfTestArgs = @(
            "--url", "https://www.douyin.com/video/7604129988555574538",
            "--output-dir", $smokeDir,
            "--self-test-imports"
        )

        $stdoutLines = & $ExePath @selfTestArgs 2> $stderrPath
        $exitCode = $LASTEXITCODE
        $stderr = ""
        if (Test-Path -LiteralPath $stderrPath) {
            $stderr = Get-Content -Raw -LiteralPath $stderrPath
        }

        if ($exitCode -ne 0) {
            throw "Douyin sidecar import self-test failed with exit code $exitCode. stderr: $stderr stdout: $($stdoutLines -join "`n")"
        }

        $events = @()
        foreach ($line in @($stdoutLines)) {
            if ([string]::IsNullOrWhiteSpace($line)) {
                continue
            }

            try {
                $events += ($line | ConvertFrom-Json)
            }
            catch {
                throw "Douyin sidecar import self-test failed: stdout line is not JSON: $line"
            }
        }

        if ($events.Count -ne 1) {
            throw "Douyin sidecar import self-test failed: expected exactly one JSON event, got $($events.Count)."
        }

        if ($events[-1].event -ne "success") {
            throw "Douyin sidecar import self-test failed: final event was '$($events[-1].event)', expected 'success'."
        }

        if ($events[-1].details.imports_ok -ne $true) {
            throw "Douyin sidecar import self-test failed: imports_ok was not true."
        }
    }
    finally {
        if ($locationPushed) {
            Pop-Location
        }

        if ($null -eq $previousThirdPartyRoot) {
            Remove-Item Env:\DOUYIN_DOWNLOADER_PROMAX_ROOT -ErrorAction SilentlyContinue
        }
        else {
            $env:DOUYIN_DOWNLOADER_PROMAX_ROOT = $previousThirdPartyRoot
        }

        if (Test-Path -LiteralPath $smokeDir) {
            Remove-Item -LiteralPath $smokeDir -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

function Get-DouyinJsonPropertyValue {
    param(
        [object]$Object,
        [string]$PropertyName
    )

    if ($null -eq $Object) {
        return $null
    }

    $property = $Object.PSObject.Properties[$PropertyName]
    if ($null -eq $property) {
        return $null
    }

    return $property.Value
}

function Get-DouyinRealSmokeUrlHash {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Url
    )

    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($Url)
        $hashBytes = $sha256.ComputeHash($bytes)
        return ([System.BitConverter]::ToString($hashBytes) -replace "-", "").ToLowerInvariant()
    }
    finally {
        $sha256.Dispose()
    }
}

function Get-DouyinRealSmokeCookieSourceLabel {
    param(
        [string]$CookieEnvVar,
        [string]$CookieFile
    )

    if (-not [string]::IsNullOrWhiteSpace($CookieEnvVar)) {
        return "env:$CookieEnvVar"
    }

    return "file:$([System.IO.Path]::GetFileName($CookieFile))"
}

function Redact-DouyinSecretText {
    param(
        [AllowNull()]
        [string]$Text,

        [string]$Url,

        [string]$CookieEnvVar,

        [string]$CookieFile
    )

    if ($null -eq $Text) {
        return ""
    }

    $redacted = [string]$Text
    if (-not [string]::IsNullOrWhiteSpace($Url)) {
        $redacted = $redacted.Replace($Url, "[REDACTED_URL]")
    }

    if (-not [string]::IsNullOrWhiteSpace($CookieEnvVar)) {
        $cookieValue = [System.Environment]::GetEnvironmentVariable($CookieEnvVar)
        if (-not [string]::IsNullOrWhiteSpace($cookieValue)) {
            $redacted = $redacted.Replace($cookieValue, "[REDACTED_COOKIE]")
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($CookieFile) -and (Test-Path -LiteralPath $CookieFile -PathType Leaf)) {
        $cookieFileInfo = Get-Item -LiteralPath $CookieFile
        $redacted = $redacted.Replace($cookieFileInfo.FullName, $cookieFileInfo.Name)
        $cookieFileText = [System.IO.File]::ReadAllText($cookieFileInfo.FullName)
        if (-not [string]::IsNullOrWhiteSpace($cookieFileText)) {
            $redacted = $redacted.Replace($cookieFileText, "[REDACTED_COOKIE]")
        }
    }

    return $redacted
}

function Resolve-DouyinSmokeOutputPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$OutputDir
    )

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $OutputDir $Path))
}

function Test-DouyinManifestFileName {
    param(
        [AllowNull()]
        [string]$FileName
    )

    if ([string]::IsNullOrWhiteSpace($FileName)) {
        return $false
    }

    if ($FileName.Equals("download_manifest.jsonl", [System.StringComparison]::OrdinalIgnoreCase)) {
        return $true
    }

    return $FileName -match '^download_manifest\.easyget-\d{8}T\d{6}Z-[0-9a-f]{8}\.jsonl$'
}

function Assert-DouyinSmokePathInsideOutputDir {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$OutputDir
    )

    $resolvedPath = Resolve-DouyinSmokeOutputPath -Path $Path -OutputDir $OutputDir
    $resolvedOutputDir = [System.IO.Path]::GetFullPath($OutputDir).TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
    $outputPrefix = $resolvedOutputDir + [System.IO.Path]::DirectorySeparatorChar
    if (-not $resolvedPath.StartsWith($outputPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Douyin real download smoke failed: output path was outside smoke output dir: $resolvedPath"
    }

    return $resolvedPath
}

function Add-DouyinSmokePathCandidate {
    param(
        [Parameter(Mandatory = $true)]
        [System.Collections.Generic.List[string]]$Paths,

        [AllowNull()]
        [object]$Value
    )

    if ($null -eq $Value) {
        return
    }

    if ($Value -is [string]) {
        if (-not [string]::IsNullOrWhiteSpace($Value)) {
            $Paths.Add($Value)
        }
        return
    }

    if ($Value -is [System.Array]) {
        foreach ($item in $Value) {
            Add-DouyinSmokePathCandidate -Paths $Paths -Value $item
        }
        return
    }

    foreach ($propertyName in @("output_file_path", "outputFilePath", "manifest_path", "manifestPath", "file_path", "filePath", "path")) {
        Add-DouyinSmokePathCandidate -Paths $Paths -Value (Get-DouyinJsonPropertyValue -Object $Value -PropertyName $propertyName)
    }

    foreach ($collectionName in @("output_files", "outputFiles", "outputs", "file_paths", "filePaths", "paths")) {
        Add-DouyinSmokePathCandidate -Paths $Paths -Value (Get-DouyinJsonPropertyValue -Object $Value -PropertyName $collectionName)
    }
}

function Add-DouyinManifestOutputPathCandidates {
    param(
        [Parameter(Mandatory = $true)]
        [System.Collections.Generic.List[string]]$Paths,

        [Parameter(Mandatory = $true)]
        [string]$ManifestPath
    )

    if (-not (Test-Path -LiteralPath $ManifestPath -PathType Leaf)) {
        return
    }

    foreach ($line in @(Get-Content -LiteralPath $ManifestPath)) {
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }

        try {
            $manifestItem = $line | ConvertFrom-Json
        }
        catch {
            continue
        }

        Add-DouyinSmokePathCandidate -Paths $Paths -Value $manifestItem
    }
}

function Test-DouyinSidecarRealDownloadSmoke {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ExePath,

        [Parameter(Mandatory = $true)]
        [string]$Url,

        [string]$CookieEnvVar,

        [string]$CookieFile
    )

    $smokeRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("easyget-douyin-sidecar-real-smoke-" + [System.Guid]::NewGuid().ToString("N"))
    $smokeCwd = Join-Path $smokeRoot "cwd"
    $smokeOutputDir = Join-Path $smokeRoot "output"
    New-Item -ItemType Directory -Path $smokeCwd -Force | Out-Null
    New-Item -ItemType Directory -Path $smokeOutputDir -Force | Out-Null

    $previousThirdPartyRoot = $env:DOUYIN_DOWNLOADER_PROMAX_ROOT
    $locationPushed = $false

    try {
        Remove-Item Env:\DOUYIN_DOWNLOADER_PROMAX_ROOT -ErrorAction SilentlyContinue
        Push-Location $smokeCwd
        $locationPushed = $true

        $stderrPath = Join-Path $smokeRoot "stderr.txt"
        $smokeArgs = @(
            "--url", $Url,
            "--output-dir", $smokeOutputDir
        )

        if (-not [string]::IsNullOrWhiteSpace($CookieEnvVar)) {
            $smokeArgs += "--cookie-env"
            $smokeArgs += $CookieEnvVar
        }
        else {
            $smokeArgs += "--cookie-file"
            $smokeArgs += $CookieFile
        }

        $stdoutLines = & $ExePath @smokeArgs 2> $stderrPath
        $exitCode = $LASTEXITCODE
        $stderr = ""
        if (Test-Path -LiteralPath $stderrPath) {
            $stderr = Get-Content -Raw -LiteralPath $stderrPath
        }

        if ($exitCode -ne 0) {
            $safeStderr = Redact-DouyinSecretText -Text $stderr -Url $Url -CookieEnvVar $CookieEnvVar -CookieFile $CookieFile
            $safeStdout = Redact-DouyinSecretText -Text ($stdoutLines -join "`n") -Url $Url -CookieEnvVar $CookieEnvVar -CookieFile $CookieFile
            throw "Douyin real download smoke failed with exit code $exitCode. stderr: $safeStderr stdout: $safeStdout"
        }

        $events = @()
        foreach ($line in @($stdoutLines)) {
            if ([string]::IsNullOrWhiteSpace($line)) {
                continue
            }

            try {
                $events += ($line | ConvertFrom-Json)
            }
            catch {
                $safeLine = Redact-DouyinSecretText -Text $line -Url $Url -CookieEnvVar $CookieEnvVar -CookieFile $CookieFile
                throw "Douyin real download smoke failed: stdout line is not JSON: $safeLine"
            }
        }

        if ($events.Count -eq 0) {
            throw "Douyin real download smoke failed: no stdout JSON events were emitted."
        }

        $finalEvent = $events[-1]
        if ($finalEvent.event -ne "success") {
            $safeEvent = Redact-DouyinSecretText -Text ($finalEvent | ConvertTo-Json -Depth 12 -Compress) -Url $Url -CookieEnvVar $CookieEnvVar -CookieFile $CookieFile
            throw "Douyin real download smoke failed: final event was '$($finalEvent.event)', expected 'success'. Final event: $safeEvent"
        }

        $details = Get-DouyinJsonPropertyValue -Object $finalEvent -PropertyName "details"
        $primaryPath = Get-DouyinJsonPropertyValue -Object $finalEvent -PropertyName "output_file_path"
        if ([string]::IsNullOrWhiteSpace($primaryPath)) {
            $primaryPath = Get-DouyinJsonPropertyValue -Object $details -PropertyName "output_file_path"
        }
        if ([string]::IsNullOrWhiteSpace($primaryPath)) {
            $primaryPath = Get-DouyinJsonPropertyValue -Object $finalEvent -PropertyName "manifest_path"
        }
        if ([string]::IsNullOrWhiteSpace($primaryPath)) {
            $primaryPath = Get-DouyinJsonPropertyValue -Object $details -PropertyName "manifest_path"
        }
        if ([string]::IsNullOrWhiteSpace($primaryPath)) {
            $primaryPath = Get-DouyinJsonPropertyValue -Object $details -PropertyName "manifestPath"
        }

        if ([string]::IsNullOrWhiteSpace($primaryPath)) {
            throw "Douyin real download smoke failed: manifest/output_file_path was not emitted or does not exist."
        }

        $resolvedPrimaryPath = Assert-DouyinSmokePathInsideOutputDir -Path $primaryPath -OutputDir $smokeOutputDir
        if (-not (Test-Path -LiteralPath $resolvedPrimaryPath -PathType Leaf)) {
            throw "Douyin real download smoke failed: manifest/output_file_path was not emitted or does not exist."
        }

        $primaryFileInfo = Get-Item -LiteralPath $resolvedPrimaryPath
        if ($primaryFileInfo.Length -le 0) {
            throw "Douyin real download smoke failed: output file size was not greater than zero: $($primaryFileInfo.Name)"
        }

        $declaredPaths = [System.Collections.Generic.List[string]]::new()
        Add-DouyinSmokePathCandidate -Paths $declaredPaths -Value $finalEvent
        Add-DouyinSmokePathCandidate -Paths $declaredPaths -Value $details

        $manifestCandidates = $declaredPaths |
            Where-Object { Test-DouyinManifestFileName -FileName ([System.IO.Path]::GetFileName($_)) }
        foreach ($manifestCandidate in @($manifestCandidates)) {
            $resolvedManifestCandidate = Assert-DouyinSmokePathInsideOutputDir -Path $manifestCandidate -OutputDir $smokeOutputDir
            Add-DouyinManifestOutputPathCandidates -Paths $declaredPaths -ManifestPath $resolvedManifestCandidate
        }

        foreach ($declaredPath in @($declaredPaths | Select-Object -Unique)) {
            if ([string]::IsNullOrWhiteSpace($declaredPath)) {
                continue
            }

            [void](Assert-DouyinSmokePathInsideOutputDir -Path $declaredPath -OutputDir $smokeOutputDir)
        }
    }
    finally {
        if ($locationPushed) {
            Pop-Location
        }

        if ($null -eq $previousThirdPartyRoot) {
            Remove-Item Env:\DOUYIN_DOWNLOADER_PROMAX_ROOT -ErrorAction SilentlyContinue
        }
        else {
            $env:DOUYIN_DOWNLOADER_PROMAX_ROOT = $previousThirdPartyRoot
        }

        if (Test-Path -LiteralPath $smokeRoot) {
            Remove-Item -LiteralPath $smokeRoot -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

function Update-DouyinSidecarVersionManifest {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [bool]$ImportSelfTestPassed,

        [bool]$RealDownloadSmokePassed,

        [AllowNull()]
        [object]$RealDownloadVerifiedAtUtc,

        [AllowNull()]
        [object]$RealDownloadSmokeUrlHash,

        [AllowNull()]
        [object]$RealDownloadSmokeUsedCookieSource
    )

    $manifest = Get-Content -Raw -LiteralPath $Path | ConvertFrom-Json
    $runtimeBundled = ($manifest.runtimeRequiresExternalPython -eq $false)
    $selfContainedRealDownload = ($runtimeBundled -and $importSelfTestPassed -and $realDownloadSmokePassed)

    $realDownloadVerified = $RealDownloadSmokePassed
    $realDownloadVerifiedAtUtc = $RealDownloadVerifiedAtUtc
    $realDownloadSmokeUrlHash = $RealDownloadSmokeUrlHash
    $realDownloadSmokeUsedCookieSource = $RealDownloadSmokeUsedCookieSource
    $manifestRealDownloadFields = [ordered]@{
        realDownloadVerified = $realDownloadVerified
        realDownloadVerifiedAtUtc = $realDownloadVerifiedAtUtc
        realDownloadSmokeUrlHash = $realDownloadSmokeUrlHash
        realDownloadSmokeUsedCookieSource = $realDownloadSmokeUsedCookieSource
        selfContainedRealDownload = $selfContainedRealDownload
    }

    foreach ($fieldName in $manifestRealDownloadFields.Keys) {
        $manifest | Add-Member -NotePropertyName $fieldName -NotePropertyValue $manifestRealDownloadFields[$fieldName] -Force
    }

    if ($null -ne $manifest.importSelfTest) {
        $manifest.importSelfTest | Add-Member -NotePropertyName lastRunInPublish -NotePropertyValue $ImportSelfTestPassed -Force
    }

    $manifest | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $Path -Encoding UTF8
}

if ($SkipDouyinSidecar) {
    Write-Host "[EasyGet] Skip Douyin sidecar staging"
}
else {
    if (-not (Test-Path -LiteralPath $sidecarBuildScriptPath -PathType Leaf)) {
        throw "Douyin sidecar build script was not found at $sidecarBuildScriptPath"
    }

    $sidecarOutputRoot = Join-Path $repoRoot "artifacts\sidecar"
    $sidecarBuildArgs = @{
        Configuration = $Configuration
        Runtime = $Runtime
        OutputRoot = $sidecarOutputRoot
        Python = $DouyinSidecarPython
    }
    if (-not [string]::IsNullOrWhiteSpace($DouyinThirdPartyRepoRoot)) {
        $sidecarBuildArgs.ThirdPartyRepoRoot = $DouyinThirdPartyRepoRoot
    }

    & $sidecarBuildScriptPath @sidecarBuildArgs

    $sidecarStageDir = Join-Path $sidecarOutputRoot $Runtime
    $sidecarPublishDir = Join-Path $publishDir "sidecars\douyin"
    New-Item -ItemType Directory -Path $sidecarPublishDir -Force | Out-Null
    Copy-Item -Path (Join-Path $sidecarStageDir "*") -Destination $sidecarPublishDir -Recurse -Force

    $sidecarExePath = Join-Path $sidecarPublishDir "EasyGet.DouyinSidecar.exe"
    $sidecarNoticesPath = Join-Path $sidecarPublishDir "THIRD_PARTY_NOTICES.md"
    $sidecarLicensePath = Join-Path $sidecarPublishDir "licenses\douyin-downloader-promax-LICENSE.txt"
    $sidecarApacheLicensePath = Join-Path $sidecarPublishDir "licenses\APACHE-2.0.txt"
    $sidecarLicenseInventoryPath = Join-Path $sidecarPublishDir "licenses\python-dependency-license-inventory.md"
    $sidecarVersionPath = Join-Path $sidecarPublishDir "sidecar-version.json"

    Test-RequiredReleaseFile -Path $sidecarExePath -Description "Douyin sidecar executable"
    Test-RequiredReleaseFile -Path $sidecarNoticesPath -Description "Douyin sidecar third-party notices"
    Test-RequiredReleaseFile -Path $sidecarLicensePath -Description "Douyin sidecar license"
    Test-RequiredReleaseFile -Path $sidecarApacheLicensePath -Description "Douyin sidecar Apache-2.0 license"
    Test-RequiredReleaseFile -Path $sidecarLicenseInventoryPath -Description "Douyin sidecar Python dependency license inventory"
    Test-RequiredReleaseFile -Path $sidecarVersionPath -Description "Douyin sidecar version manifest"
    $importSelfTestPassed = $false
    $realDownloadVerified = $false
    $realDownloadSmokePassed = $false
    $realDownloadVerifiedAtUtc = $null
    $realDownloadSmokeUrlHash = $null
    $realDownloadSmokeUsedCookieSource = $null

    Test-DouyinSidecarSmoke -ExePath $sidecarExePath
    Write-Host "[EasyGet] Douyin sidecar smoke check passed: $sidecarExePath"
    if ($RunDouyinImportSelfTest) {
        Test-DouyinSidecarImportSelfTest -ExePath $sidecarExePath -ThirdPartyRepoRoot $DouyinThirdPartyRepoRoot
        $importSelfTestPassed = $true
        Write-Host "[EasyGet] Douyin sidecar import self-test passed: $sidecarExePath"
    }

    if ($RunDouyinRealDownloadSmoke) {
        $realDownloadSmokeUrlHash = Get-DouyinRealSmokeUrlHash -Url $DouyinRealSmokeUrl
        $realDownloadSmokeUsedCookieSource = Get-DouyinRealSmokeCookieSourceLabel -CookieEnvVar $DouyinCookieEnvVar -CookieFile $DouyinCookieFile
        Test-DouyinSidecarRealDownloadSmoke -ExePath $sidecarExePath -Url $DouyinRealSmokeUrl -CookieEnvVar $DouyinCookieEnvVar -CookieFile $DouyinCookieFile
        $realDownloadVerified = $true
        $realDownloadSmokePassed = $true
        $realDownloadVerifiedAtUtc = (Get-Date).ToUniversalTime().ToString("o")
        Write-Host "[EasyGet] Douyin sidecar real download smoke passed: urlHash=$realDownloadSmokeUrlHash cookieSource=$realDownloadSmokeUsedCookieSource"
    }

    Update-DouyinSidecarVersionManifest `
        -Path $sidecarVersionPath `
        -ImportSelfTestPassed $importSelfTestPassed `
        -RealDownloadSmokePassed $realDownloadVerified `
        -RealDownloadVerifiedAtUtc $realDownloadVerifiedAtUtc `
        -RealDownloadSmokeUrlHash $realDownloadSmokeUrlHash `
        -RealDownloadSmokeUsedCookieSource $realDownloadSmokeUsedCookieSource

    if ($realDownloadSmokePassed) {
        Write-Host "[EasyGet] Douyin sidecar manifest records real download verification."
    }
    else {
        Write-Host "[EasyGet] Douyin sidecar is a smoke-only packaging skeleton unless -RunDouyinRealDownloadSmoke passes with Cookie-backed real download verification."
    }
}

function Test-PortableZipContent {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ZipPath,

        [bool]$RequireDouyinSidecar = $true
    )

    $blockedZipEntryPatterns = @(
        "*.pdb",
        "createdump.exe",
        "mscordaccore*.dll",
        "mscordbi.dll",
        "Microsoft.DiaSymReader.Native.amd64.dll",
        "System.Windows.Forms.Design*.dll",
        "System.Design.dll",
        "System.Drawing.Design.dll"
    )

    $zip = [System.IO.Compression.ZipFile]::OpenRead($ZipPath)
    try {
        $entries = @($zip.Entries)
        $rootExe = $entries | Where-Object { $_.FullName -eq "EasyGet.exe" } | Select-Object -First 1
        if (-not $rootExe) {
            throw "Portable zip smoke check failed: EasyGet.exe was not found at the zip root."
        }

        foreach ($pattern in $blockedZipEntryPatterns) {
            $matcher = [System.Management.Automation.WildcardPattern]::new($pattern, [System.Management.Automation.WildcardOptions]::IgnoreCase)
            $blockedEntry = $entries |
                Where-Object { $matcher.IsMatch([System.IO.Path]::GetFileName($_.FullName)) } |
                Select-Object -First 1
            if ($blockedEntry) {
                throw "Portable zip smoke check failed: diagnostic/runtime debugging file was included: $($blockedEntry.FullName)"
            }
        }

        if ($RequireDouyinSidecar) {
            $requiredSidecarEntries = @(
                "sidecars/douyin/EasyGet.DouyinSidecar.exe",
                "sidecars/douyin/THIRD_PARTY_NOTICES.md",
                "sidecars/douyin/licenses/douyin-downloader-promax-LICENSE.txt",
                "sidecars/douyin/licenses/APACHE-2.0.txt",
                "sidecars/douyin/licenses/python-dependency-license-inventory.md",
                "sidecars/douyin/sidecar-version.json"
            )

            foreach ($requiredEntry in $requiredSidecarEntries) {
                $entry = $entries |
                    Where-Object { ($_.FullName -replace "\\", "/") -eq $requiredEntry } |
                    Select-Object -First 1
                if (-not $entry) {
                    throw "Portable zip smoke check failed: Douyin sidecar entry was missing: $requiredEntry"
                }
                if ($entry.Length -le 0) {
                    throw "Portable zip smoke check failed: Douyin sidecar entry was empty: $requiredEntry"
                }
            }
        }
    }
    finally {
        $zip.Dispose()
    }
}

if (-not $SkipZip) {
    $zipPath = Join-Path (Split-Path $publishDir -Parent) "EasyGet-$Runtime-$Configuration.zip"
    if (Test-Path $zipPath) {
        Remove-Item -LiteralPath $zipPath -Force
    }

    Write-Host "[EasyGet] Zip $zipPath"
    Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $zipPath -Force
    Test-PortableZipContent -ZipPath $zipPath -RequireDouyinSidecar:(-not $SkipDouyinSidecar)
    Write-Host "[EasyGet] Zip ready: $zipPath"
}

Write-Host "[EasyGet] Publish ready: $publishDir"
