#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Store network-share credentials for the GuideAnts local user on this host OS.

.DESCRIPTION
  Mapped drive letters (e.g. R:) in your desktop session do NOT appear in GuideAnts
  SSH sessions — Windows isolates sessions. This script stores share credentials in
  GuideAnts's Credential Manager and verifies UNC access.

  Prerequisite on the machine that hosts the share (the share server: repos -> C:\repos):
    - Local user GuideAnts (same password you choose here is fine)
    - Grant-SmbShareAccess -Name repos -AccountName 'GuideAnts' -AccessRight Change -Force
    - icacls on the share folder for GuideAnts

.PARAMETER ShareUnc
  UNC root, e.g. \\FILESERVER\repos (same target as your R: drive).

.PARAMETER ShareUser
  Account the share accepts, e.g. DOMAIN\GuideAnts (local user on the share server).

.PARAMETER GuideAntsUser
  Local sandbox SSH account on this host OS (default GuideAnts).

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\setup-guideants-share-access.ps1 `
    -ShareUnc '\\FILESERVER\repos' -ShareUser 'DOMAIN\GuideAnts'
#>
[CmdletBinding()]
param(
    [string]$ShareUnc = '\\FILESERVER\repos',
    [string]$ShareUser = 'DOMAIN\GuideAnts',
    [string]$GuideAntsUser = 'GuideAnts'
)

$ErrorActionPreference = 'Stop'

if (-not (Get-LocalUser -Name $GuideAntsUser -ErrorAction SilentlyContinue)) {
    throw "Local user '$GuideAntsUser' not found. Run setup-windows-host.ps1 first."
}

if ($ShareUnc -notmatch '^\\\\[^\\]+\\[^\\]+') {
    throw "ShareUnc must look like \\SERVER\share (got '$ShareUnc')."
}

$shareHost = ($ShareUnc -replace '^\\\\([^\\]+)\\.*$', '$1')
$sharePassword = Read-Host "Password for $ShareUser (share access)" -AsSecureString
$guideAntsPassword = Read-Host "Password for local $GuideAntsUser (SSH sandbox user)" -AsSecureString

$bstrShare = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($sharePassword)
$sharePasswordPlain = [Runtime.InteropServices.Marshal]::PtrToStringAuto($bstrShare)
[Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstrShare)

$taskName = 'GuideAntsShareAccessOnce'
$inner = @"
`$ErrorActionPreference = 'Stop'
cmdkey /delete:$shareHost 2>`$null | Out-Null
cmdkey /add:$shareHost /user:$ShareUser /pass:$sharePasswordPlain
net use $ShareUnc /delete /y 2>`$null | Out-Null
net use $ShareUnc /user:$ShareUser $sharePasswordPlain
if (-not (Test-Path -LiteralPath '$ShareUnc')) { throw 'UNC path not reachable after net use' }
"@

$scriptPath = Join-Path $env:TEMP "guideants-share-setup-$GuideAntsUser.ps1"
[System.IO.File]::WriteAllText($scriptPath, $inner, [System.Text.UTF8Encoding]::new($false))

try {
    $action = New-ScheduledTaskAction `
        -Execute "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe" `
        -Argument "-NoProfile -ExecutionPolicy Bypass -File `"$scriptPath`""

    $bstrGuide = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($guideAntsPassword)
    $guideAntsPasswordPlain = [Runtime.InteropServices.Marshal]::PtrToStringAuto($bstrGuide)
    [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstrGuide)

    Register-ScheduledTask -TaskName $taskName -Action $action `
        -User $GuideAntsUser -Password $guideAntsPasswordPlain -Force | Out-Null
    Start-ScheduledTask -TaskName $taskName
    Wait-ScheduledTask -TaskName $taskName -Timeout (New-TimeSpan -Seconds 30)

    $result = (Get-ScheduledTaskInfo -TaskName $taskName).LastTaskResult
    if ($result -ne 0) {
        throw "Scheduled task failed with exit code $result. Check Task Scheduler history."
    }
}
finally {
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $scriptPath -Force -ErrorAction SilentlyContinue
}

Write-Host "Credential Manager entry for $shareHost stored for $GuideAntsUser." -ForegroundColor Green
Write-Host @"

Guide Environment (add to the guide editor):
  GA_HOST_SSH_SHARE_UNC = $ShareUnc
  GA_HOST_SSH_SHARE_USER = $ShareUser
  GA_HOST_SSH_SHARE_PASSWORD = (same share password, mark secret)

Optional:
  GA_HOST_SSH_SHARE_DRIVE = R

Test on this host OS:
  ssh ${GuideAntsUser}@localhost powershell.exe -NoProfile -Command "Test-Path -LiteralPath 'R:\'"

"@ -ForegroundColor Cyan
