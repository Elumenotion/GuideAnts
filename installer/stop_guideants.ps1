<#
Stop the GuideAnts stack that was started by guideants.ps1.

Reads saved component selections from .installer_state.env and runs
docker compose down on the matching compose fragment list.

Flags:
  --help   Show this help.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:RootDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$script:DockerDir = Join-Path $script:RootDir 'docker'
$script:EnvFile = Join-Path $script:DockerDir '.env'
$script:StateFile = Join-Path $script:RootDir '.installer_state.env'

. (Join-Path $script:RootDir 'scripts/installer-wizard.ps1')

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

Reads saved selections from .installer_state.env and runs docker compose down.

Flags:
  --help   Show this help.
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
            '--help' { Show-Usage; exit 0 }
            '-h'     { Show-Usage; exit 0 }
            default  { Stop-WithError "Unknown option: $($rawArgs[$i]) (try --help)" }
        }
    }
}

function Invoke-Main {
    Parse-Arguments @args

    if (-not (Test-Path -LiteralPath $script:StateFile)) {
        Stop-WithError "No saved state found ($($script:StateFile)). Run guideants.ps1 first."
    }

    $built = Build-InstallerComposeArgsFromState `
        -RootDir $script:RootDir `
        -StateFile $script:StateFile `
        -IncludeHostMountOverride `
        -IncludeVoicePackOverride `
        -IncludeRocmOverride

    $selection = $built.Selection
    Write-Log "Stopping GuideAnts (DB=$($selection.DbLayout), AI=$($selection.AiBackend))..."

    if ($selection.AiBackend -eq 'rocm') {
        $helper = Join-Path $script:RootDir 'scripts/rocm-runtime-compose.ps1'
        if (Test-Path -LiteralPath $helper) {
            & $helper -DockerDir $script:DockerDir -Backend 'rocm' -RootDir $script:RootDir
        }
    }

    Invoke-External -FilePath 'docker' -ArgumentList (@('compose') + $built.ComposeArgs + @('--env-file', $script:EnvFile, 'down'))
    Write-Log 'GuideAnts stopped.'
}

try {
    Invoke-Main @args
}
catch {
    [Console]::Error.WriteLine("[guideants][error] $($_.Exception.Message)")
    exit 1
}
