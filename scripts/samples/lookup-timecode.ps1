param(
  [Parameter(Mandatory)][string]$SessionDir,
  [Parameter(Mandatory)][string]$Timecode,
  [switch]$Crop
)

Set-Location $PSScriptRoot\..\..
$cliArgs = @("scripts/browser_session_capture.py", "at", $SessionDir, "--t", $Timecode)
if ($Crop) { $cliArgs += "--crop" }
python @cliArgs
