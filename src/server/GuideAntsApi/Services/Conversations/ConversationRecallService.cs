using System.Text.RegularExpressions;
using GuideAntsApi.DataModel;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.Services.Conversations;

/// <summary>One matching pre-boundary message. Structure (turn, sequence, role, tool name) is kept
/// alongside the text so the model can tell a tool result from something the user said.</summary>
public sealed record ConversationRecallHit(
    int TurnIndex,
    int MessageSequence,
    string Role,
    string? FunctionName,
    DateTime Created,
    string Excerpt);

/// <summary>One page of recall results.</summary>
/// <param name="BoundaryTurnIndex">The conversation's compaction boundary, echoed back so the model
/// can see what it is searching. Null means the conversation was never compacted, in which case
/// there is nothing to recall and <c>Results</c> is always empty (D7).</param>
public sealed record ConversationRecallPage(
    string Query,
    int Page,
    int PageSize,
    int TotalMatches,
    bool HasMore,
    int? BoundaryTurnIndex,
    IReadOnlyList<ConversationRecallHit> Results);

public interface IConversationRecallService
{
    Task<ConversationRecallPage> RecallAsync(
        Guid conversationId, string query, int page, CancellationToken ct = default);
}

/// <summary>
/// Searches the part of a conversation that compaction replaced with a summary
/// (<c>TurnIndex &lt;= CompactionBoundaryTurnIndex</c>, matching
/// <see cref="Mapping.ConversationHistoryBuilder"/>'s pre-boundary selection exactly).
///
/// Ranking is an in-memory tf-idf scan over the candidate set — deliberately not SQL Server
/// full-text, so behavior is identical on every deployment including the containerized SQL Server
/// image (spec: Recall / Ranking).
///
/// Scoping is structural: the conversation id is a parameter supplied by the runtime from
/// <c>InvocationContext</c>, never by the model. This type has no code path that widens the search
/// past the single conversation it was handed.
/// </summary>
public sealed class ConversationRecallService : IConversationRecallService
{
    /// <summary>Results per page. Small on purpose: recall output is re-billed as context on every
    /// later round, so a page plus its excerpts must stay cheap.</summary>
    internal const int PageSize = 5;

    /// <summary>Maximum excerpt length in UTF-16 units, before ellipses.</summary>
    internal const int MaxExcerptChars = 600;

    /// <summary>How much of an excerpt sits before the first matched term.</summary>
    private const int ExcerptLeadChars = 120;

    /// <summary>Shortest query term worth scoring. One-character terms match almost everything.</summary>
    private const int MinTermLength = 2;

    /// <summary>Cap on distinct query terms, so a pathological query cannot make the scan quadratic.</summary>
    private const int MaxQueryTerms = 12;

    /// <summary>Upper bound on candidate messages pulled into memory. Far above any realistic
    /// notebook conversation; present so a runaway conversation cannot exhaust the process.</summary>
    private const int MaxCandidateMessages = 5000;

    private static readonly Regex TermSplitter = new("[^\\p{L}\\p{N}]+", RegexOptions.Compiled);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ConversationRecallService> _logger;

