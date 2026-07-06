using System.Text.RegularExpressions;

namespace GuideAntsApi.Services.Bootstrap;

internal static class LocalServiceModelRefRules
{
    private static readonly Regex LoadableLocalModelRefPattern = new(
        @"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    internal static bool IsLoadableLocalModelRef(string? modelRef)
    {
        if (string.IsNullOrWhiteSpace(modelRef))
        {
            return false;
        }

        return LoadableLocalModelRefPattern.IsMatch(modelRef.Trim());
    }
}
