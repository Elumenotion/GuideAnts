param(
    [switch]$RebuildBase,
    [switch]$All,
    [ValidateSet('cpu', 'cuda13', 'rocm', 'slim', 'vulkan')]
    [string]$Backend
)

$ErrorActionPreference = 'Stop'
$env:DOCKER_BUILDKIT = '1'

if ($All) {
    Write-Error "The -All support-image build was split out. Run build_support_images.ps1 separately after backend builds."
    exit 1
}

function Get-CombinedHash {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Paths
    )

    $lines = foreach ($path in $Paths) {
        if (-not (Test-Path $path)) {
            throw "Hash input file not found: $path"
        }

        $hash = (Get-FileHash -Path $path -Algorithm SHA256).Hash.ToLowerInvariant()
        "$path|$hash"
    }

    $joined = [string]::Join("`n", $lines)
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($joined)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $digest = $sha.ComputeHash($bytes)
    }
    finally {
        $sha.Dispose()
    }

    -join ($digest | ForEach-Object { $_.ToString('x2') })
}

function Test-DockerImageExists {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ImageTag
    )

    $matches = docker image ls --format '{{.Repository}}:{{.Tag}}' $ImageTag 2>$null
    if ($LASTEXITCODE -ne 0) {
        return $false
    }

    return $matches -contains $ImageTag
}

function Get-HashFromFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if (-not (Test-Path $Path)) {
        return $null
    }

    return (Get-Content -Path $Path -Raw).Trim()
}

function Get-FilePathsRecursive {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Root
    )

    if (-not (Test-Path $Root)) {
        return @()
    }

    return @(
        Get-ChildItem -Path $Root -Recurse -File |
            Sort-Object FullName |
            Select-Object -ExpandProperty FullName
    )
}

function Set-DotEnvValue {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$Key,

        [Parameter(Mandatory = $true)]
        [string]$Value
    )

    $envLine = "$Key=$Value"
    $lines = @()
    if (Test-Path $Path) {
        $lines = @(Get-Content -Path $Path)
    }

    $replaced = $false
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match "^\s*$([regex]::Escape($Key))=") {
            $lines[$i] = $envLine
            $replaced = $true
            break
        }
    }

    if (-not $replaced) {
        $lines += $envLine
    }

    if ($PSVersionTable.PSVersion.Major -ge 6) {
        Set-Content -Path $Path -Value $lines -Encoding utf8NoBOM
    }
    else {
        $utf8NoBom = [System.Text.UTF8Encoding]::new($false)
        [System.IO.File]::WriteAllLines($Path, [string[]]$lines, $utf8NoBom)
    }
}

function Promote-LocalBuildxCache {
    param(
        [Parameter(Mandatory = $true)]
        [string]$CurrentPath,

        [Parameter(Mandatory = $true)]
        [string]$NewPath
    )

    if (-not (Test-Path $NewPath)) {
        return
    }

    if (Test-Path $CurrentPath) {
        Remove-Item -Path $CurrentPath -Recurse -Force
    }
    Move-Item -Path $NewPath -Destination $CurrentPath
}

function Test-BuildxCacheExportSupport {
    $inspectOutput = docker buildx inspect --bootstrap 2>$null
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "Could not inspect Docker Buildx builder. Disabling local cache export (--cache-to) for this run."
        return $false
    }

    $driverLine = $inspectOutput | Where-Object { $_ -match '^\s*Driver:\s*' } | Select-Object -First 1
    if (-not $driverLine) {
        Write-Warning "Could not determine Buildx driver. Disabling local cache export (--cache-to) for this run."
        return $false
    }

    $driver = (($driverLine -replace '^\s*Driver:\s*', '').Trim()).ToLowerInvariant()
    if ($driver -eq 'docker') {
        Write-Warning "Buildx driver 'docker' does not support cache export. Continuing without --cache-to."
        return $false
    }

    return $true
}

$repoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$dockerRoot = Split-Path $PSScriptRoot -Parent
$serverPath = Join-Path $repoRoot 'src\server'
$buildContext = Join-Path $PSScriptRoot 'guideants-ai'
$buildStateDir = Join-Path $dockerRoot '.build-state'
$depsCachePath = Join-Path $dockerRoot '.buildx-cache-deps'
$finalCachePath = Join-Path $dockerRoot '.buildx-cache-final'
$depsCachePathNew = "$depsCachePath-new"
$finalCachePathNew = "$finalCachePath-new"
$supportsCacheExport = Test-BuildxCacheExportSupport

