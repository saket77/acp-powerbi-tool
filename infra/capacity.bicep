// The Fabric capacity, deployed on its own.
//
// Separate from main.bicep on purpose: this is the only resource that costs real money, it gets
// paused and resumed constantly during development (scripts/capacity.sh), and it must survive
// redeploys of everything else. Workspaces are assigned to it by the application at runtime, so
// destroying and recreating it would orphan every client workspace.
//
// Not needed before Phase 4. Create it, then immediately pause it.

targetScope = 'resourceGroup'

@description('Short environment name. e.g. dev, demo, prod')
@maxLength(8)
param environmentName string

@allowed([
  'germanywestcentral'
  'westeurope'
])
param location string = 'germanywestcentral'

@description('''
Capacity size. F2 is the smallest and is what the MVP budget assumes (~EUR 240/month running,
near nothing while paused). Raise it only after a load test with the heaviest Databook page —
the plan deliberately refuses to name a production SKU before that measurement exists.
''')
@allowed([
  'F2'
  'F4'
  'F8'
  'F16'
])
param skuName string = 'F2'

@description('Entra object ids of the capacity administrators. The platform owner, plus the backup admin.')
param adminObjectIds array

resource capacity 'Microsoft.Fabric/capacities@2023-11-01' = {
  name: toLower('acpdb${environmentName}cap')
  location: location
  tags: {
    product: 'acp-databook-portal'
    environment: environmentName
    managedBy: 'bicep'
    costWarning: 'Billed per second while running. Pause with scripts/capacity.sh pause.'
  }
  sku: {
    name: skuName
    tier: 'Fabric'
  }
  properties: {
    administration: {
      members: adminObjectIds
    }
  }
}

output capacityId string = capacity.id
output capacityName string = capacity.name

// Reminder, because it is the single biggest cost lever in the project:
//   scripts/capacity.sh pause
