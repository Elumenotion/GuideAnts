# GuideAnts Azure Container Apps Management Script
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("scale", "logs", "status", "restart", "update")]
    [string]$Operation,

    [string]$EnvironmentName = "dev",
    [string]$AppNamePrefix = "guideants",
    [string]$AppName = "",
    [int]$MinReplicas = 0,
    [int]$MaxReplicas = 3,
    [string]$ImageTag = "",
    [switch]$Follow,
    [string]$SubscriptionId = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$ResourceGroupName = "rg-$AppNamePrefix-$EnvironmentName"
$ContainerAppsEnvironmentName = "cae-$AppNamePrefix-$EnvironmentName"
$DefaultApps = @(
    "guideants-webapi-ui",
    "guideants-ai",
    "docling-serve",
    "plantuml",
    "searxng",
    "documentserver"
)

function Write-Status { param([string]$Message) Write-Host "[INFO] $Message" -ForegroundColor Blue }
function Write-Success { param([string]$Message) Write-Host "[SUCCESS] $Message" -ForegroundColor Green }
function Write-Err { param([string]$Message) Write-Host "[ERROR] $Message" -ForegroundColor Red }

if ($SubscriptionId) { az account set --subscription $SubscriptionId | Out-Null }

function Get-ContainerApps {
    if ($AppName) { return @($AppName) }
    return $DefaultApps | Where-Object {
        $null -ne (az containerapp show --name $_ --resource-group $ResourceGroupName --query name -o tsv 2>$null)
    }
}

function Invoke-ScaleOperation {
    foreach ($app in (Get-ContainerApps)) {
        Write-Status "Scaling $app to min=$MinReplicas max=$MaxReplicas"
        az containerapp update --name $app --resource-group $ResourceGroupName --min-replicas $MinReplicas --max-replicas $MaxReplicas --output none
        if ($LASTEXITCODE -eq 0) { Write-Success "Scaled $app" } else { Write-Err "Failed to scale $app" }
    }
}

function Invoke-LogsOperation {
    if (-not $AppName) { Write-Err "AppName is required for logs operation"; exit 1 }
    if ($Follow) {
        az containerapp logs show --name $AppName --resource-group $ResourceGroupName --follow
    } else {
        az containerapp logs show --name $AppName --resource-group $ResourceGroupName
    }
}

function Invoke-StatusOperation {
    foreach ($app in (Get-ContainerApps)) {
        Write-Status "Status for $app:"
        az containerapp show --name $app --resource-group $ResourceGroupName `
            --query "{name:name, status:properties.runningStatus, provisioning:properties.provisioningState, fqdn:properties.configuration.ingress.fqdn}" -o table
        Write-Host ""
    }
}

function Invoke-RestartOperation {
    foreach ($app in (Get-ContainerApps)) {
        Write-Status "Restarting $app..."
        $revision = az containerapp revision list --name $app --resource-group $ResourceGroupName --query "[0].name" -o tsv
        az containerapp revision restart --name $app --resource-group $ResourceGroupName --revision $revision --output none
        if ($LASTEXITCODE -eq 0) { Write-Success "Restarted $app" } else { Write-Err "Failed to restart $app" }
    }
}

function Invoke-UpdateOperation {
    if (-not $ImageTag) { Write-Err "ImageTag is required for update operation"; exit 1 }
    $imageMap = @{
        "guideants-webapi-ui" = "ghcr.io/elumenotion/guideants-webapi-ui-slim:$ImageTag"
        "guideants-ai"        = "ghcr.io/elumenotion/guideants-ai-slim:$ImageTag"
        "plantuml"            = "ghcr.io/elumenotion/guideants-plantuml:$ImageTag"
        "searxng"             = "ghcr.io/elumenotion/guideants-searxng:$ImageTag"
    }
    foreach ($app in (Get-ContainerApps)) {
        if (-not $imageMap.ContainsKey($app)) { continue }
        Write-Status "Updating $app to $($imageMap[$app])"
        az containerapp update --name $app --resource-group $ResourceGroupName --image $imageMap[$app] --output none
        if ($LASTEXITCODE -eq 0) { Write-Success "Updated $app" } else { Write-Err "Failed to update $app" }
    }
}

switch ($Operation) {
    "scale"   { Invoke-ScaleOperation }
    "logs"    { Invoke-LogsOperation }
    "status"  { Invoke-StatusOperation }
    "restart" { Invoke-RestartOperation }
    "update"  { Invoke-UpdateOperation }
}
