param(
    [string]$Owner = 'elumenotion',
    [string]$Registry = 'ghcr.io',
    [string]$ComposeTag = 'main',
    [string]$ReleaseTag = '',
    [ValidateSet('cpu', 'cuda13', 'rocm', 'slim', 'vulkan')]
    [string[]]$Variant = @(),
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

    $maxAttempts = if ($Arguments.Count -gt 0 -and $Arguments[0] -ieq 'push') { 3 } else { 1 }
    for ($attempt = 1; $attempt -le $maxAttempts; $attempt++) {
        & docker @Arguments
        if ($LASTEXITCODE -eq 0) {
            return
        }

        if ($attempt -lt $maxAttempts) {
            Write-Warning "docker command failed (attempt $attempt of $maxAttempts): docker $($Arguments -join ' '). Retrying in 15 seconds..."
            Start-Sleep -Seconds 15
            continue
        }

        throw "docker command failed: docker $($Arguments -join ' ')"
    }
}

function Get-GitHubLoginFromToken {
    param(
        [string]$Token
    )

    if ([string]::IsNullOrWhiteSpace($Token)) {
        return $null
    }

    try {
        $headers = @{
            Authorization = "Bearer $Token"
            Accept        = 'application/vnd.github+json'
            'User-Agent'  = 'GuideAnts-GHCR-Push'
        }

        $user = Invoke-RestMethod -Uri 'https://api.github.com/user' -Headers $headers -Method Get
        if ($null -ne $user -and -not [string]::IsNullOrWhiteSpace($user.login)) {
            return $user.login
        }
    }
    catch {
        return $null
    }

    return $null
}

function Get-GitHubCredential {
    $inputText = "protocol=https`nhost=github.com`n`n"
    $output = $inputText | git credential fill 2>$null
    if ($LASTEXITCODE -ne 0 -or $null -eq $output) {
        return $null
    }

    $credential = @{}
    foreach ($line in $output) {
        $parts = $line -split '=', 2
        if ($parts.Count -eq 2) {
            $credential[$parts[0]] = $parts[1]
        }
    }

    if ([string]::IsNullOrWhiteSpace($credential['username']) -and [string]::IsNullOrWhiteSpace($credential['password'])) {
        return $null
    }

    return [pscustomobject]@{
        Username = $credential['username']
        Password = $credential['password']
    }
}

function Get-ConfiguredToken {
    if (-not [string]::IsNullOrWhiteSpace($env:CR_PAT)) { return $env:CR_PAT }
    if (-not [string]::IsNullOrWhiteSpace($env:GHCR_PAT)) { return $env:GHCR_PAT }
    if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_TOKEN)) { return $env:GITHUB_TOKEN }

    foreach ($name in @('CR_PAT', 'GHCR_PAT', 'GITHUB_TOKEN')) {
        $value = [Environment]::GetEnvironmentVariable($name, 'User')
        if (-not [string]::IsNullOrWhiteSpace($value)) {
            return $value
        }
    }

    return $null
}

function Get-DefaultGhcrUsername {
    param(
        [string]$Token,
        [object]$GitHubCredential
    )

    if (-not [string]::IsNullOrWhiteSpace($env:GHCR_USERNAME)) { return $env:GHCR_USERNAME }
    if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_ACTOR)) { return $env:GITHUB_ACTOR }
    if ($null -ne $GitHubCredential -and -not [string]::IsNullOrWhiteSpace($GitHubCredential.Username)) { return $GitHubCredential.Username }

    return Get-GitHubLoginFromToken -Token $Token
}

