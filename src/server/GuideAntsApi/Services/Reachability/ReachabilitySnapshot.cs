using GuideAntsApi.Configuration;
using GuideAntsApi.Endpoints;
using GuideAntsApi.Models.Settings;
using GuideAntsApi.Services.Routing;

namespace GuideAntsApi.Services.Reachability;

/// <summary>
/// Builds the per-endpoint presence list from the Plane C cache. Synchronous and
/// non-throwing: an unobserved endpoint renders Unknown ("not yet observed"), never a
/// spinner, never a blank, never a thrown error (F-2 corollary 4 / P6). Shared by the
/// overview handler and the reachability endpoints so presence is derived one way.
/// </summary>
public static class ReachabilitySnapshot
{
    /// <summary>The routed local services plus llama, matching the probe universe.</summary>
    private static readonly string[] PresenceServiceIds =
    [
        RoutedServiceNames.SpeechTranscription,
        RoutedServiceNames.SpeechSynthesis,
        RoutedServiceNames.Embeddings,
        RoutedServiceNames.ImageGeneration,
        RoutedServiceNames.DocumentIntelligence,
    ];

    public static IReadOnlyList<EndpointPresenceDto> BuildPresence(
        ILocalEndpointCache cache,
        IConfiguration configuration)
    {
        var list = new List<EndpointPresenceDto>(PresenceServiceIds.Length + 1);

        foreach (var serviceId in PresenceServiceIds)
        {
            var adminBase = LocalServiceAdminRouting.ResolveAdminBase(serviceId, configuration);
            if (string.IsNullOrWhiteSpace(adminBase))
            {
                continue;
            }

            var readinessUrl = $"{adminBase.TrimEnd('/')}{(serviceId == RoutedServiceNames.ImageGeneration ? "/health" : serviceId == RoutedServiceNames.DocumentIntelligence ? "" : "/ready")}";
            AddOne(list, serviceId, adminBase, readinessUrl, cache);
        }

        // Llama stack presence (its own base, not an admin-prefixed local service).
        var llamaBase = configuration["LlamaCpp:BaseUrl"];
        if (RuntimeConfigurationPlaceholders.HasUsableUrl(llamaBase))
        {
            AddOne(list, "llama", llamaBase!, $"{llamaBase!.TrimEnd('/')}/ready", cache);
        }

        return list;
    }

    private static void AddOne(
        List<EndpointPresenceDto> list,
        string serviceId,
        string baseUrl,
        string readinessUrl,
        ILocalEndpointCache cache)
    {
        var availability = "Unknown";
        bool? artifactPresent = null;
        DateTime? observedUtc = null;
        string? error = null;

        if (cache.TryGet(readinessUrl, out var observation))
        {
            availability = observation.Availability.ToString();
            observedUtc = observation.ObservedUtc;
            error = observation.Error;
        }

        list.Add(new EndpointPresenceDto(serviceId, baseUrl, availability, artifactPresent, SelectedRef: null, observedUtc, error));
    }
}
