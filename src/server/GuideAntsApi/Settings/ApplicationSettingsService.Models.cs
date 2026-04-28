using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models.Settings;
using GuideAntsApi.Services.LlamaCpp;

namespace GuideAntsApi.Settings;

public sealed partial class ApplicationSettingsService
{
    public async Task<IReadOnlyList<SettingsModelDto>> GetModelsAsync(CancellationToken cancellationToken = default)
    {
        var models = await _db.Models
            .AsNoTracking()
            .OrderBy(x => x.DisplayOrder)
            .ThenBy(x => x.ModelId)
            .ToListAsync(cancellationToken);

        return models.Select(ToSettingsModelDto).ToList();
    }

    public async Task<SettingsModelDto> CreateModelAsync(CreateSettingsModelRequest request, CancellationToken cancellationToken = default)
    {
        var modelId = request.ModelId.Trim();
        var provider = request.Provider.Trim();

        var exists = await _db.Models.AnyAsync(x => x.ModelId == modelId, cancellationToken);
        if (exists)
        {
            throw new InvalidOperationException($"Model '{request.ModelId}' already exists.");
        }

        var normalizedReasoningChoices = NormalizeReasoningChoicesJson(modelId, request.ReasoningChoicesJson);
        var normalizedLocalRuntimeJson = NormalizeLocalRuntimeJson(modelId, provider, request.LocalRuntimeJson);
        ValidateProviderReasoningChoices(modelId, provider, normalizedReasoningChoices);

        var model = new Model
        {
            ModelId = modelId,
            DisplayName = request.DisplayName.Trim(),
            Provider = provider,
            Description = request.Description,
            ReasoningChoicesJson = normalizedReasoningChoices,
            LocalRuntimeJson = normalizedLocalRuntimeJson,
            IsActive = request.IsActive,
            DisplayOrder = request.DisplayOrder,
            Created = DateTime.UtcNow,
            Updated = DateTime.UtcNow
        };

        _db.Models.Add(model);
        await _db.SaveChangesAsync(cancellationToken);
        await TrySyncRouterIniAfterLlamaModelPersistAsync(modelId, provider, model.LocalRuntimeJson, cancellationToken)
            .ConfigureAwait(false);
        return ToSettingsModelDto(model);
    }

    public async Task<SettingsModelDto?> UpdateModelAsync(string modelId, UpdateSettingsModelRequest request, CancellationToken cancellationToken = default)
    {
        if (!string.Equals(modelId, request.ModelId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Route modelId must match payload modelId.");
        }

        var model = await _db.Models.SingleOrDefaultAsync(x => x.ModelId == modelId, cancellationToken);
        if (model == null)
        {
            return null;
        }

        var provider = request.Provider.Trim();
        var normalizedReasoningChoices = NormalizeReasoningChoicesJson(request.ModelId.Trim(), request.ReasoningChoicesJson);
        var normalizedLocalRuntimeJson = NormalizeLocalRuntimeJson(request.ModelId.Trim(), provider, request.LocalRuntimeJson);
        ValidateProviderReasoningChoices(request.ModelId.Trim(), provider, normalizedReasoningChoices);

        model.DisplayName = request.DisplayName.Trim();
        model.Provider = provider;
        model.Description = request.Description;
        model.ReasoningChoicesJson = normalizedReasoningChoices;
        model.LocalRuntimeJson = normalizedLocalRuntimeJson;
        model.IsActive = request.IsActive;
        model.DisplayOrder = request.DisplayOrder;
        model.Updated = DateTime.UtcNow;

        await _db.SaveChangesAsync(cancellationToken);
        await TrySyncRouterIniAfterLlamaModelPersistAsync(modelId, provider, model.LocalRuntimeJson, cancellationToken)
            .ConfigureAwait(false);
        return ToSettingsModelDto(model);
    }

    public async Task<bool> DeleteModelAsync(string modelId, CancellationToken cancellationToken = default)
    {
        var model = await _db.Models.SingleOrDefaultAsync(x => x.ModelId == modelId, cancellationToken);
        if (model == null)
        {
            return false;
        }

        _db.Models.Remove(model);
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<ChatTargetDto>> GetChatTargetsAsync(CancellationToken cancellationToken = default)
    {
        var models = await _db.Models
            .AsNoTracking()
            .OrderBy(x => x.DisplayOrder)
            .ThenBy(x => x.ModelId)
            .Select(x => new { x.ModelId, x.DisplayName, x.Provider, x.IsActive, x.LocalRuntimeJson })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return models
            .Select(m => new ChatTargetDto(
                ModelId: m.ModelId,
                DisplayName: m.DisplayName,
                Provider: m.Provider,
                IsActive: m.IsActive,
                HasLocalRuntime: !string.IsNullOrWhiteSpace(m.LocalRuntimeJson)))
            .ToList();
    }

    private string? NormalizeLocalRuntimeJson(string modelId, string provider, string? localRuntimeJson)
    {
        if (!string.Equals(provider, "llama-cpp", StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrWhiteSpace(localRuntimeJson) ? null : localRuntimeJson.Trim();
        }

        var parsed = LocalRuntimeConfigurationParser.ParseRequired(modelId, localRuntimeJson);
        _runtimeProfileResolver.ResolveAsync(parsed.RuntimeProfileId).GetAwaiter().GetResult();
        return LocalRuntimeConfigurationParser.SerializeCanonical(parsed);
    }

    private async Task TrySyncRouterIniAfterLlamaModelPersistAsync(
        string modelId, string provider, string? localRuntimeJson, CancellationToken cancellationToken)
    {
        if (_llamaRouterIniSync is null
            || !string.Equals(provider, "llama-cpp", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(localRuntimeJson))
        {
            return;
        }

        var parsed = LocalRuntimeConfigurationParser.Parse(modelId, localRuntimeJson);
        await _llamaRouterIniSync.TrySyncFromLocalRuntimeAsync(parsed, cancellationToken).ConfigureAwait(false);
    }

    private static string? NormalizeReasoningChoicesJson(string modelId, string? reasoningChoicesJson)
    {
        if (string.IsNullOrWhiteSpace(reasoningChoicesJson))
        {
            return null;
        }

        List<string>? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<List<string>>(reasoningChoicesJson);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Model '{modelId}' ReasoningChoicesJson must be a JSON array of strings.", ex);
        }

        if (parsed == null)
        {
            return null;
        }

        var normalized = parsed
            .Where(choice => !string.IsNullOrWhiteSpace(choice))
            .Select(choice => choice.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return JsonSerializer.Serialize(normalized);
    }

    private void ValidateProviderReasoningChoices(
        string modelId,
        string provider,
        string? reasoningChoicesJson)
    {
        if (!string.Equals(provider, "anthropic", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(reasoningChoicesJson))
        {
            return;
        }

        var choices = JsonSerializer.Deserialize<List<string>>(reasoningChoicesJson) ?? [];
        if (choices.Count == 0)
        {
            return;
        }

        foreach (var choice in choices)
        {
            if (!string.IsNullOrWhiteSpace(choice))
            {
                continue;
            }

            throw new InvalidOperationException(
                $"Anthropic model '{modelId}' declares an empty reasoning choice.");
        }
    }

    private static SettingsModelDto ToSettingsModelDto(Model model)
    {
        return new SettingsModelDto(
            model.ModelId,
            model.DisplayName,
            model.Provider,
            model.Description,
            model.ReasoningChoicesJson,
            model.LocalRuntimeJson,
            model.IsActive,
            model.DisplayOrder,
            model.Created,
            model.Updated);
    }
}
