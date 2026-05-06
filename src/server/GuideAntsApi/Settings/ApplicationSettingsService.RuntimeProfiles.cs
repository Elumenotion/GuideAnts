using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using GuideAntsApi.Models.Settings;
using GuideAntsApi.Services.LlamaCpp;

namespace GuideAntsApi.Settings;

public sealed partial class ApplicationSettingsService
{
    public async Task<IReadOnlyList<SettingsRuntimeProfileDto>> GetRuntimeProfilesAsync(CancellationToken cancellationToken = default)
    {
        var profiles = await _db.RuntimeProfiles
            .AsNoTracking()
            .OrderBy(p => p.DisplayName)
            .ToListAsync(cancellationToken);

        return profiles.Select(ToSettingsRuntimeProfileDto).ToList();
    }

    public async Task<SettingsRuntimeProfileDto?> GetRuntimeProfileAsync(string profileId, CancellationToken cancellationToken = default)
    {
        var entity = await _db.RuntimeProfiles
            .AsNoTracking()
            .SingleOrDefaultAsync(p => p.ProfileId == profileId, cancellationToken);

        return entity == null ? null : ToSettingsRuntimeProfileDto(entity);
    }

    public async Task<SettingsRuntimeProfileDto> CreateRuntimeProfileAsync(CreateRuntimeProfileRequest request, CancellationToken cancellationToken = default)
    {
        var profileId = request.ProfileId.Trim();
        ValidateProfileId(profileId);

        var exists = await _db.RuntimeProfiles.AnyAsync(p => p.ProfileId == profileId, cancellationToken);
        if (exists)
        {
            throw new InvalidOperationException($"Runtime profile '{profileId}' already exists.");
        }

        ValidateRuntimeProfileJson(request.SamplingParametersJson, request.ThinkingControlJson);

        var entity = new DataModel.Models.RuntimeProfile
        {
            ProfileId = profileId,
            DisplayName = request.DisplayName.Trim(),
            Description = request.Description,
            CombineSystemAndDeveloperMessages = request.CombineSystemAndDeveloperMessages,
            ThoughtBlockPattern = request.ThoughtBlockPattern,
            SamplingParametersJson = request.SamplingParametersJson,
            ThinkingControlJson = request.ThinkingControlJson,
            Kind = request.Kind.Trim(),
            Created = DateTime.UtcNow,
            Updated = DateTime.UtcNow
        };

        _db.RuntimeProfiles.Add(entity);
        await _db.SaveChangesAsync(cancellationToken);
        _runtimeProfileResolver.InvalidateCache();
        return ToSettingsRuntimeProfileDto(entity);
    }

    public async Task<SettingsRuntimeProfileDto?> UpdateRuntimeProfileAsync(string profileId, UpdateRuntimeProfileRequest request, CancellationToken cancellationToken = default)
    {
        if (!string.Equals(profileId, request.ProfileId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Route profileId must match payload profileId.");
        }

        var entity = await _db.RuntimeProfiles.SingleOrDefaultAsync(p => p.ProfileId == profileId, cancellationToken);
        if (entity == null)
        {
            return null;
        }

        ValidateRuntimeProfileJson(request.SamplingParametersJson, request.ThinkingControlJson);

        entity.DisplayName = request.DisplayName.Trim();
        entity.Description = request.Description;
        entity.CombineSystemAndDeveloperMessages = request.CombineSystemAndDeveloperMessages;
        entity.ThoughtBlockPattern = request.ThoughtBlockPattern;
        entity.SamplingParametersJson = request.SamplingParametersJson;
        entity.ThinkingControlJson = request.ThinkingControlJson;
        entity.Kind = request.Kind.Trim();
        entity.Updated = DateTime.UtcNow;

        await _db.SaveChangesAsync(cancellationToken);
        _runtimeProfileResolver.InvalidateCache();
        return ToSettingsRuntimeProfileDto(entity);
    }

    public async Task<bool> DeleteRuntimeProfileAsync(string profileId, CancellationToken cancellationToken = default)
    {
        var entity = await _db.RuntimeProfiles.SingleOrDefaultAsync(p => p.ProfileId == profileId, cancellationToken);
        if (entity == null)
        {
            return false;
        }

        var referencedByModel = await _db.Models
            .AsNoTracking()
            .AnyAsync(m => m.RuntimeConfigJson != null && m.RuntimeConfigJson.Contains(profileId), cancellationToken);
        if (referencedByModel)
        {
            throw new InvalidOperationException(
                $"Cannot delete runtime profile '{profileId}' because it is referenced by one or more models.");
        }

        _db.RuntimeProfiles.Remove(entity);
        await _db.SaveChangesAsync(cancellationToken);
        _runtimeProfileResolver.InvalidateCache();
        return true;
    }

    private static SettingsRuntimeProfileDto ToSettingsRuntimeProfileDto(DataModel.Models.RuntimeProfile entity)
    {
        return new SettingsRuntimeProfileDto(
            entity.ProfileId,
            entity.DisplayName,
            entity.Description,
            entity.CombineSystemAndDeveloperMessages,
            entity.ThoughtBlockPattern,
            entity.SamplingParametersJson,
            entity.ThinkingControlJson,
            entity.Kind,
            entity.Created,
            entity.Updated);
    }

    private static void ValidateProfileId(string profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId))
        {
            throw new InvalidOperationException("Profile ID is required.");
        }

        if (profileId.Length > 64)
        {
            throw new InvalidOperationException("Profile ID must be 64 characters or fewer.");
        }

        if (!profileId.All(c => char.IsLetterOrDigit(c) || c == '_'))
        {
            throw new InvalidOperationException("Profile ID must contain only alphanumeric characters and underscores.");
        }
    }

    private static void ValidateRuntimeProfileJson(string samplingParametersJson, string thinkingControlJson)
    {
        try
        {
            var samplingParams = JsonSerializer.Deserialize<Dictionary<string, SamplingParameterDefinition>>(
                samplingParametersJson, RuntimeProfileJsonOptions);
            if (samplingParams != null)
            {
                foreach (var (key, param) in samplingParams)
                {
                    if (string.IsNullOrWhiteSpace(param.Key))
                        throw new InvalidOperationException($"Sampling parameter entry '{key}' has a blank key.");
                    if (param.Min > param.Max)
                        throw new InvalidOperationException($"Parameter '{key}': min ({param.Min}) cannot exceed max ({param.Max}).");
                    if (param.Default < param.Min || param.Default > param.Max)
                        throw new InvalidOperationException($"Parameter '{key}': default ({param.Default}) must be between min ({param.Min}) and max ({param.Max}).");
                }
            }
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("SamplingParametersJson is not valid JSON.", ex);
        }

        try
        {
            var tc = JsonSerializer.Deserialize<ThinkingControl>(thinkingControlJson, RuntimeProfileJsonOptions);
            if (tc != null)
            {
                if (!string.IsNullOrWhiteSpace(tc.DefaultChoice) && tc.ChoiceActions != null)
                {
                    if (!tc.ChoiceActions.ContainsKey(tc.DefaultChoice))
                    {
                        throw new InvalidOperationException(
                            $"ThinkingControl defaultChoice '{tc.DefaultChoice}' is not defined in choiceActions.");
                    }
                }
            }
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("ThinkingControlJson is not valid JSON.", ex);
        }
    }

    private static readonly JsonSerializerOptions RuntimeProfileJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
}
