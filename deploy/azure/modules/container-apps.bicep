// GuideAnts azure-slim Container Apps (6 services)
param location string
param environmentName string
param appNamePrefix string
param containerAppsEnvironmentId string
param containerAppsEnvironmentDefaultDomain string
param keyVaultName string
param keyVaultUri string
param storageAccountName string
param ghcrOwner string
param imageTag string
param customDomain string
param documentServerEnabled bool
param appInsightsConnectionString string
param sqlDatabaseName string = 'guideants'
param tags object

var sqlServerFqdn = '${appNamePrefix}-sql-${environmentName}.${environment().suffixes.sqlServerHostname}'

var webApiAppName = 'guideants-webapi-ui'
var aiAppName = 'guideants-ai'
var doclingAppName = 'docling-serve'
var plantumlAppName = 'plantuml'
var searxngAppName = 'searxng'
var documentServerAppName = 'documentserver'

var publicApiUrl = customDomain != '' ? 'https://${customDomain}' : 'https://${webApiAppName}.${containerAppsEnvironmentDefaultDomain}'
var allowedOrigins = customDomain != '' ? 'https://${customDomain}' : '*'

// ACA internal ingress requires {app}.internal.{envDefaultDomain} — bare short names do not resolve.
var searxngInternalUrl = 'http://${searxngAppName}.internal.${containerAppsEnvironmentDefaultDomain}'
var aiInternalUrl = 'http://${aiAppName}.internal.${containerAppsEnvironmentDefaultDomain}'
var doclingInternalUrl = 'http://${doclingAppName}.internal.${containerAppsEnvironmentDefaultDomain}'
// HTTPS on purpose: ACA terminates TLS at the ingress and forwards to the container as
// plain http, so ONLYOFFICE only learns the external scheme from X-Forwarded-Proto, which
// Envoy stamps from how the caller connected. Reaching DocumentServer over https makes it
// emit https asset/cache URLs (e.g. Editor.bin); http here causes mixed-content -4 failures.
var documentServerInternalUrl = 'https://${documentServerAppName}.internal.${containerAppsEnvironmentDefaultDomain}'
var plantumlInternalUrl = 'http://${plantumlAppName}.internal.${containerAppsEnvironmentDefaultDomain}'

var webApiImage = 'ghcr.io/${ghcrOwner}/guideants-webapi-ui-slim:${imageTag}'
var aiImage = 'ghcr.io/${ghcrOwner}/guideants-ai-slim:${imageTag}'
var plantumlImage = 'ghcr.io/${ghcrOwner}/guideants-plantuml:${imageTag}'
var searxngImage = 'ghcr.io/${ghcrOwner}/guideants-searxng:${imageTag}'
var doclingImage = 'quay.io/docling-project/docling-serve-cpu:v1.21.0'
var documentServerImage = 'ghcr.io/euro-office/documentserver:latest'

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: keyVaultName
}

resource storageAccount 'Microsoft.Storage/storageAccounts@2023-01-01' existing = {
  name: storageAccountName
}

resource containerAppsEnvironment 'Microsoft.App/managedEnvironments@2023-05-01' existing = {
  name: split(containerAppsEnvironmentId, '/')[8]
}

resource containerAppsManagedIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' existing = {
  name: 'id-${appNamePrefix}-containers-${environmentName}'
}

resource sqlConnectionStringSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: 'sql-connection-string'
  properties: {
    value: 'Server=tcp:${sqlServerFqdn},1433;Initial Catalog=${sqlDatabaseName};Authentication=Active Directory Managed Identity;User ID=${containerAppsManagedIdentity.properties.clientId};Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;ConnectRetryCount=3;ConnectRetryInterval=5;'
    contentType: 'text/plain'
  }
}

resource contentFilesStorage 'Microsoft.App/managedEnvironments/storages@2023-05-01' = {
  name: 'contentfiles-storage'
  parent: containerAppsEnvironment
  properties: {
    azureFile: {
      accountName: storageAccountName
      accountKey: storageAccount.listKeys().keys[0].value
      shareName: 'contentfiles'
      accessMode: 'ReadWrite'
    }
  }
}

