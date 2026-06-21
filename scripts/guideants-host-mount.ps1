# Rewrites docker/docker-compose.host-mounts.generated.yml and restarts affected services.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateSet('apply', 'remove')]
    [string]$Verb,

    [Parameter(Mandatory = $true)]
    [string]$MountId,

    [Parameter()]
    [string]$HostPath,

    [Parameter()]
    [string]$ProjectId
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RootDir = Split-Path -Parent $ScriptDir
$StateFile = Join-Path $RootDir '.installer_state.env'
$DefaultApiBase = 'http://localhost:5107'
$ApiPlanPath = '/api/internal/host-folder-mounts'
$AffectedMountServiceNames = @(
    'guideants-webapi-ui',
    'guideants-ai',
    'plantuml'
)

function Write-Log {
    param([Parameter(Mandatory = $true)][string]$Message)
    Write-Host "[guideants-host-mount] $Message"
}

function Write-Warn {
    param([Parameter(Mandatory = $true)][string]$Message)
    Write-Warning "[guideants-host-mount] $Message"
}

function Stop-WithError {
    param([Parameter(Mandatory = $true)][string]$Message)
    throw "[guideants-host-mount][error] $Message"
}

function Get-InstallerState {
    if (-not (Test-Path -LiteralPath $StateFile)) {
        Stop-WithError "Missing $StateFile. Run a start_* launcher first."
    }

    $state = @{
        ComposeFile = $null
        HostMountOverrideFile = 'docker-compose.host-mounts.generated.yml'
        DockerDirectory = 'docker'
    }

    foreach ($line in Get-Content -LiteralPath $StateFile) {
        $trimmed = $line.Trim()
        if ([string]::IsNullOrWhiteSpace($trimmed) -or $trimmed.StartsWith('#')) {
            continue
        }

        $separatorIndex = $trimmed.IndexOf('=')
        if ($separatorIndex -lt 1) {
            continue
        }

        $key = $trimmed.Substring(0, $separatorIndex).Trim()
        $value = $trimmed.Substring($separatorIndex + 1).Trim()
        switch ($key) {
            'COMPOSE_FILE' { $state.ComposeFile = $value }
            'HOST_MOUNT_OVERRIDE_FILE' { $state.HostMountOverrideFile = $value }
            'DOCKER_DIRECTORY' { $state.DockerDirectory = $value }
        }
    }

    if ([string]::IsNullOrWhiteSpace($state.ComposeFile)) {
        Stop-WithError "COMPOSE_FILE is missing from $StateFile. Run a start_* launcher first."
    }

    return $state
}

function Get-SafeMountKey {
    param([Parameter(Mandatory = $true)][string]$MountIdValue)

    if ($MountIdValue -match '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$') {
        return ($MountIdValue -replace '-', '').ToLowerInvariant()
    }

    $key = ($MountIdValue.ToLowerInvariant() -replace '[^a-z0-9-]+', '-' -replace '^-+', '' -replace '-+$', '')
    if ([string]::IsNullOrWhiteSpace($key)) {
        Stop-WithError "Mount id '$MountIdValue' does not produce a filesystem-safe mount key."
    }

    return $key
}

function Convert-HostPathForCompose {
    param([Parameter(Mandatory = $true)][string]$PathValue)

    if ($PathValue -match '^[A-Za-z]:\\') {
        $drive = $PathValue.Substring(0, 1)
        $rest = $PathValue.Substring(2) -replace '\\', '/'
        return "$drive`:/$($rest.TrimStart('/'))"
    }

    return ($PathValue -replace '\\', '/')
}

function Get-ApiBaseUrl {
    if (-not [string]::IsNullOrWhiteSpace($env:GUIDEANTS_API_BASE)) {
        return $env:GUIDEANTS_API_BASE.TrimEnd('/')
    }

    return $DefaultApiBase
}

function Get-ComposePlanFromApi {
    param([Parameter(Mandatory = $true)][string]$MountIdValue)

    $apiBase = Get-ApiBaseUrl
    $uri = "$apiBase$ApiPlanPath/$MountIdValue/compose-override-plan"
    $headers = @{}
    if (-not [string]::IsNullOrWhiteSpace($env:GUIDEANTS_MOUNT_API_TOKEN)) {
        $headers.Authorization = "Bearer $($env:GUIDEANTS_MOUNT_API_TOKEN)"
    }

    try {
        $response = Invoke-RestMethod -Method Get -Uri $uri -Headers $headers -ErrorAction Stop
    }
    catch {
        return $null
    }

    foreach ($requiredField in @('mountId', 'mountKey', 'sourceKind', 'hostPath')) {
        if (-not $response.PSObject.Properties.Name.Contains($requiredField) -or
            [string]::IsNullOrWhiteSpace([string]$response.$requiredField)) {
            return $null
        }
    }

    return [pscustomobject]@{
        MountId = [string]$response.mountId
        MountKey = [string]$response.mountKey
        SourceKind = [string]$response.sourceKind
        HostPath = [string]$response.hostPath
        ProjectId = if ($response.PSObject.Properties.Name.Contains('projectId')) { [string]$response.projectId } else { '' }
    }
}

