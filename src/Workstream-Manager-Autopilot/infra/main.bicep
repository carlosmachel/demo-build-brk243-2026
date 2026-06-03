targetScope = 'resourceGroup'

// =================================================================================================
// Main parameters
// =================================================================================================

@minLength(1)
@maxLength(64)
@description('Name of the application. Used to ensure resource names are unique.')
param environmentName string

@minLength(1)
@description('Primary location for all resources')
param location string

// =================================================================================================
// Project module parameters
// =================================================================================================

@description('Name of the Cognitive Services account')
param accountName string = '${environmentName}acct'

@description('Name of the Cognitive Services project')
param projectName string = '${environmentName}proj'

@description('Name of the Container Registry')
param containerRegistryName string = '${environmentName}acr'

@description('SKU of Cognitive Services account')
param cognitiveServicesSku string = 'S0'

@description('SKU of Container Registry')
@allowed(['Basic', 'Standard', 'Premium'])
param containerRegistrySku string = 'Basic'

@description('Name of the model to deploy')
param modelName string = 'gpt-5-chat'

@description('Version of the model to deploy')
param modelVersion string = '2025-10-03'

param agentName string = '${environmentName}-agent'

param maibName string = '${environmentName}-maib'

// =================================================================================================
// Bot Service module parameters
// =================================================================================================

@description('Name of the Bot Service 1')
param botName string = '${environmentName}-bot'

@description('Display name of the bot')
param botDisplayName string = '${environmentName} Bot'

@description('SKU of the Bot Service')
param botServiceSku string = 'F0'

// =================================================================================================
// Direct-message allowlist Azure Table Storage parameters
// =================================================================================================

@description('Storage account used for direct-message allowlist table data')
param directMessageAllowListStorageAccountName string = take(toLower(replace('${environmentName}dmallowlist', '-', '')), 24)

@description('Table name used for direct-message allowlist data')
param directMessageAllowListTableName string = 'digitalworkerallowlist'

// =================================================================================================
// Work items Azure Table Storage parameters
// =================================================================================================

@description('Storage account used for work items table data')
param workItemsStorageAccountName string = take(toLower(replace('${environmentName}workitems', '-', '')), 24)

@description('Table name used for work items data')
param workItemsTableName string = 'workitems'

// =================================================================================================
// Common parameters
// =================================================================================================

@description('Tags to apply to all resources')
param tags object = {}

// =================================================================================================
// Module deployments
// =================================================================================================

// 1. Deploy the project module (Cognitive Services account, project, and Container Registry)
module project 'modules/project.bicep' = {
  name: 'project1-deployment'
  params: {
    accountName: accountName
    projectName: projectName
    containerRegistryName: containerRegistryName
    location: location
    tags: tags
    cognitiveServicesSku: cognitiveServicesSku
    containerRegistrySku: containerRegistrySku
    modelName: modelName
    modelVersion: modelVersion
  }
}

// 2. Create deployment script UMI and grant roles on RG.
module deploymentScriptUmi 'modules/deployment-script-umi.bicep' = {
  name: 'deployment-script-umi'
  dependsOn: [
    project
  ]
}

// 3. Create managed agent identity blueprint using a deployment script as that is a dataplane operation.
module deploymentScriptAgent 'modules/maib-creation-script.bicep' = {
  name: 'maib-creation-script'
  params: {
    uamiResourceId: deploymentScriptUmi.outputs.uamiResourceId
    azureAIProjectEndpoint: project.outputs.foundryProjectEndpoint
    maibName: maibName
  }
  dependsOn: [
    deploymentScriptUmi
  ]
}


// 4. Deploy the bot service module
module botService 'modules/botservice.bicep' = {
  name: 'botservice-deployment'
  params: {
    botName: botName
    displayName: botDisplayName
    msaAppId: deploymentScriptAgent.outputs.blueprintClientId
    endpoint: 'https://${accountName}.services.ai.azure.com/api/projects/${projectName}/agents/${agentName}/endpoint/protocols/activityProtocol?api-version=2025-05-15-preview'
    botServiceSku: botServiceSku
  }
  dependsOn: [
    deploymentScriptAgent
  ]
}

// 5. Deploy Azure Table Storage used for digital worker direct-message allowlists.
module directMessageAllowListTables 'modules/tables.bicep' = {
  name: 'direct-message-allowlist-tables'
  params: {
    storageAccountName: directMessageAllowListStorageAccountName
    tableName: directMessageAllowListTableName
    location: location
    tags: tags
  }
}

// 6. Deploy Azure Table Storage used for work item tracking.
module workItemsTables 'modules/tables.bicep' = {
  name: 'work-items-tables'
  params: {
    storageAccountName: workItemsStorageAccountName
    tableName: workItemsTableName
    location: location
    tags: tags
  }
}

// =================================================================================================
// Outputs - These become environment variables in post-provision.sh
// =================================================================================================

@description('ACR login server endpoint')
output AZURE_CONTAINER_REGISTRY_ENDPOINT string = project.outputs.acrloginServer

output AZURE_AI_PROJECT_ENDPOINT string = project.outputs.foundryProjectEndpoint

@description('Agent identity blueprint ID')
output AGENT_IDENTITY_BLUEPRINT_ID string = deploymentScriptAgent.outputs.blueprintClientId

output SUBSCRIPTION_ID string = subscription().subscriptionId

output LOCATION string = location

output ACCOUNT_NAME string = accountName

output PROJECT_NAME string = projectName

output AGENT_NAME string = agentName

output TENANT_ID string = tenant().tenantId

output PROJECT_PRINCIPAL_ID string = project.outputs.foundryProjectPrincipalId

output MAIB_NAME string = maibName

output PROJECT_DEFAULT_INSTANCE_CLIENT_ID string = project.outputs.foundryProjectDefaultInstanceClientId

output DIRECT_MESSAGE_ALLOWLIST_TABLE_SERVICE_URI string = directMessageAllowListTables.outputs.tableServiceUri

output DIRECT_MESSAGE_ALLOWLIST_TABLE_NAME string = directMessageAllowListTables.outputs.tableName

output DIRECT_MESSAGE_ALLOWLIST_STORAGE_ACCOUNT_RESOURCE_ID string = directMessageAllowListTables.outputs.storageAccountResourceId

output WORK_ITEMS_TABLE_SERVICE_URI string = workItemsTables.outputs.tableServiceUri

output WORK_ITEMS_TABLE_NAME string = workItemsTables.outputs.tableName

output WORK_ITEMS_STORAGE_ACCOUNT_RESOURCE_ID string = workItemsTables.outputs.storageAccountResourceId
