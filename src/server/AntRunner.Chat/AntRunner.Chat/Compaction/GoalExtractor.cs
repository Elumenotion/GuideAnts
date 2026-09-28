using System.Text.RegularExpressions;

namespace AntRunner.Chat.Compaction;

internal sealed record GoalSection(string? InitialGoal, IReadOnlyList<string> ScopeChanges);

internal static class GoalExtractor
{
    private const int GoalMaxChars = 500;
    private const int ScopeChangeMaxChars = 300;

    private static readonly Regex ScopeChangeMarkerRegex = new(
        @"\b(actually|instead|scratch that|never mind|change of plan|new plan|forget (that|it)|let'?s switch|hold on)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static GoalSection Extract(IReadOnlyList<CompactionBlock> blocks)
    {
        var userMessages = blocks.Where(b => b.Kind == CompactionBlockKind.UserMessage).ToList();
        if (userMessages.Count == 0)
        {
            return new GoalSection(null, []);
        }

        var initialGoal = CompactionText.Truncate(userMessages[0].Text, GoalMaxChars);

        var scopeChanges = userMessages
            .Skip(1)
            .Where(m => ScopeChangeMarkerRegex.IsMatch(m.Text))
            .Select(m => CompactionText.Truncate(m.Text, ScopeChangeMaxChars))
            .ToList();

        return new GoalSection(initialGoal, scopeChanges);
    }
}
