#Requires -Version 7.0
<#
.SYNOPSIS
  One-time (or repeat) setup for local client + server development on Windows.

.DESCRIPTION
  Installs/checks prerequisites, client npm deps, server JWT user-secrets,
  and starts Docker dependency services from docker/docker-compose.{backend}.yml
  (localhost ports are defined in those stacks; see docker/.env.api-local-debug.example).

  Full-stack Docker (installer) and host API dev both use port 5107 for the
  containerized app. This script stops guideants-webapi-ui so the host API can
  bind to http://localhost:5106 while dependencies stay in Docker.

.EXAMPLE
  pwsh -File scripts/setup-dev-environment.ps1
  pwsh -File scripts/setup-dev-environment.ps1 -SkipDocker
#>
param(
    [switch]$SkipDocker,
    [switch]$SkipNpmInstall
)

$ErrorActionPreference = 'Stop'
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

function Test-Command([string]$Name) {
    return [bool](Get-Command $Name -ErrorAction SilentlyContinue)
}

function Write-Step([string]$Message) {
    Write-Host "`n==> $Message" -ForegroundColor Cyan
}

function Ensure-PathContains([string[]]$Paths) {
    $merged = @($Paths + ($env:PATH -split ';')) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -Unique
    $env:PATH = ($merged -join ';')
}

Write-Step 'Checking prerequisites'
$missing = @()
foreach ($tool in @('git', 'docker', 'pwsh', 'node', 'npm', 'dotnet')) {
    if (-not (Test-Command $tool)) {
        $missing += $tool
    }
}
if ($missing.Count -gt 0) {
    throw "Missing tools: $($missing -join ', '). See docs/developer-config-guide.md section 0."
}

Ensure-PathContains @(
    'C:\Program Files\dotnet',
    'C:\Program Files\nodejs',
    "$env:USERPROFILE\.dotnet"
)

Write-Host "  git:    $(git --version)"
Write-Host "  docker: $(docker --version)"
Write-Host "  node:   $(node --version)"
Write-Host "  dotnet: $(dotnet --version)"

if (-not $SkipNpmInstall) {
    Write-Step 'Installing client dependencies'
    Push-Location (Join-Path $RepoRoot 'src/client')
    npm install
    Pop-Location
}

Write-Step 'Configuring server JWT user-secrets'
Push-Location (Join-Path $RepoRoot 'src/server/GuideAntsApi')
$jwtKey = dotnet user-secrets get 'Jwt:SigningKey' 2>$null
if ([string]::IsNullOrWhiteSpace($jwtKey)) {
    dotnet user-secrets set 'Jwt:SigningKey' 'GuideAnts-Local-Dev-Signing-Key-32chars-min' | Out-Null
    Write-Host '  Set Jwt:SigningKey (local dev only).'
} else {
    Write-Host '  Jwt:SigningKey already configured.'
}
$sandboxKey = dotnet user-secrets get 'SandboxWireApi:SigningKey' 2>$null
if ([string]::IsNullOrWhiteSpace($sandboxKey)) {
    dotnet user-secrets set 'SandboxWireApi:SigningKey' 'GuideAnts-Local-Sandbox-Wire-Key-32chars' | Out-Null
    Write-Host '  Set SandboxWireApi:SigningKey (local dev only).'
} else {
    Write-Host '  SandboxWireApi:SigningKey already configured.'
}
Pop-Location

Write-Step 'Building server (GuideAntsApi)'
Push-Location (Join-Path $RepoRoot 'src/server')
dotnet build GuideAntsApi/GuideAntsApi.csproj --configuration Debug
Pop-Location

Write-Step 'Typechecking client'
Push-Location (Join-Path $RepoRoot 'src/client')
npm run typecheck
Pop-Location

function Get-DevComposeFile([string]$Backend) {
    switch ($Backend.ToLowerInvariant()) {
        'slim' { return 'docker-compose.slim.yml' }
        'cuda13' { return 'docker-compose.cuda.yml' }
        'cuda' { return 'docker-compose.cuda.yml' }
        'rocm' { return 'docker-compose.rocm.yml' }
        'vulkan' { return 'docker-compose.vulkan.yml' }
        default { return 'docker-compose.cpu.yml' }
    }
}

if (-not $SkipDocker) {
    Write-Step 'Starting Docker dependencies for host API development'
    $dockerDir = Join-Path $RepoRoot 'docker'
    $envFile = Join-Path $dockerDir '.env'
    $backend = 'cpu'
    $stateFile = Join-Path $RepoRoot 'installer/.installer_state.env'
    if (Test-Path $stateFile) {
        $match = Select-String -Path $stateFile -Pattern '^BACKEND=(.+)$'
        if ($match) {
            $backend = $match.Matches.Groups[1].Value.Trim()
        }
    }

    $composeFile = Get-DevComposeFile -Backend $backend
    if (-not (Test-Path (Join-Path $dockerDir $composeFile))) {
        Write-Host "  Compose file $composeFile not found; falling back to docker-compose.cpu.yml."
        $composeFile = 'docker-compose.cpu.yml'
    }

    $composeArgs = @('-f', $composeFile)

    if ($backend -eq 'rocm') {
        $rocmScript = Join-Path $RepoRoot 'installer/scripts/rocm-runtime-compose.ps1'
        if (Test-Path $rocmScript) {
            & $rocmScript -DockerDir $dockerDir -Backend 'rocm'
        }
        $rocmOverride = 'docker-compose.rocm-runtime.generated.yml'
        if (Test-Path (Join-Path $dockerDir $rocmOverride)) {
            $composeArgs += @('-f', $rocmOverride)
        }
    }

    $hostMountOverride = 'docker-compose.host-mounts.generated.yml'
    if (Test-Path (Join-Path $dockerDir $hostMountOverride)) {
        $composeArgs += @('-f', $hostMountOverride)
    }

    $voicePackOverride = 'docker-compose.voice-pack.local.yml'
    if (Test-Path (Join-Path $dockerDir $voicePackOverride)) {
        $composeArgs += @('-f', $voicePackOverride)
    }

    $depServices = @(
        'mssql-express',
        'guideants-ai',
        'docling-serve',
        'documentserver',
        'plantuml',
        'searxng'
    )

    Push-Location $dockerDir
    docker compose @composeArgs --env-file $envFile stop guideants-webapi-ui 2>$null
    docker compose @composeArgs --env-file $envFile up -d --force-recreate @depServices
    Pop-Location
}

Write-Step 'Done'
Write-Host @"

Run the stack locally:

  Terminal 1 (API):
    cd src/server/GuideAntsApi
    dotnet run --launch-profile http

  Terminal 2 (client):
    cd src/client
    npm run browser:dev

  API:    http://localhost:5106
  Client: http://localhost:5173  (proxies /api to :5106)

To run the full Docker app again (no host dev):
    cd installer
    ./guideants.ps1

See docs/developer-config-guide.md for details.
"@
