# GuideAnts Azure Container Apps Deployment (azure-slim profile)
param(
    [string]$EnvironmentName = "dev",
    [string]$Location = "East US 2",
    [string]$AppNamePrefix = "guideants",
    [string]$GhcrOwner = "elumenotion",
    [string]$ImageTag = "main",
    [string]$CustomDomain = "",
    [string]$SqlAdminPassword = "",
    [string]$SubscriptionId = "",
    [switch]$SkipMigrations,
    [switch]$OnlyInfra,
    [switch]$OnlyApps,
    [string]$SqlAadAdminObjectId = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Write-Status { param([string]$Message) Write-Host "[INFO] $Message" -ForegroundColor Blue }
function Write-Success { param([string]$Message) Write-Host "[SUCCESS] $Message" -ForegroundColor Green }
function Write-Warn { param([string]$Message) Write-Host "[WARNING] $Message" -ForegroundColor Yellow }
function Write-Err { param([string]$Message) Write-Host "[ERROR] $Message" -ForegroundColor Red }

$script:ResourceGroupName = "rg-$AppNamePrefix-$EnvironmentName"
$script:SqlDatabaseName = "guideants"
$script:DeployRoot = $PSScriptRoot
$script:RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot ".." "..")).Path

function Get-KeyVaultName {
    param([switch]$AllowMissing)

    $kvName = az keyvault list --resource-group $script:ResourceGroupName --query "[0].name" -o tsv 2>$null
    if (-not $kvName -and -not $AllowMissing) {
        throw "Key Vault not found in resource group '$($script:ResourceGroupName)'."
    }
    return $kvName
}

function Resolve-SqlAdminPassword {
    if (-not [string]::IsNullOrWhiteSpace($SqlAdminPassword)) {
        $script:SqlAdminPassword = $SqlAdminPassword
        return
    }

    if ($OnlyApps) {
        return
    }

    $kvName = Get-KeyVaultName -AllowMissing
    if (-not $kvName) {
        return
    }

    $stored = az keyvault secret show --vault-name $kvName --name sql-admin-password --query value -o tsv 2>$null
    if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($stored)) {
        Write-Status "Using sql-admin-password from Key Vault (no -SqlAdminPassword supplied)."
        $script:SqlAdminPassword = $stored
    }
}

function Test-Prerequisites {
    Write-Status "Checking prerequisites..."
    try { az --version | Out-Null } catch { Write-Err "Azure CLI is not installed."; exit 1 }
    try { az account show | Out-Null } catch { Write-Err "Not logged in to Azure. Run 'az login' first."; exit 1 }

    Resolve-SqlAdminPassword
    if (-not $OnlyApps -and [string]::IsNullOrWhiteSpace($script:SqlAdminPassword)) {
        Write-Err @"
SqlAdminPassword is required for infrastructure deploy (Phase 1 or full deploy).
Pass -SqlAdminPassword with the original password, or omit it when Key Vault already stores sql-admin-password.

For app-only image updates that must not touch SQL or secrets, use:
  ./deploy.ps1 -OnlyApps -SkipMigrations -ImageTag <tag>
"@
        exit 1
    }
    Write-Success "Prerequisites check passed"
}

function Set-Variables {
    Write-Status "Setting deployment variables..."
    if ($SubscriptionId) {
        az account set --subscription $SubscriptionId | Out-Null
        $script:SubscriptionId = $SubscriptionId
    } else {
        $script:SubscriptionId = az account show --query id -o tsv
    }
    if ([string]::IsNullOrWhiteSpace($script:SqlAdminPassword) -and -not [string]::IsNullOrWhiteSpace($SqlAdminPassword)) {
        $script:SqlAdminPassword = $SqlAdminPassword
    }
    Write-Success "Using subscription $($script:SubscriptionId), resource group $($script:ResourceGroupName)"
}

function Get-DeployerIdentity {
    $signedInUserId = az ad signed-in-user show --query id -o tsv 2>$null
    if ($signedInUserId) {
        return @{
            ObjectId = $signedInUserId
            PrincipalType = 'User'
        }
    }

    $accountUser = az account show --query user -o json | ConvertFrom-Json
    if ($accountUser.type -eq 'servicePrincipal') {
        $spId = az ad sp show --id $accountUser.name --query id -o tsv
        if (-not $spId) { Write-Err "Could not resolve service principal object ID for deployer."; exit 1 }
        return @{
            ObjectId = $spId
            PrincipalType = 'ServicePrincipal'
        }
    }

    Write-Err "Could not resolve deployer object ID. Sign in with 'az login' or 'az login --service-principal'."
    exit 1
}

