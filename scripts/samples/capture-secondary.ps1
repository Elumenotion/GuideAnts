# Capture on secondary monitor (2560x1440 @ 2560,0).
Set-Location $PSScriptRoot\..\..
python scripts/browser_session_capture.py start `
  --monitor 2 `
  --fps 30 `
  --slug "secondary-demo" `
  --url https://example.com