resource searxngConfigStorage 'Microsoft.App/managedEnvironments/storages@2023-05-01' = {
  name: 'searxng-config-storage'
  parent: containerAppsEnvironment
  properties: {
    azureFile: {
      accountName: storageAccountName
      accountKey: storageAccount.listKeys().keys[0].value
      shareName: 'searxng-config'
      accessMode: 'ReadWrite'
    }
  }
}

resource searxngDataStorage 'Microsoft.App/managedEnvironments/storages@2023-05-01' = {
  name: 'searxng-data-storage'
  parent: containerAppsEnvironment
  properties: {
    azureFile: {
      accountName: storageAccountName
      accountKey: storageAccount.listKeys().keys[0].value
      shareName: 'searxng-data'
      accessMode: 'ReadWrite'
    }
  }
}

resource scriptAgentStateStorage 'Microsoft.App/managedEnvironments/storages@2023-05-01' = {
  name: 'script-agent-state-storage'
  parent: containerAppsEnvironment
  properties: {
    azureFile: {
      accountName: storageAccountName
      accountKey: storageAccount.listKeys().keys[0].value
      shareName: 'script-agent-state'
      accessMode: 'ReadWrite'
    }
  }
}

var sharedSecrets = [
  {
    name: 'jwt-signing-key'
    keyVaultUrl: '${keyVaultUri}secrets/jwt-signing-key'
    identity: containerAppsManagedIdentity.id
  }
  {
    name: 'settings-secrets-key-azure-deploy'
    keyVaultUrl: '${keyVaultUri}secrets/settings-secrets-key-azure-deploy'
    identity: containerAppsManagedIdentity.id
  }
  {
    name: 'script-agent-token'
    keyVaultUrl: '${keyVaultUri}secrets/script-agent-token'
    identity: containerAppsManagedIdentity.id
  }
  {
    name: 'script-agent-admin-token'
    keyVaultUrl: '${keyVaultUri}secrets/script-agent-admin-token'
    identity: containerAppsManagedIdentity.id
  }
  {
    name: 'sql-connection-string'
    keyVaultUrl: '${keyVaultUri}secrets/sql-connection-string'
    identity: containerAppsManagedIdentity.id
  }
  {
    name: 'documentserver-jwt-secret'
    keyVaultUrl: '${keyVaultUri}secrets/documentserver-jwt-secret'
    identity: containerAppsManagedIdentity.id
  }
]

