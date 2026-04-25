namespace GuideAntsApi.Configuration;

internal static class RuntimeConfigurationPlaceholders
{
    public static bool HasUsableValue(string? value) =>
        !string.IsNullOrWhiteSpace(value) && !IsKnownPlaceholderValue(value);

    public static string? NormalizeConfiguredValueOrNull(string? value)
    {
        if (!HasUsableValue(value))
        {
            return null;
        }

        return value!.Trim();
    }

    public static bool HasUsableUrl(string? value) =>
        HasUsableValue(value) && !IsDiscardLoopbackUrl(value);

    public static string? NormalizeUrlOrNull(string? value) =>
        HasUsableUrl(value) ? value : null;

    public static bool IsKnownPlaceholderValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        if (trimmed.Contains("replace-with", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return string.Equals(uri.Host, "example.cognitiveservices.azure.com", StringComparison.OrdinalIgnoreCase);
    }

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
