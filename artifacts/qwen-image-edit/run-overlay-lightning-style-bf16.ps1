# BF16 Lightning overlay. Same job as run-overlay-lightning-style.ps1, different UNet.
# UNet: qwen_image_edit_2511_bf16.safetensors (qwen-image-edit-bf16-v1)
# Writes: <svg-stem>.overlay-lightning-style-bf16.png
# Does not overwrite *.overlay-lightning-style.png (the FP8 results).
# If the -bf16 file already exists, writes -2, -3, ...
#
# Run:
#   powershell -NoProfile -ExecutionPolicy Bypass -File "C:\repos\GuideAnts\artifacts\qwen-image-edit\run-overlay-lightning-style-bf16.ps1" -SvgPath "C:\path\diagram.svg"
#
# Iterate: -Tag bf16-cfg6 -Cfg 6
# First BF16 load can take a long time. Default timeout is 1800s.
param(
  [Parameter(Mandatory = $true, Position = 0)]
  [string]$SvgPath,
  [string]$OverlayName,
  [string]$Tag = "bf16",
  [int]$Steps = 4,
  [double]$Cfg = 4,
  [long]$Seed = 42,
  [double]$Denoise = 0.8,
  [double]$Shift = 3.1,
  [double]$Megapixels = 2.0,
  [double]$LoraStrength = 0.5,
  [int]$TimeoutSeconds = 1800
)
$ErrorActionPreference = "Stop"
$runner = Join-Path $PSScriptRoot "run-overlay-lightning-style.ps1"
if (-not (Test-Path -LiteralPath $runner)) { throw "missing runner: $runner" }
$forward = @{
  SvgPath = $SvgPath
  Tag = $Tag
  WorkflowVersion = "qwen-image-edit-bf16-v1"
  ReadyProperty = "image_edit_bf16_ready"
  Steps = $Steps
  Cfg = $Cfg
  Seed = $Seed
  Denoise = $Denoise
  Shift = $Shift
  Megapixels = $Megapixels
  LoraStrength = $LoraStrength
  TimeoutSeconds = $TimeoutSeconds
}
if (-not [string]::IsNullOrWhiteSpace($OverlayName)) {
  $forward.OverlayName = $OverlayName
}
& $runner @forward
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
