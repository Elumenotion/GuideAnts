using System.Text.Json.Serialization;

namespace GuideAntsApi.Services.Routing;

/// <summary>Where a resolved context-window value came from.</summary>
// Wire contract is the enum name. The server already writes it that way via the global converter;
// the attribute makes every other reader (tests, external clients) agree without extra options.
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ContextWindowSource
{
    Unknown,
    Learned,
    Catalog,
    LiveRuntime
}

/// <summary>
/// A resolved context window. <see cref="ContextWindowTokens"/> is null when no source knows
/// the value — callers must render an explicit unknown state rather than assume a default.
/// </summary>
public sealed record ContextWindowInfo(
    int? ContextWindowTokens,
    int? MaxOutputTokens,
    ContextWindowSource Source);

public interface IContextWindowResolver
{
    /// <param name="liveRuntimeContextSize">
    /// The loaded context size reported by a local runtime, when one is serving this model.
    /// Null for cloud models. This is the loaded window, not <c>n_ctx_train</c>.
    /// </param>
    ContextWindowInfo Resolve(string modelId, int? liveRuntimeContextSize);
}
