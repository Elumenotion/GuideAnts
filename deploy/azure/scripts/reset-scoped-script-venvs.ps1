# One-time migration: clear pre-mfsymlinks scoped python-venv trees on script-agent-state share.
# Fresh installs (no scopes / no venvs) write the marker via Azure Files — no running replica required.
param(
    [Parameter(Mandatory = $true)]
    [string]$ResourceGroupName,

    [string]$ShareName = "script-agent-state",
    [string]$ScopesPath = "scopes",
    [string]$AiAppName = "guideants-ai",
    [string]$StateMountPath = "/var/lib/guideants/script-agent-admin",
    [string]$ResetMarkerPath = ".guideants/mfsymlinks-venv-reset.done",
    [int]$ReplicaWaitSeconds = 300,
    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Write-Status { param([string]$Message) Write-Host "[INFO] $Message" -ForegroundColor Blue }
function Write-Success { param([string]$Message) Write-Host "[SUCCESS] $Message" -ForegroundColor Green }

$script:MfsymlinksMountOptions = "mfsymlinks,nobrl,file_mode=0755,dir_mode=0755"

function Get-StorageContext {
    $storageAccountName = az storage account list --resource-group $ResourceGroupName --query "[0].name" -o tsv
    if (-not $storageAccountName) {
        throw "Storage account not found in resource group '$ResourceGroupName'."
    }

    $storageKey = az storage account keys list `
        --resource-group $ResourceGroupName `
        --account-name $storageAccountName `
        --query "[0].value" -o tsv
    if (-not $storageKey) {
        throw "Could not read storage account key for '$storageAccountName'."
    }

    return @{
        AccountName = $storageAccountName
        AccountKey  = $storageKey
    }
}

function Test-AiAppHasMfsymlinks {
    $appJson = az containerapp show --name $AiAppName --resource-group $ResourceGroupName -o json 2>$null
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($appJson)) {
        Write-Status "Container app '$AiAppName' not found; skipping scoped venv migration."
        return $false
    }

    $app = $appJson | ConvertFrom-Json
    $mountOptions = @(
        $app.properties.template.volumes |
            Where-Object { $_.name -eq "script-agent-state-volume" } |
            Select-Object -ExpandProperty mountOptions
    ) | Select-Object -First 1

    if ($mountOptions -ne $script:MfsymlinksMountOptions) {
        Write-Status "guideants-ai is not using mfsymlinks mount options yet; skipping scoped venv migration."
        return $false
    }

    return $true
}

function Test-ShareFileExists {
    param(
        [hashtable]$Storage,
        [string]$Path
    )

    $output = az storage file exists `
        --account-name $Storage.AccountName `
        --account-key $Storage.AccountKey `
        --share-name $ShareName `
        --path $Path `
        -o tsv 2>$null
    return $output -eq "True"
}

function Test-SharePathExists {
    param(
        [hashtable]$Storage,
        [string]$Path
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return $true
    }

    $output = az storage directory exists `
        --account-name $Storage.AccountName `
        --account-key $Storage.AccountKey `
        --share-name $ShareName `
        --name $Path `
        -o tsv 2>$null
    return $output -eq "True"
}

function Get-ShareChildren {
    param(
        [hashtable]$Storage,
        [string]$Path
    )

    if (-not (Test-SharePathExists -Storage $Storage -Path $Path)) {
        return @()
    }

    $childrenJson = az storage file list `
        --account-name $Storage.AccountName `
        --account-key $Storage.AccountKey `
        --share-name $ShareName `
        --path $Path `
        -o json
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to list Azure Files path '$Path' on share '$ShareName'."
    }

    return @($childrenJson | ConvertFrom-Json)
}

function Set-ShareFileContent {
    param(
        [hashtable]$Storage,
        [string]$Path,
        [string]$Content
    )

    $parent = ($Path -replace '/[^/]+$','')
    if ($parent -and $parent -ne $Path) {
        if (-not (Test-SharePathExists -Storage $Storage -Path $parent)) {
            az storage directory create `
                --account-name $Storage.AccountName `
                --account-key $Storage.AccountKey `
                --share-name $ShareName `
                --name $parent `
                --output none | Out-Null
        }
    }

    $tempFile = [System.IO.Path]::GetTempFileName()
    try {
        Set-Content -Path $tempFile -Value $Content -Encoding utf8NoBOM
        az storage file upload `
            --account-name $Storage.AccountName `
            --account-key $Storage.AccountKey `
            --share-name $ShareName `
            --path $Path `
            --source $tempFile `
            --overwrite `
            --output none | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to upload marker file '$Path' to share '$ShareName'."
        }
    }
    finally {
        Remove-Item -Path $tempFile -Force -ErrorAction SilentlyContinue
    }
}

