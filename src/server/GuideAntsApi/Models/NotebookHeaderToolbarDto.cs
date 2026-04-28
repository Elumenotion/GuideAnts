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
    IReadOnlyList<string> Blockers);

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
    string? InProgressState);

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
    string? InProgressState);
