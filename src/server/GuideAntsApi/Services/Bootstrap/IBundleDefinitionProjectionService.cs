namespace GuideAntsApi.Services.Bootstrap;

public interface IBundleDefinitionProjectionService
{
    Task<BundleDefinitionProjectionReport> ProjectAllAsync(CancellationToken cancellationToken = default);

    Task ProjectBundleAsync(string bundleId, CancellationToken cancellationToken = default);
}

public sealed record BundleDefinitionProjectionReport(
    int Projected,
    int Failed,
    IReadOnlyList<string> FailedBundleIds);