function New-DeploymentSecrets {
    Write-Status "Generating deployment secrets..."
    $json = & (Join-Path $script:DeployRoot "scripts" "generate-secrets.ps1") -Quiet
    $secrets = $json | ConvertFrom-Json
    $script:JwtSigningKey = $secrets.jwtSigningKey
    $script:SettingsSecretsKey = $secrets.settingsSecretsKey
    $script:ScriptAgentToken = $secrets.scriptAgentToken
    $script:ScriptAgentAdminToken = $secrets.scriptAgentAdminToken
    $script:DocumentServerJwtSecret = $secrets.documentServerJwtSecret
    Write-Success "Secrets generated (not written to disk)"
}

function Resolve-DeploymentSecrets {
    if ($OnlyApps) {
        Write-Status "Skipping bootstrap secret generation (-OnlyApps preserves existing Key Vault secrets)."
        return
    }

    $kvName = Get-KeyVaultName -AllowMissing
    if (-not $kvName) {
        New-DeploymentSecrets
        return
    }

    Write-Status "Key Vault '$kvName' exists; reusing stored bootstrap secrets..."
    $secretNames = @{
        JwtSigningKey           = 'jwt-signing-key'
        SettingsSecretsKey      = 'settings-secrets-key-azure-deploy'
        ScriptAgentToken        = 'script-agent-token'
        ScriptAgentAdminToken   = 'script-agent-admin-token'
        DocumentServerJwtSecret = 'documentserver-jwt-secret'
    }

    $resolved = @{}
    foreach ($entry in $secretNames.GetEnumerator()) {
        $value = az keyvault secret show --vault-name $kvName --name $entry.Value --query value -o tsv 2>$null
        if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($value)) {
            Write-Warn "Missing Key Vault secret '$($entry.Value)'; generating fresh bootstrap secrets."
            New-DeploymentSecrets
            return
        }
        $resolved[$entry.Key] = $value
    }

    $script:JwtSigningKey = $resolved.JwtSigningKey
    $script:SettingsSecretsKey = $resolved.SettingsSecretsKey
    $script:ScriptAgentToken = $resolved.ScriptAgentToken
    $script:ScriptAgentAdminToken = $resolved.ScriptAgentAdminToken
    $script:DocumentServerJwtSecret = $resolved.DocumentServerJwtSecret
    Write-Success "Reusing existing Key Vault bootstrap secrets."
}

function Deploy-Infrastructure {
    Write-Status "Deploying Azure infrastructure (Phase 1)..."

    # Check if resource group exists; if not, request admin to create it
    Write-Status "Checking resource group: $($script:ResourceGroupName)..."
    $rgExists = az group exists --name $script:ResourceGroupName --output tsv 2>$null
    if ($rgExists -ne "true") {
        Write-Err @"
Resource group '$($script:ResourceGroupName)' does not exist.

AZBuilder roles typically don't have permission to create resource groups.
Ask your Azure admin to create it with:

    az group create --name $($script:ResourceGroupName) --location "$Location"

Then re-run this script.
"@
        exit 1
    }
    Write-Success "Resource group exists"

    $deployer = Get-DeployerIdentity
    $deploymentName = "guideants-$EnvironmentName-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
    # Pass secrets via a parameters file so Windows az.cmd does not treat < > & in values as shell redirection.
    $paramsPath = Join-Path ([System.IO.Path]::GetTempPath()) ("guideants-infra-{0}.parameters.json" -f [guid]::NewGuid().ToString('n'))
    try {
        $paramsObject = [ordered]@{
            environmentName          = @{ value = $EnvironmentName }
            location                 = @{ value = $Location }
            appNamePrefix            = @{ value = $AppNamePrefix }
            sqlDatabaseName          = @{ value = $script:SqlDatabaseName }
            sqlAdminPassword         = @{ value = $script:SqlAdminPassword }
            sqlAadAdminObjectId      = @{ value = $SqlAadAdminObjectId }
            deployerObjectId         = @{ value = $deployer.ObjectId }
            jwtSigningKey            = @{ value = $script:JwtSigningKey }
            settingsSecretsKey       = @{ value = $script:SettingsSecretsKey }
            scriptAgentToken         = @{ value = $script:ScriptAgentToken }
            scriptAgentAdminToken    = @{ value = $script:ScriptAgentAdminToken }
            documentServerJwtSecret  = @{ value = $script:DocumentServerJwtSecret }
        }
        ($paramsObject | ConvertTo-Json -Depth 6) | Set-Content -Path $paramsPath -Encoding utf8
        az deployment group create `
            --name $deploymentName `
            --resource-group $script:ResourceGroupName `
            --template-file (Join-Path $script:DeployRoot "main.bicep") `
            --parameters "@$paramsPath" `
            --output none
        if ($LASTEXITCODE -ne 0) { Write-Err "Infrastructure deployment failed"; exit 1 }
    }
    finally {
        if (Test-Path $paramsPath) { Remove-Item -Force $paramsPath -ErrorAction SilentlyContinue }
    }
    $script:DeploymentName = $deploymentName
    Write-Success "Infrastructure deployment completed"
}

function Deploy-ContainerApps {
    Write-Status "Deploying Container Apps (Phase 2)..."
    $deploymentName = "guideants-apps-$EnvironmentName-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
    $paramsPath = Join-Path ([System.IO.Path]::GetTempPath()) ("guideants-apps-{0}.parameters.json" -f [guid]::NewGuid().ToString('n'))
    try {
        $paramsObject = [ordered]@{
            environmentName         = @{ value = $EnvironmentName }
            location                = @{ value = $Location }
            appNamePrefix           = @{ value = $AppNamePrefix }
            ghcrOwner               = @{ value = $GhcrOwner }
            imageTag                = @{ value = $ImageTag }
            customDomain            = @{ value = $CustomDomain }
            documentServerEnabled   = @{ value = $true }
        }
        ($paramsObject | ConvertTo-Json -Depth 6) | Set-Content -Path $paramsPath -Encoding utf8
        az deployment group create `
            --name $deploymentName `
            --resource-group $script:ResourceGroupName `
            --template-file (Join-Path $script:DeployRoot "apps.bicep") `
            --parameters "@$paramsPath" `
            --output none
        if ($LASTEXITCODE -ne 0) { Write-Err "Container Apps deployment failed"; exit 1 }
    }
    finally {
        if (Test-Path $paramsPath) { Remove-Item -Force $paramsPath -ErrorAction SilentlyContinue }
    }
    $script:ContainerAppsDeploymentName = $deploymentName
    Write-Success "Container Apps deployment completed"
}

