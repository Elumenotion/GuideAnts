namespace GuideAntsApi.Configuration;

internal static class RuntimeConfigurationPlaceholders
{
    public static bool HasUsableUrl(string? value) =>
        !string.IsNullOrWhiteSpace(value) && !IsDiscardLoopbackUrl(value);

    public static string? NormalizeUrlOrNull(string? value) =>
        HasUsableUrl(value) ? value : null;

    public static bool IsDiscardLoopbackUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return uri.Port == 9 && uri.IsLoopback;
    }
}