if (-not (Test-Path $buildStateDir)) {
    New-Item -ItemType Directory -Path $buildStateDir | Out-Null
}

foreach ($cachePath in @($depsCachePath, $finalCachePath)) {
    if (-not (Test-Path $cachePath)) {
        New-Item -ItemType Directory -Path $cachePath | Out-Null
    }
}
foreach ($newCachePath in @($depsCachePathNew, $finalCachePathNew)) {
    if (Test-Path $newCachePath) {
        Remove-Item -Path $newCachePath -Recurse -Force
    }
}

# --- Select backend ---

if ([string]::IsNullOrWhiteSpace($Backend)) {
    Write-Host "Select backend:"
    Write-Host "  1) CPU-only"
    Write-Host "  2) CUDA 13"
    Write-Host "  3) ROCm"
    Write-Host "  4) Slim"
    Write-Host "  5) Vulkan"
    $choice = Read-Host "Enter choice [1-5]"
    switch ($choice) {
        '1' { $Backend = 'cpu' }
        '2' { $Backend = 'cuda13' }
        '3' { $Backend = 'rocm' }
        '4' { $Backend = 'slim' }
        '5' { $Backend = 'vulkan' }
        default {
            Write-Error "Invalid choice '$choice'. Valid values: 1-5."
            exit 1
        }
    }
}
switch ($Backend) {
    'cpu' {
        $Backend = 'cpu'
        $fullTarget = 'final-cpu'
        $depsTarget = 'deps-cpu'
        $depsImageArg = 'GA_DEPS_CPU_IMAGE'
        $requirementsSrc = Join-Path $PSScriptRoot 'Sandboxes\python311TorchCPU\requirements.txt'
        $dockerfilePath = Join-Path $buildContext 'Dockerfile.cpu'
    }
    'cuda13' {
        $Backend = 'cuda13'
        $fullTarget = 'final-cuda13'
        $depsTarget = 'deps-cuda13'
        $depsImageArg = 'GA_DEPS_CUDA13_IMAGE'
        $requirementsSrc = Join-Path $PSScriptRoot 'Sandboxes\python311TorchCUDA\requirements.txt'
        $dockerfilePath = Join-Path $buildContext 'Dockerfile.cuda'
    }
    'rocm' {
        $Backend = 'rocm'
        $fullTarget = 'final-rocm'
        $depsTarget = 'deps-rocm'
        $depsImageArg = 'GA_DEPS_ROCM_IMAGE'
        $requirementsSrc = Join-Path $PSScriptRoot 'Sandboxes\python311TorchROCM\requirements.txt'
        $dockerfilePath = Join-Path $buildContext 'Dockerfile.rocm'
    }
    'slim' {
        $Backend = 'slim'
        $fullTarget = 'final-slim'
        $depsTarget = 'deps-slim'
        $depsImageArg = 'GA_DEPS_SLIM_IMAGE'
        $requirementsSrc = Join-Path $PSScriptRoot 'Sandboxes\python311Slim\requirements.txt'
        $dockerfilePath = Join-Path $buildContext 'Dockerfile.slim'
    }
    'vulkan' {
        $Backend = 'vulkan'
        $fullTarget = 'final-vulkan'
        $depsTarget = 'deps-vulkan'
        $depsImageArg = 'GA_DEPS_VULKAN_IMAGE'
        $requirementsSrc = Join-Path $PSScriptRoot 'Sandboxes\python311TorchVulkan\requirements.txt'
        $dockerfilePath = Join-Path $buildContext 'Dockerfile.vulkan'
    }
    default {
        Write-Error "Invalid backend '$Backend'. Valid values: cpu, cuda13, rocm, slim, vulkan."
        exit 1
    }
}

# Build a unique tag per build, and also maintain a stable backend-specific latest tag.
$julianDay = "$(Get-Date -Format 'yy')$((Get-Date).DayOfYear.ToString('000'))"
$timeStamp = Get-Date -Format 'HHmm'
$imageTag = "guideants-ai:${Backend}-${julianDay}.${timeStamp}"
$latestImageTag = "guideants-ai:${Backend}-latest"

