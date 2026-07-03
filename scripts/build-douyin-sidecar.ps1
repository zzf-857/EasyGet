[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [string]$Runtime = "win-x64",

    [string]$OutputRoot = "artifacts\sidecar",

    [switch]$SkipBuild,

    [string]$Python = "python",

    [string]$ThirdPartyRepoRoot = $env:DOUYIN_DOWNLOADER_PROMAX_ROOT
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$sidecarRoot = Join-Path $repoRoot "tools\douyin-sidecar"
$sidecarSource = Join-Path $sidecarRoot "sidecar.py"
$specPath = Join-Path $sidecarRoot "EasyGet.DouyinSidecar.spec"
$noticesSource = Join-Path $sidecarRoot "THIRD_PARTY_NOTICES.md"
$licenseSource = Join-Path $sidecarRoot "licenses\douyin-downloader-promax-LICENSE.txt"
$apacheLicenseSource = Join-Path $sidecarRoot "licenses\APACHE-2.0.txt"
$licenseInventorySource = Join-Path $sidecarRoot "licenses\python-dependency-license-inventory.md"

if (-not [System.IO.Path]::IsPathRooted($OutputRoot)) {
    $OutputRoot = Join-Path $repoRoot $OutputRoot
}

$outputRootFullPath = [System.IO.Path]::GetFullPath($OutputRoot).TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
$stageDir = Join-Path $OutputRoot $Runtime
$stageDirFullPath = [System.IO.Path]::GetFullPath($stageDir)
$distDir = Join-Path $OutputRoot "dist\$Runtime"
$workDir = Join-Path $OutputRoot "obj\$Runtime"

if (-not $stageDirFullPath.StartsWith($outputRootFullPath + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to clean sidecar staging directory outside output root: $stageDirFullPath"
}

foreach ($requiredPath in @($sidecarSource, $specPath, $noticesSource, $licenseSource, $apacheLicenseSource, $licenseInventorySource)) {
    if (-not (Test-Path -LiteralPath $requiredPath)) {
        throw "Required sidecar release input is missing: $requiredPath"
    }
}

$thirdPartyRepoPath = $null
if (-not [string]::IsNullOrWhiteSpace($ThirdPartyRepoRoot)) {
    if (-not (Test-Path -LiteralPath $ThirdPartyRepoRoot -PathType Container)) {
        throw "Douyin third-party repo root was provided but does not exist: $ThirdPartyRepoRoot"
    }

    $thirdPartyRepoPath = (Resolve-Path -LiteralPath $ThirdPartyRepoRoot).Path
}

if (Test-Path -LiteralPath $stageDirFullPath) {
    Write-Host "[EasyGet] Clean Douyin sidecar stage $stageDirFullPath"
    Remove-Item -LiteralPath $stageDirFullPath -Recurse -Force
}

New-Item -ItemType Directory -Path $stageDirFullPath -Force | Out-Null
$exeStagePath = Join-Path $stageDirFullPath "EasyGet.DouyinSidecar.exe"
$pyinstallerVersion = $null

if ($SkipBuild) {
    Write-Warning "[EasyGet] Skipping PyInstaller build. Staging notices/version only; no sidecar exe will be produced."
}
else {
    Write-Host "[EasyGet] Check PyInstaller"
    try {
        $pyinstallerVersionOutput = & $Python -m PyInstaller --version 2>&1
    }
    catch {
        throw "Unable to run the selected Python command for the Douyin sidecar build. Python: $Python. Error: $_"
    }
    if ($LASTEXITCODE -ne 0) {
        $message = ($pyinstallerVersionOutput -join "`n").Trim()
        throw "PyInstaller is required to build the Douyin sidecar. Install it in the selected Python environment, then retry. Python: $Python. Output: $message"
    }
    $pyinstallerVersion = ($pyinstallerVersionOutput -join "`n").Trim()

    foreach ($buildPath in @($distDir, $workDir)) {
        $buildPathFullPath = [System.IO.Path]::GetFullPath($buildPath)
        if (-not $buildPathFullPath.StartsWith($outputRootFullPath + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to clean PyInstaller path outside output root: $buildPathFullPath"
        }
        if (Test-Path -LiteralPath $buildPathFullPath) {
            Remove-Item -LiteralPath $buildPathFullPath -Recurse -Force
        }
    }

    Write-Host "[EasyGet] Build Douyin sidecar onefile smoke skeleton"
    $previousThirdPartyRoot = $env:DOUYIN_DOWNLOADER_PROMAX_ROOT
    try {
        if ($thirdPartyRepoPath) {
            $env:DOUYIN_DOWNLOADER_PROMAX_ROOT = $thirdPartyRepoPath
        }
        else {
            Remove-Item Env:\DOUYIN_DOWNLOADER_PROMAX_ROOT -ErrorAction SilentlyContinue
        }

        & $Python -m PyInstaller `
            --noconfirm `
            --clean `
            --distpath $distDir `
            --workpath $workDir `
            $specPath
    }
    finally {
        if ($null -eq $previousThirdPartyRoot) {
            Remove-Item Env:\DOUYIN_DOWNLOADER_PROMAX_ROOT -ErrorAction SilentlyContinue
        }
        else {
            $env:DOUYIN_DOWNLOADER_PROMAX_ROOT = $previousThirdPartyRoot
        }
    }

    if ($LASTEXITCODE -ne 0) {
        throw "PyInstaller failed while building EasyGet.DouyinSidecar.exe."
    }

    $builtExe = Join-Path $distDir "EasyGet.DouyinSidecar.exe"
    if (-not (Test-Path -LiteralPath $builtExe)) {
        throw "PyInstaller completed but did not produce $builtExe."
    }

    $builtExeInfo = Get-Item -LiteralPath $builtExe
    if ($builtExeInfo.Length -le 0) {
        throw "PyInstaller produced an empty sidecar executable: $builtExe"
    }

    Copy-Item -LiteralPath $builtExe -Destination $exeStagePath -Force
}

Copy-Item -LiteralPath $noticesSource -Destination (Join-Path $stageDirFullPath "THIRD_PARTY_NOTICES.md") -Force
$licenseStageDir = Join-Path $stageDirFullPath "licenses"
New-Item -ItemType Directory -Path $licenseStageDir -Force | Out-Null
Copy-Item -LiteralPath $licenseSource -Destination (Join-Path $licenseStageDir "douyin-downloader-promax-LICENSE.txt") -Force
Copy-Item -LiteralPath $apacheLicenseSource -Destination (Join-Path $licenseStageDir "APACHE-2.0.txt") -Force
Copy-Item -LiteralPath $licenseInventorySource -Destination (Join-Path $licenseStageDir "python-dependency-license-inventory.md") -Force

$exeSha256 = $null
$artifactSizeBytes = $null
if (Test-Path -LiteralPath $exeStagePath) {
    $exeInfo = Get-Item -LiteralPath $exeStagePath
    $exeSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $exeStagePath).Hash
    $artifactSizeBytes = $exeInfo.Length
}

function Invoke-GitText {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Repository,

        [Parameter(Mandatory = $true)]
        [string[]]$GitArguments
    )

    if (-not (Test-Path -LiteralPath (Join-Path $Repository ".git"))) {
        return $null
    }

    try {
        $output = & git -C $Repository @GitArguments 2>$null
        if ($LASTEXITCODE -ne 0) {
            return $null
        }
        return ($output -join "`n").Trim()
    }
    catch {
        return $null
    }
}

$thirdPartyCommit = $null
$thirdPartyStatus = $null
if ($thirdPartyRepoPath) {
    $thirdPartyCommit = Invoke-GitText -Repository $thirdPartyRepoPath -GitArguments @("rev-parse", "HEAD")
    $thirdPartyStatus = Invoke-GitText -Repository $thirdPartyRepoPath -GitArguments @("status", "--porcelain")
}
$thirdPartyDirty = $null
if ($null -ne $thirdPartyStatus) {
    $thirdPartyDirty = -not [string]::IsNullOrWhiteSpace($thirdPartyStatus)
}
$bundlesThirdPartyRuntime = -not [string]::IsNullOrWhiteSpace($thirdPartyRepoPath)

$version = [ordered]@{
    schemaVersion = 1
    name = "EasyGet.DouyinSidecar"
    configuration = $Configuration
    runtime = $Runtime
    buildTimeUtc = (Get-Date).ToUniversalTime().ToString("o")
    mode = "pyinstaller-onefile-smoke-only"
    packagingMode = "pyinstaller-onefile-smoke-only"
    smokeOnly = $true
    runtimeRequiresExternalPython = (-not $bundlesThirdPartyRuntime)
    selfContainedRealDownload = $false
    artifactSizeBytes = $artifactSizeBytes
    licenseInventoryPath = "licenses/python-dependency-license-inventory.md"
    excludedOptionalFeatures = @(
        "playwright/browser extra",
        "server extra (fastapi/uvicorn/pydantic)",
        "whisper/transcribe extra",
        "dev/test/docs/img/venv/.git/Downloaded/config.yml/.cookies.json source and data trees"
    )
    importSelfTest = [ordered]@{
        command = "--self-test-imports"
        requiredForSelfContainedRealDownload = $true
        lastRunInBuild = $false
        notes = @(
            "The sidecar exposes this command to verify phase-one douyin-downloader-promax imports in the current runtime.",
            "The smoke-only build does not run it by default because the wrapper exe does not yet bundle third-party dependencies."
        )
    }
    notes = @(
        "This release skeleton validates --emit-sample smoke and asset placement only.",
        "Use --self-test-imports as an explicit gate before claiming bundled third-party imports are closed.",
        "Without -ThirdPartyRepoRoot, real downloads require external douyin-downloader-promax source/dependencies.",
        "With -ThirdPartyRepoRoot, this build includes a candidate bundled runtime graph, but real downloads remain unverified until import and download gates pass.",
        "A future sidecar-runtime task must close true self-contained real download support."
    )
    executable = [ordered]@{
        fileName = "EasyGet.DouyinSidecar.exe"
        sha256 = $exeSha256
        exists = (Test-Path -LiteralPath $exeStagePath)
    }
    build = [ordered]@{
        python = $Python
        pyinstallerVersion = $pyinstallerVersion
        spec = "tools/douyin-sidecar/EasyGet.DouyinSidecar.spec"
        source = "tools/douyin-sidecar/sidecar.py"
        pathex = @(
            "tools/douyin-sidecar",
            $thirdPartyRepoPath
        ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    }
    thirdParty = [ordered]@{
        name = "douyin-downloader-promax"
        repoPath = $thirdPartyRepoPath
        commit = $thirdPartyCommit
        dirty = $thirdPartyDirty
        bundled = $bundlesThirdPartyRuntime
        license = "licenses/douyin-downloader-promax-LICENSE.txt"
    }
}

$versionPath = Join-Path $stageDirFullPath "sidecar-version.json"
$version | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $versionPath -Encoding UTF8

Write-Host "[EasyGet] Douyin sidecar stage ready: $stageDirFullPath"
Write-Host "[EasyGet] Smoke-only skeleton: real self-contained downloads are not closed by this build."
