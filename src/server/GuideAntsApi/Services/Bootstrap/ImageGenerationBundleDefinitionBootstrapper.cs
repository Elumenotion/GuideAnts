using GuideAntsApi.Services.Bootstrap;

namespace GuideAntsApi.Services.Bootstrap;

public interface IImageGenerationBundleDefinitionBootstrapper
{
    Task MigrateAsync(CancellationToken cancellationToken = default);

    Task ProjectAsync(CancellationToken cancellationToken = default);
}

public sealed class ImageGenerationBundleDefinitionBootstrapper : IImageGenerationBundleDefinitionBootstrapper
{
    private readonly IBundleDefinitionMigrationService _migrationService;
    private readonly IBundleDefinitionProjectionService _projectionService;
    private readonly ILogger<ImageGenerationBundleDefinitionBootstrapper> _logger;

    public ImageGenerationBundleDefinitionBootstrapper(
        IBundleDefinitionMigrationService migrationService,
        IBundleDefinitionProjectionService projectionService,
        ILogger<ImageGenerationBundleDefinitionBootstrapper> logger)
    {
        _migrationService = migrationService;
        _projectionService = projectionService;
        _logger = logger;
    }

    public Task MigrateAsync(CancellationToken cancellationToken = default) =>
        _migrationService.MigrateAsync(cancellationToken);

    public async Task ProjectAsync(CancellationToken cancellationToken = default)
    {
        var projectionReport = await _projectionService.ProjectAllAsync(cancellationToken).ConfigureAwait(false);
        if (projectionReport.Failed > 0)
        {
            throw new InvalidOperationException(
                $"ImageGeneration bundle-definition projection failed for {projectionReport.Failed} bundle(s): {string.Join(", ", projectionReport.FailedBundleIds)}");
        }

        _logger.LogInformation(
            "ImageGeneration bundle-definition projection completed for {Projected} bundle(s).",
            projectionReport.Projected);
    }
}
