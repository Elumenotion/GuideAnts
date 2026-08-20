using GuideAntsApi.Configuration;
using GuideAntsApi.Services.Routing;

namespace GuideAntsApi.Services.Bootstrap;

/// <summary>
/// Normalizes operator stack base URLs and derives ga-admin endpoints. Each local
/// AI stack (llama, embeddings, ASR, …) is reached through one holistic host.
/// </summary>
internal static class LocalAiStackHostUrls
{
    public const string LlamaServiceId = "llama";

    public static readonly string[] WarmupServiceIds =
    [
        LlamaServiceId,
        RoutedServiceNames.SpeechTranscription,
        RoutedServiceNames.Embeddings,
        RoutedServiceNames.SpeechSynthesis,
        RoutedServiceNames.ImageGeneration,
    ];

    public static string? NormalizeStackBaseUrl(string? url)
    {
        if (!RuntimeConfigurationPlaceholders.HasUsableUrl(url))
        {
            return null;
        }

        var uri = new Uri(url!.Trim(), UriKind.Absolute);
        var builder = new UriBuilder(uri)
        {
            Query = string.Empty,
            Fragment = string.Empty,
        };

        var path = builder.Path.TrimEnd('/');
        if (path.EndsWith(ServiceRoutingContracts.LlamaCppPath, StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^ServiceRoutingContracts.LlamaCppPath.Length];
        }

        builder.Path = string.IsNullOrEmpty(path) ? "/" : path;
        return builder.Uri.ToString().TrimEnd('/');
    }

    public static Uri DeriveAdminBaseUri(string stackBaseUrl)
    {
        var normalized = NormalizeStackBaseUrl(stackBaseUrl)
            ?? throw new ArgumentException("Stack base URL is not usable.", nameof(stackBaseUrl));
        var uri = new Uri(normalized, UriKind.Absolute);
        var builder = new UriBuilder(uri)
        {
            Query = string.Empty,
            Fragment = string.Empty,
        };
        var path = builder.Path.TrimEnd('/');
        builder.Path = path + ServiceRoutingContracts.LlamaAdminPath + "/";
        return builder.Uri;
    }

    public static Uri? TryDeriveAdminBaseUriFromLlamaCppUrl(string? llamaCppBaseUrl)
    {
        var normalized = NormalizeStackBaseUrl(llamaCppBaseUrl);
        return normalized is null ? null : DeriveAdminBaseUri(normalized);
    }

    public static Uri DeriveAdminBaseUriFromLlamaCppUrl(string llamaCppBaseUrl) =>
        TryDeriveAdminBaseUriFromLlamaCppUrl(llamaCppBaseUrl)
            ?? throw new ArgumentException("LlamaCpp base URL is not usable.", nameof(llamaCppBaseUrl));

    /// <summary>
    /// Binds llama-admin on the typed HttpClient when <paramref name="llamaCppBaseUrl"/> is a real
    /// stack host. Loopback port 9 is the operator sentinel for "not in use"; leave BaseAddress
    /// unset so client construction cannot fail API startup.
    /// </summary>
    public static void ApplyLlamaRuntimeAdminBaseAddress(HttpClient client, string llamaCppBaseUrl)
    {
        ArgumentNullException.ThrowIfNull(client);
        var adminBase = TryDeriveAdminBaseUriFromLlamaCppUrl(llamaCppBaseUrl);
        if (adminBase is not null)
        {
            client.BaseAddress = adminBase;
        }
    }
}