resource webApiApp 'Microsoft.App/containerApps@2023-05-01' = {
  name: webApiAppName
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${containerAppsManagedIdentity.id}': {}
    }
  }
  properties: {
    managedEnvironmentId: containerAppsEnvironmentId
    workloadProfileName: 'consumption'
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: 8080
        allowInsecure: false
        traffic: [
          {
            weight: 100
            latestRevision: true
          }
        ]
      }
      secrets: sharedSecrets
    }
    template: {
      containers: [
        {
          name: webApiAppName
          image: webApiImage
          resources: {
            cpu: json('2.0')
            memory: '4Gi'
          }
          env: concat([
            {
              name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
              value: appInsightsConnectionString
            }
            {
              name: 'ASPNETCORE_ENVIRONMENT'
              value: 'Production'
            }
            {
              name: 'ASPNETCORE_URLS'
              value: 'http://127.0.0.1:8081'
            }
            {
              name: 'ASPNETCORE_FORWARDEDHEADERS_ENABLED'
              value: 'true'
            }
            {
              name: 'AZURE_CLIENT_ID'
              value: containerAppsManagedIdentity.properties.clientId
            }
            {
              name: 'API_RUNTIME_CONTEXT'
              value: 'azure-slim'
            }
            {
              name: 'ALLOWED_ORIGINS'
              value: allowedOrigins
            }
            {
              name: 'ConnectionStrings__DefaultConnection'
              secretRef: 'sql-connection-string'
            }
            {
              name: 'Jwt__SigningKey'
              secretRef: 'jwt-signing-key'
            }
            {
              name: 'SettingsSecrets__ActiveKeyId'
              value: 'azure-deploy'
            }
            {
              name: 'SettingsSecrets__Keys__azure-deploy'
              secretRef: 'settings-secrets-key-azure-deploy'
            }
            {
              name: 'FileStorage__Path'
              value: '/app/ContentFiles'
            }
            {
              name: 'Ui__RootPath'
              value: '/app/ui'
            }
            {
              name: 'Ui__DevServerUrl'
              value: ''
            }
            {
              name: 'SearXngSearch__BaseUrl'
              value: searxngInternalUrl
            }
            {
              name: 'BrowserRendering__BaseUrl'
              value: searxngInternalUrl
            }
            {
              name: 'LocalServiceHosts__SpeechTranscriptionBaseUrl'
              value: 'http://127.0.0.1:9'
            }
            {
              name: 'LocalServiceHosts__SpeechSynthesisBaseUrl'
              value: 'http://127.0.0.1:9'
            }
            {
              name: 'LocalServiceHosts__ImageGenerationBaseUrl'
              value: 'http://127.0.0.1:9'
            }
            {
              name: 'LocalServiceHosts__EmbeddingsBaseUrl'
              value: 'http://127.0.0.1:9'
            }
            {
              name: 'LocalServiceHosts__MediaBaseUrl'
              value: aiInternalUrl
            }
            {
              name: 'LocalServiceHosts__DocumentIntelligenceBaseUrl'
              value: doclingInternalUrl
            }
            {
              name: 'DocumentServer__Enabled'
              value: string(documentServerEnabled)
            }
            {
              name: 'DocumentServer__InternalUrl'
              value: documentServerInternalUrl
            }
            {
              name: 'DocumentServer__ApiBaseUrl'
              value: publicApiUrl
            }
            {
              name: 'DocumentServer__JwtEnabled'
              value: string(documentServerEnabled)
            }
            {
              name: 'DocumentServer__JwtSecret'
              secretRef: 'documentserver-jwt-secret'
            }
            {
              name: 'DocumentServer__JwtHeader'
              value: 'Authorization'
            }
            {
              name: 'DocumentServer__JwtInBody'
              value: 'false'
            }
            {
              name: 'ScriptExecution__AgentToken'
              secretRef: 'script-agent-token'
            }
            {
              name: 'ScriptExecution__AdminToken'
              secretRef: 'script-agent-admin-token'
            }
            {
              name: 'ScriptExecution__TimeoutSeconds'
              value: '600'
            }
            {
              name: 'LlamaCpp__BaseUrl'
              value: 'http://127.0.0.1:9/llama-cpp'
            }
            {
              name: 'LlamaCpp__TimeoutSeconds'
              value: '600'
            }
            {
              name: 'ServiceRouting__Containers__guideants-ai__BaseUrl'
              value: '${aiInternalUrl}/sandbox'
            }
            {
              name: 'ServiceRouting__Containers__plantuml__BaseUrl'
              value: plantumlInternalUrl
            }
          ], [])
          volumeMounts: [
            {
              volumeName: 'contentfiles-volume'
              mountPath: '/app/ContentFiles'
            }
          ]
        }
      ]
      scale: {
        minReplicas: 1
        maxReplicas: 3
      }
      volumes: [
        {
          name: 'contentfiles-volume'
          storageType: 'AzureFile'
          storageName: 'contentfiles-storage'
        }
      ]
    }
  }
  dependsOn: [
    contentFilesStorage
    sqlConnectionStringSecret
  ]
}

