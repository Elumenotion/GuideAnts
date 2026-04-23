using Microsoft.EntityFrameworkCore;
using GuideAntsApi.Models.Settings;
using GuideAntsApi.Configuration;
using GuideAntsApi.Services.Routing;

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

        var missing = new List<string>();

        // LocalServiceHosts:*BaseUrl sections are runtime-owned. The section name
        // IS the configuration key; the field list is just the key itself.
        if (sectionName.StartsWith("LocalServiceHosts:", StringComparison.OrdinalIgnoreCase))
        {
            var value = _configuration[sectionName];
            if (string.IsNullOrWhiteSpace(value))
            {
                missing.Add(sectionName);
            }

            return Task.FromResult(new ProviderSectionReadinessDto(
                SectionName: sectionName,
                Configured: missing.Count == 0,
                MissingFields: missing));
        }

        if (ProviderSectionRequiredFields.TryGetValue(sectionName, out var required))
        {
            foreach (var field in required)
            {
                var value = _configuration[$"{sectionName}:{field}"];
                if (string.IsNullOrWhiteSpace(value))
                {
                    missing.Add(field);
                }
            }
        }
        else
        {
            // Unknown provider section — surface as a structured blocker so the UI
            // can flag it rather than silently reporting "ready" (user rule: no fallback).
            missing.Add($"{sectionName} has no registered field requirements.");
        }

        // Anthropic is special: ApiKey OR AuthToken satisfies the requirement.
        // If BOTH were reported missing above, replace with a single combined blocker
        // describing the requirement.
        if (string.Equals(sectionName, "Anthropic", StringComparison.OrdinalIgnoreCase))
        {
            var hasApiKey = !string.IsNullOrWhiteSpace(_configuration["Anthropic:ApiKey"]);
            var hasAuthToken = !string.IsNullOrWhiteSpace(_configuration["Anthropic:AuthToken"]);
            missing.RemoveAll(m => string.Equals(m, "ApiKey", StringComparison.Ordinal)
                                   || string.Equals(m, "AuthToken", StringComparison.Ordinal));
            if (!hasApiKey && !hasAuthToken)
            {
                missing.Add("ApiKey or AuthToken");
            }
        }

        return Task.FromResult(new ProviderSectionReadinessDto(
            SectionName: sectionName,
            Configured: missing.Count == 0,
            MissingFields: missing));
    }

    private static bool IsErrorForService(ServiceContract contract, string error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return false;
        }

        return contract.ErrorKeys.Any(key => error.Contains(key, StringComparison.OrdinalIgnoreCase));
    }
}
