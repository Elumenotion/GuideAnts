# Lightning 4-step on ffmpeg 2x source, denoise 0.45, megapixels kept at ~2.3
$ErrorActionPreference = "Stop"
$VideoHost = "http://127.0.0.1:8189"
$ArtifactDir = "C:\repos\GuideAnts\artifacts\qwen-image-edit"
$Source = "C:\repos\GuideAnts\artifacts\acceptance-project\authorized-notebook\Input\diagram-style-src-2x.png"
$OutName = "diagram-styled-lightning-up2x-d045.png"
$Prompt = "Restyle this technical sequence diagram into a polished modern product illustration. Keep the same participants, arrows, labels, and notes exactly readable. Add soft color: cool slate background, blue accents for Depth 1 Inv A, teal for Depth 2 Inv B, violet for Depth 3 Inv C, warm amber highlights on notes and the Human actor. Clean flat vector look with subtle shadows, high contrast typography, professional SaaS docs aesthetic. Do not invent new boxes or change the message flow."
$Negative = "blurry text, warped arrows, extra boxes, illegible labels, watermark"

if (-not (Test-Path -LiteralPath $Source)) { throw "missing 2x source: $Source" }

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

$caps = (Get-HttpJson "$VideoHost/video/v1/capabilities")
if ($caps.Code -ne "200") { throw "capabilities HTTP $($caps.Code)" }
$ready = ($caps.Body | ConvertFrom-Json).image_ready
if ($ready -ne $true) { throw "image_ready false: $($caps.Body)" }

$paramsPath = Join-Path $ArtifactDir "lightning-up2x-d045-params.json"
[IO.File]::WriteAllText($paramsPath, '{"steps":4,"cfg":1.0,"seed":42,"denoise":0.45,"shift":3.1,"megapixels":2.3,"lora_strength":1.0}')

$sw = [Diagnostics.Stopwatch]::StartNew()
$submit = Get-HttpJson "$VideoHost/video/v1/image/jobs" @(
  "-F", "source=@$Source;type=image/png",
  "-F", "prompt=$Prompt",
  "-F", "output_filename=$OutName",
  "-F", "workflow_version=qwen-image-edit-v1",
  "-F", "negative_prompt=$Negative",
  "-F", "parameters=<$paramsPath"
)
if ($submit.Code -ne "202") { throw "submit HTTP $($submit.Code) body=$($submit.Body)" }
$jobId = [string]($submit.Body | ConvertFrom-Json).jobId
if (-not ($jobId -match '^[0-9a-f]{32}$')) { throw "invalid jobId '$jobId' body=$($submit.Body)" }
Write-Host "submitted job=$jobId denoise=0.45 megapixels=2.3 source=2x"

$deadline = (Get-Date).AddSeconds(600)
do {
  $st = Get-HttpJson "$VideoHost/video/v1/image/jobs/$jobId"
  if ($st.Code -ne "200") { throw "status HTTP $($st.Code) body=$($st.Body)" }
  $job = $st.Body | ConvertFrom-Json
  $state = [string]$job.state
  if ([string]::IsNullOrWhiteSpace($state)) { throw "missing state: $($st.Body)" }
  $msg = if ($job.progress -and $job.progress.message) { [string]$job.progress.message } else { $state }
  Write-Host ("[up2x-d045] {0}" -f $msg)
  if ($state -eq "completed") { break }
  if ($state -in @("failed", "cancelled")) { throw "ended $state : $($job.error)" }
  if ((Get-Date) -ge $deadline) { throw "timeout job $jobId after 600s" }
  Start-Sleep -Seconds 5
} while ($true)

$outPath = Join-Path $ArtifactDir $OutName
curl.exe --fail --silent --show-error -o $outPath "$VideoHost/video/v1/image/jobs/$jobId/result"
if ($LASTEXITCODE -ne 0) { throw "result download failed" }
if ((Get-Item $outPath).Length -lt 8) { throw "empty result" }
$sw.Stop()
Write-Host "OK elapsed=$([math]::Round($sw.Elapsed.TotalSeconds,1))s path=$outPath bytes=$((Get-Item $outPath).Length)"
