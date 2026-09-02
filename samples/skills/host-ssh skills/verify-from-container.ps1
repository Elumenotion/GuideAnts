#Requires -Version 5.1
<#
.SYNOPSIS
  Verify host-ssh maps R: from the guideants-ai container.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$SshPassword,

    [string]$SharePassword = '',

    [string]$SshUser = 'GuideAnts',
    [string]$SshHost = 'host.docker.internal',
    [string]$ShareUnc = '\\FILESERVER\repos',
    [string]$ShareUser = 'DOMAIN\GuideAnts',
    [string]$ContainerName = 'guideants-ai',
    [string]$ContainerCwd = ''  # absolute path to the notebook Output dir inside the container
)


$ErrorActionPreference = 'Stop'

if (-not $SharePassword) {
    $SharePassword = $SshPassword
}

$bash = @"
export GA_HOST_SSH_USER='$($SshUser -replace "'", "'\\''")'
export GA_HOST_SSH_PASSWORD='$($SshPassword -replace "'", "'\\''")'
export GA_HOST_SSH_HOST='$($SshHost -replace "'", "'\\''")'
export GA_HOST_SSH_SHARE_UNC='$($ShareUnc -replace "'", "'\\''")'
export GA_HOST_SSH_SHARE_USER='$($ShareUser -replace "'", "'\\''")'
export GA_HOST_SSH_SHARE_PASSWORD='$($SharePassword -replace "'", "'\\''")'
if ($ContainerCwd) { cd $ContainerCwd }
python3 Skills/host-ssh/scripts/host_ssh.py run - --timeout 60 <<'PS'
Test-Path -LiteralPath 'R:\'
PS
"@

Write-Host "Testing from $ContainerName -> host SSH -> R:\ ..." -ForegroundColor Cyan
$bash | docker exec -i $ContainerName bash -s
if ($LASTEXITCODE -ne 0) {
    throw "host_ssh verification failed (exit $LASTEXITCODE)"
}

Write-Host "OK — R:\ reachable from container via host SSH." -ForegroundColor Green