function Ensure-SqlAadAdmin {
    param([string]$ServerName, [string]$ResourceGroupName)
    Write-Status "Checking SQL AAD admin on '$ServerName'..."
    $adminListJson = az sql server ad-admin list --server $ServerName --resource-group $ResourceGroupName --output json
    if ($LASTEXITCODE -ne 0) { Throw "az sql server ad-admin list failed: $adminListJson" }
    $admin = $adminListJson | ConvertFrom-Json
    $adminExists = $false
    if ($null -ne $admin) {
        if ($admin -is [array]) { if ($admin.Count -gt 0) { $adminExists = $true } }
        else { $adminExists = $true }
    }
    if (-not $adminExists) {
        $objectId = if ([string]::IsNullOrEmpty($SqlAadAdminObjectId)) {
            $currentUser = az ad signed-in-user show --query "{id:id, userPrincipalName:userPrincipalName}" -o json | ConvertFrom-Json
            $script:SqlAadAdminUpn = $currentUser.userPrincipalName
            $currentUser.id
        } else {
            $adminUser = az ad user show --id $SqlAadAdminObjectId --query "{id:id, userPrincipalName:userPrincipalName}" -o json | ConvertFrom-Json
            $script:SqlAadAdminUpn = $adminUser.userPrincipalName
            $adminUser.id
        }
        $createOutput = az sql server ad-admin create --resource-group $ResourceGroupName --server $ServerName --display-name $script:SqlAadAdminUpn --object-id $objectId --output json 2>&1
        if ($LASTEXITCODE -ne 0 -and $createOutput -notlike '*ServerAdministratorNameAlreadyExists*') {
            Throw "Failed to create AAD admin: $createOutput"
        }
        Write-Success "AAD admin assigned. Waiting 60s for propagation..."
        Start-Sleep -Seconds 60
    } else {
        Write-Success "AAD admin already exists."
    }
}

function Ensure-FirewallRuleForCurrentIP {
    param([string]$ServerName)
    try {
        Write-Status "Ensuring firewall rule for current public IP..."
        $publicIp = (Invoke-RestMethod -Uri "https://api.ipify.org").Trim()
        $existing = az sql server firewall-rule list --resource-group $script:ResourceGroupName --server $ServerName --query "[?startIpAddress=='$publicIp'] | [0].name" -o tsv
        if (-not $existing) {
            az sql server firewall-rule create --resource-group $script:ResourceGroupName --server $ServerName --name "allow-script-ip" --start-ip-address $publicIp --end-ip-address $publicIp --output none
            Write-Success "Firewall rule added for $publicIp"
        } else {
            Write-Success "Firewall rule already allows $publicIp"
        }
    } catch {
        Write-Warn "Could not add firewall rule for current IP: $_"
    }
}

