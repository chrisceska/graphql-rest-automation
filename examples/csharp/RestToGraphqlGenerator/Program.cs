using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

if (args.Length < 1)
{
    Console.Error.WriteLine("Usage: dotnet run -- <openapi.json> [outputDirectory] [backendBaseUrl]");
    return 1;
}

var specPath = args[0];
var outputDirectory = args.Length > 1 ? args[1] : "generated-csharp";
var backendBaseUrl = args.Length > 2 ? args[2].TrimEnd('/') : "https://api.contoso.com";

using var specStream = File.OpenRead(specPath);
using var document = await JsonDocument.ParseAsync(specStream);

if (!document.RootElement.TryGetProperty("paths", out var paths) || paths.ValueKind != JsonValueKind.Object)
{
    Console.Error.WriteLine("OpenAPI document must contain a paths object.");
    return 1;
}

Directory.CreateDirectory(Path.Combine(outputDirectory, "resolvers"));

var queryFields = new List<string>();
var mutationFields = new List<string>();
var generatedTypes = new Dictionary<string, string>();
var resolverManifest = new List<ResolverManifestItem>();

foreach (var pathProperty in paths.EnumerateObject())
{
    var route = pathProperty.Name;

    foreach (var methodProperty in pathProperty.Value.EnumerateObject())
    {
        var method = methodProperty.Name.ToLowerInvariant();
        if (!IsHttpMethod(method))
        {
            continue;
        }

        var operation = methodProperty.Value;
        if (!operation.TryGetProperty("operationId", out var operationIdElement))
        {
            continue;
        }

        var rootType = method == "get" ? "Query" : "Mutation";
        var field = SanitizeGraphqlName(operationIdElement.GetString() ?? "operation");
        var arguments = BuildArguments(route, operation, document.RootElement);
        var responseType = InferResponseType(operation, document.RootElement, generatedTypes);
        var fieldDeclaration = $"  {field}{arguments}: {responseType}";

        if (rootType == "Query")
        {
            queryFields.Add(fieldDeclaration);
        }
        else
        {
            mutationFields.Add(fieldDeclaration);
        }

        var policyFileName = $"{rootType}.{field}.xml";
        var policyPath = Path.Combine(outputDirectory, "resolvers", policyFileName);
        await File.WriteAllTextAsync(policyPath, BuildResolverPolicy(backendBaseUrl, method, route, operation));

        resolverManifest.Add(new ResolverManifestItem(
            Name: $"{rootType}-{field}",
            Type: rootType,
            Field: field,
            PolicyFile: $"../../{outputDirectory.Replace('\\', '/')}/resolvers/{policyFileName}",
            Description: $"Resolve {rootType}.{field} from REST {method.ToUpperInvariant()} {route}"
        ));
    }
}

var schemaParts = new List<string> { "scalar JSON", "" };
if (generatedTypes.Count > 0)
{
    schemaParts.Add(string.Join($"{Environment.NewLine}{Environment.NewLine}", generatedTypes.Values));
    schemaParts.Add("");
}

schemaParts.Add("type Query {");
schemaParts.Add(queryFields.Count > 0 ? string.Join(Environment.NewLine, queryFields) : "  _empty: String");
schemaParts.Add("}");
schemaParts.Add("");
schemaParts.Add("type Mutation {");
schemaParts.Add(mutationFields.Count > 0 ? string.Join(Environment.NewLine, mutationFields) : "  _empty: String");
schemaParts.Add("}");

