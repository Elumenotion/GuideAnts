Set-Location $PSScriptRoot\..\..
$latest = Get-ChildItem recordings/sessions -Directory -ErrorAction SilentlyContinue |
  Sort-Object LastWriteTime -Descending |
  Select-Object -First 1
if ($latest) {
  explorer $latest.FullName
} else {
  Write-Host "No sessions found under recordings/sessions/"
}