function Set-SqlDatabase {
    $sqlServerName = az sql server list --resource-group $script:ResourceGroupName --query "[0].name" -o tsv
    if (-not $sqlServerName) { Write-Warn "SQL Server not found. Skipping database setup."; return $null }

    Ensure-SqlAadAdmin -ServerName $sqlServerName -ResourceGroupName $script:ResourceGroupName
    Ensure-FirewallRuleForCurrentIP -ServerName $sqlServerName

    $identityName = "id-$AppNamePrefix-containers-$EnvironmentName"
    $sqlScript = @"
IF NOT EXISTS (SELECT * FROM sys.database_principals WHERE name = '$identityName')
BEGIN
    CREATE USER [$identityName] FROM EXTERNAL PROVIDER;
END
ALTER ROLE db_datareader ADD MEMBER [$identityName];
ALTER ROLE db_datawriter ADD MEMBER [$identityName];
ALTER ROLE db_ddladmin ADD MEMBER [$identityName];
GO
"@
    $sqlScriptPath = Join-Path $script:DeployRoot "setup_sql_user.sql"
    $sqlScript | Out-File -FilePath $sqlScriptPath -Encoding UTF8

    $accessToken = az account get-access-token --resource https://database.windows.net/ --query accessToken -o tsv
    if (-not $accessToken) { Write-Err "Failed to acquire Azure AD access token."; return $sqlServerName }

    if (-not (Get-Module -ListAvailable -Name SqlServer)) {
        Write-Status "Installing SqlServer PowerShell module..."
        Install-Module -Name SqlServer -Scope CurrentUser -Force -ErrorAction Stop
    }
    Import-Module SqlServer -ErrorAction Stop

    try {
        $masterBootstrapScript = @"
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = '$identityName')
BEGIN
    CREATE USER [$identityName] FROM EXTERNAL PROVIDER;
END
"@
        Invoke-Sqlcmd -ServerInstance "${sqlServerName}.database.windows.net" `
            -Database "master" `
            -AccessToken $accessToken `
            -Query $masterBootstrapScript `
            -ErrorAction Stop
        Write-Success "SQL managed identity principal ensured in master (startup catalog check)."

        Invoke-Sqlcmd -ServerInstance "${sqlServerName}.database.windows.net" `
            -Database $script:SqlDatabaseName `
            -AccessToken $accessToken `
            -InputFile $sqlScriptPath `
            -ErrorAction Stop
        Write-Success "SQL managed identity user configured in $($script:SqlDatabaseName)."
    } catch {
        Write-Err "Failed to configure SQL user: $_"
        exit 1
    } finally {
        Remove-Item $sqlScriptPath -ErrorAction SilentlyContinue
    }

    return $sqlServerName
}

