namespace AntRunner.Chat.Compaction;

internal sealed record UnresolvedError(string ToolName, string? KeyArgument);

internal static class UnresolvedErrorsExtractor
{
    public static List<UnresolvedError> Extract(IReadOnlyList<ActivityLedgerEntry> ledger)
    {
        var unresolved = new List<UnresolvedError>();

        for (var i = 0; i < ledger.Count; i++)
        {
            var entry = ledger[i];
            if (entry.Succeeded != false)
            {
                continue; // only explicit failures count; unknown (null) and success (true) are not errors
            }

            var hasLaterSuccess = false;
            for (var j = i + 1; j < ledger.Count; j++)
            {
                if (string.Equals(ledger[j].ToolName, entry.ToolName, StringComparison.Ordinal)
                    && ledger[j].Succeeded == true)
                {
                    hasLaterSuccess = true;
                    break;
                }
            }

            if (!hasLaterSuccess)
            {
                unresolved.Add(new UnresolvedError(entry.ToolName, entry.KeyArgument));
            }
        }

        return unresolved;
    }
}
