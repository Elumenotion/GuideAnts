using AntRunner.Chat;
using AntRunner.Chat.Abstractions;
using AntRunner.ToolCalling.AssistantDefinitions;
using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services.Conversations;
using GuideAntsApi.Services.Conversations.Attachments;
using GuideAntsApi.Services.Conversations.Mapping;
using GuideAntsApi.Services.Core;
using GuideAntsApi.Tests.TestUtils;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using System.Collections;
using System.Reflection;
using ChatMessageRole = AntRunner.Chat.Abstractions.ChatRole;
using DataModelChatRole = GuideAntsApi.DataModel.Models.ChatRole;

namespace GuideAntsApi.Tests.Services.Conversations;

[TestClass]
[DoNotParallelize]
public sealed class ConversationHistoryBuilderCompactionTests
{
    // Same static-cache rationale as ConversationHistoryBuilderBatchingTests.SwitchAssistantName.
    private const string SwitchAssistantName = "Task5CompactionSwitchTestAssistant";

    private ApplicationDbContext _dbContext = null!;
    private ConversationHistoryBuilder _builder = null!;
    private Guid _conversationId;
    private Guid _notebookId;

    [TestInitialize]
    public void TestInitialize()
    {
        // AssistantUtility caches definitions in a static, process-wide dictionary keyed by name.
        // The MSTest assembly runs with [assembly: Parallelize(Scope = ExecutionScope.MethodLevel)],
        // so a per-class [DoNotParallelize] does not stop this class's tests from racing OTHER
        // classes' env-var/cache setup - clearing the whole cache up front and seeding "Claude"
        // deterministically avoids ever falling through to a real (and here, unreachable) SQL
        // lookup via AssistantUtility.GetAssistantCreateRequest, following the precedent in
        // ConversationServicePreflightTests.cs.
        AssistantUtility.ClearAllCache();
        SeedAssistantCache("Claude");

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new ApplicationDbContext(options);

        var scopeFactory = new TestServiceScopeFactory(_dbContext);
        var attachments = new AttachmentContentService(
            scopeFactory,
            Microsoft.Extensions.Options.Options.Create(new GuideAntsApi.Options.MarkdownAttachmentOptions()),
            notebookFileService: null,
            markdownExtractionService: null,
            Mock.Of<ILogger<AttachmentContentService>>(),
            configuration: null);

        _builder = new ConversationHistoryBuilder(
            scopeFactory,
            Mock.Of<IContextOptionsService>(),
            attachments,
            Mock.Of<ILogger<ConversationHistoryBuilder>>());

        SeedConversation();
    }

    [TestCleanup]
    public void TestCleanup()
    {
        _dbContext.Dispose();
        AssistantUtility.ClearAllCache();
    }

    private void SeedConversation()
    {
        _conversationId = Guid.NewGuid();
        _notebookId = Guid.NewGuid();
        var notebook = new Notebook { Id = _notebookId, ProjectId = Guid.NewGuid(), Title = "NB" };
        var conversation = new NotebookConversation
        {
            Id = _conversationId,
            NotebookId = _notebookId,
            Title = "Convo",
            Notebook = notebook,
            CompactionBoundaryTurnIndex = 2
        };
        _dbContext.Notebooks.Add(notebook);
        _dbContext.NotebookConversations.Add(conversation);

        // Turn 1: builds a file. Turn 2: modifies it. Both before the boundary.
        _dbContext.ConversationTurns.Add(new ConversationTurn
        {
            NotebookConversationId = _conversationId, TurnIndex = 1, AssistantName = "Claude",
            Instructions = "Build a report", Status = "completed",
            FilesCreated = "[\"Output/report.md\"]"
        });
        _dbContext.ConversationTurns.Add(new ConversationTurn
        {
            NotebookConversationId = _conversationId, TurnIndex = 2, AssistantName = "Claude",
            Instructions = "Tweak the report", Status = "completed",
            // A different path than turn 1's, deliberately: ArtifactsExtractor (W3) drops a
            // "modified" entry whose path was already seen in "created" across any earlier turn,
            // so reusing turn 1's path here would silently make the modified-line assertion below
            // vacuous instead of actually exercising the Modified branch.
            FilesModified = "[\"Output/chart.png\"]"
        });
        // Turn 3 is the post-boundary tail.
        _dbContext.ConversationTurns.Add(new ConversationTurn
        {
            NotebookConversationId = _conversationId, TurnIndex = 3, AssistantName = "Claude",
            Instructions = "Add a chart", Status = "completed"
        });

        for (var turn = 1; turn <= 3; turn++)
        {
            _dbContext.NotebookConversationMessages.Add(new NotebookConversationMessage
            {
                Id = Guid.NewGuid(), NotebookConversationId = _conversationId, TurnIndex = turn,
                MessageSequence = 1, Role = DataModelChatRole.User, Content = $"user message {turn}",
                Created = DateTime.UtcNow.AddMinutes(turn)
            });
            _dbContext.NotebookConversationMessages.Add(new NotebookConversationMessage
            {
                Id = Guid.NewGuid(), NotebookConversationId = _conversationId, TurnIndex = turn,
                MessageSequence = 2, Role = DataModelChatRole.Assistant, Content = $"assistant message {turn}",
                AssistantName = "Claude", Created = DateTime.UtcNow.AddMinutes(turn).AddSeconds(30)
            });
        }
        _dbContext.SaveChanges();
    }