function Apply-DatabaseMigrations {
    Write-Status "Applying EF database migrations..."
    $sqlServerName = az sql server list --resource-group $script:ResourceGroupName --query "[0].name" -o tsv
    $migrationConnectionString = "Server=tcp:${sqlServerName}.database.windows.net,1433;Initial Catalog=$($script:SqlDatabaseName);User ID=sqladmin;Password=$($script:SqlAdminPassword);TrustServerCertificate=False;Encrypt=True;"

    if (-not (dotnet tool list --global | Select-String "dotnet-ef")) {
        Write-Status "Installing dotnet-ef global tool..."
        dotnet tool install --global dotnet-ef
    }

    Push-Location (Join-Path $script:RepoRoot "src" "server")
    try {
        Write-Status "Restoring NuGet packages..."
        dotnet restore
        if ($LASTEXITCODE -ne 0) { Throw "dotnet restore failed with exit code $LASTEXITCODE" }
        Write-Success "NuGet packages restored."

        dotnet ef database update `
            --project GuideAntsApi.DataModel/GuideAntsApi.DataModel.csproj `
            --startup-project GuideAntsApi/GuideAntsApi.csproj `
            --connection $migrationConnectionString
        if ($LASTEXITCODE -ne 0) { Throw "dotnet ef database update failed with exit code $LASTEXITCODE" }
        Write-Success "Database migrations applied."
    } finally {
        Pop-Location
    }
}

function Invoke-AzChecked {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Command,
        [string]$FailureMessage
    )
    & az @Command
    if ($LASTEXITCODE -ne 0) {
        if ($FailureMessage) { Write-Err $FailureMessage }
        else { Write-Err "Azure CLI command failed: az $($Command -join ' ')" }
        exit 1
    }
}

function Update-KeyVaultConnectionString {
    param([string]$SqlServerName)
    Write-Status "Updating Key Vault SQL connection string with managed identity client ID..."
    $kvName = az keyvault list --resource-group $script:ResourceGroupName --query "[0].name" -o tsv
    if (-not $kvName) { Write-Err "Key Vault not found in resource group $($script:ResourceGroupName)."; exit 1 }

    $identityName = "id-$AppNamePrefix-containers-$EnvironmentName"
    $clientId = az identity show --resource-group $script:ResourceGroupName --name $identityName --query "clientId" -o tsv
    if (-not $clientId) { Write-Err "Could not find managed identity client ID."; exit 1 }

    $connectionString = "Server=tcp:${SqlServerName}.database.windows.net,1433;Initial Catalog=$($script:SqlDatabaseName);Authentication=Active Directory Managed Identity;User ID=${clientId};TrustServerCertificate=False;Encrypt=True;Connection Timeout=30;ConnectRetryCount=3;ConnectRetryInterval=5;"
    Invoke-AzChecked -Command @(
        'keyvault', 'secret', 'set',
        '--vault-name', $kvName,
        '--name', 'sql-connection-string',
        '--value', $connectionString,
        '--output', 'none'
    ) -FailureMessage "Failed to update Key Vault secret 'sql-connection-string' on '$kvName'."
    Write-Success "Key Vault connection string updated."
}

function Force-NewRevision-WebApiApp {
    Write-Status "Forcing new revision for guideants-webapi-ui..."
    $timestamp = (Get-Date -UFormat %s)
    Invoke-AzChecked -Command @(
        'containerapp', 'update',
        '--name', 'guideants-webapi-ui',
        '--resource-group', $script:ResourceGroupName,
        '--set-env-vars', "DEPLOYMENT_TRIGGER=$timestamp",
        '--output', 'none'
    ) -FailureMessage "Failed to force a new revision for guideants-webapi-ui."
    Write-Success "New revision triggered."
}

function Upload-SearXngConfig {
    & (Join-Path $script:DeployRoot "scripts" "upload-searxng-config.ps1") -ResourceGroupName $script:ResourceGroupName
}

function Show-DeploymentSummary {
    Write-Host ""
    Write-Host "==============================================" -ForegroundColor Cyan
    Write-Host "GuideAnts Azure Deploy — Summary" -ForegroundColor Cyan
    Write-Host "==============================================" -ForegroundColor Cyan
    Write-Host "Environment:      $EnvironmentName"
    Write-Host "Resource Group:   $($script:ResourceGroupName)"
    Write-Host "Image tag:        $ImageTag"
    Write-Host ""

    $fqdn = az containerapp show -n guideants-webapi-ui -g $script:ResourceGroupName --query "properties.configuration.ingress.fqdn" -o tsv 2>$null
    if ($fqdn) {
        $url = if ($CustomDomain) { "https://$CustomDomain" } else { "https://$fqdn" }
        Write-Host "Application URL:  $url" -ForegroundColor Green
        Write-Host ""
        Write-Host "Next steps:" -ForegroundColor Yellow
        Write-Host "  1. Open the URL above and register the first user (becomes Admin)."
        Write-Host "  2. Go to Settings → Connections and configure your cloud AI provider."
        Write-Host "  3. Create a project and run a chat smoke test."
        if ($CustomDomain) {
            Write-Host ""
            Write-Host "Custom domain: bind DNS CNAME to $fqdn and add managed cert in ACA portal."
        }
    } else {
        Write-Warn "Container app FQDN not available yet."
    }
    Write-Success "Deployment completed."
}

function Main {
    Write-Host "=============================================="
    Write-Host "GuideAnts Azure Container Apps Deployment"
    Write-Host "Profile: azure-slim"
    Write-Host "=============================================="

    Test-Prerequisites
    Set-Variables
    Resolve-DeploymentSecrets

    if (-not $OnlyApps) {
        Deploy-Infrastructure
    }

    if (-not $OnlyInfra) {
        Deploy-ContainerApps

        if (-not $OnlyApps) {
            $sqlServerName = Set-SqlDatabase
            if ($sqlServerName) {
                if (-not $SkipMigrations) {
                    Apply-DatabaseMigrations
                }
                Update-KeyVaultConnectionString -SqlServerName $sqlServerName
                Force-NewRevision-WebApiApp
            }
        } else {
            Write-Status "Skipping SQL setup, Key Vault connection string update, and web API revision bump (-OnlyApps)."
        }
        Upload-SearXngConfig
    }

    Show-DeploymentSummary
}

Main
