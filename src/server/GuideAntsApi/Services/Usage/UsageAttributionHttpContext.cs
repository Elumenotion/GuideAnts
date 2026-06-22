using Microsoft.AspNetCore.Http;

namespace GuideAntsApi.Services.Usage;

public sealed record UsageAttributionContext(
    Guid? PublishedGuideId,
    string? SourceChannel,
    string? ExternalRequestId,
    string? ExternalUserIdentity);

public static class UsageAttributionHttpContext
{
    public const string ItemKey = "UsageAttributionContext";

    public static void Set(HttpContext httpContext, UsageAttributionContext attribution)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(attribution);
        httpContext.Items[ItemKey] = attribution;
    }

    public static UsageAttributionContext? TryGet(IHttpContextAccessor accessor)
    {
        ArgumentNullException.ThrowIfNull(accessor);
        return TryGet(accessor.HttpContext);
    }

    public static UsageAttributionContext? TryGet(HttpContext? httpContext)
    {
        if (httpContext == null)
        {
            return null;
        }

        return httpContext.Items.TryGetValue(ItemKey, out var value)
            ? value as UsageAttributionContext
            : null;
    }

    public static string ResolveExternalRequestId(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        var requestId = httpContext.Request.Headers["x-request-id"].ToString();
        if (!string.IsNullOrWhiteSpace(requestId))
        {
            return requestId.Trim();
        }

        requestId = httpContext.Request.Headers["OpenAI-Request-ID"].ToString();
        if (!string.IsNullOrWhiteSpace(requestId))
        {
            return requestId.Trim();
        }

        return string.IsNullOrWhiteSpace(httpContext.TraceIdentifier)
            ? Guid.NewGuid().ToString("N")
            : httpContext.TraceIdentifier;
    }
}
