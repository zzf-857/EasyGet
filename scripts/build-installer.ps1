[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [string]$Runtime = "win-x64",

    [string]$Version = "",

    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$projectPath = Join-Path $repoRoot "EasyGet.csproj"
$publishScript = Join-Path $PSScriptRoot "publish-win-x64.ps1"
$innoScript = Join-Path $PSScriptRoot "EasyGet.iss"

[xml]$project = Get-Content -Raw -Encoding UTF8 -LiteralPath $projectPath
$projectVersion = $project.Project.PropertyGroup |
    ForEach-Object { $_.Version } |
    Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
    Select-Object -First 1

if ([string]::IsNullOrWhiteSpace($projectVersion)) {
    throw "Version is required. Define <Version> in EasyGet.csproj."
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = $projectVersion
}
elseif ($Version -ne $projectVersion) {
    throw "Version mismatch: -Version '$Version' does not match EasyGet.csproj <Version> '$projectVersion'. Update the project version before building a release."
}

Write-Host "[EasyGet] Build installer v$Version"

& $publishScript `
    -Configuration $Configuration `
    -Runtime $Runtime `
    -SelfContained $true `
    -SkipTests:$SkipTests `
    -Version $Version

function Find-InnoSetupCompiler {
    $candidates = @(
        (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
        (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
        (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe"),
        "ISCC.exe"
    )

    foreach ($candidate in $candidates) {
        if ($candidate -eq "ISCC.exe") {
            $command = Get-Command $candidate -ErrorAction SilentlyContinue
            if ($command) {
                return $command.Source
            }
            continue
        }

        if (Test-Path -LiteralPath $candidate) {
            return $candidate
        }
    }

    throw "Inno Setup compiler (ISCC.exe) was not found. Install Inno Setup 6 first."
}

$iscc = Find-InnoSetupCompiler
Write-Host "[EasyGet] ISCC: $iscc"

Push-Location $PSScriptRoot
try {
    & $iscc "/DMyAppVersion=$Version" $innoScript
}
finally {
    Pop-Location
}

$setupPath = Join-Path $repoRoot "artifacts\publish\$Configuration\EasyGet-Setup-v$Version.exe"
if (-not (Test-Path -LiteralPath $setupPath)) {
    throw "Installer build failed: $setupPath was not created."
}

$setupInfo = Get-Item -LiteralPath $setupPath
if ($setupInfo.Length -le 0) {
    throw "Installer build failed: $setupPath is empty."
}

$releaseDir = Join-Path $repoRoot "artifacts\publish\$Configuration"
$zipPath = Join-Path $releaseDir "EasyGet-$Runtime-$Configuration.zip"
if (-not (Test-Path -LiteralPath $zipPath)) {
    throw "Release zip was not found: $zipPath"
}

$zipInfo = Get-Item -LiteralPath $zipPath
if ($zipInfo.Length -le 0) {
    throw "Release zip is empty: $zipPath"
}

$manifest = [ordered]@{
    version = $Version
    tag = "v$Version"
    setupAsset = $setupInfo.Name
    setupSize = $setupInfo.Length
    zipAsset = $zipInfo.Name
    zipSize = $zipInfo.Length
    releaseUrl = "https://github.com/zzf-857/EasyGet/releases/tag/v$Version"
} | ConvertTo-Json

$manifestPath = Join-Path $releaseDir "easyget-update.json"
$manifest | Set-Content -Encoding UTF8 -LiteralPath $manifestPath

Write-Host "[EasyGet] Installer ready: $setupPath"
Write-Host "[EasyGet] Update manifest ready: $manifestPath"
