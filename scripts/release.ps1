[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$')]
    [string]$Version,

    [switch]$Publish,

    [switch]$DryRun,

    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$script:StepNumber = 0
$script:GitHubApiHeaders = @{
    Accept                 = "application/vnd.github+json"
    "X-GitHub-Api-Version" = "2026-03-10"
    "User-Agent"           = "EasyGet-release-script"
    "Cache-Control"        = "no-cache"
    Pragma                 = "no-cache"
}

function Write-Step {
    param([Parameter(Mandatory = $true)][string]$Message)

    $script:StepNumber++
    Write-Host ""
    Write-Host "[$($script:StepNumber)] $Message" -ForegroundColor Cyan
}

function Write-Pass {
    param([Parameter(Mandatory = $true)][string]$Message)

    Write-Host "  PASS  $Message" -ForegroundColor Green
}

function Assert-CommandAvailable {
    param([Parameter(Mandatory = $true)][string]$Name)

    if ($null -eq (Get-Command $Name -CommandType Application -ErrorAction SilentlyContinue)) {
        throw "Required command '$Name' was not found on PATH."
    }
}

function Invoke-NativeCommand {
    param(
        [Parameter(Mandatory = $true)][string]$Command,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$Description
    )

    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Description failed with exit code $LASTEXITCODE."
    }
}

function Invoke-NativeCommandCapture {
    param(
        [Parameter(Mandatory = $true)][string]$Command,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$Description
    )

    $output = @(& $Command @Arguments 2>&1)
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        $details = ($output | ForEach-Object { $_.ToString() }) -join [Environment]::NewLine
        if ([string]::IsNullOrWhiteSpace($details)) {
            throw "$Description failed with exit code $exitCode."
        }
        throw "$Description failed with exit code $exitCode.$([Environment]::NewLine)$details"
    }

    return @($output | ForEach-Object { $_.ToString() })
}

function Get-SingleLineCommandOutput {
    param(
        [Parameter(Mandatory = $true)][string]$Command,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$Description
    )

    $lines = @(Invoke-NativeCommandCapture -Command $Command -Arguments $Arguments -Description $Description)
    $value = ($lines -join [Environment]::NewLine).Trim()
    if ([string]::IsNullOrWhiteSpace($value)) {
        throw "$Description returned no value."
    }
    return $value
}

function Assert-CleanWorkingTree {
    $status = @(Invoke-NativeCommandCapture `
        -Command "git" `
        -Arguments @("status", "--porcelain", "--untracked-files=all") `
        -Description "Reading the Git working tree")
    if ($status.Count -gt 0) {
        $details = $status -join [Environment]::NewLine
        throw "The working tree must be clean before a release.$([Environment]::NewLine)$details"
    }
}

function ConvertTo-GitHubRepository {
    param([Parameter(Mandatory = $true)][string]$RemoteUrl)

    $path = $null
    if ($RemoteUrl -match '^https://github\.com/(.+)$') {
        $path = $Matches[1]
    }
    elseif ($RemoteUrl -match '^git@github\.com:(.+)$') {
        $path = $Matches[1]
    }
    elseif ($RemoteUrl -match '^ssh://git@github\.com/(.+)$') {
        $path = $Matches[1]
    }

    if ([string]::IsNullOrWhiteSpace($path)) {
        throw "The origin remote must point to GitHub; found '$RemoteUrl'."
    }

    $path = $path.TrimEnd('/')
    if ($path.EndsWith(".git", [System.StringComparison]::OrdinalIgnoreCase)) {
        $path = $path.Substring(0, $path.Length - 4)
    }
    if ($path -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') {
        throw "Could not derive a GitHub owner/repository name from origin '$RemoteUrl'."
    }

    return $path
}

function Test-GhAuthenticated {
    if ($null -eq (Get-Command "gh" -CommandType Application -ErrorAction SilentlyContinue)) {
        return $false
    }

    & gh auth status --hostname github.com *> $null
    return $LASTEXITCODE -eq 0
}

function Assert-GitHubReleaseDoesNotExist {
    param(
        [Parameter(Mandatory = $true)][string]$Repository,
        [Parameter(Mandatory = $true)][string]$Tag,
        [Parameter(Mandatory = $true)][bool]$GhAuthenticated
    )

    if (-not $GhAuthenticated) {
        throw "An authenticated GitHub CLI is required to check public and draft releases. Run 'gh auth login' and retry."
    }

    $releasePagesJson = @(Invoke-NativeCommandCapture `
        -Command "gh" `
        -Arguments @(
            "api",
            "--paginate",
            "--slurp",
            "repos/$Repository/releases?per_page=100"
        ) `
        -Description "Listing all public and draft GitHub Releases") -join [Environment]::NewLine
    $releasePages = @($releasePagesJson | ConvertFrom-Json)
    foreach ($page in $releasePages) {
        foreach ($release in @($page)) {
            if ($release.tag_name -eq $Tag -or $release.name -eq $Tag) {
                $state = if ($release.draft) { "draft" } else { "published" }
                throw "GitHub Release '$Tag' already exists as $state release '$($release.html_url)'. Published versions are immutable; choose a new version."
            }
        }
    }
}

