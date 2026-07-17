using System.Text.Json;
using System.Text.Json.Nodes;
using GuideAntsApi.Endpoints;
using GuideAntsApi.Options;
using GuideAntsApi.Services.Bootstrap;
using GuideAntsApi.Services.Routing;
using GuideAntsApi.Settings;
using Microsoft.AspNetCore.Http;

namespace GuideAntsApi.Endpoints.Settings;

/// <summary>
/// Injects ServiceModes selection into proxied local inventory responses so UI
/// and operators never treat engine disk markers as the selected model/bundle.
/// </summary>
internal static class ServiceLocalModelListEnricher
{
    public static async Task<IResult> ProxyAndEnrichImageBundlesAsync(
        HttpClient httpClient,
        HttpRequestMessage request,
        IApplicationSettingsService settings,
        CancellationToken cancellationToken)
    {
        var upstreamTarget = request.RequestUri?.ToString() ?? string.Empty;
        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            return Results.Json(
                new LocalServiceAdminRouting.UpstreamProxyEnvelope(
                    Error: $"Upstream request to {upstreamTarget} failed: {ex.Message}",
                    UpstreamTarget: upstreamTarget,
                    UpstreamStatus: 0,
                    UpstreamStatusText: "NetworkError",
                    UpstreamContentType: string.Empty,
                    UpstreamBody: string.Empty),
                statusCode: StatusCodes.Status502BadGateway);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return Results.Json(
                    new LocalServiceAdminRouting.UpstreamProxyEnvelope(
                        Error: "Upstream local service returned a non-success status.",
                        UpstreamTarget: upstreamTarget,
                        UpstreamStatus: (int)response.StatusCode,
                        UpstreamStatusText: response.StatusCode.ToString(),
                        UpstreamContentType: response.Content.Headers.ContentType?.MediaType ?? string.Empty,
                        UpstreamBody: body),
                    statusCode: StatusCodes.Status502BadGateway);
            }

            var selectedBundleId = await ConfiguredLocalServiceSelectionSync.ResolveOrSyncImageBundleAsync(
                    settings,
                    ServiceLocalModelListEnricher.ReadConfiguredActiveBundleId(body),
                    cancellationToken)
                .ConfigureAwait(false);
            var enriched = ApplyImageBundleSelection(body, selectedBundleId);
            return Results.Content(enriched, "application/json");
        }
    }

    public static string? ReadConfiguredActiveBundleId(string upstreamBody)
    {
        try
        {
            var root = JsonNode.Parse(upstreamBody) as JsonObject;
            var marker = root?["activeBundleMarkerId"]?.GetValue<string>()?.Trim()
                ?? root?["legacyMarkerBundleId"]?.GetValue<string>()?.Trim()
                ?? root?["activeBundleId"]?.GetValue<string>()?.Trim();
            return string.IsNullOrWhiteSpace(marker) ? null : marker;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string ApplyImageBundleSelection(string upstreamBody, string? selectedBundleId)
    {
        var root = JsonNode.Parse(upstreamBody) as JsonObject ?? new JsonObject();
        root["selectedBundleId"] = selectedBundleId;
        root["activeBundleId"] = selectedBundleId;

        if (root["items"] is JsonArray items)
        {
            foreach (var itemNode in items)
            {
                if (itemNode is not JsonObject item)
                {
                    continue;
                }

                var bundleId = item["bundleId"]?.GetValue<string>();
                var isSelected = !string.IsNullOrWhiteSpace(selectedBundleId)
                    && !string.IsNullOrWhiteSpace(bundleId)
                    && string.Equals(bundleId, selectedBundleId, StringComparison.Ordinal);
                item["active"] = isSelected;
            }
        }

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }
}
