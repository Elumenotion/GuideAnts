namespace GuideAntsApi.Services;

internal static class ReadWebHostPolicy
{
    internal const string ExcludedHostMessage =
        "Host is blocked due to prior failures. Do not fetch from this host — use search or local files.";

    private static readonly HashSet<string> AutoExclusionProtectedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "github.com",
        "api.github.com",
        "raw.githubusercontent.com",
        "gist.github.com",
    };

    internal static bool IsAutoExclusionProtected(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return false;

        if (AutoExclusionProtectedHosts.Contains(host))
            return true;

        return host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase) ||
               host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool ShouldHonorExcludedHost(string host, IExcludedHostService excludedHostService, IReadOnlySet<string> excludedHosts) =>
        !IsAutoExclusionProtected(host) && excludedHostService.IsHostExcluded(host, excludedHosts);

    internal static bool ShouldAutoExcludeHost(string host, bool accessDenied) =>
        accessDenied && !IsAutoExclusionProtected(host);
}
