[CmdletBinding()]
param(
    [string]$RepoRoot = (Get-Location).ProviderPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Remove-PathIfExists {
    param([string]$Path)

    if (Test-Path -LiteralPath $Path) {
        Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Test-ContainerPathSymlinkTarget {
    param([object]$Target)

    foreach ($entry in @($Target)) {
        if ([string]::IsNullOrWhiteSpace($entry)) {
            continue
        }

        if ($entry -match '^/app/' -or $entry -match '^\\app\\') {
            return $true
        }
    }

    return $false
}

function Remove-ContainerPathSymlinks {
    param([string]$Root)

    if (-not (Test-Path -LiteralPath $Root)) {
        return 0
    }

    $removed = 0
    Get-ChildItem -LiteralPath $Root -Recurse -Force -ErrorAction SilentlyContinue |
        Where-Object { $_.Attributes -band [System.IO.FileAttributes]::ReparsePoint } |
        Where-Object { Test-ContainerPathSymlinkTarget -Target $_.Target } |
        ForEach-Object {
            Write-Host "Removing container-path symlink: $($_.FullName)"
            Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction SilentlyContinue
            $removed++
        }

    return $removed
}

$integrationTestVolumes = Join-Path $RepoRoot 'src/server/GuideAntsApi.IntegrationTests/docker/volumes'
if (Test-Path -LiteralPath $integrationTestVolumes) {
    Write-Host "Removing integration-test docker volume tree (Linux symlinks break local C# CodeQL extraction on Windows)..."
    Remove-PathIfExists -Path $integrationTestVolumes
}

$dockerVolumes = Join-Path $RepoRoot 'docker/volumes'
$symlinkCount = Remove-ContainerPathSymlinks -Root $dockerVolumes
if ($symlinkCount -gt 0) {
    Write-Host "Removed $symlinkCount container-path symlink(s) under docker/volumes."
}
