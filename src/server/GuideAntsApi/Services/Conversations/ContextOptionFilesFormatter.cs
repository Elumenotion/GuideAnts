namespace GuideAntsApi.Services.Conversations;

/// <summary>
/// Formats <c>[@files]</c> for injection into the context-options system message with hard limits
/// so artifact explosions cannot make a thread unusable.
/// </summary>
internal static class ContextOptionFilesFormatter
{
    /// <summary>Maximum paths listed before truncation.</summary>
    internal const int MaxListedPaths = 500;

    /// <summary>Maximum characters in the fenced console block (including truncation notice).</summary>
    internal const int MaxOutputCharacters = 24_000;

    /// <summary>
    /// Formats paths as a markdown console fence. When limits are exceeded, emits an explicit
    /// truncation line so the model knows the list is incomplete.
    /// </summary>
    internal static string FormatConsole(IReadOnlyList<string> paths)
    {
        var totalCount = paths.Count;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("```console");

        var listed = 0;
        for (; listed < totalCount && listed < MaxListedPaths; listed++)
        {
            var line = paths[listed] + Environment.NewLine;
            var projectedLength = sb.Length + line.Length + TruncationNoticeLength(totalCount, listed + 1) + "```".Length;
            if (projectedLength > MaxOutputCharacters)
            {
                break;
            }

            sb.Append(line);
        }

        var omitted = totalCount - listed;
        if (omitted > 0)
        {
            sb.AppendLine(FormatTruncationNotice(listed, totalCount, omitted));
        }

        sb.Append("```");
        return sb.ToString();
    }

    private static int TruncationNoticeLength(int totalCount, int listedCount) =>
        FormatTruncationNotice(listedCount, totalCount, totalCount - listedCount).Length + Environment.NewLine.Length;

    private static string FormatTruncationNotice(int listed, int total, int omitted) =>
        $"... {omitted} additional path(s) omitted (listed {listed} of {total} — truncated to protect context window)";
}
