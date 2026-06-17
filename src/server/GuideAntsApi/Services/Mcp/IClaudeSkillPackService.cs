namespace GuideAntsApi.Services.Mcp;

public sealed record ClaudeSkillPackBuildRequest(
    Guid PubId,
    string GuideName,
    string? FriendlyName,
    string? McpDescription,
    string ApiBaseUrl,
    IReadOnlyList<McpAddressableAssistant> Assistants);

public sealed record ClaudeSkillPackResult(
    byte[] ZipBytes,
    string FileName,
    string SkillDirectoryName);

public interface IClaudeSkillPackService
{
    Task<ClaudeSkillPackResult> BuildAsync(
        ClaudeSkillPackBuildRequest request,
        CancellationToken cancellationToken = default);
}