resource aiApp 'Microsoft.App/containerApps@2023-05-01' = {
  name: aiAppName
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${containerAppsManagedIdentity.id}': {}
    }
  }
  properties: {
    managedEnvironmentId: containerAppsEnvironmentId
    workloadProfileName: 'consumption'
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: false
        targetPort: 80
        allowInsecure: true
      }
      secrets: [
        {
          name: 'script-agent-token'
          keyVaultUrl: '${keyVaultUri}secrets/script-agent-token'
          identity: containerAppsManagedIdentity.id
        }
        {
          name: 'script-agent-admin-token'
          keyVaultUrl: '${keyVaultUri}secrets/script-agent-admin-token'
          identity: containerAppsManagedIdentity.id
        }
      ]
    }
    template: {
      containers: [
        {
          name: aiAppName
          image: aiImage
          resources: {
            cpu: json('1.0')
            memory: '2Gi'
          }
          env: [
            {
              name: 'FILE_STORAGE_ROOT'
              value: '/app/ContentFiles'
            }
            {
              name: 'SCRIPT_EXECUTION_AGENT_TOKEN'
              secretRef: 'script-agent-token'
            }
            {
              name: 'SCRIPT_EXECUTION_ADMIN_TOKEN'
              secretRef: 'script-agent-admin-token'
            }
            {
              name: 'SCRIPT_EXECUTION_ADMIN_STATE_DIR'
              value: '/var/lib/guideants/script-agent-admin'
            }
            {
              name: 'SCRIPT_EXECUTION_SCOPE_STATE_ROOT'
              value: '/var/lib/guideants/script-agent-admin/scopes'
            }
            {
              name: 'SCRIPT_EXECUTION_REQUIRE_TOKEN'
              value: 'true'
            }
            {
              name: 'SCRIPT_EXECUTION_TIMEOUT_SECONDS'
              value: '600'
            }
          ]
          volumeMounts: [
            {
              volumeName: 'contentfiles-volume'
              mountPath: '/app/ContentFiles'
            }
            {
              volumeName: 'script-agent-state-volume'
              mountPath: '/var/lib/guideants/script-agent-admin'
            }
          ]
        }
      ]
      scale: {
        minReplicas: 1
        maxReplicas: 2
      }
      volumes: [
        {
          name: 'contentfiles-volume'
          storageType: 'AzureFile'
          storageName: 'contentfiles-storage'
        }
        {
          name: 'script-agent-state-volume'
          storageType: 'AzureFile'
          storageName: 'script-agent-state-storage'
        }
      ]
    }
  }
  dependsOn: [
    contentFilesStorage
    scriptAgentStateStorage
  ]
}

resource doclingApp 'Microsoft.App/containerApps@2023-05-01' = {
  name: doclingAppName
  location: location
  tags: tags
  properties: {
    managedEnvironmentId: containerAppsEnvironmentId
    workloadProfileName: 'consumption'
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: false
        targetPort: 5001
        allowInsecure: true
      }
    }
    template: {
      containers: [
        {
          name: doclingAppName
          image: doclingImage
          resources: {
            cpu: json('2.0')
            memory: '4Gi'
          }
          env: [
            {
              name: 'DOCLING_SERVE_MAX_SYNC_WAIT'
              value: '600'
            }
            {
              name: 'DOCLING_SERVE_MAX_FILE_SIZE'
              value: '524288000'
            }
            {
              name: 'DOCLING_SERVE_ENG_LOC_NUM_WORKERS'
              value: '2'
            }
            {
              name: 'DOCLING_SERVE_ENG_LOC_SHARE_MODELS'
              value: 'false'
            }
            {
              name: 'DOCLING_NUM_THREADS'
              value: '4'
            }
            {
              name: 'DOCLING_SERVE_LOAD_MODELS_AT_BOOT'
              value: 'true'
            }
            {
              name: 'DOCLING_SERVE_OPTIONS_CACHE_SIZE'
              value: '2'
            }
            {
              name: 'DOCLING_SERVE_LOG_LEVEL'
              value: 'WARNING'
            }
            {
              name: 'DOCLING_SERVE_LOG_FORMAT'
              value: 'text'
            }
            {
              name: 'DOCLING_SERVE_OTEL_ENABLE_METRICS'
              value: 'true'
            }
            {
              name: 'DOCLING_SERVE_OTEL_ENABLE_TRACES'
              value: 'false'
            }
            {
              name: 'DOCLING_DEBUG_PROFILE_PIPELINE_TIMINGS'
              value: 'false'
            }
          ]
        }
      ]
      scale: {
        minReplicas: 1
        maxReplicas: 2
      }
    }
  }
}

