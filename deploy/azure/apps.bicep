// Container Apps Deployment - Phase 2 (azure-slim profile)
targetScope = 'resourceGroup'

@description('Environment name (dev, staging, prod)')
param environmentName string = 'dev'

@description('Location for all resources')
param location string = 'East US 2'

@description('Application name prefix')
param appNamePrefix string = 'guideants'

@description('GHCR organization for GuideAnts images')
param ghcrOwner string = 'elumenotion'

@description('Image tag (main, latest, semver, sha-*)')
param imageTag string = 'main'

@description('Custom domain for public HTTPS URL (empty = ACA default FQDN)')
param customDomain string = ''

@description('Deploy DocumentServer sidecar')
param documentServerEnabled bool = true

var tags = {
  environment: environmentName
  project: 'guideants'
  deployedBy: 'bicep'
  profile: 'azure-slim'
}

resource containerAppsEnvironment 'Microsoft.App/managedEnvironments@2023-05-01' existing = {
  name: 'cae-${appNamePrefix}-${environmentName}'
}

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: 'kv-${take(appNamePrefix, 4)}-${take(environmentName, 3)}-${take(uniqueString(resourceGroup().id), 8)}'
}

resource storageAccount 'Microsoft.Storage/storageAccounts@2023-01-01' existing = {
  name: 'st${take(appNamePrefix, 4)}${take(environmentName, 3)}${take(uniqueString(resourceGroup().id), 10)}'
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' existing = {
  name: 'appi-${appNamePrefix}-${environmentName}'
}

module containerApps 'modules/container-apps.bicep' = {
  name: 'container-apps'
  params: {
    location: location
    environmentName: environmentName
    appNamePrefix: appNamePrefix
    containerAppsEnvironmentId: containerAppsEnvironment.id
    containerAppsEnvironmentDefaultDomain: containerAppsEnvironment.properties.defaultDomain
    keyVaultName: keyVault.name
    keyVaultUri: keyVault.properties.vaultUri
    storageAccountName: storageAccount.name
    ghcrOwner: ghcrOwner
    imageTag: imageTag
    customDomain: customDomain
    documentServerEnabled: documentServerEnabled
    appInsightsConnectionString: appInsights.properties.ConnectionString
    sqlDatabaseName: 'guideants'
    tags: tags
  }
}

output webApiUiUrl string = containerApps.outputs.webApiUiUrl
output webApiUiFqdn string = containerApps.outputs.webApiUiFqdn
output containerAppsDeployed bool = true
