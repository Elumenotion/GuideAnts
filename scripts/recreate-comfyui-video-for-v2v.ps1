$ErrorActionPreference = "Stop"
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$ComposeFile = Join-Path $RepoRoot "docker/compose/comfyui-video-cuda13.standalone.yml"
$EnvFile = Join-Path $RepoRoot "docker/compose/comfyui-video-local.runtime.env"
$EnvExample = Join-Path $RepoRoot "docker/compose/comfyui-video-local.runtime.env.example"

if (-not (Test-Path -LiteralPath $EnvFile -PathType Leaf)) {
    throw "Missing $EnvFile. Copy from $(Split-Path -Leaf $EnvExample) and set GA_COMFYUI_VIDEO_CUDA_VISIBLE_DEVICES=1 for the RTX 5090."
}

Write-Host "Recreating comfyui-video (env: $(Split-Path -Leaf $EnvFile), compose: cuda13 standalone)..."
docker compose --env-file $EnvFile -f $ComposeFile -p guideants-comfyui-video up -d --force-recreate comfyui-video
if ($LASTEXITCODE -ne 0) { throw "docker compose recreate failed (exit $LASTEXITCODE)" }

$deadline = (Get-Date).AddMinutes(10)
do {
    try {
        $caps = Invoke-RestMethod -Uri "http://127.0.0.1:8189/video/v1/capabilities" -TimeoutSec 5
        if ($caps.workflow_versions -contains "infinitetalk-v2v-v1") {
            Write-Host "V2V workflow is live. v2v_ready=$($caps.v2v_ready) device=$($caps.device)"
            exit 0
        }
        Write-Host "Waiting for V2V capabilities..."
    } catch {
        Write-Host "Waiting for service..."
    }
    if ((Get-Date) -ge $deadline) { throw "Timed out waiting for V2V capabilities" }
    Start-Sleep -Seconds 5
} while ($true)
