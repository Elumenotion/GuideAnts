using GuideAntsApi.Models.Settings;
using GuideAntsApi.Options;
using GuideAntsApi.Configuration;
using GuideAntsApi.DataModel.Models;
using Microsoft.EntityFrameworkCore;
using System.Text.Json.Nodes;

namespace GuideAntsApi.Settings;

public sealed partial class ApplicationSettingsService
{
    private static readonly string[] SecretKeyFragments = ["token", "key", "secret", "password"];

    private static bool LooksLikeSecretKey(string key)
    {
        var leaf = key.Contains(':', StringComparison.Ordinal)
            ? key[(key.LastIndexOf(':') + 1)..]
            : key;

        foreach (var fragment in SecretKeyFragments)
        {
            if (leaf.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The set of runtime-owned configuration keys surfaced by the Infrastructure
    /// tab (R-5.7). Exposed so <c>InfrastructureProbeService</c> can probe every
    /// URL-shaped value without re-declaring the catalog.
    /// </summary>
    public static IReadOnlyList<string> GetRuntimeDependencyKeys() =>
        RuntimeDependencyCatalog.Select(dependency => dependency.Key).ToList();

    /// <summary>
    /// Phase E (R-5.7): build the full Infrastructure tab dependency list with
    /// resolved source/value/hasValue/isSecret/kind fields. Exposed via
    /// <see cref="GetRuntimeDependenciesAsync"/> so the Infrastructure tab can
    /// render source badges without pulling the whole schema.
    /// </summary>
    private IReadOnlyList<SettingsRuntimeDependencyDto> BuildRuntimeDependencies()
    {
        return RuntimeDependencyCatalog
            .Select(dependency =>
            {
                var value = RuntimeConfigurationPlaceholders.NormalizeUrlOrNull(_configuration[dependency.Key]);
                var hasValue = !string.IsNullOrWhiteSpace(value);
                var isSecret = LooksLikeSecretKey(dependency.Key);
                // Secret-adjacent keys are masked server-side; today's catalog
                // holds only base URLs + paths, but defense-in-depth keeps the
                // mask in place so future secret keys don't leak by omission.
                var serializedValue = isSecret ? null : value;
                return new SettingsRuntimeDependencyDto(
                    Key: dependency.Key,
                    CurrentValue: serializedValue,
                    ReadOnly: dependency.ReadOnly,
                    UsedByProviderIds: dependency.UsedByProviderIds,
                    Source: RuntimeDependencySourceResolver.Resolve(_configuration, dependency.Key),
                    HasValue: hasValue,
                    IsSecret: isSecret,
                    Kind: ClassifyDependencyKind(dependency.Key, value));
            })
            .ToList();
    }

    /// <summary>
    /// Infrastructure tab discriminator that drives the Phase E UI probe
    /// strategy. URL-shaped values ("http://", "https://") are probed by
    /// reachability; path-shaped values are probed by existence+writability;
    /// anything else is rendered without a probe button.
    /// </summary>
    private static string ClassifyDependencyKind(string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return "url";
            }

            if (value.Contains(Path.DirectorySeparatorChar)
                || value.Contains(Path.AltDirectorySeparatorChar))
            {
                return "path";
            }
        }

        // Key shape is a secondary signal: "*BaseUrl" reads as url-intended,
        // "*Path" reads as path-intended. This lets us classify unset values
        // so the UI can still decide which kind of probe button to render.
        if (key.EndsWith("BaseUrl", StringComparison.Ordinal)
            || key.EndsWith("Url", StringComparison.Ordinal))
        {
            return "url";
        }

        if (key.EndsWith("Path", StringComparison.Ordinal))
        {
            return "path";
        }

        return "other";
    }

    public Task<IReadOnlyList<SettingsRuntimeDependencyDto>> GetRuntimeDependenciesAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(BuildRuntimeDependencies());
    }

    public async Task<SettingsRuntimeDependencyDto?> SetRuntimeDependencyOverrideAsync(
        string key,
        string? value,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidOperationException("Runtime dependency key is required.");
        }

        var contract = RuntimeDependencyCatalog.FirstOrDefault(d =>
            string.Equals(d.Key, key, StringComparison.Ordinal));
        if (contract is null)
        {
            return null;
        }

        if (contract.ReadOnly)
        {
            throw new InvalidOperationException($"Runtime dependency '{key}' is read-only.");
        }

        var separator = key.IndexOf(':');
        if (separator <= 0 || separator >= key.Length - 1)
        {
            throw new InvalidOperationException($"Runtime dependency key '{key}' is not a supported Section:Field key.");
        }

        var sectionName = key[..separator];
        var fieldName = key[(separator + 1)..];
        if (!_registry.TryGet(sectionName, out var definition))
        {
            throw new InvalidOperationException($"Settings section '{sectionName}' is not supported.");
        }

