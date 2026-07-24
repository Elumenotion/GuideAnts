namespace GuideAntsApi.Configuration;

public static class DocumentServerUrlResolver
{
    public const string ProxyPublicPrefix = "/api/documentserver/ds";

    /// <summary>
    /// Public browser-facing URL for ONLYOFFICE assets served through the API proxy.
    /// Prefer <see cref="DocumentServerOptions.ApiBaseUrl"/> so HTTPS is preserved behind
    /// TLS-terminating ingress (ACA, reverse proxies).
    /// </summary>
    public static string ResolvePublicUrl(DocumentServerOptions options, HttpContext? httpContext = null)
    {
        var configured = options.ApiBaseUrl?.Trim();
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (!Uri.TryCreate(configured, UriKind.Absolute, out _))
            {
                throw new InvalidOperationException("DocumentServer:ApiBaseUrl must be an absolute URL.");
            }

            return $"{configured.TrimEnd('/')}{ProxyPublicPrefix}";
        }

        if (httpContext == null)
        {
            return ProxyPublicPrefix;
        }

        var scheme = httpContext.Request.Scheme?.Trim();
        if (string.IsNullOrWhiteSpace(scheme) || !httpContext.Request.Host.HasValue)
        {
            return ProxyPublicPrefix;
        }

        return $"{scheme}://{httpContext.Request.Host.Value.TrimEnd('/')}{ProxyPublicPrefix}";
    }

    public static string ResolveUpstreamHost(Uri destinationUri) =>
        destinationUri.IsDefaultPort
            ? destinationUri.IdnHost
            : destinationUri.Authority;
}
