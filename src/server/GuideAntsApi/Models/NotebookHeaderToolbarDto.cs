using GuideAntsApi.Models.Settings;

namespace GuideAntsApi.Models;

public sealed record NotebookHeaderToolbarDto(
    NotebookToolbarChatDto Chat,
    IReadOnlyList<NotebookToolbarServiceDto> Services,
    DateTime GeneratedUtc);

public sealed record NotebookToolbarModelOptionDto(
    string ModelId,
    string DisplayName,
    string Provider,
    bool IsActive);

public sealed record NotebookToolbarProviderOptionDto(
    string ProviderId,
    string DisplayName,
    string ProviderKind,
    bool CanActivate,
    IReadOnlyList<string> Blockers,
    string? ProviderSection = null,
    string? ModelId = null);

public sealed record NotebookToolbarSelectionDto(
    string? ResourceId,
    string? Summary);

public sealed record NotebookToolbarServiceDto(
    string ServiceId,
    string DisplayName,
    string Kind,
    string Status,
    string Summary,
    string ActiveProviderId,
    string ActiveProviderLabel,
    bool SupportsLocalRuntimePower,
    bool LocalRuntimeOn,
    IReadOnlyList<NotebookToolbarProviderOptionDto> ProviderOptions,
    NotebookToolbarSelectionDto? Selection,
    IReadOnlyList<string> Blockers,
    IReadOnlyList<NotebookToolbarLocalModelOptionDto> LocalModelOptions,
    string? InProgressOperationId,
    string? InProgressState,
    // Plane A selection ∈ Plane C inventory. False => a deleted artifact surfaces as a
    // named condition instead of a phantom "(active)" marker (P6 empty-state).
    bool SelectionExists = true,
    EndpointPresenceDto? Presence = null,
    DateTime? ObservedUtc = null);

public sealed record NotebookToolbarLocalModelOptionDto(
    string ModelRef,
    string DisplayLabel,
    bool IsComplete,
    bool IsActive = false);

public sealed record NotebookToolbarChatDto(
    string Status,
    string Summary,
    string? ConversationId,
    string? SelectedAssistantName,
    string? EffectiveModelId,
    string? EffectiveModelDisplayName,
    string? EffectiveProvider,
    bool OverrideAllChatModels,
    bool SupportsLocalRuntimePower,
    bool LocalRuntimeOn,
    IReadOnlyList<NotebookToolbarModelOptionDto> ModelOptions,
    IReadOnlyList<string> Blockers,
    string? InProgressOperationId,
    string? InProgressState,
    string? EffectiveModelSource = null,
    // Chat section carries presence ONLY (nothing else): the config section renders it
    // from cache; readiness stays live and unchanged bit-for-bit.
    EndpointPresenceDto? Presence = null);

public sealed record NotebookChatReadinessDto(
    string? EffectiveModelId,
    string? EffectiveModelDisplayName,
    string? EffectiveProvider,
    IReadOnlyList<string> Blockers,
    bool SupportsLocalRuntimePower,
    bool LocalRuntimeOn,
    string? InProgressOperationId,
    string? InProgressState);
