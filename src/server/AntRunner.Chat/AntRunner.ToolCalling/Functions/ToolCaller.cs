using AntRunner.ToolCalling.AssistantDefinitions.Storage;
using AntRunner.ToolCalling.HttpClient;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;

namespace AntRunner.ToolCalling.Functions
{
    /// <summary>
    /// WebApi indicates an external API call, while LocalFunction indicates a local function call based on a valid static method in a loaded assembly.
    /// </summary>
    public enum ActionType
    {
        /// <summary>
        /// Represents a web API action.
        /// </summary>
        WebApi,

        /// <summary>
        /// Represents a local function action.
        /// </summary>
        LocalFunction,

        /// <summary>
        /// Represents a client-handled action (executed by client via external tool bridge).
        /// </summary>
        ClientHandled,

        /// <summary>
        /// Represents a sandbox-handled action (Python script executed in sandboxed Docker environment).
        /// The initialization script filename is specified in the OpenAPI server URL (e.g., sandbox://init.py).
        /// </summary>
        SandboxHandled
    }

    /// <summary>
    /// Represents an action request to make HTTP calls.
    /// </summary>
    public class ToolCaller(
        string baseUrl,
        string path,
        string method,
        string operation,
        JsonElement methodSchema,
        string contentType,
        Dictionary<string, string> authHeaders,
        Dictionary<string, string> authQueryParams,
        bool oAuth = false,
        bool authRequiredButMissing = false)
    {
        public static Func<string, string?>? ConfigurationVariableResolver { get; set; }

        /// <summary>
        /// Generates request builders based on the OpenAPI specification.
        /// </summary>
        /// <param name="openApiSpec">The OpenAPI specification as a <see cref="JsonDocument"/>.</param>
        /// <returns>A dictionary of <see cref="ToolCaller"/> objects with operation IDs as keys.</returns>
        public static Dictionary<string, ToolCaller> GetToolCallers(JsonDocument openApiSpec)
        {
            var openApiSpecRootJsonElement = openApiSpec.RootElement;
            var baseUrl = openApiSpecRootJsonElement.GetProperty("servers")[0].GetProperty("url").GetString()
                          ?? throw new InvalidOperationException("Base URL not found");

            return GetToolCallers(openApiSpec, baseUrl, [], [], false, false);
        }


        /// <summary>
        /// Generates request builders based on the OpenAPI specification.
        /// </summary>
        /// <param name="openApiSpec">The OpenAPI specification as a <see cref="JsonDocument"/>.</param>
        /// <param name="domainAuth"></param>
        /// <returns>A dictionary of <see cref="ToolCaller"/> objects with operation IDs as keys.</returns>
        public static Dictionary<string, ToolCaller> GetToolCallers(JsonDocument openApiSpec, DomainAuth? domainAuth)
        {
            var openApiSpecRootJsonElement = openApiSpec.RootElement;
            var baseUrl = openApiSpecRootJsonElement.GetProperty("servers")[0].GetProperty("url").GetString()
                          ?? throw new InvalidOperationException("Base URL not found");

            var authority = (new Uri(baseUrl)).Authority;
            var oAuth = false;
            var authRequiredButMissing = false;

            Dictionary<string, string> authHeaders = [];
            Dictionary<string, string> authQueryParams = [];

            if (domainAuth != null && domainAuth.HostAuthorizationConfigurations.TryGetValue(authority, out var actionAuthConfig))
            {
                if (actionAuthConfig.AuthType == AuthType.service_http && actionAuthConfig.HeaderKey != null)
                {
                    // Prefer literal value when present and not masked
                    if (!string.IsNullOrWhiteSpace(actionAuthConfig.HeaderValueLiteral) && actionAuthConfig.HeaderValueLiteral != "••••••••")
                    {
                        authHeaders[actionAuthConfig.HeaderKey] = actionAuthConfig.HeaderValueLiteral!;
                    }
                    else if (!string.IsNullOrWhiteSpace(actionAuthConfig.HeaderValueEnvironmentVariable))
                    {
                        var envValue = ConfigurationVariableResolver?.Invoke(actionAuthConfig.HeaderValueEnvironmentVariable)
                            ?? Environment.GetEnvironmentVariable(actionAuthConfig.HeaderValueEnvironmentVariable);
                        if (!string.IsNullOrEmpty(envValue))
                        {
                            authHeaders[actionAuthConfig.HeaderKey] = envValue;
                        }
                        else
                        {
                            // Use HeaderValueEnvironmentVariable itself as the value (when it contains the actual secret, not an env var name)
                            authHeaders[actionAuthConfig.HeaderKey] = actionAuthConfig.HeaderValueEnvironmentVariable;
                        }
                    }
                    else
                    {
                        authRequiredButMissing = true;
                    }
                }
                else if (actionAuthConfig.AuthType == AuthType.service_query && actionAuthConfig.HeaderKey != null)
                {
                    // Reuse header fields to carry key/value semantics for query auth
                    if (!string.IsNullOrWhiteSpace(actionAuthConfig.HeaderValueLiteral) && actionAuthConfig.HeaderValueLiteral != "••••••••")
                    {
                        authQueryParams[actionAuthConfig.HeaderKey] = actionAuthConfig.HeaderValueLiteral!;
                    }
                    else if (!string.IsNullOrWhiteSpace(actionAuthConfig.HeaderValueEnvironmentVariable))
                    {
                        var envValue = ConfigurationVariableResolver?.Invoke(actionAuthConfig.HeaderValueEnvironmentVariable)
                            ?? Environment.GetEnvironmentVariable(actionAuthConfig.HeaderValueEnvironmentVariable);
                        if (!string.IsNullOrEmpty(envValue))
                        {
                            authQueryParams[actionAuthConfig.HeaderKey] = envValue;
                        }
                        else
                        {
                            // Use HeaderValueEnvironmentVariable itself as the value (when it contains the actual secret, not an env var name)
                            authQueryParams[actionAuthConfig.HeaderKey] = actionAuthConfig.HeaderValueEnvironmentVariable;
                        }
                    }
                    else
                    {
                        authRequiredButMissing = true;
                    }
                }
                else if (actionAuthConfig.AuthType == AuthType.azure_oauth || actionAuthConfig.AuthType == AuthType.oauth)
                {
                    oAuth = true;
                }
            }

            return GetToolCallers(openApiSpec, baseUrl, authHeaders, authQueryParams, oAuth, authRequiredButMissing);
        }

        /// <summary>
        /// Generates request builders based on the OpenAPI specification.
        /// </summary>
        /// <param name="openApiSpec">The OpenAPI specification as a <see cref="JsonDocument"/>.</param>
        /// <param name="assistantName">The assistant</param>
        /// <returns>A dictionary of <see cref="ToolCaller"/> objects with operation IDs as keys.</returns>
        public static async Task<Dictionary<string, ToolCaller>> GetToolCallers(JsonDocument openApiSpec, string? assistantName = null)
        {
            var openApiSpecRootJsonElement = openApiSpec.RootElement;
            var baseUrl = openApiSpecRootJsonElement.GetProperty("servers")[0].GetProperty("url").GetString()
                          ?? throw new InvalidOperationException("Base URL not found");

            var authority = (new Uri(baseUrl)).Authority;

            if (assistantName != null)
            {
                var authJson = await AssistantDefinitionFiles.GetActionAuth(assistantName);
                if (authJson != null)
                {
                    var domainAuth = JsonSerializer.Deserialize<DomainAuth>(authJson);

                    if (domainAuth != null && domainAuth.HostAuthorizationConfigurations.TryGetValue(authority, out _))
                    {
                        return GetToolCallers(openApiSpec, domainAuth);
                    }
                }
            }

            return GetToolCallers(openApiSpec, baseUrl, [], [], false, false);
        }


        private static Dictionary<string, ToolCaller> GetToolCallers(JsonDocument openApiSpec, string baseUrl, Dictionary<string, string> authHeaders, Dictionary<string, string> authQueryParams, bool oAuth, bool authRequiredButMissing = false)
        {
            Dictionary<string, ToolCaller> toolCallers = [];
            foreach (var pathProperty in openApiSpec.RootElement.GetProperty("paths").EnumerateObject())
            {
                foreach (var methodProperty in pathProperty.Value.EnumerateObject())
                {
                    var operationObj = methodProperty.Value;
                    var operationId = operationObj.TryGetProperty("operationId", out var opId)
                        ? opId.GetString()
                        : $"{methodProperty.Name}_{pathProperty.Name}";

                    var toolDefinition = OpenApiHelper.GetToolDefinitionsFromSchema(openApiSpec).FirstOrDefault(o => o.Function?.AsObject?.Name == operationId);

                    var actionRequest = new ToolCaller(
                        baseUrl,
                        pathProperty.Name,
                        methodProperty.Name,
                        operationId!,
                        operationObj,
                        toolDefinition?.Function?.AsObject?.ContentType ?? "application/json",
                        authHeaders,
                        authQueryParams,
                        oAuth,
                        authRequiredButMissing
                    );

                    toolCallers[operationId!] = actionRequest;
                }
            }
            return toolCallers;
        }

        /// <summary>
        /// Gets the type of action to perform.
        /// </summary>
        public ActionType ActionType
        {
            get
            {
                var scheme = (new Uri(BaseUrl)).Scheme;
                if (string.Equals(scheme, "client", StringComparison.InvariantCultureIgnoreCase)) return ActionType.ClientHandled;
                if (string.Equals(scheme, "tool", StringComparison.InvariantCultureIgnoreCase)) return ActionType.LocalFunction;
                if (string.Equals(scheme, "sandbox", StringComparison.InvariantCultureIgnoreCase)) return ActionType.SandboxHandled;
                return ActionType.WebApi;
            }
        }

        /// <summary>
        /// Gets or sets the baseUrl of the request.
        /// </summary>
        public string BaseUrl { get; set; } = baseUrl ?? throw new ArgumentNullException(nameof(baseUrl));

        /// <summary>
        /// Gets or sets the path of the request.
        /// </summary>
        public string Path { get; set; } = path ?? throw new ArgumentNullException(nameof(path));

        /// <summary>
        /// Gets or sets the HTTP method used in the request.
        /// </summary>
        public string Method { get; set; } = method ?? throw new ArgumentNullException(nameof(method));

        /// <summary>
        /// Gets or sets the operation name of the request.
        /// </summary>
        public string Operation { get; set; } = operation ?? throw new ArgumentNullException(nameof(operation));
        public JsonElement MethodSchema { get; } = methodSchema;

        /// <summary>
        /// Gets or sets a value indicating whether the request is consequential.
        /// </summary>
        public bool IsConsequential { get; set; }

        /// <summary>
        /// Gets or sets the content type of the request.
        /// </summary>
        public string ContentType { get; set; } = contentType ?? throw new ArgumentNullException(nameof(contentType));

        /// <summary>
        /// Gets the authentication headers for the request.
        /// </summary>
        public Dictionary<string, string> AuthHeaders { get; set; } = authHeaders ?? throw new ArgumentNullException(nameof(authHeaders));

        /// <summary>
        /// Authentication query parameters to be appended to the request URL.
        /// </summary>
        public Dictionary<string, string> AuthQueryParams { get; set; } = authQueryParams ?? throw new ArgumentNullException(nameof(authQueryParams));

        /// <summary>
        /// Gets or sets the additional parameters for the request.
        /// </summary>
        public Dictionary<string, object>? Params { get; set; }

        /// <summary>
        /// Gets or sets the response schemas for the API operations.
        /// This dictionary holds the response schema for the `200` status code for each operation.
        /// The key is the operation ID, and the value is the JSON schema representing the successful response.
        /// </summary>
        public Dictionary<string, JsonElement> ResponseSchemas
        {
            get
            {
                var responseSchemas = new Dictionary<string, JsonElement>();
                if (MethodSchema.TryGetProperty("responses", out var responses))
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
                return responseSchemas;
            }
        }


        /// <summary>
        /// Gets or sets a value indicating whether the request uses OAuth for authentication.
        /// </summary>
        public bool OAuth { get; set; } = oAuth;

        /// <summary>
        /// True when the guide is configured for service auth (header or query) for this host but no key was supplied.
        /// </summary>
        public bool AuthRequiredButMissing { get; set; } = authRequiredButMissing;

        /// <summary>
        /// Executes the action request asynchronously.
        /// </summary>
        /// <param name="oAuthUserAccessToken">Optional OAuth user access token for authentication.</param>
        /// <exception cref="ArgumentNullException"></exception>
        public async Task<HttpResponseMessage> ExecuteWebApiAsync(
            string? oAuthUserAccessToken = null,
            System.Net.Http.HttpClient? httpClient = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Replace path parameters with actual values from Params
            foreach (var param in Params ?? [])
            {
                if (Path.Contains($"{{{param.Key}}}", StringComparison.OrdinalIgnoreCase))
                {
                    Path = Path.Replace($"{{{param.Key}}}", param.Value.ToString(), StringComparison.OrdinalIgnoreCase);
                }
            }

            // Construct the complete URL
            string url = CreateUrl(BaseUrl, Path);

            // Append declared query parameters (from OpenAPI schema) for any HTTP method
            url = AppendQueryParamsFromSchema(url);

            // Append or replace auth query parameters
            if (AuthQueryParams.Count > 0)
            {
                url = AppendOrReplaceQueryParams(url, AuthQueryParams);
            }

            // Create the HTTP client and request message
            var client = httpClient ?? HttpClientUtility.Get();
            var request = new HttpRequestMessage(new HttpMethod(Method), url);

            // Add authentication headers to the request
            foreach (var header in AuthHeaders)
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            // Add OAuth token to the request if specified
            if (OAuth)
            {
                if (string.IsNullOrEmpty(oAuthUserAccessToken))
                {
                    throw new ArgumentNullException(nameof(oAuthUserAccessToken), "OAuth user access token is required.");
                }
                request.Headers.TryAddWithoutValidation("Authorization", oAuthUserAccessToken);
            }

            // If the guide configured service auth (header or query) for this host, we have already added it above.
            // Only fail when the guide explicitly requires an API key for this host but none was supplied.
            if (AuthRequiredButMissing)
            {
                throw new MissingAssistantAuthException(BaseUrl);
            }

            // Build request body content based on schema and selected ContentType
            if (!Method.Equals(HttpMethod.Get.Method, StringComparison.OrdinalIgnoreCase))
            {
                var content = BuildHttpContentFromSchema();
                if (content != null)
                {
                    request.Content = content;
                }
            }

            TraceInformation($"{nameof(ExecuteWebApiAsync)}:{request.RequestUri!.Host}");

            // Execute the request based on the specified HTTP method
            switch (Method.ToUpperInvariant())
            {
                case "GET":
                    return await client.SendAsync(request, cancellationToken);
                case "POST":
                    request.Method = HttpMethod.Post;
                    return await client.SendAsync(request, cancellationToken);
                case "PUT":
                    request.Method = HttpMethod.Put;
                    return await client.SendAsync(request, cancellationToken);
                case "DELETE":
                    request.Method = HttpMethod.Delete;
                    return await client.SendAsync(request, cancellationToken);
                case "PATCH":
                    request.Method = HttpMethod.Patch;
                    return await client.SendAsync(request, cancellationToken);
                default:
                    // Throw an exception if the HTTP method is not supported
                    throw new NotSupportedException($"Unsupported HTTP method: {Method}");
            }
        }

        public class MissingAssistantAuthException(string baseUrl)
            : Exception("Assistant tool requires an API key that is not set.")
        {
            public string BaseUrlValue { get; } = baseUrl;
        }

        /// <summary>
        /// Builds the HttpContent for the current operation using the OpenAPI requestBody schema
        /// and the configured ContentType. Supports JSON bodies (object/primitive/array) and
        /// textual bodies such as application/xhtml+xml, text/html, and text/plain.
        /// </summary>
        /// <returns>HttpContent or null when no body should be sent.</returns>
        private StringContent? BuildHttpContentFromSchema()
        {
            // No body for GET
            if (Method.Equals(HttpMethod.Get.Method, StringComparison.OrdinalIgnoreCase)) return null;

            // No params → no body
            if (Params == null) return null;

            // No requestBody in schema → no body
            if (!MethodSchema.TryGetProperty("requestBody", out var requestBodyEl)) return null;
            if (!requestBodyEl.TryGetProperty("content", out var contentEl)) return null;

            // Choose the media type matching ContentType or fall back to the first defined
            JsonProperty chosenMedia = default;
            foreach (var media in contentEl.EnumerateObject())
            {
                if (string.Equals(media.Name, ContentType, StringComparison.OrdinalIgnoreCase))
                {
                    chosenMedia = media;
                    break;
                }
                if (chosenMedia.Equals(default(JsonProperty)))
                {
                    chosenMedia = media;
                }
            }

            // If still nothing chosen, there is no content definition
            if (chosenMedia.Equals(default(JsonProperty))) return null;

            // Determine if the chosen media type is JSON
            bool isJson = chosenMedia.Name.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) ||
                          ContentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase);

