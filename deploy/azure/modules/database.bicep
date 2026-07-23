// Database Module - Azure SQL Database (Waterfall-proven S2 pattern)
param location string
param environmentName string
param appNamePrefix string
@secure()
param sqlAdminPassword string
param tags object
param sqlAadAdminObjectId string = ''
param sqlDatabaseName string = 'guideants'

var sqlServerName = '${appNamePrefix}-sql-${environmentName}'
var sqlAdminUsername = 'sqladmin'

resource sqlServer 'Microsoft.Sql/servers@2023-05-01-preview' = {
  name: sqlServerName
  location: location
  tags: tags
  properties: {
    administratorLogin: sqlAdminUsername
    administratorLoginPassword: sqlAdminPassword
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
    restrictOutboundNetworkAccess: 'Disabled'
  }
  identity: {
    type: 'SystemAssigned'
  }
}

resource sqlDatabase 'Microsoft.Sql/servers/databases@2023-05-01-preview' = {
  parent: sqlServer
  name: sqlDatabaseName
  location: location
  tags: tags
  sku: {
    name: 'S2'
    tier: 'Standard'
    capacity: 50
  }
  properties: {
    collation: 'SQL_Latin1_General_CP1_CI_AS'
    maxSizeBytes: 268435456000
    catalogCollation: 'SQL_Latin1_General_CP1_CI_AS'
    zoneRedundant: false
    readScale: 'Disabled'
    requestedBackupStorageRedundancy: 'Local'
    isLedgerOn: false
  }
}

resource sqlServerFirewallRuleAzure 'Microsoft.Sql/servers/firewallRules@2023-05-01-preview' = {
  parent: sqlServer
  name: 'AllowAllAzureServices'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

resource sqlManagedIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'id-${appNamePrefix}-sql-${environmentName}'
  location: location
  tags: tags
}

resource sqlDatabaseContributorRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: sqlServer
  name: guid(sqlServer.id, sqlManagedIdentity.id, 'SqlDbContributor')
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '9b7fa17d-e63e-47b0-bb0a-15c516ac86ec')
    principalId: sqlManagedIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource sqlAdAdmin 'Microsoft.Sql/servers/administrators@2023-05-01-preview' = if (length(sqlAadAdminObjectId) > 0) {
  parent: sqlServer
  name: 'activeDirectory'
  properties: {
    administratorType: 'ActiveDirectory'
    login: 'guideants-admin'
    sid: sqlAadAdminObjectId
    tenantId: subscription().tenantId
  }
}

resource sqlDatabaseAuditing 'Microsoft.Sql/servers/databases/extendedAuditingSettings@2023-05-01-preview' = {
  parent: sqlDatabase
  name: 'default'
  properties: {
    state: 'Enabled'
    storageEndpoint: ''
    retentionDays: 7
    isAzureMonitorTargetEnabled: true
  }
}

output sqlServerId string = sqlServer.id
output sqlServerName string = sqlServer.name
output sqlServerFqdn string = sqlServer.properties.fullyQualifiedDomainName
output sqlDatabaseId string = sqlDatabase.id
output sqlDatabaseName string = sqlDatabase.name
output sqlManagedIdentityId string = sqlManagedIdentity.id
output sqlManagedIdentityClientId string = sqlManagedIdentity.properties.clientId
output sqlManagedIdentityPrincipalId string = sqlManagedIdentity.properties.principalId
output sqlConnectionStringTemplate string = 'Server=tcp:${sqlServer.properties.fullyQualifiedDomainName},1433;Initial Catalog=${sqlDatabaseName};Authentication=Active Directory Managed Identity;TrustServerCertificate=False;Encrypt=True;'
