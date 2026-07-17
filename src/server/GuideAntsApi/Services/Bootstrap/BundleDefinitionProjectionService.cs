using System.Net.Http.Json;
using GuideAntsApi.Endpoints;
using GuideAntsApi.Endpoints.Settings;
using GuideAntsApi.Models.Settings;
using GuideAntsApi.Settings;

namespace GuideAntsApi.Services.Bootstrap;

public sealed class BundleDefinitionProjectionService : IBundleDefinitionProjectionService
{
    private readonly IApplicationSettingsService _settingsService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<BundleDefinitionProjectionService> _logger;

    public BundleDefinitionProjectionService(
        IApplicationSettingsService settingsService,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<BundleDefinitionProjectionService> logger)
    {
        _settingsService = settingsService;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<BundleDefinitionProjectionReport> ProjectAllAsync(CancellationToken cancellationToken = default)
    {
        var definitions = await _settingsService.GetImageGenerationBundleDefinitionsAsync(cancellationToken);
        var projected = 0;
        var failed = 0;
        var failedBundleIds = new List<string>();

        foreach (var definition in definitions)
        {
            try
            {
                await ProjectDefinitionAsync(definition, cancellationToken).ConfigureAwait(false);
                projected++;
            }
            catch (Exception ex)
            {
                failed++;
                failedBundleIds.Add(definition.BundleId);
                _logger.LogError(
                    ex,
                    "Failed to project ImageGeneration bundle definition for {BundleId}.",
                    definition.BundleId);
            }
        }

        return new BundleDefinitionProjectionReport(projected, failed, failedBundleIds);
    }

    public Task ProjectBundleAsync(string bundleId, CancellationToken cancellationToken = default)
    {
        return ProjectDefinitionByIdAsync(bundleId, cancellationToken);
    }

    private async Task ProjectDefinitionByIdAsync(string bundleId, CancellationToken cancellationToken)
    {
        var definition = await _settingsService.GetImageGenerationBundleDefinitionAsync(bundleId, cancellationToken)
            ?? throw new InvalidOperationException(
                $"ImageGeneration bundle definition '{bundleId}' was not found in API-owned settings.");

        await ProjectDefinitionAsync(definition, cancellationToken).ConfigureAwait(false);
    }

    private async Task ProjectDefinitionAsync(
        ImageGenerationBundleDefinitionDto definition,
        CancellationToken cancellationToken)
    {
        var adminBase = LocalServiceAdminRouting.ResolveAdminBase("ImageGeneration", _configuration)
            ?? throw new InvalidOperationException(
                "ImageGeneration local service host is not configured; cannot project bundle definitions.");

        var encodedBundleId = Uri.EscapeDataString(definition.BundleId);
        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            $"{adminBase}/admin/bundles/{encodedBundleId}/definition")
        {
            Content = JsonContent.Create(
                new
                {
                    revision = definition.Revision,
                    roles = new
                    {
                        diffusion = new { repo = definition.Roles.Diffusion.Repo, file = definition.Roles.Diffusion.File },
                        vae = new { repo = definition.Roles.Vae.Repo, file = definition.Roles.Vae.File },
                        textEncoder = new
                        {
                            repo = definition.Roles.TextEncoder.Repo,
                            file = definition.Roles.TextEncoder.File,
                        },
                    },
                    sampling = new
                    {
                        steps = definition.Sampling.Steps,
                        cfgScale = definition.Sampling.CfgScale,
                        samplingMethod = definition.Sampling.SamplingMethod,
                    },
                },
                options: BundleDefinitionJson.Options),
        };

        using var response = await _httpClientFactory.CreateClient()
            .SendAsync(request, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"SD bundle definition projection failed for '{definition.BundleId}' with status {(int)response.StatusCode}: {body}");
        }
    }
}
