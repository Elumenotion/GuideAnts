using GuideAntsApi.Models.Settings;
using GuideAntsApi.Services.Reachability;

namespace GuideAntsApi.Endpoints.Settings;

/// <summary>
/// Plane C surface for configuration UIs. Both routes are configuration (R-A): they
/// perform zero inline probes. GET returns the current cache snapshot instantly; POST
/// signals interest in a refresh and returns that same snapshot WITHOUT awaiting the
/// probe (F-2 corollary 3 — Refresh must not become the sneaky await). The next truth
/// arrives asynchronously via the background loop (≤15 s) or the signaled wake.
/// </summary>
public static class SettingsReachabilityEndpoints
{
    public static void MapSettingsReachabilityEndpoints(this WebApplication app)
    {
        var group = SettingsGroupFactory.MapCoreGroup(app);

        group.MapGet("/reachability", (
            ILocalEndpointCache endpointCache,
            IConfiguration configuration) =>
        {
            IReadOnlyList<EndpointPresenceDto> presence = ReachabilitySnapshot.BuildPresence(endpointCache, configuration);
            return Results.Ok(presence);
        })
        .WithName("GetReachability")
        .Produces<IReadOnlyList<EndpointPresenceDto>>(StatusCodes.Status200OK);

        group.MapPost("/reachability/refresh", (
            [Microsoft.AspNetCore.Mvc.FromQuery] string? serviceId,
            ILocalEndpointCache endpointCache,
            IConfiguration configuration) =>
        {
            // Signal interest only. We do NOT await ObserveAsync here — that would put a
            // socket on a persisting/configuration request (R2/S-2/F-2). The client shows
            // "checking…", keeps the button enabled, and reads the updated snapshot next poll.
            if (!string.IsNullOrWhiteSpace(serviceId))
            {
                foreach (var url in ResolveUrlsForService(serviceId, configuration))
                {
                    endpointCache.SignalRefresh(url);
                }
            }
            else
            {
                foreach (var url in ResolveAllUrls(configuration))
                {
                    endpointCache.SignalRefresh(url);
                }
            }

            IReadOnlyList<EndpointPresenceDto> presence = ReachabilitySnapshot.BuildPresence(endpointCache, configuration);
            return Results.Ok(presence);
        })
        .WithName("RefreshReachability")
        .Produces<IReadOnlyList<EndpointPresenceDto>>(StatusCodes.Status200OK);
    }

    private static IEnumerable<string> ResolveUrlsForService(string serviceId, IConfiguration configuration)
    {
        var adminBase = Endpoints.LocalServiceAdminRouting.ResolveAdminBase(serviceId, configuration);
        if (!string.IsNullOrWhiteSpace(adminBase))
        {
            yield return $"{adminBase.TrimEnd('/')}{(serviceId == Services.Routing.RoutedServiceNames.ImageGeneration ? "/health" : serviceId == Services.Routing.RoutedServiceNames.DocumentIntelligence ? "" : "/ready")}";
            yield break;
        }

        if (serviceId is "llama" && Configuration.RuntimeConfigurationPlaceholders.HasUsableUrl(configuration["LlamaCpp:BaseUrl"]))
        {
            yield return $"{configuration["LlamaCpp:BaseUrl"]!.TrimEnd('/')}/ready";
        }
    }

    private static IEnumerable<string> ResolveAllUrls(IConfiguration configuration)
    {
        foreach (var serviceId in new[]
                 {
                     Services.Routing.RoutedServiceNames.SpeechTranscription,
                     Services.Routing.RoutedServiceNames.SpeechSynthesis,
                     Services.Routing.RoutedServiceNames.Embeddings,
                     Services.Routing.RoutedServiceNames.ImageGeneration,
                     Services.Routing.RoutedServiceNames.DocumentIntelligence,
                 })
        {
            foreach (var url in ResolveUrlsForService(serviceId, configuration))
            {
                yield return url;
            }
        }

        foreach (var url in ResolveUrlsForService("llama", configuration))
        {
            yield return url;
        }
    }
}
