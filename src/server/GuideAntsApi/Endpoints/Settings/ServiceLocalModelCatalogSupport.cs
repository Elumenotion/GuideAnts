using System.Text.Json;

namespace GuideAntsApi.Endpoints.Settings;

internal static class ServiceLocalModelCatalogSupport
{
    public static bool ExposesCuratedCatalog(string serviceId) =>
        string.Equals(serviceId, "Embeddings", StringComparison.Ordinal)
        || string.Equals(serviceId, "SpeechTranscription", StringComparison.Ordinal)
        || string.Equals(serviceId, "SpeechSynthesis", StringComparison.Ordinal);

    // The curated catalog id set is fetched fresh from the engine on every download
    // validation. A download is a rare, user-initiated, heavyweight action, so one extra
    // GET /admin/catalog is negligible and avoids caching (and any staleness) entirely.
    public static async Task<CatalogIdSetResult> GetCatalogIdsAsync(
        string serviceId,
        IConfiguration configuration,
        HttpClient httpClient,
        CancellationToken cancellationToken)
    {
        var adminBase = LocalServiceAdminRouting.ResolveAdminBase(serviceId, configuration);
        if (string.IsNullOrWhiteSpace(adminBase))
        {
            return new CatalogIdSetResult(null, SettingsGroupFactory.LocalServiceUnavailable(serviceId));
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{adminBase}/admin/catalog");
        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            return new CatalogIdSetResult(
                null,
                Results.Json(
                    new { error = $"Could not load the curated model catalog: {ex.Message}" },
                    statusCode: StatusCodes.Status502BadGateway));
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                return new CatalogIdSetResult(
                    null,
                    Results.Json(
                        new
                        {
                            error = "Could not load the curated model catalog from the local engine.",
                            upstreamStatus = (int)response.StatusCode,
                            upstreamBody = body,
                        },
                        statusCode: StatusCodes.Status502BadGateway));
            }

            var payload = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!TryParseCatalogIds(payload, out var ids, out var parseError))
            {
                return new CatalogIdSetResult(null, Results.BadRequest(new { error = parseError }));
            }

            return new CatalogIdSetResult(ids, null);
        }
    }

    /// <summary>
    /// Reads the curated catalog's version (F-5: the only engine fact beyond the API's own
    /// payload that install memory records, read once at download acceptance). Returns null
    /// on any failure — version is enrichment, not truth.
    /// </summary>
    public static async Task<string?> TryReadCatalogVersionAsync(
        string serviceId,
        IConfiguration configuration,
        HttpClient httpClient,
        CancellationToken cancellationToken)
    {
        var adminBase = LocalServiceAdminRouting.ResolveAdminBase(serviceId, configuration);
        if (string.IsNullOrWhiteSpace(adminBase))
        {
            return null;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{adminBase}/admin/catalog");
            using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(payload);
            return doc.RootElement.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal static bool TryParseCatalogIds(string payload, out HashSet<string> ids, out string error)
    {
        ids = new HashSet<string>(StringComparer.Ordinal);
        error = string.Empty;

        try
        {
            using var doc = JsonDocument.Parse(payload);
            return TryParseCatalogIds(doc.RootElement, out ids, out error);
        }
        catch (JsonException)
        {
            error = "Curated model catalog response was not valid JSON.";
            return false;
        }
    }

    internal static bool TryParseCatalogIds(JsonElement root, out HashSet<string> ids, out string error)
    {
        ids = new HashSet<string>(StringComparer.Ordinal);
        error = string.Empty;

        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("entries", out var entries)
            || entries.ValueKind != JsonValueKind.Array)
        {
            error = "Curated model catalog response is missing an entries array.";
            return false;
        }

        foreach (var entry in entries.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object
                || !entry.TryGetProperty("id", out var idProp)
                || idProp.ValueKind != JsonValueKind.String)
            {
                error = "Curated model catalog entry is missing a string id.";
                return false;
            }

            var id = idProp.GetString();
            if (string.IsNullOrWhiteSpace(id))
            {
                error = "Curated model catalog entry id must not be empty.";
                return false;
            }

            ids.Add(id.Trim());
        }

        if (ids.Count == 0)
        {
            error = "Curated model catalog has no entries.";
            return false;
        }

        return true;
    }

    internal readonly record struct CatalogIdSetResult(HashSet<string>? Ids, IResult? Error);
}
