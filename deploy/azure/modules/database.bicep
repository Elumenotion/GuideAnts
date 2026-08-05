// Database Module - Azure SQL Database (GP serverless, auto-pause for low at-rest cost)
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
    name: 'GP_S_Gen5'
    tier: 'GeneralPurpose'
    family: 'Gen5'
    capacity: 1
  }
  properties: {
    collation: 'SQL_Latin1_General_CP1_CI_AS'
    maxSizeBytes: 34359738368 // 32 GB
    catalogCollation: 'SQL_Latin1_General_CP1_CI_AS'
    zoneRedundant: false
    readScale: 'Disabled'
    requestedBackupStorageRedundancy: 'Local'
    isLedgerOn: false
    // Pause after 15 minutes idle (platform minimum). Compute bills only while resumed.
    autoPauseDelay: 15
    minCapacity: json('0.5')
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

// Disabled by default: auditing to Azure Monitor feeds Log Analytics and is not required for app console diagnostics.
resource sqlDatabaseAuditing 'Microsoft.Sql/servers/databases/extendedAuditingSettings@2023-05-01-preview' = {
  parent: sqlDatabase
  name: 'default'
  properties: {
    state: 'Disabled'
    storageEndpoint: ''
    retentionDays: 0
    isAzureMonitorTargetEnabled: false
  }
}

output sqlServerId string = sqlServer.id
output sqlServerName string = sqlServer.name
output sqlServerFqdn string = sqlServer.properties.fullyQualifiedDomainName
output sqlDatabaseId string = sqlDatabase.id
output sqlDatabaseName string = sqlDatabase.name
output sqlConnectionStringTemplate string = 'Server=tcp:${sqlServer.properties.fullyQualifiedDomainName},1433;Initial Catalog=${sqlDatabaseName};Authentication=Active Directory Managed Identity;TrustServerCertificate=False;Encrypt=True;'
