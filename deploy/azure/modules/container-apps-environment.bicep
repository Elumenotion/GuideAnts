// Container Apps Environment Module
param location string
param containerAppsEnvironmentName string
param containerAppsSubnetId string
param logAnalyticsWorkspaceId string
param tags object

resource containerAppsEnvironment 'Microsoft.App/managedEnvironments@2023-05-01' = {
  name: containerAppsEnvironmentName
  location: location
  tags: tags
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: reference(logAnalyticsWorkspaceId, '2023-09-01').customerId
        sharedKey: listKeys(logAnalyticsWorkspaceId, '2023-09-01').primarySharedKey
      }
    }
    vnetConfiguration: {
      internal: false
      infrastructureSubnetId: containerAppsSubnetId
    }
    zoneRedundant: false
    kedaConfiguration: {}
    daprConfiguration: {}
    workloadProfiles: [
      {
        name: 'consumption'
        workloadProfileType: 'Consumption'
      }
    ]
  }
}

output containerAppsEnvironmentId string = containerAppsEnvironment.id
output containerAppsEnvironmentName string = containerAppsEnvironment.name
output containerAppsEnvironmentDefaultDomain string = containerAppsEnvironment.properties.defaultDomain
output containerAppsEnvironmentStaticIp string = containerAppsEnvironment.properties.staticIp
