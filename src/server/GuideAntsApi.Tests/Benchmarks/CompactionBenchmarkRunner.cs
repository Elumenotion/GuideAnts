using System.Text;
using AntRunner.Chat.Abstractions;
using GuideAntsApi.DataModel;
using GuideAntsApi.Services.Conversations.Mapping;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.Tests.Benchmarks;

internal sealed record BenchmarkCandidate(
    Guid ConversationId,
    string ShortId,
    int BoundaryTurnIndex,
    int CompletedTurns,
    string Domain,
    IReadOnlyList<string> ToolNames);

internal sealed record BenchmarkEntry(
    BenchmarkCandidate Candidate,
    int PreBoundaryChars,
    string SummaryText,
    IReadOnlyDictionary<string, int> SectionLineCounts,
    int UnknownOutcomeLines,
    bool Deterministic);

/// <summary>
/// W9 / spec Testing item 5: runs the production compaction summary path over real conversations and
/// renders what it produced, for a human to read. Measures; does not judge -- the verdict is Task 5's.
/// </summary>
internal static class CompactionBenchmarkRunner
{
    /// <summary>Tool names that mark a conversation as coding work. Code_Executor is the crew coding sub-agent.</summary>
    internal static readonly string[] CodingToolNames = ["run_python", "run_bash", "code_interpreter", "Code_Executor"];

    internal static readonly string[] Sections = ["Goal", "Artifacts", "Activity ledger", "Unresolved errors", "Directives"];

    private static readonly string[] Placeholders = ["(none)", "(none recorded)", "(no tool calls)"];

    public static async Task<IReadOnlyList<BenchmarkCandidate>> SelectCandidatesAsync(
        ApplicationDbContext db, int minCompletedTurns, CancellationToken ct)
    {
        // Boundary = the turn a fresh Compact press would pick: CompactionService.cs:104,
        // max(TurnIndex where Status == "completed").
        var completed = await db.ConversationTurns
            .AsNoTracking()
            .Where(t => t.Status == "completed")
            .GroupBy(t => t.NotebookConversationId)
            .Select(g => new { ConversationId = g.Key, Count = g.Count(), Boundary = g.Max(t => t.TurnIndex) })
            .Where(g => g.Count >= minCompletedTurns)
            .ToListAsync(ct);

        var ids = completed.Select(c => c.ConversationId).ToList();
        var toolRows = await db.NotebookConversationMessages
            .AsNoTracking()
            .Where(m => ids.Contains(m.NotebookConversationId) && m.FunctionName != null)
            .Select(m => new { m.NotebookConversationId, m.FunctionName })
            .Distinct()
            .ToListAsync(ct);

        return completed
            .OrderByDescending(c => c.Count)
            .ThenBy(c => c.ConversationId)
            .Select(c =>
            {
                var tools = toolRows
                    .Where(r => r.NotebookConversationId == c.ConversationId)
                    .Select(r => r.FunctionName!)
                    .OrderBy(n => n, StringComparer.Ordinal)
                    .ToList();
                var domain = tools.Any(t => CodingToolNames.Contains(t, StringComparer.OrdinalIgnoreCase))
                    ? "coding"
                    : "non-coding";
                return new BenchmarkCandidate(
                    c.ConversationId, c.ConversationId.ToString("N")[..8].ToUpperInvariant(),
                    c.Boundary, c.Count, domain, tools);
            })
            .ToList();
    }

    public static async Task<BenchmarkEntry> RunAsync(
        ConversationHistoryBuilder builder, ApplicationDbContext db, BenchmarkCandidate candidate, CancellationToken ct)
    {
        var first = await builder.BuildCompactionSummaryMessageAsync(candidate.ConversationId, candidate.BoundaryTurnIndex, ct);
        var second = await builder.BuildCompactionSummaryMessageAsync(candidate.ConversationId, candidate.BoundaryTurnIndex, ct);
        var summary = first.GetText() ?? string.Empty;

        var preBoundaryChars = await db.NotebookConversationMessages
            .AsNoTracking()
            .Where(m => m.NotebookConversationId == candidate.ConversationId && m.TurnIndex <= candidate.BoundaryTurnIndex)
            .SumAsync(m => (int?)m.Content.Length, ct) ?? 0;

        return new BenchmarkEntry(
            candidate,
            preBoundaryChars,
            summary,
            CountSectionLines(summary),
            CountUnknownOutcomes(summary),
            Deterministic: string.Equals(summary, second.GetText(), StringComparison.Ordinal));
    }

    public static IReadOnlyDictionary<string, int> CountSectionLines(string summary)
    {
        var counts = Sections.ToDictionary(s => s, _ => 0);
        string? current = null;
        foreach (var raw in summary.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                current = counts.ContainsKey(line[3..]) ? line[3..] : null;
                continue;
            }

            if (current != null && line.Length > 0 && !Placeholders.Contains(line))
            {
                counts[current]++;
            }
        }

        return counts;
    }

    public static int CountUnknownOutcomes(string summary) =>
        summary.Split('\n').Count(l => l.TrimEnd().EndsWith("— unknown", StringComparison.Ordinal));

    public static string RenderConversationReport(BenchmarkEntry e)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {e.Candidate.ShortId} — {e.Candidate.Domain}");
        sb.AppendLine();
        sb.AppendLine($"- Completed turns: {e.Candidate.CompletedTurns}; boundary turn: {e.Candidate.BoundaryTurnIndex}");
        sb.AppendLine($"- Tools used: {(e.Candidate.ToolNames.Count == 0 ? "(none)" : string.Join(", ", e.Candidate.ToolNames))}");
        sb.AppendLine($"- Pre-boundary chars: {e.PreBoundaryChars}; summary chars: {e.SummaryText.Length}");
        sb.AppendLine($"- Section lines: {string.Join(", ", e.SectionLineCounts.Select(kv => $"{kv.Key} {kv.Value}"))}");
        sb.AppendLine($"- Ledger lines with unknown outcome: {e.UnknownOutcomeLines}; deterministic: {e.Deterministic}");
        sb.AppendLine();
        sb.AppendLine("## Summary as the model would receive it");
        sb.AppendLine();
        sb.AppendLine("```text");
        sb.AppendLine(e.SummaryText);
        sb.AppendLine("```");
        return sb.ToString();
    }

    public static string RenderIndex(IReadOnlyList<BenchmarkEntry> entries)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Compaction benchmark — index");
        sb.AppendLine();
        sb.AppendLine("| Conversation | Domain | Turns | Pre-boundary chars | Summary chars | Goal | Artifacts | Ledger | Errors | Directives | Unknown outcomes |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var e in entries)
        {
            var c = e.SectionLineCounts;
            sb.AppendLine(
                $"| [{e.Candidate.ShortId}]({e.Candidate.ShortId}.md) | {e.Candidate.Domain} | {e.Candidate.CompletedTurns} | " +
                $"{e.PreBoundaryChars} | {e.SummaryText.Length} | {c["Goal"]} | {c["Artifacts"]} | {c["Activity ledger"]} | " +
                $"{c["Unresolved errors"]} | {c["Directives"]} | {e.UnknownOutcomeLines} |");
        }

        return sb.ToString();
    }
}
