using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.Services;

public sealed class ExcludedHostService(ApplicationDbContext context, ILogger<ExcludedHostService> logger) : IExcludedHostService
{
    private readonly ApplicationDbContext _context = context ?? throw new ArgumentNullException(nameof(context));
    private readonly ILogger<ExcludedHostService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public async Task<HashSet<string>> GetExcludedHostsAsync(CancellationToken cancellationToken = default)
    {
        var hosts = await _context.ExcludedHosts
            .AsNoTracking()
            .Where(x => x.Host != string.Empty)
            .Select(x => x.Host)
            .ToListAsync(cancellationToken);

        return new HashSet<string>(
            hosts
                .Select(NormalizeHost)
                .Where(x => !string.IsNullOrWhiteSpace(x))!,
            StringComparer.OrdinalIgnoreCase);
    }

    public bool IsHostExcluded(string host, IReadOnlySet<string> excludedHosts)
    {
        if (string.IsNullOrWhiteSpace(host) || excludedHosts.Count == 0)
            return false;

        var normalizedHost = NormalizeHost(host);
        if (string.IsNullOrWhiteSpace(normalizedHost))
            return false;

        if (excludedHosts.Contains(normalizedHost))
            return true;

        foreach (var excluded in excludedHosts)
        {
            if (normalizedHost.EndsWith('.' + excluded, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public string? NormalizeHost(string? hostOrUrl)
    {
        if (string.IsNullOrWhiteSpace(hostOrUrl))
            return null;

        var trimmed = hostOrUrl.Trim().ToLowerInvariant();

        if (trimmed.StartsWith("http://", StringComparison.Ordinal) ||
            trimmed.StartsWith("https://", StringComparison.Ordinal))
        {
            if (Uri.TryCreate(trimmed, UriKind.Absolute, out var absoluteUri) &&
                !string.IsNullOrWhiteSpace(absoluteUri.Host))
            {
                return absoluteUri.Host.Trim().ToLowerInvariant();
            }
        }

        if (trimmed.Contains('/'))
            trimmed = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries)[0].Trim();

        if (Uri.TryCreate($"http://{trimmed}", UriKind.Absolute, out var uriWithScheme) &&
            !string.IsNullOrWhiteSpace(uriWithScheme.Host))
        {
            return uriWithScheme.Host.Trim().ToLowerInvariant();
        }

        if (trimmed.Contains(':'))
            trimmed = trimmed.Split(':', StringSplitOptions.RemoveEmptyEntries)[0].Trim();

        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    public async Task<bool> TryAddExcludedHostAsync(string? hostOrUrl, string? reason, CancellationToken cancellationToken = default)
    {
        var normalizedHost = NormalizeHost(hostOrUrl);
        if (string.IsNullOrWhiteSpace(normalizedHost))
            return false;

        if (await _context.ExcludedHosts.AnyAsync(x => x.Host == normalizedHost, cancellationToken))
            return false;

        _context.ExcludedHosts.Add(new ExcludedHost
        {
            Host = normalizedHost,
            Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
            Created = DateTime.UtcNow,
            Updated = DateTime.UtcNow
        });

        try
        {
            await _context.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Added excluded host {Host}.", normalizedHost);
            return true;
        }
        catch (DbUpdateException ex)
        {
            _logger.LogInformation(ex, "Excluded host {Host} was already present.", normalizedHost);
            return false;
        }
    }
}
