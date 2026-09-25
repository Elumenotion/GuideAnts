namespace AntRunner.Chat.Compaction;

/// <summary>
/// Renders the five extracted sections into one system-message body. The caller (W5) is
/// responsible for wrapping this text in a <c>[system: ...]</c> message and appending the
/// verbatim tail after it - the Tail section in the spec's vocabulary is not rendered here.
/// </summary>
internal static class CompactionSummaryRenderer
{
    private const string HandoffFramingLine =
        "The following is a condensed handoff briefing summarizing an earlier part of this " +
        "conversation that has been compacted. It is reference material, not something to " +
        "continue or respond to directly.";

    public static string Render(
        GoalSection goal,
        ArtifactsSection artifacts,
        IReadOnlyList<ActivityLedgerEntry> activityLedger,
        IReadOnlyList<UnresolvedError> unresolvedErrors,
        IReadOnlyList<Directive> directives,
        int summarizedMessageCount)
    {
        var lines = new List<string>
        {
            HandoffFramingLine,
            string.Empty,
            $"[Compacted {summarizedMessageCount} earlier message(s).]",
            string.Empty,
            "## Goal",
            string.IsNullOrWhiteSpace(goal.InitialGoal) ? "(none recorded)" : goal.InitialGoal
        };

        foreach (var change in goal.ScopeChanges)
        {
            lines.Add($"- Scope change: {change}");
        }

        lines.Add(string.Empty);
        lines.Add("## Artifacts");
        if (artifacts.Created.Count == 0 && artifacts.Modified.Count == 0)
        {
            lines.Add("(none)");
        }
        else
        {
            lines.AddRange(artifacts.Created.Select(path => $"- created: {path}"));
            lines.AddRange(artifacts.Modified.Select(path => $"- modified: {path}"));
        }

        lines.Add(string.Empty);
        lines.Add("## Activity ledger");
        if (activityLedger.Count == 0)
        {
            lines.Add("(no tool calls)");
        }
        else
        {
            lines.AddRange(activityLedger.Select(FormatLedgerEntry));
        }

        lines.Add(string.Empty);
        lines.Add("## Unresolved errors");
        if (unresolvedErrors.Count == 0)
        {
            lines.Add("(none)");
        }
        else
        {
            lines.AddRange(unresolvedErrors.Select(FormatUnresolvedError));
        }

        lines.Add(string.Empty);
        lines.Add("## Directives");
        lines.AddRange(directives.Count == 0
            ? ["(none)"]
            : directives.Select(d => $"- {d.Text}"));

        return string.Join("\n", lines);
    }

    private static string FormatLedgerEntry(ActivityLedgerEntry entry)
    {
        var status = entry.Succeeded switch { true => "ok", false => "error", null => "unknown" };
        var arg = string.IsNullOrEmpty(entry.KeyArgument) ? string.Empty : $" {entry.KeyArgument}";
        return $"- {entry.ToolName}{arg} — {status}";
    }

    private static string FormatUnresolvedError(UnresolvedError error)
    {
        var arg = string.IsNullOrEmpty(error.KeyArgument) ? string.Empty : $" {error.KeyArgument}";
        return $"- {error.ToolName}{arg}";
    }
}
