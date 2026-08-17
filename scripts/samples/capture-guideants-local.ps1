# Local GuideAnts + reference tab on primary monitor.
Set-Location $PSScriptRoot\..\..
python scripts/browser_session_capture.py start `
  --monitor 1 `
  --slug "guideants-walkthrough" `
  --url http://localhost:5107 `
  --url https://github.com
