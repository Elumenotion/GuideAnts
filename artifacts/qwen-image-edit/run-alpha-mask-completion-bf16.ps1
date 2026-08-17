# Edit a source PNG through a separate grayscale mask with Qwen Image Edit 2511 BF16.
# White mask pixels are editable. Black mask pixels are preserved. The unaltered
# source is used for Qwen conditioning and composited back after generation.
# Default mask is office-whiteboard-mask-inset.png: the flush board mask eroded
# 24px so the silver frame and a band of original whiteboard fully surround it.
#
# Run:
#   powershell -NoProfile -ExecutionPolicy Bypass -File "C:\repos\GuideAnts\artifacts\qwen-image-edit\run-alpha-mask-completion-bf16.ps1"
#
# Optional:
#   -SourcePath "C:\path\source.png" -MaskPath "C:\path\mask.png" -Seed 42 -Tag test
param(
  [string]$SourcePath = "C:\Users\dougl\Downloads\office-bg.png",
  [string]$MaskPath = "C:\repos\GuideAnts\artifacts\qwen-image-edit\office-whiteboard-mask-inset.png",
  [string]$Prompt = "On the existing white whiteboard, add a casual hand-written message that says `Elvis is everywhere! <3` using blue and red dry-erase markers, plus a simple crude green marker line drawing of Elvis. Keep the whiteboard white.",
  [string]$Negative = "fancy, block text, perfect text, font, black background",
  [string]$Tag = "office-whiteboard-bf16-inpaint",
  [long]$Seed,
  [int]$TimeoutSeconds = 1800
)

$ErrorActionPreference = "Stop"
if (-not $PSBoundParameters.ContainsKey("Seed")) {
  $Seed = Get-Random -Minimum 1 -Maximum 2147483647
}
$VideoHost = "http://127.0.0.1:8189"
$WorkflowVersion = "qwen-image-edit-bf16-inpaint-v1"
$ArtifactDir = $PSScriptRoot

if (-not (Test-Path -LiteralPath $SourcePath -PathType Leaf)) {
  throw "missing source image: $SourcePath"
}
if (-not (Test-Path -LiteralPath $MaskPath -PathType Leaf)) {
  throw "missing mask image: $MaskPath"
}
if (
  [IO.Path]::GetExtension($SourcePath).ToLowerInvariant() -ne ".png" -or
  [IO.Path]::GetExtension($MaskPath).ToLowerInvariant() -ne ".png"
) {
  throw "source and mask must both be PNG files"
}

function Get-UnusedPath([string]$Path) {
  if (-not (Test-Path -LiteralPath $Path)) { return $Path }
  $dir = Split-Path -Parent $Path
  $base = [IO.Path]::GetFileNameWithoutExtension($Path)
  $ext = [IO.Path]::GetExtension($Path)
  for ($n = 2; $n -le 9999; $n++) {
    $candidate = Join-Path $dir ("{0}-{1}{2}" -f $base, $n, $ext)
    if (-not (Test-Path -LiteralPath $candidate)) { return $candidate }
  }
  throw "no unused name for $Path"
}

function Get-HttpJson([string]$Url, [string[]]$ExtraArgs = @()) {
  $tmp = Join-Path $env:TEMP ("ga-http-{0}.out" -f [guid]::NewGuid().ToString("n"))
  try {
    $args = @("--silent", "--show-error", "-o", $tmp, "-w", "%{http_code}") + $ExtraArgs + @($Url)
    $code = (& curl.exe @args).Trim()
    $body = if (Test-Path -LiteralPath $tmp) { [IO.File]::ReadAllText($tmp) } else { "" }
    return @{ Code = $code; Body = $body }
  } finally {
    Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
  }
}

function Get-ImageInfo([string]$Path) {
  Add-Type -AssemblyName System.Drawing
  $image = [System.Drawing.Bitmap]::FromFile($Path)
  try {
    return [ordered]@{
      width = $image.Width
      height = $image.Height
      pixelFormat = [string]$image.PixelFormat
      hasAlpha = [System.Drawing.Image]::IsAlphaPixelFormat($image.PixelFormat)
    }
  } finally {
    $image.Dispose()
  }
}

$source = Get-ImageInfo $SourcePath
$mask = Get-ImageInfo $MaskPath
if ($source.width % 8 -ne 0 -or $source.height % 8 -ne 0) {
  throw "source dimensions must be multiples of 8 (got $($source.width)x$($source.height))"
}
if ($mask.width -ne $source.width -or $mask.height -ne $source.height) {
  throw "mask dimensions must match source: source=$($source.width)x$($source.height) mask=$($mask.width)x$($mask.height)"
}

$OutPath = Get-UnusedPath (Join-Path $ArtifactDir "$Tag.png")
$OutName = [IO.Path]::GetFileName($OutPath)
$paramsObject = [ordered]@{
  steps = 4
  cfg = 1.0
  seed = $Seed
  denoise = 1.0
  shift = 3.1
  lora_strength = 1.0
}

Write-Host "inpaint workflow=$WorkflowVersion seed=$Seed source=$SourcePath mask=$MaskPath"
Write-Host "source size=$($source.width)x$($source.height) pixel_format=$($source.pixelFormat)"
Write-Host "mask size=$($mask.width)x$($mask.height) pixel_format=$($mask.pixelFormat) white=edit black=preserve"
Write-Host "output=$OutPath"

$timings = [ordered]@{}
$totalSw = [Diagnostics.Stopwatch]::StartNew()
$lapSw = [Diagnostics.Stopwatch]::StartNew()
function Complete-Lap([string]$Name) {
  $script:timings[$Name] = [math]::Round($script:lapSw.Elapsed.TotalSeconds, 1)
  $script:lapSw.Restart()
}

