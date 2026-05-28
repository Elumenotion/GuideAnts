using System.Text.Json;
using GuideAntsApi.BackgroundJobs.Services.Search;
using GuideAntsApi.Models;
using GuideAntsApi.Options;
using GuideAnts.Usage;
using AntRunner.ToolCalling;
using AntRunner.ToolCalling.Attributes;

namespace GuideAntsApi.Services;

/// <summary>
/// Static semantic-memory tools exposed to ChatRunner.
/// The tool-calling layer automatically injects <c>projectId</c> and (optionally) <c>notebookId</c>
/// so the OpenAPI manifests only list the user–supplied parameter (query / question).
/// These IDs MUST remain in the method signature to satisfy reflection-based invocation.
/// </summary>
public static class MemoryTools
{
    private static IServiceProvider? _provider;
    private static ILogger? _log;

    public static void InitializeServiceProvider(IServiceProvider provider)
    {
        _provider = provider;
        _log = provider.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(MemoryTools));
    }

    // ----------------------------  PROJECT  ----------------------------

    /// <summary>Semantic search across an entire project.</summary>
    [Tool(
        OperationId = "search_project",
        Summary = "Search inside the whole project."
    )]
    [RequiresNotebookContext]
    public static async Task<string> SearchProjectContent(
        [Parameter(Description = "Semantic query terms")] string query,
        [Parameter(Description = "Invocation context", Hidden = true)] InvocationContext? context = null)
        => await HybridSearchAsync(query, context?.ProjectId.ToString() ?? "", notebookId: null,
            context: context);

    // ---------------------------- NOTEBOOK ----------------------------

    /// <summary>Semantic search limited to a single notebook.</summary>
    [Tool(
        OperationId = "search_notebook",
        Summary = "Search inside the current notebook."
    )]
    [RequiresNotebookContext]
    public static async Task<string> SearchLocalContent(
        [Parameter(Description = "Search query")] string query,
        [Parameter(Description = "Invocation context", Hidden = true)] InvocationContext? context = null)
        => await HybridSearchAsync(query, context?.ProjectId.ToString() ?? "", context?.NotebookId,
            context: context);

    [Tool(
        OperationId = "WebSearch",
        Summary = "Search the web with searXNG."
    )]
    [RequiresNotebookContext]
    public static async Task<string> WebSearch(
        [Parameter(Description = "Search query")] string q,
        [Parameter(Description = "Number of results to return")] int count = SearXngSearchOptions.DefaultCount,
        [Parameter(Description = "Number of results to skip")] int skip = SearXngSearchOptions.DefaultSkip,
        [Parameter(Description = "Invocation context", Hidden = true)] InvocationContext? context = null)
    {
        if (string.IsNullOrWhiteSpace(q))
            return Json("Query cannot be empty.");

        count = Math.Max(1, count);
        skip = Math.Max(0, skip);

        using var scope = _provider!.CreateScope();
        var webSearchService = scope.ServiceProvider.GetRequiredService<IWebSearchService>();

        WebSearchToolResponse response;
        try
        {
            response = await webSearchService.SearchAsync(q, count, skip);
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Web search failed for query: {Query}", q);
            return Json($"Web search failed: {ex.Message}");
        }

        try
        {
            var recorder = scope.ServiceProvider.GetService<IUsageRecorder>();
            if (recorder != null && context != null)
            {
                await recorder.RecordAsync(
                    projectId: context.ProjectId,
                    notebookId: context.NotebookId,
                    category: UsageCategory.Search,
                    service: "SearXngSearch",
                    operation: "WebSearch",
                    metrics: new UsageMetrics(ValueOther: 1),
                    conversationId: context.ConversationId,
                    metadataJson: JsonSerializer.Serialize(new
                    {
                        query = q,
                        count,
                        skip,
                        resultsCount = response.Web.Results.Count
                    }),
                    assistantId: context.AssistantId,
                    agentInvocationId: context.CurrentInvocationId,
                    notebookConversationMessageId: context.NotebookConversationMessageIdForUsage);
            }
        }
        catch
        {
            // best effort usage recording
        }

        return JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = true });
    }

    // ---------------------------- INDEX ----------------------------

    /// <summary>Semantic search using a specific assistant files index.</summary>
    [Tool(
        OperationId = "SearchAssistantFiles",
        Summary = "Search the assistant's internal knowledge base."
    )]
    [RequiresNotebookContext]
    public static async Task<string> SearchAssistantFiles(
        [Parameter(Description = "Search query")] string query,
        [Parameter(Description = "Assistant definition", Hidden = true)] AntRunner.ToolCalling.AssistantDefinitions.AssistantDefinition? assistantDefinition = null,
        [Parameter(Description = "Invocation context", Hidden = true)] InvocationContext? context = null)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Json("Query cannot be empty.");
        
        if (assistantDefinition == null)
            return Json("Assistant definition is required.");
        
        // Determine indexName from the AssistantDefinition:
        // - Database-backed: use Id as indexName
        // - File-based: use vectorStoreId from ToolResources
        string? indexName = assistantDefinition.Id?.ToString() 
            ?? assistantDefinition.ToolResources?.FileSearch?.VectorStoreIds?.FirstOrDefault();
        
        if (string.IsNullOrWhiteSpace(indexName))
            return Json("No vector store configured for this assistant.");

        using var scope = _provider!.CreateScope();
        var searcher = scope.ServiceProvider.GetRequiredService<IHybridSearcher>();

        var results = await searcher.SearchAsync(
            query: query,
            indexName: indexName,
            notebookId: null, // Assistant files don't have notebook scope
            maxResults: DefaultLimit);

        // Consolidate overlapping chunks from the same file
        var consolidatedResults = ConsolidateOverlappingChunks(results);

        // Keep no-result logging sparse; search tools can be high-volume.
        if (results.Count == 0)
        {
            _log?.LogWarning("Assistant files search returned no results for query: {Query}", query);
        }

        // Track usage
        try
        {
            var recorder = scope.ServiceProvider.GetService<IUsageRecorder>();
            if (recorder != null && context != null)
            {
                await recorder.RecordAsync(
                    projectId: context.ProjectId,
                    notebookId: context.NotebookId,
                    category: UsageCategory.Search,
                    service: "HybridSearch",
                    operation: "SearchAssistant",
                    metrics: new UsageMetrics(ValueOther: 1),
                    conversationId: context.ConversationId,
                    metadataJson: JsonSerializer.Serialize(new { query, indexName, resultsCount = consolidatedResults.Count }),
                    assistantId: context.AssistantId,
                    agentInvocationId: context.CurrentInvocationId,
                    notebookConversationMessageId: context.NotebookConversationMessageIdForUsage);
            }
        }
        catch { /* best-effort */ }

        return JsonSerializer.Serialize(consolidatedResults);
    }

    private const int DefaultLimit = 5;

    private static async Task<string> HybridSearchAsync(string query,
                                                        string projectId,
                                                        Guid? notebookId = null,
                                                        InvocationContext? context = null)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Json("Query cannot be empty.");
        if (string.IsNullOrWhiteSpace(projectId))
            return Json("projectId is required.");

        using var scope = _provider!.CreateScope();
        var searcher = scope.ServiceProvider.GetRequiredService<IHybridSearcher>();

        var results = await searcher.SearchAsync(
            query: query,
            indexName: projectId,
            notebookId: notebookId,
            maxResults: DefaultLimit);

        // Consolidate overlapping chunks from the same file
        var consolidatedResults = ConsolidateOverlappingChunks(results);

        // Keep no-result logging sparse; search tools can be high-volume.
        if (results.Count == 0)
        {
            _log?.LogWarning("Hybrid search returned no results for query: {Query}", query);
        }

        // Track usage
        try
        {
            var recorder = scope.ServiceProvider.GetService<GuideAnts.Usage.IUsageRecorder>();
            if (recorder != null && Guid.TryParse(projectId, out var projId))
            {
                await recorder.RecordAsync(
                    projectId: projId,
                    notebookId: notebookId ?? Guid.Empty,
                    category: GuideAnts.Usage.UsageCategory.Search,
                    service: "HybridSearch",
                    operation: notebookId.HasValue ? "SearchNotebook" : "SearchProject",
                    metrics: new GuideAnts.Usage.UsageMetrics(ValueOther: 1),
                    conversationId: context?.ConversationId,
                    metadataJson: JsonSerializer.Serialize(new { query, notebookOnly = notebookId.HasValue, resultsCount = consolidatedResults.Count }),
                    assistantId: context?.AssistantId,
                    agentInvocationId: context?.CurrentInvocationId,
                    notebookConversationMessageId: context?.NotebookConversationMessageIdForUsage);
            }
        }
        catch { /* best-effort */ }

        return JsonSerializer.Serialize(consolidatedResults.Take(DefaultLimit), new JsonSerializerOptions { WriteIndented = true });
    }

    private static List<HybridSearchResult> ConsolidateOverlappingChunks(IReadOnlyList<HybridSearchResult> results)
    {
        // Group results by file ID for project/notebook files, or by IndexName for assistant files
        var fileGroups = results
            .GroupBy(r => r.ContentFileId ?? r.NotebookFileId ?? Guid.Empty)
            .ToList();

        var consolidated = new List<HybridSearchResult>();

        foreach (var fileGroup in fileGroups)
        {
            var fileId = fileGroup.Key;

            // For assistant files (no ContentFileId/NotebookFileId), group by IndexName instead
            if (fileId == Guid.Empty)
            {
                // Group assistant files by their IndexName (vector store ID)
                var assistantGroups = fileGroup
                    .GroupBy(r => r.IndexName)
                    .ToList();

                foreach (var assistantGroup in assistantGroups)
                {
                    ProcessChunkGroup(assistantGroup, consolidated);
                }
                continue;
            }

            // Process regular project/notebook files
            ProcessChunkGroup(fileGroup, consolidated);
        }

        return consolidated.OrderByDescending(c => c.Score).ToList();
    }

    private static void ProcessChunkGroup(IEnumerable<HybridSearchResult> chunks, List<HybridSearchResult> consolidated)
    {
        var sortedChunks = chunks.OrderBy(c => c.ChunkIndex).ToList();
        if (sortedChunks.Count == 0) return;

        // Find contiguous sequences of chunks
        var sequences = new List<List<HybridSearchResult>>();
        var currentSequence = new List<HybridSearchResult> { sortedChunks[0] };

        for (int i = 1; i < sortedChunks.Count; i++)
        {
            // If chunk index is sequential, add to current sequence
            if (sortedChunks[i].ChunkIndex == sortedChunks[i - 1].ChunkIndex + 1)
            {
                currentSequence.Add(sortedChunks[i]);
            }
            else
            {
                // Start new sequence
                sequences.Add(currentSequence);
                currentSequence = new List<HybridSearchResult> { sortedChunks[i] };
            }
        }
        sequences.Add(currentSequence);

        // Create consolidated results from each sequence
        foreach (var sequence in sequences)
        {
            var bestChunk = sequence.OrderByDescending(c => c.Score).First();
            var combinedContent = CombineOverlappingChunks(sequence.Select(c => c.Content).ToList());

            consolidated.Add(new HybridSearchResult
            {
                ChunkId = bestChunk.ChunkId,
                Content = combinedContent,
                ContentFileId = bestChunk.ContentFileId,
                NotebookFileId = bestChunk.NotebookFileId,
                Score = bestChunk.Score, // Use best score from sequence
                VectorScore = bestChunk.VectorScore,
                TextScore = bestChunk.TextScore,
                ChunkIndex = sequence.First().ChunkIndex // Start of sequence
            });
        }
    }


    private static string CombineOverlappingChunks(List<string> chunks)
    {
        if (chunks.Count == 0) return string.Empty;
        if (chunks.Count == 1) return chunks[0];

        var result = chunks[0];

        for (int i = 1; i < chunks.Count; i++)
        {
            var currentChunk = chunks[i];

            // Since chunks have 50% overlap, find the midpoint of the previous chunk
            // and look for where the current chunk content starts
            var prevChunkMidpoint = result.Length / 2;
            var searchStart = Math.Max(0, prevChunkMidpoint);

            // Find the best overlap point by looking for the longest common substring
            // starting from the midpoint of the accumulated result
            var bestOverlapLength = 0;
            var bestOverlapStart = -1;

            // Look for overlap in the second half of accumulated result
            var searchText = result.Substring(searchStart);
            var minOverlapLength = Math.Min(100, Math.Min(searchText.Length, currentChunk.Length) / 4);

            for (int overlapLen = Math.Min(searchText.Length, currentChunk.Length); overlapLen >= minOverlapLength; overlapLen--)
            {
                var suffix = searchText.Substring(searchText.Length - overlapLen);
                var prefix = currentChunk.Substring(0, Math.Min(overlapLen, currentChunk.Length));

                if (suffix.Equals(prefix, StringComparison.Ordinal))
                {
                    bestOverlapLength = overlapLen;
                    bestOverlapStart = result.Length - overlapLen;
                    break;
                }
            }

            if (bestOverlapLength > 0)
            {
                // Remove overlap and append the non-overlapping portion
                var nonOverlappingPortion = currentChunk.Substring(bestOverlapLength);
                result += nonOverlappingPortion;
            }
            else
            {
                // No overlap found, just append with separator
                result += "\n\n" + currentChunk;
            }
        }

        return result;
    }

    // BuildTagFilters and GetMemory methods removed - Kernel Memory no longer used

    private static string Json(string message) => JsonSerializer.Serialize(new { error = message });
}
