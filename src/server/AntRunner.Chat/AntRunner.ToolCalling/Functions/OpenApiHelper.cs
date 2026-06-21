using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using YamlDotNet.Serialization;

namespace AntRunner.ToolCalling.Functions
{
    /// <summary>
    /// Provides helper methods for validating, parsing and using OpenAPI specifications
    /// </summary>
    public class OpenApiHelper
    {
        /// <summary>
        /// Safely extracts an enum value as a string regardless of its JSON type.
        /// Handles string, number, boolean, and null JSON values.
        /// </summary>
        private static string? GetEnumValueAsString(JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Number => element.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Null => null,
                _ => element.GetRawText()
            };
        }

        /// <summary>
        /// Returns the element's string value only when ValueKind is String; otherwise null.
        /// Avoids InvalidOperationException when the property exists but is not a string, or is default(JsonElement).
        /// </summary>
        private static string? GetOptionalString(JsonElement element)
        {
            return element.ValueKind == JsonValueKind.String ? element.GetString() : null;
        }

        /// <summary>
        /// Returns a list of enum values only when the element is a JSON array; otherwise null.
        /// Avoids InvalidOperationException when "enum" exists but is not an array.
        /// </summary>
        private static List<string>? GetOptionalEnumArray(JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Array) return null;
            return element.EnumerateArray()
                .Select(GetEnumValueAsString)
                .Where(s => s != null)
                .Select(s => s!)
                .ToList();
        }

        /// <summary>
        /// Safely extracts the "type" field from a JSON schema element.
        /// Handles:
        /// - Missing type (returns defaultType)
        /// - String type (normal OpenAPI 3.0)
        /// - Array type (OpenAPI 3.1, e.g., ["string", "null"] - returns first non-null type)
        /// </summary>
        internal static string GetSchemaType(JsonElement schema, string defaultType = "string")
        {
            if (!schema.TryGetProperty("type", out var typeEl))
            {
                return defaultType;
            }

            return typeEl.ValueKind switch
            {
                JsonValueKind.String => typeEl.GetString() ?? defaultType,
                JsonValueKind.Array => typeEl.EnumerateArray()
                    .Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : null)
                    .FirstOrDefault(t => t != null && t != "null") ?? defaultType,
                _ => defaultType
            };
        }

        /// <summary>
        /// Validates and parses the OpenAPI specification string.
        /// </summary>
        /// <param name="specString">The OpenAPI specification string in JSON or YAML format.</param>
        /// <returns>A <see cref="ValidationResult"/> indicating the validation result.</returns>
        public static ValidationResult ValidateAndParseOpenApiSpec(string specString)
        {
            try
            {
                JsonDocument parsedSpec;
                try
                {
                    parsedSpec = JsonDocument.Parse(specString);
                }
                catch
                {
                    var yamlDeserializer = new Deserializer();
                    using var reader = new StringReader(specString);
                    var yamlObject = yamlDeserializer.Deserialize(reader);
                    var jsonString = JsonSerializer.Serialize(yamlObject);
                    parsedSpec = JsonDocument.Parse(jsonString);
                }

                var root = parsedSpec.RootElement;

                if (!root.TryGetProperty("servers", out var servers) || servers.GetArrayLength() == 0)
                {
                    return new ValidationResult { Status = false, Message = "Could not find a valid URL in `servers`", Spec = parsedSpec };
                }

                if (!root.TryGetProperty("paths", out var paths) || paths.GetRawText() == "{}")
                {
                    return new ValidationResult { Status = false, Message = "No paths found in the OpenAPI spec.", Spec = parsedSpec };
                }

                return new ValidationResult
                {
                    Status = true,
                    Message = "OpenAPI spec is valid.",
                    Spec = parsedSpec
                };
            }
            catch (Exception ex)
            {
                return new ValidationResult { Status = false, Message = "Error parsing OpenAPI spec: " + ex.Message };
            }
        }

        /// <summary>
        /// Extracts tool definitions from the OpenAPI specification.
        /// </summary>
        /// <param name="openApiSpec">The OpenAPI specification as a <see cref="JsonDocument"/>.</param>
        /// <returns>A list of <see cref="ToolDefinition"/> objects extracted from the OpenAPI spec.</returns>
        public static List<ToolDefinition> GetToolDefinitionsFromSchema(JsonDocument openApiSpec)
        {
            var toolDefinitions = new List<ToolDefinition>();
            var root = openApiSpec.RootElement;

            // Iterate over all paths in the OpenAPI spec
            foreach (var pathProperty in root.GetProperty("paths").EnumerateObject())
            {
                // Iterate over all HTTP methods in each path
                foreach (var methodProperty in pathProperty.Value.EnumerateObject())
                {
                    var operationObj = methodProperty.Value;

                    // Extract operation ID, or generate one if not present
                    var operationId = operationObj.TryGetProperty("operationId", out var opId)
                        ? opId.GetString()
                        : $"{methodProperty.Name}_{pathProperty.Name}";

                    // Extract description, preferring 'summary' over 'description'
                    var description = operationObj.TryGetProperty("summary", out var summary)
                        ? summary.GetString()
                        : operationObj.TryGetProperty("description", out var desc)
                        ? desc.GetString()
                        : "";

                    var properties = new Dictionary<string, PropertyDefinition>();
                    var required = new List<string>();

                    // Extract 'parameters' schema
                    if (operationObj.TryGetProperty("parameters", out var parameters))
                    {
                        foreach (var param in parameters.EnumerateArray())
                        {
                            var paramName = param.GetProperty("name").GetString();
                            param.TryGetProperty("description", out var paramDescriptionElement);

                            var schema = param.GetProperty("schema");

                            var propertyDefinition = new PropertyDefinition
                            {
                                Type = GetSchemaType(schema),
                                Description = schema.TryGetProperty("description", out var descriptionElement)
                                    ? GetOptionalString(descriptionElement)
                                    : GetOptionalString(paramDescriptionElement),
                                Default = schema.TryGetProperty("default", out var defaultElement)
                                    ? (defaultElement.ValueKind == JsonValueKind.String
                                        ? defaultElement.GetString()
                                        : defaultElement.ValueKind == JsonValueKind.Null
                                            ? null
                                            : defaultElement.GetRawText())
                                    : null,
                                Example = schema.TryGetProperty("example", out var exampleElement)
                                    ? (exampleElement.ValueKind == JsonValueKind.String
                                        ? exampleElement.GetString()
                                        : exampleElement.ValueKind == JsonValueKind.Null
                                            ? null
                                            : exampleElement.GetRawText())
                                    : null,
                                Enum = (schema.TryGetProperty("enum", out var enumElement)
                                    ? GetOptionalEnumArray(enumElement)
                                    : null)!,
                            };

                            properties[paramName ?? throw new InvalidOperationException("Parameter name not found")] = propertyDefinition;

                            if (param.TryGetProperty("required", out var requiredParamEl) && requiredParamEl.ValueKind == JsonValueKind.True)
                            {
                                required.Add(paramName);
                            }
                        }
                    }

                    string? contentType = null;
                    if (operationObj.TryGetProperty("requestBody", out var requestBody))
                    {
                        // TODO: DTW don't have a good example of an API with multiple media types, but the spec allows it
                        // This does not handle it... future bug fix required
                        var mediaType = requestBody.GetProperty("content").EnumerateObject().FirstOrDefault();
                        contentType = mediaType.Name;

                        var schema = mediaType.Value.GetProperty("schema");

                        var type = GetSchemaType(schema);
                        if (type == "object")
                        {
                            if (schema.TryGetProperty("properties", out var propertiesElement))
                            {
                                                                var hiddenParameters = new HashSet<string>();
                                foreach (var property in propertiesElement.EnumerateObject())
                                {
                                    var propertyDefinition = new PropertyDefinition
                                    {
                                        Type = GetSchemaType(property.Value),
                                        Description = property.Value.TryGetProperty("description", out var descriptionElement)
                                            ? GetOptionalString(descriptionElement)
                                            : null,
                                        Default = property.Value.TryGetProperty("default", out var defaultElement)
                                            ? (defaultElement.ValueKind == JsonValueKind.String
                                                ? defaultElement.GetString()
                                                : defaultElement.ValueKind == JsonValueKind.Null
                                                    ? null
                                                    : defaultElement.GetRawText())
                                            : null,
                                        Example = property.Value.TryGetProperty("example", out var exampleElement)
                                            ? (exampleElement.ValueKind == JsonValueKind.String
                                                ? exampleElement.GetString()
                                                : exampleElement.ValueKind == JsonValueKind.Null
                                                    ? null
                                                    : exampleElement.GetRawText())
                                            : null,
                                        Enum = (property.Value.TryGetProperty("enum", out var enumElement)
                                            ? GetOptionalEnumArray(enumElement)
                                            : null)!,
                                    };

                                    // If this requestBody property is an array, preserve its items schema
                                    if (string.Equals(propertyDefinition.Type, "array", StringComparison.OrdinalIgnoreCase)
                                        && property.Value.TryGetProperty("items", out var itemsElementForProp))
                                    {
                                        var itemsDef = new ParametersDefinition();

                                        // Set the items type if present; default remains "object"
                                        if (itemsElementForProp.TryGetProperty("type", out var itemTypeEl))
                                        {
                                            itemsDef.Type = itemTypeEl.GetString() ?? itemsDef.Type;
                                        }

                                        // Map nested item properties, if any
                                        if (itemsElementForProp.TryGetProperty("properties", out var itemPropsEl))
                                        {
                                            itemsDef.Properties = new Dictionary<string, PropertyDefinition>();
                                            foreach (var itemProp in itemPropsEl.EnumerateObject())
                                            {
                                                var itemPropDef = new PropertyDefinition
                                                {
                                                    Type = GetSchemaType(itemProp.Value),
                                                    Description = itemProp.Value.TryGetProperty("description", out var itemDescEl)
                                                        ? GetOptionalString(itemDescEl)
                                                        : null,
                                                    Default = itemProp.Value.TryGetProperty("default", out var itemDefaultEl)
                                                        ? (itemDefaultEl.ValueKind == JsonValueKind.String
                                                            ? itemDefaultEl.GetString()
                                                            : itemDefaultEl.ValueKind == JsonValueKind.Null
                                                                ? null
                                                                : itemDefaultEl.GetRawText())
                                                        : null,
                                                    Example = itemProp.Value.TryGetProperty("example", out var itemExampleEl)
                                                        ? (itemExampleEl.ValueKind == JsonValueKind.String
                                                            ? itemExampleEl.GetString()
                                                            : itemExampleEl.ValueKind == JsonValueKind.Null
                                                                ? null
                                                                : itemExampleEl.GetRawText())
                                                        : null,
                                                    Enum = (itemProp.Value.TryGetProperty("enum", out var itemEnumEl)
                                                        ? GetOptionalEnumArray(itemEnumEl)
                                                        : null)!,
                                                };

                                                itemsDef.Properties[itemProp.Name] = itemPropDef;
                                            }
                                        }

                                        // Map required fields for items, if any
                                        if (itemsElementForProp.TryGetProperty("required", out var itemReqEl))
                                        {
                                            itemsDef.Required = new List<string>();
                                            foreach (var req in itemReqEl.EnumerateArray())
                                            {
                                                var reqVal = req.GetString();
                                                if (reqVal != null) itemsDef.Required.Add(reqVal);
                                            }
                                        }

                                        propertyDefinition.Items = itemsDef;
                                    }

                                    if (ShouldAutoInjectRequestBodyProperty(propertyDefinition))
                                    {
                                        hiddenParameters.Add(property.Name);
                                        continue; // do not expose to LLM
                                    }

                                    properties[property.Name] = propertyDefinition;
                                }


                                if (schema.TryGetProperty("required", out var requiredElement))
                                {
                                                                        foreach (var requiredField in requiredElement.EnumerateArray())
                                    {
                                        var requiredFieldVal = requiredField.GetString();
                                        if (requiredFieldVal != null && !hiddenParameters.Contains(requiredFieldVal))
                                        {
                                            required.Add(requiredFieldVal);
                                        }
                                    }
                                }
                            }
                        }
                        else if (type == "array")
                        {
                            if (schema.TryGetProperty("items", out var itemsElement))
                            {
                                var body = properties["requestBody"] = new PropertyDefinition
                                {
                                    Type = type,
                                    Items = new ParametersDefinition(),
                                };
                                body.Items!.Properties = new Dictionary<string, PropertyDefinition>();

                                if (itemsElement.TryGetProperty("properties", out var bodyProperties))
                                {
                                    foreach (var property in bodyProperties.EnumerateObject())
                                    {
                                        var paramName = property.Name;

                                        var propertyDefinition = new PropertyDefinition
                                        {
                                            Type = GetSchemaType(property.Value),
                                            Description = property.Value.TryGetProperty("description", out var descriptionElement)
                                                ? GetOptionalString(descriptionElement)
                                                : null,
                                            Default = property.Value.TryGetProperty("default", out var defaultElement)
                                                ? (defaultElement.ValueKind == JsonValueKind.String
                                                    ? defaultElement.GetString()
                                                    : defaultElement.ValueKind == JsonValueKind.Null
                                                        ? null
                                                        : defaultElement.GetRawText())
                                                : null,
                                            Example = property.Value.TryGetProperty("example", out var exampleElement)
                                                ? (exampleElement.ValueKind == JsonValueKind.String
                                                    ? exampleElement.GetString()
                                                    : exampleElement.ValueKind == JsonValueKind.Null
                                                        ? null
                                                        : exampleElement.GetRawText())
                                                : null,
                                            Enum = (property.Value.TryGetProperty("enum", out var enumElement)
                                                ? GetOptionalEnumArray(enumElement)
                                                : null)!,
                                        };

                                        body.Items.Properties[paramName] = propertyDefinition;
                                    }
                                }

                                if (itemsElement.TryGetProperty("required", out var requiredElement))
                                {
                                    body.Items.Required = new List<string>();
                                    foreach (var requiredParam in requiredElement.EnumerateArray())
                                    {
                                        var requiredParamVal = requiredParam.GetString();
                                        if (requiredParamVal != null) body.Items.Required.Add(requiredParamVal);
                                    }
                                }
                            }
                        }
                        else
                        {
                            // Handle the case where requestBody is a simple type
                            var body = properties["requestBody"] = new PropertyDefinition
                            {
                                Type = type
                            };
                            if (schema.TryGetProperty("example", out var example))
                            {
                                body.Example = example.ValueKind == JsonValueKind.String
                                    ? example.GetString()
                                    : example.ValueKind == JsonValueKind.Null
                                        ? null
                                        : example.GetRawText();
                            }
                        }
                    }

                    var responseSchemas = new Dictionary<string, JsonElement>();
                    if (operationObj.TryGetProperty("responses", out var responses))
                    {
                        foreach (var response in responses.EnumerateObject())
                        {
                            if (response.Name == "200" && response.Value.TryGetProperty("content", out var content))
                            {
                                var mediaType = content.EnumerateObject().FirstOrDefault();
                                var schema = mediaType.Value.GetProperty("schema");

                                responseSchemas["200"] = schema;
                                break;
                            }
                        }
                    }

                    // Construct ParametersDefinition from properties and required fields
                    var parametersDefinition = new ParametersDefinition
                    {
                        Type = "object",
                        Properties = properties,
                        Required = required
                    };

                    // Construct FunctionDefinition from operationId, description, and parametersDefinition
                    var functionDefinition = new FunctionDefinition
                    {
                        Name = operationId ?? throw new InvalidOperationException("Operation ID not found"),
                        Description = description ?? string.Empty,
                        Parameters = parametersDefinition,
                        ContentType = contentType,
                        ResponseSchemas = responseSchemas
                    };

                    // Wrap FunctionDefinition in a ToolDefinition
                    var toolDefinition = ToolDefinition.DefineFunction(new AssistantsApiToolFunctionOneOfType
                    {
                        AsObject = functionDefinition
                    });
                    toolDefinitions.Add(toolDefinition);
                }
            }

            return toolDefinitions;
        }

        /// <summary>
        /// Retrieves tool definitions from a JSON string representing an OpenAPI specification.
        /// </summary>
        /// <param name="json">A JSON string representing an OpenAPI specification.</param>
        /// <returns>A list of <see cref="ToolDefinition"/> objects parsed from the JSON string.</returns>
        public static List<ToolDefinition> GetToolDefinitionsFromJson(string json)
        {
            var validationResult = ValidateAndParseOpenApiSpec(json);
            var spec = validationResult.Spec;

            if (!validationResult.Status || spec == null)
            {
                ToolCallingDiagnostics
                    .CreateLogger<OpenApiHelper>()
                    .LogWarning("Json is not a valid openapi spec. Ignoring payload.");
                return new List<ToolDefinition>();
            }

            return GetToolDefinitionsFromSchema(spec);
        }

        /// <summary>
        /// Returns request-body property names that are auto-injected at runtime (default or single enum value)
        /// and therefore hidden from the LLM-facing tool schema.
        /// </summary>
        public static List<string> GetAutoInjectedRequestBodyParameterNames(JsonElement operationObj)
        {
            var hidden = new List<string>();
            if (!operationObj.TryGetProperty("requestBody", out var requestBody))
            {
                return hidden;
            }

            if (!requestBody.TryGetProperty("content", out var content))
            {
                return hidden;
            }

            var mediaType = content.EnumerateObject().FirstOrDefault();
            if (mediaType.Value.ValueKind == JsonValueKind.Undefined
                || !mediaType.Value.TryGetProperty("schema", out var schema))
            {
                return hidden;
            }

            if (!string.Equals(GetSchemaType(schema), "object", StringComparison.OrdinalIgnoreCase)
                || !schema.TryGetProperty("properties", out var propertiesElement))
            {
                return hidden;
            }

            foreach (var property in propertiesElement.EnumerateObject())
            {
                var propertyDefinition = new PropertyDefinition
                {
                    Type = GetSchemaType(property.Value),
                    Default = property.Value.TryGetProperty("default", out var defaultElement)
                        ? (defaultElement.ValueKind == JsonValueKind.String
                            ? defaultElement.GetString()
                            : defaultElement.ValueKind == JsonValueKind.Null
                                ? null
                                : defaultElement.GetRawText())
                        : null,
                    Enum = (property.Value.TryGetProperty("enum", out var enumElement)
                        ? GetOptionalEnumArray(enumElement)
                        : null)!,
                };

                if (ShouldAutoInjectRequestBodyProperty(propertyDefinition))
                {
                    hidden.Add(property.Name);
                }
            }

            return hidden;
        }

        private static bool ShouldAutoInjectRequestBodyProperty(PropertyDefinition propertyDefinition) =>
            propertyDefinition.Default != null
            || (propertyDefinition.Enum != null && propertyDefinition.Enum.Count == 1);
    }
}