Write-Host "============================================" -ForegroundColor Cyan
Write-Host "  Building GuideAnts AI" -ForegroundColor Cyan
Write-Host "============================================" -ForegroundColor Cyan
Write-Host "Backend:       $Backend"
Write-Host "Target stage:  $fullTarget"
Write-Host "Image tag:     $imageTag"
Write-Host "Latest alias:  $latestImageTag"
Write-Host "Deps target:   $depsTarget"
Write-Host "Rebuild base:  $RebuildBase"
Write-Host ""

if (-not (Test-Path $requirementsSrc)) {
    Write-Error "Requirements file not found at $requirementsSrc"
    exit 1
}
if (-not (Test-Path $dockerfilePath)) {
    Write-Error "Dockerfile not found at $dockerfilePath"
    exit 1
}

# --- Build ScriptExecutionAgent ---
$scriptAgentProject = Join-Path $serverPath 'ScriptExecutionAgent'
if (-not (Test-Path $scriptAgentProject)) {
    Write-Error "ScriptExecutionAgent directory not found at $scriptAgentProject"
    exit 1
}

$publishOutput = Join-Path $buildStateDir 'scriptexecutionagent-guideants-ai-publish'
$scriptAgentHashFile = Join-Path $buildStateDir 'scriptexecutionagent-guideants-ai.hash'
$scriptAgentSourceFiles = Get-ChildItem -Path $scriptAgentProject -Recurse -File |
    Where-Object {
        $_.FullName -notlike "*\bin\*" -and
        $_.FullName -notlike "*\obj\*" -and
        $_.FullName -notlike "*\publish\*"
    } |
    Sort-Object FullName |
    Select-Object -ExpandProperty FullName

$scriptAgentSourceHash = Get-CombinedHash -Paths $scriptAgentSourceFiles
$canReusePublish = $false
if ((Test-Path $publishOutput) -and (Test-Path $scriptAgentHashFile)) {
    $previousHash = (Get-Content -Path $scriptAgentHashFile -Raw).Trim()
    if ($previousHash -eq $scriptAgentSourceHash) {
        $canReusePublish = $true
    }
}

if ($canReusePublish) {
    Write-Host "ScriptExecutionAgent unchanged; reusing existing publish output." -ForegroundColor Green
}
else {
    if (Test-Path $publishOutput) {
        Remove-Item -Path $publishOutput -Recurse -Force
    }

    Push-Location $scriptAgentProject
    try {
        dotnet restore
        dotnet publish -c Release -o $publishOutput
        Set-Content -Path $scriptAgentHashFile -Value $scriptAgentSourceHash -Encoding UTF8
        Write-Host "ScriptExecutionAgent built successfully." -ForegroundColor Green
    }
    catch {
        Write-Error "Failed to build ScriptExecutionAgent: $($_.Exception.Message)"
        exit 1
    }
    finally {
        Pop-Location
    }
}

# --- Stage build context artifacts ---
$agentDest = Join-Path $buildContext 'ScriptExecutionAgent'
$reqDest = Join-Path $buildContext 'requirements.txt'

if (Test-Path $agentDest) {
    Remove-Item -Path $agentDest -Recurse -Force
}
Copy-Item -Path $publishOutput -Destination $agentDest -Recurse -Force

# Sandbox requirements.txt is copied as-is: torch/torchaudio/torchvision/torchtext were
# removed from the sandbox requirements files (Tier B torch removal, 2026-07-02); the AI
# services never depended on torch (facades over native audio.cpp/llama.cpp/sd.cpp binaries).
Copy-Item -Path $requirementsSrc -Destination $reqDest -Force

Write-Host "Build context staged." -ForegroundColor Green

$depsHashInputs = @(
    $dockerfilePath,
    (Join-Path $buildContext 'asr-requirements.txt'),
    (Join-Path $buildContext 'tts-requirements.txt'),
    (Join-Path $buildContext 'emb-requirements.txt'),
    $reqDest
)
$depsHash = (Get-CombinedHash -Paths $depsHashInputs).Substring(0, 12)
$depsTag = "guideants-ai-deps:${Backend}-${depsHash}"
$depsCacheTag = "guideants-ai-deps:${Backend}-cache"
Write-Host "Dependency image tag: $depsTag"
Write-Host "Dependency cache tag: $depsCacheTag"