            // Try to obtain the schema for body shape (object vs primitive/array)
            bool hasSchema = chosenMedia.Value.TryGetProperty("schema", out var schema);
            string? schemaType = hasSchema ? OpenApiHelper.GetSchemaType(schema, defaultType: "object") : null;

            if (isJson)
            {
                // JSON handling
                if (string.Equals(schemaType, "object", StringComparison.OrdinalIgnoreCase))
                {
                    // Build an object containing only defined properties
                    var body = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    if (schema.TryGetProperty("properties", out var props))
                    {
                        foreach (var prop in props.EnumerateObject())
                        {
                            if (Params.TryGetValue(prop.Name, out var value))
                            {
                                if (value is JsonElement je)
                                {
                                    body[prop.Name] = JsonSerializer.Deserialize<object>(je.GetRawText());
                                }
                                else
                                {
                                    body[prop.Name] = value;
                                }
                            }
                        }
                    }
                    var json = JsonSerializer.Serialize(body);
                    return new StringContent(json, Encoding.UTF8, "application/json");
                }
                else
                {
                    // Array or primitive: use a single "requestBody" param
                    object? bodyParam = null;
                    if (Params.TryGetValue("requestBody", out var any)) bodyParam = any;
                    string payload = bodyParam is string s ? s : JsonSerializer.Serialize(bodyParam);
                    return new StringContent(payload ?? "null", Encoding.UTF8, "application/json");
                }
            }

