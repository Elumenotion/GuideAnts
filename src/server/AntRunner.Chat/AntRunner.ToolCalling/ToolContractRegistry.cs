using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using AntRunner.ToolCalling.Attributes;

namespace AntRunner.ToolCalling;

/// <summary>
/// Represents the contract for a tool method, including its metadata and requirements.
/// </summary>
public record ToolContract(
    bool RequiresNotebookContext,
    OAuthPolicy OAuthPolicy,
    ToolAttribute? ToolMetadata,
    Dictionary<string, ParameterAttribute> ParameterMetadata
);

/// <summary>
/// Central registry for tool contracts that discovers annotated methods and generates OpenAPI schemas dynamically.
/// Eliminates the need for hardcoded schema generators and static JSON files.
/// </summary>
public static class ToolContractRegistry
{
    private static readonly Dictionary<string, ToolContract> _contracts = new();
    private static readonly Dictionary<string, (Type Type, MethodInfo Method)> _methodLookup = new();
    private static readonly ConcurrentDictionary<string, string> _schemaCache = new();
    
    static ToolContractRegistry()
    {
        InitializeContracts();
    }
    
    /// <summary>
    /// Discovers all annotated tool methods via reflection and builds the contract registry.
    /// </summary>
    private static void InitializeContracts()
    {
        // Get all currently loaded assemblies
        var assemblies = AppDomain.CurrentDomain.GetAssemblies().ToList();
        
        // Also ensure GuideAntsApi assembly is loaded if available
        try
        {
            var guideantsAssembly = assemblies.FirstOrDefault(a => a.GetName().Name?.Contains("GuideAntsApi") == true);
            if (guideantsAssembly == null)
            {
                // Try to load GuideAntsApi assembly by name
                try
                {
                    guideantsAssembly = Assembly.Load("GuideAntsApi");
                    assemblies.Add(guideantsAssembly);
                }
                catch
                {
                    // Assembly not available, continue with what we have
                }
            }
        }
        catch
        {
            // Continue with available assemblies
        }
        
        foreach (var assembly in assemblies)
        {
            try
            {
                foreach (var type in assembly.GetTypes())
                {
                    foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
                    {
                        var toolAttr = method.GetCustomAttribute<ToolAttribute>();
                        var contextAttr = method.GetCustomAttribute<RequiresNotebookContextAttribute>();
                        var oauthAttr = method.GetCustomAttribute<OAuthAttribute>();
                        
                        if (toolAttr != null || contextAttr != null || oauthAttr != null)
                        {
                            var fullyQualifiedName = $"{type.FullName}.{method.Name}";
                            
                            // Collect parameter metadata
                            var parameterMetadata = new Dictionary<string, ParameterAttribute>();
                            foreach (var param in method.GetParameters())
                            {
                                var paramAttr = param.GetCustomAttribute<ParameterAttribute>();
                                if (paramAttr != null)
                                {
                                    parameterMetadata[param.Name!] = paramAttr;
                                }
                            }
                            
                            var contract = new ToolContract(
                                RequiresNotebookContext: contextAttr != null,
                                OAuthPolicy: oauthAttr?.Policy ?? OAuthPolicy.None,
                                ToolMetadata: toolAttr,
                                ParameterMetadata: parameterMetadata
                            );
                            
                            _contracts[fullyQualifiedName] = contract;
                            _methodLookup[fullyQualifiedName] = (type, method);
                        }
                    }
                }
            }
            catch (ReflectionTypeLoadException)
            {
                // Skip assemblies that can't be reflected over
                continue;
            }
            catch
            {
                // Skip problematic assemblies
                continue;
            }
        }
    }
    
    /// <summary>
    /// Gets the tool contract for a fully qualified method name.
    /// </summary>
    public static ToolContract GetContract(string fullyQualifiedMethodName)
    {
        return _contracts.GetValueOrDefault(fullyQualifiedMethodName, 
            new ToolContract(false, OAuthPolicy.None, null, new Dictionary<string, ParameterAttribute>()));
    }
    
    /// <summary>
    /// Gets all registered tool contracts that have ToolAttribute metadata.
    /// Returns a dictionary mapping operation IDs to their fully qualified method names.
    /// </summary>
    public static Dictionary<string, string> GetAllToolOperations()
    {
        return _contracts
            .Where(kvp => kvp.Value.ToolMetadata != null)
            .ToDictionary(
                kvp => kvp.Value.ToolMetadata!.OperationId, 
                kvp => kvp.Key);
    }
    
    /// <summary>
    /// Generates an OpenAPI schema dynamically from method signature and attributes.
    /// Schemas are cached for performance since they're based on compile-time attributes.
    /// Thread-safe via ConcurrentDictionary.
    /// </summary>
    public static string GenerateOpenApiSchema(string fullyQualifiedMethodName)
    {
        return _schemaCache.GetOrAdd(fullyQualifiedMethodName, methodName =>
        {
            var contract = GetContract(methodName);
            if (contract.ToolMetadata == null)
            {
                // Try to refresh contracts in case assemblies were loaded after initialization
                RefreshContracts();
                contract = GetContract(methodName);
                
                if (contract.ToolMetadata == null)
                {
                    // Provide better error message with available contracts
                    var availableContracts = _contracts.Keys.Where(k => _contracts[k].ToolMetadata != null).ToList();
                    throw new InvalidOperationException($"No tool metadata found for {methodName}. Available contracts: {string.Join(", ", availableContracts)}");
                }
            }
                
            if (!_methodLookup.TryGetValue(methodName, out var methodInfo))
                throw new InvalidOperationException($"Method not found: {methodName}");
                
            return GenerateSchemaFromMethodAndAttributes(methodInfo.Type, methodInfo.Method, contract);
        });
    }
    
