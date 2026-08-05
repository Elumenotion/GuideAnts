// Container Apps Environment Module
param location string
param containerAppsEnvironmentName string
param containerAppsSubnetId string
param tags object

resource containerAppsEnvironment 'Microsoft.App/managedEnvironments@2025-01-01' = {
  name: containerAppsEnvironmentName
  location: location
  tags: tags
  properties: {
    // Null destination = don't save logs (live stream still works via az/portal).
    // String 'none' fails preflight on several API versions; null is the supported create shape.
    appLogsConfiguration: {
      destination: null
      logAnalyticsConfiguration: null
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