function Assert-GitHubImmutableReleasesEnabled {
    param([Parameter(Mandatory = $true)][string]$Repository)

    $settingsJson = @(Invoke-NativeCommandCapture `
        -Command "gh" `
        -Arguments @(
            "api",
            "-H", "Accept: application/vnd.github+json",
            "-H", "X-GitHub-Api-Version: 2026-03-10",
            "repos/$Repository/immutable-releases"
        ) `
        -Description "Checking GitHub immutable releases") -join [Environment]::NewLine
    $settings = $settingsJson | ConvertFrom-Json
    if ($settings.enabled -ne $true) {
        throw "GitHub immutable releases are disabled for '$Repository'. Enable the repository protection before publishing."
    }
}

function Assert-ReleaseWorkflowActive {
    param([Parameter(Mandatory = $true)][string]$Repository)

    $workflowJson = @(Invoke-NativeCommandCapture `
        -Command "gh" `
        -Arguments @(
            "workflow", "list",
            "--repo", $Repository,
            "--all",
            "--json", "id,name,path,state"
        ) `
        -Description "Checking the release workflow") -join [Environment]::NewLine
    $workflows = @($workflowJson | ConvertFrom-Json)
    $releaseWorkflow = $workflows |
        Where-Object { $_.path -eq ".github/workflows/release.yml" } |
        Select-Object -First 1
    if ($null -eq $releaseWorkflow) {
        throw "GitHub has not registered .github/workflows/release.yml. Refusing to consume a release version."
    }
    if ($releaseWorkflow.state -ne "active") {
        throw "GitHub workflow .github/workflows/release.yml is '$($releaseWorkflow.state)', not active."
    }
}

function New-PreservedXmlDocument {
    param([Parameter(Mandatory = $true)][string]$Path)

    $document = [System.Xml.XmlDocument]::new()
    $document.PreserveWhitespace = $true
    $document.Load($Path)
    return $document
}

function Get-ProjectVersion {
    param([Parameter(Mandatory = $true)][string]$Path)

    $document = New-PreservedXmlDocument -Path $Path
    $versionNodes = @($document.SelectNodes("/Project/PropertyGroup/Version"))
    if ($versionNodes.Count -ne 1) {
        throw "EasyGet.csproj must contain exactly one /Project/PropertyGroup/Version element; found $($versionNodes.Count)."
    }

    return $versionNodes[0].InnerText.Trim()
}

