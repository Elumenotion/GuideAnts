param(
    [string]$ContainerName = 'guideants-ai',
    [string]$Image,
    # Name of the single named Docker volume that holds every local AI
    # model (llama GGUFs, ASR, SD bundles, TTS weights, embeddings).
    # Must be populated before running — see
    # docker/scripts/migrate-local-models-to-single-volume.ps1.
    [string]$LocalModelsVolume = 'ai_local_models',
    [string]$ContentFilesDir,
    [int]$GatewayPort = 8110,
    [int]$ContextSize = 262144,
    [int]$Threads = 16,
    [int]$Parallel = 8,
    [int]$CacheRamMiB = 8192,
    [int]$GpuLayers = 999,
    [int]$ModelsMax = 1,
    [string]$CudaVisibleDevices,
    [string]$LlamaCudaVisibleDevices,
    [string]$LlamaTensorSplit,
    [string]$AsrCudaVisibleDevices,
    [string]$TtsCudaVisibleDevices,
    [string]$EmbCudaVisibleDevices,
    [string]$SdCudaVisibleDevices,
    [string]$EmbDevice,
    [string]$EmbTargetDevices,
    [switch]$NoContBatching,
    [switch]$UseMmap
)

$ErrorActionPreference = 'Stop'

$dockerRoot     = Split-Path -Parent $PSScriptRoot
$repoDockerRoot = Split-Path -Parent $dockerRoot

function Resolve-OptionalValue {
    param(
        [string]$ProvidedValue,
        [string]$EnvName,
        [string]$DefaultValue = ''
    )

    if (-not [string]::IsNullOrWhiteSpace($ProvidedValue)) {
        return $ProvidedValue.Trim()
    }

    $envValue = [Environment]::GetEnvironmentVariable($EnvName)
    if (-not [string]::IsNullOrWhiteSpace($envValue)) {
        return $envValue.Trim()
    }

    return $DefaultValue
}

# Resolve image tag: explicit param > docker/.env > error
if ([string]::IsNullOrWhiteSpace($Image)) {
    $envPath = Join-Path $repoDockerRoot '.env'
    if (Test-Path $envPath) {
        $envLine = Get-Content $envPath | Where-Object { $_ -match '^GA_AI_CUDA_IMAGE=' }
        if (-not $envLine) {
            $envLine = Get-Content $envPath | Where-Object { $_ -match '^GA_AI_IMAGE=' }
        }
        if ($envLine) { $Image = ($envLine -split '=', 2)[1].Trim() }
    }
}
if ([string]::IsNullOrWhiteSpace($Image)) {
    Write-Error "No image specified. Pass -Image or run build_guideants_ai.ps1 first to generate docker/.env."
    exit 1
}

$CudaVisibleDevices = Resolve-OptionalValue -ProvidedValue $CudaVisibleDevices -EnvName 'CUDA_VISIBLE_DEVICES'
$LlamaCudaVisibleDevices = Resolve-OptionalValue -ProvidedValue $LlamaCudaVisibleDevices -EnvName 'GA_LLAMA_CUDA_VISIBLE_DEVICES'
$LlamaTensorSplit = Resolve-OptionalValue -ProvidedValue $LlamaTensorSplit -EnvName 'GA_LLAMA_TENSOR_SPLIT'
$AsrCudaVisibleDevices = Resolve-OptionalValue -ProvidedValue $AsrCudaVisibleDevices -EnvName 'GA_ASR_CUDA_VISIBLE_DEVICES'
$TtsCudaVisibleDevices = Resolve-OptionalValue -ProvidedValue $TtsCudaVisibleDevices -EnvName 'GA_TTS_CUDA_VISIBLE_DEVICES'
$EmbCudaVisibleDevices = Resolve-OptionalValue -ProvidedValue $EmbCudaVisibleDevices -EnvName 'GA_EMB_CUDA_VISIBLE_DEVICES'
$SdCudaVisibleDevices = Resolve-OptionalValue -ProvidedValue $SdCudaVisibleDevices -EnvName 'GA_SD_CUDA_VISIBLE_DEVICES'
$EmbDevice = Resolve-OptionalValue -ProvidedValue $EmbDevice -EnvName 'GA_EMB_DEVICE' -DefaultValue 'cuda'
$EmbTargetDevices = Resolve-OptionalValue -ProvidedValue $EmbTargetDevices -EnvName 'GA_EMB_TARGET_DEVICES' -DefaultValue 'cuda:0,cuda:1'

