param(
    [string]$Owner,
    [string]$Registry = 'ghcr.io',
    [string]$Username = $env:GHCR_USERNAME,
    [string]$Token = $(if ($env:CR_PAT) { $env:CR_PAT } elseif ($env:GHCR_PAT) { $env:GHCR_PAT } elseif ($env:GITHUB_TOKEN) { $env:GITHUB_TOKEN } else { $null }),
    [switch]$SkipLogin,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

function Invoke-DockerCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    if ($DryRun) {
        Write-Host "[dry-run] docker $($Arguments -join ' ')" -ForegroundColor Yellow
        return
    }

    & docker @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "docker command failed: docker $($Arguments -join ' ')"
    }
}

function Get-OwnerFromGitRemote {
    $remote = git remote get-url origin 2>$null
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($remote)) {
        return $null
    }

    $pattern = 'github\.com[:/](?<owner>[^/]+)/(?<repo>[^/.]+?)(?:\.git)?$'
    $match = [regex]::Match($remote.Trim(), $pattern, [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
    if (-not $match.Success) {
        return $null
    }

    return $match.Groups['owner'].Value.ToLowerInvariant()
}

function Get-LatestVariantImage {
    param(
        [Parameter(Mandatory = $true)]
        [ValidateSet('cpu', 'cuda13')]
        [string]$Variant
    )

    $variantPattern = if ($Variant -eq 'cpu') {
        '^cpu-(?<build>\d{5}\.\d{4})$'
    }
    else {
        '^cuda13-(?<build>\d{5}\.\d{4})$'
    }

    $rows = docker image ls guideants-ai --format "{{.Repository}}|{{.Tag}}"
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to enumerate local guideants-ai images."
    }

    $candidates = foreach ($row in $rows) {
        if ([string]::IsNullOrWhiteSpace($row)) { continue }
        $parts = $row -split '\|', 2
        if ($parts.Count -ne 2) { continue }

        $repository = $parts[0].Trim()
        $tag = $parts[1].Trim()
        if ([string]::IsNullOrWhiteSpace($tag) -or $tag -eq '<none>') { continue }

        $tagMatch = [regex]::Match($tag, $variantPattern, [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
        if (-not $tagMatch.Success) { continue }

        $buildTag = $tagMatch.Groups['build'].Value
        [pscustomobject]@{
            SourceRef = "${repository}:$tag"
            BuildTag  = $buildTag
            SortKey   = [int64]($buildTag -replace '\.', '')
        }
    }

    if (-not $candidates) {
        throw "No local guideants-ai:$Variant-* image found. Build that variant first with docker/build/build_guideants_ai.ps1."
    }

    return $candidates | Sort-Object -Property SortKey -Descending | Select-Object -First 1
}

if ([string]::IsNullOrWhiteSpace($Owner)) {
    $Owner = Get-OwnerFromGitRemote
}

if ([string]::IsNullOrWhiteSpace($Owner)) {
    throw "Unable to determine GHCR owner. Pass -Owner (for example: -Owner elumenotion)."
}

$Owner = $Owner.ToLowerInvariant()

if (-not $SkipLogin) {
    if ([string]::IsNullOrWhiteSpace($Username)) {
        throw "GHCR username is required. Pass -Username or set GHCR_USERNAME."
    }

    if ([string]::IsNullOrWhiteSpace($Token)) {
        throw "GHCR token is required. Pass -Token or set CR_PAT / GHCR_PAT / GITHUB_TOKEN."
    }

    if ($DryRun) {
        Write-Host "[dry-run] docker login $Registry -u $Username --password-stdin" -ForegroundColor Yellow
    }
    else {
        $Token | docker login $Registry -u $Username --password-stdin
        if ($LASTEXITCODE -ne 0) {
            throw "docker login failed for $Registry."
        }
    }
}

$cpuImage = Get-LatestVariantImage -Variant 'cpu'
$cudaImage = Get-LatestVariantImage -Variant 'cuda13'

$targets = @(
    [pscustomobject]@{
        Variant     = 'cpu'
        PackageName = 'guideants-ai-cpu'
        SourceRef   = $cpuImage.SourceRef
        BuildTag    = $cpuImage.BuildTag
    },
    [pscustomobject]@{
        Variant     = 'cuda13'
        PackageName = 'guideants-ai-cuda13'
        SourceRef   = $cudaImage.SourceRef
        BuildTag    = $cudaImage.BuildTag
    }
)

foreach ($target in $targets) {
    $buildRef = "$Registry/$Owner/$($target.PackageName):$($target.BuildTag)"
    $latestRef = "$Registry/$Owner/$($target.PackageName):latest"

    Write-Host ""
    Write-Host "Pushing $($target.Variant) image" -ForegroundColor Cyan
    Write-Host "  Source:      $($target.SourceRef)"
    Write-Host "  Build tag:   $buildRef"
    Write-Host "  Latest tag:  $latestRef"

    Invoke-DockerCommand -Arguments @('tag', $target.SourceRef, $buildRef)
    Invoke-DockerCommand -Arguments @('push', $buildRef)

    Invoke-DockerCommand -Arguments @('tag', $target.SourceRef, $latestRef)
    Invoke-DockerCommand -Arguments @('push', $latestRef)
}

Write-Host ""
Write-Host "Done. Pushed latest local CPU and CUDA13 GuideAnts AI images to GHCR owner '$Owner'." -ForegroundColor Green
