# External-facing API succession test: Lightning 4-step then 20-step edit
$ErrorActionPreference = "Stop"
$VideoHost = "http://127.0.0.1:8189"
$AdminToken = "local-video-admin-test-token"
$ArtifactDir = "C:\repos\GuideAnts\artifacts\qwen-image-edit"
$Source = "C:\repos\GuideAnts\artifacts\acceptance-project\authorized-notebook\Input\diagram-style-src.png"
$Prompt = "Restyle this technical sequence diagram into a polished modern product illustration. Keep the same participants, arrows, labels, and notes exactly readable. Add soft color: cool slate background, blue accents for Depth 1 Inv A, teal for Depth 2 Inv B, violet for Depth 3 Inv C, warm amber highlights on notes and the Human actor. Clean flat vector look with subtle shadows, high contrast typography, professional SaaS docs aesthetic. Do not invent new boxes or change the message flow."
$Negative = "blurry text, warped arrows, extra boxes, illegible labels, watermark"

function Get-HttpJson([string]$Url, [string[]]$ExtraArgs = @()) {
  $tmp = Join-Path $env:TEMP ("ga-http-{0}.out" -f [guid]::NewGuid().ToString("n"))
  $codeFile = "$tmp.code"
  try {
    $args = @("--silent", "--show-error", "-o", $tmp, "-w", "%{http_code}") + $ExtraArgs + @($Url)
    $code = (& curl.exe @args).Trim()
    $body = if (Test-Path $tmp) { [IO.File]::ReadAllText($tmp) } else { "" }
    return @{ Code = $code; Body = $body }
  } finally {
    Remove-Item -LiteralPath $tmp, $codeFile -Force -ErrorAction SilentlyContinue
  }
}

function Wait-ImageJob([string]$JobId, [string]$Label, [int]$TimeoutSeconds = 1800) {
  if (-not ($JobId -match '^[0-9a-f]{32}$')) {
    throw "$Label refusing to poll invalid jobId '$JobId'"
  }
  $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
  do {
    $resp = Get-HttpJson "$VideoHost/video/v1/image/jobs/$JobId"
    if ($resp.Code -ne "200") {
      throw "$Label status HTTP $($resp.Code) body=$($resp.Body)"
    }
    $status = $resp.Body | ConvertFrom-Json
    $state = [string]$status.state
    if ([string]::IsNullOrWhiteSpace($state)) {
      throw "$Label status missing state: $($resp.Body)"
    }
    $msg = if ($status.progress -and $status.progress.message) { [string]$status.progress.message } else { $state }
    Write-Host ("[{0}] {1}" -f $Label, $msg)
    if ($state -eq "completed") { return $status }
    if ($state -in @("failed", "cancelled")) {
      throw "$Label ended $state : $($status.error)"
    }
    if ((Get-Date) -ge $deadline) { throw "timeout $Label $JobId after ${TimeoutSeconds}s" }
    Start-Sleep -Seconds 5
  } while ($true)
}

function Submit-Edit([string]$Workflow, [string]$OutName, [hashtable]$Parameters, [string]$Label) {
  $paramsPath = Join-Path $ArtifactDir ("{0}-params.json" -f $Label)
  $encoding = New-Object System.Text.UTF8Encoding $false
  [IO.File]::WriteAllText($paramsPath, ($Parameters | ConvertTo-Json -Compress), $encoding)
  $sw = [Diagnostics.Stopwatch]::StartNew()
  $resp = Get-HttpJson "$VideoHost/video/v1/image/jobs" @(
    "-F", "source=@$Source;type=image/png",
    "-F", "prompt=$Prompt",
    "-F", "output_filename=$OutName",
    "-F", "workflow_version=$Workflow",
    "-F", "negative_prompt=$Negative",
    "-F", "parameters=<$paramsPath"
  )
  if ($resp.Code -ne "202") {
    throw "$Label submit failed HTTP $($resp.Code) body=$($resp.Body)"
  }
  $submit = $resp.Body | ConvertFrom-Json
  $jobId = [string]$submit.jobId
  if (-not ($jobId -match '^[0-9a-f]{32}$')) {
    throw "$Label submit returned invalid jobId '$jobId' body=$($resp.Body)"
  }
  Write-Host "$Label submitted job=$jobId workflow=$Workflow"
  Wait-ImageJob $jobId $Label | Out-Null
  $outPath = Join-Path $ArtifactDir $OutName
  $dl = Get-HttpJson "$VideoHost/video/v1/image/jobs/$jobId/result" @("-o", $outPath)
  # Get-HttpJson with -o writes body to outPath; code still returned on stdout path via our helper
  # Re-download simply with curl for binary safety:
  curl.exe --fail --silent --show-error -o $outPath "$VideoHost/video/v1/image/jobs/$jobId/result"
  if ($LASTEXITCODE -ne 0) { throw "$Label result download failed exit=$LASTEXITCODE" }
  if (-not (Test-Path $outPath) -or (Get-Item $outPath).Length -lt 8) { throw "$Label empty result" }
  $sw.Stop()
  Write-Host "$Label OK elapsed=$([math]::Round($sw.Elapsed.TotalSeconds,1))s path=$outPath bytes=$((Get-Item $outPath).Length)"
  return @{ jobId = $jobId; path = $outPath; elapsed = [math]::Round($sw.Elapsed.TotalSeconds, 1) }
}