function Set-ProjectVersionPreservingWhitespace {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$NewVersion
    )

    $originalBytes = [System.IO.File]::ReadAllBytes($Path)
    $hasUtf8Bom = $originalBytes.Length -ge 3 -and
        $originalBytes[0] -eq 0xEF -and
        $originalBytes[1] -eq 0xBB -and
        $originalBytes[2] -eq 0xBF

    $document = New-PreservedXmlDocument -Path $Path
    $versionNodes = @($document.SelectNodes("/Project/PropertyGroup/Version"))
    if ($versionNodes.Count -ne 1) {
        throw "EasyGet.csproj must contain exactly one /Project/PropertyGroup/Version element; found $($versionNodes.Count)."
    }
    $versionNodes[0].InnerText = $NewVersion

    $temporaryPath = Join-Path `
        ([System.IO.Path]::GetDirectoryName($Path)) `
        (".{0}.release-{1}.tmp" -f [System.IO.Path]::GetFileName($Path), $PID)
    $encoding = [System.Text.UTF8Encoding]::new($hasUtf8Bom)
    $settings = [System.Xml.XmlWriterSettings]::new()
    $settings.Encoding = $encoding
    $settings.Indent = $false
    $settings.NewLineHandling = [System.Xml.NewLineHandling]::None
    $settings.OmitXmlDeclaration = $document.FirstChild.NodeType -ne [System.Xml.XmlNodeType]::XmlDeclaration

    $writer = $null
    try {
        $writer = [System.Xml.XmlWriter]::Create($temporaryPath, $settings)
        $document.Save($writer)
        $writer.Dispose()
        $writer = $null

        Move-Item -LiteralPath $temporaryPath -Destination $Path -Force
    }
    finally {
        if ($null -ne $writer) {
            $writer.Dispose()
        }
        if (Test-Path -LiteralPath $temporaryPath) {
            Remove-Item -LiteralPath $temporaryPath -Force
        }
    }
}

function Get-ChangelogReleaseNotes {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$ReleaseVersion
    )

    $lines = [System.IO.File]::ReadAllLines($Path)
    $escapedVersion = [System.Text.RegularExpressions.Regex]::Escape($ReleaseVersion)
    $headingPattern = "^##[ ]+$escapedVersion[ ]+-[ ]+[0-9]{4}-[0-9]{2}-[0-9]{2}[ ]*$"
    $matchingIndexes = @()
    for ($index = 0; $index -lt $lines.Length; $index++) {
        if ($lines[$index] -match $headingPattern) {
            $matchingIndexes += $index
        }
    }

    if ($matchingIndexes.Count -ne 1) {
        throw "CHANGELOG.md must contain exactly one '## $ReleaseVersion - YYYY-MM-DD' section; found $($matchingIndexes.Count)."
    }

    $startIndex = $matchingIndexes[0]
    $firstVersionHeadingIndex = -1
    for ($index = 0; $index -lt $lines.Length; $index++) {
        if ($lines[$index] -match '^##[ ]+') {
            $firstVersionHeadingIndex = $index
            break
        }
    }
    if ($startIndex -ne $firstVersionHeadingIndex) {
        throw "CHANGELOG.md section '$ReleaseVersion' must be the newest version section at the top of the changelog."
    }
    $endIndex = $lines.Length
    for ($index = $startIndex + 1; $index -lt $lines.Length; $index++) {
        if ($lines[$index] -match '^##[ ]+') {
            $endIndex = $index
            break
        }
    }

    $sectionLines = @($lines[$startIndex..($endIndex - 1)])
    $body = ($sectionLines | Select-Object -Skip 1) -join [Environment]::NewLine
    if ([string]::IsNullOrWhiteSpace($body)) {
        throw "CHANGELOG.md section '$ReleaseVersion' is empty. Add user-facing release notes before releasing."
    }

    return ($sectionLines -join [Environment]::NewLine).TrimEnd() + [Environment]::NewLine
}

function ConvertTo-StrictVersion {
    param(
        [Parameter(Mandatory = $true)][string]$Value,
        [Parameter(Mandatory = $true)][string]$Description
    )

    if ($Value -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') {
        throw "$Description '$Value' must use strict X.Y.Z format."
    }

    try {
        return [System.Version]::Parse($Value)
    }
    catch {
        throw "$Description '$Value' is outside the supported numeric range."
    }
}