    private NotebookConversation LoadConversation() =>
        _dbContext.NotebookConversations
            .Include(c => c.Messages)
            .Include(c => c.Turns)
            .Include(c => c.Notebook)
            .AsNoTracking()
            .Single(c => c.Id == _conversationId);

    private static void SeedAssistantCache(string assistantName)
    {
        var cacheType = typeof(AssistantUtility).GetNestedType("CachedAssistant", BindingFlags.NonPublic)!;
        var entry = Activator.CreateInstance(cacheType, new AssistantDefinition { Name = assistantName })!;
        var cache = (IDictionary)typeof(AssistantUtility)
            .GetField("AssistantDefinitionCache", BindingFlags.Static | BindingFlags.NonPublic)!
            .GetValue(null)!;
        cache[assistantName] = entry;
    }

    [TestMethod]
    public async Task PrepareMessages_WithBoundary_LeadsWithSummaryThenTail()
    {
        var conv = LoadConversation();

        var messages = await _builder.PrepareMessagesForAssistantAsync(conv, "Claude", Guid.NewGuid());

        // "Claude" is cache-seeded with no Instructions (TestInitialize) and the context-options
        // mock returns null, so no instructions/context messages precede the summary - it is message 0.
        messages[0].Role.Should().Be(ChatMessageRole.System);
        var summaryText = messages[0].GetText();
        summaryText.Should().Contain("condensed handoff briefing");
        summaryText.Should().Contain("[Compacted 4 earlier message(s).]"); // turns 1+2 = 4 messages
        summaryText.Should().Contain("## Goal");
        summaryText.Should().Contain("user message 1");
        summaryText.Should().Contain("- created: Output/report.md");
        summaryText.Should().Contain("- modified: Output/chart.png");

        messages.Skip(1).Select(m => m.GetText()).Should().ContainInOrder("user message 3", "assistant message 3");
        messages.Should().HaveCount(3); // summary + turn 3's 2 messages
    }

    [TestMethod]
    public async Task PrepareMessages_NoBoundary_NeverCallsTheEngine()
    {
        var conv = LoadConversation();
        conv.CompactionBoundaryTurnIndex = null;

        var messages = await _builder.PrepareMessagesForAssistantAsync(conv, "Claude", Guid.NewGuid());

        messages.Should().HaveCount(6); // all 3 turns, verbatim, no summary
        messages.Should().NotContain(m => m.GetText().Contains("condensed handoff briefing"));
    }

    [TestMethod]
    public async Task PrepareMessages_AssistantSwitchWithBoundary_SummaryPrecedesTailWhichPrecedesHandoff()
    {
        SeedAssistantCache(SwitchAssistantName);
        var conv = LoadConversation();

        var messages = await _builder.PrepareMessagesForAssistantAsync(conv, SwitchAssistantName, Guid.NewGuid());

        messages[0].Role.Should().Be(ChatMessageRole.System);
        messages[0].GetText().Should().Contain("condensed handoff briefing");

        messages.Skip(1).Take(2).Select(m => m.GetText())
            .Should().ContainInOrder("user message 3", "assistant message 3");

        messages[3].Role.Should().Be(ChatMessageRole.System);
        messages[3].GetText().Should().Contain("previous messages between the user and assistant");
        messages.Should().HaveCount(4);
    }

