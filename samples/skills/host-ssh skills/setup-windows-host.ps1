#Requires -RunAsAdministrator
<#
.SYNOPSIS
  One-time OpenSSH Server setup for GuideAnts sandbox host access.

.DESCRIPTION
  - Installs OpenSSH Server (Windows Capability)
  - Configures sshd for local user GuideAnts (password auth)
  - Inserts AllowUsers / auth settings BEFORE "Match Group administrators"

  After this script:
    1. Set guide Environment: GA_HOST_SSH_USER, GA_HOST_SSH_PASSWORD, GA_HOST_SSH_HOST
    2. Test: ssh GuideAnts@localhost
    3. Test sandbox: python3 Skills/host-ssh/scripts/preflight.py --for probe
#>
$ErrorActionPreference = 'Stop'
$UserName = 'GuideAnts'

Write-Host "=== OpenSSH setup for sandbox user '$UserName' ===" -ForegroundColor Cyan

if (-not (Get-LocalUser -Name $UserName -ErrorAction SilentlyContinue)) {
    throw "Local user '$UserName' not found. Create it first (see README.md)."
}

$isAdmin = Get-LocalGroupMember -Group 'Administrators' -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match "\\$UserName$" }
if ($isAdmin) {
    throw "'$UserName' must not be in Administrators. Remove and re-run."
}

$cap = Get-WindowsCapability -Online |
    Where-Object { $_.Name -like 'OpenSSH.Server*' -and $_.State -ne 'Installed' }
if ($cap) {
    Write-Host "Installing OpenSSH Server..."
    Add-WindowsCapability -Online -Name $cap.Name | Out-Null
}

Set-Service sshd -StartupType Automatic
Start-Service sshd
Enable-NetFirewallRule -DisplayGroup 'Windows Remote Management' -ErrorAction SilentlyContinue | Out-Null
$fw = Get-NetFirewallRule -Name 'OpenSSH-Server-In-TCP' -ErrorAction SilentlyContinue
if (-not $fw) {
    New-NetFirewallRule -Name 'OpenSSH-Server-In-TCP' -DisplayName 'OpenSSH SSH Server (sshd)' `
        -Enabled True -Direction Inbound -Protocol TCP -Action Allow -LocalPort 22 | Out-Null
} else {
    Enable-NetFirewallRule -Name 'OpenSSH-Server-In-TCP' | Out-Null
}

$cfg = 'C:\ProgramData\ssh\sshd_config'
$default = @(
    'C:\Program Files\OpenSSH\sshd_config_default',
    'C:\Windows\System32\OpenSSH\sshd_config_default'
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $default) { throw 'sshd_config_default not found' }

$lines = Get-Content $default
$insert = @(
    ''
    "AllowUsers $UserName"
    'StrictModes no'
    'PubkeyAuthentication yes'
    'PasswordAuthentication yes'
    ''
)
$out = [System.Collections.Generic.List[string]]::new()
foreach ($line in $lines) {
    if ($line -match '^Match Group administrators') {
        foreach ($extra in $insert) { [void]$out.Add($extra) }
    }
    [void]$out.Add($line)
}
$out | Set-Content -Path $cfg -Encoding ascii

$test = & 'C:\Windows\System32\OpenSSH\sshd.exe' -t -f $cfg 2>&1
if ($LASTEXITCODE -ne 0) { throw "sshd_config invalid: $test" }

Restart-Service sshd
Write-Host "sshd:" (Get-Service sshd).Status -ForegroundColor Green
Write-Host @"

Next steps:
  1. Set guide Environment variables (see samples/skills/host-ssh skills/README.md)
  2. If host content lives on a network share (desktop R:), run setup-guideants-share-access.ps1
  3. Test: ssh ${UserName}@localhost
  4. Sandbox: python3 Skills/host-ssh/scripts/preflight.py --for probe

"@