        var row = await _db.ApplicationSettings
            .SingleOrDefaultAsync(x => x.SectionName == sectionName, cancellationToken)
            .ConfigureAwait(false);
        var trimmedValue = value?.Trim();
        var normalizedValue = RuntimeConfigurationPlaceholders.NormalizeUrlOrNull(trimmedValue);

        if (string.IsNullOrWhiteSpace(normalizedValue))
        {
            if (row is not null)
            {
                var payload = ApplicationSettingsJson.DeserializeObject(row.JsonValue);
                payload.Remove(fieldName);

                if (payload.Count == 0)
                {
                    _db.ApplicationSettings.Remove(row);
                }
                else
                {
                    row.JsonValue = ApplicationSettingsJson.Serialize(payload);
                    row.SchemaVersion = definition.SchemaVersion;
                    row.UpdatedUtc = DateTime.UtcNow;
                }

                await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                ReloadConfiguration();
            }

            return BuildRuntimeDependencies().FirstOrDefault(d => string.Equals(d.Key, key, StringComparison.Ordinal));
        }

        ValidateRuntimeDependencyOverrideValue(contract, normalizedValue);

        if (row is null)
        {
            var payload = new JsonObject
            {
                [fieldName] = normalizedValue
            };
            _db.ApplicationSettings.Add(new ApplicationSetting
            {
                SectionName = sectionName,
                SchemaVersion = definition.SchemaVersion,
                JsonValue = ApplicationSettingsJson.Serialize(payload),
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            });
        }
        else
        {
            var payload = ApplicationSettingsJson.DeserializeObject(row.JsonValue);
            payload[fieldName] = normalizedValue;
            row.JsonValue = ApplicationSettingsJson.Serialize(payload);
            row.SchemaVersion = definition.SchemaVersion;
            row.UpdatedUtc = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        ReloadConfiguration();
        return BuildRuntimeDependencies().FirstOrDefault(d => string.Equals(d.Key, key, StringComparison.Ordinal));
    }

    private static void ValidateRuntimeDependencyOverrideValue(RuntimeDependencyContract contract, string value)
    {
        var expectedKind = ClassifyDependencyKind(contract.Key, null);
        if (!string.Equals(expectedKind, "url", StringComparison.Ordinal))
        {
            return;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException($"Runtime dependency '{contract.Key}' must be an absolute URL.");
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Runtime dependency '{contract.Key}' must use http:// or https://.");
        }

        if (string.IsNullOrWhiteSpace(uri.Host))
        {
            throw new InvalidOperationException($"Runtime dependency '{contract.Key}' must include a host.");
        }

        if (string.Equals(contract.Key, ServiceRoutingContracts.LlamaBaseUrlKey, StringComparison.Ordinal))
        {
            var normalizedPath = uri.AbsolutePath.TrimEnd('/');
            normalizedPath = string.IsNullOrWhiteSpace(normalizedPath) ? "/" : normalizedPath;
            if (!string.Equals(normalizedPath, ServiceRoutingContracts.LlamaCppPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Runtime dependency '{ServiceRoutingContracts.LlamaBaseUrlKey}' must include the '{ServiceRoutingContracts.LlamaCppPath}' path.");
            }
        }
    }

    private static readonly IReadOnlyList<RuntimeDependencyContract> RuntimeDependencyCatalog =
    [
        new(
            Key: ServiceRoutingContracts.LlamaBaseUrlKey,
            UsedByProviderIds: [],
            ReadOnly: false),
        new(
            Key: "LocalServiceHosts:SpeechTranscriptionBaseUrl",
            UsedByProviderIds: [ServiceProviderIds.SpeechTranscriptionLocalAsrHttp],
            ReadOnly: false),
        new(
            Key: "LocalServiceHosts:SpeechSynthesisBaseUrl",
            UsedByProviderIds: [ServiceProviderIds.SpeechSynthesisLocalTtsHttp],
            ReadOnly: false),
        new(
            Key: "LocalServiceHosts:ImageGenerationBaseUrl",
            UsedByProviderIds: [ServiceProviderIds.ImageGenerationLocalSdHttp],
            ReadOnly: false),
        new(
            Key: "LocalServiceHosts:EmbeddingsBaseUrl",
            UsedByProviderIds: [ServiceProviderIds.EmbeddingsLocalEmbHttp],
            ReadOnly: false),
        new(
            Key: "LocalServiceHosts:MediaBaseUrl",
            UsedByProviderIds: [ServiceProviderIds.SpeechTranscriptionLocalAsrHttp],
            ReadOnly: false),
        new(
            Key: "LocalServiceHosts:DocumentIntelligenceBaseUrl",
            UsedByProviderIds: [ServiceProviderIds.DocumentIntelligenceLocalDoclingHttp],
            ReadOnly: false)
    ];
}
