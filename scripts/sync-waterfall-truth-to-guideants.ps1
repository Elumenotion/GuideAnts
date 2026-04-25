[CmdletBinding()]
param(
    [Parameter()]
    [string]$SourceRoot = "D:\Elumenotion\repos\waterfall-major-refactor",

    [Parameter()]
    [string]$TargetRoot = "D:\repos\GuideAnts",

    [Parameter()]
    [bool]$IncludeMissing = $true,

    [Parameter()]
    [switch]$DryRun,

    [Parameter()]
    [switch]$CompareContent,

    [Parameter()]
    [bool]$UseGitTrackedFiles = $true,

    [Parameter()]
    [string[]]$ExcludeRelativePaths = @()
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Resolve-NormalizedPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    return (Resolve-Path -LiteralPath $Path).ProviderPath.TrimEnd('\')
}

function Test-IsGitRepo {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepoPath
    )

    try {
        $null = git -C $RepoPath rev-parse --show-toplevel 2>$null
        return $LASTEXITCODE -eq 0
    }
    catch {
        return $false
    }
}

function Get-TrackedRelativePaths {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepoPath
    )

    $tracked = git -C $RepoPath ls-files
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to enumerate tracked files under $RepoPath"
    }

    return $tracked |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        ForEach-Object { $_.Replace('/', '\') }
}

function Get-RelativePathsByRecursion {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RootPath
    )

    Get-ChildItem -LiteralPath $RootPath -Recurse -File |
        ForEach-Object {
            $_.FullName.Substring($RootPath.Length).TrimStart('\')
        }
}

function Test-IsExcludedRelativePath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RelativePath
    )

    foreach ($excludedPath in $ExcludeRelativePaths) {
        if ([string]::IsNullOrWhiteSpace($excludedPath)) {
            continue
        }

        $normalizedExcludedPath = $excludedPath.Trim().Replace('/', '\').TrimStart('\')
        if ($RelativePath.Equals($normalizedExcludedPath, [System.StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }

        $prefix = $normalizedExcludedPath.TrimEnd('\') + '\'
        if ($RelativePath.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }

    return $false
}

function Get-FileHashOrNull {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return $null
    }

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

$resolvedSourceRoot = Resolve-NormalizedPath -Path $SourceRoot
$resolvedTargetRoot = Resolve-NormalizedPath -Path $TargetRoot

$canUseGitTrackedFiles = $UseGitTrackedFiles -and (Test-IsGitRepo -RepoPath $resolvedSourceRoot)
$relativePaths = if ($canUseGitTrackedFiles) {
    Get-TrackedRelativePaths -RepoPath $resolvedSourceRoot
}
else {
    Get-RelativePathsByRecursion -RootPath $resolvedSourceRoot
}

$relativePaths = $relativePaths |
    Where-Object { -not (Test-IsExcludedRelativePath -RelativePath $_) } |
    Sort-Object -Unique

$selectedPaths = [System.Collections.Generic.List[string]]::new()
$skippedUpToDateCount = 0
$skippedMissingCount = 0
$copiedCount = 0

foreach ($relativePath in $relativePaths) {
    $sourcePath = Join-Path -Path $resolvedSourceRoot -ChildPath $relativePath
    $targetPath = Join-Path -Path $resolvedTargetRoot -ChildPath $relativePath

    if (-not (Test-Path -LiteralPath $targetPath)) {
        if (-not $IncludeMissing) {
            $skippedMissingCount++
            continue
        }

        $selectedPaths.Add($relativePath) | Out-Null
        if (-not $DryRun) {
            $targetDirectory = Split-Path -Path $targetPath -Parent
            if (-not (Test-Path -LiteralPath $targetDirectory)) {
                New-Item -ItemType Directory -Path $targetDirectory -Force | Out-Null
            }

            Copy-Item -LiteralPath $sourcePath -Destination $targetPath -Force
            $copiedCount++
        }

        continue
    }

    $sourceItem = Get-Item -LiteralPath $sourcePath
    $targetItem = Get-Item -LiteralPath $targetPath

    $shouldCopy = $sourceItem.LastWriteTimeUtc -gt $targetItem.LastWriteTimeUtc
    if (-not $shouldCopy -and $CompareContent) {
        $shouldCopy = (Get-FileHashOrNull -Path $sourcePath) -ne (Get-FileHashOrNull -Path $targetPath)
    }

    if (-not $shouldCopy) {
        $skippedUpToDateCount++
        continue
    }

    $selectedPaths.Add($relativePath) | Out-Null
    if (-not $DryRun) {
        Copy-Item -LiteralPath $sourcePath -Destination $targetPath -Force
        $copiedCount++
    }
}

$modeLabel = if ($DryRun) { "Dry run" } else { "Copy run" }
$selectionCount = $selectedPaths.Count
$sourceMode = if ($canUseGitTrackedFiles) { "git tracked files" } else { "recursive filesystem scan" }

Write-Host "$modeLabel complete."
Write-Host "Source root: $resolvedSourceRoot"
Write-Host "Target root: $resolvedTargetRoot"
Write-Host "Source selection mode: $sourceMode"
Write-Host "Tracked or discovered file count: $($relativePaths.Count)"
Write-Host "Selected file count: $selectionCount"
if (-not $DryRun) {
    Write-Host "Copied file count: $copiedCount"
}
Write-Host "Skipped up-to-date file count: $skippedUpToDateCount"
Write-Host "Skipped missing file count: $skippedMissingCount"

if ($ExcludeRelativePaths.Count -gt 0) {
    Write-Host "Excluded relative paths: $($ExcludeRelativePaths -join ', ')"
}

if ($selectionCount -gt 0) {
    Write-Host ""
    Write-Host "Files selected for copy:"
    $selectedPaths | ForEach-Object { Write-Host $_ }
}
