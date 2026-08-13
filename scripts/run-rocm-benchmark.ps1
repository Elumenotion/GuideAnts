[CmdletBinding()]
param(
    [string]$VideoHost = "http://127.0.0.1:8189",
    [string]$ScriptAgentToken = "local-script-agent-test-token",
    [string]$ContentFilesRoot = "artifacts",
    [string]$ArtifactsRoot = "artifacts/infinitetalk",
    [string]$AudioFile = "voice-10s.wav",
    [string]$OutputName = "",
    [string]$ExistingJobId = "",
    [int]$Width = 832,
    [int]$Height = 480,
    [int]$Steps = 14,
    [double]$Cfg = 5.0,
    [switch]$FullRun,
    [int]$TimeoutHours = 6,
    [int]$PollSeconds = 10
)

$ErrorActionPreference = "Stop"
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$NotebookRoot = Join-Path (Resolve-Path (Join-Path $RepoRoot $ContentFilesRoot)) "acceptance-project/authorized-notebook"
$InputDir = Join-Path $NotebookRoot "Input"
$ArtifactDir = Join-Path $RepoRoot $ArtifactsRoot
$AudioPath = Join-Path $InputDir $AudioFile
$LogPath = Join-Path $ArtifactDir ("rocm-benchmark-{0:yyyyMMdd-HHmmss}.log" -f (Get-Date))

if (-not (Test-Path -LiteralPath $AudioPath)) {
    throw "Missing benchmark audio: $AudioPath"
}
if ([string]::IsNullOrWhiteSpace($OutputName)) {
    $audioStem = [IO.Path]::GetFileNameWithoutExtension($AudioFile)
    $OutputName = "rocm-benchmark-${Width}x${Height}-${audioStem}.mp4"
}
if ($OutputName -notmatch '^[A-Za-z0-9._-]+\.mp4$') {
    throw "OutputName must be a filename ending in .mp4"
}

$duration = [double](& ffprobe -v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 $AudioPath)
"audio_file=$AudioPath" | Add-Content -LiteralPath $LogPath
"audio_duration_seconds=$duration" | Add-Content -LiteralPath $LogPath

docker exec compose-comfyui-video-1 printenv TORCH_ROCM_AOTRITON_ENABLE_EXPERIMENTAL HSA_ENABLE_SDMA HSA_USE_SVM PYTORCH_TUNABLEOP_ENABLED VIDEO_WORKFLOW_PATH |
    Add-Content -LiteralPath $LogPath

$submitScript = @"
from guideants_video_client import submit_talking_head, get_talking_head_job
import json
result = submit_talking_head(
    image_path='../Input/avatar.png',
    audio_path='../Input/$AudioFile',
    output_filename='$OutputName',
    parameters={'width': $Width, 'height': $Height, 'steps': $Steps, 'cfg': $Cfg},
)
print(json.dumps(result, separators=(',', ':')))
"@

$payload = @{
    script = $submitScript
    scriptType = "Python"
    workingDirectory = "/app/ContentFiles/acceptance-project/authorized-notebook/Output"
    projectId = "11111111-1111-1111-1111-111111111111"
    notebookId = "22222222-2222-2222-2222-222222222222"
    guideId = "33333333-3333-3333-3333-333333333333"
    timeoutSeconds = 7200
} | ConvertTo-Json -Compress

$payloadPath = Join-Path $ArtifactDir "rocm-benchmark-submit.json"
[System.IO.File]::WriteAllText($payloadPath, $payload, (New-Object System.Text.UTF8Encoding $false))

if ([string]::IsNullOrWhiteSpace($ExistingJobId)) {
    docker exec compose-comfyui-video-1 curl -s -X POST http://127.0.0.1:8188/interrupt | Out-Null
    Add-Content -LiteralPath $LogPath -Value "queue interrupted before submit"

    $submittedAt = Get-Date
    $submitResponse = curl.exe --fail --silent --show-error --max-time 180 -H "X-Script-Agent-Token: $ScriptAgentToken" -H "Content-Type: application/json" --data-binary "@$payloadPath" "$VideoHost/sandbox/execute"
    if ($LASTEXITCODE -ne 0) { throw "Benchmark submit request failed (curl exit $LASTEXITCODE)" }
    $submitResponse | Add-Content -LiteralPath $LogPath
    $sandbox = $submitResponse | ConvertFrom-Json
    if ([int]$sandbox.ExitCode -ne 0) { throw "Benchmark submit script failed: $($sandbox.StandardError)" }
    $submit = $sandbox.StandardOutput | ConvertFrom-Json
    $jobId = $submit.jobId
    if ([string]::IsNullOrWhiteSpace($jobId)) { throw "Benchmark submit returned no job ID" }
} else {
    $jobId = $ExistingJobId
    $initialStatusText = docker exec compose-comfyui-video-1 curl -s -m 10 "http://127.0.0.1:8190/v1/talking-head/jobs/$jobId"
    if ($LASTEXITCODE -ne 0) { throw "Existing benchmark job is unavailable: $jobId" }
    $initialStatus = $initialStatusText | ConvertFrom-Json
    $submittedAt = [DateTimeOffset]::FromUnixTimeMilliseconds([long]([double]$initialStatus.created_at * 1000)).LocalDateTime
    Add-Content -LiteralPath $LogPath -Value "attached_to_existing_job=true"
}
"job_id=$jobId" | Add-Content -LiteralPath $LogPath
Write-Host "job_id=$jobId"

