param(
    [switch]$RebuildBase,
    [switch]$RunSmokeTests,
    [string]$CudaVisibleDevices = $env:GA_COMFYUI_VIDEO_CUDA_VISIBLE_DEVICES
)

$ErrorActionPreference = 'Stop'
$env:DOCKER_BUILDKIT = '1'
$repoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$context = Join-Path $PSScriptRoot 'comfyui-video'
$state = Join-Path (Split-Path $PSScriptRoot -Parent) '.build-state'
$publish = Join-Path $state 'scriptexecutionagent-comfyui-video-publish'
$agentProject = Join-Path $repoRoot 'src\server\ScriptExecutionAgent'
$agentDest = Join-Path $context 'ScriptExecutionAgent'
$execDest = Join-Path $context 'script-agent-exec'
$execSource = Join-Path $PSScriptRoot 'guideants-ai\script-agent-exec\ga-script-exec.c'
$dockerfile = Join-Path $context 'Dockerfile.cuda'
$lockPath = Join-Path $context 'source-lock.json'

New-Item -ItemType Directory -Force $state | Out-Null

$lock = Get-Content $lockPath -Raw | ConvertFrom-Json
if ($lock.baseImage.reference -notmatch '@sha256:[0-9a-f]{64}$') {
    throw 'source-lock.json base image is unresolved; release builds require a verified digest'
}
$referenceDigest = ($lock.baseImage.reference -split '@', 2)[1]
if ($referenceDigest -ne $lock.baseImage.platformDigest) {
    throw 'source-lock.json base image reference must use the linux/amd64 platform digest'
}
if ($lock.pytorch.version -ne '2.11.0+cu130' -or $lock.pytorch.attention -ne 'sdpa') {
    throw 'source-lock.json must select torch 2.11.0+cu130 and SDPA'
}
if (-not (Test-Path -LiteralPath $execSource -PathType Leaf)) {
    throw "ga-script-exec source not found: $execSource"
}

dotnet restore $agentProject
dotnet publish $agentProject -c Release -o $publish --no-restore
if ($LASTEXITCODE -ne 0) { throw 'ScriptExecutionAgent publish failed' }

try {
    Remove-Item $agentDest -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item $execDest -Recurse -Force -ErrorAction SilentlyContinue
    Copy-Item $publish $agentDest -Recurse
    New-Item -ItemType Directory -Force $execDest | Out-Null
    Copy-Item $execSource $execDest

    $hashFiles = @(
        $dockerfile,
        (Join-Path $context 'constraints\common.txt'),
        (Join-Path $context 'constraints\cuda13.txt'),
        $lockPath
    )
    $hashText = ($hashFiles | ForEach-Object {
        "$($_)|$((Get-FileHash $_ -Algorithm SHA256).Hash.ToLowerInvariant())"
    }) -join "`n"
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $depsHash = -join ($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($hashText)) |
            ForEach-Object { $_.ToString('x2') })
    } finally { $sha.Dispose() }
    $depsTag = "guideants-comfyui-deps:cuda13-$($depsHash.Substring(0, 12))"
    $depsCacheTag = 'guideants-comfyui-deps:cuda13-cache'
    $imageTag = 'guideants-comfyui:cuda13-latest'

    docker image inspect $depsTag *> $null
    if ($RebuildBase -or $LASTEXITCODE -ne 0) {
        $dockerArgs = @('buildx', 'build', '--load', '--target', 'deps-cuda13',
            '-t', $depsTag, '-t', $depsCacheTag, '-f', $dockerfile)
        if ($RebuildBase) { $dockerArgs += '--no-cache' }
        $dockerArgs += $context
        docker @dockerArgs
        if ($LASTEXITCODE -ne 0) { throw 'CUDA dependency image build failed' }
    }

    docker buildx build --load --target final-cuda13 `
        --build-arg "GA_COMFYUI_VIDEO_DEPS_IMAGE=$depsTag" `
        --cache-from $depsCacheTag -t $imageTag -f $dockerfile $context
    if ($LASTEXITCODE -ne 0) { throw 'CUDA image build failed' }

    if ($RunSmokeTests) {
        if ([string]::IsNullOrWhiteSpace($CudaVisibleDevices)) {
            throw 'RunSmokeTests requires -CudaVisibleDevices or GA_COMFYUI_VIDEO_CUDA_VISIBLE_DEVICES'
        }
        if ($CudaVisibleDevices.Contains(',')) {
            throw 'CudaVisibleDevices must select exactly one GPU UUID or index'
        }
        docker run --rm --gpus all `
            --env "CUDA_VISIBLE_DEVICES=$CudaVisibleDevices" `
            --entrypoint python $imageTag `
            /opt/guideants/comfyui-video/scripts/verify-install.py
        if ($LASTEXITCODE -ne 0) { throw 'CUDA image smoke validation failed' }
    }
    Write-Host "Built $imageTag (dependencies: $depsTag)" -ForegroundColor Green
}
finally {
    Remove-Item $agentDest -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item $execDest -Recurse -Force -ErrorAction SilentlyContinue
}
