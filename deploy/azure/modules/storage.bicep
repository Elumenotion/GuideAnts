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

output storageAccountId string = storageAccount.id
output storageAccountName string = storageAccount.name
output contentFilesShareName string = contentFilesShare.name
output searxngConfigShareName string = searxngConfigShare.name
output searxngDataShareName string = searxngDataShare.name
output scriptAgentStateShareName string = scriptAgentStateShare.name