$defaultContentDir = Join-Path $repoDockerRoot 'volumes\content-files'
$contentDir = if ([string]::IsNullOrWhiteSpace($ContentFilesDir)) { $defaultContentDir } else { $ContentFilesDir }

# Named volume must already exist and contain the llama/, asr/, sd/,
# tts/, emb/ subdirs. Refuse to start otherwise — an empty mount would
# silently bring the stack up with every model missing.
$volumeExists = (docker volume ls --format '{{.Name}}' | Where-Object { $_.Trim() -eq $LocalModelsVolume }).Count -gt 0
if (-not $volumeExists) {
    Write-Error "Docker volume '$LocalModelsVolume' does not exist. Create + populate it via docker/scripts/migrate-local-models-to-single-volume.ps1 before running this script."
    exit 1
}

$existing = docker ps -a --format '{{.Names}}' | Where-Object { $_ -eq $ContainerName }
if ($existing) {
    docker rm -f $ContainerName | Out-Null
}

$dockerArgs = @(
    'run', '--detach',
    '--name', $ContainerName,
    '--gpus', 'all',
    '--restart', 'unless-stopped',
    '--publish', ("{0}:80" -f $GatewayPort),
    '--volume', ("{0}:/models-local" -f $LocalModelsVolume),
    '--volume', ("{0}:/app/ContentFiles" -f $contentDir),
    '-e', 'FILE_STORAGE_ROOT=/app/ContentFiles',
    # Router aliases live on the named volume (see entrypoint.sh seed on first boot).
    '-e', 'GA_LLAMA_MODELS_PRESET=/models-local/router-models.ini',
    '-e', 'GA_LLAMA_MODEL_DIR=/models-local/llama',
    '-e', "GA_LLAMA_MODELS_MAX=$ModelsMax",
    '-e', 'GA_LLAMA_NO_AUTOLOAD=1',
    '-e', "GA_LLAMA_CTX_SIZE=$ContextSize",
    '-e', "GA_LLAMA_THREADS=$Threads",
    '-e', "GA_LLAMA_PARALLEL=$Parallel",
    '-e', "GA_LLAMA_CACHE_RAM=$CacheRamMiB",
    '-e', "GA_LLAMA_GPU_LAYERS=$GpuLayers",
    '-e', 'GA_LLAMA_KV_UNIFIED=1',
    '-e', 'GA_LLAMA_JINJA=1',
    # Global router knobs (propagate to every spawned child instance).
    # q8_0 KV cache halves KV VRAM (requires flash attention enabled);
    # image-min-tokens >=1024 is needed for Qwen-VL grounding accuracy.
    '-e', 'GA_LLAMA_FLASH_ATTN=on',
    '-e', 'GA_LLAMA_CACHE_TYPE_K=q8_0',
    '-e', 'GA_LLAMA_CACHE_TYPE_V=q8_0',
    '-e', 'GA_LLAMA_IMAGE_MIN_TOKENS=1024',
    # Per-GPU layer split (comma list, e.g. "7,1"); empty => free-VRAM heuristic.
    '-e', "GA_LLAMA_TENSOR_SPLIT=$LlamaTensorSplit",
    '-e', "GA_LLAMA_CUDA_VISIBLE_DEVICES=$LlamaCudaVisibleDevices",
    '-e', 'GA_ASR_HOST=127.0.0.1',
    '-e', 'GA_ASR_PORT=8082',
    '-e', 'GA_ASR_MODEL_DIR=/models-local/asr',
    '-e', 'GA_ASR_DEFAULT_MODEL_PATH=Qwen3-ASR-0.6B',
    '-e', 'GA_ASR_DEFAULT_MODEL_ID=Qwen/Qwen3-ASR-0.6B',
    '-e', 'GA_ASR_AUTO_LOAD_ON_STARTUP=1',
    '-e', 'GA_ASR_DEVICE_MAP=auto',
    '-e', 'GA_ASR_BACKEND=cuda',
    '-e', "GA_ASR_CUDA_VISIBLE_DEVICES=$AsrCudaVisibleDevices",
    '-e', 'GA_ASR_DTYPE=bfloat16',
    '-e', 'GA_ASR_MAX_INFERENCE_BATCH_SIZE=8',
    '-e', 'GA_ASR_MAX_NEW_TOKENS=512',
    '-e', 'GA_TTS_HOST=127.0.0.1',
    '-e', 'GA_TTS_PORT=8084',
    '-e', 'GA_TTS_MODEL_DIR=/models-local/tts',
    '-e', 'GA_TTS_DEFAULT_MODEL_PATH=chatterbox',
    '-e', 'GA_TTS_DEFAULT_MODEL_ID=chatterbox',
    '-e', 'GA_TTS_AUTO_LOAD_ON_STARTUP=0',
    '-e', 'GA_TTS_DEVICE_MAP=auto',
    '-e', 'GA_TTS_BACKEND=cuda',
    '-e', "GA_TTS_CUDA_VISIBLE_DEVICES=$TtsCudaVisibleDevices",
    '-e', 'GA_TTS_DTYPE=float32',
    '-e', 'GA_TTS_TIMEOUT_SECONDS=300',
    '-e', 'GA_TTS_MAX_NEW_TOKENS=512',
    '-e', 'GA_TTS_SAMPLE_RATE=24000',
    '-e', 'GA_TTS_VOICE=en_us_cv_001',
    '-e', 'GA_TTS_LANG_CODE=a',
    '-e', 'GA_TTS_SPEED=1.0',
    '-e', 'GA_EMB_HOST=127.0.0.1',
    '-e', 'GA_EMB_PORT=8085',
    '-e', 'GA_EMB_MODEL_DIR=/models-local/emb',
    '-e', 'GA_EMB_DEFAULT_MODEL_PATH=',
    '-e', "GA_EMB_DEVICE=$EmbDevice",
    '-e', "GA_EMB_TARGET_DEVICES=$EmbTargetDevices",
    '-e', "GA_EMB_CUDA_VISIBLE_DEVICES=$EmbCudaVisibleDevices",
    '-e', 'GA_EMB_AUTO_LOAD_ON_STARTUP=0',
    '-e', 'GA_EMB_WARMUP_ON_LOAD=1',
    '-e', 'GA_EMB_WAIT_FOR_READY_ON_STARTUP=0',
    '-e', 'GA_EMB_READY_TIMEOUT_SECONDS=1800',
    '-e', 'GA_SD_HOST=127.0.0.1',
    '-e', 'GA_SD_PORT=8083',
    '-e', 'GA_SD_MODEL_DIR=/models-local/sd',
    # Diffusion / VAE / text-encoder files are resolved from the SD
    # service's active bundle under /models-local/sd/bundles/<id>/.
    # No GA_SD_DIFFUSION_MODEL_PATH/... env seam: bundle = single source
    # of truth.
    '-e', 'GA_SD_TIMEOUT_SECONDS=900',
    '-e', 'GA_SD_ENGINE_REQUEST_TIMEOUT_SECONDS=120',
    '-e', 'GA_SD_STEPS=4',
    '-e', 'GA_SD_CFG_SCALE=1.0',
    '-e', 'GA_SD_STRENGTH=0.75',
    '-e', 'GA_SD_OFFLOAD_TO_CPU=0',
    '-e', 'GA_SD_DIFFUSION_FA=1',
    '-e', 'GA_SD_AUTO_LOAD_ON_STARTUP=1',
    '-e', 'GA_SD_WARMUP_PROMPT=startup-warmup',
    '-e', 'GA_SD_WARMUP_SIZE=512x512',
    '-e', 'GA_SD_WARMUP_STEPS=1',
    '-e', 'GA_SD_WARMUP_OUTPUT_FORMAT=png',
    '-e', 'GA_SD_WARMUP_REQUEST_TIMEOUT_SECONDS=180',
    '-e', 'GA_SD_WARMUP_FAIL_OPEN_ON_STARTUP=1',
    '-e', 'GA_SD_WAIT_FOR_READY_ON_STARTUP=0',
    '-e', 'GA_SD_READY_TIMEOUT_SECONDS=1800',
    '-e', "GA_SD_CUDA_VISIBLE_DEVICES=$SdCudaVisibleDevices"
)

if (-not [string]::IsNullOrWhiteSpace($CudaVisibleDevices)) {
    $dockerArgs += @('-e', "CUDA_VISIBLE_DEVICES=$CudaVisibleDevices")
}

if (-not $NoContBatching) {
    $dockerArgs += @('-e', 'GA_LLAMA_CONT_BATCH=1')
}

if (-not $UseMmap) {
    $dockerArgs += @('-e', 'GA_LLAMA_NO_MMAP=1')
}

$dockerArgs += $Image

docker @dockerArgs
Start-Sleep -Seconds 3
docker ps --filter ("name={0}" -f $ContainerName) --format "table {{.Names}}`t{{.Status}}`t{{.Ports}}"
