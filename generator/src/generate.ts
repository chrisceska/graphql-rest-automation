import fs from "node:fs";
import path from "node:path";
import YAML from "yaml";

type HttpMethod = "get" | "post" | "put" | "patch" | "delete";

type OpenApiDocument = {
  paths: Record<string, Partial<Record<HttpMethod, OpenApiOperation>>>;
  components?: {
    schemas?: Record<string, JsonSchema>;
  };
};

type OpenApiOperation = {
  operationId?: string;
  parameters?: OpenApiParameter[];
  requestBody?: {
    content?: Record<string, { schema?: JsonSchema }>;
  };
  responses?: Record<string, {
    content?: Record<string, { schema?: JsonSchema }>;
  }>;
};

type OpenApiParameter = {
  name: string;
  in: "path" | "query" | "header";
  required?: boolean;
  schema?: JsonSchema;
};

type JsonSchema = {
  type?: string;
  format?: string;
  properties?: Record<string, JsonSchema>;
  required?: string[];
  items?: JsonSchema;
  $ref?: string;
};

type ResolverManifestItem = {
  name: string;
  type: "Query" | "Mutation";
  field: string;
  policy_file: string;
  description: string;
};

const [specPath, outputDirectory = "generated", backendBaseUrl = "https://api.contoso.com"] = process.argv.slice(2);

if (!specPath) {
  throw new Error("Usage: npm run generate -- <openapi.yaml> [outputDirectory] [backendBaseUrl]");
}

const spec = readOpenApi(specPath);
const resolversDirectory = path.join(outputDirectory, "resolvers");

fs.mkdirSync(resolversDirectory, { recursive: true });

const queryFields: string[] = [];
const mutationFields: string[] = [];
const generatedTypes = new Map<string, string>();
const resolverManifest: ResolverManifestItem[] = [];

for (const [route, operations] of Object.entries(spec.paths)) {
  for (const [method, operation] of Object.entries(operations) as [HttpMethod, OpenApiOperation][]) {
    if (!operation?.operationId) {
      continue;
    }

    const rootType = method === "get" ? "Query" : "Mutation";
    const field = sanitizeGraphqlName(operation.operationId);
    const args = buildArguments(route, operation, spec);
    const responseType = inferResponseType(operation, spec, generatedTypes);
    const fieldDeclaration = `  ${field}${args}: ${responseType}`;

    if (rootType === "Query") {
      queryFields.push(fieldDeclaration);
    } else {
      mutationFields.push(fieldDeclaration);
    }

    const policyFileName = `${rootType}.${field}.xml`;
    const policyPath = path.join(resolversDirectory, policyFileName);

    fs.writeFileSync(
      policyPath,
      buildResolverPolicy({
        backendBaseUrl,
        method,
        operation,
        route
      })
    );

    resolverManifest.push({
      name: `${rootType}-${field}`,
      type: rootType,
      field,
      policy_file: `../../generated/resolvers/${policyFileName}`,
      description: `Resolve ${rootType}.${field} from REST ${method.toUpperCase()} ${route}`
    });
  }
}

const schema = [
  "scalar JSON",
  "",
  generatedTypes.size ? [...generatedTypes.values()].join("\n\n") : "",
  "type Query {",
  queryFields.length ? queryFields.join("\n") : "  _empty: String",
  "}",
  "",
  "type Mutation {",
  mutationFields.length ? mutationFields.join("\n") : "  _empty: String",
  "}"
].filter(Boolean).join("\n");

fs.writeFileSync(path.join(outputDirectory, "schema.graphql"), `${schema}\n`);
fs.writeFileSync(path.join(outputDirectory, "resolvers.json"), `${JSON.stringify(resolverManifest, null, 2)}\n`);

function readOpenApi(filePath: string): OpenApiDocument {
  const raw = fs.readFileSync(filePath, "utf8");
  const parsed = filePath.endsWith(".json") ? JSON.parse(raw) : YAML.parse(raw);
  assertOpenApiDocument(parsed);
  return parsed;
}

function assertOpenApiDocument(value: unknown): asserts value is OpenApiDocument {
  if (!value || typeof value !== "object" || !("paths" in value)) {
    throw new Error("OpenAPI document must contain a paths object.");
  }
}

function buildArguments(route: string, operation: OpenApiOperation, spec: OpenApiDocument): string {
  const params = operation.parameters ?? [];
  const args = params.map((parameter) => {
    const required = parameter.required || route.includes(`{${parameter.name}}`) ? "!" : "";
    return `${sanitizeGraphqlName(parameter.name)}: ${schemaToGraphqlType(parameter.schema, spec)}${required}`;
  });

  const inputSchema = operation.requestBody?.content?.["application/json"]?.schema;
  if (inputSchema) {
    args.push(`input: ${schemaToGraphqlType(inputSchema, spec, true)}!`);
  }

  return args.length ? `(${args.join(", ")})` : "";
}

