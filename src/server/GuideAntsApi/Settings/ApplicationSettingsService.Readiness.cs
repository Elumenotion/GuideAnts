using Microsoft.EntityFrameworkCore;
using System.Text.Json.Nodes;
using GuideAntsApi.Models.Settings;
using GuideAntsApi.Configuration;
using GuideAntsApi.Services.Routing;
using GuideAntsApi.DataModel.Models;

namespace GuideAntsApi.Settings;

public sealed partial class ApplicationSettingsService
{
    public async Task<SettingsReadinessDto> GetReadinessAsync(CancellationToken cancellationToken = default)
    {
        await EnsureRowsExistFromCurrentConfigAsync(cancellationToken);

        var errors = ServiceRoutingStartupValidator.Evaluate(_configuration).ToList();
        var allServiceBlockers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var services = ServiceContracts.Select(contract =>
        {
            var blockers = errors
                .Where(error => IsErrorForService(contract, error))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var blocker in blockers)
            {
                allServiceBlockers.Add(blocker);
            }

            return new SettingsServiceReadinessDto(
                ServiceId: contract.ServiceId,
                DisplayName: contract.DisplayName,
                Status: blockers.Count == 0 ? "ready" : "blocked",
                Blockers: blockers,
                Warnings: []);
        }).ToList();

