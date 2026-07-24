using System.Text.RegularExpressions;

namespace GuideAntsApi.Configuration;

public static class DocumentServerUrlResolver
{
    public const string ProxyPublicPrefix = "/api/documentserver/ds";

    private static readonly Regex VersionedRuntimePathRegex = new(
        @"^/\d+\.\d+\.\d+-[0-9a-f]+(?:/|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// DocumentServer serves hashed runtime bundles at the app origin root, for example
    /// <c>/9.3.1-{hash}/web-apps/...</c>, even when <c>api.js</c> is loaded through
    /// <see cref="ProxyPublicPrefix"/>.
    /// </summary>
    public static bool IsVersionedRuntimePath(PathString path)
    {
        var value = path.Value;
        return !string.IsNullOrEmpty(value) && VersionedRuntimePathRegex.IsMatch(value);
    }

    /// <summary>
    /// Browser-facing DocumentServer URL for the API proxy. Always the inbound request
    /// origin + <see cref="ProxyPublicPrefix"/> so local Docker
    /// (<c>localhost:5107</c>) and Azure public FQDNs both work. Do not use
    /// <c>DocumentServer:ApiBaseUrl</c> here — that value is the DocumentServer→API
    /// callback base (often an internal container hostname).
    /// </summary>
    public static string ResolvePublicUrl(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        if (!httpContext.Request.Host.HasValue)
        {
            return ProxyPublicPrefix;
        }

        var scheme = ResolveRequestScheme(httpContext);
        if (string.IsNullOrWhiteSpace(scheme))
        {
            return ProxyPublicPrefix;
        }

        return $"{scheme}://{httpContext.Request.Host.Value.TrimEnd('/')}{ProxyPublicPrefix}";
    }

    /// <summary>
    /// Public scheme for browser/editor URLs. Prefer the first
    /// <c>X-Forwarded-Proto</c> value when present (TLS-terminating ingress such as ACA).
    /// </summary>
    public static string ResolveRequestScheme(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        var forwarded = httpContext.Request.Headers["X-Forwarded-Proto"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(forwarded))
        {
            var proto = forwarded.Split(',', 2)[0].Trim();
            if (proto.Equals("https", StringComparison.OrdinalIgnoreCase)
                || proto.Equals("http", StringComparison.OrdinalIgnoreCase))
            {
                return proto.ToLowerInvariant();
            }
        }

        return httpContext.Request.Scheme?.Trim() ?? string.Empty;
    }

    /// <summary>
    /// Host header for the upstream DocumentServer request. ACA internal ingress routes
    /// by Host, so this must be the InternalUrl authority (including
    /// <c>*.internal.{env-domain}</c>), not the browser's public host.
    /// </summary>
    public static string ResolveUpstreamHost(Uri destinationUri) =>
        destinationUri.IsDefaultPort
            ? destinationUri.IdnHost
            : destinationUri.Authority;
}
