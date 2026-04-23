using System.Text.Json.Serialization;

namespace GuideAntsApi.Models;

public sealed record WebSearchToolResponse(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("web")] WebSearchResultsEnvelope Web,
    [property: JsonPropertyName("family_friendly")] bool FamilyFriendly,
    [property: JsonPropertyName("meta")] WebSearchTelemetry? Meta = null);

public sealed record WebSearchResultsEnvelope(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("results")] IReadOnlyList<WebSearchResultItem> Results);

public sealed record WebSearchResultItem(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("snippet")] string Snippet,
    [property: JsonPropertyName("search_url")] string? SearchUrl = null);

public sealed record WebSearchTelemetry(
    [property: JsonPropertyName("profile_label")] string ProfileLabel,
    [property: JsonPropertyName("attempt_used")] int AttemptUsed,
    [property: JsonPropertyName("pool_mode")] string PoolMode,
    [property: JsonPropertyName("request_total_ms")] long RequestTotalMs,
    [property: JsonPropertyName("pages_parsed")] int PagesParsed,
    [property: JsonPropertyName("next_hops")] int NextHops);
