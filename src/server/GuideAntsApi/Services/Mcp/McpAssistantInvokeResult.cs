namespace GuideAntsApi.Services.Mcp;

/// <summary>
/// Structured result of an MCP assistant invocation. Carries the JSON payload returned to MCP
/// clients plus the identifiers needed to embed the run's output images as <c>ImageContentBlock</c>s.
/// </summary>
/// <param name="Json">The serialized JSON payload (or a JSON error document on failure).</param>
/// <param name="ConversationId">The conversation the turn belongs to; null on error.</param>
/// <param name="TurnIndex">The index of the turn produced by this invocation; null on error.</param>
public sealed record McpAssistantInvokeResult(
    string Json,
    Guid? ConversationId,
    int? TurnIndex);
