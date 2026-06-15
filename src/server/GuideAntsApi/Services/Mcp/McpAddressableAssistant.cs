namespace GuideAntsApi.Services.Mcp;

/// <summary>
/// A guide or crew member exposed as an MCP tool for a published guide.
/// </summary>
public sealed record McpAddressableAssistant(
    Guid AssistantId,
    string Name,
    string ToolName,
    string Description,
    bool IsGuide);
