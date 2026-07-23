using GuideAntsApi.Models.Settings;
using GuideAntsApi.Services.LlamaCpp;

namespace GuideAntsApi.IntegrationTests.TestUtils;

/// <summary>
/// In-process stand-in for <see cref="ILlamaRuntimeAdminClient"/>. Integration
/// tests configure <c>LlamaCpp:BaseUrl</c> to <c>localhost:8110</c>; without this
/// stub, catalog / router / inventory probes block on the 4-hour admin HttpClient
/// when nginx accepts TCP but the upstream never answers.
/// </summary>
public sealed class StubLlamaRuntimeAdminClient : ILlamaRuntimeAdminClient
{
    public Task<LlamaCatalogResponseDto> GetCatalogAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new LlamaCatalogResponseDto(
            SchemaVersion: 1,
            Task: "chat",
            CatalogVersion: "test",
            Models: []));

    public Task<LlamaCatalogQuantsResponseDto> GetCatalogQuantsAsync(
        string catalogId,
        string? catalogVersion,
        string? resolvedHfToken,
        CancellationToken cancellationToken = default,
        string? resolvedRevision = null) =>
        Task.FromResult(new LlamaCatalogQuantsResponseDto(
            CatalogId: catalogId,
            Repository: "test/repo",
            RequestedRevision: catalogVersion ?? "main",
            ResolvedRevision: resolvedRevision ?? catalogVersion ?? "main",
            Quants: [],
            Projector: null));

    public Task<LlamaAdminRouterEntriesResponseDto> GetRouterEntriesAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new LlamaAdminRouterEntriesResponseDto([]));

    public Task<LlamaAdminRouterEntryUpsertResult> PutRouterEntryAsync(
        LlamaRouterEntryPutRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new LlamaAdminRouterEntryUpsertResult(true, null, null));

    public Task AddOrUpdateRouterEntryAsync(
        string alias,
        string modelPath,
        string mmprojPath,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task AddOrUpdateRouterEntryAsync(
        string alias,
        string modelPath,
        string mmprojPath,
        int? contextSize,
        int? cacheRamMib,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task<ModelDownloadOperationDto> StartExactDownloadAsync(
        ExactStartModelDownloadRequest request,
        string? resolvedHfToken,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(CompletedDownload(request.Alias));

    public Task<ModelDownloadOperationDto> StartDownloadAsync(
        StartModelDownloadRequest request,
        string? resolvedHfToken,
        bool allowOverwrite,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(CompletedDownload(request.RouterModelId));

    public Task<ModelDownloadOperationDto?> GetDownloadStatusAsync(
        string operationId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<ModelDownloadOperationDto?>(CompletedDownload("stub-alias", operationId));

    public Task<bool> DeleteRouterEntryAsync(
        string alias,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(true);

    public Task DeleteObsoleteArtifactPathsAsync(
        string targetDirectory,
        IReadOnlyList<string> repositoryPaths,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task<LlamaAdminRestartResultDto> RestartLlamaServerAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new LlamaAdminRestartResultDto(
            Restarted: true,
            Termed: false,
            OldPid: null,
            NewPid: 1));

    private static ModelDownloadOperationDto CompletedDownload(string alias, string? operationId = null) =>
        new(
            OperationId: operationId ?? Guid.NewGuid().ToString("N"),
            Status: "completed",
            RouterModelId: alias,
            Progress: 1.0,
            ErrorMessage: null,
            LogLine: null);
}