$caps = Get-HttpJson "$VideoHost/video/v1/capabilities"
if ($caps.Code -ne "200") {
  throw "capabilities HTTP $($caps.Code) body=$($caps.Body)"
}
$capsObj = $caps.Body | ConvertFrom-Json
if ($capsObj.image_edit_bf16_inpaint_ready -ne $true) {
  throw "image_edit_bf16_inpaint_ready false: $($caps.Body)"
}
Complete-Lap "capabilities"

$paramsPath = Join-Path $env:TEMP ("ga-inpaint-params-{0}.json" -f [guid]::NewGuid().ToString("n"))
[IO.File]::WriteAllText($paramsPath, ($paramsObject | ConvertTo-Json -Compress))
try {
  $submit = Get-HttpJson "$VideoHost/video/v1/image/jobs" @(
    "-F", "source=@$SourcePath;type=image/png",
    "-F", "mask=@$MaskPath;type=image/png",
    "-F", "prompt=$Prompt",
    "-F", "output_filename=$OutName",
    "-F", "workflow_version=$WorkflowVersion",
    "-F", "negative_prompt=$Negative",
    "-F", "parameters=<$paramsPath"
  )
} finally {
  Remove-Item -LiteralPath $paramsPath -Force -ErrorAction SilentlyContinue
}
if ($submit.Code -ne "202") {
  throw "submit HTTP $($submit.Code) body=$($submit.Body)"
}
$jobId = [string]($submit.Body | ConvertFrom-Json).jobId
if (-not ($jobId -match '^[0-9a-f]{32}$')) {
  throw "invalid jobId '$jobId' body=$($submit.Body)"
}
Complete-Lap "submit"
Write-Host "submitted job=$jobId steps=4 cfg=1 denoise=1 lora_strength=1"

$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
$lastTelemetry = ""
do {
  $status = Get-HttpJson "$VideoHost/video/v1/image/jobs/$jobId"
  if ($status.Code -ne "200") {
    throw "status HTTP $($status.Code) body=$($status.Body)"
  }
  $job = $status.Body | ConvertFrom-Json
  $state = [string]$job.state
  if ([string]::IsNullOrWhiteSpace($state)) {
    throw "missing state: $($status.Body)"
  }

  $progress = $job.progress
  $parts = @("state=$state")
  if ($progress) {
    if ($progress.phase) { $parts += "phase=$($progress.phase)" }
    if ($progress.message) { $parts += "message=$($progress.message)" }
    if ($progress.node_id -or $progress.node_class) {
      $parts += "node=$($progress.node_id):$($progress.node_class)"
    }
    if ($null -ne $progress.step -and $null -ne $progress.max_steps) {
      $parts += "progress=$($progress.step)/$($progress.max_steps)"
    }
    if ($null -ne $progress.percent) { $parts += "percent=$($progress.percent)%" }
    if ($null -ne $progress.queue_position) { $parts += "queue_position=$($progress.queue_position)" }
    if ($null -ne $progress.queue_remaining) { $parts += "queue_remaining=$($progress.queue_remaining)" }
  }
  $telemetry = $parts -join " | "
  if ($telemetry -ne $lastTelemetry) {
    Write-Host "[bf16-inpaint] $telemetry"
    $lastTelemetry = $telemetry
  }

  if ($state -eq "completed") { break }
  if ($state -in @("failed", "cancelled")) {
    throw "job ended $state : $($job.error)"
  }
  if ((Get-Date) -ge $deadline) {
    throw "timeout job $jobId after ${TimeoutSeconds}s"
  }
  Start-Sleep -Seconds 5
} while ($true)
Complete-Lap "comfy"

curl.exe --fail --silent --show-error -o $OutPath "$VideoHost/video/v1/image/jobs/$jobId/result"
if ($LASTEXITCODE -ne 0) { throw "result download failed" }
if ((Get-Item -LiteralPath $OutPath).Length -lt 8) { throw "empty result" }
Complete-Lap "download"

$result = Get-ImageInfo $OutPath
if ($result.width -ne $source.width -or $result.height -ne $source.height) {
  throw "result dimensions changed: source=$($source.width)x$($source.height) result=$($result.width)x$($result.height)"
}
Complete-Lap "verify"

$totalSw.Stop()
$totalSeconds = [math]::Round($totalSw.Elapsed.TotalSeconds, 1)
$record = [ordered]@{
  source = [IO.Path]::GetFullPath($SourcePath)
  mask = [IO.Path]::GetFullPath($MaskPath)
  out = $OutName
  workflow = $WorkflowVersion
  precision = "bf16"
  mode = "alpha-mask-inpaint"
  prompt = $Prompt
  negative = $Negative
  seed = $Seed
  width = $result.width
  height = $result.height
  steps = $paramsObject.steps
  cfg = $paramsObject.cfg
  denoise = $paramsObject.denoise
  shift = $paramsObject.shift
  lora_strength = $paramsObject.lora_strength
  jobId = $jobId
  bytes = (Get-Item -LiteralPath $OutPath).Length
  timings = $timings
  total = $totalSeconds
}
$recordPath = [IO.Path]::ChangeExtension($OutPath, ".json")
[IO.File]::WriteAllText($recordPath, ($record | ConvertTo-Json -Depth 5 -Compress))

Write-Host "OK inpaint=$OutPath size=$($result.width)x$($result.height) bytes=$($record.bytes)"
Write-Host "log=$recordPath"
Write-Host "timings:"
foreach ($name in $timings.Keys) {
  Write-Host ("  {0}={1}s" -f $name, $timings[$name])
}
Write-Host ("  total={0}s" -f $totalSeconds)
