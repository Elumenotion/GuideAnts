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

$repoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$dockerRoot = Split-Path $PSScriptRoot -Parent
$serverPath = Join-Path $repoRoot 'src\server'
$buildContext = Join-Path $PSScriptRoot 'guideants-ai'
$depsCachePath = Join-Path $dockerRoot '.buildx-cache-deps'
$finalCachePath = Join-Path $dockerRoot '.buildx-cache-final'

foreach ($cachePath in @($depsCachePath, $finalCachePath)) {
    if (-not (Test-Path $cachePath)) {
        New-Item -ItemType Directory -Path $cachePath | Out-Null
    }
}

# --- Select backend ---

if ([string]::IsNullOrWhiteSpace($Backend)) {
    Write-Host "Select backend:"
    Write-Host "  1) CPU-only"
    Write-Host "  2) CUDA 13"
    Write-Host "  3) ROCm"
    $choice = Read-Host "Enter choice [1, 2, or 3]"
    switch ($choice) {
        '1' { $Backend = 'cpu' }
        '2' { $Backend = 'cuda13' }
        '3' { $Backend = 'rocm' }
        default {
            Write-Error "Invalid choice '$choice'. Valid values: 1, 2, or 3."
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
    default {
        Write-Error "Invalid backend '$Backend'. Valid values: cpu, cuda13, rocm."
        exit 1
    }
}

# Build a unique tag per build, and also maintain a stable backend-specific latest tag.
$julianDay = "$(Get-Date -Format 'yy')$((Get-Date).DayOfYear.ToString('000'))"
$timeStamp = Get-Date -Format 'HHmm'
$imageTag = "guideants-ai:${Backend}-${julianDay}.${timeStamp}"
$latestTag = "guideants-ai:${Backend}-latest"

Write-Host "============================================" -ForegroundColor Cyan
Write-Host "  Building GuideAnts AI" -ForegroundColor Cyan
Write-Host "============================================" -ForegroundColor Cyan
Write-Host "Backend:       $Backend"
Write-Host "Target stage:  $fullTarget"
Write-Host "Image tag:     $imageTag"
Write-Host "Latest tag:    $latestTag"
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
$scriptAgentHashFile = Join-Path $publishOutput '.source-hash'
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
        dotnet publish -c Release -o ./publish
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
        }
        $depsBuildArgs += @(
            '--cache-to', 'type=inline',
            '--target', $depsTarget,
            '-t', $depsTag,
            '-t', $depsCacheTag,
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
        '-t', $latestTag,
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
$envLine = "$imageEnvKey=$latestTag"

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
    $buildStateDir = Join-Path $dockerRoot '.build-state'
    if (-not (Test-Path $buildStateDir)) {
        New-Item -ItemType Directory -Path $buildStateDir | Out-Null
    }

    $scriptAgentPublish = Join-Path $serverPath "ScriptExecutionAgent\publish"
    $plantumlContainerPath = Join-Path $PSScriptRoot "Sandboxes" "PlantUml"
    $plantumlScriptAgentPath = Join-Path $plantumlContainerPath "ScriptExecutionAgent"
    $plantumlDockerfilePath = Join-Path $plantumlContainerPath "dockerfile"
    $plantumlImageTag = "plantuml-1.2025.2"
    $plantumlHashFile = Join-Path $buildStateDir "plantuml.hash"

    $plantumlInputFiles = @($plantumlDockerfilePath) + (Get-FilePathsRecursive -Root $scriptAgentPublish)
    $plantumlHash = Get-CombinedHash -Paths $plantumlInputFiles
    $plantumlCanReuse =
        (-not $RebuildBase) -and
        (Test-DockerImageExists -ImageTag $plantumlImageTag) -and
        ((Get-HashFromFile -Path $plantumlHashFile) -eq $plantumlHash)

    if ($plantumlCanReuse) {
        Write-Host "PlantUML unchanged; reusing existing image: $plantumlImageTag" -ForegroundColor Green
    }
    else {
        if ((Test-Path $scriptAgentPublish) -and (Test-Path $plantumlContainerPath)) {
            if (Test-Path $plantumlScriptAgentPath) {
                Remove-Item -Path $plantumlScriptAgentPath -Recurse -Force
            }
            Copy-Item -Path $scriptAgentPublish -Destination $plantumlScriptAgentPath -Recurse -Force
            Write-Host "Copied ScriptExecutionAgent to PlantUML container directory" -ForegroundColor Green
        }

        docker build -t $plantumlImageTag -f Sandboxes/PlantUml/dockerfile Sandboxes/PlantUml
        if ($LASTEXITCODE -ne 0) {
            Write-Error "PlantUML image build failed with exit code $LASTEXITCODE"
            exit 1
        }
        Set-Content -Path $plantumlHashFile -Value $plantumlHash -Encoding UTF8
    }

    $mssqlBuildContext = Join-Path $PSScriptRoot "mssql-fts"
    $mssqlDockerfilePath = Join-Path $mssqlBuildContext "Dockerfile"
    $mssqlImageTag = "mssql2025-express-fts"
    $mssqlHashFile = Join-Path $buildStateDir "mssql-fts.hash"
    if (-not (Test-Path $mssqlDockerfilePath)) {
        Write-Error "MSSQL Dockerfile not found at $mssqlDockerfilePath"
        exit 1
    }
    $mssqlHash = Get-CombinedHash -Paths (Get-FilePathsRecursive -Root $mssqlBuildContext)
    $mssqlCanReuse =
        (-not $RebuildBase) -and
        (Test-DockerImageExists -ImageTag $mssqlImageTag) -and
        ((Get-HashFromFile -Path $mssqlHashFile) -eq $mssqlHash)
    if ($mssqlCanReuse) {
        Write-Host "MSSQL unchanged; reusing existing image: $mssqlImageTag" -ForegroundColor Green
    }
    else {
        Write-Host "Building mssql image: $mssqlImageTag"
        docker build -t $mssqlImageTag -f $mssqlDockerfilePath --build-arg MSSQL_PID=Express $mssqlBuildContext
        if ($LASTEXITCODE -ne 0) {
            Write-Error "MSSQL image build failed with exit code $LASTEXITCODE"
            exit 1
        }
        Set-Content -Path $mssqlHashFile -Value $mssqlHash -Encoding UTF8
    }

    $searxngDockerfilePath = Join-Path $PSScriptRoot "searxng\Dockerfile"
    # Build context is the repo root; avoid Join-Path with a missing child argument.
    $searxngBuildContext = $repoRoot
    $searxngImageTag = "guideants-searxng:latest"
    $searxngHashFile = Join-Path $buildStateDir "searxng.hash"
    if (-not (Test-Path $searxngDockerfilePath)) {
        Write-Error "SearXNG Dockerfile not found at $searxngDockerfilePath"
        exit 1
    }
    $searxngInputFiles = @($searxngDockerfilePath) + (Get-FilePathsRecursive -Root (Join-Path $PSScriptRoot "searxng"))
    $searxngHash = Get-CombinedHash -Paths $searxngInputFiles
    $searxngCanReuse =
        (-not $RebuildBase) -and
        (Test-DockerImageExists -ImageTag $searxngImageTag) -and
        ((Get-HashFromFile -Path $searxngHashFile) -eq $searxngHash)
    if ($searxngCanReuse) {
        Write-Host "SearXNG unchanged; reusing existing image: $searxngImageTag" -ForegroundColor Green
    }
    else {
        Write-Host "Building searxng image: $searxngImageTag"
        docker build -t $searxngImageTag -f $searxngDockerfilePath $searxngBuildContext
        if ($LASTEXITCODE -ne 0) {
            Write-Error "SearXNG image build failed with exit code $LASTEXITCODE"
            exit 1
        }
        Set-Content -Path $searxngHashFile -Value $searxngHash -Encoding UTF8
    }

    $webApiUiBuildScript = Join-Path $PSScriptRoot "build_webapi_ui.ps1"
    $webApiUiHashFile = Join-Path $buildStateDir "webapi-ui.hash"
    $envFile = Join-Path $dockerRoot '.env'
    if (-not (Test-Path $webApiUiBuildScript)) {
        Write-Error "WebAPI+UI build script not found at $webApiUiBuildScript"
        exit 1
    }

    $webApiUiInputs = @(
        $webApiUiBuildScript,
        (Join-Path $PSScriptRoot "webapi-ui\Dockerfile")
    ) + (Get-FilePathsRecursive -Root (Join-Path $repoRoot "src\client")) + (Get-FilePathsRecursive -Root (Join-Path $repoRoot "src\server"))
    $webApiUiHash = Get-CombinedHash -Paths $webApiUiInputs

    $existingWebApiUiImage = $null
    if (Test-Path $envFile) {
        $line = Get-Content -Path $envFile | Where-Object { $_ -match '^GA_WEBAPI_UI_IMAGE=' } | Select-Object -First 1
        if ($line) {
            $existingWebApiUiImage = ($line -split '=', 2)[1].Trim()
        }
    }
    $webApiUiCanReuse =
        (-not $RebuildBase) -and
        (-not [string]::IsNullOrWhiteSpace($existingWebApiUiImage)) -and
        (Test-DockerImageExists -ImageTag $existingWebApiUiImage) -and
        ((Get-HashFromFile -Path $webApiUiHashFile) -eq $webApiUiHash)

    if ($webApiUiCanReuse) {
        Write-Host "WebAPI+UI unchanged; reusing existing image: $existingWebApiUiImage" -ForegroundColor Green
    }
    else {
        Write-Host "Building WebAPI+UI image via build_webapi_ui.ps1" -ForegroundColor Cyan
        if ($RebuildBase) {
            & $webApiUiBuildScript -NoCache -NoRecreate
        }
        else {
            & $webApiUiBuildScript -NoRecreate
        }

        if ($LASTEXITCODE -ne 0) {
            Write-Error "WebAPI+UI image build failed with exit code $LASTEXITCODE"
            exit 1
        }

        Set-Content -Path $webApiUiHashFile -Value $webApiUiHash -Encoding UTF8
    }
}

Write-Host ""
Write-Host "============================================" -ForegroundColor Cyan
Write-Host "  Build complete: $imageTag" -ForegroundColor Cyan
Write-Host "  Updated latest: $latestTag" -ForegroundColor Cyan
Write-Host "============================================" -ForegroundColor Cyan
