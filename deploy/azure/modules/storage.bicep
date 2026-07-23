// Storage Module - Azure Files shares for GuideAnts azure-slim
param location string
param environmentName string
param appNamePrefix string
param tags object

var storageAccountName = 'st${take(appNamePrefix, 4)}${take(environmentName, 3)}${take(uniqueString(resourceGroup().id), 10)}'

resource storageAccount 'Microsoft.Storage/storageAccounts@2023-01-01' = {
  name: storageAccountName
  location: location
  tags: tags
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  properties: {
    accessTier: 'Hot'
    allowBlobPublicAccess: false
    allowSharedKeyAccess: true
    minimumTlsVersion: 'TLS1_2'
    networkAcls: {
      bypass: 'AzureServices'
      defaultAction: 'Allow'
    }
    publicNetworkAccess: 'Enabled'
    supportsHttpsTrafficOnly: true
  }
}

resource fileServices 'Microsoft.Storage/storageAccounts/fileServices@2023-01-01' = {
  parent: storageAccount
  name: 'default'
  properties: {
    cors: {
      corsRules: []
    }
    shareDeleteRetentionPolicy: {
      enabled: true
      days: 7
    }
  }
}

resource contentFilesShare 'Microsoft.Storage/storageAccounts/fileServices/shares@2023-01-01' = {
  parent: fileServices
  name: 'contentfiles'
  properties: {
    accessTier: 'Hot'
    enabledProtocols: 'SMB'
    shareQuota: 100
  }
}

resource searxngConfigShare 'Microsoft.Storage/storageAccounts/fileServices/shares@2023-01-01' = {
  parent: fileServices
  name: 'searxng-config'
  properties: {
    accessTier: 'Hot'
    enabledProtocols: 'SMB'
    shareQuota: 1
  }
}

resource searxngDataShare 'Microsoft.Storage/storageAccounts/fileServices/shares@2023-01-01' = {
  parent: fileServices
  name: 'searxng-data'
  properties: {
    accessTier: 'Hot'
    enabledProtocols: 'SMB'
    shareQuota: 10
  }
}

resource scriptAgentStateShare 'Microsoft.Storage/storageAccounts/fileServices/shares@2023-01-01' = {
  parent: fileServices
  name: 'script-agent-state'
  properties: {
    accessTier: 'Hot'
    enabledProtocols: 'SMB'
    shareQuota: 10
  }
}

resource storageManagedIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'id-${appNamePrefix}-storage-${environmentName}'
  location: location
  tags: tags
}

resource storageBlobDataContributorRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: storageAccount
  name: guid(storageAccount.id, storageManagedIdentity.id, 'StorageBlobDataContributor')
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
    principalId: storageManagedIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource storageFileDataSMBShareContributorRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: storageAccount
  name: guid(storageAccount.id, storageManagedIdentity.id, 'StorageFileDataSMBShareContributor')
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '0c867c2a-1d8c-454a-a3db-ab2ea1bdc8bb')
    principalId: storageManagedIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

output storageAccountId string = storageAccount.id
output storageAccountName string = storageAccount.name
output contentFilesShareName string = contentFilesShare.name
output searxngConfigShareName string = searxngConfigShare.name
output searxngDataShareName string = searxngDataShare.name
output scriptAgentStateShareName string = scriptAgentStateShare.name
output storageManagedIdentityId string = storageManagedIdentity.id
output storageManagedIdentityClientId string = storageManagedIdentity.properties.clientId
output storageManagedIdentityPrincipalId string = storageManagedIdentity.properties.principalId