    [TestMethod]
    public async Task PrepareMessages_RepeatedCompaction_RecomputesFromOriginalMessages_NotFromThePriorSummary()
    {
        var conv = LoadConversation();

        var firstPass = await _builder.PrepareMessagesForAssistantAsync(conv, "Claude", Guid.NewGuid());
        var firstSummary = firstPass[0].GetText();

        // Simulate a second compaction press that moved the boundary forward to include turn 3
        // (CompactionService's job in W4; here we just set the column directly, as W5 only cares
        // that the history builder re-derives from source, not that it drives the move itself).
        // A turn 4 is added first because in the real flow (ConversationService.cs) the current
        // turn's own user message is always persisted before PrepareMessagesForAssistantAsync runs
        // for it - so a boundary never sits at-or-above every existing turn when this method is
        // called for real. Without turn 4 here, boundary=3 would look identical to the
        // stale-after-undo case the next test guards against, and this test would start exercising
        // that guard instead of the recomputation property it's meant to prove.
        conv.Turns.Add(new ConversationTurn
        {
            NotebookConversationId = _conversationId, TurnIndex = 4, AssistantName = "Claude",
            Instructions = "One more thing", Status = "completed"
        });
        conv.Messages.Add(new NotebookConversationMessage
        {
            NotebookConversationId = _conversationId, TurnIndex = 4, MessageSequence = 1,
            Role = DataModelChatRole.User, Content = "user message 4"
        });
        conv.CompactionBoundaryTurnIndex = 3;
        var secondPass = await _builder.PrepareMessagesForAssistantAsync(conv, "Claude", Guid.NewGuid());
        var secondSummary = secondPass[0].GetText();

        secondSummary.Should().NotContain(firstSummary); // not built by wrapping the first summary
        // GoalExtractor only surfaces the FIRST user message as the initial goal, and turn 3 has no
        // tool calls, files, or scope-change-marker text of its own - so the message count moving
        // from 4 to 6 (recomputed from all 3 turns' raw messages, not from firstSummary) is the
        // observable proof that recomputation happened over the new, wider window.
        secondSummary.Should().Contain("[Compacted 6 earlier message(s).]");
        secondPass.Skip(1).Select(m => m.GetText()).Should().ContainInOrder("user message 4");
    }

    [TestMethod]
    public async Task PrepareMessages_BoundaryStaleAfterUndo_FallsBackToFullHistoryRatherThanCompactingAgainstTheUsersWill()
    {
        var conv = LoadConversation();
        // Simulates the state left behind when ConversationUndoService deletes turns at/after a
        // target index and the next turn is reassigned from the new Max(TurnIndex) - without
        // clamping CompactionBoundaryTurnIndex, a boundary set before the undo can end up at or
        // above every turn that still exists. D1 forbids compaction the user didn't ask for, so a
        // boundary this stale must be treated as if the conversation was never compacted, not
        // silently applied against whatever turns happen to remain.
        conv.CompactionBoundaryTurnIndex = 5;

        var messages = await _builder.PrepareMessagesForAssistantAsync(conv, "Claude", Guid.NewGuid());

        messages.Should().HaveCount(6); // full uncompacted history for turns 1-3, not a bare summary
        messages.Should().NotContain(m => m.GetText().Contains("condensed handoff briefing"));
    }