function Get-LatestVariantImage {
    param(
        [Parameter(Mandatory = $true)]
        [ValidateSet('cpu', 'cuda13', 'rocm', 'slim', 'vulkan')]
        [string]$Variant
    )

    $variantPattern = switch ($Variant) {
        'cpu' { '^cpu-(?<build>\d{5}\.\d{4})$' }
        'cuda13' { '^cuda13-(?<build>\d{5}\.\d{4})$' }
        'rocm' { '^rocm-(?<build>\d{5}\.\d{4})$' }
        'slim' { '^slim-(?<build>\d{5}\.\d{4})$' }
        'vulkan' { '^vulkan-(?<build>\d{5}\.\d{4})$' }
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

function Get-VariantPackageName {
    param(
        [Parameter(Mandatory = $true)]
        [ValidateSet('cpu', 'cuda13', 'rocm', 'slim', 'vulkan')]
        [string]$Variant
    )

    switch ($Variant) {
        'cpu' { return 'guideants-ai-cpu' }
        'cuda13' { return 'guideants-ai-cuda13' }
        'rocm' { return 'guideants-ai-rocm' }
        'slim' { return 'guideants-ai-slim' }
        'vulkan' { return 'guideants-ai-vulkan' }
    }
}

function New-VariantTarget {
    param(
        [Parameter(Mandatory = $true)]
        [ValidateSet('cpu', 'cuda13', 'rocm', 'slim', 'vulkan')]
        [string]$Variant,
        [switch]$Required
    )

    try {
        $image = Get-LatestVariantImage -Variant $Variant
    }
    catch {
        if ($Required) {
            throw
        }

        Write-Warning "No local $Variant image found; skipping $Variant push. Build it first with docker/build/build_guideants_ai.ps1 -Backend $Variant."
        return $null
    }

    return [pscustomobject]@{
        Variant     = $Variant
        PackageName = Get-VariantPackageName -Variant $Variant
        SourceRef   = $image.SourceRef
        BuildTag    = $image.BuildTag
    }
}

function Get-LocalImageRef {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Repository,
        [string]$Tag = 'latest',
        [Parameter(Mandatory = $true)]
        [string]$MissingMessage
    )

    $rows = docker image ls $Repository --format "{{.Repository}}|{{.Tag}}"
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to enumerate local image '$Repository'."
    }

    foreach ($row in $rows) {
        if ([string]::IsNullOrWhiteSpace($row)) { continue }
        $parts = $row -split '\|', 2
        if ($parts.Count -ne 2) { continue }

        $repo = $parts[0].Trim()
        $rowTag = $parts[1].Trim()
        if ([string]::IsNullOrWhiteSpace($repo) -or [string]::IsNullOrWhiteSpace($rowTag)) { continue }

        if ($repo -ieq $Repository -and $rowTag -ieq $Tag) {
            return "${repo}:$rowTag"
        }
    }

    throw $MissingMessage
}

if ([string]::IsNullOrWhiteSpace($Owner)) {
    throw "Unable to determine GHCR owner. Pass -Owner (for example: -Owner elumenotion)."
}

$Owner = $Owner.ToLowerInvariant()

