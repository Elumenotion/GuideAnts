// User-assigned identity for Container Apps runtime (Phase 1 — created early for Key Vault access policies)
param location string
param environmentName string
param appNamePrefix string
param tags object

resource containerAppsManagedIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'id-${appNamePrefix}-containers-${environmentName}'
  location: location
  tags: tags
}

output id string = containerAppsManagedIdentity.id
output name string = containerAppsManagedIdentity.name
output principalId string = containerAppsManagedIdentity.properties.principalId
output clientId string = containerAppsManagedIdentity.properties.clientId
