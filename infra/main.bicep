// Azure resources for the portal, minus the Fabric capacity (see capacity.bicep, which is
// deployed separately so it can be paused and resumed without touching anything else).
//
// Phase 1 status: this deploys correctly but nothing in the application reads from it yet.
// It exists now so that infrastructure is written as code from the first commit rather than
// reconstructed later from a portal somebody clicked through.

targetScope = 'resourceGroup'

@description('Short environment name, used in resource names. e.g. dev, demo, prod')
@maxLength(8)
param environmentName string

@description('Azure region. EU only — see docs/product-brief.md, ground rules.')
@allowed([
  'germanywestcentral'
  'westeurope'
])
param location string = 'germanywestcentral'

@description('Entra object id of the person or group to make SQL administrator.')
param sqlAdminObjectId string

@description('Display name for the SQL administrator.')
param sqlAdminLogin string

var prefix = 'acpdb-${environmentName}'
var tags = {
  product: 'acp-databook-portal'
  environment: environmentName
  managedBy: 'bicep'
}

// --- Identity ---------------------------------------------------------------------------------
// One user-assigned identity for the whole application. Power BI service principal profiles
// belong permanently to the identity that creates them, so this resource must outlive every
// deployment — hence a resource lock below, and never a system-assigned identity, which would
// be destroyed and recreated with the App Service.
resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${prefix}-id'
  location: location
  tags: tags
}

resource identityLock 'Microsoft.Authorization/locks@2020-05-01' = {
  scope: identity
  name: '${prefix}-id-lock'
  properties: {
    level: 'CanNotDelete'
    notes: 'Owns the Power BI service principal profiles. Deleting it orphans every client workspace.'
  }
}

// --- Data -------------------------------------------------------------------------------------
resource sqlServer 'Microsoft.Sql/servers@2023-08-01-preview' = {
  name: '${prefix}-sql'
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${identity.id}': {} }
  }
  properties: {
    // Entra-only authentication: there is no SQL password to leak or rotate.
    administrators: {
      administratorType: 'ActiveDirectory'
      azureADOnlyAuthentication: true
      login: sqlAdminLogin
      sid: sqlAdminObjectId
      principalType: 'Group'
    }
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
  }
}

resource sqlDatabase 'Microsoft.Sql/servers/databases@2023-08-01-preview' = {
  parent: sqlServer
  name: 'portal'
  location: location
  tags: tags
  sku: {
    name: 'GP_S_Gen5'
    tier: 'GeneralPurpose'
    family: 'Gen5'
    capacity: 2
  }
  properties: {
    // Serverless, so it sleeps when nobody is using it. The MVP is idle most of the time.
    autoPauseDelay: 60
    minCapacity: json('0.5')
    zoneRedundant: false
  }
}

resource allowAzureServices 'Microsoft.Sql/servers/firewallRules@2023-08-01-preview' = {
  parent: sqlServer
  name: 'AllowAllWindowsAzureIps'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

// --- Storage ----------------------------------------------------------------------------------
// Blob holds the uploaded .pbix until it is imported; Queue carries the import jobs.
resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: replace('${prefix}st', '-', '')
  location: location
  tags: tags
  sku: { name: 'Standard_LRS' }
  kind: 'StorageV2'
  properties: {
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    supportsHttpsTrafficOnly: true
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storage
  name: 'default'
  properties: {
    deleteRetentionPolicy: { enabled: true, days: 7 }
    cors: {
      // The browser uploads straight to Blob with a short-lived write-only SAS, so the storage
      // account has to allow it. Phase 5 narrows allowedOrigins to the deployed portal.
      corsRules: [
        {
          allowedOrigins: ['https://${prefix}-app.azurewebsites.net']
          allowedMethods: ['PUT', 'OPTIONS']
          allowedHeaders: ['*']
          exposedHeaders: ['*']
          maxAgeInSeconds: 3600
        }
      ]
    }
  }
}

resource uploadsContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: 'pbix-uploads'
  properties: { publicAccess: 'None' }
}

// --- Secrets and monitoring --------------------------------------------------------------------
resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: '${prefix}-kv'
  location: location
  tags: tags
  properties: {
    sku: { family: 'A', name: 'standard' }
    tenantId: subscription().tenantId
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 90
  }
}

resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: '${prefix}-logs'
  location: location
  tags: tags
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: '${prefix}-ai'
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logAnalytics.id
  }
}

// --- Hosting ------------------------------------------------------------------------------------
resource appServicePlan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: '${prefix}-plan'
  location: location
  tags: tags
  sku: { name: 'B1', tier: 'Basic' }
  properties: { reserved: true }
}

resource webApp 'Microsoft.Web/sites@2023-12-01' = {
  name: '${prefix}-app'
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${identity.id}': {} }
  }
  properties: {
    serverFarmId: appServicePlan.id
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|10.0'
      alwaysOn: true
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      http20Enabled: true
      appSettings: [
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsights.properties.ConnectionString }
        { name: 'AZURE_CLIENT_ID', value: identity.properties.clientId }
        { name: 'Storage__AccountName', value: storage.name }
        { name: 'Storage__UploadsContainer', value: uploadsContainer.name }
        { name: 'KeyVault__Uri', value: keyVault.properties.vaultUri }
      ]
    }
  }
}

// --- Access -------------------------------------------------------------------------------------
// Shared keys are switched off on the storage account, so the app reaches it by role instead.
var blobDataContributor = 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
var queueDataContributor = '974c5e8b-45b9-4653-ba55-5f855dd0fb88'
var keyVaultSecretsUser = '4633458b-17de-408a-b874-0445c86b69e6'

resource blobRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: storage
  name: guid(storage.id, identity.id, blobDataContributor)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', blobDataContributor)
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource queueRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: storage
  name: guid(storage.id, identity.id, queueDataContributor)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', queueDataContributor)
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource keyVaultRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: keyVault
  name: guid(keyVault.id, identity.id, keyVaultSecretsUser)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUser)
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

output identityClientId string = identity.properties.clientId
output identityPrincipalId string = identity.properties.principalId
output sqlServerFqdn string = sqlServer.properties.fullyQualifiedDomainName
output storageAccountName string = storage.name
output webAppName string = webApp.name
output webAppUrl string = 'https://${webApp.properties.defaultHostName}'