function Resolve-ApplyPlan {
    param(
        [Parameter(Mandatory = $true)][string]$MountIdValue,
        [string]$HostPathValue,
        [string]$ProjectIdValue
    )

    $apiPlan = Get-ComposePlanFromApi -MountIdValue $MountIdValue
    if ($null -ne $apiPlan) {
        if ([string]::IsNullOrWhiteSpace($apiPlan.HostPath)) {
            Stop-WithError 'Compose plan host path is empty.'
        }

        if (-not [string]::IsNullOrWhiteSpace($HostPathValue) -and $HostPathValue -ne $apiPlan.HostPath) {
            Stop-WithError 'Host path on the command line does not match the API mount plan.'
        }

        return $apiPlan
    }

    if ([string]::IsNullOrWhiteSpace($HostPathValue)) {
        Stop-WithError 'Could not fetch compose plan from the GuideAnts API and -HostPath was not provided.'
    }

    return [pscustomobject]@{
        MountId = $MountIdValue
        MountKey = Get-SafeMountKey -MountIdValue $MountIdValue
        SourceKind = 'LocalPath'
        HostPath = Convert-HostPathForCompose -PathValue $HostPathValue
        ProjectId = $ProjectIdValue
    }
}

function Read-ExistingMounts {
    param([Parameter(Mandatory = $true)][string]$OverridePath)

    $mounts = [ordered]@{}
    if (-not (Test-Path -LiteralPath $OverridePath)) {
        return $mounts
    }

    $currentMountId = $null
    $currentMountKey = $null
    $currentSourceKind = $null
    $currentHostPath = $null
    $inBindBlock = $false

    foreach ($line in Get-Content -LiteralPath $OverridePath) {
        if ($line -match 'guideants-host-mount:\s+mount-id=(?<mountId>\S+)\s+mount-key=(?<mountKey>\S+)\s+source-kind=(?<sourceKind>\S+)') {
            $currentMountId = $Matches.mountId
            $currentMountKey = $Matches.mountKey
            $currentSourceKind = $Matches.sourceKind
            $currentHostPath = $null
            $inBindBlock = $false
            continue
        }

        if ($null -ne $currentMountKey -and $line -match '^\s*source:\s*(?<hostPath>.+)$') {
            $currentHostPath = $Matches.hostPath.Trim()
            $inBindBlock = $true
            continue
        }

        if ($inBindBlock -and $line -match '^\s*target:\s*/app/HostMounts/') {
            $mounts[$currentMountKey] = [pscustomobject]@{
                MountId = $currentMountId
                MountKey = $currentMountKey
                SourceKind = $currentSourceKind
                HostPath = $currentHostPath
            }
            $currentMountId = $null
            $currentMountKey = $null
            $currentSourceKind = $null
            $currentHostPath = $null
            $inBindBlock = $false
        }
    }

    return $mounts
}

function Write-LocalBindBlock {
    param(
        [Parameter(Mandatory = $true)][string]$MountIdValue,
        [Parameter(Mandatory = $true)][string]$MountKeyValue,
        [Parameter(Mandatory = $true)][string]$HostPathValue
    )

    if ([string]::IsNullOrWhiteSpace($HostPathValue)) {
        Stop-WithError "Mount '$MountIdValue' has an empty host path; refusing to write an invalid compose override."
    }

    "      # guideants-host-mount: mount-id=$MountIdValue mount-key=$MountKeyValue source-kind=LocalPath"
    '      - type: bind'
    "        source: $HostPathValue"
    "        target: /app/HostMounts/$MountKeyValue"
    '        read_only: false'
}

