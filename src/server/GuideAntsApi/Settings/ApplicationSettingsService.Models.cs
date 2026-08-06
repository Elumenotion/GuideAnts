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
        var normalizedRuntimeConfigJson = NormalizeRuntimeConfigJson(modelId, provider, request.RuntimeConfigJson);
        ValidateProviderReasoningChoices(modelId, provider, normalizedReasoningChoices);
        ValidateLlamaBehavior(modelId, provider, request.ThinkingControlJson);

        var model = new Model
        {
            ModelId = modelId,
            DisplayName = request.DisplayName.Trim(),
            Provider = provider,
            Description = request.Description,
            ReasoningChoicesJson = normalizedReasoningChoices,
            RuntimeConfigJson = normalizedRuntimeConfigJson,
            CombineSystemAndDeveloperMessages = request.CombineSystemAndDeveloperMessages,
            ThoughtBlockPattern = request.ThoughtBlockPattern,
            SamplingParametersJson = NormalizeJsonObject(request.SamplingParametersJson, "{}"),
            ThinkingControlJson = NormalizeBehaviorJsonObject(
                modelId,
                nameof(Model.ThinkingControlJson),
                request.ThinkingControlJson,
                "{}"),
            RequestFieldsWhenToolsPresentJson = NormalizeBehaviorJsonObject(
                modelId,
                nameof(Model.RequestFieldsWhenToolsPresentJson),
                request.RequestFieldsWhenToolsPresentJson,
                "{}"),
            IsActive = request.IsActive,
            DisplayOrder = request.DisplayOrder,
            Created = DateTime.UtcNow,
            Updated = DateTime.UtcNow
        };

        _db.Models.Add(model);
        await _db.SaveChangesAsync(cancellationToken);
        return ToSettingsModelDto(model);
    }

    public async Task<SettingsModelDto?> UpdateModelAsync(string modelId, UpdateSettingsModelRequest request, CancellationToken cancellationToken = default)
    {
        var routeModelId = (modelId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(routeModelId))
        {
            throw new InvalidOperationException("Route modelId is required.");
        }

        var model = await _db.Models.SingleOrDefaultAsync(x => x.ModelId == routeModelId, cancellationToken);
        if (model == null)
        {
            return null;
        }

        var provider = request.Provider.Trim();
        var normalizedReasoningChoices = NormalizeReasoningChoicesJson(routeModelId, request.ReasoningChoicesJson);
        var normalizedRuntimeConfigJson = NormalizeRuntimeConfigJson(routeModelId, provider, request.RuntimeConfigJson);
        ValidateProviderReasoningChoices(routeModelId, provider, normalizedReasoningChoices);
        ValidateLlamaBehavior(routeModelId, provider, request.ThinkingControlJson);

        model.DisplayName = request.DisplayName.Trim();
        model.Provider = provider;
        model.Description = request.Description;
        model.ReasoningChoicesJson = normalizedReasoningChoices;
        model.RuntimeConfigJson = normalizedRuntimeConfigJson;
        model.CombineSystemAndDeveloperMessages = request.CombineSystemAndDeveloperMessages;
        model.ThoughtBlockPattern = request.ThoughtBlockPattern;
        model.SamplingParametersJson = NormalizeJsonObject(request.SamplingParametersJson, model.SamplingParametersJson);
        model.ThinkingControlJson = NormalizeBehaviorJsonObject(
            routeModelId,
            nameof(Model.ThinkingControlJson),
            request.ThinkingControlJson,
            model.ThinkingControlJson);
        model.RequestFieldsWhenToolsPresentJson = NormalizeBehaviorJsonObject(
            routeModelId,
            nameof(Model.RequestFieldsWhenToolsPresentJson),
            request.RequestFieldsWhenToolsPresentJson,
            model.RequestFieldsWhenToolsPresentJson);
        model.IsActive = request.IsActive;
        model.DisplayOrder = request.DisplayOrder;
        model.Updated = DateTime.UtcNow;

        await _db.SaveChangesAsync(cancellationToken);
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
            .Select(x => new { x.ModelId, x.DisplayName, x.Provider, x.IsActive, x.RuntimeConfigJson })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return models
            .Select(m => new ChatTargetDto(
                ModelId: m.ModelId,
                DisplayName: m.DisplayName,
                Provider: m.Provider,
                IsActive: m.IsActive,
                HasLocalRuntime: !string.IsNullOrWhiteSpace(m.RuntimeConfigJson)))
            .ToList();
    }

    private string? NormalizeRuntimeConfigJson(string modelId, string provider, string? runtimeConfigJson)
    {
        if (!string.Equals(provider, "llama-cpp", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(runtimeConfigJson))
            {
                return null;
            }

            if (ContainsRuntimeProfilePointer(runtimeConfigJson))
            {
                throw new InvalidOperationException(
                    $"Model '{modelId}' cannot persist runtimeProfileId for provider '{provider}'. Configure SamplingParametersJson and ReasoningChoicesJson on the model row instead.");
            }

            return runtimeConfigJson.Trim();
        }

        var parsed = LocalRuntimeConfigurationParser.ParseRequired(modelId, runtimeConfigJson);
        return LocalRuntimeConfigurationParser.SerializeCanonical(parsed);
    }

    private static bool ContainsRuntimeProfilePointer(string runtimeConfigJson)
    {
        try
        {
            using var document = JsonDocument.Parse(runtimeConfigJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Name.Equals("runtimeProfileId", StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(property.Value.GetString()))
                {
                    return true;
                }
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return false;
    }

    private static void ValidateLlamaBehavior(string modelId, string provider, string thinkingControlJson)
    {
        if (!string.Equals(provider, "llama-cpp", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(thinkingControlJson)
            || string.Equals(thinkingControlJson.Trim(), "{}", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Model '{modelId}' is configured as llama-cpp but is missing ThinkingControlJson.");
        }
    }

    private static string NormalizeJsonObject(string? json, string fallback)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return fallback;
        }

        return json.Trim();
    }

    /// <summary>
    /// Chat-behavior columns are replayed into request bodies at dispatch, where malformed JSON can
    /// only be ignored. Reject it on write instead so a bad paste fails visibly in Settings.
    /// </summary>
    private static string NormalizeBehaviorJsonObject(string modelId, string field, string? json, string fallback)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return fallback;
        }

        var trimmed = json.Trim();
        JsonValueKind kind;
        try
        {
            using var document = JsonDocument.Parse(trimmed);
            kind = document.RootElement.ValueKind;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Model '{modelId}' {field} must be valid JSON.", ex);
        }

        if (kind != JsonValueKind.Object)
        {
            throw new InvalidOperationException($"Model '{modelId}' {field} must be a JSON object.");
        }

        return trimmed;
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
            model.RuntimeConfigJson,
            model.IsActive,
            model.DisplayOrder,
            model.Created,
            model.Updated,
            model.CombineSystemAndDeveloperMessages,
            model.ThoughtBlockPattern,
            model.SamplingParametersJson,
            model.ThinkingControlJson,
            model.RequestFieldsWhenToolsPresentJson);
    }
}
