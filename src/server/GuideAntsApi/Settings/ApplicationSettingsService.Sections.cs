using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.Text.Json.Nodes;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models.Settings;
using GuideAntsApi.Options;

namespace GuideAntsApi.Settings;

public sealed partial class ApplicationSettingsService
{
    public async Task<IReadOnlyList<SettingsSectionSummaryDto>> GetSectionSummariesAsync(CancellationToken cancellationToken = default)
    {
        await EnsureRowsExistFromCurrentConfigAsync(cancellationToken);

        var rowsBySection = await _db.ApplicationSettings
            .AsNoTracking()
            .ToDictionaryAsync(x => x.SectionName, x => x, StringComparer.OrdinalIgnoreCase, cancellationToken);

        return _registry.All
            .OrderBy(x => x.DisplayOrder)
            .Select(definition =>
            {
                var readinessStatus = "not-applicable";
                IReadOnlyList<string> missingFields = [];

                if (ProviderSectionRequirements.ContainsKey(definition.SectionName))
                {
                    var readiness = EvaluateProviderSectionReadiness(definition.SectionName);
                    var hasMeaningfulValue = rowsBySection.TryGetValue(definition.SectionName, out var row)
                        && SectionHasMeaningfulConfiguredValue(definition, row);
                    readinessStatus = readiness.Configured
                        ? "configured"
                        : hasMeaningfulValue
                            ? "blocked"
                            : "unconfigured";
                    missingFields = readiness.MissingFields;
                }

                return new SettingsSectionSummaryDto(
                    definition.SectionName,
                    definition.DisplayName,
                    definition.DisplayOrder,
                    definition.HasSecrets,
                    readinessStatus,
                    missingFields);
            })
            .ToList();
    }

    public async Task<SettingsSectionDto?> GetSectionAsync(string sectionName, CancellationToken cancellationToken = default)
    {
        if (!_registry.TryGet(sectionName, out var definition))
        {
            return null;
        }

        await EnsureRowsExistFromCurrentConfigAsync(cancellationToken);

        var row = await _db.ApplicationSettings
            .AsNoTracking()
            .SingleOrDefaultAsync(
                x => x.SectionName == definition.SectionName,
                cancellationToken);

        if (row == null)
        {
            return null;
        }

        return ToSectionDto(definition, row);
    }

    public async Task<(SettingsSectionDto? Section, IReadOnlyList<string> ValidationErrors, bool ConcurrencyConflict)> UpdateSectionAsync(
        string sectionName,
        UpdateSettingsSectionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!_registry.TryGet(sectionName, out var definition))
        {
            return (null, [$"Unsupported section '{sectionName}'."], false);
        }

        await EnsureRowsExistFromCurrentConfigAsync(cancellationToken);

        var row = await _db.ApplicationSettings
            .SingleOrDefaultAsync(
                x => x.SectionName == definition.SectionName,
                cancellationToken);

        if (row == null)
        {
            return (null, [$"Section '{sectionName}' not found."], false);
        }

        var currentRowVersion = Convert.ToBase64String(row.RowVersion);
        if (!string.Equals(currentRowVersion, request.RowVersion, StringComparison.Ordinal))
        {
            return (null, [], true);
        }

        var protector = GetProtector();
        var decryptedCurrent = ApplicationSettingsJson.DecryptSecrets(
            definition,
            ApplicationSettingsJson.DeserializeObject(row.JsonValue),
            _settingsSecretsOptionsMonitor.CurrentValue,
            protector.Protect);

        var unsupportedFields = GetUnsupportedSectionFields(definition, request.Payload ?? new JsonObject());
        if (unsupportedFields.Count > 0)
        {
            return (null, unsupportedFields
                .Select(field => $"Field '{field}' is not supported by section '{definition.SectionName}'.")
                .ToList(), false);
        }

        var merged = ApplicationSettingsJson.MergeForUpdate(definition, decryptedCurrent, request.Payload ?? new JsonObject());
        var validationErrors = definition.Validate(merged)
            .Concat(await ValidateSectionPayloadAsync(definition.SectionName, merged, cancellationToken).ConfigureAwait(false))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (validationErrors.Count > 0)
        {
            return (null, validationErrors, false);
        }

