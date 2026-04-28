using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using GuideAntsApi.Configuration;
using GuideAntsApi.Models.Settings;
using GuideAntsApi.Options;
using GuideAntsApi.Services.Routing;

namespace GuideAntsApi.Settings;

public sealed partial class ApplicationSettingsService
{
    public async Task<ServiceEditorStateDto> GetServiceEditorStateAsync(
        string serviceId,
        CancellationToken cancellationToken = default)
    {
        var contract = GetServiceContract(serviceId);
        var modes = await GetServiceModesAsync(contract.ServiceId, cancellationToken).ConfigureAwait(false);
        var activeMode = modes.FirstOrDefault(mode => mode.IsDefault && mode.Enabled)
            ?? modes.FirstOrDefault(mode => mode.IsDefault)
            ?? modes.FirstOrDefault(mode => mode.Enabled);
        var activeProvider = TryResolveActiveProvider(contract, activeMode?.ProviderSection);
        var readiness = await GetServiceEditorReadinessAsync(contract.ServiceId, cancellationToken).ConfigureAwait(false);

        var providers = contract.Providers
            .Select(provider => BuildProviderEditorState(contract, provider, modes))
            .ToList();

        return new ServiceEditorStateDto(
            ServiceId: contract.ServiceId,
            DisplayName: contract.DisplayName,
            ActiveProviderId: activeProvider?.ProviderId ?? string.Empty,
            Providers: providers,
            Readiness: readiness);
    }

