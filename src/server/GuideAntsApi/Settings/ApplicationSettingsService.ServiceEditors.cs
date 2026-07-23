using System.Globalization;
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

        await SeedFoundryServiceConnectionFromChatIfNeededAsync(provider, cancellationToken)
            .ConfigureAwait(false);

        var (row, payload) = await LoadOrCreateServiceModesRowAsync(cancellationToken).ConfigureAwait(false);
        var canonicalService = CanonicalizeServiceName(contract.ServiceId);
        var modes = ServiceModesPayload.ReadModesFor(payload, canonicalService).ToList();
        var selectedMode = FindModeForProvider(modes, provider.ProviderSectionKey);
        if (selectedMode == null)
        {
            var missingConnectionFields = BuildProviderConnectionMissingFields(provider).ToList();
            if (missingConnectionFields.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Provider '{providerId}' cannot be activated: {string.Join("; ", missingConnectionFields.Select(field => $"Missing provider connection value: {field}."))}");
            }

            await CreateExplicitServiceModeAsync(contract, provider, cancellationToken).ConfigureAwait(false);

            (row, payload) = await LoadOrCreateServiceModesRowAsync(cancellationToken).ConfigureAwait(false);
            modes = ServiceModesPayload.ReadModesFor(payload, canonicalService).ToList();
            selectedMode = FindModeForProvider(modes, provider.ProviderSectionKey);
            if (selectedMode == null)
            {
                throw new InvalidOperationException(
                    $"Provider '{providerId}' cannot be activated because no explicit service mode exists for '{provider.ProviderSectionKey}'.");
            }
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

        await SeedFoundryServiceConnectionFromChatIfNeededAsync(provider, cancellationToken)
            .ConfigureAwait(false);

        var metadataProvider = ResolveMetadataProvider();
        var metadataByName = metadataProvider
            .GetProviderFields(contract.ServiceId, provider.ProviderId)
            .ToDictionary(field => field.Name, field => field, StringComparer.Ordinal);
        var hasExplicitMode = (await GetServiceModesAsync(contract.ServiceId, cancellationToken).ConfigureAwait(false))
            .Any(mode => string.Equals(mode.ProviderSection, provider.ProviderSectionKey, StringComparison.OrdinalIgnoreCase));

        var normalizedFields = NormalizeProviderFieldUpdates(request.Fields);

        foreach (var (fieldName, _) in normalizedFields)
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

            if (!IsServiceEditorEditableField(contract, provider, meta))
            {
                throw new InvalidOperationException(
                    $"Field '{fieldName}' belongs to provider connection configuration and cannot be updated through the service editor.");
            }
        }

        var updatesBySection = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        var modeFieldUpdates = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (fieldName, fieldValue) in normalizedFields)
        {
            var metadata = metadataByName[fieldName];
            ValidateProviderFieldUpdate(contract, provider, metadata, fieldValue);

            if (TryResolveServiceModeField(contract, provider, metadata.Name, out var modeField))
            {
                modeFieldUpdates[modeField] = fieldValue;
                continue;
            }

            if (!TryResolveServiceFieldSection(contract, metadata.Name, out var sectionName))
            {
                sectionName = ResolveFieldSection(provider, metadata.Name) ?? string.Empty;
            }

            if (string.IsNullOrWhiteSpace(sectionName))
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

        // Connection fields may have been written above. Create the explicit mode only after
        // those updates so Foundry multi-endpoint setup can complete in one Services save.
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

        if (modeFieldUpdates.Count > 0)
        {
            await UpdateServiceModeFieldsAsync(contract, provider, modeFieldUpdates, cancellationToken).ConfigureAwait(false);
        }

        return await GetServiceEditorStateAsync(contract.ServiceId, cancellationToken).ConfigureAwait(false);
    }

    private static IReadOnlyDictionary<string, string?> NormalizeProviderFieldUpdates(
        IReadOnlyDictionary<string, JsonElement> rawFields)
    {
        var normalized = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (fieldName, rawValue) in rawFields)
        {
            normalized[fieldName] = rawValue.ValueKind switch
            {
                JsonValueKind.Null => null,
                JsonValueKind.Undefined => null,
                JsonValueKind.String => rawValue.GetString(),
                JsonValueKind.Number => rawValue.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => throw new InvalidOperationException(
                    $"Field '{fieldName}' must be a string, number, boolean, or null."),
            };
        }

        return normalized;
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
            .Where(field => IsServiceEditorVisibleField(contract, provider, field))
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
                    HasValue: !string.IsNullOrWhiteSpace(value),
                    CurrentValue: value);
            })
            .ToList();
        var connectionMissingFields = BuildProviderConnectionMissingFields(provider).ToList();
        var activationBlockers = BuildActivationBlockers(contract, provider, matchingMode).ToList();

        return new ProviderEditorStateDto(
            ProviderId: provider.ProviderId,
            ProviderKind: provider.ProviderKind,
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
            FieldMetadata: metadata,
            RelatedChatConnectionConfigured: IsFoundryServiceProvider(provider)
                && TryGetFoundryChatConnection(out _, out _));
    }

    private ProviderFieldValueDto BuildProviderFieldValue(
        ServiceContract contract,
        ProviderContract provider,
        ProviderFieldMetadataDto field,
        ServiceModeDto? matchingMode)
    {
        if (TryResolveServiceModeField(contract, provider, field.Name, out var modeField))
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
        var rawValue = string.IsNullOrWhiteSpace(sectionName)
            ? null
            : ResolveProviderConnectionFieldValue(provider, sectionName, field.Name);
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

        if (IsFoundryConnectionSectionField(provider, fieldName))
        {
            return provider.ProviderSectionKey;
        }

        return provider.ProviderSettingsSection;
    }

    private static bool IsServiceEditorVisibleField(
        ServiceContract contract,
        ProviderContract provider,
        ProviderFieldMetadataDto field) =>
        IsServiceEditorEditableField(contract, provider, field);

    private static bool IsServiceEditorEditableField(
        ServiceContract contract,
        ProviderContract provider,
        ProviderFieldMetadataDto field) =>
        field.Operative
        && (TryResolveServiceModeField(contract, provider, field.Name, out _)
            || TryResolveServiceFieldSection(contract, field.Name, out _)
            || IsFoundryConnectionSectionField(provider, field.Name));

    private static bool IsFoundryConnectionSectionField(ProviderContract provider, string fieldName)
    {
        if (!IsFoundryServiceProvider(provider))
        {
            return false;
        }

        if (provider.RequiredSectionFields.Any(field =>
            string.Equals(field.FieldName, fieldName, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // Extra Foundry connection-section fields that are not always in the
        // per-service required list (Region on speech transcription, Endpoint on
        // speech synthesis, ApiVersion on images).
        return IsConnectionOwnedFieldName(fieldName)
            || (string.Equals(fieldName, "ApiVersion", StringComparison.OrdinalIgnoreCase)
                && string.Equals(provider.ProviderSectionKey, "AzureOpenAiImages", StringComparison.OrdinalIgnoreCase));
    }

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

    private static bool TryResolveServiceModeField(
        ServiceContract contract,
        ProviderContract provider,
        string fieldName,
        out string modeFieldName)
    {
        if (string.Equals(fieldName, "ModelId", StringComparison.Ordinal)
            || string.Equals(fieldName, "Deployment", StringComparison.Ordinal)
            || string.Equals(fieldName, "TextToImageModelId", StringComparison.Ordinal))
        {
            modeFieldName = "ModelId";
            return true;
        }

        // Foundry Images stores ApiVersion on AzureOpenAiImages; Document Intelligence
        // keeps ApiVersion on the service-mode preset.
        if (string.Equals(fieldName, "ApiVersion", StringComparison.Ordinal)
            && IsFoundryConnectionSectionField(provider, fieldName))
        {
            modeFieldName = string.Empty;
            return false;
        }

        if (string.Equals(fieldName, "VoiceName", StringComparison.Ordinal)
            || string.Equals(fieldName, "LanguageCode", StringComparison.Ordinal)
            || string.Equals(fieldName, "Speed", StringComparison.Ordinal)
            || string.Equals(fieldName, "EditModelDeployment", StringComparison.Ordinal)
            || string.Equals(fieldName, "ImageToImageModelId", StringComparison.Ordinal)
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

    public async Task EnsureServiceModeExistsAsync(
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

        await CreateExplicitServiceModeAsync(contract, provider, cancellationToken).ConfigureAwait(false);
    }

    private async Task CreateExplicitServiceModeAsync(
        ServiceContract contract,
        ProviderContract provider,
        CancellationToken cancellationToken)
    {
        await SeedFoundryServiceConnectionFromChatIfNeededAsync(provider, cancellationToken)
            .ConfigureAwait(false);

        var (row, payload) = await LoadOrCreateServiceModesRowAsync(cancellationToken).ConfigureAwait(false);
        var canonicalService = CanonicalizeServiceName(contract.ServiceId);
        var modes = ServiceModesPayload.ReadModesFor(payload, canonicalService).ToList();
        var existingMode = FindModeForProvider(modes, provider.ProviderSectionKey);
        if (existingMode is not null)
        {
            return;
        }

        var modeId = BuildExplicitServiceModeId(provider, modes);
        // ModelId stays null until the operator explicitly selects a model/bundle.
        // Seeding catalog defaults here invents configuration and forces warmup to
        // load services that were never configured.
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

    public async Task SetServiceModeModelIdAsync(
        string serviceId,
        string modelId,
        CancellationToken cancellationToken = default)
    {
        var trimmed = modelId.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            throw new ArgumentException("Model id is required.", nameof(modelId));
        }

        var contract = GetServiceContract(serviceId);
        var (row, payload) = await LoadOrCreateServiceModesRowAsync(cancellationToken).ConfigureAwait(false);
        var canonicalService = CanonicalizeServiceName(contract.ServiceId);
        var modes = ServiceModesPayload.ReadModesFor(payload, canonicalService).ToList();
        var activeMode = modes.FirstOrDefault(mode => mode.IsDefault && mode.Enabled)
            ?? modes.FirstOrDefault(mode => mode.IsDefault);
        if (activeMode is null)
        {
            throw new InvalidOperationException(
                $"Service '{serviceId}' has no default service mode; cannot persist configured model id.");
        }

        var localProviderSection = ResolveLocalProviderSectionKey(canonicalService);
        ServiceMode? targetMode;
        if (!string.IsNullOrWhiteSpace(localProviderSection))
        {
            targetMode = modes.FirstOrDefault(mode =>
                string.Equals(mode.ProviderSection, localProviderSection, StringComparison.OrdinalIgnoreCase));
            if (targetMode is null)
            {
                throw new InvalidOperationException(
                    $"Service '{serviceId}' has no local service mode for '{localProviderSection}'. "
                    + "Activate the local provider before selecting a model or bundle.");
            }
        }
        else
        {
            targetMode = activeMode;
        }

        if (string.Equals(targetMode.ModelId, trimmed, StringComparison.Ordinal))
        {
            return;
        }

        var updatedModes = modes
            .Select(mode => string.Equals(mode.ModeId, targetMode.ModeId, StringComparison.Ordinal)
                ? mode with { ModelId = trimmed }
                : mode)
            .ToList();
        ServiceModesPayload.WriteModesFor(payload, canonicalService, updatedModes, activeMode.ModeId);
        await PersistServiceModesAsync(row, payload, cancellationToken).ConfigureAwait(false);
    }

    private static string? ResolveLocalProviderSectionKey(string serviceId) =>
        serviceId switch
        {
            RoutedServiceNames.SpeechTranscription => $"{LocalServiceHostsOptions.SectionName}:SpeechTranscriptionBaseUrl",
            RoutedServiceNames.Embeddings => $"{LocalServiceHostsOptions.SectionName}:EmbeddingsBaseUrl",
            RoutedServiceNames.SpeechSynthesis => $"{LocalServiceHostsOptions.SectionName}:SpeechSynthesisBaseUrl",
            RoutedServiceNames.ImageGeneration => $"{LocalServiceHostsOptions.SectionName}:ImageGenerationBaseUrl",
            _ => null,
        };

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
            var value = ResolveProviderConnectionFieldValue(provider, field.SectionName, field.FieldName);
            if (string.IsNullOrWhiteSpace(value))
            {
                yield return field.FieldName;
            }
        }

        foreach (var runtime in provider.RequiredRuntimeKeys)
        {
            var value = RuntimeConfigurationPlaceholders.NormalizeUrlOrNull(_configuration[runtime.Key]);
            if (string.IsNullOrWhiteSpace(value))
            {
                yield return runtime.Key;
            }
        }
    }

    /// <summary>
    /// Resolves a required connection field, allowing Microsoft Foundry Images/Embeddings to
    /// inherit Endpoint/ApiKey from the chat <c>AzureOpenAI</c> connection when their dedicated
    /// service sections are still empty (chat-only wizard setup).
    /// </summary>
    private string? ResolveProviderConnectionFieldValue(
        ProviderContract provider,
        string sectionName,
        string fieldName)
    {
        var direct = _configuration[$"{sectionName}:{fieldName}"];
        if (!string.IsNullOrWhiteSpace(direct))
        {
            return direct;
        }

        if (!CanInheritFoundryChatConnection(sectionName))
        {
            return direct;
        }

        if (!TryGetFoundryChatConnection(out var endpoint, out var apiKey))
        {
            return direct;
        }

        if (string.Equals(fieldName, "Endpoint", StringComparison.OrdinalIgnoreCase))
        {
            return endpoint;
        }

        if (string.Equals(fieldName, "ApiKey", StringComparison.OrdinalIgnoreCase))
        {
            return apiKey;
        }

        return direct;
    }

    private static bool CanInheritFoundryChatConnection(string sectionName)
    {
        return string.Equals(sectionName, "AzureOpenAiImages", StringComparison.OrdinalIgnoreCase)
            || string.Equals(sectionName, "AzureOpenAiEmbedding", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFoundryServiceProvider(ProviderContract provider)
    {
        return string.Equals(provider.ProviderSectionKey, "AzureOpenAiImages", StringComparison.OrdinalIgnoreCase)
            || string.Equals(provider.ProviderSectionKey, "AzureOpenAiEmbedding", StringComparison.OrdinalIgnoreCase)
            || string.Equals(provider.ProviderSectionKey, AzureSpeechServiceOptions.SectionName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(provider.ProviderSectionKey, AzureDocumentIntelligenceOptions.SectionName, StringComparison.OrdinalIgnoreCase);
    }

    private bool TryGetFoundryChatConnection(out string endpoint, out string apiKey)
    {
        endpoint = string.Empty;
        apiKey = string.Empty;

        var resource = _configuration["AzureOpenAI:Resource"]?.Trim();
        var key = _configuration["AzureOpenAI:ApiKey"];
        if (string.IsNullOrWhiteSpace(resource) || string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        endpoint = DeriveFoundryEndpointFromResource(resource);
        apiKey = key;
        return !string.IsNullOrWhiteSpace(endpoint);
    }

    private static string DeriveFoundryEndpointFromResource(string resource)
    {
        var trimmed = resource.Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return string.Empty;
        }

        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed.EndsWith('/') ? trimmed : trimmed + "/";
        }

        return $"https://{trimmed}.openai.azure.com/";
    }

    /// <summary>
    /// Materializes AzureOpenAiImages / AzureOpenAiEmbedding connection values from the chat
    /// AzureOpenAI connection so runtime readers that only look at the dedicated sections succeed
    /// after a chat-only Foundry setup activates those service providers.
    /// </summary>
    private async Task SeedFoundryServiceConnectionFromChatIfNeededAsync(
        ProviderContract provider,
        CancellationToken cancellationToken)
    {
        var sectionName = provider.ProviderSectionKey;
        if (!CanInheritFoundryChatConnection(sectionName))
        {
            return;
        }

        if (!TryGetFoundryChatConnection(out var endpoint, out var apiKey))
        {
            return;
        }

        var existing = await GetSectionAsync(sectionName, cancellationToken).ConfigureAwait(false);
        if (existing == null)
        {
            return;
        }

        var patch = new JsonObject();
        var endpointValue = existing.Payload.TryGetPropertyValue("Endpoint", out var endpointNode)
            ? endpointNode?.GetValue<string>()
            : null;
        var apiKeyHasValue = existing.SecretHasValue.TryGetValue("ApiKey", out var hasApiKey) && hasApiKey;

        if (string.IsNullOrWhiteSpace(endpointValue))
        {
            patch["Endpoint"] = endpoint;
        }

        if (!apiKeyHasValue)
        {
            patch["ApiKey"] = apiKey;
        }

        if (string.Equals(sectionName, "AzureOpenAiImages", StringComparison.OrdinalIgnoreCase))
        {
            var apiVersionValue = existing.Payload.TryGetPropertyValue("ApiVersion", out var apiVersionNode)
                ? apiVersionNode?.GetValue<string>()
                : null;
            if (string.IsNullOrWhiteSpace(apiVersionValue))
            {
                var configuredVersion = _configuration["AzureOpenAI:ApiVersion"]?.Trim();
                patch["ApiVersion"] = string.IsNullOrWhiteSpace(configuredVersion)
                    ? "2025-04-01-preview"
                    : configuredVersion;
            }
        }

        if (patch.Count == 0)
        {
            return;
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
            && TryResolveServiceModeField(contract, provider, field.Name, out _)))
        {
            var value = BuildProviderFieldValue(contract, provider, field, matchingMode);
            if (!value.HasValue)
            {
                yield return $"{field.Name} is required.";
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
        var existing = string.IsNullOrWhiteSpace(sectionName)
            ? null
            : ResolveProviderConnectionFieldValue(provider, sectionName, metadata.Name);
        var hasExisting = !string.IsNullOrWhiteSpace(existing);

        if (string.Equals(metadata.Kind, "secret", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(submittedValue))
            {
                if (metadata.Required && !hasExisting)
                {
                    throw new InvalidOperationException($"{metadata.Name} is required.");
                }

                return;
            }
        }
        else if (string.IsNullOrWhiteSpace(submittedValue))
        {
            if (metadata.Required)
            {
                throw new InvalidOperationException($"{metadata.Name} is required.");
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
                    throw new InvalidOperationException($"{metadata.Name} must be a whole number.");
                }

                if ((string.Equals(metadata.Name, "TimeoutSeconds", StringComparison.Ordinal)
                    || string.Equals(metadata.Name, "MaxRetries", StringComparison.Ordinal)
                    || string.Equals(metadata.Name, "ReadyTimeoutSeconds", StringComparison.Ordinal)
                    || string.Equals(metadata.Name, "MaxAudioBytes", StringComparison.Ordinal))
                    && n <= 0)
                {
                    throw new InvalidOperationException($"{metadata.Name} must be greater than zero.");
                }

                if (string.Equals(metadata.Name, "LocalMinIntervalMs", StringComparison.Ordinal) && n < 0)
                {
                    throw new InvalidOperationException($"{metadata.Name} must be zero or greater.");
                }
            }
            else if (string.Equals(metadata.Kind, "url", StringComparison.OrdinalIgnoreCase))
            {
                if (!Uri.TryCreate(v, UriKind.Absolute, out var uri) ||
                    (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                {
                    throw new InvalidOperationException($"{metadata.Name} must be a valid http(s) URL.");
                }
            }
            else if (string.Equals(metadata.Kind, "enum", StringComparison.OrdinalIgnoreCase))
            {
                if (metadata.EnumOptions is { Count: > 0 } opts)
                {
                    if (!opts.Any(o => string.Equals(o, v, StringComparison.Ordinal)))
                    {
                        throw new InvalidOperationException(
                            $"{metadata.Name} must be one of: {string.Join(", ", opts)}.");
                    }
                }
                else if (!bool.TryParse(v, out _))
                {
                    throw new InvalidOperationException($"{metadata.Name} must be 'true' or 'false'.");
                }
            }
            else if (string.Equals(metadata.Name, "ApiVersion", StringComparison.Ordinal) && v.Length > 0)
            {
                // Date-style API versions (e.g. 2024-11-30, 2025-04-01-preview)
                if (System.Text.RegularExpressions.Regex.IsMatch(v, @"^\d{4}-\d{2}-\d{2}(-[a-zA-Z0-9.-]+)?$") == false)
                {
                    throw new InvalidOperationException(
                        $"{metadata.Name} should look like a date-based API version (e.g. 2024-11-30).");
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
