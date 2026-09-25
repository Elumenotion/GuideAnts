using System.Text.Json.Serialization;
using GuideAntsApi.Services.Routing;

namespace GuideAntsApi.Services.Conversations;

// Wire contract is the enum name. The server already writes it that way via the global converter;
// the attribute makes every other reader (tests, external clients) agree without extra options.
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ContextEstimateSource
{
    None,
    ProviderUsage,
    Characters
}

/// <summary>
/// Context-meter inputs for a conversation. <see cref="BoundaryTurnIndex"/> reflects the persisted
/// compaction boundary, or null if the conversation has never been compacted.
/// </summary>
public sealed record ConversationContextStatusDto(
    int? ContextWindowTokens,
    int? EstimatedPromptTokens,
    int? BoundaryTurnIndex,
    ContextEstimateSource EstimateSource,
    string? ModelDeploymentId,
    ContextWindowSource ContextWindowSource);

public interface IConversationContextStatusService
{
    Task<ConversationContextStatusDto> GetAsync(Guid conversationId, CancellationToken ct = default);
}
