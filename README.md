# GraphQL REST Automation

Automation starter for creating synthetic GraphQL endpoints from REST APIs using Azure API Management (APIM).

The workflow is:

1. Read an OpenAPI document.
2. Generate a synthetic GraphQL schema.
3. Generate APIM GraphQL resolver policies that call REST endpoints.
4. Deploy the APIM GraphQL API, schema, and resolvers with Terraform or Bicep.

## Repository layout

```text
.
├── generator/              # TypeScript OpenAPI -> GraphQL/APIM resolver generator
├── infra/
│   ├── terraform/          # Terraform deployment using azurerm + azapi
│   └── bicep/              # Bicep sample deployment
├── openapi/                # Example REST API OpenAPI specs
└── .github/workflows/      # Validation workflow
```

## Quick start

```powershell
npm install
npm run build
npm run generate -- .\openapi\users.yaml .\generated https://api.contoso.com
```

The generator emits:

```text
generated/
├── schema.graphql
├── resolvers.json
└── resolvers/
    ├── Query.getUser.xml
    ├── Query.listUsers.xml
    └── Mutation.createUser.xml
```

## Deploy with Terraform

```powershell
cd .\infra\terraform
terraform init
terraform validate
terraform plan -out main.tfplan `
  -var "resource_group_name=rg-graphql-rest-dev" `
  -var "location=eastus" `
  -var "apim_name=apim-graphql-rest-dev" `
  -var "publisher_email=platform@example.com"
terraform apply main.tfplan
```

## Deploy with Bicep

The Bicep sample wires the generated GraphQL schema and the example `Query.getUser` resolver.

```powershell
az deployment group what-if `
  --resource-group rg-graphql-rest-dev `
  --template-file .\infra\bicep\main.bicep `
  --parameters publisherEmail=platform@example.com

az deployment group create `
  --resource-group rg-graphql-rest-dev `
  --template-file .\infra\bicep\main.bicep `
  --parameters publisherEmail=platform@example.com
```

## Notes

- APIM GraphQL resolvers are field-level resources. Each generated resolver maps one GraphQL field to one REST operation.
- The Terraform sample uses `azapi_resource` for APIM schema and resolver child resources so it can track Azure API surface changes even when the native provider lags.
- Do not store REST API secrets in generated policies. Use APIM named values backed by Key Vault or managed identity where possible.