$statusScriptTemplate = @'
from guideants_video_client import get_talking_head_job
import json
print(json.dumps(get_talking_head_job("{0}"), separators=(",", ":")))
'@

$seenSteps = @{}
$deadline = (Get-Date).AddHours($TimeoutHours)
do {
    $statusScript = $statusScriptTemplate -f $jobId
    $statusPayload = @{
        script = $statusScript
        scriptType = "Python"
        workingDirectory = "/app/ContentFiles/acceptance-project/authorized-notebook/Output"
        projectId = "11111111-1111-1111-1111-111111111111"
        notebookId = "22222222-2222-2222-2222-222222222222"
        guideId = "33333333-3333-3333-3333-333333333333"
        timeoutSeconds = 120
    } | ConvertTo-Json -Compress
    $statusPayloadPath = Join-Path $ArtifactDir "rocm-benchmark-status.json"
    [System.IO.File]::WriteAllText($statusPayloadPath, $statusPayload, (New-Object System.Text.UTF8Encoding $false))
    $statusResponse = curl.exe --fail --silent --show-error --max-time 180 -H "X-Script-Agent-Token: $ScriptAgentToken" -H "Content-Type: application/json" --data-binary "@$statusPayloadPath" "$VideoHost/sandbox/execute"
    if ($LASTEXITCODE -ne 0) { throw "Benchmark status request failed (curl exit $LASTEXITCODE)" }
    $status = ($statusResponse | ConvertFrom-Json).StandardOutput | ConvertFrom-Json
    $progress = $status.progress
    $now = Get-Date
    $elapsedSeconds = [math]::Round(($now - $submittedAt).TotalSeconds, 1)
    $statsText = docker exec compose-comfyui-video-1 curl -s -m 10 http://127.0.0.1:8188/system_stats
    if ($LASTEXITCODE -ne 0) { throw "ComfyUI system_stats failed during benchmark" }
    $device = ($statsText | ConvertFrom-Json).devices[0]
    $torchVramGb = [math]::Round(([double]$device.torch_vram_total / 1GB), 2)
    $vramFreeGb = [math]::Round(([double]$device.vram_free / 1GB), 2)
    $line = "{0:u} elapsed_s={1} state={2} phase={3} node={4} step={5}/{6} torch_vram_gb={7} vram_free_gb={8} msg={9}" -f $now, $elapsedSeconds, $status.state, $progress.phase, $progress.node_class, $progress.step, $progress.max_steps, $torchVramGb, $vramFreeGb, $progress.message
    Add-Content -LiteralPath $LogPath -Value $line
    Write-Host $line
    if ($progress.node_class -eq "WanVideoSampler" -and $null -ne $progress.step -and -not $seenSteps.ContainsKey([string]$progress.step)) {
        $seenSteps[[string]$progress.step] = Get-Date
    }
    if ($status.state -eq "completed") { break }
    if ($status.state -in @("failed", "cancelled")) { throw "Benchmark job ended in state $($status.state)" }
    if (-not $FullRun -and $seenSteps.ContainsKey("1") -and $seenSteps.ContainsKey("2")) { break }
    if ((Get-Date) -ge $deadline) { throw "Timed out waiting for benchmark steps" }
    Start-Sleep -Seconds $PollSeconds
} while ($true)

if ($FullRun -and $status.state -eq "completed") {
    $materializeScript = @"
from guideants_video_client import materialize_talking_head_result
import json
result = materialize_talking_head_result(
    '$jobId',
    '$OutputName',
)
print(json.dumps(result, separators=(',', ':')))
"@
    $materializePayload = @{
        script = $materializeScript
        scriptType = "Python"
        workingDirectory = "/app/ContentFiles/acceptance-project/authorized-notebook/Output"
        projectId = "11111111-1111-1111-1111-111111111111"
        notebookId = "22222222-2222-2222-2222-222222222222"
        guideId = "33333333-3333-3333-3333-333333333333"
        timeoutSeconds = 600
    } | ConvertTo-Json -Compress
    $materializePayloadPath = Join-Path $ArtifactDir "rocm-benchmark-materialize.json"
    [System.IO.File]::WriteAllText($materializePayloadPath, $materializePayload, (New-Object System.Text.UTF8Encoding $false))
    $materializeResponse = curl.exe --fail --silent --show-error --max-time 600 -H "X-Script-Agent-Token: $ScriptAgentToken" -H "Content-Type: application/json" --data-binary "@$materializePayloadPath" "$VideoHost/sandbox/execute"
    if ($LASTEXITCODE -ne 0) { throw "Benchmark materialize request failed (curl exit $LASTEXITCODE)" }
    $materializeSandbox = $materializeResponse | ConvertFrom-Json
    if ([int]$materializeSandbox.ExitCode -ne 0) { throw "Benchmark materialize script failed: $($materializeSandbox.StandardError)" }
    Write-Host $materializeSandbox.StandardOutput.Trim()
}

$previousErrorActionPreference = $ErrorActionPreference
$ErrorActionPreference = "Continue"
$containerLogs = docker logs compose-comfyui-video-1 2>&1
$ErrorActionPreference = $previousErrorActionPreference
$containerLogs | Select-String -Pattern "Sampling audio indices|s/it\]|Swapping 0|AOTriton|Block swap memory|Transformer blocks" | Select-Object -Last 20 |
    ForEach-Object { $_.Line } | Add-Content -LiteralPath $LogPath

Write-Host "Benchmark log: $LogPath"
