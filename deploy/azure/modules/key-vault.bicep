// Key Vault Module - bootstrap secrets for GuideAnts azure-slim
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

@description('Object ID of the deployer principal (user or service principal) for post-deploy Key Vault updates')
param deployerObjectId string = ''

@description('Principal type for deployerObjectId (User or ServicePrincipal)')
@allowed([
  'User'
  'ServicePrincipal'
])
param deployerPrincipalType string = 'User'

param tags object

var keyVaultName = 'kv-${take(appNamePrefix, 4)}-${take(environmentName, 3)}-${take(uniqueString(resourceGroup().id), 8)}'

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
    enableRbacAuthorization: true
    enableSoftDelete: true
    enablePurgeProtection: true
    softDeleteRetentionInDays: 7
    networkAcls: {
      bypass: 'AzureServices'
      defaultAction: 'Allow'
    }
    publicNetworkAccess: 'Enabled'
  }
}

resource keyVaultManagedIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'id-${appNamePrefix}-kv-${environmentName}'
  location: location
  tags: tags
}

resource keyVaultSecretsOfficerRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: keyVault
  name: guid(keyVault.id, keyVaultManagedIdentity.id, 'KeyVaultSecretsOfficer')
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'b86a8fe4-44ce-4948-aee5-eccb2c155cd7')
    principalId: keyVaultManagedIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource deployerSecretsOfficerRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(deployerObjectId)) {
  scope: keyVault
  name: guid(keyVault.id, deployerObjectId, 'KeyVaultSecretsOfficer-Deployer')
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'b86a8fe4-44ce-4948-aee5-eccb2c155cd7')
    principalId: deployerObjectId
    principalType: deployerPrincipalType
  }
}

resource jwtSigningKeySecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: 'jwt-signing-key'
  properties: {
    value: jwtSigningKey
    contentType: 'text/plain'
  }
  dependsOn: [
    keyVaultSecretsOfficerRoleAssignment
  ]
}

resource settingsSecretsKeySecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: 'settings-secrets-key-azure-deploy'
  properties: {
    value: settingsSecretsKey
    contentType: 'text/plain'
  }
  dependsOn: [
    keyVaultSecretsOfficerRoleAssignment
  ]
}

resource scriptAgentTokenSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: 'script-agent-token'
  properties: {
    value: scriptAgentToken
    contentType: 'text/plain'
  }
  dependsOn: [
    keyVaultSecretsOfficerRoleAssignment
  ]
}

resource scriptAgentAdminTokenSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: 'script-agent-admin-token'
  properties: {
    value: scriptAgentAdminToken
    contentType: 'text/plain'
  }
  dependsOn: [
    keyVaultSecretsOfficerRoleAssignment
  ]
}

resource documentServerJwtSecretResource 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: 'documentserver-jwt-secret'
  properties: {
    value: documentServerJwtSecret
    contentType: 'text/plain'
  }
  dependsOn: [
    keyVaultSecretsOfficerRoleAssignment
  ]
}

resource sqlAdminPasswordSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: 'sql-admin-password'
  properties: {
    value: sqlAdminPassword
    contentType: 'text/plain'
  }
  dependsOn: [
    keyVaultSecretsOfficerRoleAssignment
  ]
}

resource sqlConnectionStringSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: 'sql-connection-string'
  properties: {
    value: 'Server=tcp:${appNamePrefix}-sql-${environmentName}.${environment().suffixes.sqlServerHostname},1433;Initial Catalog=${sqlDatabaseName};Authentication=Active Directory Managed Identity;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;ConnectRetryCount=3;ConnectRetryInterval=5;'
    contentType: 'text/plain'
  }
  dependsOn: [
    keyVaultSecretsOfficerRoleAssignment
  ]
}

output keyVaultId string = keyVault.id
output keyVaultName string = keyVault.name
output keyVaultUri string = keyVault.properties.vaultUri
output keyVaultManagedIdentityId string = keyVaultManagedIdentity.id
output keyVaultManagedIdentityClientId string = keyVaultManagedIdentity.properties.clientId
output keyVaultManagedIdentityPrincipalId string = keyVaultManagedIdentity.properties.principalId
