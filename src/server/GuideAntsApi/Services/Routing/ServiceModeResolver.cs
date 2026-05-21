using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using GuideAntsApi.DataModel;
using GuideAntsApi.Settings;

namespace GuideAntsApi.Services.Routing;

public sealed class ServiceModeResolver : IServiceModeResolver
{
    public const string SectionName = "ServiceModes";
    public const string ModesProperty = "Modes";

    private readonly IServiceScopeFactory _scopeFactory;

    public ServiceModeResolver(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    }

    public async Task<ServiceMode> ResolveAsync(string serviceName, string? modeId, CancellationToken cancellationToken = default)
    {
        var modes = await GetModesAsync(serviceName, cancellationToken).ConfigureAwait(false);

        if (modes.Count == 0)
        {
            throw new RoutingException(
                RoutingErrorCodes.ModeNotFound,
                $"Service '{serviceName}' has no modes configured.",
                action: $"Open Settings → Routing → {serviceName} and add at least one enabled default mode.",
                serviceId: serviceName,
                modeId: modeId);
        }

        ServiceMode? selected;
        if (string.IsNullOrWhiteSpace(modeId))
        {
            selected = modes.FirstOrDefault(m => m.IsDefault && m.Enabled)
                       ?? throw new RoutingException(
                           RoutingErrorCodes.ModeNotFound,
                           $"Service '{serviceName}' has no enabled default mode.",
                           action: $"Open Settings → Routing → {serviceName} and mark one enabled mode as the default.",
                           serviceId: serviceName);
        }
        else
        {
            selected = modes.FirstOrDefault(m => string.Equals(m.ModeId, modeId, StringComparison.Ordinal))
                       ?? throw RoutingException.ModeNotFound(serviceName, modeId);
            if (!selected.Enabled)
            {
                throw new RoutingException(
                    RoutingErrorCodes.ModeNotFound,
                    $"Service '{serviceName}' mode '{modeId}' is disabled.",
                    action: $"Enable mode '{modeId}' on Settings → Routing → {serviceName}, or request a different mode id.",
                    serviceId: serviceName,
                    modeId: modeId);
            }
        }

        return selected;
    }

    public async Task<IReadOnlyList<ServiceMode>> GetModesAsync(string serviceName, CancellationToken cancellationToken = default)
    {
        if (!RoutedServiceNames.IsValid(serviceName))
        {
            throw new RoutingException(
                RoutingErrorCodes.ModeNotFound,
                $"'{serviceName}' is not a routed service. Expected one of: "
                + string.Join(", ", RoutedServiceNames.All),
                action: "Use one of the five routed service names: Embeddings, ImageGeneration, SpeechTranscription, SpeechSynthesis, DocumentIntelligence.",
                serviceId: serviceName);
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var row = await db.ApplicationSettings
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.SectionName == SectionName, cancellationToken)
            .ConfigureAwait(false);

        if (row == null)
        {
            return Array.Empty<ServiceMode>();
        }

        var payload = ApplicationSettingsJson.DeserializeObject(row.JsonValue);
        return ServiceModesPayload.ReadModesFor(payload, serviceName);
    }
}