function Remove-ShareFile {
    param(
        [hashtable]$Storage,
        [string]$Path
    )

    az storage file delete `
        --account-name $Storage.AccountName `
        --account-key $Storage.AccountKey `
        --share-name $ShareName `
        --path $Path `
        --output none | Out-Null
}

function Write-MigrationMarker {
    param(
        [hashtable]$Storage,
        [int]$RemovedVenvs = 0,
        [int]$RemovedAppliedState = 0
    )

    $markerBody = "completedUtc=$([DateTime]::UtcNow.ToString('o'))`nremovedVenvs=$RemovedVenvs`nremovedAppliedState=$RemovedAppliedState`n"
    Set-ShareFileContent -Storage $Storage -Path $ResetMarkerPath -Content $markerBody
}

function Get-ScopedVenvWorkOnShare {
    param([hashtable]$Storage)

    $venvPaths = [System.Collections.Generic.List[string]]::new()
    $appliedStatePaths = [System.Collections.Generic.List[string]]::new()

    if (-not (Test-SharePathExists -Storage $Storage -Path $ScopesPath)) {
        return @{
            VenvPaths = $venvPaths
            AppliedStatePaths = $appliedStatePaths
        }
    }

    foreach ($projectEntry in Get-ShareChildren -Storage $Storage -Path $ScopesPath) {
        if (-not $projectEntry.isDirectory) { continue }
        if ($projectEntry.name -notlike "project-*") { continue }

        $projectPath = "$ScopesPath/$($projectEntry.name)"
        foreach ($guideEntry in Get-ShareChildren -Storage $Storage -Path $projectPath) {
            if (-not $guideEntry.isDirectory) { continue }
            if ($guideEntry.name -notlike "guide-*") { continue }

            $guidePath = "$projectPath/$($guideEntry.name)"
            $venvPath = "$guidePath/python-venv"
            if (Test-SharePathExists -Storage $Storage -Path $venvPath) {
                $venvPaths.Add($venvPath)
            }

            $appliedStatePath = "$guidePath/applied-state.json"
            if (Test-ShareFileExists -Storage $Storage -Path $appliedStatePath) {
                $appliedStatePaths.Add($appliedStatePath)
            }
        }
    }

    return @{
        VenvPaths = $venvPaths
        AppliedStatePaths = $appliedStatePaths
    }
}

function Get-AiScaleSettings {
    $scaleJson = az containerapp show `
        --name $AiAppName `
        --resource-group $ResourceGroupName `
        --query "properties.template.scale" -o json
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to read scale settings for '$AiAppName'."
    }
    $scale = $scaleJson | ConvertFrom-Json
    return @{
        MinReplicas = [int]$scale.minReplicas
        MaxReplicas = [int]$scale.maxReplicas
    }
}

function Test-AiReplicaRunning {
    $running = az containerapp replica list `
        --name $AiAppName `
        --resource-group $ResourceGroupName `
        --query "[?properties.runningState=='Running'] | length(@)" -o tsv 2>$null
    return $running -match '^\d+$' -and [int]$running -gt 0
}

function Wait-AiReplicaRunning {
    $deadline = (Get-Date).AddSeconds($ReplicaWaitSeconds)
    while ((Get-Date) -lt $deadline) {
        if (Test-AiReplicaRunning) {
            return $true
        }
        Start-Sleep -Seconds 5
    }
    return $false
}