try {
    $depsExists = Test-DockerImageExists -ImageTag $depsTag
    $depsCacheExists = Test-DockerImageExists -ImageTag $depsCacheTag
    if ($RebuildBase -or -not $depsExists) {
        if ($RebuildBase) {
            Write-Host "Rebuilding dependency image without cache..." -ForegroundColor Yellow
        }
        else {
            Write-Host "Dependency image not found. Building $depsTag..." -ForegroundColor Cyan
        }

        $depsBuildArgs = @('buildx', 'build', '--load')
        if ($RebuildBase) {
            $depsBuildArgs += '--no-cache'
        }
        else {
            $depsBuildArgs += @(
                '--cache-from', "type=local,src=$depsCachePath",
                '--cache-from', "type=local,src=$finalCachePath"
            )
            if ($depsCacheExists) {
                $depsBuildArgs += @('--cache-from', $depsCacheTag)
            }
        }
        $depsBuildArgs += @(
            '--target', $depsTarget,
            '-t', $depsTag,
            '-t', $depsCacheTag,
            '-f', $dockerfilePath,
            $buildContext
        )
        if ($supportsCacheExport) {
            $depsBuildArgs += @('--cache-to', "type=local,dest=$depsCachePathNew,mode=min")
        }

        docker @depsBuildArgs
        if ($LASTEXITCODE -ne 0) {
            Write-Error "Dependency image build failed with exit code $LASTEXITCODE"
            exit 1
        }
        Promote-LocalBuildxCache -CurrentPath $depsCachePath -NewPath $depsCachePathNew
    }
    else {
        Write-Host "Reusing cached dependency image: $depsTag" -ForegroundColor Green
        docker tag $depsTag $depsCacheTag
        if ($LASTEXITCODE -ne 0) {
            Write-Error "Failed to tag dependency cache image $depsCacheTag from $depsTag"
            exit 1
        }
    }

    # --- Build final image (one Dockerfile, backend selected by target) ---
    $dockerArgs = @('buildx', 'build', '--load')
    if ($RebuildBase) {
        $dockerArgs += '--no-cache'
    }
    $dockerArgs += @(
        '--cache-from', "type=local,src=$depsCachePath",
        '--cache-from', "type=local,src=$finalCachePath",
        '--build-arg', "$depsImageArg=$depsTag",
        '--target', $fullTarget,
        '-t', $imageTag,
        '-t', $latestImageTag,
        '-f', $dockerfilePath,
        $buildContext
    )
    if ($supportsCacheExport) {
        $dockerArgs += @('--cache-to', "type=local,dest=$finalCachePathNew,mode=min")
    }

    docker @dockerArgs
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Image build failed with exit code $LASTEXITCODE"
        exit 1
    }
    Promote-LocalBuildxCache -CurrentPath $finalCachePath -NewPath $finalCachePathNew

    Write-Host "Image built: $imageTag" -ForegroundColor Green
}
finally {
    if (Test-Path $agentDest) {
        Remove-Item -Path $agentDest -Recurse -Force
    }
    if (Test-Path $reqDest) {
        Remove-Item -Path $reqDest -Force
    }
    foreach ($newCachePath in @($depsCachePathNew, $finalCachePathNew)) {
        if (Test-Path $newCachePath) {
            Remove-Item -Path $newCachePath -Recurse -Force
        }
    }
}

# --- Write backend-specific GuideAnts AI image tag to docker/.env ---
$envFile = Join-Path $dockerRoot '.env'
$imageEnvKey = switch ($Backend) {
    'cuda13' { 'GA_AI_CUDA_IMAGE' }
    'rocm' { 'GA_AI_ROCM_IMAGE' }
    'slim' { 'GA_AI_SLIM_IMAGE' }
    'vulkan' { 'GA_AI_VULKAN_IMAGE' }
    default { 'GA_AI_CPU_IMAGE' }
}
$envLine = "$imageEnvKey=$latestImageTag"
Set-DotEnvValue -Path $envFile -Key $imageEnvKey -Value $latestImageTag
Write-Host "Wrote $envLine to $envFile" -ForegroundColor Green

Write-Host ""
Write-Host "============================================" -ForegroundColor Cyan
Write-Host "  Build complete: $imageTag" -ForegroundColor Cyan
Write-Host "  Latest alias:   $latestImageTag" -ForegroundColor Cyan
Write-Host "============================================" -ForegroundColor Cyan
