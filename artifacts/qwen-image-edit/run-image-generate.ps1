# Image 2512 generate. Tuning / comparison. Does not touch overlay-lightning-style PNGs.
# Workflow: qwen-image-v1 (already mounted). API: POST /video/v1/image/generate/jobs
#
# Modes (parameters only; no new workflow):
#   lightning  steps=4  cfg=1  lora_strength=1
#   full20     steps=20 cfg=4  lora_strength=0
#
# Precision:
#   fp8   uses qwen_image_2512_fp8_e4m3fn.safetensors (the file's default)
#   bf16  writes qwen_image_2512_bf16.safetensors into the mounted JSON for this
#         job only, then writes FP8 back. Does not change the adapter or compose.
#
# Writes: image-gen-<precision>-<mode>.png  (then -2, -3 if that file exists)
#         plus a matching .json sidecar (seed, size, timings).
#
# Run:
#   powershell -NoProfile -ExecutionPolicy Bypass -File "C:\repos\GuideAnts\artifacts\qwen-image-edit\run-image-generate.ps1" -Precision fp8 -Mode lightning
#   powershell -NoProfile -ExecutionPolicy Bypass -File "C:\repos\GuideAnts\artifacts\qwen-image-edit\run-image-generate.ps1" -Precision fp8 -Mode full20
#   powershell -NoProfile -ExecutionPolicy Bypass -File "C:\repos\GuideAnts\artifacts\qwen-image-edit\run-image-generate.ps1" -Precision bf16 -Mode lightning
#   powershell -NoProfile -ExecutionPolicy Bypass -File "C:\repos\GuideAnts\artifacts\qwen-image-edit\run-image-generate.ps1" -Precision bf16 -Mode full20
#   powershell -NoProfile -ExecutionPolicy Bypass -File "C:\repos\GuideAnts\artifacts\qwen-image-edit\run-image-generate.ps1" -Precision bf16 -Mode lightning -Width 1664 -Height 928
#
# Seed is random each run so reruns are new images. Pass -Seed 42 to lock FP8 vs BF16.
# Canvas (multiples of 8, 256-1920). Default 1328x1328 square.
#   -Width 1664 -Height 928   16:9 landscape
#   -Width 928  -Height 1664  9:16 portrait
#   -Width 1472 -Height 1140  4:3 landscape
# Optional: -Prompt "..." -Seed 42 -Tag myrun -TimeoutSeconds 1800
param(
  [Parameter(Mandatory = $true)]
  [ValidateSet("fp8", "bf16")]
  [string]$Precision,
  [Parameter(Mandatory = $true)]
  [ValidateSet("lightning", "full20")]
  [string]$Mode,
  [string]$Prompt = "Realistic aerial photo of an island in the sea covered with a jungle and birds. The `island` is the shell of a giant lion-turtle with the facial appearance and marine mamal whiskers of a female lion whose head is above the water.",
  [string]$Negative = "fur, text, watermark, logo",
  [string]$Tag,
  [long]$Seed,
  [int]$Width = 1328,
  [int]$Height = 1328,
  [int]$TimeoutSeconds = 1800
)
$ErrorActionPreference = "Stop"
if (-not $PSBoundParameters.ContainsKey("Seed")) {
  $Seed = Get-Random -Minimum 1 -Maximum 2147483647
}
$VideoHost = "http://127.0.0.1:8189"
$ArtifactDir = $PSScriptRoot
$WorkflowPath = [IO.Path]::GetFullPath((Join-Path $ArtifactDir "..\..\docker\build\comfyui-video\workflows\qwen-image-v1.json"))
$Fp8Unet = "qwen_image_2512_fp8_e4m3fn.safetensors"
$Bf16Unet = "qwen_image_2512_bf16.safetensors"

if (-not (Test-Path -LiteralPath $WorkflowPath)) { throw "missing generate workflow: $WorkflowPath" }
if ($Width -lt 256 -or $Width -gt 1920) { throw "Width must be between 256 and 1920 (got $Width)" }
if ($Height -lt 256 -or $Height -gt 1920) { throw "Height must be between 256 and 1920 (got $Height)" }
if ($Width % 8 -ne 0) { throw "Width must be a multiple of 8 (got $Width)" }
if ($Height % 8 -ne 0) { throw "Height must be a multiple of 8 (got $Height)" }

$modeParams = @{
  lightning = @{ steps = 4; cfg = 1.0; lora_strength = 1.0 }
  full20    = @{ steps = 20; cfg = 4.0; lora_strength = 0.0 }
}[$Mode]
$tagSuffix = if ([string]::IsNullOrWhiteSpace($Tag)) { "$Precision-$Mode" } else { $Tag }
$OutName = "image-gen-$tagSuffix.png"

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
    $body = if (Test-Path $tmp) { [IO.File]::ReadAllText($tmp) } else { "" }
    return @{ Code = $code; Body = $body }
  } finally {
    Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
  }
}

function Get-WorkflowUnet([string]$Text) {
  if ($Text.Contains($Bf16Unet)) { return $Bf16Unet }
  if ($Text.Contains($Fp8Unet)) { return $Fp8Unet }
  throw "generate workflow unet is not $Fp8Unet or $Bf16Unet : $WorkflowPath"
}

