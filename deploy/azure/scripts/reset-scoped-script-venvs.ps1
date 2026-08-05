# Remove scoped Python venv directories (and applied-state) from script-agent-state share.
# One-time migration after mfsymlinks is enabled on guideants-ai (marker file prevents repeat wipes).
param(
    [Parameter(Mandatory = $true)]
    [string]$ResourceGroupName,

    [string]$ShareName = "script-agent-state",
    [string]$ScopesPath = "scopes",
    [string]$AiAppName = "guideants-ai",
    [string]$ResetMarkerPath = ".guideants/mfsymlinks-venv-reset.done",
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
        Write-Status "Container app '$AiAppName' not found; skipping scoped venv reset."
        return $false
    }

    $app = $appJson | ConvertFrom-Json
    $mountOptions = @(
        $app.properties.template.volumes |
            Where-Object { $_.name -eq "script-agent-state-volume" } |
            Select-Object -ExpandProperty mountOptions
    ) | Select-Object -First 1

    if ($mountOptions -ne $script:MfsymlinksMountOptions) {
        Write-Status "guideants-ai is not using mfsymlinks mount options yet; skipping scoped venv reset."
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

function Set-ShareFileContent {
    param(
        [hashtable]$Storage,
        [string]$Path,
        [string]$Content
    )

    $parent = Split-Path -Parent $Path
    if ($parent -and -not (Test-SharePathExists -Storage $Storage -Path $parent)) {
        az storage directory create `
            --account-name $Storage.AccountName `
            --account-key $Storage.AccountKey `
            --share-name $ShareName `
            --name $parent `
            --output none | Out-Null
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

function Remove-ShareDirectoryRecursive {
    param(
        [hashtable]$Storage,
        [string]$Path
    )

    $childrenJson = az storage file list `
        --account-name $Storage.AccountName `
        --account-key $Storage.AccountKey `
        --share-name $ShareName `
        --path $Path `
        -o json
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to list Azure Files path '$Path' on share '$ShareName'."
    }

    $children = @($childrenJson | ConvertFrom-Json)
    foreach ($child in $children) {
        $childPath = "$Path/$($child.name)"
        if ($child.isDirectory) {
            Remove-ShareDirectoryRecursive -Storage $Storage -Path $childPath
        }
        else {
            Remove-ShareFile -Storage $Storage -Path $childPath
        }
    }

    az storage directory delete `
        --account-name $Storage.AccountName `
        --account-key $Storage.AccountKey `
        --share-name $ShareName `
        --name $Path `
        --output none | Out-Null
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

Write-Status "Checking scoped script-agent venv migration on share '$ShareName'..."

if (-not (Test-AiAppHasMfsymlinks)) {
    exit 0
}

$storage = Get-StorageContext

if (-not $Force -and (Test-ShareFileExists -Storage $storage -Path $ResetMarkerPath)) {
    Write-Success "mfsymlinks venv migration already completed; scoped venvs were not modified."
    exit 0
}

if (-not (Test-SharePathExists -Storage $storage -Path $ScopesPath)) {
    $markerBody = "completedUtc=$([DateTime]::UtcNow.ToString('o'))`nremovedVenvs=0`nremovedAppliedState=0`n"
    Set-ShareFileContent -Storage $storage -Path $ResetMarkerPath -Content $markerBody
    Write-Success "No '$ScopesPath' directory on share — migration marker recorded; nothing to reset."
    exit 0
}

$removedVenvs = 0
$removedAppliedState = 0

foreach ($projectEntry in Get-ShareChildren -Storage $storage -Path $ScopesPath) {
    if (-not $projectEntry.isDirectory) { continue }
    if ($projectEntry.name -notlike "project-*") { continue }

    $projectPath = "$ScopesPath/$($projectEntry.name)"
    foreach ($guideEntry in Get-ShareChildren -Storage $storage -Path $projectPath) {
        if (-not $guideEntry.isDirectory) { continue }
        if ($guideEntry.name -notlike "guide-*") { continue }

        $guidePath = "$projectPath/$($guideEntry.name)"
        $venvPath = "$guidePath/python-venv"
        if (Test-SharePathExists -Storage $storage -Path $venvPath) {
            Write-Status "Removing $venvPath"
            Remove-ShareDirectoryRecursive -Storage $storage -Path $venvPath
            $removedVenvs++
        }

        $appliedStatePath = "$guidePath/applied-state.json"
        if (Test-ShareFileExists -Storage $storage -Path $appliedStatePath) {
            Write-Status "Removing $appliedStatePath"
            Remove-ShareFile -Storage $storage -Path $appliedStatePath
            $removedAppliedState++
        }
    }
}

$markerBody = "completedUtc=$([DateTime]::UtcNow.ToString('o'))`nremovedVenvs=$removedVenvs`nremovedAppliedState=$removedAppliedState`n"
Set-ShareFileContent -Storage $storage -Path $ResetMarkerPath -Content $markerBody

if ($removedVenvs -eq 0 -and $removedAppliedState -eq 0) {
    Write-Success "No scoped venv or applied-state files found — migration marker recorded."
}
else {
    Write-Success "Removed $removedVenvs scoped venv director$(if ($removedVenvs -eq 1) { 'y' } else { 'ies' }) and $removedAppliedState applied-state file$(if ($removedAppliedState -eq 1) { '' } else { 's' }). Venvs recreate on next script run; pip requirements re-apply automatically. Future deploys will not repeat this reset."
}
