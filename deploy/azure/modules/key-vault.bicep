// Key Vault Module - bootstrap secrets for GuideAnts azure-slim
// Uses access policies (not RBAC) so Contributor can deploy without roleAssignments/write.
param location string
param environmentName string
param appNamePrefix string
param sqlDatabaseName string
@secure()
param sqlAdminPassword string
@secure()
param jwtSigningKey string
@secure()
param settingsSecretsKey string
@secure()
param scriptAgentToken string
@secure()
param scriptAgentAdminToken string
@secure()
param documentServerJwtSecret string

@description('Object ID of the deployer principal for post-deploy Key Vault secret updates')
param deployerObjectId string = ''

@description('Object ID of the container apps user-assigned managed identity')
param containerAppsPrincipalId string

param tags object

var keyVaultName = 'kv-${take(appNamePrefix, 4)}-${take(environmentName, 3)}-${take(uniqueString(resourceGroup().id), 8)}'

var deployerAccessPolicy = !empty(deployerObjectId) ? [
  {
    tenantId: subscription().tenantId
    objectId: deployerObjectId
    permissions: {
      secrets: [
        'get'
        'list'
        'set'
        'delete'
        'backup'
        'restore'
        'recover'
        'purge'
      ]
    }
  }
] : []

var containerAppsAccessPolicy = [
  {
    tenantId: subscription().tenantId
    objectId: containerAppsPrincipalId
    permissions: {
      secrets: [
        'get'
        'list'
      ]
    }
  }
]

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: keyVaultName
  location: location
  tags: tags
  properties: {
    sku: {
      family: 'A'
      name: 'standard'
    }
    tenantId: subscription().tenantId
    enableRbacAuthorization: false
    enableSoftDelete: true
    enablePurgeProtection: true
    softDeleteRetentionInDays: 7
    networkAcls: {
      bypass: 'AzureServices'
      defaultAction: 'Allow'
    }
    publicNetworkAccess: 'Enabled'
    accessPolicies: concat(deployerAccessPolicy, containerAppsAccessPolicy)
  }
}

resource jwtSigningKeySecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: 'jwt-signing-key'
  properties: {
    value: jwtSigningKey
    contentType: 'text/plain'
  }
}

resource settingsSecretsKeySecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: 'settings-secrets-key-azure-deploy'
  properties: {
    value: settingsSecretsKey
    contentType: 'text/plain'
  }
}

resource scriptAgentTokenSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: 'script-agent-token'
  properties: {
    value: scriptAgentToken
    contentType: 'text/plain'
  }
}

resource scriptAgentAdminTokenSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: 'script-agent-admin-token'
  properties: {
    value: scriptAgentAdminToken
    contentType: 'text/plain'
  }
}

resource documentServerJwtSecretResource 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: 'documentserver-jwt-secret'
  properties: {
    value: documentServerJwtSecret
    contentType: 'text/plain'
  }
}

resource sqlAdminPasswordSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: 'sql-admin-password'
  properties: {
    value: sqlAdminPassword
    contentType: 'text/plain'
  }
}

resource sqlConnectionStringSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: 'sql-connection-string'
  properties: {
    value: 'Server=tcp:${appNamePrefix}-sql-${environmentName}.${environment().suffixes.sqlServerHostname},1433;Initial Catalog=${sqlDatabaseName};Authentication=Active Directory Managed Identity;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;ConnectRetryCount=3;ConnectRetryInterval=5;'
    contentType: 'text/plain'
  }
}

output keyVaultId string = keyVault.id
output keyVaultName string = keyVault.name
output keyVaultUri string = keyVault.properties.vaultUri
