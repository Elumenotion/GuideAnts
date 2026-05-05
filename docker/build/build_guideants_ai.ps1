param(
    [switch]$RebuildBase,
    [switch]$All,
    [ValidateSet('cpu', 'cuda13', 'rocm')]
    [string]$Backend
)

$ErrorActionPreference = 'Stop'
$env:DOCKER_BUILDKIT = '1'

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

    docker image inspect $ImageTag *> $null
    return ($LASTEXITCODE -eq 0)
}

$repoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$dockerRoot = Split-Path $PSScriptRoot -Parent
$serverPath = Join-Path $repoRoot 'src\server'
$buildContext = Join-Path $PSScriptRoot 'guideants-ai'
$depsCachePath = Join-Path $dockerRoot '.buildx-cache-deps'
$finalCachePath = Join-Path $dockerRoot '.buildx-cache-final'

# --- Select backend ---

if ([string]::IsNullOrWhiteSpace($Backend)) {
    Write-Host "Select backend:"
    Write-Host "  1) CPU-only"
    Write-Host "  2) CUDA 13"
    Write-Host "  3) ROCm"
    $choice = Read-Host "Enter choice [1, 2, or 3]"
}
else {
    $choice = switch ($Backend) {
        'cpu' { '1' }
        'cuda13' { '2' }
        'rocm' { '3' }
    }
}

switch ($choice) {
    '1' {
        $Backend = 'cpu'
        $fullTarget = 'final-cpu'
        $depsTarget = 'deps-cpu'
        $depsImageArg = 'GA_DEPS_CPU_IMAGE'
        $requirementsSrc = Join-Path $PSScriptRoot 'Sandboxes\python311TorchCPU\requirements.txt'
        $dockerfilePath = Join-Path $buildContext 'Dockerfile.cpu'
    }
    '2' {
        $Backend = 'cuda13'
        $fullTarget = 'final-cuda13'
        $depsTarget = 'deps-cuda13'
        $depsImageArg = 'GA_DEPS_CUDA13_IMAGE'
        $requirementsSrc = Join-Path $PSScriptRoot 'Sandboxes\python311TorchCUDA\requirements.txt'
        $dockerfilePath = Join-Path $buildContext 'Dockerfile.cuda'
    }
    '3' {
        $Backend = 'rocm'
        $fullTarget = 'final-rocm'
        $depsTarget = 'deps-rocm'
        $depsImageArg = 'GA_DEPS_ROCM_IMAGE'
        $requirementsSrc = Join-Path $PSScriptRoot 'Sandboxes\python311TorchROCM\requirements.txt'
        $dockerfilePath = Join-Path $buildContext 'Dockerfile.rocm'
    }
    default {
        Write-Error "Invalid choice."
        exit 1
    }
}

# Julian date (2-digit year + day-of-year) + time tag: e.g. cuda13-26096.1430
$julianDay = "$(Get-Date -Format 'yy')$((Get-Date).DayOfYear.ToString('000'))"
$timeStamp = Get-Date -Format 'HHmm'
$imageTag = "guideants-ai:${Backend}-${julianDay}.${timeStamp}"

Write-Host "============================================" -ForegroundColor Cyan
Write-Host "  Building GuideAnts AI" -ForegroundColor Cyan
Write-Host "============================================" -ForegroundColor Cyan
Write-Host "Backend:       $Backend"
Write-Host "Target stage:  $fullTarget"
Write-Host "Image tag:     $imageTag"
Write-Host "Deps target:   $depsTarget"
Write-Host "Rebuild base:  $RebuildBase"
if ($All) { Write-Host "All images:    Yes" }
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

$publishOutput = Join-Path $scriptAgentProject 'publish'
if (Test-Path $publishOutput) {
    Remove-Item -Path $publishOutput -Recurse -Force
}

Push-Location $scriptAgentProject
try {
    dotnet restore
    dotnet publish -c Release -o ./publish
    Write-Host "ScriptExecutionAgent built successfully." -ForegroundColor Green
}
catch {
    Write-Error "Failed to build ScriptExecutionAgent: $($_.Exception.Message)"
    exit 1
}
finally {
    Pop-Location
}

# --- Stage build context artifacts ---
$agentDest = Join-Path $buildContext 'ScriptExecutionAgent'
$reqDest = Join-Path $buildContext 'requirements.txt'

if (Test-Path $agentDest) {
    Remove-Item -Path $agentDest -Recurse -Force
}
Copy-Item -Path $publishOutput -Destination $agentDest -Recurse -Force

$torchPackages = @('torch', 'torchaudio', 'torchvision', 'torchtext')
Get-Content $requirementsSrc |
    Where-Object {
        $line = $_.Trim()
        if ($line -eq '' -or $line.StartsWith('#')) { return $true }
        $pkg = ($line -split '[=<>!~\[]')[0].Trim().ToLower()
        $pkg -notin $torchPackages
    } |
    Set-Content -Path $reqDest -Encoding UTF8

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
Write-Host "Dependency image tag: $depsTag"

