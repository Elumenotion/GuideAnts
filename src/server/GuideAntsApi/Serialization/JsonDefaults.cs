using System.Text.Json;
using System.Text.Json.Serialization;

namespace GuideAntsApi.Serialization;

/// <summary>
/// Single source of truth for the API's JSON serializer options.
/// Every path that emits dates to clients (HTTP endpoints, SSE streaming) must
/// build its options from here so the UTC "Z"-suffix invariant cannot drift.
/// </summary>
public static class JsonDefaults
{
    private static readonly UtcDateTimeJsonConverter UtcDateTimeConverter = new();

    /// <summary>Options for HTTP endpoint request/response bodies (minimal web defaults).</summary>
    public static JsonSerializerOptions CreateWebApiOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        options.Converters.Add(UtcDateTimeConverter);
        return options;
    }

    /// <summary>
    /// Applies the web-API options in place to an existing <see cref="JsonSerializerOptions"/>
    /// instance. <c>JsonOptions.SerializerOptions</c> (Minimal API / ConfigureHttpJsonOptions)
    /// is a read-only property whose backing instance must be mutated, not replaced.
    /// </summary>
    public static void ApplyWebApiOptions(JsonSerializerOptions options)
    {
        options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.NumberHandling = JsonNumberHandling.AllowReadingFromString;
        options.Converters.Clear();
        options.Converters.Add(new JsonStringEnumConverter());
        options.Converters.Add(UtcDateTimeConverter);
    }

    /// <summary>
    /// Options for the SSE conversation stream. Deliberately NOT web defaults: the
    /// stream's existing payload shape (default enum numbers, camelCase) is a client
    /// contract; only the UTC date converter is added on top.
    /// </summary>
    public static JsonSerializerOptions CreateSseOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        options.Converters.Add(UtcDateTimeConverter);
        return options;
    }
}
