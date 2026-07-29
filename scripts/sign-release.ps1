[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern("^\d+\.\d+\.\d+$")]
    [string]$Version,

    [Parameter(Mandatory = $true)]
    [string]$CertificatePath,

    [ValidateSet("Release")]
    [string]$Configuration = "Release",

    [ValidateSet("win-x64")]
    [string]$Runtime = "win-x64",

    [string]$CertificatePasswordEnvironmentVariable = "WINDOWS_CODE_SIGNING_CERTIFICATE_PASSWORD",

    [string]$TimestampUrl = "http://timestamp.digicert.com"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$releaseDir = Join-Path $repoRoot "artifacts\publish\$Configuration"
$publishDir = Join-Path $releaseDir $Runtime
$innoScript = Join-Path $PSScriptRoot "EasyGet.iss"
$zipPath = Join-Path $releaseDir "EasyGet-$Runtime-$Configuration.zip"
$setupPath = Join-Path $releaseDir "EasyGet-Setup-v$Version.exe"
$manifestPath = Join-Path $releaseDir "easyget-update.json"

function Assert-NativeCommandSucceeded {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Description
    )

    if ($LASTEXITCODE -ne 0) {
        throw "$Description failed with exit code $LASTEXITCODE."
    }
}

function Find-SignTool {
    $command = Get-Command "signtool.exe" -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    $windowsKitsRoot = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin"
    if (Test-Path -LiteralPath $windowsKitsRoot -PathType Container) {
        $candidate = Get-ChildItem -LiteralPath $windowsKitsRoot -Filter "signtool.exe" -File -Recurse |
            Where-Object { $_.FullName -match '[\\/]x64[\\/]signtool\.exe$' } |
            Sort-Object FullName -Descending |
            Select-Object -First 1
        if ($candidate) {
            return $candidate.FullName
        }
    }

    throw "Windows SDK SignTool (signtool.exe) was not found."
}

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

        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return $candidate
        }
    }

    throw "Inno Setup compiler (ISCC.exe) was not found."
}

function Invoke-CodeSign {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,

        [Parameter(Mandatory = $true)]
        [string]$SignToolPath,

        [Parameter(Mandatory = $true)]
        [string]$CertificatePassword
    )

    if (-not (Test-Path -LiteralPath $FilePath -PathType Leaf)) {
        throw "Code-signing target was not found: $FilePath"
    }

    & $SignToolPath sign `
        /fd SHA256 `
        /td SHA256 `
        /tr $TimestampUrl `
        /f $CertificatePath `
        /p $CertificatePassword `
        /d "EasyGet" `
        /v `
        $FilePath
    Assert-NativeCommandSucceeded "Authenticode signing for $([System.IO.Path]::GetFileName($FilePath))"

    & $SignToolPath verify /pa /all /v $FilePath
    Assert-NativeCommandSucceeded "Authenticode verification for $([System.IO.Path]::GetFileName($FilePath))"
}

if (-not (Test-Path -LiteralPath $CertificatePath -PathType Leaf)) {
    throw "Code-signing certificate was not found: $CertificatePath"
}

if (-not [System.Uri]::IsWellFormedUriString($TimestampUrl, [System.UriKind]::Absolute)) {
    throw "Timestamp URL must be an absolute URI."
}

$certificatePassword = [System.Environment]::GetEnvironmentVariable($CertificatePasswordEnvironmentVariable)
if ([string]::IsNullOrWhiteSpace($certificatePassword)) {
    throw "Code-signing certificate password environment variable is missing or empty: $CertificatePasswordEnvironmentVariable"
}

$requiredPaths = @($publishDir, $innoScript, $zipPath, $setupPath, $manifestPath)
foreach ($requiredPath in $requiredPaths) {
    if (-not (Test-Path -LiteralPath $requiredPath)) {
        throw "Release signing input was not found: $requiredPath"
    }
}

$signTool = Find-SignTool
$iscc = Find-InnoSetupCompiler
$firstPartyExecutables = @(
    @(
        (Join-Path $publishDir "EasyGet.exe"),
        (Join-Path $publishDir "sidecars\douyin\EasyGet.DouyinSidecar.exe")
    ) | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf }
)

if ($firstPartyExecutables.Count -eq 0 -or $firstPartyExecutables[0] -ne (Join-Path $publishDir "EasyGet.exe")) {
    throw "EasyGet.exe was not found in the publish directory."
}

try {
    foreach ($executable in $firstPartyExecutables) {
        Invoke-CodeSign -FilePath $executable -SignToolPath $signTool -CertificatePassword $certificatePassword
    }

    $temporaryZipPath = Join-Path $releaseDir "EasyGet-$Runtime-$Configuration.signing.zip"
    if (Test-Path -LiteralPath $temporaryZipPath) {
        Remove-Item -LiteralPath $temporaryZipPath -Force
    }

    Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $temporaryZipPath -Force
    if (-not (Test-Path -LiteralPath $temporaryZipPath -PathType Leaf) -or (Get-Item -LiteralPath $temporaryZipPath).Length -le 0) {
        throw "Rebuilding the signed portable zip failed."
    }
    Move-Item -LiteralPath $temporaryZipPath -Destination $zipPath -Force

    if (Test-Path -LiteralPath $setupPath) {
        Remove-Item -LiteralPath $setupPath -Force
    }

    Push-Location $PSScriptRoot
    try {
        & $iscc "/DMyAppVersion=$Version" $innoScript
        Assert-NativeCommandSucceeded "Signed installer rebuild"
    }
    finally {
        Pop-Location
    }

    Invoke-CodeSign -FilePath $setupPath -SignToolPath $signTool -CertificatePassword $certificatePassword

    $setupInfo = Get-Item -LiteralPath $setupPath
    $zipInfo = Get-Item -LiteralPath $zipPath
    $manifest = Get-Content -Raw -Encoding UTF8 -LiteralPath $manifestPath | ConvertFrom-Json
    if ($manifest.version -ne $Version -or $manifest.tag -ne "v$Version") {
        throw "Update manifest identity does not match release v$Version."
    }
    if ($manifest.setupAsset -ne $setupInfo.Name -or $manifest.zipAsset -ne $zipInfo.Name) {
        throw "Update manifest asset names do not match the signed release artifacts."
    }

    $manifest.setupSize = $setupInfo.Length
    $manifest.setupSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $setupPath).Hash
    $manifest.zipSize = $zipInfo.Length
    $manifest.zipSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $zipPath).Hash

    $temporaryManifestPath = "$manifestPath.signing"
    $manifest | ConvertTo-Json -Depth 10 | Set-Content -Encoding UTF8 -LiteralPath $temporaryManifestPath
    Move-Item -LiteralPath $temporaryManifestPath -Destination $manifestPath -Force

    Write-Host "[EasyGet] Authenticode signatures verified and release manifest hashes refreshed."
}
finally {
    $certificatePassword = $null
}
