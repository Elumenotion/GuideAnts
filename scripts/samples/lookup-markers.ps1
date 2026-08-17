param(
  [Parameter(Mandatory)][string]$SessionDir,
  [string[]]$Timecodes = @("0:15.0", "1:23.4", "2:05.0")
)

Set-Location $PSScriptRoot\..\..
foreach ($t in $Timecodes) {
  $safe = ($t -replace "[:.]", "-")
  $out = Join-Path $SessionDir "lookup\marker-$safe.json"
  New-Item -ItemType Directory -Force -Path (Split-Path $out) | Out-Null
  python scripts/browser_session_capture.py at $SessionDir --t $t --crop |
    Out-File -Encoding utf8 $out
  Write-Host "Wrote $out"
}
