using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using GuideAntsApi.Models.Settings;
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
        var activeMode = modes.FirstOrDefault(mode => mode.IsDefault) ?? modes.FirstOrDefault();
        var activeProvider = ResolveActiveProvider(contract, activeMode?.ProviderSection);
        var readiness = await GetServiceEditorReadinessAsync(contract.ServiceId, cancellationToken).ConfigureAwait(false);

        var providers = contract.Providers
            .Select(provider => BuildProviderEditorState(contract.ServiceId, provider))
            .ToList();

        return new ServiceEditorStateDto(
            ServiceId: contract.ServiceId,
            DisplayName: contract.DisplayName,
            ActiveProviderId: activeProvider.ProviderId,
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
        var singleMode = new ServiceMode(
            ModeId: "active",
            ProviderSection: provider.ProviderSectionKey,
            ModelId: null,
            RequestPresetJson: null,
            Enabled: true,
            IsDefault: true);
        ServiceModesPayload.WriteModesFor(payload, canonicalService, new[] { singleMode }, defaultModeId: singleMode.ModeId);
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

        foreach (var (fieldName, _) in request.Fields)
        {
            if (!metadataByName.TryGetValue(fieldName, out var meta))
            {
                throw new InvalidOperationException(
                    $"Unknown field '{fieldName}' for provider '{providerId}' on service '{serviceId}'.");
            }

            if (!meta.Operative)
            {
                throw new InvalidOperationException(
                    $"Field '{fieldName}' is diagnostic-only and cannot be updated through the service editor.");
            }
        }

        var updatesBySection = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        foreach (var (fieldName, fieldValue) in request.Fields)
        {
            var metadata = metadataByName[fieldName];
            ValidateProviderFieldUpdate(provider, metadata, fieldValue);

            var sectionName = ResolveFieldSection(provider, metadata.Name);
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

    private static ProviderContract ResolveActiveProvider(ServiceContract contract, string? providerSection)
    {
        if (!string.IsNullOrWhiteSpace(providerSection))
        {
            var match = contract.Providers.FirstOrDefault(provider =>
                string.Equals(provider.ProviderSectionKey, providerSection, StringComparison.OrdinalIgnoreCase));
            if (match != null)
            {
                return match;
            }
        }

        return contract.Providers.First();
    }

    private ProviderEditorStateDto BuildProviderEditorState(string serviceId, ProviderContract provider)
    {
        var metadataProvider = ResolveMetadataProvider();
        var metadata = metadataProvider.GetProviderFields(serviceId, provider.ProviderId);
        var fieldValues = metadata.ToDictionary(
            field => field.Name,
            field => BuildProviderFieldValue(provider, field),
            StringComparer.Ordinal);

        var runtimeDependencies = provider.RequiredRuntimeKeys
            .Select(key =>
            {
                var value = _configuration[key.Key];
                return new RuntimeKeyDto(
                    Key: key.Key,
                    DisplayName: key.DisplayName,
                    ChangeHint: key.ChangeHint,
                    HasValue: !string.IsNullOrWhiteSpace(value),
                    CurrentValue: value);
            })
            .ToList();

        return new ProviderEditorStateDto(
            ProviderId: provider.ProviderId,
            ProviderKind: provider.ProviderKind,
            DisplayName: provider.ProviderDisplayName,
            Fields: fieldValues,
            RuntimeDependencies: runtimeDependencies,
            OperativeFields: metadata.Where(field => field.Operative).Select(field => field.Name).ToList(),
            DiagnosticFields: metadata.Where(field => !field.Operative).Select(field => field.Name).ToList(),
            FieldMetadata: metadata);
    }

    private ProviderFieldValueDto BuildProviderFieldValue(ProviderContract provider, ProviderFieldMetadataDto field)
    {
        var sectionName = ResolveFieldSection(provider, field.Name);
        var key = string.IsNullOrWhiteSpace(sectionName) ? null : $"{sectionName}:{field.Name}";
        var value = key == null ? null : _configuration[key];
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

    private void ValidateProviderFieldUpdate(ProviderContract provider, ProviderFieldMetadataDto metadata, string? submittedValue)
    {
        var sectionName = ResolveFieldSection(provider, metadata.Name);
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

                if (string.Equals(metadata.Name, "TimeoutSeconds", StringComparison.Ordinal) && n <= 0)
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
