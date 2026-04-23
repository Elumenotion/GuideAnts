using GuideAntsApi.Models.Settings;
using GuideAntsApi.Options;

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
                var value = _configuration[dependency.Key];
                var hasValue = !string.IsNullOrWhiteSpace(value);
                var isSecret = LooksLikeSecretKey(dependency.Key);
                // Secret-adjacent keys are masked server-side; today's catalog
                // holds only base URLs + paths, but defense-in-depth keeps the
                // mask in place so future secret keys don't leak by omission.
                var serializedValue = isSecret ? null : value;
                return new SettingsRuntimeDependencyDto(
                    Key: dependency.Key,
                    DisplayName: dependency.DisplayName,
                    CurrentValue: serializedValue,
                    ReadOnly: true,
                    ChangeHint: dependency.ChangeHint,
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

    private static readonly IReadOnlyList<RuntimeDependencyContract> RuntimeDependencyCatalog =
    [
        new(
            Key: "LlamaCpp:BaseUrl",
            DisplayName: "Llama.cpp Server Base URL",
            ChangeHint: RuntimeChangeHint,
            UsedByProviderIds: []),
        new(
            Key: "LocalServiceHosts:SpeechTranscriptionBaseUrl",
            DisplayName: "Speech Transcription Base URL",
            ChangeHint: RuntimeChangeHint,
            UsedByProviderIds: [ServiceProviderIds.SpeechTranscriptionLocalAsrHttp]),
        new(
            Key: "LocalServiceHosts:SpeechSynthesisBaseUrl",
            DisplayName: "Speech Synthesis Base URL",
            ChangeHint: RuntimeChangeHint,
            UsedByProviderIds: [ServiceProviderIds.SpeechSynthesisLocalTtsHttp]),
        new(
            Key: "LocalServiceHosts:ImageGenerationBaseUrl",
            DisplayName: "Image Generation Base URL",
            ChangeHint: RuntimeChangeHint,
            UsedByProviderIds: [ServiceProviderIds.ImageGenerationLocalSdHttp]),
        new(
            Key: "LocalServiceHosts:EmbeddingsBaseUrl",
            DisplayName: "Embeddings Base URL",
            ChangeHint: RuntimeChangeHint,
            UsedByProviderIds: [ServiceProviderIds.EmbeddingsLocalEmbHttp]),
        new(
            Key: "LocalServiceHosts:MediaBaseUrl",
            DisplayName: "Media Extraction Base URL",
            ChangeHint: RuntimeChangeHint,
            UsedByProviderIds: [ServiceProviderIds.SpeechTranscriptionLocalAsrHttp]),
        new(
            Key: "LocalServiceHosts:DocumentIntelligenceBaseUrl",
            DisplayName: "Markdown Extraction Base URL",
            ChangeHint: RuntimeChangeHint,
            UsedByProviderIds: [ServiceProviderIds.DocumentIntelligenceLocalDoclingHttp])
    ];
}
