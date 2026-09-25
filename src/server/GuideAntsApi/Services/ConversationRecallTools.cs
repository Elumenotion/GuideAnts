using System.Text.Json;
using AntRunner.ToolCalling;
using AntRunner.ToolCalling.Attributes;
using GuideAntsApi.Services.Conversations;

namespace GuideAntsApi.Services;

/// <summary>
/// Server-handled recall over the compacted part of the current conversation (W7).
///
/// The tool-calling layer injects <c>context</c> automatically for
/// <see cref="RequiresNotebookContextAttribute"/> methods, so the OpenAPI manifest lists only the
/// model-supplied parameters. <c>ConversationId</c> comes from that injected context and can never
/// be named by the model — this is the same structural scoping that already protects
/// <c>search_project</c>.
///
/// Exposure is decided per run by <c>ChatRunOptions.EnableConversationRecall</c>, not by an
/// assistant's tool list, because whether a conversation has a compaction boundary is a property of
/// the conversation rather than of the guide (D7).
/// </summary>
public static class ConversationRecallTools
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static IServiceProvider? _provider;

    public static void InitializeServiceProvider(IServiceProvider provider) => _provider = provider;

    [Tool(
        OperationId = "conversation_recall",
        Summary = "Search the earlier, compacted part of this conversation for detail the summary left out."
    )]
    [RequiresNotebookContext]
    public static async Task<string> RecallConversation(
        [Parameter(Description = "Search terms. Use distinctive words from what you are trying to recall — names, file paths, error text — rather than a full sentence.")] string query,
        [Parameter(Description = "1-based page of results. Omit for the first page.")] int? page = null,
        [Parameter(Description = "Invocation context", Hidden = true)] InvocationContext? context = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return JsonError("query is required.");
        }

        if (context == null)
        {
            return JsonError("Invocation context is required.");
        }

        if (_provider == null)
        {
            throw new InvalidOperationException("ConversationRecallTools service provider is not initialized.");
        }

        using var scope = _provider.CreateScope();
        var recall = scope.ServiceProvider.GetRequiredService<IConversationRecallService>();

        var effectivePage = page ?? 1;
        var result = await recall.RecallAsync(context.ConversationId, query, effectivePage, cancellationToken);
        return JsonSerializer.Serialize(result, JsonOptions);
    }

    private static string JsonError(string message) =>
        JsonSerializer.Serialize(new { error = message });
}
