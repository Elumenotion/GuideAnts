[CmdletBinding()]
param(
    [string]$ModelsVolume = "compose_comfyui_video_models",
    [string]$ComfySource = "C:\models\comfyui",
    [string]$MoveScript = ""
)

$ErrorActionPreference = "Stop"
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if ([string]::IsNullOrWhiteSpace($MoveScript)) {
    $MoveScript = Join-Path $PSScriptRoot "move-host-models-to-volume.sh"
}
if (-not (Test-Path -LiteralPath $MoveScript)) {
    throw "Move script not found: $MoveScript"
}
if (-not (Test-Path -LiteralPath $ComfySource -PathType Container)) {
    throw "Required source directory is missing: $ComfySource"
}
docker volume inspect $ModelsVolume | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "Docker volume not found: $ModelsVolume"
}

$transcript = Join-Path $RepoRoot "artifacts\infinitetalk\move-host-models-$(Get-Date -Format yyyyMMdd-HHmmss).log"
New-Item -ItemType Directory -Force -Path (Split-Path $transcript) | Out-Null
Write-Host "Logging to $transcript"

$dockerArgs = @(
    "run", "--rm",
    "-v", "${ModelsVolume}:/models",
    "-v", "${ComfySource}:/src/comfyui",
    "-v", "${MoveScript}:/move.sh:ro",
    "alpine:3.20",
    "sh", "/move.sh"
)
Write-Host "> docker $($dockerArgs -join ' ')"
& docker @dockerArgs 2>&1 | Tee-Object -FilePath $transcript
if ($LASTEXITCODE -ne 0) {
    throw "Model move failed. See $transcript"
}

$hostComfyFiles = @(Get-ChildItem -LiteralPath $ComfySource -Recurse -File -ErrorAction SilentlyContinue)
if ($hostComfyFiles.Count -gt 0) {
    throw "AC1 failed: $($hostComfyFiles.Count) files remain under $ComfySource"
}

Write-Host "AC1 move verified: comfyui host source cleared, models volume populated."