function Write-OverrideFile {
    param(
        [Parameter(Mandatory = $true)][string]$OverridePath,
        [Parameter(Mandatory = $true)][System.Collections.IDictionary]$Mounts
    )

    if ($Mounts.Count -eq 0) {
        if (Test-Path -LiteralPath $OverridePath) {
            Remove-Item -LiteralPath $OverridePath -Force
        }
        return
    }

    $lines = New-Object System.Collections.Generic.List[string]
    $null = $lines.Add('# Generated by scripts/guideants-host-mount.* — do not edit manually.')
    $null = $lines.Add('# Host folder mount Compose override. Managed idempotently by guideants-host-mount scripts.')
    $null = $lines.Add('')
    $null = $lines.Add('services:')

    $services = $Script:AffectedMountServiceNames
    foreach ($service in $services) {
        $null = $lines.Add("  $service`:")
        $null = $lines.Add('    volumes:')
        foreach ($mountKey in ($Mounts.Keys | Sort-Object)) {
            $mount = $Mounts[$mountKey]
            if ($mount.SourceKind -eq 'LocalPath') {
                foreach ($blockLine in (Write-LocalBindBlock -MountIdValue $mount.MountId -MountKeyValue $mount.MountKey -HostPathValue $mount.HostPath)) {
                    $null = $lines.Add($blockLine)
                }
            }
            else {
                Stop-WithError "Unsupported source kind '$($mount.SourceKind)' in override rewrite (SMB branch not implemented)."
            }
        }
    }

    $text = ($lines -join "`n") + "`n"
    [System.IO.File]::WriteAllText($OverridePath, $text, [System.Text.UTF8Encoding]::new($false))
}

function Invoke-ApplyMount {
    param(
        [Parameter(Mandatory = $true)][object]$InstallerState,
        [Parameter(Mandatory = $true)][object]$Plan
    )

    if ($Plan.SourceKind -ne 'LocalPath') {
        Stop-WithError 'Only LocalPath mounts are implemented in this phase.'
    }

    $overridePath = Join-Path $RootDir (Join-Path $InstallerState.DockerDirectory $InstallerState.HostMountOverrideFile)
    $mounts = Read-ExistingMounts -OverridePath $overridePath
    $mounts[$Plan.MountKey] = [pscustomobject]@{
        MountId = $Plan.MountId
        MountKey = $Plan.MountKey
        SourceKind = $Plan.SourceKind
        HostPath = $Plan.HostPath
    }
    Write-OverrideFile -OverridePath $overridePath -Mounts $mounts
}

function Invoke-RemoveMount {
    param(
        [Parameter(Mandatory = $true)][object]$InstallerState,
        [Parameter(Mandatory = $true)][string]$MountIdValue
    )

    $overridePath = Join-Path $RootDir (Join-Path $InstallerState.DockerDirectory $InstallerState.HostMountOverrideFile)
    $mounts = Read-ExistingMounts -OverridePath $overridePath
    $removed = $false
    foreach ($mountKey in @($mounts.Keys)) {
        if ($mounts[$mountKey].MountId -eq $MountIdValue) {
            $mounts.Remove($mountKey)
            $removed = $true
        }
    }

    if (-not $removed) {
        Write-Warn "No override block found for mount id '$MountIdValue'."
    }

    Write-OverrideFile -OverridePath $overridePath -Mounts $mounts
}

function Restart-AffectedServices {
    param([Parameter(Mandatory = $true)][object]$InstallerState)

    $overridePath = Join-Path $RootDir (Join-Path $InstallerState.DockerDirectory $InstallerState.HostMountOverrideFile)
    $composeArgs = @('-f', $InstallerState.ComposeFile)
    if (Test-Path -LiteralPath $overridePath) {
        $composeArgs += @('-f', $InstallerState.HostMountOverrideFile)
    }

    $services = $Script:AffectedMountServiceNames
    Write-Log "Restarting affected services (--no-deps): $($services -join ' ')"
    Push-Location (Join-Path $RootDir $InstallerState.DockerDirectory)
    try {
        & docker compose @composeArgs up -d --no-deps @services
        if ($LASTEXITCODE -ne 0) {
            throw "docker compose exited with code $LASTEXITCODE"
        }
    }
    finally {
        Pop-Location
    }
}

$installerState = Get-InstallerState
$plan = $null

switch ($Verb) {
    'apply' {
        $plan = Resolve-ApplyPlan -MountIdValue $MountId -HostPathValue $HostPath -ProjectIdValue $ProjectId
        Invoke-ApplyMount -InstallerState $installerState -Plan $plan
        Restart-AffectedServices -InstallerState $installerState
    }
    'remove' {
        Invoke-RemoveMount -InstallerState $installerState -MountIdValue $MountId
        Restart-AffectedServices -InstallerState $installerState
    }
}

Write-Log 'Done.'