if (-not $SkipLogin) {
    if ([string]::IsNullOrWhiteSpace($Token)) {
        $Token = Get-ConfiguredToken
    }

    $gitHubCredential = $null
    if ([string]::IsNullOrWhiteSpace($Username) -or [string]::IsNullOrWhiteSpace($Token)) {
        $gitHubCredential = Get-GitHubCredential
    }

    if ([string]::IsNullOrWhiteSpace($Username)) {
        $Username = Get-DefaultGhcrUsername -Token $Token -GitHubCredential $gitHubCredential
    }

    if ([string]::IsNullOrWhiteSpace($Token) -and $null -ne $gitHubCredential -and -not [string]::IsNullOrWhiteSpace($gitHubCredential.Password)) {
        $Token = $gitHubCredential.Password
    }

    if ([string]::IsNullOrWhiteSpace($Username)) {
        throw "GHCR username is required. Pass -Username, set GHCR_USERNAME / GITHUB_ACTOR, or sign in through git credential manager."
    }

    if ([string]::IsNullOrWhiteSpace($Token)) {
        throw "GHCR token is required. Pass -Token, set CR_PAT / GHCR_PAT / GITHUB_TOKEN, or sign in through git credential manager."
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

$pushSupportImages = $Variant.Count -eq 0
$variantFilter = if ($Variant.Count -gt 0) { $Variant } else { @('cpu', 'cuda13', 'rocm', 'slim', 'vulkan') }

$targets = @()
foreach ($variantName in $variantFilter) {
    $required = $Variant.Count -gt 0 -or $variantName -in @('cpu', 'cuda13')
    $target = New-VariantTarget -Variant $variantName -Required:($required)
    if ($null -ne $target) {
        $targets += $target
    }
}

if ($targets.Count -eq 0) {
    throw 'No GuideAnts AI images matched the requested variant filter.'
}

$cpuImage = $targets | Where-Object { $_.Variant -eq 'cpu' } | Select-Object -First 1
if ($pushSupportImages -and $null -eq $cpuImage) {
    $cpuImage = Get-LatestVariantImage -Variant 'cpu'
}

foreach ($target in $targets) {
    $buildRef = "$Registry/$Owner/$($target.PackageName):$($target.BuildTag)"
    $latestRef = "$Registry/$Owner/$($target.PackageName):latest"
    $composeRef = "$Registry/$Owner/$($target.PackageName):$ComposeTag"
    $releaseRef = if (-not [string]::IsNullOrWhiteSpace($ReleaseTag)) {
        "$Registry/$Owner/$($target.PackageName):$ReleaseTag"
    } else {
        $null
    }

    Write-Host ""
    Write-Host "Pushing $($target.Variant) image" -ForegroundColor Cyan
    Write-Host "  Source:      $($target.SourceRef)"
    Write-Host "  Build tag:   $buildRef"
    Write-Host "  Compose tag: $composeRef"
    if ($null -ne $releaseRef) {
        Write-Host "  Release tag: $releaseRef"
    }
    Write-Host "  Latest tag:  $latestRef"

    Invoke-DockerCommand -Arguments @('tag', $target.SourceRef, $buildRef)
    Invoke-DockerCommand -Arguments @('push', $buildRef)

    Invoke-DockerCommand -Arguments @('tag', $target.SourceRef, $composeRef)
    Invoke-DockerCommand -Arguments @('push', $composeRef)

    if ($null -ne $releaseRef) {
        Invoke-DockerCommand -Arguments @('tag', $target.SourceRef, $releaseRef)
        Invoke-DockerCommand -Arguments @('push', $releaseRef)
    }

    Invoke-DockerCommand -Arguments @('tag', $target.SourceRef, $latestRef)
    Invoke-DockerCommand -Arguments @('push', $latestRef)
}

if ($pushSupportImages) {
    $plantUmlSourceRef = Get-LocalImageRef `
        -Repository 'plantuml-1.2025.2' `
        -MissingMessage "No local plantuml-1.2025.2:latest image found. Build it first with docker/build/build_support_images.ps1."

    $mssqlSourceRef = Get-LocalImageRef `
        -Repository 'mssql2025-express-fts' `
        -MissingMessage "No local mssql2025-express-fts:latest image found. Build it first with docker/build/build_support_images.ps1."

    $searxngSourceRef = Get-LocalImageRef `
        -Repository 'guideants-searxng' `
        -MissingMessage "No local guideants-searxng:latest image found. Build it first with docker/build/build_support_images.ps1."

    $extraTargets = @(
        [pscustomobject]@{
            Name        = 'plantuml'
            SourceRef   = $plantUmlSourceRef
            PackageName = 'guideants-plantuml'
            Tags        = @($cpuImage.BuildTag, '1.2025.2', $ComposeTag, 'latest')
        },
        [pscustomobject]@{
            Name        = 'mssql'
            SourceRef   = $mssqlSourceRef
            PackageName = 'mssql2025-express-fts'
            Tags        = @($cpuImage.BuildTag, $ComposeTag, 'latest')
        },
        [pscustomobject]@{
            Name        = 'searxng'
            SourceRef   = $searxngSourceRef
            PackageName = 'guideants-searxng'
            Tags        = @($cpuImage.BuildTag, $ComposeTag, 'latest')
        }
    )

    if (-not [string]::IsNullOrWhiteSpace($ReleaseTag)) {
        foreach ($target in $extraTargets) {
            $target.Tags = @($target.Tags + @($ReleaseTag) | Select-Object -Unique)
        }
    }

    foreach ($target in $extraTargets) {
        $targetRefs = @()
        foreach ($tag in $target.Tags) {
            $targetRefs += "$Registry/$Owner/$($target.PackageName):$tag"
        }

        Write-Host ""
        Write-Host "Pushing $($target.Name) image" -ForegroundColor Cyan
        Write-Host "  Source:      $($target.SourceRef)"
        foreach ($targetRef in $targetRefs) {
            Write-Host "  Target tag:  $targetRef"
            Invoke-DockerCommand -Arguments @('tag', $target.SourceRef, $targetRef)
            Invoke-DockerCommand -Arguments @('push', $targetRef)
        }
    }
}

Write-Host ""
$aiVariants = ($targets | ForEach-Object { $_.Variant }) -join ', '
$doneMessage = if ($pushSupportImages) {
    "Done. Pushed local GuideAnts AI images ($aiVariants) plus PlantUML, MSSQL FTS, and SearXNG images to GHCR owner '$Owner'."
}
else {
    "Done. Pushed local GuideAnts AI images ($aiVariants) to GHCR owner '$Owner'."
}
Write-Host $doneMessage -ForegroundColor Green
