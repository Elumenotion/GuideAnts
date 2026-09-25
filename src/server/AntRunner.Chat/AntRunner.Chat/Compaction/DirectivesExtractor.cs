using System.Text.RegularExpressions;

namespace AntRunner.Chat.Compaction;

internal sealed record Directive(string Text);

internal static class DirectivesExtractor
{
    private const int DirectiveMaxChars = 200;

    private static readonly Regex DirectiveRegex = new(
        @"\b(always|never|prefer|don'?t)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static List<Directive> Extract(IReadOnlyList<CompactionBlock> blocks)
    {
        var directives = new List<Directive>();
        foreach (var block in blocks)
        {
            if (block.Kind != CompactionBlockKind.UserMessage || string.IsNullOrWhiteSpace(block.Text))
            {
                continue;
            }

            if (DirectiveRegex.IsMatch(block.Text))
            {
                directives.Add(new Directive(CompactionText.Truncate(block.Text, DirectiveMaxChars)));
            }
        }

        return directives;
    }
}