resource plantumlApp 'Microsoft.App/containerApps@2023-05-01' = {
  name: plantumlAppName
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${containerAppsManagedIdentity.id}': {}
    }
  }
  properties: {
    managedEnvironmentId: containerAppsEnvironmentId
    workloadProfileName: 'consumption'
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: false
        targetPort: 80
        allowInsecure: true
      }
      secrets: [
        {
          name: 'script-agent-token'
          keyVaultUrl: '${keyVaultUri}secrets/script-agent-token'
          identity: containerAppsManagedIdentity.id
        }
      ]
    }
    template: {
      containers: [
        {
          name: plantumlAppName
          image: plantumlImage
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
          env: [
            {
              name: 'FILE_STORAGE_ROOT'
              value: '/app/ContentFiles'
            }
            {
              name: 'SCRIPT_EXECUTION_AGENT_TOKEN'
              secretRef: 'script-agent-token'
            }
            {
              name: 'SCRIPT_EXECUTION_REQUIRE_TOKEN'
              value: 'true'
            }
            {
              name: 'SCRIPT_EXECUTION_TIMEOUT_SECONDS'
              value: '600'
            }
            {
              name: 'PLANTUML_LIMIT_SIZE'
              value: '8192'
            }
          ]
          volumeMounts: [
            {
              volumeName: 'contentfiles-volume'
              mountPath: '/app/ContentFiles'
            }
          ]
        }
      ]
      scale: {
        minReplicas: 1
        maxReplicas: 2
      }
      volumes: [
        {
          name: 'contentfiles-volume'
          storageType: 'AzureFile'
          storageName: 'contentfiles-storage'
        }
      ]
    }
  }
  dependsOn: [
    contentFilesStorage
  ]
}

resource searxngApp 'Microsoft.App/containerApps@2023-05-01' = {
  name: searxngAppName
  location: location
  tags: tags
  properties: {
    managedEnvironmentId: containerAppsEnvironmentId
    workloadProfileName: 'consumption'
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: false
        targetPort: 8080
        allowInsecure: true
      }
    }
    template: {
      containers: [
        {
          name: searxngAppName
          image: searxngImage
          resources: {
            cpu: json('1.0')
            memory: '2Gi'
          }
          env: [
            {
              name: 'FORCE_OWNERSHIP'
              value: 'true'
            }
            {
              name: 'BROWSER_RENDER_PORT'
              value: '3001'
            }
          ]
          volumeMounts: [
            {
              volumeName: 'searxng-config-volume'
              mountPath: '/etc/searxng'
            }
            {
              volumeName: 'searxng-data-volume'
              mountPath: '/var/cache/searxng'
            }
          ]
        }
      ]
      scale: {
        minReplicas: 1
        maxReplicas: 2
      }
      volumes: [
        {
          name: 'searxng-config-volume'
          storageType: 'AzureFile'
          storageName: 'searxng-config-storage'
        }
        {
          name: 'searxng-data-volume'
          storageType: 'AzureFile'
          storageName: 'searxng-data-storage'
        }
      ]
    }
  }
  dependsOn: [
    searxngConfigStorage
    searxngDataStorage
  ]
}

resource documentServerApp 'Microsoft.App/containerApps@2023-05-01' = if (documentServerEnabled) {
  name: documentServerAppName
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${containerAppsManagedIdentity.id}': {}
    }
  }
  properties: {
    managedEnvironmentId: containerAppsEnvironmentId
    workloadProfileName: 'consumption'
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: false
        targetPort: 80
        allowInsecure: true
      }
      secrets: [
        {
          name: 'documentserver-jwt-secret'
          keyVaultUrl: '${keyVaultUri}secrets/documentserver-jwt-secret'
          identity: containerAppsManagedIdentity.id
        }
      ]
    }
    template: {
      containers: [
        {
          name: documentServerAppName
          image: documentServerImage
          resources: {
            cpu: json('2.0')
            memory: '4Gi'
          }
          env: [
            {
              name: 'JWT_ENABLED'
              value: 'true'
            }
            {
              name: 'JWT_SECRET'
              secretRef: 'documentserver-jwt-secret'
            }
            {
              name: 'JWT_HEADER'
              value: 'Authorization'
            }
            {
              name: 'JWT_IN_BODY'
              value: 'false'
            }
            {
              name: 'ALLOW_PRIVATE_IP_ADDRESS'
              value: 'true'
            }
          ]
        }
      ]
      scale: {
        minReplicas: 1
        maxReplicas: 2
      }
    }
  }
}

output webApiUiFqdn string = webApiApp.properties.configuration.ingress.fqdn
output webApiUiUrl string = 'https://${webApiApp.properties.configuration.ingress.fqdn}'
output containerAppsManagedIdentityId string = containerAppsManagedIdentity.id
output containerAppsManagedIdentityClientId string = containerAppsManagedIdentity.properties.clientId
output managedIdentityPrincipalId string = containerAppsManagedIdentity.properties.principalId
