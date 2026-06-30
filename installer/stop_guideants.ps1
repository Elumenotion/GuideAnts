<#
Stop the GuideAnts stack that was started by guideants.ps1.

Reads the saved backend/compose from .installer_state.env and runs
docker compose down on the matching compose file(s).

Flags:
  --backend <cpu|cuda13|rocm|slim|vulkan>   Override the saved backend.
  --compose <ghcr|local>                    Compose mode when using --backend (default: ghcr).
  --help                                    Show this help.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:RootDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$script:DockerDir = Join-Path $script:RootDir 'docker'
$script:EnvFile = Join-Path $script:DockerDir '.env'
$script:StateFile = Join-Path $script:RootDir '.installer_state.env'

$script:BackendOverride = ''
$script:ComposeMode = 'ghcr'

function Write-Log {
    param([Parameter(Mandatory = $true)][string]$Message)
    Write-Host "[guideants] $Message"
}

function Stop-WithError {
    param([Parameter(Mandatory = $true)][string]$Message)
    throw $Message
}

function Show-Usage {
    @'
Stop the GuideAnts stack that was started by guideants.ps1.

  Windows       : .\stop_guideants.ps1
  Linux / macOS : pwsh ./stop_guideants.ps1

Reads the saved backend from .installer_state.env and runs docker compose down.

Flags:
  --backend <cpu|cuda13|rocm|slim|vulkan>   Override the saved backend.
  --compose <ghcr|local>                    Compose mode when using --backend (default: ghcr).
  --help                                    Show this help.
'@
}

function Get-LastExitCodeSafe {
    $var = Get-Variable -Name LASTEXITCODE -Scope Global -ErrorAction SilentlyContinue
    if ($null -eq $var -or $null -eq $var.Value) { return 0 }
    return [int]$var.Value
}

function Invoke-External {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [string[]]$ArgumentList = @()
    )
    & $FilePath @ArgumentList
    $exitCode = Get-LastExitCodeSafe
    if ($exitCode -ne 0) {
        Stop-WithError "$FilePath $($ArgumentList -join ' ') failed with exit code $exitCode."
    }
}

function Parse-Arguments {
    $rawArgs = @($args)
    for ($i = 0; $i -lt $rawArgs.Count; $i++) {
        switch ($rawArgs[$i]) {
            '--backend' {
                if ($i + 1 -ge $rawArgs.Count) { Stop-WithError 'Missing value for --backend' }
                $script:BackendOverride = [string]$rawArgs[$i + 1]
                $i++
            }
            '--compose' {
                if ($i + 1 -ge $rawArgs.Count) { Stop-WithError 'Missing value for --compose' }
                $script:ComposeMode = [string]$rawArgs[$i + 1]
                $i++
            }
            '--help' { Show-Usage; exit 0 }
            '-h'     { Show-Usage; exit 0 }
            default  { Stop-WithError "Unknown option: $($rawArgs[$i]) (try --help)" }
        }
    }

    if ($script:BackendOverride -ne '' -and $script:BackendOverride -notmatch '^(cpu|cuda13|rocm|slim|vulkan)$') {
        Stop-WithError '--backend must be cpu, cuda13, rocm, slim, or vulkan'
    }

    if ($script:ComposeMode -notin @('ghcr', 'local')) {
        Stop-WithError '--compose must be ghcr or local'
    }
}

function Get-InstallerStateValue {
    param([Parameter(Mandatory = $true)][string]$Key)
    if (-not (Test-Path -LiteralPath $script:StateFile)) { return $null }
    foreach ($line in Get-Content -LiteralPath $script:StateFile) {
        $trimmed = $line.Trim()
        if ([string]::IsNullOrWhiteSpace($trimmed) -or $trimmed.StartsWith('#')) { continue }
        $sep = $trimmed.IndexOf('=')
        if ($sep -lt 1) { continue }
        if ($trimmed.Substring(0, $sep).Trim() -eq $Key) {
            return $trimmed.Substring($sep + 1).Trim()
        }
    }
    return $null
}

function Get-ComposeFileForBackend {
    param(
        [Parameter(Mandatory = $true)][string]$Backend,
        [Parameter(Mandatory = $true)][string]$ComposeMode
    )
    if ($ComposeMode -eq 'local') {
        switch ($Backend) {
            'slim'   { return 'docker-compose.slim.yml' }
            'cuda13' { return 'docker-compose.cuda.yml' }
            'rocm'   { return 'docker-compose.rocm.yml' }
            'vulkan' { return 'docker-compose.vulkan.yml' }
            default  { return 'docker-compose.cpu.yml' }
        }
    }
    switch ($Backend) {
        'slim'   { return 'docker-compose.ghcr-slim.yml' }
        'cuda13' { return 'docker-compose.ghcr-cuda13.yml' }
        'rocm'   { return 'docker-compose.ghcr-rocm.yml' }
        'vulkan' { return 'docker-compose.ghcr-vulkan.yml' }
        default  { return 'docker-compose.ghcr-cpu.yml' }
    }
}

function Invoke-Main {
    Parse-Arguments @args

    if (-not [string]::IsNullOrWhiteSpace($script:BackendOverride)) {
        $backend = $script:BackendOverride
        $composeFile = Get-ComposeFileForBackend -Backend $backend -ComposeMode $script:ComposeMode
        Write-Log "Backend overridden: $backend  ->  docker/$composeFile"
    }
    elseif (Test-Path -LiteralPath $script:StateFile) {
        $backend = Get-InstallerStateValue -Key 'BACKEND'
        if ([string]::IsNullOrWhiteSpace($backend) -or $backend -notmatch '^(cpu|cuda13|rocm|slim|vulkan)$') {
            Stop-WithError "Saved backend '$backend' in $($script:StateFile) is invalid. Use --backend to specify one."
        }
        $composeFile = Get-InstallerStateValue -Key 'COMPOSE_FILE'
        if ([string]::IsNullOrWhiteSpace($composeFile)) {
            $composeFile = Get-ComposeFileForBackend -Backend $backend -ComposeMode $script:ComposeMode
        }
        Write-Log "Loaded state: backend=$backend  ->  docker/$composeFile"
    }
    else {
        Stop-WithError "No saved state found ($($script:StateFile)). Use --backend to specify one."
    }

    $composePath = Join-Path $script:DockerDir $composeFile
    if (-not (Test-Path -LiteralPath $composePath)) {
        Stop-WithError "Compose file not found: $composePath"
    }

    $composeArgs = @('-f', $composePath)

    $hostMountOverrideFile = Get-InstallerStateValue -Key 'HOST_MOUNT_OVERRIDE_FILE'
    if ([string]::IsNullOrWhiteSpace($hostMountOverrideFile)) {
        $hostMountOverrideFile = 'docker-compose.host-mounts.generated.yml'
    }
    $hostMountOverridePath = Join-Path $script:DockerDir $hostMountOverrideFile
    if (Test-Path -LiteralPath $hostMountOverridePath) {
        $composeArgs += @('-f', $hostMountOverridePath)
    }

    Write-Log "Stopping GuideAnts ($backend backend)..."
    Invoke-External -FilePath 'docker' -ArgumentList (@('compose') + $composeArgs + @('--env-file', $script:EnvFile, 'down'))
    Write-Log 'GuideAnts stopped.'
}

try {
    Invoke-Main @args
}
catch {
    [Console]::Error.WriteLine("[guideants][error] $($_.Exception.Message)")
    exit 1
}