/// <summary>
/// Shared serializer for the <c>ServiceModes</c> section payload:
/// <code>
/// {
///   "Modes": {
///     "Embeddings": { "defaultModeId": "default", "modes": [ServiceMode, ...] },
///     "ImageGeneration": { ... },
///     ...
///   }
/// }
/// </code>
/// </summary>
public static class ServiceModesPayload
{
    public const string ModesProperty = "Modes";
    private const string DefaultModeIdProperty = "defaultModeId";
    private const string ModesListProperty = "modes";
    private const string DeprecatedLegacyModelPresetField = "Allowed" + "Models";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    public static IReadOnlyList<ServiceMode> ReadModesFor(JsonObject rootPayload, string serviceName)
    {
        if (!rootPayload.TryGetPropertyValue(ModesProperty, out var modesNode) || modesNode is null)
        {
            return Array.Empty<ServiceMode>();
        }

        var modesContainer = ParseModesContainer(modesNode);
        if (modesContainer == null || !modesContainer.TryGetPropertyValue(serviceName, out var serviceNode) || serviceNode is not JsonObject serviceObject)
        {
            return Array.Empty<ServiceMode>();
        }

        if (!serviceObject.TryGetPropertyValue(ModesListProperty, out var listNode) || listNode is not JsonArray listArray)
        {
            return Array.Empty<ServiceMode>();
        }

        var defaultModeId = serviceObject.TryGetPropertyValue(DefaultModeIdProperty, out var defaultNode)
            ? defaultNode?.GetValue<string?>()
            : null;

        var list = new List<ServiceMode>(listArray.Count);
        foreach (var item in listArray)
        {
            if (item is not JsonObject modeObject)
            {
                continue;
            }

            try
            {
                var mode = modeObject.Deserialize<ServiceMode>(SerializerOptions);
                if (mode == null)
                {
                    continue;
                }

                var isDefault = mode.IsDefault
                                || (!string.IsNullOrWhiteSpace(defaultModeId)
                                    && string.Equals(mode.ModeId, defaultModeId, StringComparison.Ordinal));
                list.Add(mode with
                {
                    IsDefault = isDefault,
                    RequestPresetJson = StripDeprecatedPresetFields(mode.RequestPresetJson)
                });
            }
            catch (JsonException)
            {
                // Skip malformed entries so a bad row cannot brick the whole service.
                continue;
            }
        }

        return list;
    }

    public static JsonObject WriteModesFor(JsonObject rootPayload, string serviceName, IReadOnlyList<ServiceMode> modes, string? defaultModeId)
    {
        var modesContainer = GetOrCreateModesContainer(rootPayload);

        var serviceObject = new JsonObject
        {
            [DefaultModeIdProperty] = defaultModeId
        };

        var listArray = new JsonArray();
        foreach (var mode in modes)
        {
            var modeToWrite = mode with
            {
                RequestPresetJson = StripDeprecatedPresetFields(mode.RequestPresetJson)
            };
            var node = JsonSerializer.SerializeToNode(modeToWrite, SerializerOptions);
            if (node is JsonObject modeObject)
            {
                listArray.Add(modeObject);
            }
        }

        serviceObject[ModesListProperty] = listArray;
        modesContainer[serviceName] = serviceObject;
        return rootPayload;
    }

    public static string SerializeModesContainer(JsonObject container) =>
        container.ToJsonString(SerializerOptions);

    private static JsonObject? ParseModesContainer(JsonNode node)
    {
        // Modes may be stored either as a nested JsonObject (preferred) or as a
        // JSON-encoded string for forward compatibility with environments that
        // can only persist string values.
        return node switch
        {
            JsonObject obj => obj,
            JsonValue value when value.TryGetValue<string>(out var raw) && !string.IsNullOrWhiteSpace(raw)
                => ApplicationSettingsJson.DeserializeObject(raw),
            _ => null
        };
    }

    private static JsonObject GetOrCreateModesContainer(JsonObject rootPayload)
    {
        if (rootPayload.TryGetPropertyValue(ModesProperty, out var existingNode) && existingNode is JsonObject existing)
        {
            return existing;
        }

        if (existingNode is JsonValue value && value.TryGetValue<string>(out var raw) && !string.IsNullOrWhiteSpace(raw))
        {
            var parsed = ApplicationSettingsJson.DeserializeObject(raw);
            rootPayload[ModesProperty] = parsed;
            return parsed;
        }

        var fresh = new JsonObject();
        rootPayload[ModesProperty] = fresh;
        return fresh;
    }

    private static string? StripDeprecatedPresetFields(string? requestPresetJson)
    {
        if (string.IsNullOrWhiteSpace(requestPresetJson))
        {
            return requestPresetJson;
        }

        try
        {
            var payload = ApplicationSettingsJson.DeserializeObject(requestPresetJson);
            if (!payload.Remove(DeprecatedLegacyModelPresetField))
            {
                return requestPresetJson;
            }

            return payload.Count == 0
                ? null
                : payload.ToJsonString(SerializerOptions);
        }
        catch (JsonException)
        {
            return requestPresetJson;
        }
    }
}