    public async Task<ServiceEditorStateDto> SetServiceActiveProviderAsync(
        string serviceId,
        string providerId,
        CancellationToken cancellationToken = default)
    {
        var contract = GetServiceContract(serviceId);
        var provider = contract.Providers.FirstOrDefault(p =>
            string.Equals(p.ProviderId, providerId, StringComparison.Ordinal));
        if (provider == null)
        {
            throw new InvalidOperationException($"Provider '{providerId}' is not valid for service '{serviceId}'.");
        }

        var (row, payload) = await LoadOrCreateServiceModesRowAsync(cancellationToken).ConfigureAwait(false);
        var canonicalService = CanonicalizeServiceName(contract.ServiceId);
        var modes = ServiceModesPayload.ReadModesFor(payload, canonicalService).ToList();
        var selectedMode = FindModeForProvider(modes, provider.ProviderSectionKey);
        if (selectedMode == null)
        {
            throw new InvalidOperationException(
                $"Provider '{providerId}' cannot be activated because no explicit service mode exists for '{provider.ProviderSectionKey}'.");
        }

        var activationBlockers = BuildActivationBlockers(contract, provider, ToServiceModeDto(contract.ServiceId, selectedMode)).ToList();
        if (activationBlockers.Count > 0)
        {
            throw new InvalidOperationException(
                $"Provider '{providerId}' cannot be activated: {string.Join("; ", activationBlockers)}");
        }

        var normalized = modes
            .Select(mode => mode with
            {
                IsDefault = string.Equals(mode.ModeId, selectedMode.ModeId, StringComparison.Ordinal),
                Enabled = string.Equals(mode.ModeId, selectedMode.ModeId, StringComparison.Ordinal)
                    ? true
                    : mode.Enabled
            })
            .ToList();

        ServiceModesPayload.WriteModesFor(payload, canonicalService, normalized, defaultModeId: selectedMode.ModeId);
        await PersistServiceModesAsync(row, payload, cancellationToken).ConfigureAwait(false);

        return await GetServiceEditorStateAsync(contract.ServiceId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ServiceEditorStateDto> UpdateServiceProviderFieldsAsync(
        string serviceId,
        string providerId,
        ProviderFieldsUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var contract = GetServiceContract(serviceId);
        var provider = contract.Providers.FirstOrDefault(p =>
            string.Equals(p.ProviderId, providerId, StringComparison.Ordinal));
        if (provider == null)
        {
            throw new InvalidOperationException($"Provider '{providerId}' is not valid for service '{serviceId}'.");
        }

        var metadataProvider = ResolveMetadataProvider();
        var metadataByName = metadataProvider
            .GetProviderFields(contract.ServiceId, provider.ProviderId)
            .ToDictionary(field => field.Name, field => field, StringComparer.Ordinal);
        var hasExplicitMode = (await GetServiceModesAsync(contract.ServiceId, cancellationToken).ConfigureAwait(false))
            .Any(mode => string.Equals(mode.ProviderSection, provider.ProviderSectionKey, StringComparison.OrdinalIgnoreCase));

        foreach (var (fieldName, _) in request.Fields)
        {
            if (!metadataByName.TryGetValue(fieldName, out var meta))
            {
                if (IsConnectionOwnedFieldName(fieldName))
                {
                    throw new InvalidOperationException(
                        $"Field '{fieldName}' belongs to provider connection configuration and cannot be updated through the service editor.");
                }

                throw new InvalidOperationException(
                    $"Unknown field '{fieldName}' for provider '{providerId}' on service '{serviceId}'.");
            }

            if (!meta.Operative)
            {
                throw new InvalidOperationException(
                    $"Field '{fieldName}' is diagnostic-only and cannot be updated through the service editor.");
            }

            if (!IsServiceEditorEditableField(contract, meta))
            {
                throw new InvalidOperationException(
                    $"Field '{fieldName}' belongs to provider connection configuration and cannot be updated through the service editor.");
            }
        }

        if (!hasExplicitMode)
        {
            var missingConnectionFields = BuildProviderConnectionMissingFields(provider).ToList();
            if (missingConnectionFields.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Provider '{providerId}' cannot be configured until its connection is ready: {string.Join(", ", missingConnectionFields)}.");
            }

            await CreateExplicitServiceModeAsync(contract, provider, cancellationToken).ConfigureAwait(false);
        }

        var updatesBySection = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        var modeFieldUpdates = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (fieldName, fieldValue) in request.Fields)
        {
            var metadata = metadataByName[fieldName];
            ValidateProviderFieldUpdate(contract, provider, metadata, fieldValue);

            if (TryResolveServiceModeField(contract, metadata.Name, out var modeField))
            {
                modeFieldUpdates[modeField] = fieldValue;
                continue;
            }

            if (!TryResolveServiceFieldSection(contract, metadata.Name, out var sectionName))
            {
                throw new InvalidOperationException(
                    $"Field '{fieldName}' does not resolve to a settings section for provider '{providerId}'.");
            }

            if (!updatesBySection.TryGetValue(sectionName, out var sectionPatch))
            {
                sectionPatch = new JsonObject();
                updatesBySection[sectionName] = sectionPatch;
            }

            sectionPatch[fieldName] = ToFieldNode(metadata.Kind, fieldValue);
        }

        foreach (var (sectionName, patch) in updatesBySection)
        {
            var existing = await GetSectionAsync(sectionName, cancellationToken).ConfigureAwait(false);
            if (existing == null)
            {
                throw new InvalidOperationException($"Section '{sectionName}' was not found.");
            }

            var result = await UpdateSectionAsync(
                sectionName,
                new UpdateSettingsSectionRequest(existing.RowVersion, patch),
                cancellationToken).ConfigureAwait(false);
            if (result.ConcurrencyConflict)
            {
                throw new InvalidOperationException($"Section '{sectionName}' was modified by another request.");
            }

            if (result.ValidationErrors.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Validation failed for section '{sectionName}': {string.Join("; ", result.ValidationErrors)}");
            }
        }

        if (modeFieldUpdates.Count > 0)
        {
            await UpdateServiceModeFieldsAsync(contract, provider, modeFieldUpdates, cancellationToken).ConfigureAwait(false);
        }

        return await GetServiceEditorStateAsync(contract.ServiceId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ServiceEditorReadinessDto> GetServiceEditorReadinessAsync(
        string serviceId,
        CancellationToken cancellationToken = default)
    {
        var readiness = await GetReadinessAsync(cancellationToken).ConfigureAwait(false);
        var service = readiness.Services.FirstOrDefault(s =>
            string.Equals(s.ServiceId, serviceId, StringComparison.OrdinalIgnoreCase));

        return service == null
            ? new ServiceEditorReadinessDto("blocked", [$"Service '{serviceId}' not found."], [])
            : new ServiceEditorReadinessDto(service.Status, service.Blockers, service.Warnings);
    }

    private IServiceEditorMetadataProvider ResolveMetadataProvider()
    {
        if (_injectedMetadataProvider != null)
        {
            return _injectedMetadataProvider;
        }

        if (_metadataProvider != null)
        {
            return _metadataProvider;
        }

        return _metadataProvider = new ServiceEditorMetadataProvider();
    }

    private ServiceContract GetServiceContract(string serviceId)
    {
        var contract = ServiceContracts.FirstOrDefault(service =>
            string.Equals(service.ServiceId, serviceId, StringComparison.OrdinalIgnoreCase));
        if (contract == null)
        {
            throw new InvalidOperationException(
                $"Unknown service '{serviceId}'. Expected one of: {string.Join(", ", ServiceContracts.Select(s => s.ServiceId))}");
        }

        return contract;
    }

    private static ProviderContract? TryResolveActiveProvider(ServiceContract contract, string? providerSection)
    {
        if (string.IsNullOrWhiteSpace(providerSection))
        {
            return null;
        }

        return contract.Providers.FirstOrDefault(provider =>
            string.Equals(provider.ProviderSectionKey, providerSection, StringComparison.OrdinalIgnoreCase));
    }

    private ProviderEditorStateDto BuildProviderEditorState(
        ServiceContract contract,
        ProviderContract provider,
        IReadOnlyList<ServiceModeDto> modes)
    {
        var metadataProvider = ResolveMetadataProvider();
        var metadata = metadataProvider
            .GetProviderFields(contract.ServiceId, provider.ProviderId)
            .Where(field => IsServiceEditorVisibleField(contract, field))
            .ToList();
        var matchingMode = modes.FirstOrDefault(mode =>
            string.Equals(mode.ProviderSection, provider.ProviderSectionKey, StringComparison.OrdinalIgnoreCase));
        var fieldValues = metadata.ToDictionary(
            field => field.Name,
            field => BuildProviderFieldValue(contract, provider, field, matchingMode),
            StringComparer.Ordinal);

        var runtimeDependencies = provider.RequiredRuntimeKeys
            .Select(key =>
            {
                var value = RuntimeConfigurationPlaceholders.NormalizeUrlOrNull(_configuration[key.Key]);
                return new RuntimeKeyDto(
                    Key: key.Key,
                    DisplayName: key.DisplayName,
                    ChangeHint: key.ChangeHint,
                    HasValue: !string.IsNullOrWhiteSpace(value),
                    CurrentValue: value);
            })
            .ToList();
        var connectionMissingFields = BuildProviderConnectionMissingFields(provider).ToList();
        var activationBlockers = BuildActivationBlockers(contract, provider, matchingMode).ToList();

        return new ProviderEditorStateDto(
            ProviderId: provider.ProviderId,
            ProviderKind: provider.ProviderKind,
            DisplayName: provider.ProviderDisplayName,
            ProviderSection: provider.ProviderSectionKey,
            ModeId: matchingMode?.ModeId,
            HasExplicitMode: matchingMode != null,
            IsDefaultMode: matchingMode?.IsDefault == true,
            ConnectionConfigured: connectionMissingFields.Count == 0,
            ConnectionMissingFields: connectionMissingFields,
            CanActivate: matchingMode != null && activationBlockers.Count == 0,
            ActivationBlockers: activationBlockers,
            Fields: fieldValues,
            RuntimeDependencies: runtimeDependencies,
            OperativeFields: metadata.Where(field => field.Operative).Select(field => field.Name).ToList(),
            DiagnosticFields: metadata.Where(field => !field.Operative).Select(field => field.Name).ToList(),
            FieldMetadata: metadata);
    }

    private ProviderFieldValueDto BuildProviderFieldValue(
        ServiceContract contract,
        ProviderContract provider,
        ProviderFieldMetadataDto field,
        ServiceModeDto? matchingMode)
    {
        if (TryResolveServiceModeField(contract, field.Name, out var modeField))
        {
            var modeValue = ResolveServiceModeFieldValue(modeField, matchingMode);

            return new ProviderFieldValueDto(
                Name: field.Name,
                Value: modeValue,
                IsSecret: false,
                HasValue: !string.IsNullOrWhiteSpace(modeValue));
        }

        var sectionName = TryResolveServiceFieldSection(contract, field.Name, out var serviceSectionName)
            ? serviceSectionName
            : ResolveFieldSection(provider, field.Name);
        var key = string.IsNullOrWhiteSpace(sectionName) ? null : $"{sectionName}:{field.Name}";
        var rawValue = key == null ? null : _configuration[key];
        var value = string.Equals(field.Kind, "url", StringComparison.OrdinalIgnoreCase)
            ? RuntimeConfigurationPlaceholders.NormalizeUrlOrNull(rawValue)
            : rawValue;
        var isSecret = string.Equals(field.Kind, "secret", StringComparison.OrdinalIgnoreCase);
        return new ProviderFieldValueDto(
            Name: field.Name,
            Value: isSecret ? null : value,
            IsSecret: isSecret,
            HasValue: !string.IsNullOrWhiteSpace(value));
    }

    private static string? ResolveFieldSection(ProviderContract provider, string fieldName)
    {
        var requiredSection = provider.RequiredSectionFields.FirstOrDefault(field =>
            string.Equals(field.FieldName, fieldName, StringComparison.OrdinalIgnoreCase));
        if (requiredSection != null)
        {
            return requiredSection.SectionName;
        }

        return provider.ProviderSettingsSection;
    }

    private static bool IsServiceEditorVisibleField(ServiceContract contract, ProviderFieldMetadataDto field) =>
        IsServiceEditorEditableField(contract, field);

    private static bool IsServiceEditorEditableField(ServiceContract contract, ProviderFieldMetadataDto field) =>
        field.Operative
        && (TryResolveServiceModeField(contract, field.Name, out _)
            || TryResolveServiceFieldSection(contract, field.Name, out _));

    private static bool IsConnectionOwnedFieldName(string fieldName)
    {
        string[] connectionFields =
        [
            "Endpoint",
            "ApiKey",
            "Token",
            "BaseUrl",
            "Region",
            "Resource",
            "AuthToken",
            "HttpReferer",
            "AppTitle",
            "RouterBaseUrl"
        ];

        return connectionFields.Any(field =>
            string.Equals(field, fieldName, StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryResolveServiceFieldSection(
        ServiceContract contract,
        string fieldName,
        out string sectionName)
    {
        if (contract.ServiceFieldNames.Any(name => string.Equals(name, fieldName, StringComparison.Ordinal)))
        {
            sectionName = contract.SectionName;
            return true;
        }

        sectionName = string.Empty;
        return false;
    }

    private static bool TryResolveServiceModeField(ServiceContract contract, string fieldName, out string modeFieldName)
    {
        if (string.Equals(fieldName, "ModelId", StringComparison.Ordinal)
            || string.Equals(fieldName, "Deployment", StringComparison.Ordinal))
        {
            modeFieldName = "ModelId";
            return true;
        }

        if (string.Equals(fieldName, "VoiceName", StringComparison.Ordinal)
            || string.Equals(fieldName, "EditModelDeployment", StringComparison.Ordinal)
            || string.Equals(fieldName, "AllowedModels", StringComparison.Ordinal)
            || string.Equals(fieldName, "MaxAudioBytes", StringComparison.Ordinal)
            || string.Equals(fieldName, "ApiVersion", StringComparison.Ordinal)
            || (string.Equals(fieldName, "MaxRetries", StringComparison.Ordinal)
                && string.Equals(contract.ServiceId, DocumentIntelligenceOptions.SectionName, StringComparison.Ordinal)))
        {
            modeFieldName = $"Preset:{fieldName}";
            return true;
        }

        modeFieldName = string.Empty;
        return false;
    }

    private static string? ResolveServiceModeFieldValue(string modeField, ServiceModeDto? matchingMode) => modeField switch
    {
        "ModelId" => matchingMode?.ModelId,
        var preset when preset.StartsWith("Preset:", StringComparison.Ordinal) =>
            ReadServiceModePresetField(matchingMode?.RequestPresetJson, preset["Preset:".Length..]),
        _ => null
    };

    private static string? ReadServiceModePresetField(string? requestPresetJson, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(requestPresetJson))
        {
            return null;
        }

        try
        {
            var payload = ApplicationSettingsJson.DeserializeObject(requestPresetJson);
            return payload[fieldName]?.GetValue<string?>()?.Trim();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task UpdateServiceModeFieldsAsync(
        ServiceContract contract,
        ProviderContract provider,
        IReadOnlyDictionary<string, string?> updates,
        CancellationToken cancellationToken)
    {
        var (row, payload) = await LoadOrCreateServiceModesRowAsync(cancellationToken).ConfigureAwait(false);
        var canonicalService = CanonicalizeServiceName(contract.ServiceId);
        var modes = ServiceModesPayload.ReadModesFor(payload, canonicalService).ToList();
        var existing = FindModeForProvider(modes, provider.ProviderSectionKey);
        if (existing == null)
        {
            throw new InvalidOperationException(
                $"Provider '{provider.ProviderId}' cannot be configured because no explicit service mode exists for '{provider.ProviderSectionKey}'.");
        }

        var updated = existing;

        foreach (var (fieldName, value) in updates)
        {
            switch (fieldName)
            {
                case "ModelId":
                    updated = updated with { ModelId = string.IsNullOrWhiteSpace(value) ? null : value.Trim() };
                    break;
                case var preset when preset.StartsWith("Preset:", StringComparison.Ordinal):
                    updated = updated with
                    {
                        RequestPresetJson = UpsertServiceModePresetField(
                            updated.RequestPresetJson,
                            preset["Preset:".Length..],
                            value)
                    };
                    break;
            }
        }

        var index = modes.FindIndex(mode =>
            string.Equals(mode.ModeId, existing.ModeId, StringComparison.Ordinal));
        if (index >= 0)
        {
            modes[index] = updated;
        }

        var defaultModeId = modes.FirstOrDefault(mode => mode.IsDefault)?.ModeId
            ?? updated.ModeId;
        ServiceModesPayload.WriteModesFor(payload, canonicalService, modes, defaultModeId);
        await PersistServiceModesAsync(row, payload, cancellationToken).ConfigureAwait(false);
    }

    private async Task CreateExplicitServiceModeAsync(
        ServiceContract contract,
        ProviderContract provider,
        CancellationToken cancellationToken)
    {
        var (row, payload) = await LoadOrCreateServiceModesRowAsync(cancellationToken).ConfigureAwait(false);
        var canonicalService = CanonicalizeServiceName(contract.ServiceId);
        var modes = ServiceModesPayload.ReadModesFor(payload, canonicalService).ToList();
        if (FindModeForProvider(modes, provider.ProviderSectionKey) != null)
        {
            return;
        }

        var modeId = BuildExplicitServiceModeId(provider, modes);
        modes.Add(new ServiceMode(
            ModeId: modeId,
            ProviderSection: provider.ProviderSectionKey,
            ModelId: null,
            RequestPresetJson: null,
            Enabled: false,
            IsDefault: false));

        var defaultModeId = modes.FirstOrDefault(mode => mode.IsDefault)?.ModeId;
        ServiceModesPayload.WriteModesFor(payload, canonicalService, modes, defaultModeId);
        await PersistServiceModesAsync(row, payload, cancellationToken).ConfigureAwait(false);
    }

    private static string BuildExplicitServiceModeId(
        ProviderContract provider,
        IReadOnlyList<ServiceMode> modes)
    {
        var candidate = provider.ProviderId;
        if (!modes.Any(mode => string.Equals(mode.ModeId, candidate, StringComparison.Ordinal)))
        {
            return candidate;
        }

        for (var i = 2; ; i++)
        {
            var numbered = $"{candidate}-{i}";
            if (!modes.Any(mode => string.Equals(mode.ModeId, numbered, StringComparison.Ordinal)))
            {
                return numbered;
            }
        }
    }

    private static ServiceMode? FindModeForProvider(IReadOnlyList<ServiceMode> modes, string providerSectionKey)
    {
        return modes.FirstOrDefault(mode =>
            string.Equals(mode.ProviderSection, providerSectionKey, StringComparison.OrdinalIgnoreCase));
    }

    private IEnumerable<string> BuildActivationBlockers(
        ServiceContract contract,
        ProviderContract provider,
        ServiceModeDto? matchingMode)
    {
        if (matchingMode == null)
        {
            yield return $"No explicit service mode exists for '{provider.ProviderSectionKey}'.";
        }

        foreach (var missing in BuildProviderConnectionMissingFields(provider))
        {
            yield return $"Missing provider connection value: {missing}.";
        }

        foreach (var missing in BuildRequiredModeFieldBlockers(contract, provider, matchingMode))
        {
            yield return missing;
        }

    }

    private IEnumerable<string> BuildProviderConnectionMissingFields(ProviderContract provider)
    {
        foreach (var field in provider.RequiredSectionFields)
        {
            var value = _configuration[$"{field.SectionName}:{field.FieldName}"];
            if (string.IsNullOrWhiteSpace(value))
            {
                yield return field.DisplayName;
            }
        }

        foreach (var runtime in provider.RequiredRuntimeKeys)
        {
            var value = RuntimeConfigurationPlaceholders.NormalizeUrlOrNull(_configuration[runtime.Key]);
            if (string.IsNullOrWhiteSpace(value))
            {
                yield return runtime.DisplayName;
            }
        }
    }

    private IEnumerable<string> BuildRequiredModeFieldBlockers(
        ServiceContract contract,
        ProviderContract provider,
        ServiceModeDto? matchingMode)
    {
        if (matchingMode == null)
        {
            yield break;
        }

        var metadata = ResolveMetadataProvider().GetProviderFields(contract.ServiceId, provider.ProviderId);
        foreach (var field in metadata.Where(field =>
            field.Required
            && field.Operative
            && TryResolveServiceModeField(contract, field.Name, out _)))
        {
            var value = BuildProviderFieldValue(contract, provider, field, matchingMode);
            if (!value.HasValue)
            {
                yield return $"{field.Label} is required.";
            }
        }
    }

    private static string? UpsertServiceModePresetField(string? existingJson, string fieldName, string? value)
    {
        JsonObject payload;
        if (string.IsNullOrWhiteSpace(existingJson))
        {
            payload = new JsonObject();
        }
        else
        {
            try
            {
                payload = ApplicationSettingsJson.DeserializeObject(existingJson);
            }
            catch (JsonException)
            {
                payload = new JsonObject();
            }
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            payload.Remove(fieldName);
        }
        else
        {
            payload[fieldName] = value.Trim();
        }

        return payload.Count == 0 ? null : ApplicationSettingsJson.Serialize(payload);
    }

    private void ValidateProviderFieldUpdate(
        ServiceContract contract,
        ProviderContract provider,
        ProviderFieldMetadataDto metadata,
        string? submittedValue)
    {
        var sectionName = TryResolveServiceFieldSection(contract, metadata.Name, out var serviceSectionName)
            ? serviceSectionName
            : ResolveFieldSection(provider, metadata.Name);
        var configKey = string.IsNullOrWhiteSpace(sectionName) ? null : $"{sectionName}:{metadata.Name}";
        var existing = configKey == null ? null : _configuration[configKey];
        var hasExisting = !string.IsNullOrWhiteSpace(existing);

        if (string.Equals(metadata.Kind, "secret", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(submittedValue))
            {
                if (metadata.Required && !hasExisting)
                {
                    throw new InvalidOperationException($"{metadata.Label} is required.");
                }

                return;
            }
        }
        else if (string.IsNullOrWhiteSpace(submittedValue))
        {
            if (metadata.Required)
            {
                throw new InvalidOperationException($"{metadata.Label} is required.");
            }

            return;
        }
        else
        {
            var v = submittedValue.Trim();
            if (string.Equals(metadata.Kind, "int", StringComparison.OrdinalIgnoreCase))
            {
                if (!int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                {
                    throw new InvalidOperationException($"{metadata.Label} must be a whole number.");
                }

                if ((string.Equals(metadata.Name, "TimeoutSeconds", StringComparison.Ordinal)
                    || string.Equals(metadata.Name, "MaxRetries", StringComparison.Ordinal)
                    || string.Equals(metadata.Name, "MaxAudioBytes", StringComparison.Ordinal))
                    && n <= 0)
                {
                    throw new InvalidOperationException($"{metadata.Label} must be greater than zero.");
                }

                if (string.Equals(metadata.Name, "LocalMinIntervalMs", StringComparison.Ordinal) && n < 0)
                {
                    throw new InvalidOperationException($"{metadata.Label} must be zero or greater.");
                }
            }
            else if (string.Equals(metadata.Kind, "url", StringComparison.OrdinalIgnoreCase))
            {
                if (!Uri.TryCreate(v, UriKind.Absolute, out var uri) ||
                    (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                {
                    throw new InvalidOperationException($"{metadata.Label} must be a valid http(s) URL.");
                }
            }
            else if (string.Equals(metadata.Kind, "enum", StringComparison.OrdinalIgnoreCase))
            {
                if (metadata.EnumOptions is { Count: > 0 } opts)
                {
                    if (!opts.Any(o => string.Equals(o, v, StringComparison.Ordinal)))
                    {
                        throw new InvalidOperationException(
                            $"{metadata.Label} must be one of: {string.Join(", ", opts)}.");
                    }
                }
                else if (!bool.TryParse(v, out _))
                {
                    throw new InvalidOperationException($"{metadata.Label} must be 'true' or 'false'.");
                }
            }
            else if (string.Equals(metadata.Name, "ApiVersion", StringComparison.Ordinal) && v.Length > 0)
            {
                // Date-style API versions (e.g. 2024-11-30, 2025-04-01-preview)
                if (System.Text.RegularExpressions.Regex.IsMatch(v, @"^\d{4}-\d{2}-\d{2}(-[a-zA-Z0-9.-]+)?$") == false)
                {
                    throw new InvalidOperationException(
                        $"{metadata.Label} should look like a date-based API version (e.g. 2024-11-30).");
                }
            }
        }
    }

    private static JsonNode? ToFieldNode(string kind, string? value)
    {
        if (value == null)
        {
            return null;
        }

        if (string.Equals(kind, "int", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(value, out var intValue))
        {
            return JsonValue.Create(intValue);
        }

        if (string.Equals(kind, "enum", StringComparison.OrdinalIgnoreCase)
            && bool.TryParse(value, out var boolValue))
        {
            return JsonValue.Create(boolValue);
        }

        return JsonValue.Create(value);
    }
}