function Get-VersionTags {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][string[]]$LocalTagLines,
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][string[]]$RemoteTagLines
    )

    $tagNames = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    foreach ($line in $LocalTagLines) {
        $candidate = $line.Trim()
        if (-not [string]::IsNullOrWhiteSpace($candidate)) {
            [void]$tagNames.Add($candidate)
        }
    }
    foreach ($line in $RemoteTagLines) {
        if ($line -match 'refs/tags/(?<tag>[^\s]+)$') {
            [void]$tagNames.Add($Matches['tag'])
        }
    }

    $versionTags = @()
    foreach ($tagName in $tagNames) {
        if ($tagName -match '^v(?<version>(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*))$') {
            $versionTags += [pscustomobject]@{
                Tag     = $tagName
                Version = ConvertTo-StrictVersion -Value $Matches['version'] -Description "Tag version"
            }
        }
    }

    return @($versionTags)
}

function Wait-ForReleaseWorkflow {
    param(
        [Parameter(Mandatory = $true)][string]$Repository,
        [Parameter(Mandatory = $true)][string]$Tag,
        [Parameter(Mandatory = $true)][string]$Commit
    )

    $maxDiscoveryAttempts = 60
    $run = $null
    for ($attempt = 1; $attempt -le $maxDiscoveryAttempts; $attempt++) {
        $runJsonLines = @(Invoke-NativeCommandCapture `
            -Command "gh" `
            -Arguments @(
                "run", "list",
                "--repo", $Repository,
                "--workflow", "release.yml",
                "--event", "push",
                "--commit", $Commit,
                "--limit", "20",
                "--json", "databaseId,headBranch,headSha,status,conclusion,url,workflowName,event"
            ) `
            -Description "Discovering the release.yml workflow run")
        $runJson = $runJsonLines -join [Environment]::NewLine
        $runs = @($runJson | ConvertFrom-Json)
        $run = $runs |
            Where-Object {
                $_.headSha -eq $Commit -and
                $_.headBranch -eq $Tag -and
                $_.event -eq "push"
            } |
            Sort-Object databaseId -Descending |
            Select-Object -First 1

        if ($null -ne $run) {
            break
        }

        if ($attempt -lt $maxDiscoveryAttempts) {
            Write-Host "  Waiting for release.yml run ($attempt/$maxDiscoveryAttempts)..."
            Start-Sleep -Seconds 5
        }
    }

    if ($null -eq $run) {
        throw "No release.yml run for tag '$Tag' and commit '$Commit' appeared within five minutes. The tag remains immutable; diagnose the trigger and release a new version if needed."
    }

    Write-Host "  Workflow: $($run.url)"
    Invoke-NativeCommand `
        -Command "gh" `
        -Arguments @("run", "watch", $run.databaseId.ToString(), "--repo", $Repository, "--interval", "10", "--exit-status") `
        -Description "Watching release.yml run $($run.databaseId)"

    return $run
}

function Assert-PublicLatestRelease {
    param(
        [Parameter(Mandatory = $true)][string]$Repository,
        [Parameter(Mandatory = $true)][string]$Tag,
        [Parameter(Mandatory = $true)][string]$ReleaseVersion
    )

    $expectedSetupAsset = "EasyGet-Setup-v$ReleaseVersion.exe"
    $expectedZipAsset = "EasyGet-win-x64-Release.zip"
    $expectedSbomAsset = "EasyGet-v$ReleaseVersion.spdx.json"
    $requiredAssets = @(
        $expectedSetupAsset,
        $expectedZipAsset,
        "easyget-update.json",
        $expectedSbomAsset
    )
    $maxAttempts = 24

    for ($attempt = 1; $attempt -le $maxAttempts; $attempt++) {
        $cacheBuster = "{0}-{1}" -f [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds(), $attempt
        try {
            $latestReleaseJson = @(Invoke-NativeCommandCapture `
                -Command "gh" `
                -Arguments @("api", "repos/$Repository/releases/latest?cache-buster=$cacheBuster") `
                -Description "Reading the latest GitHub Release") -join [Environment]::NewLine
            $latestRelease = $latestReleaseJson | ConvertFrom-Json
            if ($latestRelease.draft -or $latestRelease.prerelease -or $latestRelease.tag_name -ne $Tag) {
                throw "Public latest release is '$($latestRelease.tag_name)' instead of non-draft '$Tag'."
            }
            if ($latestRelease.immutable -ne $true) {
                throw "Public latest release '$Tag' is not immutable."
            }

            $assetNames = @($latestRelease.assets | ForEach-Object { $_.name })
            foreach ($requiredAsset in $requiredAssets) {
                if ($requiredAsset -notin $assetNames) {
                    throw "Public latest release '$Tag' is missing asset '$requiredAsset'."
                }
            }

            $manifestUri = "https://github.com/$Repository/releases/latest/download/easyget-update.json?cache-buster=$cacheBuster"
            $manifestResponse = Invoke-WebRequest `
                -Method Get `
                -Uri $manifestUri `
                -Headers $script:GitHubApiHeaders `
                -TimeoutSec 30 `
                -UseBasicParsing `
                -ErrorAction Stop
            $manifestJson = if ($manifestResponse.Content -is [byte[]]) {
                [System.Text.Encoding]::UTF8.GetString($manifestResponse.Content)
            }
            else {
                [string]$manifestResponse.Content
            }
            $manifest = $manifestJson | ConvertFrom-Json
            if ($manifest.version -ne $ReleaseVersion) {
                throw "Public latest manifest version is '$($manifest.version)' instead of '$ReleaseVersion'."
            }
            if ($manifest.tag -ne $Tag) {
                throw "Public latest manifest tag is '$($manifest.tag)' instead of '$Tag'."
            }
            if ($manifest.setupAsset -ne $expectedSetupAsset -or $manifest.zipAsset -ne $expectedZipAsset) {
                throw "Public latest manifest asset names do not match release '$Tag'."
            }
            if ([long]$manifest.setupSize -le 0 -or [long]$manifest.zipSize -le 0) {
                throw "Public latest manifest contains an invalid asset size."
            }
            if ($manifest.setupSha256 -notmatch '^[A-Fa-f0-9]{64}$' -or
                $manifest.zipSha256 -notmatch '^[A-Fa-f0-9]{64}$') {
                throw "Public latest manifest contains an invalid SHA-256 value."
            }

            $setupReleaseAsset = $latestRelease.assets |
                Where-Object { $_.name -eq $expectedSetupAsset } |
                Select-Object -First 1
            $zipReleaseAsset = $latestRelease.assets |
                Where-Object { $_.name -eq $expectedZipAsset } |
                Select-Object -First 1
            if ([long]$manifest.setupSize -ne [long]$setupReleaseAsset.size -or
                [long]$manifest.zipSize -ne [long]$zipReleaseAsset.size) {
                throw "Public latest manifest sizes do not match the GitHub Release assets."
            }

            $setupDigest = [string]$setupReleaseAsset.digest
            $zipDigest = [string]$zipReleaseAsset.digest
            if ([string]::IsNullOrWhiteSpace($setupDigest) -or
                [string]::IsNullOrWhiteSpace($zipDigest)) {
                throw "GitHub Release assets do not expose SHA-256 digests for verification."
            }
            if (-not $setupDigest.Equals("sha256:$($manifest.setupSha256)", [System.StringComparison]::OrdinalIgnoreCase) -or
                -not $zipDigest.Equals("sha256:$($manifest.zipSha256)", [System.StringComparison]::OrdinalIgnoreCase)) {
                throw "Public latest manifest SHA-256 values do not match the GitHub Release asset digests."
            }

            $clientManifestUri = "https://github.com/$Repository/releases/latest/download/easyget-update.json"
            $clientManifestResponse = Invoke-WebRequest `
                -Method Get `
                -Uri $clientManifestUri `
                -Headers $script:GitHubApiHeaders `
                -TimeoutSec 30 `
                -UseBasicParsing `
                -ErrorAction Stop
            $clientManifestJson = if ($clientManifestResponse.Content -is [byte[]]) {
                [System.Text.Encoding]::UTF8.GetString($clientManifestResponse.Content)
            }
            else {
                [string]$clientManifestResponse.Content
            }
            $clientManifest = $clientManifestJson | ConvertFrom-Json
            if ($clientManifest.version -ne $ReleaseVersion -or $clientManifest.tag -ne $Tag) {
                throw "The exact manifest URL used by installed clients still reports '$($clientManifest.tag)' / '$($clientManifest.version)' instead of '$Tag' / '$ReleaseVersion'."
            }

            Write-Pass "GitHub Release is public, non-draft, latest, and the exact client manifest URL exposes the new version."
            return
        }
        catch {
            if ($attempt -eq $maxAttempts) {
                throw "Public latest release verification failed after $maxAttempts attempts: $($_.Exception.Message)"
            }
            Write-Warning "Public release is not consistent yet (attempt $attempt/$maxAttempts): $($_.Exception.Message)"
            Start-Sleep -Seconds 10
        }
    }
}

if ($Publish -and $DryRun) {
    throw "-Publish and -DryRun are mutually exclusive. Omit both for the default validation-only mode."
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$projectPath = Join-Path $repoRoot "EasyGet.csproj"
$testProjectPath = Join-Path $repoRoot "EasyGet.Tests\EasyGet.Tests.csproj"
$changelogPath = Join-Path $repoRoot "CHANGELOG.md"
$tag = "v$Version"
$targetVersion = ConvertTo-StrictVersion -Value $Version -Description "Target version"
$mode = if ($Publish) { "PUBLISH" } else { "VALIDATION ONLY (no release state changes)" }

Write-Host "EasyGet release orchestrator" -ForegroundColor White
Write-Host "  Mode:    $mode"
Write-Host "  Version: $Version"
Write-Host "  Tag:     $tag"

Push-Location $repoRoot
try {
    Write-Step "Validate tools, branch, and clean working tree"
    Assert-CommandAvailable -Name "git"
    Assert-CommandAvailable -Name "dotnet"
    Assert-CommandAvailable -Name "gh"

    $branch = Get-SingleLineCommandOutput `
        -Command "git" `
        -Arguments @("branch", "--show-current") `
        -Description "Reading the current branch"
    if ($branch -ne "main") {
        throw "Releases must run from branch 'main'; current branch is '$branch'."
    }
    Assert-CleanWorkingTree
    Write-Pass "Current branch is main and the working tree is clean."

    Write-Step "Fetch and require exact synchronization with origin/main"
    Invoke-NativeCommand `
        -Command "git" `
        -Arguments @("fetch", "--no-tags", "origin", "refs/heads/main:refs/remotes/origin/main") `
        -Description "Fetching origin/main"
    $headCommit = Get-SingleLineCommandOutput `
        -Command "git" `
        -Arguments @("rev-parse", "HEAD") `
        -Description "Reading HEAD"
    $originMainCommit = Get-SingleLineCommandOutput `
        -Command "git" `
        -Arguments @("rev-parse", "refs/remotes/origin/main") `
        -Description "Reading origin/main"
    if (-not $headCommit.Equals($originMainCommit, [System.StringComparison]::OrdinalIgnoreCase)) {
        $counts = @(Invoke-NativeCommandCapture `
            -Command "git" `
            -Arguments @("rev-list", "--left-right", "--count", "HEAD...refs/remotes/origin/main") `
            -Description "Comparing main with origin/main") -join " "
        throw "Local main must exactly match origin/main after fetch (HEAD=$headCommit, origin/main=$originMainCommit, ahead/behind=$counts)."
    }
    Write-Pass "HEAD equals origin/main at $headCommit."

    Write-Step "Validate project version, changelog, tags, and GitHub Release identity"
    if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
        throw "Project file was not found: $projectPath"
    }
    if (-not (Test-Path -LiteralPath $changelogPath -PathType Leaf)) {
        throw "Changelog was not found: $changelogPath"
    }

    $currentVersionText = Get-ProjectVersion -Path $projectPath
    $currentVersion = ConvertTo-StrictVersion -Value $currentVersionText -Description "EasyGet.csproj version"
    if ($targetVersion -le $currentVersion) {
        throw "Target version '$Version' must be greater than EasyGet.csproj version '$currentVersionText'."
    }
    $releaseNotes = Get-ChangelogReleaseNotes -Path $changelogPath -ReleaseVersion $Version

    $localTagLines = @(Invoke-NativeCommandCapture `
        -Command "git" `
        -Arguments @("tag", "--list", "v*") `
        -Description "Listing local version tags")
    $remoteTagLines = @(Invoke-NativeCommandCapture `
        -Command "git" `
        -Arguments @("ls-remote", "--tags", "--refs", "origin", "refs/tags/v*") `
        -Description "Listing remote version tags")
    $versionTags = @(Get-VersionTags -LocalTagLines $localTagLines -RemoteTagLines $remoteTagLines)
    if (@($versionTags | Where-Object { $_.Tag -eq $tag }).Count -gt 0) {
        throw "Tag '$tag' already exists locally or on origin. Tags are immutable; choose a new version."
    }
    if ($versionTags.Count -gt 0) {
        $latestTag = $versionTags | Sort-Object Version -Descending | Select-Object -First 1
        if ($currentVersion.CompareTo($latestTag.Version) -ne 0) {
            throw "EasyGet.csproj version '$currentVersionText' must equal latest version tag '$($latestTag.Tag)' before the release script applies the next version."
        }
        if ($targetVersion -le $latestTag.Version) {
            throw "Target version '$Version' must be greater than latest version tag '$($latestTag.Tag)'."
        }
        Write-Pass "Target version is greater than latest version tag $($latestTag.Tag)."
    }
    else {
        Write-Pass "No existing strict version tags were found."
    }

    $ghAuthenticated = Test-GhAuthenticated
    if (-not $ghAuthenticated) {
        throw "Release validation requires an authenticated GitHub CLI. Run 'gh auth login' and retry."
    }

    $originUrl = Get-SingleLineCommandOutput `
        -Command "git" `
        -Arguments @("remote", "get-url", "origin") `
        -Description "Reading the origin URL"
    $repository = ConvertTo-GitHubRepository -RemoteUrl $originUrl
    $expectedRepository = "zzf-857/EasyGet"
    if (-not $repository.Equals($expectedRepository, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "EasyGet releases must target '$expectedRepository'; origin resolves to '$repository'."
    }
    Assert-GitHubImmutableReleasesEnabled -Repository $repository
    Assert-ReleaseWorkflowActive -Repository $repository
    Assert-GitHubReleaseDoesNotExist `
        -Repository $repository `
        -Tag $tag `
        -GhAuthenticated $ghAuthenticated
    Write-Pass "Version, changelog section, tag, and GitHub Release identity are available."
    Write-Pass "GitHub immutable releases are enabled for $repository."
    Write-Pass "GitHub has registered the active release.yml workflow."

    Write-Step "Run Release tests"
    if ($SkipTests) {
        Write-Warning "Release tests were skipped explicitly. Use -SkipTests only with recorded evidence for this exact release candidate."
    }
    else {
        Invoke-NativeCommand `
            -Command "dotnet" `
            -Arguments @("test", $testProjectPath, "-c", "Release") `
            -Description "dotnet test EasyGet.Tests/EasyGet.Tests.csproj -c Release"
        Write-Pass "Release tests passed."
    }

    # Tests must not be able to leave tracked or untracked release inputs behind.
    Assert-CleanWorkingTree

    if (-not $Publish) {
        Write-Host ""
        Write-Host "VALIDATION PASSED for $tag" -ForegroundColor Green
        Write-Host "No project version, commit, tag, remote ref, workflow, or GitHub Release was changed."
        Write-Host "Publish only after explicit approval:"
        Write-Host "  .\scripts\release.ps1 -Version $Version -Publish"
        return
    }

    Write-Step "Update EasyGet.csproj and create the release commit"
    Set-ProjectVersionPreservingWhitespace -Path $projectPath -NewVersion $Version
    $updatedVersion = Get-ProjectVersion -Path $projectPath
    if ($updatedVersion -ne $Version) {
        throw "Structured XML version update failed: expected '$Version', found '$updatedVersion'."
    }

    Invoke-NativeCommand `
        -Command "git" `
        -Arguments @("add", "--", "EasyGet.csproj") `
        -Description "Staging EasyGet.csproj"
    $stagedPaths = @(Invoke-NativeCommandCapture `
        -Command "git" `
        -Arguments @("diff", "--cached", "--name-only") `
        -Description "Inspecting staged release files")
    if ($stagedPaths.Count -ne 1 -or $stagedPaths[0].Trim() -ne "EasyGet.csproj") {
        throw "The release commit may contain only EasyGet.csproj; staged files: $($stagedPaths -join ', ')."
    }
    $stagedNumstat = @(Invoke-NativeCommandCapture `
        -Command "git" `
        -Arguments @("diff", "--cached", "--numstat", "--", "EasyGet.csproj") `
        -Description "Checking the structured project version diff") -join ""
    if ($stagedNumstat -notmatch '^1\s+1\s+EasyGet\.csproj$') {
        throw "The release version edit must change exactly one line in EasyGet.csproj; git numstat was '$stagedNumstat'."
    }
    Invoke-NativeCommand `
        -Command "git" `
        -Arguments @("diff", "--cached", "--check") `
        -Description "Checking the staged release diff"

    Invoke-NativeCommand `
        -Command "git" `
        -Arguments @("commit", "-m", "chore: release $tag") `
        -Description "Creating release commit"
    $releaseCommit = Get-SingleLineCommandOutput `
        -Command "git" `
        -Arguments @("rev-parse", "HEAD") `
        -Description "Reading the release commit"
    Write-Pass "Created release commit $releaseCommit."

    Write-Step "Create a new annotated tag from CHANGELOG release notes"
    $releaseNotesPath = [System.IO.Path]::GetTempFileName()
    try {
        [System.IO.File]::WriteAllText(
            $releaseNotesPath,
            $releaseNotes,
            [System.Text.UTF8Encoding]::new($false))
        Invoke-NativeCommand `
            -Command "git" `
            -Arguments @("tag", "-a", $tag, "-F", $releaseNotesPath) `
            -Description "Creating annotated tag $tag"
    }
    finally {
        if (Test-Path -LiteralPath $releaseNotesPath) {
            Remove-Item -LiteralPath $releaseNotesPath -Force
        }
    }

    $tagCommit = Get-SingleLineCommandOutput `
        -Command "git" `
        -Arguments @("rev-list", "-n", "1", $tag) `
        -Description "Verifying annotated tag $tag"
    if (-not $tagCommit.Equals($releaseCommit, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Annotated tag '$tag' does not resolve to release commit '$releaseCommit'."
    }
    Write-Pass "Created annotated tag $tag; GitHub will lock it when the immutable Release is published."

    Write-Step "Atomically push main and the new tag"
    Invoke-NativeCommand `
        -Command "git" `
        -Arguments @("push", "--atomic", "origin", "main", $tag) `
        -Description "Atomic push of main and $tag"
    Write-Pass "Atomic push completed; main and $tag were accepted together."

    Write-Step "Discover and watch the matching release.yml workflow"
    $workflowRun = Wait-ForReleaseWorkflow `
        -Repository $repository `
        -Tag $tag `
        -Commit $releaseCommit
    Write-Pass "release.yml run $($workflowRun.databaseId) completed successfully."

    Write-Step "Verify the public latest release and cache-busted update manifest"
    Assert-PublicLatestRelease `
        -Repository $repository `
        -Tag $tag `
        -ReleaseVersion $Version

    Write-Host ""
    Write-Host "RELEASE COMPLETE: $tag" -ForegroundColor Green
    Write-Host "  Commit:   $releaseCommit"
    Write-Host "  Workflow: $($workflowRun.url)"
    Write-Host "  Release:  https://github.com/$repository/releases/tag/$tag"
}
finally {
    Pop-Location
}
