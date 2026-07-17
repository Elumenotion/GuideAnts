namespace GuideAntsApi.Services.Bootstrap;

public interface IBundleDefinitionMigrationService
{
    Task<BundleDefinitionMigrationReport> MigrateAsync(CancellationToken cancellationToken = default);
}

public sealed record BundleDefinitionMigrationReport(
    int DefaultsDiscovered,
    int DefaultsImported,
    int RuntimeDiscovered,
    int RuntimeImported,
    int SamplingBackfilled,
    int SkippedExisting,
    int Failed);
