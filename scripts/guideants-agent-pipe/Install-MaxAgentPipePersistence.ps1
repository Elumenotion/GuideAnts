#Requires -Version 5.1
<#
.SYNOPSIS
  Install Max pipe listener on Max C: and register a logon scheduled task (survives reboot).

.NOTES
  Persistent home: C:\Users\dougl\guideants-agent-pipe
  Task: GuideAntsMaxPipe (At logon, current user, StartWhenAvailable, restart on failure)
#>
param(
    [string]$InstallDir = 'C:\Users\dougl\guideants-agent-pipe',
    [string]$TaskName = 'GuideAntsMaxPipe',
    [string]$SourceDir = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $SourceDir) {
    $SourceDir = Split-Path -Parent $MyInvocation.MyCommand.Path
}

$files = @('Start-MaxAgentPipe.ps1', 'start.cmd')
foreach ($f in $files) {
    $src = Join-Path $SourceDir $f
    if (-not (Test-Path -LiteralPath $src)) { throw "Missing $src" }
}

New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
foreach ($f in $files) {
    Copy-Item -Force (Join-Path $SourceDir $f) (Join-Path $InstallDir $f)
}

# Keep the already-issued token (do not print). Prefer source (known client), else keep dest.
$destToken = Join-Path $InstallDir 'token'
$srcToken = Join-Path $SourceDir 'token'
if (Test-Path -LiteralPath $srcToken) {
    Copy-Item -Force $srcToken $destToken
}

$ps = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
$script = Join-Path $InstallDir 'Start-MaxAgentPipe.ps1'
$arg = "-NoProfile -ExecutionPolicy Bypass -File `"$script`" -Detach"

$action = New-ScheduledTaskAction -Execute $ps -Argument $arg -WorkingDirectory $InstallDir
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
$settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -StartWhenAvailable `
    -ExecutionTimeLimit ([TimeSpan]::Zero) `
    -RestartCount 3 `
    -RestartInterval (New-TimeSpan -Minutes 1)
$principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel Highest

Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings -Principal $principal | Out-Null

# Start now so we don't wait for next logon.
& $ps -NoProfile -ExecutionPolicy Bypass -File $script -Detach

$task = Get-ScheduledTask -TaskName $TaskName
[pscustomobject]@{
    installDir = $InstallDir
    taskName   = $TaskName
    taskState  = [string]$task.State
    script     = $script
    token      = (Test-Path -LiteralPath $destToken)
} | ConvertTo-Json -Compress
