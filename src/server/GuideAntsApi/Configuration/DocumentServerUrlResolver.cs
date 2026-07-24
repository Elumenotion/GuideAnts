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
    /// Browser-facing DocumentServer URL for the API proxy.
    /// Host always comes from the inbound request (browser origin: localhost, ACA FQDN, etc.).
    /// Scheme comes from <see cref="DocumentServerOptions.ApiBaseUrl"/> when configured —
    /// that is the public API origin scheme (https on Azure, http in local Docker). Behind
    /// TLS-terminating ingress <c>Request.Scheme</c> is http and is not used for the browser URL.
    /// </summary>
    public static string ResolvePublicUrl(DocumentServerOptions options, HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(httpContext);

        if (!httpContext.Request.Host.HasValue)
        {
            return ProxyPublicPrefix;
        }

        var scheme = ResolvePublicScheme(options, httpContext);
        if (string.IsNullOrWhiteSpace(scheme))
        {
            return ProxyPublicPrefix;
        }

        return $"{scheme}://{httpContext.Request.Host.Value.TrimEnd('/')}{ProxyPublicPrefix}";
    }

    /// <summary>
    /// Scheme for browser-facing DocumentServer URLs and upstream X-Forwarded-Proto.
    /// Prefer <see cref="DocumentServerOptions.ApiBaseUrl"/> scheme, then
    /// <c>X-Forwarded-Proto</c>, then <c>Request.Scheme</c>.
    /// </summary>
    public static string ResolvePublicScheme(DocumentServerOptions options, HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(httpContext);

        var configured = options.ApiBaseUrl?.Trim();
        if (!string.IsNullOrWhiteSpace(configured)
            && Uri.TryCreate(configured, UriKind.Absolute, out var apiBaseUri)
            && (apiBaseUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)
                || apiBaseUri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)))
        {
            return apiBaseUri.Scheme.ToLowerInvariant();
        }

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