Write-Host "=== capabilities ==="
$capsResp = Get-HttpJson "$VideoHost/video/v1/capabilities"
if ($capsResp.Code -ne "200") { throw "capabilities HTTP $($capsResp.Code)" }
$caps = $capsResp.Body | ConvertFrom-Json
$caps | ConvertTo-Json -Compress | Write-Host
if ($caps.image_ready -ne $true) { throw "image_ready false" }
if ($caps.image_edit_20_ready -ne $true) { throw "image_edit_20_ready false" }
foreach ($p in @("denoise", "shift", "megapixels", "lora_strength")) {
  if ($caps.image_parameters -notcontains $p) { throw "$p not advertised" }
}

Write-Host "=== admin models ==="
$modelsResp = Get-HttpJson "$VideoHost/video/v1/models" @("-H", "X-Video-Admin-Token: $AdminToken")
if ($modelsResp.Code -ne "200") { throw "models HTTP $($modelsResp.Code) body=$($modelsResp.Body)" }
$models = $modelsResp.Body | ConvertFrom-Json
foreach ($need in @("qwen-image-edit-v1", "qwen-image-edit-20-v1", "qwen-image-v1")) {
  $b = $models.bundles | Where-Object { $_.name -eq $need } | Select-Object -First 1
  if ($null -eq $b) { throw "missing catalog bundle $need" }
  if (-not $b.ready) { throw "bundle $need not ready" }
  Write-Host "bundle $need ready=$($b.ready)"
}

Write-Host "=== admin install smoke (bundle already on volume) ==="
$installPath = Join-Path $ArtifactDir "admin-install-20.json"
[IO.File]::WriteAllText($installPath, '{"bundle":"qwen-image-edit-20-v1"}')
$installResp = Get-HttpJson "$VideoHost/video/v1/admin/models/install" @(
  "-H", "X-Video-Admin-Token: $AdminToken",
  "-H", "Content-Type: application/json",
  "--data-binary", "@$installPath"
)
if ($installResp.Code -ne "202") { throw "install HTTP $($installResp.Code) body=$($installResp.Body)" }
$install = $installResp.Body | ConvertFrom-Json
$installId = [string]$install.installId
if (-not ($installId -match '^[0-9a-f]{32}$')) { throw "bad installId '$installId'" }
$deadline = (Get-Date).AddSeconds(60)
do {
  $stResp = Get-HttpJson "$VideoHost/video/v1/admin/models/install/$installId" @("-H", "X-Video-Admin-Token: $AdminToken")
  if ($stResp.Code -ne "200") { throw "install status HTTP $($stResp.Code)" }
  $st = $stResp.Body | ConvertFrom-Json
  Write-Host "install status=$($st.state)"
  if ($st.state -eq "completed") { break }
  if ($st.state -eq "failed") { throw "install failed: $($st.error)" }
  if ((Get-Date) -ge $deadline) { throw "install timeout 60s (state=$($st.state))" }
  Start-Sleep -Seconds 1
} while ($true)

Write-Host "=== workflow 1: Lightning 4-step ==="
$r1 = Submit-Edit -Workflow "qwen-image-edit-v1" -OutName "diagram-styled-lightning.png" -Label "lightning" -Parameters @{
  steps = 4; cfg = 1.0; seed = 42; denoise = 1.0; shift = 3.1; megapixels = 1.6; lora_strength = 1.0
}

Write-Host "=== workflow 2: 20-step non-Lightning ==="
$r2 = Submit-Edit -Workflow "qwen-image-edit-20-v1" -OutName "diagram-styled-20step.png" -Label "edit20" -Parameters @{
  steps = 20; cfg = 4.0; seed = 42; denoise = 0.85; shift = 3.1; megapixels = 1.8
}

Write-Host "SUCCESSION_OK lightning=$($r1.elapsed)s edit20=$($r2.elapsed)s"
Write-Host "lightning=$($r1.path)"
Write-Host "edit20=$($r2.path)"