            // Textual payloads (e.g., application/xhtml+xml)
            if (string.Equals(ContentType, "application/xhtml+xml", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(ContentType, "text/html", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(ContentType, "text/plain", StringComparison.OrdinalIgnoreCase))
            {
                string text = Params.TryGetValue("requestBody", out var v)
                    ? ConvertToBodyString(v)
                    : string.Empty;
                return new StringContent(text, Encoding.UTF8, ContentType);
            }

            // Unsupported content types can be added here (e.g., multipart/form-data)
            return null;
        }

        private static string ConvertToBodyString(object? value)
        {
            if (value == null) return string.Empty;
            if (value is string s) return s;
            if (value is JsonElement je)
            {
                if (je.ValueKind == JsonValueKind.String) return je.GetString() ?? string.Empty;
                return je.GetRawText();
            }
            return value.ToString() ?? string.Empty;
        }

        /// <summary>
        /// Appends query parameters defined in the OpenAPI schema (parameters[] with in:"query")
        /// using values found in Params. Applies to any HTTP method.
        /// </summary>
        private string AppendQueryParamsFromSchema(string url)
        {
            if (Params == null) return url;
            if (!MethodSchema.TryGetProperty("parameters", out var parametersEl)) return url;

            var pairs = new List<string>();
            foreach (var param in parametersEl.EnumerateArray())
            {
                if (param.TryGetProperty("in", out var loc) &&
                    string.Equals(loc.GetString(), "query", StringComparison.OrdinalIgnoreCase) &&
                    param.TryGetProperty("name", out var nameEl))
                {
                    var name = nameEl.GetString();
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    if (Params.TryGetValue(name!, out var val) && val != null)
                    {
                        pairs.Add($"{Uri.EscapeDataString(name!)}={Uri.EscapeDataString(val.ToString()!)}");
                    }
                }
            }

            if (pairs.Count > 0)
            {
                url += (url.Contains('?') ? "&" : "?") + string.Join("&", pairs);
            }

            return url;
        }

        /// <summary>
        /// Append or replace specified query parameters on the given URL. Preserves fragments.
        /// </summary>
        private static string AppendOrReplaceQueryParams(string url, Dictionary<string, string> parameters)
        {
            if (parameters == null || parameters.Count == 0) return url;

            // Separate fragment
            string fragment = string.Empty;
            int hashIndex = url.IndexOf('#');
            if (hashIndex >= 0)
            {
                fragment = url[hashIndex..];
                url = url[..hashIndex];
            }

            string basePart = url;
            string query = string.Empty;
            int qIndex = url.IndexOf('?');
            if (qIndex >= 0)
            {
                basePart = url[..qIndex];
                query = url[(qIndex + 1)..];
            }

            var existing = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(query))
            {
                foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    var kv = pair.Split('=', 2);
                    var k = Uri.UnescapeDataString(kv[0]);
                    var v = kv.Length > 1 ? Uri.UnescapeDataString(kv[1]) : string.Empty;
                    existing[k] = v;
                }
            }

