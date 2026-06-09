output "apim_gateway_url" {
  description = "API Management gateway URL."
  value       = azurerm_api_management.main.gateway_url
}

output "graphql_endpoint" {
  description = "Synthetic GraphQL endpoint URL."
  value       = "${azurerm_api_management.main.gateway_url}/${var.graphql_api_path}"
}

