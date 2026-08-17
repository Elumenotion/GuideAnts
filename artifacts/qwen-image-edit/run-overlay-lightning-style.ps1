# FP8 Lightning overlay. This is the previous-test path.
# UNet: qwen_image_edit_2511_fp8mixed.safetensors (qwen-image-edit-v1)
# Writes: <svg-stem>.overlay-lightning-style.png
# If that file already exists, writes -2, -3, ... and leaves the old file.
#
# Run:
#   powershell -NoProfile -ExecutionPolicy Bypass -File "C:\repos\GuideAnts\artifacts\qwen-image-edit\run-overlay-lightning-style.ps1" -SvgPath "C:\path\diagram.svg"
#
# Compare against BF16 with run-overlay-lightning-style-bf16.ps1 on the same SVG.
# Optional: -Tag name -Cfg 4 -Denoise 0.8 -TimeoutSeconds 600
param(
  [Parameter(Mandatory = $true, Position = 0)]
  [string]$SvgPath,
  [string]$OverlayName,
  [string]$Tag,
  [string]$WorkflowVersion = "qwen-image-edit-v1",
  [string]$ReadyProperty = "image_ready",
  [int]$Steps = 4,
  [double]$Cfg = 4,
  [long]$Seed = 42,
  [double]$Denoise = 0.8,
  [double]$Shift = 3.1,
  [double]$Megapixels = 2.0,
  [double]$LoraStrength = 0.5,
  [int]$TimeoutSeconds = 600
)
$ErrorActionPreference = "Stop"
$VideoHost = "http://127.0.0.1:8189"
$ArtifactDir = $PSScriptRoot
$SplitPy = Join-Path $ArtifactDir "split_plantuml_svg.py"
$RasterPy = Join-Path $ArtifactDir "rasterize_svg.py"
$Scale = 2.0

if (-not (Test-Path -LiteralPath $SvgPath)) { throw "missing SVG: $SvgPath" }
if (-not (Test-Path -LiteralPath $SplitPy)) { throw "missing splitter: $SplitPy" }
if (-not (Test-Path -LiteralPath $RasterPy)) { throw "missing rasterizer: $RasterPy" }

$stem = [IO.Path]::GetFileNameWithoutExtension($SvgPath)
$srcSvg = Join-Path $ArtifactDir "$stem.svg"
$chromeSvg = Join-Path $ArtifactDir "$stem.chrome.svg"
$textSvg = Join-Path $ArtifactDir "$stem.text.svg"
$Source = Join-Path $ArtifactDir "$stem.chrome-2x.png"
$TextLayer = Join-Path $ArtifactDir "$stem.text-2x.png"
$tagSuffix = if ([string]::IsNullOrWhiteSpace($Tag)) { "" } else { "-$Tag" }
$RestyleName = "$stem.chrome-restyled-style$tagSuffix.png"
if ([string]::IsNullOrWhiteSpace($OverlayName)) {
  $OverlayName = "$stem.overlay-lightning-style$tagSuffix.png"
}
$OutPath = if ([IO.Path]::IsPathRooted($OverlayName)) { $OverlayName } else { Join-Path $ArtifactDir $OverlayName }

function Get-UnusedPath([string]$Path) {
  if (-not (Test-Path -LiteralPath $Path)) { return $Path }
  $dir = Split-Path -Parent $Path
  $base = [IO.Path]::GetFileNameWithoutExtension($Path)
  $ext = [IO.Path]::GetExtension($Path)
  for ($n = 2; $n -le 9999; $n++) {
    $candidate = Join-Path $dir ("{0}-{1}{2}" -f $base, $n, $ext)
    if (-not (Test-Path -LiteralPath $candidate)) { return $candidate }
  }
  throw "no unused overlay name for $Path"
}

$OutPath = Get-UnusedPath $OutPath
$RestyleName = [IO.Path]::GetFileName((Get-UnusedPath (Join-Path $ArtifactDir $RestyleName)))
Write-Host "overlay=$OutPath workflow=$WorkflowVersion tag=$Tag"

$Prompt = "Add subtle styling to the provided diagram to make it more visually appealing and professional and include a lemon-chiffon gradient background. Preserve all original layout and do not add new elements"
$Negative = "extra elements, text"

$srcFull = (Resolve-Path -LiteralPath $SvgPath).Path
$dstFull = [IO.Path]::GetFullPath($srcSvg)
if ($srcFull -ne $dstFull) {
  Copy-Item -LiteralPath $SvgPath -Destination $srcSvg -Force
}
Write-Host "svg=$srcSvg"

$timings = [ordered]@{}
$totalSw = [Diagnostics.Stopwatch]::StartNew()
$lapSw = [Diagnostics.Stopwatch]::StartNew()
function Complete-Lap([string]$Name) {
  $script:timings[$Name] = [math]::Round($script:lapSw.Elapsed.TotalSeconds, 1)
  $script:lapSw.Restart()
}