$OutPath = Get-UnusedPath (Join-Path $ArtifactDir $OutName)
$OutName = [IO.Path]::GetFileName($OutPath)
$desiredUnet = if ($Precision -eq "bf16") { $Bf16Unet } else { $Fp8Unet }
$workflowOriginal = [IO.File]::ReadAllText($WorkflowPath)
$unetBefore = Get-WorkflowUnet $workflowOriginal
$workflowFp8 = $workflowOriginal.Replace($Bf16Unet, $Fp8Unet)
$workflowDesired = $workflowFp8.Replace($Fp8Unet, $desiredUnet)
$wroteWorkflow = $false

Write-Host "generate precision=$Precision mode=$Mode seed=$Seed size=${Width}x${Height} unet=$desiredUnet out=$OutPath"
Write-Host "workflow=$WorkflowPath unet_on_disk=$unetBefore"

$timings = [ordered]@{}
$totalSw = [Diagnostics.Stopwatch]::StartNew()
$lapSw = [Diagnostics.Stopwatch]::StartNew()
function Complete-Lap([string]$Name) {
  $script:timings[$Name] = [math]::Round($script:lapSw.Elapsed.TotalSeconds, 1)
  $script:lapSw.Restart()
}

try {
  if ($workflowDesired -ne $workflowOriginal) {
    [IO.File]::WriteAllText($WorkflowPath, $workflowDesired)
    $wroteWorkflow = $true
    Write-Host "workflow unet written=$desiredUnet (will restore $Fp8Unet)"
  }
  Complete-Lap "workflow"

  $caps = Get-HttpJson "$VideoHost/video/v1/capabilities"
  if ($caps.Code -ne "200") { throw "capabilities HTTP $($caps.Code) body=$($caps.Body)" }
  $capsObj = $caps.Body | ConvertFrom-Json
  if ($capsObj.image_generate_ready -ne $true) { throw "image_generate_ready false: $($caps.Body)" }
  Complete-Lap "capabilities"

  $paramsObject = [ordered]@{
    steps = $modeParams.steps
    cfg = $modeParams.cfg
    seed = $Seed
    denoise = 1.0
    shift = 3.1
    megapixels = 1.6
    lora_strength = $modeParams.lora_strength
    width = $Width
    height = $Height
  }
  $paramsPath = Join-Path $env:TEMP ("ga-gen-params-{0}.json" -f [guid]::NewGuid().ToString("n"))
  [IO.File]::WriteAllText($paramsPath, ($paramsObject | ConvertTo-Json -Compress))
  try {
    $submit = Get-HttpJson "$VideoHost/video/v1/image/generate/jobs" @(
      "-F", "prompt=$Prompt",
      "-F", "output_filename=$OutName",
      "-F", "workflow_version=qwen-image-v1",
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
  Write-Host "submitted job=$jobId seed=$Seed size=${Width}x${Height} steps=$($modeParams.steps) cfg=$($modeParams.cfg) lora_strength=$($modeParams.lora_strength)"

  $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
  do {
    $st = Get-HttpJson "$VideoHost/video/v1/image/jobs/$jobId"
    if ($st.Code -ne "200") { throw "status HTTP $($st.Code) body=$($st.Body)" }
    $job = $st.Body | ConvertFrom-Json
    $state = [string]$job.state
    if ([string]::IsNullOrWhiteSpace($state)) { throw "missing state: $($st.Body)" }
    $msg = if ($job.progress -and $job.progress.message) { [string]$job.progress.message } else { $state }
    Write-Host ("[image-gen] {0}" -f $msg)
    if ($state -eq "completed") { break }
    if ($state -in @("failed", "cancelled")) { throw "ended $state : $($job.error)" }
    if ((Get-Date) -ge $deadline) { throw "timeout job $jobId after ${TimeoutSeconds}s" }
    Start-Sleep -Seconds 5
  } while ($true)
  Complete-Lap "comfy"

  curl.exe --fail --silent --show-error -o $OutPath "$VideoHost/video/v1/image/jobs/$jobId/result"
  if ($LASTEXITCODE -ne 0) { throw "result download failed" }
  if ((Get-Item $OutPath).Length -lt 8) { throw "empty result" }
  Complete-Lap "download"
} finally {
  if ($wroteWorkflow) {
    [IO.File]::WriteAllText($WorkflowPath, $workflowFp8)
    Write-Host "workflow restored unet=$Fp8Unet"
  }
}

$totalSw.Stop()
$totalSeconds = [math]::Round($totalSw.Elapsed.TotalSeconds, 1)
$record = [ordered]@{
  out = $OutName
  precision = $Precision
  mode = $Mode
  seed = $Seed
  width = $Width
  height = $Height
  steps = $modeParams.steps
  cfg = $modeParams.cfg
  lora_strength = $modeParams.lora_strength
  unet = $desiredUnet
  jobId = $jobId
  bytes = (Get-Item $OutPath).Length
  timings = $timings
  total = $totalSeconds
}
$recordPath = [IO.Path]::ChangeExtension($OutPath, ".json")
[IO.File]::WriteAllText($recordPath, ($record | ConvertTo-Json -Compress))
Write-Host "OK generate=$OutPath seed=$Seed bytes=$($record.bytes)"
Write-Host "log=$recordPath"
Write-Host "timings:"
foreach ($name in $timings.Keys) {
  Write-Host ("  {0}={1}s" -f $name, $timings[$name])
}
Write-Host ("  total={0}s" -f $totalSeconds)