    public ConversationRecallService(
        IServiceScopeFactory scopeFactory,
        ILogger<ConversationRecallService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<ConversationRecallPage> RecallAsync(
        Guid conversationId, string query, int page, CancellationToken ct = default)
    {
        query ??= string.Empty;
        page = Math.Max(1, page);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var boundary = await db.NotebookConversations
            .Where(c => c.Id == conversationId)
            .Select(c => c.CompactionBoundaryTurnIndex)
            .FirstOrDefaultAsync(ct);

        var terms = Tokenize(query);
        if (boundary == null || terms.Count == 0)
        {
            return Empty(query, page, boundary);
        }

        var candidates = await db.NotebookConversationMessages
            .AsNoTracking()
            .Where(m => m.NotebookConversationId == conversationId && m.TurnIndex <= boundary.Value)
            .OrderBy(m => m.TurnIndex).ThenBy(m => m.MessageSequence)
            .Take(MaxCandidateMessages)
            .Select(m => new Candidate(
                m.TurnIndex, m.MessageSequence, m.Role.ToString(), m.FunctionName, m.Created, m.Content))
            .ToListAsync(ct);

        if (candidates.Count == 0)
        {
            return Empty(query, page, boundary);
        }

        var lowered = new string[candidates.Count];
        var termFrequency = new int[candidates.Count][];
        var documentFrequency = new int[terms.Count];

        for (var i = 0; i < candidates.Count; i++)
        {
            lowered[i] = candidates[i].Content?.ToLowerInvariant() ?? string.Empty;
            termFrequency[i] = new int[terms.Count];

            for (var t = 0; t < terms.Count; t++)
            {
                var occurrences = CountOccurrences(lowered[i], terms[t]);
                termFrequency[i][t] = occurrences;
                if (occurrences > 0)
                {
                    documentFrequency[t]++;
                }
            }
        }

        var scored = new List<(double Score, int Index)>();
        for (var i = 0; i < candidates.Count; i++)
        {
            var score = 0.0;
            for (var t = 0; t < terms.Count; t++)
            {
                if (termFrequency[i][t] == 0)
                {
                    continue;
                }

                // idf damps terms that appear in most of the conversation (say, the project's own
                // name); the log on tf stops one message repeating a term from dominating.
                var idf = Math.Log(1.0 + (double)candidates.Count / (1 + documentFrequency[t]));
                score += idf * (1.0 + Math.Log(termFrequency[i][t]));
            }

            if (score > 0)
            {
                scored.Add((score, i));
            }
        }

        // Candidates are already in (TurnIndex, MessageSequence) order, so falling back to the index
        // makes ties deterministic and chronological.
        scored.Sort((a, b) =>
        {
            var byScore = b.Score.CompareTo(a.Score);
            return byScore != 0 ? byScore : a.Index.CompareTo(b.Index);
        });

        var skip = (page - 1) * PageSize;
        var results = scored
            .Skip(skip)
            .Take(PageSize)
            .Select(s =>
            {
                var candidate = candidates[s.Index];
                return new ConversationRecallHit(
                    candidate.TurnIndex,
                    candidate.MessageSequence,
                    candidate.Role,
                    candidate.FunctionName,
                    candidate.Created,
                    BuildExcerpt(candidate.Content ?? string.Empty, lowered[s.Index], terms));
            })
            .ToList();

        _logger.LogDebug(
            "conversation_recall matched {MatchCount} pre-boundary messages of {CandidateCount} for conversation {ConversationId}",
            scored.Count, candidates.Count, conversationId);

        return new ConversationRecallPage(
            query, page, PageSize, scored.Count, skip + results.Count < scored.Count, boundary, results);
    }

    private static ConversationRecallPage Empty(string query, int page, int? boundary) =>
        new(query, page, PageSize, 0, false, boundary, []);

    private static List<string> Tokenize(string query) =>
        TermSplitter
            .Split(query.ToLowerInvariant())
            .Where(t => t.Length >= MinTermLength)
            .Distinct(StringComparer.Ordinal)
            .Take(MaxQueryTerms)
            .ToList();

    private static int CountOccurrences(string haystack, string needle)
    {
        if (haystack.Length == 0)
        {
            return 0;
        }

        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    /// <summary>
    /// Returns a bounded window of the message centred on its earliest matched term. Both ends are
    /// nudged off surrogate boundaries: splitting a pair would emit invalid UTF-16 into the tool
    /// result, which the JSON serializer turns into replacement characters.
    /// </summary>
    private static string BuildExcerpt(string content, string lowered, IReadOnlyList<string> terms)
    {
        if (content.Length == 0)
        {
            return string.Empty;
        }

        var firstMatch = -1;
        foreach (var term in terms)
        {
            var index = lowered.IndexOf(term, StringComparison.Ordinal);
            if (index >= 0 && (firstMatch < 0 || index < firstMatch))
            {
                firstMatch = index;
            }
        }

        if (firstMatch < 0)
        {
            firstMatch = 0;
        }

        var start = Math.Max(0, firstMatch - ExcerptLeadChars);
        if (start > 0 && char.IsLowSurrogate(content[start]))
        {
            start--;
        }

        var length = Math.Min(MaxExcerptChars, content.Length - start);
        if (start + length < content.Length && char.IsHighSurrogate(content[start + length - 1]))
        {
            length--;
        }

        var prefix = start > 0 ? "…" : string.Empty;
        var suffix = start + length < content.Length ? "…" : string.Empty;
        return prefix + content.Substring(start, length) + suffix;
    }

    private sealed record Candidate(
        int TurnIndex,
        int MessageSequence,
        string Role,
        string? FunctionName,
        DateTime Created,
        string? Content);
}
