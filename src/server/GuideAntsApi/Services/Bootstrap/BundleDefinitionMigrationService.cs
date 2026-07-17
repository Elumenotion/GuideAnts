using System.Text.Json;
using GuideAntsApi.Models.Settings;
using GuideAntsApi.Options;
using GuideAntsApi.Services.Routing;
using GuideAntsApi.Settings;

namespace GuideAntsApi.Services.Bootstrap;

public sealed class BundleDefinitionMigrationService : IBundleDefinitionMigrationService
{
    private readonly IWebHostEnvironment _environment;
    private readonly IApplicationSettingsService _settingsService;
    private readonly ILogger<BundleDefinitionMigrationService> _logger;

    public BundleDefinitionMigrationService(
        IWebHostEnvironment environment,
        IApplicationSettingsService settingsService,
        ILogger<BundleDefinitionMigrationService> logger)
    {
        _environment = environment;
        _settingsService = settingsService;
        _logger = logger;
    }

    public async Task<BundleDefinitionMigrationReport> MigrateAsync(CancellationToken cancellationToken = default)
    {
        var store = (await _settingsService.GetImageGenerationBundleDefinitionsAsync(cancellationToken))
            .ToDictionary(x => x.BundleId, StringComparer.OrdinalIgnoreCase);
        var defaultsDiscovered = 0;
        var defaultsImported = 0;
        var samplingBackfilled = 0;
        var skippedExisting = 0;
        var failed = 0;
        var changed = new List<ImageGenerationBundleDefinitionDto>();

        ApplyLegacyBundleIdRenames(store, changed);

        foreach (var defaultDefinition in LoadCheckedInDefaults())
        {
            defaultsDiscovered++;
            var outcome = TryMergeDefinition(store, defaultDefinition, out var merged);
            switch (outcome)
            {
                case MergeOutcome.Imported:
                    defaultsImported++;
                    changed.Add(merged!);
                    break;
                case MergeOutcome.Skipped:
                    skippedExisting++;
                    break;
            }
        }

        RemoveLegacyBundleDefinitionsFromStore(store);
        EnsureMissingCanonicalBundles(store, changed);

        await _settingsService.ReplaceImageGenerationBundleDefinitionsAsync(
            store.Values.ToList(),
            cancellationToken);

        await MigrateLegacyImageGenerationServiceModeModelIdsAsync(cancellationToken);

        var report = new BundleDefinitionMigrationReport(
            defaultsDiscovered,
            defaultsImported,
            RuntimeDiscovered: 0,
            RuntimeImported: 0,
            samplingBackfilled,
            skippedExisting,
            failed);

        _logger.LogInformation(
            "ImageGeneration bundle-definition migration completed. DefaultsDiscovered={DefaultsDiscovered}, DefaultsImported={DefaultsImported}, RuntimeDiscovered={RuntimeDiscovered}, RuntimeImported={RuntimeImported}, SamplingBackfilled={SamplingBackfilled}, SkippedExisting={SkippedExisting}, Failed={Failed}",
            report.DefaultsDiscovered,
            report.DefaultsImported,
            report.RuntimeDiscovered,
            report.RuntimeImported,
            report.SamplingBackfilled,
            report.SkippedExisting,
            report.Failed);

        return report;
    }

    private void ApplyLegacyBundleIdRenames(
        Dictionary<string, ImageGenerationBundleDefinitionDto> store,
        List<ImageGenerationBundleDefinitionDto> changed)
    {
        foreach (var (legacyId, canonicalId) in ImageGenerationBundleDefinitionContracts.LegacyRenames)
        {
            if (!store.TryGetValue(legacyId, out var legacyDefinition))
            {
                continue;
            }

            store.Remove(legacyId);
            if (store.TryGetValue(canonicalId, out var existing))
            {
                var fromLegacy = legacyDefinition with { BundleId = canonicalId };
                var merged = existing with
                {
                    Roles = fromLegacy.Roles,
                    Revision = !string.IsNullOrWhiteSpace(fromLegacy.Revision)
                        ? fromLegacy.Revision
                        : existing.Revision,
                    Sampling = HasExplicitSampling(existing) ? existing.Sampling : fromLegacy.Sampling,
                    UpdatedAtUtc = DateTime.UtcNow.ToString("O"),
                };
                store[canonicalId] = merged;
                changed.Add(merged);
                _logger.LogInformation(
                    "Merged legacy ImageGeneration bundle '{LegacyId}' install config into '{CanonicalId}'.",
                    legacyId,
                    canonicalId);
                continue;
            }

            var renamed = legacyDefinition with { BundleId = canonicalId };
            store[canonicalId] = renamed;
            changed.Add(renamed);
            _logger.LogInformation(
                "Renamed ImageGeneration bundle id '{LegacyId}' to '{CanonicalId}' in API settings.",
                legacyId,
                canonicalId);
        }
    }