await File.WriteAllTextAsync(Path.Combine(outputDirectory, "schema.graphql"), string.Join(Environment.NewLine, schemaParts) + Environment.NewLine);
await File.WriteAllTextAsync(
    Path.Combine(outputDirectory, "resolvers.json"),
    JsonSerializer.Serialize(resolverManifest, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);

Console.WriteLine($"Generated GraphQL schema and {resolverManifest.Count} resolver policies in {outputDirectory}");
return 0;

static bool IsHttpMethod(string method) =>
    method is "get" or "post" or "put" or "patch" or "delete";

static string BuildArguments(string route, JsonElement operation, JsonElement root)
{
    var args = new List<string>();

    if (operation.TryGetProperty("parameters", out var parameters) && parameters.ValueKind == JsonValueKind.Array)
    {
        foreach (var parameter in parameters.EnumerateArray())
        {
            var name = parameter.GetProperty("name").GetString() ?? "arg";
            var required = route.Contains($"{{{name}}}", StringComparison.Ordinal) ||
                           (parameter.TryGetProperty("required", out var requiredElement) && requiredElement.GetBoolean());
            var schema = parameter.TryGetProperty("schema", out var parameterSchema) ? parameterSchema : default;
            args.Add($"{SanitizeGraphqlName(name)}: {SchemaToGraphqlType(schema, root)}{(required ? "!" : "")}");
        }
    }

    if (TryGetJsonContentSchema(operation, "requestBody", out _))
    {
        args.Add("input: JSON!");
    }

    return args.Count > 0 ? $"({string.Join(", ", args)})" : string.Empty;
}

static string InferResponseType(JsonElement operation, JsonElement root, Dictionary<string, string> generatedTypes)
{
    if (!operation.TryGetProperty("responses", out var responses))
    {
        return "JSON";
    }

    JsonElement response;
    if (!responses.TryGetProperty("200", out response) &&
        !responses.TryGetProperty("201", out response) &&
        !responses.TryGetProperty("default", out response))
    {
        return "JSON";
    }

    if (!TryGetApplicationJsonSchema(response, out var schema))
    {
        return "JSON";
    }

    CollectGraphqlTypes(schema, root, generatedTypes);
    return SchemaToGraphqlType(schema, root);
}

static void CollectGraphqlTypes(JsonElement schema, JsonElement root, Dictionary<string, string> generatedTypes)
{
    if (schema.ValueKind == JsonValueKind.Undefined)
    {
        return;
    }

    if (TryGetString(schema, "type", out var type) && type == "array" &&
        schema.TryGetProperty("items", out var items))
    {
        CollectGraphqlTypes(items, root, generatedTypes);
        return;
    }

    if (TryGetString(schema, "$ref", out var reference))
    {
        var typeName = RefName(reference);
        if (!generatedTypes.ContainsKey(typeName) && TryResolveRef(root, reference, out var resolved))
        {
            generatedTypes[typeName] = ObjectType(typeName, resolved, root);
            CollectNestedTypes(resolved, root, generatedTypes);
        }
    }
}

static void CollectNestedTypes(JsonElement schema, JsonElement root, Dictionary<string, string> generatedTypes)
{
    if (!schema.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
    {
        return;
    }

    foreach (var property in properties.EnumerateObject())
    {
        var nestedSchema = property.Value;
        if (TryGetString(nestedSchema, "type", out var type) && type == "array" &&
            nestedSchema.TryGetProperty("items", out var items))
        {
            nestedSchema = items;
        }

        CollectGraphqlTypes(nestedSchema, root, generatedTypes);
    }
}

static string ObjectType(string typeName, JsonElement schema, JsonElement root)
{
    var required = new HashSet<string>(StringComparer.Ordinal);
    if (schema.TryGetProperty("required", out var requiredElement) && requiredElement.ValueKind == JsonValueKind.Array)
    {
        foreach (var item in requiredElement.EnumerateArray())
        {
            if (item.GetString() is { } requiredName)
            {
                required.Add(requiredName);
            }
        }
    }

    var fields = new List<string> { $"type {SanitizeGraphqlName(typeName)} {{" };
    if (schema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
    {
        foreach (var property in properties.EnumerateObject())
        {
            var suffix = required.Contains(property.Name) ? "!" : string.Empty;
            fields.Add($"  {SanitizeGraphqlName(property.Name)}: {SchemaToGraphqlType(property.Value, root)}{suffix}");
        }
    }

    fields.Add("}");
    return string.Join(Environment.NewLine, fields);
}

static string SchemaToGraphqlType(JsonElement schema, JsonElement root)
{
    if (schema.ValueKind == JsonValueKind.Undefined)
    {
        return "JSON";
    }

    if (TryGetString(schema, "$ref", out var reference))
    {
        return SanitizeGraphqlName(RefName(reference));
    }

    if (TryGetString(schema, "type", out var type))
    {
        if (type == "array" && schema.TryGetProperty("items", out var items))
        {
            return $"[{SchemaToGraphqlType(items, root)}!]";
        }

        return type switch
        {
            "integer" => "Int",
            "number" => "Float",
            "boolean" => "Boolean",
            "string" => TryGetString(schema, "format", out var format) && format == "uuid" ? "ID" : "String",
            _ => "JSON"
        };
    }

    return "JSON";
}

static string BuildResolverPolicy(string backendBaseUrl, string method, string route, JsonElement operation)
{
    var url = BuildResolverUrl(backendBaseUrl, route, operation);
    var body = method is "get" or "delete"
        ? string.Empty
        : $"{Environment.NewLine}    <set-body>@(context.GraphQL.Arguments[\"input\"].ToString())</set-body>";

    return string.Join(Environment.NewLine, new[]
    {
        "<http-data-source>",
        "  <http-request>",
        $"    <set-method>{method.ToUpperInvariant()}</set-method>",
        $"    <set-url>{EscapeXml(url)}</set-url>",
        "    <set-header name=\"Accept\" exists-action=\"override\">",
        "      <value>application/json</value>",
        "    </set-header>",
        "    <set-header name=\"Content-Type\" exists-action=\"override\">",
        "      <value>application/json</value>",
        $"    </set-header>{body}",
        "  </http-request>",
        "  <http-response>",
        "    <set-body>@(context.Response.Body.As&lt;string&gt;())</set-body>",
        "  </http-response>",
        "</http-data-source>",
        string.Empty
    });
}

static string BuildResolverUrl(string backendBaseUrl, string route, JsonElement operation)
{
    var routeExpression = Regex.Replace(route, "\\{([^}]+)\\}", match =>
        $"{{context.GraphQL.Arguments[\\\"{match.Groups[1].Value}\\\"]}}");

    var queryParameters = new List<string>();
    if (operation.TryGetProperty("parameters", out var parameters) && parameters.ValueKind == JsonValueKind.Array)
    {
        foreach (var parameter in parameters.EnumerateArray())
        {
            if (parameter.TryGetProperty("in", out var location) && location.GetString() == "query")
            {
                var name = parameter.GetProperty("name").GetString() ?? "arg";
                queryParameters.Add($"{name}={{context.GraphQL.Arguments[\\\"{name}\\\"]}}");
            }
        }
    }

    var query = queryParameters.Count > 0 ? $"?{string.Join("&", queryParameters)}" : string.Empty;
    return $"@($\"{backendBaseUrl}{routeExpression}{query}\")";
}

static bool TryGetJsonContentSchema(JsonElement operation, string propertyName, out JsonElement schema)
{
    schema = default;
    return operation.TryGetProperty(propertyName, out var property) && TryGetApplicationJsonSchema(property, out schema);
}

static bool TryGetApplicationJsonSchema(JsonElement element, out JsonElement schema)
{
    schema = default;
    if (!element.TryGetProperty("content", out var content) ||
        !content.TryGetProperty("application/json", out var jsonContent) ||
        !jsonContent.TryGetProperty("schema", out schema))
    {
        return false;
    }

    return true;
}

static bool TryResolveRef(JsonElement root, string reference, out JsonElement resolved)
{
    resolved = default;
    var name = RefName(reference);
    return root.TryGetProperty("components", out var components) &&
           components.TryGetProperty("schemas", out var schemas) &&
           schemas.TryGetProperty(name, out resolved);
}

static string RefName(string reference) => reference.Split('/')[^1];

static bool TryGetString(JsonElement element, string propertyName, out string value)
{
    value = string.Empty;
    if (element.ValueKind == JsonValueKind.Undefined ||
        !element.TryGetProperty(propertyName, out var property) ||
        property.ValueKind != JsonValueKind.String)
    {
        return false;
    }

    value = property.GetString() ?? string.Empty;
    return true;
}

static string SanitizeGraphqlName(string value)
{
    var sanitized = Regex.Replace(value, "[^_0-9A-Za-z]", "_");
    return Regex.IsMatch(sanitized, "^[A-Za-z_]") ? sanitized : $"_{sanitized}";
}

static string EscapeXml(string value) => value.Replace("&", "&amp;", StringComparison.Ordinal);

internal sealed record ResolverManifestItem(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("field")] string Field,
    [property: JsonPropertyName("policy_file")] string PolicyFile,
    [property: JsonPropertyName("description")] string Description);
