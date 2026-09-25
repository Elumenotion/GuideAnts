using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AntRunner.Chat.Abstractions;
using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.IntegrationTests.Infrastructure;
using GuideAntsApi.Models.Conversations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using DataModelChatRole = GuideAntsApi.DataModel.Models.ChatRole;

namespace GuideAntsApi.IntegrationTests.Services.Conversations;

/// <summary>
/// W9 / spec "Integration": long conversation -> overflow error -> compact -> continue -> recall, through
/// the real HTTP, ConversationService, ThreadRun and SQL Server path. The provider is the fake one, so
/// this runs in CI; CompactionLlamaCppEndToEndTests runs the same story against a real llama.cpp.
/// </summary>
[TestClass]
public sealed class CompactionEndToEndTests : BaseEndpointTest
{
    private const int SetupTurns = 6;
    private const int PaddingChars = 3000;

    // One token for the recall tokenizer ([^\p{L}\p{N}]+ splits on hyphens), placed in a middle turn so
    // neither the Goal extractor (first user message) nor the verbatim tail carries it.
    private const string RecallMarker = "RECALLTARGET7Q2X";
    private const int RecallMarkerTurn = 3;

    [ClassInitialize]
    public static Task ClassInitialize(TestContext context) => InitializeSharedFactoryAsync(context);

    [ClassCleanup]
    public static Task ClassCleanup() => DisposeSharedFactoryAsync();

    [TestMethod]
    public async Task LongConversation_Overflows_Compacts_Continues_AndRecallsWhatTheSummaryReplaced()
    {
        var conv = await BuildOverflowedThenCompactedConversationAsync();
        var behavior = FakeChatCompletionBehavior.Instance;

        // Phase 4 -- continue. Same budget that overflowed in phase 2; compaction is the remedy.
        var continueEvents = await SendAsync(conv, "Continuing after compaction.");
        continueEvents.Should().NotContain(e => e.EventType == StreamingEventTypes.Error,
            "the compacted prompt must fit where the uncompacted one did not");
        continueEvents.Should().Contain(e => e.EventType == StreamingEventTypes.Complete);

        var requestText = string.Join("\n", behavior.LastRequestMessages!.Select(m => m.GetText()));
        requestText.Should().Contain(ConversationStreamTestHelpers.HandoffFramingFragment,
            "the summary system message must reach the model");
        requestText.Should().NotContain(RecallMarker,
            "turn 3 is behind the boundary, so the model sees it only through the summary or recall");
        behavior.LastRequestToolNames.Should().Contain("conversation_recall",
            "a genuinely compacted conversation (not the stale-boundary fallback) must be offered recall");

        // Phase 5 -- recall. The model asks for the marker; the real tool must return it.
        behavior.Reset();
        behavior.Scenario = FakeChatScenario.ToolCallThenReply;
        behavior.ToolFunctionName = "conversation_recall";
        behavior.ToolCallId = "call_recall_e2e";
        behavior.ToolArgumentsJson = $$"""{"query":"{{RecallMarker}}"}""";
        behavior.FinalAssistantText = "Recalled the earlier code.";

        var recallEvents = await SendAsync(conv, "What was the calibration code from earlier?");
        recallEvents.Should().Contain(e => e.EventType == StreamingEventTypes.Complete);

        using var scope = SharedFactory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var recallResult = await db.NotebookConversationMessages
            .AsNoTracking()
            .Where(m => m.NotebookConversationId == conv.ConversationId
                        && m.Role == DataModelChatRole.Tool
                        && m.ToolCallId == "call_recall_e2e")
            .SingleAsync();

        // ThreadRun.SerializeToolResult re-serializes a string tool result, so the stored content is a JSON
        // string literal wrapping the tool's JSON. Unwrap it before parsing.
        var payload = recallResult.Content.StartsWith('"')
            ? JsonSerializer.Deserialize<string>(recallResult.Content)!
            : recallResult.Content;
        using var recallJson = JsonDocument.Parse(payload);
        recallJson.RootElement.GetProperty("boundaryTurnIndex").GetInt32().Should().Be(conv.BoundaryTurnIndex);
        recallJson.RootElement.GetProperty("results").EnumerateArray()
            .Select(r => r.GetProperty("excerpt").GetString())
            .Should().Contain(excerpt => excerpt != null && excerpt.Contains(RecallMarker),
                "recall must reach content the summary replaced");

        behavior.LastRequestMessages!
            .Where(m => m.Role == ChatRole.Tool)
            .Select(m => m.GetText())
            .Should().Contain(text => text != null && text.Contains(RecallMarker),
                "the recall result must be fed back to the model on the next round");
    }

