variable "resource_group_name" {
  type        = string
  description = "Name of the resource group for API Management."
}

variable "location" {
  type        = string
  description = "Azure region for API Management."
  default     = "eastus"
}

variable "apim_name" {
  type        = string
  description = "Globally unique API Management service name."
}

variable "publisher_name" {
  type        = string
  description = "API Management publisher name."
  default     = "Platform Engineering"
}

variable "publisher_email" {
  type        = string
  description = "API Management publisher email."
}

variable "graphql_api_name" {
  type        = string
  description = "Synthetic GraphQL API name in API Management."
  default     = "synthetic-graphql"
}

variable "graphql_api_path" {
  type        = string
  description = "Public path for the GraphQL API."
  default     = "graphql"
}

