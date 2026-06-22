$cpuEnv = @'
    environment:
      - DOCLING_SERVE_MAX_SYNC_WAIT=${DOCLING_SERVE_MAX_SYNC_WAIT:-600}
      - DOCLING_SERVE_MAX_FILE_SIZE=${DOCLING_SERVE_MAX_FILE_SIZE:-524288000}
      - DOCLING_SERVE_ENG_LOC_NUM_WORKERS=${DOCLING_SERVE_ENG_LOC_NUM_WORKERS:-2}
      - DOCLING_SERVE_ENG_LOC_SHARE_MODELS=${DOCLING_SERVE_ENG_LOC_SHARE_MODELS:-false}
      - DOCLING_NUM_THREADS=${DOCLING_NUM_THREADS:-4}
      - DOCLING_SERVE_LOAD_MODELS_AT_BOOT=${DOCLING_SERVE_LOAD_MODELS_AT_BOOT:-true}
      - DOCLING_SERVE_OPTIONS_CACHE_SIZE=${DOCLING_SERVE_OPTIONS_CACHE_SIZE:-2}
      - DOCLING_SERVE_LOG_LEVEL=${DOCLING_SERVE_LOG_LEVEL:-WARNING}
      - DOCLING_SERVE_LOG_FORMAT=${DOCLING_SERVE_LOG_FORMAT:-text}
      - DOCLING_SERVE_OTEL_ENABLE_METRICS=${DOCLING_SERVE_OTEL_ENABLE_METRICS:-true}
      - DOCLING_SERVE_OTEL_ENABLE_TRACES=${DOCLING_SERVE_OTEL_ENABLE_TRACES:-false}
      - DOCLING_DEBUG_PROFILE_PIPELINE_TIMINGS=${DOCLING_DEBUG_PROFILE_PIPELINE_TIMINGS:-false}
    healthcheck:
      test: ["CMD", "curl", "-f", "http://localhost:5001/version"]
      interval: 30s
      retries: 5
      start_period: 120s
      timeout: 10s
'@

$newCudaEnv = @'
    environment:
      - DOCLING_SERVE_MAX_SYNC_WAIT=${DOCLING_SERVE_MAX_SYNC_WAIT:-600}
      - DOCLING_SERVE_MAX_FILE_SIZE=${DOCLING_SERVE_MAX_FILE_SIZE:-524288000}
      - DOCLING_SERVE_ENG_LOC_NUM_WORKERS=${DOCLING_SERVE_ENG_LOC_NUM_WORKERS:-2}
      - DOCLING_SERVE_ENG_LOC_SHARE_MODELS=${DOCLING_SERVE_ENG_LOC_SHARE_MODELS:-false}
      - DOCLING_NUM_THREADS=${DOCLING_NUM_THREADS:-4}
      - DOCLING_SERVE_LOAD_MODELS_AT_BOOT=${DOCLING_SERVE_LOAD_MODELS_AT_BOOT:-true}
      - DOCLING_SERVE_OPTIONS_CACHE_SIZE=${DOCLING_SERVE_OPTIONS_CACHE_SIZE:-2}
      - DOCLING_SERVE_LOG_LEVEL=${DOCLING_SERVE_LOG_LEVEL:-WARNING}
      - DOCLING_SERVE_LOG_FORMAT=${DOCLING_SERVE_LOG_FORMAT:-text}
      - DOCLING_SERVE_OTEL_ENABLE_METRICS=${DOCLING_SERVE_OTEL_ENABLE_METRICS:-true}
      - DOCLING_SERVE_OTEL_ENABLE_TRACES=${DOCLING_SERVE_OTEL_ENABLE_TRACES:-false}
      - DOCLING_DEBUG_PROFILE_PIPELINE_TIMINGS=${DOCLING_DEBUG_PROFILE_PIPELINE_TIMINGS:-false}
      # Keep document intelligence on the same GPU visibility/order seam.
      - CUDA_VISIBLE_DEVICES=${GA_DOCLING_CUDA_VISIBLE_DEVICES:-${CUDA_VISIBLE_DEVICES:-}}
      - DOCLING_DEVICE=${DOCLING_DEVICE:-cuda}
    healthcheck:
      test: ["CMD", "curl", "-f", "http://localhost:5001/version"]
      interval: 30s
      retries: 5
      start_period: 120s
      timeout: 10s
'@

$oldCpuEnv = @'
    environment:
      - DOCLING_SERVE_MAX_SYNC_WAIT=${DOCLING_SERVE_MAX_SYNC_WAIT:-600}
'@

$oldCudaEnv = @'
    environment:
      - DOCLING_SERVE_MAX_SYNC_WAIT=${DOCLING_SERVE_MAX_SYNC_WAIT:-600}
      # Keep document intelligence on the same GPU visibility/order seam.
      - CUDA_VISIBLE_DEVICES=${CUDA_VISIBLE_DEVICES:-}
'@

$files = Get-ChildItem -Path $PSScriptRoot -Filter 'docker-compose*.yml' -File
$updated = 0
foreach ($file in $files) {
    $content = [IO.File]::ReadAllText($file.FullName)
    $original = $content
    if ($content.Contains('DOCLING_SERVE_CUDA_IMAGE') -and $content.Contains($oldCudaEnv)) {
        $content = $content.Replace($oldCudaEnv, $newCudaEnv)
    }
    elseif ($content.Contains($oldCpuEnv)) {
        $content = $content.Replace($oldCpuEnv, $cpuEnv)
    }
    if ($content -ne $original) {
        [IO.File]::WriteAllText($file.FullName, $content)
        $updated++
    }
}

Write-Output "Patched $updated compose files"