    [TestMethod]
    public async Task CompactedConversation_StillOverflowing_FailsCleanly_WithoutMutatingContent()
    {
        var conv = await BuildOverflowedThenCompactedConversationAsync();

        Dictionary<Guid, string> before;
        using (var scope = SharedFactory!.Services.CreateScope())
        {
            before = await ConversationStreamTestHelpers.SnapshotMessageContentAsync(
                scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(), conv.ConversationId);
        }

        // A single message larger than the whole window: compaction cannot help, and must not pretend to.
        var events = await SendAsync(conv, new string('z', conv.PromptCharBudget + 1));

        events.Should().Contain(e => e.EventType == StreamingEventTypes.Error
                                     && e.Payload.Contains("chat_context_overflow"));

        using var verify = SharedFactory!.Services.CreateScope();
        var after = await ConversationStreamTestHelpers.SnapshotMessageContentAsync(
            verify.ServiceProvider.GetRequiredService<ApplicationDbContext>(), conv.ConversationId);
        foreach (var (id, content) in before)
        {
            after.Should().ContainKey(id);
            after[id].Should().Be(content, "D5: overflow must never rewrite a stored message");
        }
    }

    private sealed record CompactedConversation(
        Guid ProjectId, Guid NotebookId, Guid ConversationId, int BoundaryTurnIndex, int PromptCharBudget);

    /// <summary>
    /// Phases 1-3, shared by both tests: build a long conversation, overflow it (asserting a clean error and
    /// no mutation), then compact it (asserting the boundary skips the failed turn). Leaves the fake in
    /// OverflowAbovePromptBudget with the budget that overflowed.
    /// </summary>
    private async Task<CompactedConversation> BuildOverflowedThenCompactedConversationAsync()
    {
        Guid projectId, notebookId, conversationId;
        using (var scope = SharedFactory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (projectId, notebookId) = await ConversationStreamTestHelpers.SeedProjectNotebookAsync(db, "Compaction E2E");
            // Not "New Conversation": that title triggers background title generation after turn 1
            // (ConversationStreamEngine.MaybeScheduleFirstTurnTitleGeneration), whose request would
            // overwrite the fake's LastRequestMessages that the budget below is measured from.
            conversationId = await ConversationStreamTestHelpers.SeedConversationAsync(db, notebookId, "Compaction E2E");
        }

        var behavior = FakeChatCompletionBehavior.Instance;
        var conv = new CompactedConversation(projectId, notebookId, conversationId, 0, 0);

        // Phase 1 -- a long conversation, no budget.
        for (var turn = 1; turn <= SetupTurns; turn++)
        {
            var events = await SendAsync(conv, SetupMessage(turn));
            events.Should().Contain(e => e.EventType == StreamingEventTypes.Complete, $"setup turn {turn} must complete");
        }

        // The last successful request is the biggest prompt that fit. The next one carries all of it plus a
        // reply and a new message, so a budget of exactly this size must overflow.
        var budget = FakeChatCompletionBehavior.PromptChars(behavior.LastRequestMessages!);

        // Phase 2 -- overflow.
        behavior.Scenario = FakeChatScenario.OverflowAbovePromptBudget;
        behavior.PromptCharBudget = budget;

        Dictionary<Guid, string> beforeOverflow;
        using (var scope = SharedFactory!.Services.CreateScope())
        {
            beforeOverflow = await ConversationStreamTestHelpers.SnapshotMessageContentAsync(
                scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(), conversationId);
        }

        var overflowEvents = await SendAsync(conv, SetupMessage(SetupTurns + 1));
        overflowEvents.Should().Contain(e => e.EventType == StreamingEventTypes.Error
                                             && e.Payload.Contains("chat_context_overflow"),
            "an oversized prompt must surface as the named, actionable overflow error");

        int lastCompletedTurn;
        using (var scope = SharedFactory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var afterOverflow = await ConversationStreamTestHelpers.SnapshotMessageContentAsync(db, conversationId);
            foreach (var (id, content) in beforeOverflow)
            {
                afterOverflow.Should().ContainKey(id);
                afterOverflow[id].Should().Be(content, "D5: overflow must never rewrite a stored message");
            }

            lastCompletedTurn = await ConversationStreamTestHelpers.LastCompletedTurnIndexAsync(db, conversationId);
        }

        // Phase 3 -- compact.
        var compactResponse = await Client.PostAsync(
            $"/api/projects/{projectId}/notebooks/{notebookId}/conversations/{conversationId}/compact", content: null);
        compactResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var compaction = await compactResponse.Content.ReadFromJsonAsync<CompactionResultDto>();
        compaction!.BoundaryTurnIndex.Should().Be(lastCompletedTurn,
            "the overflowed turn failed, so the boundary must stop at the last completed turn");

        return conv with { BoundaryTurnIndex = lastCompletedTurn, PromptCharBudget = budget };
    }

    private Task<List<(string EventType, string Payload)>> SendAsync(CompactedConversation conv, string text) =>
        ConversationStreamTestHelpers.SendMessageStreamAsync(
            Client, conv.ProjectId, conv.NotebookId, conv.ConversationId,
            new { instructions = text, assistantName = "assistant" });

    // Neutral filler: none of the Directives words (always/never/prefer/don't) or scope-change words
    // (actually/instead), so the summary stays small and the marker appears nowhere but turn 3.
    private static string SetupMessage(int turn)
    {
        var head = turn == RecallMarkerTurn
            ? $"Turn {turn} notes. The calibration code is {RecallMarker}. "
            : $"Turn {turn} notes. ";
        var filler = string.Concat(Enumerable.Repeat("lorem ipsum dolor sit amet ", PaddingChars / 27 + 1));
        return head + filler[..PaddingChars];
    }
}
