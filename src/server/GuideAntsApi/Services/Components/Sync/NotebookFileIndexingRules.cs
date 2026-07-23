namespace GuideAntsApi.Services.Components.Sync;

public static class NotebookFileIndexingRules
{
    private static readonly HashSet<string> DirectIndexableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".txt", ".json", ".xml", ".puml", ".yaml", ".yml", ".csv", ".sql",
        ".cs", ".js", ".ts", ".py", ".java", ".cpp", ".h",
    };

    public static bool IsDirectIndexable(string extension) =>
        DirectIndexableExtensions.Contains(extension);

    /// <summary>
    /// Identifies temporary script files created by /execute endpoint in ScriptExecutionAgent.
    /// Pattern: {32-char hex GUID}_script.{sh|ps1|py}
    /// </summary>
    public static bool IsTemporaryScriptFile(string filename)
    {
        var pattern = @"^[a-f0-9]{32}_script\.(sh|ps1|py)$";
        return System.Text.RegularExpressions.Regex.IsMatch(
            filename,
            pattern,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }
}
