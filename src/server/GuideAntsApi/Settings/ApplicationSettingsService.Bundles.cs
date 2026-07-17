using System.Text.Json;
using System.Text.Json.Nodes;
using GuideAntsApi.Models.Settings;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.Settings;

public sealed partial class ApplicationSettingsService
{
    public async Task<IReadOnlyList<ImageGenerationBundleDefinitionDto>> GetImageGenerationBundleDefinitionsAsync(
        CancellationToken cancellationToken = default)
    {
        var payload = await LoadImageGenerationBundlesPayloadAsync(cancellationToken);
        return payload.Values
            .OrderBy(x => x.BundleId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<ImageGenerationBundleDefinitionDto?> GetImageGenerationBundleDefinitionAsync(
        string bundleId,
        CancellationToken cancellationToken = default)
    {
        var payload = await LoadImageGenerationBundlesPayloadAsync(cancellationToken);
        return payload.TryGetValue(bundleId, out var definition) ? definition : null;
    }

    public async Task<ImageGenerationBundleDefinitionDto> UpsertImageGenerationBundleDefinitionAsync(
        ImageGenerationBundleDefinitionDto definition,
        CancellationToken cancellationToken = default)
    {
        var errors = BundleDefinitionValidator.Validate(definition);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(string.Join(' ', errors));
        }

        var normalized = definition with
        {
            BundleId = definition.BundleId.Trim(),
            UpdatedAtUtc = DateTime.UtcNow.ToString("O"),
        };

        var payload = await LoadImageGenerationBundlesPayloadAsync(cancellationToken);
        payload[normalized.BundleId] = normalized;
        await PersistImageGenerationBundlesPayloadAsync(payload, cancellationToken);
        return normalized;
    }

    public async Task ReplaceImageGenerationBundleDefinitionsAsync(
        IReadOnlyList<ImageGenerationBundleDefinitionDto> definitions,
        CancellationToken cancellationToken = default)
    {
        var payload = new Dictionary<string, ImageGenerationBundleDefinitionDto>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in definitions)
        {
            var errors = BundleDefinitionValidator.Validate(definition);
            if (errors.Count > 0)
            {
                throw new InvalidOperationException(string.Join(' ', errors));
            }

            var normalized = definition with
            {
                BundleId = definition.BundleId.Trim(),
                UpdatedAtUtc = string.IsNullOrWhiteSpace(definition.UpdatedAtUtc)
                    ? DateTime.UtcNow.ToString("O")
                    : definition.UpdatedAtUtc,
            };
            payload[normalized.BundleId] = normalized;
        }

        await PersistImageGenerationBundlesPayloadAsync(payload, cancellationToken);
    }

    internal async Task<Dictionary<string, ImageGenerationBundleDefinitionDto>> LoadImageGenerationBundlesPayloadAsync(
        CancellationToken cancellationToken = default)
    {
        var row = await LoadOrCreateImageGenerationBundlesRowAsync(cancellationToken);
        var json = ApplicationSettingsJson.DeserializeObject(row.JsonValue);
        if (!json.TryGetPropertyValue(ImageGenerationBundleDefinitionContracts.BundlesPayloadProperty, out var bundlesNode)
            || bundlesNode is not JsonObject bundlesObject)
        {
            return new Dictionary<string, ImageGenerationBundleDefinitionDto>(StringComparer.OrdinalIgnoreCase);
        }

        var result = new Dictionary<string, ImageGenerationBundleDefinitionDto>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in bundlesObject)
        {
            if (property.Value is null)
            {
                continue;
            }

            var definition = JsonSerializer.Deserialize<ImageGenerationBundleDefinitionDto>(
                property.Value.ToJsonString(),
                BundleDefinitionJson.Options);
            if (definition is null || string.IsNullOrWhiteSpace(definition.BundleId))
            {
                continue;
            }

            result[definition.BundleId] = definition;
        }

        return result;
    }

    internal async Task PersistImageGenerationBundlesPayloadAsync(
        Dictionary<string, ImageGenerationBundleDefinitionDto> bundles,
        CancellationToken cancellationToken = default)
    {
        if (!_registry.TryGet(ImageGenerationBundleDefinitionContracts.SettingsSectionName, out var definition))
        {
            throw new InvalidOperationException(
                $"Settings section '{ImageGenerationBundleDefinitionContracts.SettingsSectionName}' is not registered.");
        }

        var row = await LoadOrCreateImageGenerationBundlesRowAsync(cancellationToken);
        var bundlesObject = new JsonObject();
        foreach (var bundle in bundles.Values.OrderBy(x => x.BundleId, StringComparer.OrdinalIgnoreCase))
        {
            bundlesObject[bundle.BundleId] = BundleDefinitionJson.ToJsonObject(bundle);
        }

        var payload = new JsonObject
        {
            [ImageGenerationBundleDefinitionContracts.BundlesPayloadProperty] = bundlesObject,
        };

        row.JsonValue = ApplicationSettingsJson.Serialize(payload);
        row.UpdatedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        ReloadConfiguration();
    }

    private async Task<DataModel.Models.ApplicationSetting> LoadOrCreateImageGenerationBundlesRowAsync(
        CancellationToken cancellationToken)
    {
        if (!_registry.TryGet(ImageGenerationBundleDefinitionContracts.SettingsSectionName, out var definition))
        {
            throw new InvalidOperationException(
                $"Settings section '{ImageGenerationBundleDefinitionContracts.SettingsSectionName}' is not registered.");
        }

        var row = await _db.ApplicationSettings
            .SingleOrDefaultAsync(
                x => x.SectionName == definition.SectionName,
                cancellationToken);

        if (row is not null)
        {
            return row;
        }

        var payload = new JsonObject
        {
            [ImageGenerationBundleDefinitionContracts.BundlesPayloadProperty] = new JsonObject(),
        };

        row = new DataModel.Models.ApplicationSetting
        {
            SectionName = definition.SectionName,
            SchemaVersion = definition.SchemaVersion,
            JsonValue = ApplicationSettingsJson.Serialize(payload),
            UpdatedUtc = DateTime.UtcNow,
        };
        _db.ApplicationSettings.Add(row);
        await _db.SaveChangesAsync(cancellationToken);
        return row;
    }
}