python $SplitPy $srcSvg $chromeSvg $textSvg
if ($LASTEXITCODE -ne 0) { throw "split failed $LASTEXITCODE" }
Complete-Lap "split"
python $RasterPy $chromeSvg $Source $Scale false
if ($LASTEXITCODE -ne 0) { throw "chrome rasterize failed $LASTEXITCODE" }
Complete-Lap "raster_chrome"
python $RasterPy $textSvg $TextLayer $Scale true
if ($LASTEXITCODE -ne 0) { throw "text rasterize failed $LASTEXITCODE" }
Complete-Lap "raster_text"

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
$capsObj = $caps.Body | ConvertFrom-Json
$ready = $capsObj.$ReadyProperty
if ($ready -ne $true) { throw "$ReadyProperty false: $($caps.Body)" }
Complete-Lap "capabilities"

$paramsObject = [ordered]@{
  steps = $Steps
  cfg = $Cfg
  seed = $Seed
  denoise = $Denoise
  shift = $Shift
  megapixels = $Megapixels
  lora_strength = $LoraStrength
}
$paramsPath = Join-Path $env:TEMP ("ga-overlay-params-{0}.json" -f [guid]::NewGuid().ToString("n"))
[IO.File]::WriteAllText($paramsPath, ($paramsObject | ConvertTo-Json -Compress))

try {
  $submit = Get-HttpJson "$VideoHost/video/v1/image/jobs" @(
    "-F", "source=@$Source;type=image/png",
    "-F", "prompt=$Prompt",
    "-F", "output_filename=$RestyleName",
    "-F", "workflow_version=$WorkflowVersion",
    "-F", "negative_prompt=$Negative",
    "-F", "parameters=<$paramsPath"
  )
} finally {
  Remove-Item -LiteralPath $paramsPath -Force -ErrorAction SilentlyContinue
}
if ($submit.Code -ne "202") { throw "submit HTTP $($submit.Code) body=$($submit.Body)" }
$jobId = [string]($submit.Body | ConvertFrom-Json).jobId
if (-not ($jobId -match '^[0-9a-f]{32}$')) { throw "invalid jobId '$jobId' body=$($submit.Body)" }
Complete-Lap "submit"
Write-Host "submitted job=$jobId workflow=$WorkflowVersion denoise=$Denoise cfg=$Cfg source=$Source overlay=$OutPath"

$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
do {
  $st = Get-HttpJson "$VideoHost/video/v1/image/jobs/$jobId"
  if ($st.Code -ne "200") { throw "status HTTP $($st.Code) body=$($st.Body)" }
  $job = $st.Body | ConvertFrom-Json
  $state = [string]$job.state
  if ([string]::IsNullOrWhiteSpace($state)) { throw "missing state: $($st.Body)" }
  $msg = if ($job.progress -and $job.progress.message) { [string]$job.progress.message } else { $state }
  Write-Host ("[overlay-style] {0}" -f $msg)
  if ($state -eq "completed") { break }
  if ($state -in @("failed", "cancelled")) { throw "ended $state : $($job.error)" }
  if ((Get-Date) -ge $deadline) { throw "timeout job $jobId after ${TimeoutSeconds}s" }
  Start-Sleep -Seconds 5
} while ($true)
Complete-Lap "comfy"

$restylePath = Join-Path $ArtifactDir $RestyleName
curl.exe --fail --silent --show-error -o $restylePath "$VideoHost/video/v1/image/jobs/$jobId/result"
if ($LASTEXITCODE -ne 0) { throw "result download failed" }
if ((Get-Item $restylePath).Length -lt 8) { throw "empty result" }
Complete-Lap "download"

$rs = Get-ImageSize $restylePath
Write-Host ("restyle {0}x{1}" -f $rs.Width, $rs.Height)

ffmpeg -y -i $restylePath -i $TextLayer -filter_complex ("[1:v]scale={0}:{1}:flags=lanczos,format=rgba[txt];[0:v]format=rgba[bg];[bg][txt]overlay=0:0" -f $rs.Width, $rs.Height) $OutPath
if ($LASTEXITCODE -ne 0) { throw "ffmpeg overlay failed $LASTEXITCODE" }
if ((Get-Item $OutPath).Length -lt 8) { throw "empty overlay" }
Complete-Lap "ffmpeg"

$totalSw.Stop()
Write-Host "OK restyle=$restylePath overlay=$OutPath bytes=$((Get-Item $OutPath).Length)"
Write-Host "timings:"
foreach ($name in $timings.Keys) {
  Write-Host ("  {0}={1}s" -f $name, $timings[$name])
}
Write-Host ("  total={0}s" -f [math]::Round($totalSw.Elapsed.TotalSeconds, 1))
