param(
    [string]$Owner = 'elumenotion',
    [string]$Registry = 'ghcr.io',
    [string]$ComposeTag = 'main',
    [string]$ReleaseTag = '',
    [ValidateSet('cpu', 'cuda13', 'rocm', 'slim', 'vulkan', 'spark')]
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
        [ValidateSet('cpu', 'cuda13', 'rocm', 'slim', 'vulkan', 'spark')]
        [string]$Variant
    )

    $variantPattern = switch ($Variant) {
        'cpu' { '^cpu-(?<build>\d{5}\.\d{4})$' }
        'cuda13' { '^cuda13-(?<build>\d{5}\.\d{4})$' }
        'rocm' { '^rocm-(?<build>\d{5}\.\d{4})$' }
        'slim' { '^slim-(?<build>\d{5}\.\d{4})$' }
        'vulkan' { '^vulkan-(?<build>\d{5}\.\d{4})$' }
        'spark'  { '^spark-(?<build>\d{5}\.\d{4})$' }
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
        [ValidateSet('cpu', 'cuda13', 'rocm', 'slim', 'vulkan', 'spark')]
        [string]$Variant
    )

    switch ($Variant) {
        'cpu' { return 'guideants-ai-cpu' }
        'cuda13' { return 'guideants-ai-cuda13' }
        'rocm' { return 'guideants-ai-rocm' }
        'slim' { return 'guideants-ai-slim' }
        'vulkan' { return 'guideants-ai-vulkan' }
        'spark'  { return 'guideants-ai-spark' }
    }
}

function New-VariantTarget {
    param(
        [Parameter(Mandatory = $true)]
        [ValidateSet('cpu', 'cuda13', 'rocm', 'slim', 'vulkan', 'spark')]
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

function Get-ImageArch {
    param([Parameter(Mandatory = $true)][string]$ImageRef)
    $arch = docker image inspect $ImageRef --format '{{.Architecture}}'
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($arch)) {
        throw "Unable to determine architecture of '$ImageRef'."
    }
    return $arch.Trim()
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
$variantFilter = if ($Variant.Count -gt 0) { $Variant } else { @('cpu', 'cuda13', 'rocm', 'slim', 'vulkan', 'spark') }

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
    # --- Architecture-aware support-image push -----------------------------------
    # Shared mutable tags (ComposeTag / latest) must stay amd64: the amd64 stacks
    # consume them with pull_policy: always. arm64 (spark) builds get arch-suffixed
    # tags only, so neither stack can clobber the other's tags. The arch check is a
    # hard guard: it refuses to push an image under tags owned by the other arch.
    function Push-SupportImage {
        param(
            [Parameter(Mandatory = $true)][string]$Name,
            [Parameter(Mandatory = $true)][string]$LocalRepository,
            [Parameter(Mandatory = $true)][string]$PackageName,
            [Parameter(Mandatory = $true)][string[]]$Amd64Tags,
            [Parameter(Mandatory = $true)][AllowEmptyCollection()][string[]]$Arm64Tags,
            [Parameter(Mandatory = $true)][string]$MissingMessage
        )
        $amd64Ref = $null; $arm64Ref = $null
        try { $amd64Ref = Get-LocalImageRef -Repository $LocalRepository -Tag 'latest' -MissingMessage 'not-found' } catch { }
        try { $arm64Ref = Get-LocalImageRef -Repository $LocalRepository -Tag 'arm64'  -MissingMessage 'not-found' } catch { }
        if (-not $amd64Ref -and -not $arm64Ref) { throw $MissingMessage }

        foreach ($pair in @(
            @{ Arch = 'amd64'; Tags = $Amd64Tags;   SourceRef = $amd64Ref },
            @{ Arch = 'arm64'; Tags = $Arm64Tags;   SourceRef = $arm64Ref }
        )) {
            if (-not $pair.SourceRef) { continue }
            $arch = Get-ImageArch -ImageRef $pair.SourceRef
            if ($arch -ne $pair.Arch) {
                $msg = ("Local '{0}' is '{1}' but was selected as the {2} source for {3}. Refusing to push: this would publish a {1} image under {2}-owned tags." -f $pair.SourceRef, $arch, $pair.Arch, $PackageName)
                throw $msg
            }
            Write-Host ""
            Write-Host "Pushing $Name image ($arch)" -ForegroundColor Cyan
            Write-Host "  Source:      $($pair.SourceRef)"
            foreach ($tag in $pair.Tags) {
                $targetRef = "$Registry/$Owner/${PackageName}:$tag"
                Write-Host "  Target tag:  $targetRef"
                Invoke-DockerCommand -Arguments @('tag', $pair.SourceRef, $targetRef)
                Invoke-DockerCommand -Arguments @('push', $targetRef)
            }
        }
    }

    $plantUmlAmd64Tags = @($cpuImage.BuildTag, '1.2025.2', $ComposeTag, 'latest')
    $mssqlAmd64Tags    = @($cpuImage.BuildTag, $ComposeTag, 'latest')
    $searxngAmd64Tags  = @($cpuImage.BuildTag, $ComposeTag, 'latest')
    $plantUmlArm64Tags = @('1.2025.2-arm64', "$ComposeTag-arm64", 'latest-arm64')
    $searxngArm64Tags  = @("$ComposeTag-arm64", 'latest-arm64')
    if (-not [string]::IsNullOrWhiteSpace($ReleaseTag)) {
        $plantUmlAmd64Tags = @($plantUmlAmd64Tags + $ReleaseTag | Select-Object -Unique)
        $mssqlAmd64Tags    = @($mssqlAmd64Tags + $ReleaseTag | Select-Object -Unique)
        $searxngAmd64Tags  = @($searxngAmd64Tags + $ReleaseTag | Select-Object -Unique)
        $plantUmlArm64Tags = @($plantUmlArm64Tags + "$ReleaseTag-arm64" | Select-Object -Unique)
        $searxngArm64Tags  = @($searxngArm64Tags + "$ReleaseTag-arm64" | Select-Object -Unique)
    }

    Push-SupportImage -Name 'plantuml' -LocalRepository 'plantuml-1.2025.2' -PackageName 'guideants-plantuml' `
        -Amd64Tags $plantUmlAmd64Tags `
        -Arm64Tags $plantUmlArm64Tags `
        -MissingMessage "No local guideants-plantuml image found. Build it first with docker/build/build_support_images.ps1."

    Push-SupportImage -Name 'mssql' -LocalRepository 'mssql2025-express-fts' -PackageName 'mssql2025-express-fts' `
        -Amd64Tags $mssqlAmd64Tags -Arm64Tags @() `
        -MissingMessage "No local mssql2025-express-fts:latest image found. Build it first with docker/build/build_support_images.ps1."

    Push-SupportImage -Name 'searxng' -LocalRepository 'guideants-searxng' -PackageName 'guideants-searxng' `
        -Amd64Tags $searxngAmd64Tags `
        -Arm64Tags $searxngArm64Tags `
        -MissingMessage "No local guideants-searxng image found. Build it first with docker/build/build_support_images.ps1."
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
