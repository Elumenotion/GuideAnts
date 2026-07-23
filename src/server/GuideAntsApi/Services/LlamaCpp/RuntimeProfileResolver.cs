using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using GuideAntsApi.DataModel;

namespace GuideAntsApi.Services.LlamaCpp;

public interface IRuntimeProfileResolver
{
    Task<RuntimeProfileData> ResolveAsync(string profileId, CancellationToken ct = default);
    void InvalidateCache();
}

public sealed class RuntimeProfileResolver : IRuntimeProfileResolver
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ConcurrentDictionary<string, (RuntimeProfileData Data, DateTime LoadedAt)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    public RuntimeProfileResolver(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public async Task<RuntimeProfileData> ResolveAsync(string profileId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(profileId))
        {
            throw new InvalidOperationException("Runtime profile id is required.");
        }

        if (_cache.TryGetValue(profileId, out var cached) && DateTime.UtcNow - cached.LoadedAt < CacheTtl)
        {
            return cached.Data;
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var entity = await db.RuntimeProfiles
            .AsNoTracking()
            .SingleOrDefaultAsync(p => p.ProfileId == profileId, ct);

        if (entity == null)
        {
            throw new InvalidOperationException(
                $"Runtime profile '{profileId}' not found. Create it in Settings > Runtime Profiles.");
        }

        var data = RuntimeProfileDataJson.FromJsonStrings(
            entity.ProfileId,
            entity.CombineSystemAndDeveloperMessages,
            entity.ThoughtBlockPattern,
            entity.SamplingParametersJson,
            entity.ThinkingControlJson,
            entity.RequestFieldsWhenToolsPresentJson,
            entity.DisplayName,
            entity.Description);

        _cache[profileId] = (data, DateTime.UtcNow);
        return data;
    }

    public void InvalidateCache()
    {
        _cache.Clear();
    }

}