        var globalBlockers = errors
            .Where(error => !allServiceBlockers.Contains(error))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new SettingsReadinessDto(
            GeneratedUtc: DateTime.UtcNow,
            Services: services,
            GlobalBlockers: globalBlockers);
    }

    public async Task<ConnectionUsageDto> GetConnectionUsageAsync(string sectionName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sectionName))
        {
            throw new ArgumentException("Section name is required.", nameof(sectionName));
        }

        var trimmedSection = sectionName.Trim();

        // 1. ServiceModes rows that point at this section.
        var modePayload = await LoadServiceModesPayloadAsync(cancellationToken).ConfigureAwait(false);
        var matchingModes = new List<ConnectionUsageModeRef>();
        foreach (var service in RoutedServiceNames.All)
        {
            var modes = ServiceModesPayload.ReadModesFor(modePayload, service);
            foreach (var mode in modes)
            {
                if (string.Equals(mode.ProviderSection, trimmedSection, StringComparison.OrdinalIgnoreCase))
                {
                    matchingModes.Add(new ConnectionUsageModeRef(service, mode.ModeId, mode.IsDefault));
                }
            }
        }

        // 2. Active-assistant-referenced catalog models whose provider maps to this section.
        var assistantUsage = await _db.Assistants
            .AsNoTracking()
            .Where(a => a.IsActive && a.ModelId != null)
            .GroupBy(a => a.ModelId!)
            .Select(g => new { ModelId = g.Key, AssistantCount = g.Count() })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var chatTargets = new List<ConnectionUsageChatTargetRef>();
        if (assistantUsage.Count > 0)
        {
            var referencedIds = assistantUsage.Select(u => u.ModelId).ToList();
            var catalogRows = await _db.Models
                .AsNoTracking()
                .Where(m => referencedIds.Contains(m.ModelId))
                .Select(m => new { m.ModelId, m.Provider })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            foreach (var row in catalogRows.OrderBy(r => r.ModelId, StringComparer.Ordinal))
            {
                var mappedSection = RoutingReadinessService.MapChatProviderToSection(row.Provider);
                if (mappedSection != null
                    && string.Equals(mappedSection, trimmedSection, StringComparison.OrdinalIgnoreCase))
                {
                    var count = assistantUsage.First(u => string.Equals(u.ModelId, row.ModelId, StringComparison.Ordinal)).AssistantCount;
                    chatTargets.Add(new ConnectionUsageChatTargetRef(row.ModelId, count));
                }
            }
        }

        return new ConnectionUsageDto(
            Section: trimmedSection,
            Modes: matchingModes
                .OrderBy(m => m.Service, StringComparer.Ordinal)
                .ThenBy(m => m.ModeId, StringComparer.Ordinal)
                .ToList(),
            ChatTargets: chatTargets);
    }

    public Task<ProviderSectionReadinessDto> GetProviderSectionReadinessAsync(string sectionName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sectionName))
        {
            throw new ArgumentException("Section name is required.", nameof(sectionName));
        }

        return Task.FromResult(EvaluateProviderSectionReadiness(sectionName));
    }

    private static bool IsErrorForService(ServiceContract contract, string error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return false;
        }

        return contract.ErrorKeys.Any(key => error.Contains(key, StringComparison.OrdinalIgnoreCase));
    }

    private ProviderSectionReadinessDto EvaluateProviderSectionReadiness(string sectionName)
    {
        var missing = new List<string>();

        // LocalServiceHosts:*BaseUrl sections are runtime-owned. The section name
        // IS the configuration key; the field list is just the key itself.
        if (sectionName.StartsWith("LocalServiceHosts:", StringComparison.OrdinalIgnoreCase))
        {
            var value = RuntimeConfigurationPlaceholders.NormalizeUrlOrNull(_configuration[sectionName]);
            if (string.IsNullOrWhiteSpace(value))
            {
                missing.Add(sectionName);
            }

            return new ProviderSectionReadinessDto(
                SectionName: sectionName,
                Configured: missing.Count == 0,
                MissingFields: missing);
        }

        if (!ProviderSectionRequirements.TryGetValue(sectionName, out var requirement))
        {
            return new ProviderSectionReadinessDto(
                SectionName: sectionName,
                Configured: false,
                MissingFields: [$"{sectionName} has no registered field requirements."]);
        }

        foreach (var field in requirement.RequiredFields)
        {
            var rawValue = _configuration[$"{sectionName}:{field}"];
            var value = string.Equals(field, "BaseUrl", StringComparison.OrdinalIgnoreCase)
                ? RuntimeConfigurationPlaceholders.NormalizeUrlOrNull(rawValue)
                : RuntimeConfigurationPlaceholders.NormalizeConfiguredValueOrNull(rawValue);
            if (string.IsNullOrWhiteSpace(value))
            {
                missing.Add(field);
            }
        }

        foreach (var alternatives in requirement.AlternativeFieldGroups)
        {
            var hasAny = alternatives.Any(field =>
            {
                var rawValue = _configuration[$"{sectionName}:{field}"];
                var value = string.Equals(field, "BaseUrl", StringComparison.OrdinalIgnoreCase)
                    ? RuntimeConfigurationPlaceholders.NormalizeUrlOrNull(rawValue)
                    : RuntimeConfigurationPlaceholders.NormalizeConfiguredValueOrNull(rawValue);
                return !string.IsNullOrWhiteSpace(value);
            });

            if (!hasAny)
            {
                missing.Add(string.Join(" or ", alternatives));
            }
        }

        return new ProviderSectionReadinessDto(
            SectionName: sectionName,
            Configured: missing.Count == 0,
            MissingFields: missing);
    }

    private bool SectionHasMeaningfulConfiguredValue(SettingsSectionDefinition definition, ApplicationSetting row)
    {
        var protector = GetProtector();
        var decrypted = ApplicationSettingsJson.DecryptSecrets(
            definition,
            ApplicationSettingsJson.DeserializeObject(row.JsonValue),
            _settingsSecretsOptionsMonitor.CurrentValue,
            protector.Protect);

        foreach (var property in definition.Properties)
        {
            if (!decrypted.TryGetPropertyValue(property.Name, out var node) || node is null)
            {
                continue;
            }

            if (node.GetValueKind() == System.Text.Json.JsonValueKind.Null)
            {
                continue;
            }

            if (node.GetValueKind() == System.Text.Json.JsonValueKind.String
                && string.IsNullOrWhiteSpace(node.GetValue<string>()))
            {
                continue;
            }

            if (node.GetValueKind() == System.Text.Json.JsonValueKind.String
                && RuntimeConfigurationPlaceholders.IsKnownPlaceholderValue(node.GetValue<string>()))
            {
                continue;
            }

            var defaultNode = ToJsonNode(property.DefaultValue);
            if (defaultNode != null && JsonNode.DeepEquals(node, defaultNode))
            {
                continue;
            }

            return true;
        }

        return false;
    }
}
