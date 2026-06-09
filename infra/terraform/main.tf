resource "azurerm_resource_group" "main" {
  name     = var.resource_group_name
  location = var.location
}

resource "azurerm_api_management" "main" {
  name                = var.apim_name
  location            = azurerm_resource_group.main.location
  resource_group_name = azurerm_resource_group.main.name
  publisher_name      = var.publisher_name
  publisher_email     = var.publisher_email
  sku_name            = "Developer_1"

  identity {
    type = "SystemAssigned"
  }
}

resource "azurerm_api_management_api" "graphql" {
  name                = var.graphql_api_name
  resource_group_name = azurerm_resource_group.main.name
  api_management_name = azurerm_api_management.main.name

  api_type              = "graphql"
  display_name          = "Synthetic GraphQL"
  path                  = var.graphql_api_path
  protocols             = ["https"]
  revision              = "1"
  service_url           = "https://example.invalid"
  subscription_required = false
}

locals {
  generated_root = "${path.module}/../../generated"
  resolvers      = jsondecode(file("${local.generated_root}/resolvers.json"))
}

resource "azapi_resource" "graphql_schema" {
  type      = "Microsoft.ApiManagement/service/apis/schemas@2023-09-01-preview"
  name      = "graphql"
  parent_id = azurerm_api_management_api.graphql.id

  body = {
    properties = {
      contentType = "application/vnd.ms-azure-apim.graphql.schema"
      document = {
        value = file("${local.generated_root}/schema.graphql")
      }
    }
  }
}

resource "azapi_resource" "resolver" {
  for_each = {
    for resolver in local.resolvers : resolver.name => resolver
  }

  type      = "Microsoft.ApiManagement/service/apis/resolvers@2023-09-01-preview"
  name      = each.value.name
  parent_id = azurerm_api_management_api.graphql.id

  body = {
    properties = {
      displayName = "${each.value.type}.${each.value.field}"
      description = each.value.description
      path        = "${each.value.type}/${each.value.field}"
      policies    = file("${path.module}/${each.value.policy_file}")
    }
  }

  depends_on = [azapi_resource.graphql_schema]
}