try {
    $depsExists = Test-DockerImageExists -ImageTag $depsTag
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
        }
        $depsBuildArgs += @(
            '--cache-to', "type=local,dest=$depsCachePath,mode=min",
            '--target', $depsTarget,
            '-t', $depsTag,
            '-f', $dockerfilePath,
            $buildContext
        )

        docker @depsBuildArgs
        if ($LASTEXITCODE -ne 0) {
            Write-Error "Dependency image build failed with exit code $LASTEXITCODE"
            exit 1
        }
    }
    else {
        Write-Host "Reusing cached dependency image: $depsTag" -ForegroundColor Green
    }

    # --- Build final image (one Dockerfile, backend selected by target) ---
    $dockerArgs = @('buildx', 'build', '--load')
    if ($RebuildBase) {
        $dockerArgs += '--no-cache'
    }
    $dockerArgs += @(
        '--cache-from', "type=local,src=$depsCachePath",
        '--cache-from', "type=local,src=$finalCachePath",
        '--cache-to', "type=local,dest=$finalCachePath,mode=min",
        '--build-arg', "$depsImageArg=$depsTag",
        '--target', $fullTarget,
        '-t', $imageTag,
        '-f', $dockerfilePath,
        $buildContext
    )

    docker @dockerArgs
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Image build failed with exit code $LASTEXITCODE"
        exit 1
    }

    Write-Host "Image built: $imageTag" -ForegroundColor Green
}
finally {
    if (Test-Path $agentDest) {
        Remove-Item -Path $agentDest -Recurse -Force
    }
    if (Test-Path $reqDest) {
        Remove-Item -Path $reqDest -Force
    }
}

# --- Write backend-specific GuideAnts AI image tag to docker/.env ---
$envFile = Join-Path $dockerRoot '.env'
$imageEnvKey = switch ($Backend) {
    'cuda13' { 'GA_AI_CUDA_IMAGE' }
    'rocm' { 'GA_AI_ROCM_IMAGE' }
    default { 'GA_AI_CPU_IMAGE' }
}
$envLine = "$imageEnvKey=$imageTag"

if (Test-Path $envFile) {
    $lines = Get-Content $envFile
    $replaced = $false
    $lines = $lines | ForEach-Object {
        if ($_ -match "^$([regex]::Escape($imageEnvKey))=") { $replaced = $true; $envLine } else { $_ }
    }
    if (-not $replaced) { $lines += $envLine }
    Set-Content -Path $envFile -Value $lines -Encoding UTF8
}
else {
    Set-Content -Path $envFile -Value $envLine -Encoding UTF8
}
Write-Host "Wrote $envLine to $envFile" -ForegroundColor Green

# --- Additional images (only with -All) ---
if ($All) {
    $scriptAgentPublish = Join-Path $serverPath "ScriptExecutionAgent\publish"
    $plantumlContainerPath = Join-Path $PSScriptRoot "Sandboxes" "PlantUml"
    $plantumlScriptAgentPath = Join-Path $plantumlContainerPath "ScriptExecutionAgent"

    if ((Test-Path $scriptAgentPublish) -and (Test-Path $plantumlContainerPath)) {
        if (Test-Path $plantumlScriptAgentPath) {
            Remove-Item -Path $plantumlScriptAgentPath -Recurse -Force
        }
        Copy-Item -Path $scriptAgentPublish -Destination $plantumlScriptAgentPath -Recurse -Force
        Write-Host "Copied ScriptExecutionAgent to PlantUML container directory" -ForegroundColor Green
    }

    $timestamp = Get-Date -Format "yyyyMMddHHmmss"
    docker build -t plantuml-1.2025.2 -f Sandboxes/PlantUml/dockerfile --build-arg SCRIPT_AGENT_VERSION=$timestamp Sandboxes/PlantUml
    if ($LASTEXITCODE -ne 0) {
        Write-Error "PlantUML image build failed with exit code $LASTEXITCODE"
        exit 1
    }

    $mssqlBuildContext = Join-Path $PSScriptRoot "mssql-fts"
    $mssqlDockerfilePath = Join-Path $mssqlBuildContext "Dockerfile"
    if (-not (Test-Path $mssqlDockerfilePath)) {
        Write-Error "MSSQL Dockerfile not found at $mssqlDockerfilePath"
        exit 1
    }
    Write-Host "Building mssql image: mssql2025-express-fts"
    docker build -t mssql2025-express-fts -f $mssqlDockerfilePath --build-arg MSSQL_PID=Express $mssqlBuildContext
    if ($LASTEXITCODE -ne 0) {
        Write-Error "MSSQL image build failed with exit code $LASTEXITCODE"
        exit 1
    }

    $webApiUiBuildScript = Join-Path $PSScriptRoot "build_webapi_ui.ps1"
    if (-not (Test-Path $webApiUiBuildScript)) {
        Write-Error "WebAPI+UI build script not found at $webApiUiBuildScript"
        exit 1
    }

    Write-Host "Building WebAPI+UI image via build_webapi_ui.ps1" -ForegroundColor Cyan
    if ($RebuildBase) {
        & $webApiUiBuildScript -NoCache -NoRecreate
    }
    else {
        & $webApiUiBuildScript -NoRecreate
    }
}

Write-Host ""
Write-Host "============================================" -ForegroundColor Cyan
Write-Host "  Build complete: $imageTag" -ForegroundColor Cyan
Write-Host "============================================" -ForegroundColor Cyan