        var encrypted = ApplicationSettingsJson.EncryptSecrets(
            definition,
            merged,
            _settingsSecretsOptionsMonitor.CurrentValue);
        row.JsonValue = ApplicationSettingsJson.Serialize(encrypted);
        row.SchemaVersion = definition.SchemaVersion;
        row.UpdatedUtc = DateTime.UtcNow;

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return (null, [], true);
        }

        ReloadConfiguration();

        var refreshed = await _db.ApplicationSettings
            .AsNoTracking()
            .SingleAsync(
                x => x.SectionName == definition.SectionName,
                cancellationToken);

        return (ToSectionDto(definition, refreshed), [], false);
    }

    private async Task<IReadOnlyList<string>> ValidateSectionPayloadAsync(
        string sectionName,
        JsonObject payload,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(sectionName, "ChatDefaults", StringComparison.Ordinal))
        {
            return [];
        }

        var defaultModelId = GetOptionalTrimmedString(payload, "DefaultModelId");
        var reasoningEffort = GetOptionalTrimmedString(payload, "ReasoningEffort");

        if (string.IsNullOrWhiteSpace(reasoningEffort))
        {
            return [];
        }

        if (string.IsNullOrWhiteSpace(defaultModelId))
        {
            return ["ReasoningEffort requires a default catalog model."];
        }

        var model = await _db.Models
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.ModelId == defaultModelId, cancellationToken)
            .ConfigureAwait(false);

        if (model == null)
        {
            return [$"ReasoningEffort cannot be set because catalog model '{defaultModelId}' does not exist."];
        }

        var reasoningChoices = ParseReasoningChoices(model.ReasoningChoicesJson);
        if (reasoningChoices.Count == 0)
        {
            return [$"Catalog model '{defaultModelId}' does not declare any reasoning choices."];
        }

        if (!reasoningChoices.Contains(reasoningEffort, StringComparer.OrdinalIgnoreCase))
        {
            return [$"ReasoningEffort '{reasoningEffort}' is not valid for catalog model '{defaultModelId}' (allowed: {string.Join(", ", reasoningChoices)})."];
        }

        return [];
    }

    private static IReadOnlyList<string> GetUnsupportedSectionFields(
        SettingsSectionDefinition definition,
        JsonObject incomingPayload)
    {
        if (definition.Properties.Count == 0)
        {
            return [];
        }

        var supported = definition.Properties
            .Select(property => property.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return incomingPayload
            .Select(kvp => kvp.Key)
            .Where(key => !supported.Contains(key))
            .ToList();
    }

    public async Task<SettingsSchemaDto> GetSchemaAsync(CancellationToken cancellationToken = default)
    {
        await EnsureRowsExistFromCurrentConfigAsync(cancellationToken);

        var rowsBySection = await _db.ApplicationSettings
            .AsNoTracking()
            .ToDictionaryAsync(x => x.SectionName, x => x, StringComparer.OrdinalIgnoreCase, cancellationToken);

        var sectionSchemas = _registry.All
            .OrderBy(x => x.DisplayOrder)
            .Select(definition =>
            {
                var schemaVersion = rowsBySection.TryGetValue(definition.SectionName, out var row)
                    ? row.SchemaVersion
                    : definition.SchemaVersion;

                var properties = definition.Properties
                    .Select(property => new SettingsSectionPropertyDefinitionDto(
                        Name: property.Name,
                        ValueType: ToApiValueType(property.ValueType),
                        IsSecret: property.IsSecret,
                        IsEditable: true,
                        IsRequired: ProviderSectionRequirements.TryGetValue(definition.SectionName, out var requirement)
                            && requirement.RequiredFields.Contains(property.Name, StringComparer.OrdinalIgnoreCase),
                        DefaultValue: ToJsonNode(property.DefaultValue)))
                    .ToList();

                return new SettingsSectionSchemaDto(
                    SectionName: definition.SectionName,
                    DisplayName: definition.DisplayName,
                    DisplayOrder: definition.DisplayOrder,
                    SchemaVersion: schemaVersion,
                    HasSecrets: definition.HasSecrets,
                    Properties: properties);
            })
            .ToList();

        var services = ServiceContracts
            .Select(contract => new SettingsServiceDefinitionDto(
                ServiceId: contract.ServiceId,
                DisplayName: contract.DisplayName,
                SectionName: contract.SectionName,
                ProviderIds: contract.Providers.Select(p => p.ProviderId).ToList(),
                ServiceFields: contract.ServiceFieldNames
                    .Select(fieldName => new SettingsDependencyFieldDto(
                        SectionName: contract.SectionName,
                        FieldName: fieldName,
                        DisplayName: HumanizeKey(fieldName)))
                    .ToList()))
            .ToList();

        var providers = ServiceContracts
            .SelectMany(contract => contract.Providers.Select(provider => new SettingsProviderDefinitionDto(
                ProviderId: provider.ProviderId,
                ServiceId: contract.ServiceId,
                DisplayName: provider.DisplayName,
                Kind: provider.Kind,
                ProviderSectionKey: provider.ProviderSectionKey,
                ProviderSettingsSection: provider.ProviderSettingsSection,
                MarketingSummary: provider.MarketingSummary,
                SectionName: provider.SectionName,
                RuntimeSectionName: provider.RequiredRuntimeKeys.Count > 0
                    ? LocalServiceHostsOptions.SectionName
                    : null,
                RequiredSectionFields: provider.RequiredSectionFields
                    .Select(field => new SettingsDependencyFieldDto(
                        SectionName: field.SectionName,
                        FieldName: field.FieldName,
                        DisplayName: field.DisplayName))
                    .ToList(),
                RequiredRuntimeKeys: provider.RequiredRuntimeKeys
                    .Select(key => new SettingsRuntimeKeyRequirementDto(
                        Key: key.Key,
                        DisplayName: key.DisplayName,
                        ChangeHint: key.ChangeHint))
                    .ToList())))
            .ToList();

        var runtimeDependencies = BuildRuntimeDependencies();

        return new SettingsSchemaDto(
            Sections: sectionSchemas,
            Services: services,
            Providers: providers,
            RuntimeDependencies: runtimeDependencies);
    }

    private SettingsSectionDto ToSectionDto(SettingsSectionDefinition definition, ApplicationSetting row)
    {
        var protector = GetProtector();
        var decrypted = ApplicationSettingsJson.DecryptSecrets(
            definition,
            ApplicationSettingsJson.DeserializeObject(row.JsonValue),
            _settingsSecretsOptionsMonitor.CurrentValue,
            protector.Protect);

        var (maskedPayload, metadata) = ApplicationSettingsJson.MaskSecrets(definition, decrypted);

        return new SettingsSectionDto(
            SectionName: definition.SectionName,
            DisplayName: definition.DisplayName,
            SchemaVersion: row.SchemaVersion,
            RowVersion: Convert.ToBase64String(row.RowVersion),
            UpdatedUtc: row.UpdatedUtc,
            Payload: maskedPayload,
            SecretHasValue: metadata);
    }

    private static bool JsonObjectsEquivalent(JsonObject left, JsonObject right)
    {
        return string.Equals(
            ApplicationSettingsJson.Serialize(left),
            ApplicationSettingsJson.Serialize(right),
            StringComparison.Ordinal);
    }

    private static string ToApiValueType(SettingsValueType valueType)
    {
        return valueType switch
        {
            SettingsValueType.Int => "int",
            SettingsValueType.Bool => "bool",
            _ => "string"
        };
    }

    private static JsonNode? ToJsonNode(object? value)
    {
        return value switch
        {
            null => null,
            int i => JsonValue.Create(i),
            long l => JsonValue.Create(l),
            double d => JsonValue.Create(d),
            decimal m => JsonValue.Create(m),
            bool b => JsonValue.Create(b),
            _ => JsonValue.Create(value.ToString())
        };
    }

    private static string? GetOptionalTrimmedString(JsonObject payload, string propertyName)
    {
        if (!payload.TryGetPropertyValue(propertyName, out var node) || node is not JsonValue value)
        {
            return null;
        }

        if (!value.TryGetValue<string>(out var parsed) || string.IsNullOrWhiteSpace(parsed))
        {
            return null;
        }

        return parsed.Trim();
    }

    private static IReadOnlyList<string> ParseReasoningChoices(string? reasoningChoicesJson)
    {
        if (string.IsNullOrWhiteSpace(reasoningChoicesJson))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(reasoningChoicesJson)?
                .Where(choice => !string.IsNullOrWhiteSpace(choice))
                .Select(choice => choice.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
                ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string HumanizeKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var buffer = new List<char>(value.Length + 8);
        for (var i = 0; i < value.Length; i++)
        {
            var current = value[i];
            var previous = i > 0 ? value[i - 1] : '\0';
            var shouldInsertSpace = i > 0
                                    && char.IsUpper(current)
                                    && (char.IsLower(previous) || char.IsDigit(previous));
            if (shouldInsertSpace)
            {
                buffer.Add(' ');
            }

            buffer.Add(current is '_' or '-' ? ' ' : current);
        }

        return new string(buffer.ToArray());
    }
}
