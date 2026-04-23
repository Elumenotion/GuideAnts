using Microsoft.EntityFrameworkCore;
using System.Text.Json.Nodes;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models.Settings;
using GuideAntsApi.Services.Routing;

namespace GuideAntsApi.Settings;

public sealed partial class ApplicationSettingsService
{
    public async Task<IReadOnlyList<ServiceModeDto>> GetServiceModesAsync(string serviceName, CancellationToken cancellationToken = default)
    {
        AssertServiceName(serviceName);
        var payload = await LoadServiceModesPayloadAsync(cancellationToken).ConfigureAwait(false);
        var modes = ServiceModesPayload.ReadModesFor(payload, CanonicalizeServiceName(serviceName));
        return modes.Select(m => ToServiceModeDto(serviceName, m)).ToList();
    }

    private async Task<JsonObject> LoadServiceModesPayloadAsync(CancellationToken cancellationToken)
    {
        await EnsureRowsExistFromCurrentConfigAsync(cancellationToken).ConfigureAwait(false);
        var row = await _db.ApplicationSettings
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.SectionName == ServiceModeResolver.SectionName, cancellationToken)
            .ConfigureAwait(false);

        return row == null
            ? new JsonObject()
            : ApplicationSettingsJson.DeserializeObject(row.JsonValue);
    }

    private async Task<(ApplicationSetting? Row, JsonObject Payload)> LoadOrCreateServiceModesRowAsync(CancellationToken cancellationToken)
    {
        await EnsureRowsExistFromCurrentConfigAsync(cancellationToken).ConfigureAwait(false);
        var row = await _db.ApplicationSettings
            .SingleOrDefaultAsync(x => x.SectionName == ServiceModeResolver.SectionName, cancellationToken)
            .ConfigureAwait(false);
        var payload = row == null
            ? new JsonObject()
            : ApplicationSettingsJson.DeserializeObject(row.JsonValue);
        return (row, payload);
    }

    private async Task PersistServiceModesAsync(ApplicationSetting? row, JsonObject payload, CancellationToken cancellationToken)
    {
        var serialized = ApplicationSettingsJson.Serialize(payload);
        if (row == null)
        {
            _db.ApplicationSettings.Add(new ApplicationSetting
            {
                SectionName = ServiceModeResolver.SectionName,
                SchemaVersion = 1,
                JsonValue = serialized,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            });
        }
        else
        {
            row.JsonValue = serialized;
            row.UpdatedUtc = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task NormalizeLegacySingleModeRowsAsync(CancellationToken cancellationToken)
    {
        if (!_registry.TryGet(ServiceModeResolver.SectionName, out var definition))
        {
            return;
        }

        var row = await _db.ApplicationSettings
            .SingleOrDefaultAsync(x => x.SectionName == definition.SectionName, cancellationToken)
            .ConfigureAwait(false);
        if (row == null || string.IsNullOrWhiteSpace(row.JsonValue))
        {
            return;
        }

        var payload = ApplicationSettingsJson.DeserializeObject(row.JsonValue);
        var changed = false;

        foreach (var serviceName in RoutedServiceNames.All)
        {
            var modes = ServiceModesPayload.ReadModesFor(payload, serviceName).ToList();
            if (modes.Count != 1)
            {
                continue;
            }

            var contract = ServiceContracts.FirstOrDefault(c =>
                string.Equals(c.ServiceId, serviceName, StringComparison.OrdinalIgnoreCase));
            if (contract == null)
            {
                continue;
            }

            var existing = modes[0];
            var existingKind = InferModeKind(existing, contract);
            if (existingKind is not ("local" or "cloud"))
            {
                continue;
            }

            var existingModeId = existingKind;
            var existingProviderSection = GetCanonicalProviderSection(contract, existing.ProviderSection, existingKind);
            if (string.IsNullOrWhiteSpace(existingProviderSection))
            {
                continue;
            }

            var counterpartKind = string.Equals(existingKind, "local", StringComparison.OrdinalIgnoreCase)
                ? "cloud"
                : "local";
            var counterpartSection = GetProviderSectionForKind(contract, counterpartKind);

            var shouldAddCounterpart = !string.IsNullOrWhiteSpace(counterpartSection)
                && !string.Equals(counterpartSection, existingProviderSection, StringComparison.OrdinalIgnoreCase);
            var shapeAlreadyCanonical =
                string.Equals(existing.ModeId, existingModeId, StringComparison.Ordinal)
                && string.Equals(existing.ProviderSection, existingProviderSection, StringComparison.Ordinal)
                && existing.IsDefault
                && !shouldAddCounterpart;
            if (shapeAlreadyCanonical)
            {
                continue;
            }

            var normalized = new List<ServiceMode>
            {
                existing with
                {
                    ModeId = existingModeId,
                    ProviderSection = existingProviderSection!,
                    IsDefault = true
                }
            };

            if (shouldAddCounterpart)
            {
                normalized.Add(new ServiceMode(
                    ModeId: counterpartKind,
                    ProviderSection: counterpartSection!,
                    ModelId: null,
                    RequestPresetJson: null,
                    Enabled: true,
                    IsDefault: false));
            }

            ServiceModesPayload.WriteModesFor(payload, serviceName, normalized, defaultModeId: existingModeId);
            changed = true;
        }

        if (!changed)
        {
            return;
        }

        row.JsonValue = ApplicationSettingsJson.Serialize(payload);
        row.SchemaVersion = definition.SchemaVersion;
        row.UpdatedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string? InferModeKind(ServiceMode mode, ServiceContract contract)
    {
        if (string.Equals(mode.ModeId, "local", StringComparison.OrdinalIgnoreCase))
        {
            return "local";
        }

        if (string.Equals(mode.ModeId, "cloud", StringComparison.OrdinalIgnoreCase))
        {
            return "cloud";
        }

        if (!string.Equals(mode.ModeId, "default", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return GetProviderKind(contract, mode.ProviderSection);
    }

    private static string? GetProviderKind(ServiceContract contract, string providerSectionOrProviderId)
    {
        if (string.IsNullOrWhiteSpace(providerSectionOrProviderId))
        {
            return null;
        }

        foreach (var provider in contract.Providers)
        {
            if (!string.IsNullOrWhiteSpace(provider.SectionName)
                && string.Equals(provider.SectionName, providerSectionOrProviderId, StringComparison.OrdinalIgnoreCase))
            {
                return provider.Kind;
            }

            if (provider.RequiredRuntimeKeys.Any(k =>
                string.Equals(k.Key, providerSectionOrProviderId, StringComparison.OrdinalIgnoreCase)))
            {
                return provider.Kind;
            }

            if (string.Equals(provider.ProviderId, providerSectionOrProviderId, StringComparison.OrdinalIgnoreCase))
            {
                return provider.Kind;
            }
        }

        return null;
    }

    private static string? GetCanonicalProviderSection(ServiceContract contract, string providerSectionOrProviderId, string kindFallback)
    {
        if (!string.IsNullOrWhiteSpace(providerSectionOrProviderId))
        {
            foreach (var provider in contract.Providers)
            {
                if (!string.IsNullOrWhiteSpace(provider.SectionName)
                    && string.Equals(provider.SectionName, providerSectionOrProviderId, StringComparison.OrdinalIgnoreCase))
                {
                    return provider.SectionName;
                }

                var runtimeKey = provider.RequiredRuntimeKeys.FirstOrDefault(k =>
                    string.Equals(k.Key, providerSectionOrProviderId, StringComparison.OrdinalIgnoreCase))?.Key;
                if (!string.IsNullOrWhiteSpace(runtimeKey))
                {
                    return runtimeKey;
                }

                if (string.Equals(provider.ProviderId, providerSectionOrProviderId, StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrWhiteSpace(provider.SectionName))
                    {
                        return provider.SectionName;
                    }

                    runtimeKey = provider.RequiredRuntimeKeys.FirstOrDefault()?.Key;
                    if (!string.IsNullOrWhiteSpace(runtimeKey))
                    {
                        return runtimeKey;
                    }
                }
            }
        }

        return GetProviderSectionForKind(contract, kindFallback);
    }

    private static string? GetProviderSectionForKind(ServiceContract contract, string kind)
    {
        var provider = contract.Providers.FirstOrDefault(p =>
            string.Equals(p.Kind, kind, StringComparison.OrdinalIgnoreCase));
        if (provider == null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(provider.SectionName))
        {
            return provider.SectionName;
        }

        return provider.RequiredRuntimeKeys.FirstOrDefault()?.Key;
    }

    private static void AssertServiceName(string serviceName)
    {
        if (!RoutedServiceNames.IsValid(serviceName))
        {
            throw new InvalidOperationException(
                $"'{serviceName}' is not a routed service. Expected one of: "
                + string.Join(", ", RoutedServiceNames.All));
        }
    }

    private static string CanonicalizeServiceName(string serviceName)
    {
        return RoutedServiceNames.All.First(s => string.Equals(s, serviceName, StringComparison.OrdinalIgnoreCase));
    }

    private static ServiceModeDto ToServiceModeDto(string serviceName, ServiceMode mode) =>
        new(
            ServiceName: serviceName,
            ModeId: mode.ModeId,
            ProviderSection: mode.ProviderSection,
            ModelId: mode.ModelId,
            RequestPresetJson: mode.RequestPresetJson,
            Enabled: mode.Enabled,
            IsDefault: mode.IsDefault);
}
