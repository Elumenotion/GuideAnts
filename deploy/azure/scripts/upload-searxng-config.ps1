# Upload SearXNG config from repo to Azure Files share
param(
    [Parameter(Mandatory = $true)]
    [string]$ResourceGroupName,
    [string]$ConfigSourcePath = "",
    [int]$MaxRetries = 3
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if (-not $ConfigSourcePath) {
    $ConfigSourcePath = Join-Path $PSScriptRoot ".." ".." ".." "docker" "volumes" "searxng" "config"
}

if (-not (Test-Path $ConfigSourcePath)) {
    Write-Error "SearXNG config source not found at '$ConfigSourcePath'"
    exit 1
}

$storageAccountName = az storage account list --resource-group $ResourceGroupName --query "[0].name" -o tsv
if (-not $storageAccountName) {
    Write-Error "Storage account not found in resource group '$ResourceGroupName'"
    exit 1
}

$storageKey = az storage account keys list --resource-group $ResourceGroupName --account-name $storageAccountName --query "[0].value" -o tsv

Write-Host "[INFO] Uploading SearXNG config to share 'searxng-config'..." -ForegroundColor Blue

$retryCount = 0
$success = $false
while (-not $success -and $retryCount -lt $MaxRetries) {
    try {
        az storage file upload-batch `
            --account-name $storageAccountName `
            --account-key $storageKey `
            --destination searxng-config `
            --source $ConfigSourcePath `
            --max-connections 1
        $success = $LASTEXITCODE -eq 0
    } catch {
        Write-Warning "Upload attempt $($retryCount + 1) failed: $($_.Exception.Message)"
    }

    if (-not $success) {
        $retryCount++
        if ($retryCount -lt $MaxRetries) {
            Write-Host "[INFO] Retrying in 10 seconds... (attempt $($retryCount + 1)/$MaxRetries)" -ForegroundColor Blue
            Start-Sleep -Seconds 10
        }
    }
}

if (-not $success) {
    Write-Error "Failed to upload SearXNG config after $MaxRetries attempts."
    exit 1
}

Write-Host "[SUCCESS] SearXNG config uploaded." -ForegroundColor Green
