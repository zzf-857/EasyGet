[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [string]$Runtime = "win-x64",

    [bool]$SelfContained = $true,

    [switch]$SkipTests,

    [switch]$SkipZip,

    [string]$Version = "",

    [string]$OutputRoot = "artifacts\publish"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$projectPath = Join-Path $repoRoot "EasyGet.csproj"
$testProjectPath = Join-Path $repoRoot "EasyGet.Tests\EasyGet.Tests.csproj"

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
dotnet restore $projectPath
dotnet restore $testProjectPath

if (-not $SkipTests) {
    Write-Host "[EasyGet] Test"
    dotnet test $testProjectPath -c $Configuration --no-restore
}

Write-Host "[EasyGet] Publish $Configuration $Runtime"
$publishArgs = @(
    "publish",
    $projectPath,
    "-c", $Configuration,
    "-r", $Runtime,
    "--self-contained", $selfContainedValue,
    "-o", $publishDir,
    "/p:PublishSingleFile=false",
    "/p:IncludeNativeLibrariesForSelfExtract=true",
    "/p:DebugType=none",
    "/p:DebugSymbols=false"
)

if (-not [string]::IsNullOrWhiteSpace($Version)) {
    $publishArgs += "/p:Version=$Version"
    $publishArgs += "/p:FileVersion=$Version.0"
    $publishArgs += "/p:AssemblyVersion=$Version.0"
}

dotnet @publishArgs

Get-ChildItem -LiteralPath $publishDir -Filter "*.pdb" -Recurse -File -ErrorAction SilentlyContinue |
    Remove-Item -Force

$exePath = Join-Path $publishDir "EasyGet.exe"
if (-not (Test-Path $exePath)) {
    throw "Publish smoke check failed: EasyGet.exe was not found at $exePath"
}

$exeInfo = Get-Item $exePath
if ($exeInfo.Length -le 0) {
    throw "Publish smoke check failed: EasyGet.exe is empty."
}

Write-Host "[EasyGet] Smoke check passed: $exePath"

if (-not $SkipZip) {
    $zipPath = Join-Path (Split-Path $publishDir -Parent) "EasyGet-$Runtime-$Configuration.zip"
    if (Test-Path $zipPath) {
        Remove-Item -LiteralPath $zipPath -Force
    }

    Write-Host "[EasyGet] Zip $zipPath"
    Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $zipPath -Force
    Write-Host "[EasyGet] Zip ready: $zipPath"
}

Write-Host "[EasyGet] Publish ready: $publishDir"
