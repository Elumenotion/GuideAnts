namespace GuideAnts.Logging;

/// <summary>
/// Neutralizes line breaks in values written to log sinks (CWE-117 / log forging).
/// </summary>
public static class LogValueSanitizer
{
    public static string? Sanitize(string? value) => value?.ReplaceLineEndings(" ");

    public static string? Sanitize(Guid value) => Sanitize(value.ToString());

    public static string? Sanitize(object? value) => Sanitize(value?.ToString());
}
