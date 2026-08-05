# After container apps deploy: restart guideants-ai so entrypoint migration runs on the mounted share.
param(
    [Parameter(Mandatory = $true)]
    [string]$ResourceGroupName,

    [string]$ShareName = "script-agent-state",
    [string]$AiAppName = "guideants-ai",
    [string]$ResetMarkerPath = ".guideants/mfsymlinks-venv-reset.done",
    [int]$ReplicaWaitSeconds = 300,
    [int]$MarkerWaitSeconds = 300,
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

function Wait-MigrationMarker {
    param([hashtable]$Storage)

    $deadline = (Get-Date).AddSeconds($MarkerWaitSeconds)
    while ((Get-Date) -lt $deadline) {
        if (Test-ShareFileExists -Storage $Storage -Path $ResetMarkerPath) {
            return $true
        }
        Start-Sleep -Seconds 5
    }
    return $false
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

function Restart-AiApp {
    $revision = az containerapp revision list `
        --name $AiAppName `
        --resource-group $ResourceGroupName `
        --query "[0].name" -o tsv
    if (-not $revision) {
        throw "No active revision found for '$AiAppName'."
    }

    Write-Status "Restarting $AiAppName revision $revision so entrypoint migration can run..."
    az containerapp revision restart `
        --name $AiAppName `
        --resource-group $ResourceGroupName `
        --revision $revision `
        --output none | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to restart '$AiAppName' revision '$revision'."
    }
}

Write-Status "Checking scoped script-agent venv migration..."

if (-not (Test-AiAppHasMfsymlinks)) {
    exit 0
}

$storage = Get-StorageContext

if ($Force -and (Test-ShareFileExists -Storage $storage -Path $ResetMarkerPath)) {
    Write-Status "Removing migration marker (--ForceScriptVenvReset)..."
    Remove-ShareFile -Storage $storage -Path $ResetMarkerPath
}
elseif (-not $Force -and (Test-ShareFileExists -Storage $storage -Path $ResetMarkerPath)) {
    Write-Success "mfsymlinks venv migration already completed."
    exit 0
}

if (-not (Test-AiReplicaRunning)) {
    Write-Status "Waiting for a running $AiAppName replica..."
    if (-not (Wait-AiReplicaRunning)) {
        throw "Timed out waiting for a running '$AiAppName' replica after ${ReplicaWaitSeconds}s."
    }
}

Restart-AiApp

if (-not (Wait-AiReplicaRunning)) {
    throw "Timed out waiting for '$AiAppName' to come back after restart."
}

if (Wait-MigrationMarker -Storage $storage) {
    Write-Success "mfsymlinks scoped venv migration completed (marker present on share)."
    exit 0
}

throw @"
Scoped venv migration did not complete within ${MarkerWaitSeconds}s.

The guideants-ai image must include script-agent-admin/migrate-mfsymlinks-scoped-venvs.sh
(entrypoint runs it on start). Redeploy with a current guideants-ai-slim image (:main after this change),
then re-run:

  ./deploy.ps1 -OnlyApps -SkipMigrations -ImageTag main
"@
