namespace AntRunner.ToolCalling.Attributes;

/// <summary>
/// Marks a method as a tool that can be called by AI agents.
/// Provides metadata for generating OpenAPI schemas dynamically.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class ToolAttribute : Attribute
{
    /// <summary>
    /// The unique operation ID for this tool in OpenAPI specifications.
    /// </summary>
    public required string OperationId { get; init; }
    
    /// <summary>
    /// A brief summary of what this tool does.
    /// </summary>
    public required string Summary { get; init; }
}

/// <summary>
/// Provides metadata for method parameters in tool methods.
/// Used to generate rich OpenAPI parameter documentation.
/// </summary>
[AttributeUsage(AttributeTargets.Parameter)]
public sealed class ParameterAttribute : Attribute
{
    /// <summary>
    /// Description of what this parameter represents.
    /// </summary>
    public required string Description { get; init; }
    
    /// <summary>
    /// Default value for this parameter (optional).
    /// </summary>
    public object? Default { get; init; }
    
    /// <summary>
    /// If true, this parameter will be excluded from OpenAPI schema generation.
    /// Used for context parameters that are injected automatically.
    /// </summary>
    public bool Hidden { get; init; } = false;
}

/// <summary>
/// Indicates that a tool method requires notebook context to function properly.
/// Methods with this attribute will have InvocationContext automatically injected.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RequiresNotebookContextAttribute : Attribute { }

/// <summary>
/// Specifies OAuth handling policy for a tool method.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class OAuthAttribute : Attribute
{
    /// <summary>
    /// The OAuth policy to apply to this tool.
    /// </summary>
    public OAuthPolicy Policy { get; init; } = OAuthPolicy.Forward;
}

/// <summary>
/// Defines how OAuth tokens should be handled for a tool.
/// </summary>
public enum OAuthPolicy
{
    /// <summary>
    /// No OAuth handling required.
    /// </summary>
    None,
    
    /// <summary>
    /// Forward OAuth token if available.
    /// </summary>
    Forward,
    
    /// <summary>
    /// OAuth token is required for this tool to function.
    /// </summary>
    Required
}
