namespace GuideAntsApi.Services.Routing;

using AntRunner.Chat.Abstractions;

/// <summary>
/// R-1.6 / R-9.1 seam: resolves the effective catalog model id string for a chat turn from
/// entity configuration and <c>ChatDefaults</c> application settings.
/// </summary>
public enum ChatModelReferenceKind
{
    Direct,
    DefaultedTo,
    OverriddenToDefault
}

/// <summary>
/// Effective chat model for one turn after applying <c>ChatDefaults</c> and override rules.
/// </summary>
/// <param name="ModelId">Catalog model id string passed to routing.</param>
/// <param name="ReferenceKind">Whether the entity id was used as-is, defaulted, or forced by override.</param>
/// <param name="ExecutionPolicy">Deterministic execution model/parameter policy for this run.</param>
public sealed record ResolvedChatModel(
    string ModelId,
    ChatModelReferenceKind ReferenceKind,
    ResolvedExecutionPolicy ExecutionPolicy);

public interface IChatModelResolver
{
    /// <summary>
    /// <paramref name="entityModelId"/> is the raw id from the assistant/guide/template/request chain
    /// before applying global defaults (may be null/whitespace when the entity uses "default").
    /// </summary>
    ResolvedChatModel Resolve(string? entityModelId);
}
