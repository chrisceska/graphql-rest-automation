# C# generator example

This example shows how to generate the same APIM synthetic GraphQL artifacts with a dependency-free .NET console app.

The app reads an OpenAPI JSON file and emits:

- `schema.graphql`
- `resolvers.json`
- APIM GraphQL resolver policies under `resolvers/*.xml`

## Run

```powershell
dotnet run --project .\examples\csharp\RestToGraphqlGenerator\RestToGraphqlGenerator.csproj -- `
  .\examples\csharp\openapi\users.json `
  .\examples\csharp\generated `
  https://api.contoso.com
```

The generated resolver policies use APIM GraphQL resolver-scoped `<http-data-source>` policies and map `context.GraphQL.Arguments` into REST path/query/body values.