    [TestMethod]
    public async Task PrepareMessages_ToolCallAcrossBoundary_DropsPreBoundaryPairing_PreservesPostBoundaryPairing()
    {
        // The spec ranks "tool_calls/tool_result pairing preserved" as the highest-consequence
        // invariant in this feature: breaking it produces a provider-rejected request, not just a
        // quality regression. Turn 2's pair sits before the boundary (compacted away into the
        // summary's Activity ledger text, never re-emitted as a structured tool_call message);
        // turn 3's pair sits after it and must survive intact and still adjacent.
        var conv = LoadConversation();

        _dbContext.NotebookConversationMessages.Add(new NotebookConversationMessage
        {
            NotebookConversationId = _conversationId, TurnIndex = 2, MessageSequence = 3,
            Role = DataModelChatRole.Assistant, AssistantName = "Claude", Content = "",
            ToolCalls = "[{\"id\":\"t1\",\"type\":\"function\",\"function\":{\"name\":\"search\",\"arguments\":{}}}]"
        });
        _dbContext.NotebookConversationMessages.Add(new NotebookConversationMessage
        {
            NotebookConversationId = _conversationId, TurnIndex = 2, MessageSequence = 4,
            Role = DataModelChatRole.Tool, ToolCallId = "t1", FunctionName = "search", Content = "t1 result"
        });
        _dbContext.NotebookConversationMessages.Add(new NotebookConversationMessage
        {
            NotebookConversationId = _conversationId, TurnIndex = 3, MessageSequence = 3,
            Role = DataModelChatRole.Assistant, AssistantName = "Claude", Content = "",
            ToolCalls = "[{\"id\":\"t2\",\"type\":\"function\",\"function\":{\"name\":\"search\",\"arguments\":{}}}]"
        });
        _dbContext.NotebookConversationMessages.Add(new NotebookConversationMessage
        {
            NotebookConversationId = _conversationId, TurnIndex = 3, MessageSequence = 4,
            Role = DataModelChatRole.Tool, ToolCallId = "t2", FunctionName = "search", Content = "t2 result"
        });
        await _dbContext.SaveChangesAsync();
        conv = LoadConversation();

        var messages = await _builder.PrepareMessagesForAssistantAsync(conv, "Claude", Guid.NewGuid());

        messages.Should().NotContain(m => m.ToolCallId == "t1");
        messages.Should().ContainSingle(m => m.ToolCallId == "t2");
        var toolCallIndex = messages.FindIndex(m => m.ToolCalls != null && m.ToolCalls.Any(tc => tc.Id == "t2"));
        toolCallIndex.Should().BeGreaterThanOrEqualTo(0);
        messages[toolCallIndex + 1].ToolCallId.Should().Be("t2");
    }

    [TestMethod]
    public async Task PrepareMessages_CompactionPathThrows_FallsBackToFullUncompactedHistory()
    {
        // BuildHistoryTailAsync's try block calls BuildOpenAiMessagesAsync (tail; scope call #1,
        // needed unconditionally for attachment loading) THEN BuildCompactionSummaryMessageAsync
        // (summary; scope call #2). Throwing only on call #2 isolates the failure to the
        // compaction-specific step - the fallback's own re-run of BuildOpenAiMessagesAsync is scope
        // call #3, which must succeed against the real in-memory DB for this test to prove anything.
        var flakyScopeFactory = new NthCallThrowsScopeFactory(
            new TestServiceScopeFactory(_dbContext), throwOnCallNumber: 2);
        var builder = new ConversationHistoryBuilder(
            flakyScopeFactory,
            Mock.Of<IContextOptionsService>(),
            Mock.Of<IAttachmentContentService>(),
            Mock.Of<ILogger<ConversationHistoryBuilder>>());
        var conv = LoadConversation();

        var messages = await builder.PrepareMessagesForAssistantAsync(conv, "Claude", Guid.NewGuid());

        messages.Should().HaveCount(6); // full uncompacted history, not a partial/empty result
        messages.Should().NotContain(m => m.GetText().Contains("condensed handoff briefing"));
    }

    private sealed class NthCallThrowsScopeFactory : IServiceScopeFactory
    {
        private readonly IServiceScopeFactory _inner;
        private readonly int _throwOnCallNumber;
        private int _callCount;

        public NthCallThrowsScopeFactory(IServiceScopeFactory inner, int throwOnCallNumber)
        {
            _inner = inner;
            _throwOnCallNumber = throwOnCallNumber;
        }

        public IServiceScope CreateScope()
        {
            _callCount++;
            if (_callCount == _throwOnCallNumber)
            {
                throw new InvalidOperationException("scope unavailable");
            }

            return _inner.CreateScope();
        }
    }
}
