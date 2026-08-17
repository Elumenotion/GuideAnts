# Restyle PlantUML chrome (no glyphs), then overlay original SVG text.
$ErrorActionPreference = "Stop"
$VideoHost = "http://127.0.0.1:8189"
$ArtifactDir = "C:\repos\GuideAnts\artifacts\qwen-image-edit"
$Source = Join-Path $ArtifactDir "diagram_activity_providerchat_behavior.chrome-2x.png"
$TextLayer = Join-Path $ArtifactDir "diagram_activity_providerchat_behavior.text-2x.png"
$RestyleName = "diagram_activity_providerchat_behavior.chrome-restyled.png"
$OutPath = Join-Path $ArtifactDir "diagram_activity_providerchat_behavior.overlay-lightning.png"
$Prompt = "Restyle this unlabeled architecture diagram chrome into a polished modern product illustration. Keep every box, note, arrow, dashed line, and stick figure in the exact same position and size. Cool slate canvas, richer fills, subtle shadows, clean flat vector SaaS docs look. Do not add any text, letters, numbers, watermarks, or new shapes. Empty boxes must stay empty."
$Negative = "text, letters, labels, words, typography, watermark, extra boxes, warped arrows, missing arrows"

if (-not (Test-Path -LiteralPath $Source)) { throw "missing chrome PNG: $Source" }
if (-not (Test-Path -LiteralPath $TextLayer)) { throw "missing text PNG: $TextLayer" }

function Get-HttpJson([string]$Url, [string[]]$ExtraArgs = @()) {
  $tmp = Join-Path $env:TEMP ("ga-http-{0}.out" -f [guid]::NewGuid().ToString("n"))
  try {
    $args = @("--silent", "--show-error", "-o", $tmp, "-w", "%{http_code}") + $ExtraArgs + @($Url)
    $code = (& curl.exe @args).Trim()
    $body = if (Test-Path $tmp) { [IO.File]::ReadAllText($tmp) } else { "" }
    return @{ Code = $code; Body = $body }
  } finally {
    Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
  }
}

function Get-ImageSize([string]$Path) {
  Add-Type -AssemblyName System.Drawing
  $img = [System.Drawing.Image]::FromFile($Path)
  try {
    return @{ Width = $img.Width; Height = $img.Height }
  } finally {
    $img.Dispose()
  }
}

$caps = Get-HttpJson "$VideoHost/video/v1/capabilities"
if ($caps.Code -ne "200") { throw "capabilities HTTP $($caps.Code) body=$($caps.Body)" }
$ready = ($caps.Body | ConvertFrom-Json).image_ready
if ($ready -ne $true) { throw "image_ready false: $($caps.Body)" }

$paramsPath = Join-Path $ArtifactDir "overlay-lightning-params.json"
[IO.File]::WriteAllText($paramsPath, '{"steps":4,"cfg":1.0,"seed":42,"denoise":0.55,"shift":3.1,"megapixels":2.0,"lora_strength":1.0}')

$sw = [Diagnostics.Stopwatch]::StartNew()
$submit = Get-HttpJson "$VideoHost/video/v1/image/jobs" @(
  "-F", "source=@$Source;type=image/png",
  "-F", "prompt=$Prompt",
  "-F", "output_filename=$RestyleName",
  "-F", "workflow_version=qwen-image-edit-v1",
  "-F", "negative_prompt=$Negative",
  "-F", "parameters=<$paramsPath"
)
if ($submit.Code -ne "202") { throw "submit HTTP $($submit.Code) body=$($submit.Body)" }
$jobId = [string]($submit.Body | ConvertFrom-Json).jobId
if (-not ($jobId -match '^[0-9a-f]{32}$')) { throw "invalid jobId '$jobId' body=$($submit.Body)" }
Write-Host "submitted job=$jobId denoise=0.55 megapixels=2.0 chrome-only overlay test"

$deadline = (Get-Date).AddSeconds(600)
do {
  $st = Get-HttpJson "$VideoHost/video/v1/image/jobs/$jobId"
  if ($st.Code -ne "200") { throw "status HTTP $($st.Code) body=$($st.Body)" }
  $job = $st.Body | ConvertFrom-Json
  $state = [string]$job.state
  if ([string]::IsNullOrWhiteSpace($state)) { throw "missing state: $($st.Body)" }
  $msg = if ($job.progress -and $job.progress.message) { [string]$job.progress.message } else { $state }
  Write-Host ("[overlay-lightning] {0}" -f $msg)
  if ($state -eq "completed") { break }
  if ($state -in @("failed", "cancelled")) { throw "ended $state : $($job.error)" }
  if ((Get-Date) -ge $deadline) { throw "timeout job $jobId after 600s" }
  Start-Sleep -Seconds 5
} while ($true)

$restylePath = Join-Path $ArtifactDir $RestyleName
curl.exe --fail --silent --show-error -o $restylePath "$VideoHost/video/v1/image/jobs/$jobId/result"
if ($LASTEXITCODE -ne 0) { throw "result download failed" }
if ((Get-Item $restylePath).Length -lt 8) { throw "empty result" }

$rs = Get-ImageSize $restylePath
$ts = Get-ImageSize $TextLayer
Write-Host ("restyle {0}x{1} text {2}x{3}" -f $rs.Width, $rs.Height, $ts.Width, $ts.Height)

ffmpeg -y -i $restylePath -i $TextLayer -filter_complex ("[1:v]scale={0}:{1}:flags=lanczos,format=rgba[txt];[0:v]format=rgba[bg];[bg][txt]overlay=0:0" -f $rs.Width, $rs.Height) $OutPath
if ($LASTEXITCODE -ne 0) { throw "ffmpeg overlay failed $LASTEXITCODE" }
if ((Get-Item $OutPath).Length -lt 8) { throw "empty overlay" }

$sw.Stop()
Write-Host "OK elapsed=$([math]::Round($sw.Elapsed.TotalSeconds,1))s restyle=$restylePath overlay=$OutPath bytes=$((Get-Item $OutPath).Length)"
