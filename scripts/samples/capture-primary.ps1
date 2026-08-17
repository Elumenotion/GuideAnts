# Capture on primary monitor (2560x1440 @ 0,0).
Set-Location $PSScriptRoot\..\..
$slug = "demo-$(Get-Date -Format 'yyyyMMdd-HHmm')"
python scripts/browser_session_capture.py start `
  --monitor 1 `
  --fps 30 `
  --slug $slug `
  --url http://localhost:5107 `
  --url https://example.com