    /// <summary>
    /// Refreshes the contract registry by re-scanning all loaded assemblies.
    /// Useful when assemblies are loaded after initial static initialization.
    /// </summary>
    public static void RefreshContracts()
    {
        InitializeContracts();
    }
    
    /// <summary>
    /// Generates the complete OpenAPI schema JSON from method reflection and attributes.
    /// </summary>
    private static string GenerateSchemaFromMethodAndAttributes(Type type, MethodInfo method, ToolContract contract)
    {
        var toolMetadata = contract.ToolMetadata!;
        
        var schema = new
        {
            openapi = "3.0.1",
            info = new { title = "Tool API", version = "v1" },
            servers = new[] { new { url = "tool://localhost" } },
            paths = new Dictionary<string, object>
            {
                [$"{type.FullName}.{method.Name}"] = new
                {
                    post = new
                    {
                        tags = new[] { "Tools" },
                        summary = toolMetadata.Summary,
                        operationId = toolMetadata.OperationId,
                        parameters = Array.Empty<object>(),
                        requestBody = BuildRequestBody(method, contract),
                        responses = BuildStandardResponses()
                    }
                }
            }
        };

        return JsonSerializer.Serialize(schema, new JsonSerializerOptions { WriteIndented = true });
    }
    
    /// <summary>
    /// Builds the request body schema from method parameters, excluding hidden parameters.
    /// </summary>
    private static object BuildRequestBody(MethodInfo method, ToolContract contract)
    {
        var properties = new Dictionary<string, object>();
        var required = new List<string>();
        
        foreach (var parameter in method.GetParameters())
        {
            // Skip hidden parameters (like InvocationContext)
            if (contract.ParameterMetadata.TryGetValue(parameter.Name!, out var paramAttr) && paramAttr.Hidden)
                continue;
                
            // Skip InvocationContext parameters automatically
            if (parameter.ParameterType == typeof(InvocationContext) || 
                (parameter.ParameterType.IsGenericType && parameter.ParameterType.GetGenericTypeDefinition() == typeof(Nullable<>) && 
                 Nullable.GetUnderlyingType(parameter.ParameterType) == typeof(InvocationContext)))
                continue;
            
            var propertySchema = BuildParameterSchema(parameter, paramAttr);
            properties[parameter.Name!] = propertySchema;
            
            // Add to required if no default value and not nullable
            if (!parameter.HasDefaultValue && !IsNullableType(parameter.ParameterType))
            {
                required.Add(parameter.Name!);
            }
        }
        
        return new
        {
            required = true,
            description = "Request payload",
            content = new
            {
                application_json = new
                {
                    schema = new
                    {
                        type = "object",
                        properties = properties,
                        required = required.ToArray(),
                        additionalProperties = false
                    }
                }
            }
        };
    }
    
    /// <summary>
    /// Builds schema for an individual parameter based on its type and attributes.
    /// </summary>
    private static object BuildParameterSchema(ParameterInfo parameter, ParameterAttribute? paramAttr)
    {
        var type = parameter.ParameterType;
        var schema = new Dictionary<string, object>();
        
        // Determine JSON schema type
        if (type == typeof(string) || (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Nullable<>) && Nullable.GetUnderlyingType(type) == typeof(string)))
        {
            schema["type"] = "string";
        }
        else if (type == typeof(int) || Nullable.GetUnderlyingType(type) == typeof(int))
        {
            schema["type"] = "integer";
        }
        else if (type == typeof(double) || Nullable.GetUnderlyingType(type) == typeof(double) || 
                 type == typeof(float) || Nullable.GetUnderlyingType(type) == typeof(float))
        {
            schema["type"] = "number";
        }
        else if (type == typeof(bool) || Nullable.GetUnderlyingType(type) == typeof(bool))
        {
            schema["type"] = "boolean";
        }
        else if (type.IsEnum)
        {
            schema["type"] = "integer";
            // For enums, we could add enum values, but for now keep it simple
        }
        else
        {
            schema["type"] = "string"; // Default fallback
        }
        
        // Add description from parameter attribute
        if (paramAttr != null)
        {
            schema["description"] = paramAttr.Description;
        }
        
        // Add default value if parameter has one
        if (parameter.HasDefaultValue && parameter.DefaultValue != null)
        {
            schema["default"] = parameter.DefaultValue;
        }
        
        return schema;
    }
    
    /// <summary>
    /// Builds standard response schemas for tool methods.
    /// </summary>
    private static object BuildStandardResponses()
    {
        return new Dictionary<string, object>
        {
            ["200"] = new
            {
                description = "OK",
                content = new
                {
                    application_json = new
                    {
                        schema = new { type = "object" }
                    }
                }
            },
            ["400"] = new
            {
                description = "Bad Request",
                content = new
                {
                    application_json = new
                    {
                        schema = new { type = "string" }
                    }
                }
            }
        };
    }
    
    /// <summary>
    /// Checks if a type is nullable (either Nullable&lt;T&gt; or reference type).
    /// </summary>
    private static bool IsNullableType(Type type)
    {
        return Nullable.GetUnderlyingType(type) != null || !type.IsValueType;
    }
}
