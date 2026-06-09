param location string = resourceGroup().location
param apimName string = 'apim-graphql-rest-dev'
param publisherName string = 'Platform Engineering'
param publisherEmail string
param graphqlApiName string = 'synthetic-graphql'
param graphqlApiPath string = 'graphql'

resource apim 'Microsoft.ApiManagement/service@2023-09-01-preview' = {
  name: apimName
  location: location
  sku: {
    name: 'Developer'
    capacity: 1
  }
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    publisherEmail: publisherEmail
    publisherName: publisherName
  }
}

resource graphqlApi 'Microsoft.ApiManagement/service/apis@2023-09-01-preview' = {
  parent: apim
  name: graphqlApiName
  properties: {
    type: 'graphql'
    displayName: 'Synthetic GraphQL'
    path: graphqlApiPath
    protocols: [
      'https'
    ]
    subscriptionRequired: false
    serviceUrl: 'https://example.invalid'
  }
}

resource graphqlSchema 'Microsoft.ApiManagement/service/apis/schemas@2023-09-01-preview' = {
  parent: graphqlApi
  name: 'graphql'
  properties: {
    contentType: 'application/vnd.ms-azure-apim.graphql.schema'
    document: {
      value: loadTextContent('../../generated/schema.graphql')
    }
  }
}

resource getUserResolver 'Microsoft.ApiManagement/service/apis/resolvers@2023-09-01-preview' = {
  parent: graphqlApi
  name: 'Query-getUser'
  properties: {
    displayName: 'Query.getUser'
    description: 'Resolve Query.getUser from REST GET /users/{id}'
    path: 'Query/getUser'
    policies: loadTextContent('../../generated/resolvers/Query.getUser.xml')
  }
  dependsOn: [
    graphqlSchema
  ]
}

output graphqlEndpoint string = '${apim.properties.gatewayUrl}/${graphqlApiPath}'

