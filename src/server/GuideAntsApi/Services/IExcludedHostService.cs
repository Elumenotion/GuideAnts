namespace GuideAntsApi.Services;

public interface IExcludedHostService
{
    Task<HashSet<string>> GetExcludedHostsAsync(CancellationToken cancellationToken = default);
    bool IsHostExcluded(string host, IReadOnlySet<string> excludedHosts);
    string? NormalizeHost(string? hostOrUrl);
    Task<bool> TryAddExcludedHostAsync(string? hostOrUrl, string? reason, CancellationToken cancellationToken = default);
}
