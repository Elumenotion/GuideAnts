namespace GuideAntsApi.Services.Mcp;

/// <summary>
/// Scoped service populated by <see cref="McpApiKeyMiddleware"/> and consumed by MCP tool methods.
/// Contains the resolved published-guide context for the current request.
/// </summary>
public class McpPublishedGuideContext
{
    public Guid PubId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid NotebookId { get; set; }
    public Guid GuideId { get; set; }
    public string? UserIdentity { get; set; }
    public bool IsValid { get; set; }

    // Guide metadata for discovery
    public string GuideName { get; set; } = string.Empty;
    public string? GuideDescription { get; set; }
    public string? McpDescription { get; set; }

    /// <summary>
    /// Public API origin for the current MCP request (scheme + host), used to rewrite
    /// published file URLs before returning content to external MCP clients.
    /// </summary>
    public string? PublicApiOrigin { get; set; }

    /// <summary>
    /// Guide plus crew members exposed as MCP tools for this published guide.
    /// Populated by <see cref="McpApiKeyMiddleware"/>.
    /// </summary>
    public IReadOnlyList<McpAddressableAssistant> AddressableAssistants { get; set; } = [];
}
