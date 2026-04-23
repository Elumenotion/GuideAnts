using System.Collections.Concurrent;
using System.Text.Json;
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
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

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

        var samplingParams = DeserializeSamplingParameters(entity.SamplingParametersJson, profileId);
        var thinkingControl = DeserializeThinkingControl(entity.ThinkingControlJson, profileId);

        var data = new RuntimeProfileData(
            entity.ProfileId,
            entity.CombineSystemAndDeveloperMessages,
            entity.ThoughtBlockPattern,
            samplingParams,
            thinkingControl);

        _cache[profileId] = (data, DateTime.UtcNow);
        return data;
    }

    public void InvalidateCache()
    {
        _cache.Clear();
    }

    private static IReadOnlyDictionary<string, SamplingParameterDefinition> DeserializeSamplingParameters(
        string json, string profileId)
    {
        try
        {
            var dict = JsonSerializer.Deserialize<Dictionary<string, SamplingParameterDefinition>>(json, JsonOptions);
            return dict ?? new Dictionary<string, SamplingParameterDefinition>();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Failed to deserialize SamplingParametersJson for profile '{profileId}'.", ex);
        }
    }

    private static ThinkingControl DeserializeThinkingControl(string json, string profileId)
    {
        try
        {
            var tc = JsonSerializer.Deserialize<ThinkingControl>(json, JsonOptions);
            if (tc == null)
            {
                return new ThinkingControl("enabled", new Dictionary<string, IReadOnlyList<ThinkingAction>>());
            }
            return tc;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Failed to deserialize ThinkingControlJson for profile '{profileId}'.", ex);
        }
    }
}