function inferResponseType(
  operation: OpenApiOperation,
  spec: OpenApiDocument,
  generatedTypes: Map<string, string>
): string {
  const response = operation.responses?.["200"] ?? operation.responses?.["201"] ?? operation.responses?.default;
  const schema = response?.content?.["application/json"]?.schema;

  if (!schema) {
    return "JSON";
  }

  collectGraphqlTypes(schema, spec, generatedTypes);
  return schemaToGraphqlType(schema, spec);
}

function collectGraphqlTypes(
  schema: JsonSchema,
  spec: OpenApiDocument,
  generatedTypes: Map<string, string>
): void {
  if (schema.type === "array" && schema.items) {
    collectGraphqlTypes(schema.items, spec, generatedTypes);
    return;
  }

  const resolved = resolveRef(schema, spec);

  if (schema.$ref) {
    const typeName = refName(schema.$ref);
    if (!generatedTypes.has(typeName) && resolved.properties) {
      generatedTypes.set(typeName, objectType(typeName, resolved, spec));
    }
  }

  for (const property of Object.values(resolved.properties ?? {})) {
    const propertySchema = property.items ?? property;
    if (propertySchema.$ref) {
      collectGraphqlTypes(propertySchema, spec, generatedTypes);
    }
  }
}

function objectType(typeName: string, schema: JsonSchema, spec: OpenApiDocument): string {
  const required = new Set(schema.required ?? []);
  const fields = Object.entries(schema.properties ?? {}).map(([name, property]) => {
    const suffix = required.has(name) ? "!" : "";
    return `  ${sanitizeGraphqlName(name)}: ${schemaToGraphqlType(property, spec)}${suffix}`;
  });

  return [`type ${sanitizeGraphqlName(typeName)} {`, ...fields, "}"].join("\n");
}

function schemaToGraphqlType(schema: JsonSchema | undefined, spec: OpenApiDocument, input = false): string {
  if (!schema) {
    return "JSON";
  }

  if (schema.$ref) {
    const name = sanitizeGraphqlName(refName(schema.$ref));
    return input ? "JSON" : name;
  }

  if (schema.type === "array") {
    return `[${schemaToGraphqlType(schema.items, spec, input)}!]`;
  }

  switch (schema.type) {
    case "integer":
      return "Int";
    case "number":
      return "Float";
    case "boolean":
      return "Boolean";
    case "string":
      return schema.format === "uuid" ? "ID" : "String";
    case "object":
      return "JSON";
    default:
      return "JSON";
  }
}

function resolveRef(schema: JsonSchema, spec: OpenApiDocument): JsonSchema {
  if (!schema.$ref) {
    return schema;
  }

  const name = refName(schema.$ref);
  const resolved = spec.components?.schemas?.[name];

  if (!resolved) {
    throw new Error(`Unable to resolve schema reference ${schema.$ref}`);
  }

  return resolved;
}

function refName(ref: string): string {
  return ref.split("/").at(-1) ?? ref;
}

function sanitizeGraphqlName(value: string): string {
  const sanitized = value.replace(/[^_0-9A-Za-z]/g, "_");
  return sanitized.match(/^[A-Za-z_]/) ? sanitized : `_${sanitized}`;
}

function buildResolverPolicy(input: {
  backendBaseUrl: string;
  method: HttpMethod;
  operation: OpenApiOperation;
  route: string;
}): string {
  const url = buildResolverUrl(input.backendBaseUrl, input.route, input.operation);
  const body = input.method === "get" || input.method === "delete"
    ? ""
    : "\n    <set-body>@(context.GraphQL.Arguments[\"input\"].ToString())</set-body>";

  return [
    "<http-data-source>",
    "  <http-request>",
    `    <set-method>${input.method.toUpperCase()}</set-method>`,
    `    <set-url>${escapeXml(url)}</set-url>`,
    "    <set-header name=\"Accept\" exists-action=\"override\">",
    "      <value>application/json</value>",
    "    </set-header>",
    "    <set-header name=\"Content-Type\" exists-action=\"override\">",
    "      <value>application/json</value>",
    `    </set-header>${body}`,
    "  </http-request>",
    "  <http-response>",
    "    <set-body>@(context.Response.Body.As&lt;string&gt;())</set-body>",
    "  </http-response>",
    "</http-data-source>",
    ""
  ].join("\n");
}

function buildResolverUrl(backendBaseUrl: string, route: string, operation: OpenApiOperation): string {
  const routeExpression = route.replace(/\{([^}]+)\}/g, (_, name: string) => {
    return `{context.GraphQL.Arguments[\"${name}\"]}`;
  });

  const queryParameters = (operation.parameters ?? [])
    .filter((parameter) => parameter.in === "query")
    .map((parameter) => `${parameter.name}={context.GraphQL.Arguments[\"${parameter.name}\"]}`);

  const query = queryParameters.length ? `?${queryParameters.join("&")}` : "";
  return `@($"${backendBaseUrl}${routeExpression}${query}")`;
}

function escapeXml(value: string): string {
  return value.replace(/&/g, "&amp;");
}
