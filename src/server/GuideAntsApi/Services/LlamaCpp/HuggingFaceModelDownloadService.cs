using GuideAntsApi.Configuration;
using GuideAntsApi.Models.Settings;
using GuideAntsApi.Services.HuggingFace;
using Microsoft.Extensions.Options;

namespace GuideAntsApi.Services.LlamaCpp;

public interface IHuggingFaceModelDownloadService
{
    Task<ModelDownloadOperationDto> StartDownloadAsync(StartModelDownloadRequest request, CancellationToken cancellationToken = default);

    Task<ModelDownloadOperationDto?> GetOperationStatusAsync(string operationId, CancellationToken cancellationToken = default);
}

/// <summary>
/// API-side adapter: delegates Hugging Face download + router registration work
/// to the guideants-ai runtime admin service, so the web API never requires
/// direct access to model-storage volumes.
/// </summary>
public sealed class HuggingFaceModelDownloadService : IHuggingFaceModelDownloadService
{
    private readonly ILlamaRuntimeAdminClient _adminClient;
    private readonly IHuggingFaceTokenResolver _tokenResolver;
    private readonly IOptionsMonitor<LlamaModelManagementOptions> _options;
    private readonly ILogger<HuggingFaceModelDownloadService> _logger;

    public HuggingFaceModelDownloadService(
        ILlamaRuntimeAdminClient adminClient,
        IHuggingFaceTokenResolver tokenResolver,
        IOptionsMonitor<LlamaModelManagementOptions> options,
        ILogger<HuggingFaceModelDownloadService> logger)
    {
        _adminClient = adminClient;
        _tokenResolver = tokenResolver;
        _options = options;
        _logger = logger;
    }

    public async Task<ModelDownloadOperationDto> StartDownloadAsync(
        StartModelDownloadRequest request,
        CancellationToken cancellationToken = default)
    {
        var options = _options.CurrentValue;
        var resolvedHfToken = _tokenResolver.Resolve();
        try
        {
            return await _adminClient
                .StartDownloadAsync(
                    request,
                    resolvedHfToken: resolvedHfToken,
                    allowOverwrite: options.AllowOverwrite,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to start delegated llama download for alias {Alias} from repo {Repo}.",
                request.RouterModelId,
                request.Repository);
            throw;
        }
    }

    public async Task<ModelDownloadOperationDto?> GetOperationStatusAsync(
        string operationId,
        CancellationToken cancellationToken = default)
    {
        return await _adminClient.GetDownloadStatusAsync(operationId, cancellationToken).ConfigureAwait(false);
    }
}
