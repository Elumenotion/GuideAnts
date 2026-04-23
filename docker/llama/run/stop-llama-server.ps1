param(
    [string]$ContainerName = 'llama-router-server',
    [switch]$Remove
)

$ErrorActionPreference = 'Stop'

$existing = docker ps -a --format '{{.Names}}' | Where-Object { $_ -eq $ContainerName }
if (-not $existing) {
    Write-Host "Container '$ContainerName' does not exist."
    exit 0
}

if ($Remove) {
    docker rm -f $ContainerName
}
else {
    docker stop $ContainerName
}