            foreach (var kvp in parameters)
            {
                existing[kvp.Key] = kvp.Value ?? string.Empty;
            }

            var newQuery = string.Join("&", existing.Select(kvp => $"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value)}"));
            return basePart + (newQuery.Length > 0 ? "?" + newQuery : string.Empty) + fragment;
        }

        /// <summary>
        /// Executes the local function asynchronously.
        /// </summary>
        /// <returns>The result of the local function execution.</returns>
        public async Task<object?> ExecuteLocalFunctionAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Validate parameters against the OpenAPI schema before attempting any reflection
            var (isValid, errorMessage) = ValidateParamsAgainstSchema();
            if (!isValid)
            {
                throw new InvalidOperationException(errorMessage!);
            }

            // Get the assembly name and method/property name from the Path
            var memberName = Path.Split('.').Last();

            // Get the containing type name from the Path
            var typeName = Path[..Path.LastIndexOf('.')];

            // Get the containing type from the loaded assemblies
            var type = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => a.GetTypes())
                .FirstOrDefault(t => t.FullName == typeName);

            if (type == null)
            {
                string binPath = AppDomain.CurrentDomain.BaseDirectory;
                foreach (string dll in Directory.GetFiles(binPath, "*.dll"))
                {
                    try
                    {
                        var assembly = Assembly.LoadFrom(dll);
                        type = assembly.GetTypes().FirstOrDefault(t => t.FullName == typeName);
                        if (type != null)
                        {
                            break;
                        }
                    }
                    catch { }
                }
            }
            if (type == null) throw new InvalidOperationException($"Type {typeName} not found in any loaded assembly");

            // First, try to find a property with the specified name
            var property = type.GetProperty(memberName, BindingFlags.Static | BindingFlags.Public);
            if (property != null)
            {
                TraceInformation($"{nameof(ExecuteLocalFunctionAsync)}:{memberName} (Property)");
                return property.GetValue(null);
            }

            // If no property is found, look for a method
            var methods = type.GetMethods().Where(m => m.Name == memberName).ToArray();
            if (methods.Length == 0) throw new InvalidOperationException($"Method or property {memberName} not found in type {typeName}");

            // Find a method that matches the provided parameters by name and type
            MethodInfo? method = null;
            foreach (var candidateMethod in methods)
            {
                var parameters = candidateMethod.GetParameters();
                if (Params == null && parameters.All(p => p.IsOptional))
                {
                    method = candidateMethod;
                    break;
                }

                if (Params != null)
                {
                    bool match = true;

                    // Ensure every required parameter is present
                    foreach (var param in parameters)
                    {
                        if (param.ParameterType == typeof(CancellationToken))
                        {
                            // Implicitly provided by runtime, not by tool-call arguments
                            continue;
                        }

                        if (!param.IsOptional && !Params.ContainsKey(param.Name!))
                        {
                            match = false;
                            break;
                        }
                    }

                    // Ensure NO extra parameters are supplied
                    if (match)
                    {
                        var declaredParamNames = parameters
                            .Where(p => p.ParameterType != typeof(CancellationToken))
                            .Select(p => p.Name)
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);

                        foreach (var suppliedKey in Params.Keys)
                        {
                            if (!declaredParamNames.Contains(suppliedKey))
                            {
                                match = false;
                                break;
                            }
                        }
                    }

                    if (match)
                    {
                        method = candidateMethod;
                        break;
                    }
                }
            }

            if (method == null) throw new InvalidOperationException($"No matching method found for {memberName} with the provided parameters");

            TraceInformation($"{nameof(ExecuteLocalFunctionAsync)}:{memberName} (Method)");

            // Get the parameters for the method
            var methodParameters = method.GetParameters();
            var paramValues = new object?[methodParameters.Length];
            for (int i = 0; i < methodParameters.Length; i++)
            {
                var param = methodParameters[i];
                if (param.ParameterType == typeof(CancellationToken))
                {
                    paramValues[i] = cancellationToken;
                    continue;
                }

                if (Params != null && Params.TryGetValue(param.Name!, out var paramValue))
                {
                    if (paramValue is JsonElement jsonElement)
                    {
                        // Convert JsonElement to the appropriate type
                        paramValues[i] = ConvertJsonElement(jsonElement, param.ParameterType);
                    }
                    else
                    {
                        // Handle enums explicitly when paramValue is not JsonElement
                        if (param.ParameterType.IsEnum)
                        {
                            try
                            {
                                paramValues[i] = paramValue switch
                                {
                                    string s => Enum.Parse(param.ParameterType, s, ignoreCase: true),
                                    int i32 => Enum.ToObject(param.ParameterType, i32),
                                    long i64 => Enum.ToObject(param.ParameterType, (int)i64),
                                    double d => Enum.ToObject(param.ParameterType, (int)d),
                                    _ => Enum.ToObject(param.ParameterType, paramValue)
                                };
                            }
                            catch (Exception)
                            {
                                // Fallback: attempt standard change type which will throw if invalid
                                paramValues[i] = Convert.ChangeType(paramValue, Enum.GetUnderlyingType(param.ParameterType));
                            }
                        }
                        else
                        {
                            // Convert the parameter value to the appropriate type
                            paramValues[i] = Convert.ChangeType(paramValue, param.ParameterType);
                        }
                    }
                }
                else
                {
                    paramValues[i] = param.HasDefaultValue ? param.DefaultValue : GetDefault(param.ParameterType);
                }
            }

            // Special handling for crew-bridge operations (Agent.Invoke)
            // These are NOT normal local functions - they're bridges to invoke other assistants
            if (type.FullName == "AntRunner.Chat.Agent" && string.Equals(memberName, "Invoke", StringComparison.Ordinal))
            {
                var assistantNameArg = Params != null && Params.TryGetValue("assistantName", out var an) ? an?.ToString() : null;
                var instructionsArg = Params != null && Params.TryGetValue("instructions", out var ins) ? ins?.ToString() : null;
                if (string.IsNullOrWhiteSpace(assistantNameArg) || string.IsNullOrWhiteSpace(instructionsArg))
                {
                    throw new InvalidOperationException("Agent.Invoke requires 'assistantName' and 'instructions'.");
                }

                // The tool-calling layer must inject the InvocationContext
                if (Params == null || !Params.TryGetValue("context", out var contextObj) || contextObj == null)
                {
                    throw new InvalidOperationException("Agent.Invoke requires an 'context' parameter of type InvocationContext.");
                }

                if (contextObj is not InvocationContext context)
                {
                    throw new InvalidOperationException("Agent.Invoke 'context' parameter must be of type InvocationContext.");
                }
                
                // Use reflection to find and invoke Agent.Invoke
                var invokeMethod = type.GetMethod(
                    "Invoke",
                    BindingFlags.Public | BindingFlags.Static,
                    [typeof(string), typeof(string), typeof(InvocationContext), typeof(CancellationToken)]
                );
                
                if (invokeMethod != null)
                {
                    object? invokeResult = invokeMethod.Invoke(null, [assistantNameArg, instructionsArg, context, cancellationToken]);
                    if (invokeResult is Task<string> strTask)
                    {
                        return await strTask.ConfigureAwait(false);
                    }
                    if (invokeResult is Task taskResult)
                    {
                        await taskResult.ConfigureAwait(false);
                        var t = taskResult.GetType();
                        if (t.IsGenericType)
                        {
                            var prop = t.GetProperty("Result");
                            return prop?.GetValue(taskResult);
                        }
                        return null;
                    }
                    return invokeResult;
                }
                else
                {
                    throw new InvalidOperationException($"Could not find Agent.Invoke(string, string, InvocationContext, CancellationToken) method on {type.FullName}");
                }
            }

            // Invoke the method with the parameters
            var result = method.Invoke(null, paramValues);

            // If the method is async, await its completion
            if (result is Task task)
            {
                await task.ConfigureAwait(false);
                var taskType = task.GetType();
                if (taskType.IsGenericType)
                {
                    var resultProperty = taskType.GetProperty("Result");
                    if (resultProperty != null)
                    {
                        return resultProperty.GetValue(task);
                    }
                }
                return null;
            }

            return result;
        }

        /// <summary>
        /// Validates supplied Params against the OpenAPI method schema. Ensures that:
        /// - No extra parameters are supplied that are not defined in the schema
        /// - All required parameters (after any default injection) are present
        /// </summary>
        /// <remarks>
        /// This validation occurs prior to attempting to resolve and invoke a local function,
        /// allowing us to return a clear error when the tool call is invalid by definition.
        /// </remarks>
        /// <returns>A tuple indicating validity and an error message when invalid.</returns>
        public (bool IsValid, string? ErrorMessage) ValidateParamsAgainstSchema()
        {
            try
            {
                // Collect declared parameter names and required names from the OpenAPI operation
                var declaredNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var requiredNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // parameters[] (path/query/header/cookie)
                if (MethodSchema.TryGetProperty("parameters", out var parametersEl))
                {
                    foreach (var param in parametersEl.EnumerateArray())
                    {
                        if (param.TryGetProperty("name", out var nameEl))
                        {
                            var name = nameEl.GetString();
                            if (!string.IsNullOrWhiteSpace(name))
                            {
                                declaredNames.Add(name!);
                                if (param.TryGetProperty("required", out var reqEl) && reqEl.ValueKind == JsonValueKind.True)
                                {
                                    requiredNames.Add(name!);
                                }
                            }
                        }
                    }
                }

                // requestBody
                if (MethodSchema.TryGetProperty("requestBody", out var requestBodyEl))
                {
                    if (requestBodyEl.TryGetProperty("content", out var contentEl))
                    {
                        var firstMedia = contentEl.EnumerateObject().FirstOrDefault();
                        if (!firstMedia.Equals(default(JsonProperty)) && firstMedia.Value.TryGetProperty("schema", out var schemaEl))
                        {
                            var isObjectBody = string.Equals(OpenApiHelper.GetSchemaType(schemaEl), "object", StringComparison.OrdinalIgnoreCase);

                            if (isObjectBody)
                            {
                                // Object body: expose object properties as parameters and use its required list
                                if (schemaEl.TryGetProperty("properties", out var propsEl))
                                {
                                    foreach (var prop in propsEl.EnumerateObject())
                                    {
                                        declaredNames.Add(prop.Name);
                                    }
                                }
                                if (schemaEl.TryGetProperty("required", out var reqArray) && reqArray.ValueKind == JsonValueKind.Array)
                                {
                                    foreach (var req in reqArray.EnumerateArray())
                                    {
                                        var name = req.GetString();
                                        if (!string.IsNullOrWhiteSpace(name)) requiredNames.Add(name!);
                                    }
                                }
                                // Note: do NOT add top-level requestBody.required for object schemas
                            }
                            else
                            {
                                // Non-object body → allow/require a single "requestBody" parameter as appropriate
                                declaredNames.Add("requestBody");
                                if (requestBodyEl.TryGetProperty("required", out var rbReq) && rbReq.ValueKind == JsonValueKind.True)
                                {
                                    requiredNames.Add("requestBody");
                                }
                            }
                        }
                    }
                }

                // If no params were supplied, validation passes (method defaults or optional params may apply)
                var supplied = Params ?? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

                // Allow all hidden parameters from the contract (annotation-driven)
                var contract = ToolContractRegistry.GetContract(Path);
                foreach (var paramMetadata in contract.ParameterMetadata.Values)
                {
                    if (paramMetadata.Hidden)
                    {
                        // Hidden parameters are injected at runtime and shouldn't be in the LLM-facing schema
                        // But they ARE valid parameters for validation purposes
                        var paramName = contract.ParameterMetadata.FirstOrDefault(kvp => kvp.Value == paramMetadata).Key;
                        if (!string.IsNullOrEmpty(paramName))
                        {
                            declaredNames.Add(paramName);
                        }
                    }
                }

                // Unknown parameters (extras not defined by the schema)
                var unknown = supplied.Keys.Where(k => !declaredNames.Contains(k)).OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToArray();

                // Missing required parameters
                var missing = requiredNames.Where(r => !supplied.ContainsKey(r)).OrderBy(r => r, StringComparer.OrdinalIgnoreCase).ToArray();

                if (unknown.Length == 0 && missing.Length == 0)
                {
                    return (true, null);
                }

                // Build a user-friendly, natural-language error message
                var msg = new StringBuilder();
                msg.Append("Your tool call is not valid. ");

                if (unknown.Length > 0)
                {
                    if (unknown.Length == 1)
                    {
                        msg.Append($"`{unknown[0]}` is not a valid parameter. ");
                    }
                    else
                    {
                        msg.Append($"These parameters are not valid: {FormatParamList(unknown)}. ");
                    }
                }

                if (missing.Length > 0)
                {
                    var plural = missing.Length > 1 ? "parameters" : "parameter";
                    msg.Append($"It is missing required {plural}: {FormatParamList(missing)}. ");
                }

                msg.Append($"Please refer to the `{Operation}` tool definition and try again.");

                return (false, msg.ToString().Trim());
            }
            catch
            {
                // If anything goes wrong during validation, do not block execution here; allow downstream handling
                return (true, null);
            }
        }

        private static string FormatParamList(IEnumerable<string> names)
        {
            var list = names.Select(n => $"`{n}`").ToArray();
            if (list.Length <= 1) return string.Join("", list);
            if (list.Length == 2) return string.Join(" and ", list);
            return string.Join(", ", list[..^1]) + ", and " + list[^1];
        }

        private static object? ConvertJsonElement(JsonElement jsonElement, Type targetType)
        {
            return targetType switch
            {
                Type t when t == typeof(int) => jsonElement.GetInt32(),
                Type t when t == typeof(double) => jsonElement.GetDouble(),
                Type t when t == typeof(bool) => jsonElement.GetBoolean(),
                Type t when t == typeof(string) => jsonElement.GetString(),
                Type t when t.IsEnum => jsonElement.ValueKind switch
                {
                    JsonValueKind.Number => Enum.ToObject(targetType, jsonElement.GetInt32()),
                    JsonValueKind.String => Enum.Parse(targetType, jsonElement.GetString() ?? string.Empty, ignoreCase: true),
                    _ => throw new InvalidOperationException($"Cannot convert {jsonElement.ValueKind} to enum {targetType}")
                },
                Type t when t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>) => 
                    JsonSerializer.Deserialize(jsonElement.GetRawText(), targetType),
                _ => throw new InvalidOperationException($"Unsupported target type: {targetType}")
            };
        }

        private static object? GetDefault(Type type)
        {
            if (type.IsValueType)
            {
                return Activator.CreateInstance(type);
            }
            return null; // Reference types default to null
        }

        /// <summary>
        /// Creates the complete URL by combining the baseUrl and path.
        /// Properly handles base URLs with path segments (e.g., "https://api.example.com/v1")
        /// and appends the operation path according to OpenAPI specification.
        /// </summary>
        /// <param name="domain">The baseUrl of the request.</param>
        /// <param name="path">The path of the request.</param>
        /// <returns>The complete URL as a string.</returns>
        private static string CreateUrl(string domain, string path)
        {
            // Ensure base URL ends with a slash for proper path appending
            var baseUrl = domain.TrimEnd('/') + "/";
            // Remove leading slash from path since we're appending to a trailing slash
            var relativePath = path.TrimStart('/');
            // Combine and remove any trailing slash from the final URL
            var uri = new Uri(new Uri(baseUrl), relativePath);
            return uri.ToString().TrimEnd('/');
        }

        /// <summary>
        /// Creates a new instance of the <see cref="ToolCaller"/> class that is a copy of the current instance.
        /// </summary>
        /// <returns>A new instance of <see cref="ToolCaller"/> that is a copy of this instance.</returns>
        public ToolCaller Clone()
        {
            // Create a new instance of ToolCaller with the same properties
            return new ToolCaller(
                baseUrl: this.BaseUrl,
                path: this.Path,
                method: this.Method,
                operation: this.Operation,
                methodSchema: this.MethodSchema,
                contentType: this.ContentType,
                authHeaders: new Dictionary<string, string>(this.AuthHeaders),
                authQueryParams: new Dictionary<string, string>(this.AuthQueryParams),
                oAuth: this.OAuth,
                authRequiredButMissing: this.AuthRequiredButMissing)
            {
                // Copy the Params dictionary if it is not null
                Params = this.Params != null ? new Dictionary<string, object>(this.Params) : null
            };
        }

        /// <summary>
        /// Ensures all required parameters are present in <see cref="Params"/>. If a required
        /// parameter is missing and the OpenAPI schema provides a default/enum/description value,
        /// that value is injected so the LLM doesn’t have to repeat boiler-plate fields.
        /// </summary>
        /// <remarks>
        /// This follows the rules:
        /// 1. Look at requestBody → content → first media type → schema → required[]
        /// 2. For each required name missing from Params, attempt to obtain:
        ///    a. "default" property
        ///    b. single-value "enum" property
        ///    c. fall back to the "description" text when it looks like a literal
        /// </remarks>
        public void AddMissingRequiredParamsFromSchema()
        {
            // Params must be mutable
            Params ??= new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

            // Navigate to the schema inside the OpenAPI operation
            if (!MethodSchema.TryGetProperty("requestBody", out var requestBody)) return;
            if (!requestBody.TryGetProperty("content", out var content)) return;

            // Use the first media type in the content object (usually application/json)
            var firstMediaType = content.EnumerateObject().FirstOrDefault();
            if (firstMediaType.Equals(default(JsonProperty))) return;
            if (!firstMediaType.Value.TryGetProperty("schema", out var bodySchema)) return;

            // Required array
            if (!bodySchema.TryGetProperty("required", out var requiredArray)) return;
            if (!bodySchema.TryGetProperty("properties", out var properties)) return;

            foreach (var req in requiredArray.EnumerateArray())
            {
                var name = req.GetString();
                if (string.IsNullOrEmpty(name)) continue;
                if (Params.ContainsKey(name)) continue;

                if (!properties.TryGetProperty(name, out var propSchema)) continue;

                // Try default
                if (propSchema.TryGetProperty("default", out var def))
                {
                    Params[name] = ExtractJsonElementValue(def) ?? "";
                    continue;
                }

                // Try single-value enum
                if (propSchema.TryGetProperty("enum", out var enumArray) && enumArray.GetArrayLength() == 1)
                {
                    Params[name] = ExtractJsonElementValue(enumArray[0]) ?? "";
                    continue;
                }
            }
        }

        private static object? ExtractJsonElementValue(JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null
            };
        }
    }
}
