param(
    [ValidateSet('cuda13', 'rocm')]
    [string]$Backend = 'cuda13',
    [switch]$RebuildBase,
    [switch]$RunSmokeTests,
    [string]$CudaVisibleDevices = $env:GA_COMFYUI_VIDEO_CUDA_VISIBLE_DEVICES,
    [string]$HipVisibleDevices = $env:GA_COMFYUI_VIDEO_HIP_VISIBLE_DEVICES
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

$backendConfig = @{
    cuda13 = @{
        Dockerfile = 'Dockerfile.cuda'
        LockFile = 'source-lock.json'
        Constraints = 'constraints\cuda13.txt'
        DepsTarget = 'deps-cuda13'
        FinalTarget = 'final-cuda13'
        DepsPrefix = 'guideants-comfyui-deps:cuda13'
        ImageTag = 'guideants-comfyui:cuda13-latest'
        ExpectedTorch = '2.11.0+cu130'
    }
    rocm = @{
        Dockerfile = 'Dockerfile.rocm'
        LockFile = 'source-lock-rocm.json'
        Constraints = 'constraints\therock-gfx1151.txt'
        DepsTarget = 'deps-rocm'
        FinalTarget = 'final-rocm'
        DepsPrefix = 'guideants-comfyui-deps:rocm'
        ImageTag = 'guideants-comfyui:rocm-latest'
        ExpectedTorch = '2.11.0a0+rocm7.11.0a20260106'
    }
}[$Backend]

$dockerfile = Join-Path $context $backendConfig.Dockerfile
$lockPath = Join-Path $context $backendConfig.LockFile

New-Item -ItemType Directory -Force $state | Out-Null

$lock = Get-Content $lockPath -Raw | ConvertFrom-Json
if ($lock.baseImage.reference -notmatch '@sha256:[0-9a-f]{64}$') {
    throw "$($backendConfig.LockFile) base image is unresolved; release builds require a verified digest"
}
$referenceDigest = ($lock.baseImage.reference -split '@', 2)[1]
if ($referenceDigest -ne $lock.baseImage.platformDigest) {
    throw "$($backendConfig.LockFile) base image reference must use the linux/amd64 platform digest"
}
if ($lock.pytorch.version -ne $backendConfig.ExpectedTorch -or $lock.pytorch.attention -ne 'sdpa') {
    throw "$($backendConfig.LockFile) must select $($backendConfig.ExpectedTorch) and SDPA"
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
        (Join-Path $context $backendConfig.Constraints),
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
    $depsTag = "$($backendConfig.DepsPrefix)-$($depsHash.Substring(0, 12))"
    $depsCacheTag = "$($backendConfig.DepsPrefix)-cache"
    $imageTag = $backendConfig.ImageTag

    $prevEap = $ErrorActionPreference
    $ErrorActionPreference = 'SilentlyContinue'
    docker image inspect $depsTag | Out-Null
    $depsMissing = ($LASTEXITCODE -ne 0)
    $ErrorActionPreference = $prevEap
    if ($RebuildBase -or $depsMissing) {
        $dockerArgs = @('buildx', 'build', '--load', '--target', $backendConfig.DepsTarget,
            '-t', $depsTag, '-t', $depsCacheTag, '-f', $dockerfile)
        if ($RebuildBase) { $dockerArgs += '--no-cache' }
        $dockerArgs += $context
        docker @dockerArgs
        if ($LASTEXITCODE -ne 0) { throw "$Backend dependency image build failed" }
    }

    docker buildx build --load --target $backendConfig.FinalTarget `
        --build-arg "GA_COMFYUI_VIDEO_DEPS_IMAGE=$depsTag" `
        --cache-from $depsCacheTag -t $imageTag -f $dockerfile $context
    if ($LASTEXITCODE -ne 0) { throw "$Backend image build failed" }

    if ($RunSmokeTests) {
        if ($Backend -eq 'cuda13') {
            if ([string]::IsNullOrWhiteSpace($CudaVisibleDevices)) {
                throw 'RunSmokeTests requires -CudaVisibleDevices or GA_COMFYUI_VIDEO_CUDA_VISIBLE_DEVICES'
            }
            if ($CudaVisibleDevices.Contains(',')) {
                throw 'CudaVisibleDevices must select exactly one GPU UUID or index'
            }
            docker run --rm --gpus all `
                --env "CUDA_VISIBLE_DEVICES=$CudaVisibleDevices" `
                --env "VIDEO_GPU_BACKEND=cuda13" `
                --entrypoint python $imageTag `
                /opt/guideants/comfyui-video/scripts/verify-install.py
        }
        else {
            if ([string]::IsNullOrWhiteSpace($HipVisibleDevices)) { $HipVisibleDevices = '0' }
            if ($HipVisibleDevices.Contains(',')) {
                throw 'HipVisibleDevices must select exactly one GPU index'
            }
            $libRocdxg = Join-Path $repoRoot 'docker\volumes\rocm-wsl\lib\librocdxg.so'
            if (-not (Test-Path -LiteralPath $libRocdxg)) {
                $libRocdxg = Join-Path $repoRoot 'installer\docker\volumes\rocm-wsl\lib\librocdxg.so'
            }
            if (-not (Test-Path -LiteralPath $libRocdxg)) {
                throw "ROCm smoke tests require staged librocdxg at docker/volumes/rocm-wsl/lib/librocdxg.so"
            }
            docker run --rm `
                --device /dev/dxg `
                --cap-add SYS_PTRACE `
                --security-opt seccomp=unconfined `
                -v /usr/lib/wsl/lib/libdxcore.so:/usr/lib/libdxcore.so:ro `
                -v "${libRocdxg}:/lib/librocdxg.so:ro" `
                -v "${libRocdxg}:/usr/lib/librocdxg.so:ro" `
                --env "HIP_VISIBLE_DEVICES=$HipVisibleDevices" `
                --env "HSA_ENABLE_DXG_DETECTION=1" `
                --env "VIDEO_GPU_BACKEND=rocm" `
                --env "LD_LIBRARY_PATH=/opt/rocm/lib" `
                --entrypoint python $imageTag `
                /opt/guideants/comfyui-video/scripts/verify-install.py
        }
        if ($LASTEXITCODE -ne 0) { throw "$Backend image smoke validation failed" }
    }
    Write-Host "Built $imageTag (dependencies: $depsTag)" -ForegroundColor Green
}
finally {
    Remove-Item $agentDest -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item $execDest -Recurse -Force -ErrorAction SilentlyContinue
}