    private void EnsureMissingCanonicalBundles(
        Dictionary<string, ImageGenerationBundleDefinitionDto> store,
        List<ImageGenerationBundleDefinitionDto> changed)
    {
        foreach (var defaultDefinition in LoadCheckedInDefaults())
        {
            if (store.ContainsKey(defaultDefinition.BundleId))
            {
                continue;
            }

            store[defaultDefinition.BundleId] = defaultDefinition;
            changed.Add(defaultDefinition);
            _logger.LogInformation(
                "Seeded ImageGeneration bundle '{BundleId}' from checked-in defaults because no settings entry exists.",
                defaultDefinition.BundleId);
        }
    }

    private static void RemoveLegacyBundleDefinitionsFromStore(
        Dictionary<string, ImageGenerationBundleDefinitionDto> store)
    {
        foreach (var legacyId in ImageGenerationBundleDefinitionContracts.LegacyRenames.Keys)
        {
            store.Remove(legacyId);
        }
    }

    private async Task MigrateLegacyImageGenerationServiceModeModelIdsAsync(CancellationToken cancellationToken)
    {
        var modes = await _settingsService
            .GetServiceModesAsync(RoutedServiceNames.ImageGeneration, cancellationToken)
            .ConfigureAwait(false);
        var localProviderSection = $"{LocalServiceHostsOptions.SectionName}:ImageGenerationBaseUrl";
        var localMode = modes.FirstOrDefault(mode =>
            string.Equals(mode.ProviderSection, localProviderSection, StringComparison.OrdinalIgnoreCase));
        var modelId = localMode?.ModelId?.Trim();
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return;
        }

        var normalized = ImageGenerationBundleDefinitionContracts.NormalizeBundleId(modelId);
        if (string.Equals(normalized, modelId, StringComparison.Ordinal))
        {
            return;
        }

        await _settingsService
            .SetServiceModeModelIdAsync(RoutedServiceNames.ImageGeneration, normalized, cancellationToken)
            .ConfigureAwait(false);
        _logger.LogInformation(
            "Renamed ImageGeneration ServiceModes local bundle id '{LegacyId}' to '{CanonicalId}'.",
            modelId,
            normalized);
    }

    private IEnumerable<ImageGenerationBundleDefinitionDto> LoadCheckedInDefaults()
    {
        var root = Path.Combine(_environment.ContentRootPath, ImageGenerationBundleDefinitionContracts.DefaultsRelativePath);
        var indexPath = Path.Combine(root, "index.json");
        if (!File.Exists(indexPath))
        {
            _logger.LogWarning(
                "ImageGeneration bundle defaults index not found at {Path}.",
                indexPath);
            yield break;
        }

        using var indexDoc = JsonDocument.Parse(File.ReadAllText(indexPath));
        if (!indexDoc.RootElement.TryGetProperty("bundles", out var bundlesElement)
            || bundlesElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException(
                $"ImageGeneration bundle defaults index '{indexPath}' must contain a 'bundles' array.");
        }

        foreach (var entry in bundlesElement.EnumerateArray())
        {
            var bundleId = entry.TryGetProperty("bundleId", out var bundleIdElement)
                ? bundleIdElement.GetString()?.Trim()
                : null;
            var definitionPath = entry.TryGetProperty("definitionPath", out var pathElement)
                ? pathElement.GetString()?.Trim()
                : null;

            if (string.IsNullOrWhiteSpace(bundleId) || string.IsNullOrWhiteSpace(definitionPath))
            {
                throw new InvalidOperationException(
                    "ImageGeneration bundle defaults index entry must include bundleId and definitionPath.");
            }

            var filePath = Path.Combine(root, definitionPath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(filePath))
            {
                throw new InvalidOperationException(
                    $"ImageGeneration bundle default definition file not found: {filePath}");
            }

            var definition = BundleDefinitionJson.Deserialize(File.ReadAllText(filePath));
            if (definition is null)
            {
                throw new InvalidOperationException(
                    $"ImageGeneration bundle default definition file is invalid: {filePath}");
            }

            var errors = BundleDefinitionValidator.Validate(definition);
            if (errors.Count > 0)
            {
                throw new InvalidOperationException(
                    $"ImageGeneration bundle default definition '{filePath}' failed validation: {string.Join(' ', errors)}");
            }

            if (!string.Equals(definition.BundleId, bundleId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"ImageGeneration bundle default definition '{filePath}' bundleId '{definition.BundleId}' does not match index entry '{bundleId}'.");
            }

            yield return definition;
        }
    }

    private enum MergeOutcome
    {
        Imported,
        Skipped,
    }

    private static MergeOutcome TryMergeDefinition(
        Dictionary<string, ImageGenerationBundleDefinitionDto> store,
        ImageGenerationBundleDefinitionDto incoming,
        out ImageGenerationBundleDefinitionDto? merged)
    {
        merged = null;
        if (store.ContainsKey(incoming.BundleId))
        {
            return MergeOutcome.Skipped;
        }

        store[incoming.BundleId] = incoming;
        merged = incoming;
        return MergeOutcome.Imported;
    }

    private static bool HasExplicitSampling(ImageGenerationBundleDefinitionDto definition)
    {
        return definition.Sampling is { Steps: > 0, CfgScale: > 0 }
               && !string.IsNullOrWhiteSpace(definition.Sampling.SamplingMethod);
    }
}