function Invoke-AiContainerCleanup {
    $markerContainerPath = "$StateMountPath/$ResetMarkerPath".Replace('\', '/')
    $scopeContainerPath = "$StateMountPath/$ScopesPath".Replace('\', '/')

    $shellScript = @"
set -eu
ROOT='$StateMountPath'
SCOPE_ROOT='$scopeContainerPath'
MARKER='$markerContainerPath'
VENV_COUNT=0
APPLIED_COUNT=0
if [ -d "`$SCOPE_ROOT" ]; then
  while IFS= read -r -d '' venv_dir; do
    rm -rf "`$venv_dir"
    VENV_COUNT=`$((VENV_COUNT + 1))
  done < <(find "`$SCOPE_ROOT" -type d -name python-venv -print0 2>/dev/null || true)
  APPLIED_COUNT=`$(find "`$SCOPE_ROOT" -type f -name applied-state.json 2>/dev/null | wc -l | tr -d ' ')
  find "`$SCOPE_ROOT" -type f -name applied-state.json -delete 2>/dev/null || true
fi
mkdir -p "`$(dirname "`$MARKER")"
printf 'completedUtc=%s\nremovedVenvs=%s\nremovedAppliedState=%s\n' "`$(date -u +%Y-%m-%dT%H:%M:%SZ)" "`$VENV_COUNT" "`$APPLIED_COUNT" > "`$MARKER"
echo GA_VENV_RESET_REMOVED_VENVS=`$VENV_COUNT
echo GA_VENV_RESET_REMOVED_APPLIED=`$APPLIED_COUNT
"@

    $encoded = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($shellScript))
    $remoteCommand = "echo $encoded | base64 -d | /bin/sh"

    Write-Status "Removing scoped python-venv directories via $AiAppName (mounted share; seconds, not minutes)..."

    $output = az containerapp exec `
        --name $AiAppName `
        --resource-group $ResourceGroupName `
        --command "/bin/sh" `
        --args "-c" $remoteCommand 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Scoped venv migration failed in '$AiAppName': $output"
    }

    $removedVenvs = 0
    $removedAppliedState = 0
    foreach ($line in @($output)) {
        if ($line -match '^GA_VENV_RESET_REMOVED_VENVS=(\d+)$') {
            $removedVenvs = [int]$Matches[1]
        }
        elseif ($line -match '^GA_VENV_RESET_REMOVED_APPLIED=(\d+)$') {
            $removedAppliedState = [int]$Matches[1]
        }
    }

    return @{
        RemovedVenvs = $removedVenvs
        RemovedAppliedState = $removedAppliedState
    }
}

Write-Status "Checking scoped script-agent venv migration on share '$ShareName'..."

if (-not (Test-AiAppHasMfsymlinks)) {
    exit 0
}

$storage = Get-StorageContext

if ($Force -and (Test-ShareFileExists -Storage $storage -Path $ResetMarkerPath)) {
    Write-Status "Removing migration marker (--ForceScriptVenvReset)..."
    Remove-ShareFile -Storage $storage -Path $ResetMarkerPath
}
elseif (-not $Force -and (Test-ShareFileExists -Storage $storage -Path $ResetMarkerPath)) {
    Write-Success "mfsymlinks venv migration already completed; scoped venvs were not modified."
    exit 0
}

$work = Get-ScopedVenvWorkOnShare -Storage $storage
if ($work.VenvPaths.Count -eq 0 -and $work.AppliedStatePaths.Count -eq 0) {
    Write-MigrationMarker -Storage $storage
    Write-Success "No scoped venv or applied-state files on share — migration marker recorded; no replica required."
    exit 0
}

$scale = Get-AiScaleSettings
$scaledUpForCleanup = $false
try {
    if (-not (Test-AiReplicaRunning)) {
        if ($scale.MinReplicas -lt 1) {
            Write-Status "Scaling $AiAppName to minReplicas=1 so migration can run on the mounted share..."
            az containerapp update `
                --name $AiAppName `
                --resource-group $ResourceGroupName `
                --min-replicas 1 `
                --output none | Out-Null
            if ($LASTEXITCODE -ne 0) {
                throw "Failed to scale '$AiAppName' to minReplicas=1."
            }
            $scaledUpForCleanup = $true
        }

        if (-not (Wait-AiReplicaRunning)) {
            throw "Timed out waiting for a running '$AiAppName' replica after ${ReplicaWaitSeconds}s."
        }
    }

    $result = Invoke-AiContainerCleanup
}
finally {
    if ($scaledUpForCleanup) {
        Write-Status "Restoring $AiAppName minReplicas=$($scale.MinReplicas)..."
        az containerapp update `
            --name $AiAppName `
            --resource-group $ResourceGroupName `
            --min-replicas $scale.MinReplicas `
            --output none | Out-Null
    }
}

if ($result.RemovedVenvs -eq 0 -and $result.RemovedAppliedState -eq 0) {
    Write-Success "No scoped venv or applied-state files found — migration marker recorded."
}
else {
    Write-Success "Removed $($result.RemovedVenvs) scoped venv director$(if ($result.RemovedVenvs -eq 1) { 'y' } else { 'ies' }) and $($result.RemovedAppliedState) applied-state file$(if ($result.RemovedAppliedState -eq 1) { '' } else { 's' }). Venvs recreate on next script run. Future deploys will not repeat this reset."
}
