# W5: History-Builder Wiring Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** When a conversation has a `CompactionBoundaryTurnIndex` (W4), the next turn's history is built as `[system: deterministic handoff summary] + verbatim post-boundary tail` instead of full replay — for both the normal continuation path and the assistant-switch path — with zero behavior change when no boundary exists, and with compaction failure never able to fail a turn.

**Architecture:** All wiring lives in `ConversationHistoryBuilder` (`GuideAntsApi.Services.Conversations.Mapping`), the sole private-path caller per D4. `BuildOpenAiMessagesAsync` and `ApplyAssistantSwitchLogicAsync` gain an optional `compactionBoundaryTurnIndex` parameter that trims their output to the post-boundary tail; a new private helper, `BuildCompactionSummaryMessageAsync`, loads pre-boundary `NotebookConversationMessage`/`ConversationTurn` rows, calls W3's pure `CompactionEngine.Compact`, and returns the `[system: ...]` summary message. `PrepareMessagesForAssistantAsync` — the one method both the switch and non-switch branches already flow through — composes summary + tail and falls back to full uncompacted history if anything in the compaction path throws. Two small, separately-testable signals close out the workstream: a `CaptureCompaction` hook on `IThreadRunTraceCollector` (following the `CaptureToolLimitState` precedent) and a `compaction_boundary_marker` SSE event, both wired into `ConversationStreamEngine` at the exact point it already creates the per-turn `TurnTraceCollector`. `BuildPublishedMessagesForAssistantAsync` (published/sandbox-wire) is untouched per the checklist (5.3) and per D4/spec's "Shape at history-build time" being scoped to the notebook-UI history path only.

**Tech Stack:** ASP.NET Core 8, EF Core 8 (EF InMemory in tests), MSTest + FluentAssertions + Moq, `AntRunner.Chat.Compaction` (W3, already merged), `NotebookConversation.CompactionBoundaryTurnIndex` (W4, already merged).

**Spec:** [`docs/superpowers/specs/2026-09-21-context-compaction-design.md`](../specs/2026-09-21-context-compaction-design.md) — *Architecture → Data flow*, *Section vocabulary → Placement*, *Error handling*. Checklist: [`docs/context-compaction-plan.md`](../../context-compaction-plan.md) W5 (5.1–5.7).

## Global Constraints

- **`BuildPublishedMessagesForAssistantAsync`'s three call sites stay untouched (5.3).** Published guides and sandbox-wire keep pre-compaction behavior in v1 — this is explicit in both the checklist and the spec's Data flow diagram, which only shows `ConversationHistoryBuilder`'s *private*-path composition changing.
- **No boundary → byte-identical behavior to before this plan (5.2, D7).** Every existing test in `ConversationHistoryBuilderBatchingTests.cs` and `ConversationServiceAssistantSwitchingTests.cs` must keep passing completely unmodified — they are the regression suite for "zero behavior change for conversations that were never compacted."
- **Compaction failing must never fail a turn** (spec, *Error handling*: "Engine throws → Log, proceed with uncompacted history"). Any exception anywhere in the boundary-aware path (DB read, JSON parse, `CompactionEngine.Compact` itself) must be caught, logged, and followed by a fallback to the exact same call with `compactionBoundaryTurnIndex: null` — i.e. full history, not a partially-built one.
- **The boundary never splits a turn** (D3/W4's own invariant). This plan does not re-validate that invariant — `CompactionBoundaryTurnIndex` is guaranteed by `CompactionService` (W4) to always sit on a `"completed"` turn boundary. `TurnFacts` and message filtering here simply trust `TurnIndex <= boundary` / `TurnIndex > boundary` as the pre/post split.
- **`CompactionEngine.Compact`'s exact shipped signature** (verified directly against `AntRunner.Chat/AntRunner.Chat/Compaction/CompactionEngine.cs` and `TurnFacts.cs` on this branch): `CompactionEngine.Compact(IReadOnlyList<ChatMessage> preBoundaryMessages, IReadOnlyList<TurnFacts> turnFacts) -> CompactionResult(string SummaryText, int SummarizedMessageCount, int SummarizedTurnCount)`; `TurnFacts(int TurnIndex, IReadOnlyList<string> FilesCreated, IReadOnlyList<string> FilesModified)`. Both list parameters tolerate null (treated as empty) and the method never throws on degenerate input — the try/catch in this plan exists for the *caller's* DB/JSON work, not because the engine itself is expected to throw.
- **Server tests: MSTest (`[TestClass]`/`[TestMethod]`) + FluentAssertions + Moq**; EF InMemory for anything touching `ApplicationDbContext`.
- Never co-author commits, and only commit when the user has said to (per `CLAUDE.md` and memory `no-commits-without-permission`). The commit steps below are proposed checkpoints — ask first.

## Findings that shaped this plan

- **`PrepareMessagesForAssistantAsync` is the single call site for both `BuildOpenAiMessagesAsync` and `ApplyAssistantSwitchLogicAsync`** in production code (confirmed by grep across `src/server`) — the "one private-path call site" the spec's Architecture section refers to. `BuildPublishedMessagesForAssistantAsync` is a structurally separate method with its own three callers (`PublishedConversationService.cs` ×2, `SandboxWireConversationService.cs` ×1) and is not touched.
- **`PrepareMessagesForAssistantAsync` already calls both target methods with `cancellationToken` positionally as the third argument** (`ConversationHistoryBuilder.cs:134` and `:153`). Inserting `compactionBoundaryTurnIndex` as a new third parameter before `cancellationToken` (Task 1) would silently break these two call sites at compile time (`CancellationToken` doesn't convert to `int?`) unless they're updated in the same task — Task 1 updates them to pass `cancellationToken: cancellationToken` by name (no functional change), and Task 2 is the one that starts passing a real boundary value and composing the summary. This keeps every task's diff independently compiling and testable.
- **Neither `BuildOpenAiMessagesAsync` nor `ApplyAssistantSwitchLogicAsync` reads `conv.Turns`** — both operate purely on `conv.Messages`. This is why boundary filtering can be a plain `TurnIndex >` predicate applied after the existing dedup step, with no need to touch turn-loading logic in either method.
- **`ApplyAssistantSwitchLogicAsync`'s trailing handoff-message check — `if (conv.Messages.Any())`, near the end of the method — must keep reading the *original* `conv.Messages` nav collection, not the boundary-filtered local list.** If boundary filtering left the tail empty (e.g. compact pressed on the conversation's last turn, then an immediate assistant switch with no new turns yet), the conversation still isn't "new," and the handoff notice is still correct to show. The plan filters into a new local variable and leaves this specific check reading `conv.Messages` directly.
- **No existing test file covers `ConversationStreamEngine` or `TurnTraceCollector` directly** (confirmed: no `*ConversationStreamEngineTests.cs`/`*TurnTraceCollectorTests.cs` exist). The established pattern for testing pure decision logic that lives inside `ConversationStreamEngine`'s otherwise-untestable background-worker method is `RecordLearnedContextWindow` — an `internal static` method, unit-tested directly in `ContextOverflowLearningTests.cs` via `InternalsVisibleTo`, with the one-line call site wired into the untested worker body. Task 3 follows the exact same shape for a new `ShouldEmitCompactionBoundaryMarker` static method.
- **`CaptureCompaction` records only the boundary, not a message count.** `CaptureToolLimitState(int toolCallsUsed, string escalationPhase)` is the closest precedent, but its two values both come for free from state `ThreadRun` already tracks mid-run. A compaction message count is not free here — getting it into `ConversationStreamEngine` would mean either an extra DB query on every post-compaction turn (for a trace nicety no one has asked to query yet) or changing `PrepareMessagesForAssistantAsync`'s return shape (which ripples into `ConversationService.cs`, the interface, and existing mocks, for the exact same reason). `messagesSummarized` already ships to the user via the compact endpoint's response (W4); the trace only needs to answer "was this turn's history compacted, and to what boundary," which `CaptureCompaction(int boundaryTurnIndex)` alone answers.
- **The client boundary marker (5.6) must fire once, not every turn.** `ConversationStreamEngine` supports multiple simultaneous observers per conversation (`ConversationLocked`/`UserJoined`/etc. events already exist for this), so a live-connected observer needs the marker event exactly at the turn where compacted history is first used — not on every subsequent turn, which would just repeat the same marker. `ShouldEmitCompactionBoundaryMarker(int? compactionBoundaryTurnIndex, int currentTurnIndex)` gates on `currentTurnIndex == compactionBoundaryTurnIndex + 1`. A client that wasn't connected at that moment (e.g. opened the notebook later) gets the same information from the conversation's context-status read (`boundaryTurnIndex`, W4/W2), so nothing is lost by not repeating the event.
- **`ConversationStreamRunContext.Conversation` and `.TurnIndex` are both already available** at the exact point `ConversationStreamEngine.cs` constructs the per-turn `TurnTraceCollector` (`:257-258`, inside the `Task.Run` background-worker lambda) — no new parameter threading needed for Task 3. The `TryWrite` local function used to broadcast the marker event is declared later in the same lambda (`:276`) but is callable from `:258` regardless, since C# local functions aren't order-dependent within their enclosing scope.

---

## File Structure

**Created:**
- `src/server/GuideAntsApi.Tests/Services/Conversations/ConversationHistoryBuilderCompactionTests.cs`
- `src/server/GuideAntsApi.Tests/Services/Conversations/Tracing/TurnTraceCollectorTests.cs`
- `src/server/GuideAntsApi.Tests/Services/Conversations/Streaming/CompactionBoundaryMarkerTests.cs`

**Modified:**
- `src/server/GuideAntsApi/Services/Conversations/Mapping/IConversationHistoryBuilder.cs` — add `int? compactionBoundaryTurnIndex = null` to `BuildOpenAiMessagesAsync` and `ApplyAssistantSwitchLogicAsync`
- `src/server/GuideAntsApi/Services/Conversations/Mapping/ConversationHistoryBuilder.cs` — boundary-aware tail filtering, `BuildCompactionSummaryMessageAsync`, `PrepareMessagesForAssistantAsync` composition + fallback
- `src/server/GuideAntsApi.Tests/Services/Conversations/ConversationHistoryBuilderBatchingTests.cs` — new boundary-filtering tests for both methods
- `src/server/AntRunner.Chat/AntRunner.Chat/ThreadRunTrace.cs` — add `CaptureCompaction(int boundaryTurnIndex)` to `IThreadRunTraceCollector`
- `src/server/GuideAntsApi/Services/Conversations/Tracing/TurnTraceCollector.cs` — implement `CaptureCompaction`
- `src/server/GuideAntsApi/Services/Conversations/Tracing/TurnTracePayload.cs` — add `TurnTraceSegment.CompactionBoundaryTurnIndex`
- `src/server/GuideAntsApi/Models/Conversations/StreamingEvent.cs` — add `StreamingEventTypes.CompactionBoundaryMarker`
- `src/server/GuideAntsApi/Services/Conversations/Streaming/StreamingEvents.cs` — add `BuildCompactionBoundaryMarkerEvent`
- `src/server/GuideAntsApi/Services/Conversations/Streaming/ConversationStreamEngine.cs` — add `ShouldEmitCompactionBoundaryMarker`, wire both signals in at `:257-258`
- `docs/context-compaction-plan.md` — tick 5.1–5.7, link this plan

---

## Task 1: Boundary-aware tail filtering in `BuildOpenAiMessagesAsync` and `ApplyAssistantSwitchLogicAsync` (groundwork for 5.1, 5.2)

**Files:**
- Modify: `src/server/GuideAntsApi/Services/Conversations/Mapping/IConversationHistoryBuilder.cs`
- Modify: `src/server/GuideAntsApi/Services/Conversations/Mapping/ConversationHistoryBuilder.cs`
- Test: `src/server/GuideAntsApi.Tests/Services/Conversations/ConversationHistoryBuilderBatchingTests.cs`

**Interfaces:**
- Consumes: nothing new — this task only reshapes existing methods' signatures.
- Produces: `IConversationHistoryBuilder.BuildOpenAiMessagesAsync(NotebookConversation, string, int? compactionBoundaryTurnIndex = null, CancellationToken = default) -> Task<List<ChatMessage>>` and `IConversationHistoryBuilder.ApplyAssistantSwitchLogicAsync(NotebookConversation, string, int? compactionBoundaryTurnIndex = null, CancellationToken = default) -> Task<List<ChatMessage>>`, both consumed by Task 2.

- [ ] **Step 1: Write the failing tests**

Add to `ConversationHistoryBuilderBatchingTests.cs` (the existing fixture's `SeedConversation()` already produces 3 turns × 2 messages = 6 messages; these tests only need that base fixture, no `ConversationTurn` rows, since neither method under test reads `conv.Turns`):

```csharp
    [TestMethod]
    public async Task BuildOpenAiMessages_WithBoundary_ExcludesMessagesAtOrBeforeBoundary()
    {
        var conv = LoadConversation();

        var messages = await _builder.BuildOpenAiMessagesAsync(conv, "Claude", compactionBoundaryTurnIndex: 1);

        messages.Should().HaveCount(4);
        messages.Select(m => m.GetText()).Should().ContainInOrder(
            "user message 2", "assistant message 2",
            "user message 3", "assistant message 3");
    }

    [TestMethod]
    public async Task BuildOpenAiMessages_NoBoundary_UnchangedFromBeforeThisPlan()
    {
        var conv = LoadConversation();

        var messages = await _builder.BuildOpenAiMessagesAsync(conv, "Claude");

        messages.Should().HaveCount(6);
    }

    [TestMethod]
    public async Task ApplyAssistantSwitchLogic_WithBoundary_ExcludesMessagesAtOrBeforeBoundary()
    {
        SeedAssistantCache(SwitchAssistantName);
        var conv = LoadConversation();

        var messages = await _builder.ApplyAssistantSwitchLogicAsync(conv, SwitchAssistantName, compactionBoundaryTurnIndex: 2);

        // 2 tail messages (turn 3 only) + the trailing handoff system message, which is governed by
        // conv.Messages.Any() on the ORIGINAL collection, not the filtered tail - so it still appears.
        messages.Should().HaveCount(3);
        messages.Select(m => m.GetText()).Should().ContainInOrder("user message 3", "assistant message 3");
        messages[2].Role.Should().Be(ChatMessageRole.System);
        messages[2].GetText().Should().Contain("previous messages between the user and assistant");
    }

    [TestMethod]
    public async Task ApplyAssistantSwitchLogic_NoBoundary_UnchangedFromBeforeThisPlan()
    {
        SeedAssistantCache(SwitchAssistantName);
        var conv = LoadConversation();

        var messages = await _builder.ApplyAssistantSwitchLogicAsync(conv, SwitchAssistantName);

        messages.Should().HaveCount(7);
    }
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test src/server/GuideAntsApi.Tests/GuideAntsApi.Tests.csproj --filter "FullyQualifiedName~ConversationHistoryBuilderBatchingTests"`
Expected: the two `NoBoundary_Unchanged` tests PASS already (nothing changed yet); the two `WithBoundary` tests FAIL to compile — no `compactionBoundaryTurnIndex` parameter exists yet.

- [ ] **Step 3: Implement**

In `IConversationHistoryBuilder.cs`, change:

```csharp
    Task<List<ChatMessage>> ApplyAssistantSwitchLogicAsync(
        NotebookConversation conv,
        string newAssistantName,
        CancellationToken cancellationToken = default);

    Task<List<ChatMessage>> BuildOpenAiMessagesAsync(
        NotebookConversation conv,
        string assistantName,
        CancellationToken cancellationToken = default);
```

to:

```csharp
    Task<List<ChatMessage>> ApplyAssistantSwitchLogicAsync(
        NotebookConversation conv,
        string newAssistantName,
        int? compactionBoundaryTurnIndex = null,
        CancellationToken cancellationToken = default);

    Task<List<ChatMessage>> BuildOpenAiMessagesAsync(
        NotebookConversation conv,
        string assistantName,
        int? compactionBoundaryTurnIndex = null,
        CancellationToken cancellationToken = default);
```

In `ConversationHistoryBuilder.cs`, change `ApplyAssistantSwitchLogicAsync`'s signature and the top of its body from:

```csharp
    public async Task<List<ChatMessage>> ApplyAssistantSwitchLogicAsync(
        NotebookConversation conv,
        string newAssistantName,
        CancellationToken cancellationToken = default)
    {
        var dedupedMessages = ConversationMessageMapper.FilterDuplicateAssistantMessages(
            conv.Messages,
            m => m.Role,
            m => m.TurnIndex,
            m => m.Content,
            m => !string.IsNullOrEmpty(m.ToolCalls)
        );

        var assistantDef = await AssistantUtility.GetAssistantCreateRequest(newAssistantName);
```

to:

```csharp
    public async Task<List<ChatMessage>> ApplyAssistantSwitchLogicAsync(
        NotebookConversation conv,
        string newAssistantName,
        int? compactionBoundaryTurnIndex = null,
        CancellationToken cancellationToken = default)
    {
        var dedupedMessages = ConversationMessageMapper.FilterDuplicateAssistantMessages(
            conv.Messages,
            m => m.Role,
            m => m.TurnIndex,
            m => m.Content,
            m => !string.IsNullOrEmpty(m.ToolCalls)
        );

        if (compactionBoundaryTurnIndex.HasValue)
        {
            // W4's CompactionService guarantees the boundary always sits on a completed turn, so a
            // plain TurnIndex cut here never splits an assistant tool_calls message from its
            // tool_result pairing.
            dedupedMessages = dedupedMessages
                .Where(m => m.TurnIndex > compactionBoundaryTurnIndex.Value)
                .ToList();
        }

        var assistantDef = await AssistantUtility.GetAssistantCreateRequest(newAssistantName);
```

Leave the rest of the method body untouched, **including** the trailing block near the end:

```csharp
        if (conv.Messages.Any())
        {
            newMessages.Add(new ChatMessage(ChatMessageRole.System, HandoffSystemMessage));
        }
```

— this must keep reading `conv.Messages` (the full, original collection), not `dedupedMessages`, per the Findings note above.

Change `BuildOpenAiMessagesAsync`'s signature and the top of its body from:

```csharp
    public async Task<List<ChatMessage>> BuildOpenAiMessagesAsync(
        NotebookConversation conv,
        string assistantName,
        CancellationToken cancellationToken = default)
    {
        var list = new List<ChatMessage>();

        var filteredMessages = ConversationMessageMapper.FilterDuplicateAssistantMessages(
            conv.Messages,
            m => m.Role,
            m => m.TurnIndex,
            m => m.Content,
            m => !string.IsNullOrEmpty(m.ToolCalls)
        );

        var validToolCallIds = new HashSet<string>();
```

to:

```csharp
    public async Task<List<ChatMessage>> BuildOpenAiMessagesAsync(
        NotebookConversation conv,
        string assistantName,
        int? compactionBoundaryTurnIndex = null,
        CancellationToken cancellationToken = default)
    {
        var list = new List<ChatMessage>();

        var filteredMessages = ConversationMessageMapper.FilterDuplicateAssistantMessages(
            conv.Messages,
            m => m.Role,
            m => m.TurnIndex,
            m => m.Content,
            m => !string.IsNullOrEmpty(m.ToolCalls)
        );

        if (compactionBoundaryTurnIndex.HasValue)
        {
            filteredMessages = filteredMessages
                .Where(m => m.TurnIndex > compactionBoundaryTurnIndex.Value)
                .ToList();
        }

        var validToolCallIds = new HashSet<string>();
```

Finally, in `PrepareMessagesForAssistantAsync`, fix the two call sites so the file still compiles (no functional change in this task — both still pass no boundary, just now by explicit name instead of position):

```csharp
            var switchMessages = await ApplyAssistantSwitchLogicAsync(conv, assistantName, cancellationToken: cancellationToken);
```

and

```csharp
        var conversationMessages = await BuildOpenAiMessagesAsync(conv, assistantName, cancellationToken: cancellationToken);
```

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test src/server/GuideAntsApi.Tests/GuideAntsApi.Tests.csproj --filter "FullyQualifiedName~ConversationHistoryBuilderBatchingTests"`
Expected: PASS — all pre-existing tests in the file plus the 4 new ones.

Then run the full history-builder-adjacent suite to confirm nothing else broke from the signature change:

Run: `dotnet test src/server/GuideAntsApi.Tests/GuideAntsApi.Tests.csproj --filter "FullyQualifiedName~ConversationHistoryBuilder|FullyQualifiedName~ConversationServiceAssistantSwitchingTests|FullyQualifiedName~ConversationServicePreflightTests"`
Expected: PASS, no regressions.

- [ ] **Step 5: Commit (ask first)**

```bash
git add src/server/GuideAntsApi/Services/Conversations/Mapping/IConversationHistoryBuilder.cs src/server/GuideAntsApi/Services/Conversations/Mapping/ConversationHistoryBuilder.cs src/server/GuideAntsApi.Tests/Services/Conversations/ConversationHistoryBuilderBatchingTests.cs
git commit -m "Adds boundary-aware tail filtering to the conversation history builder"
```

---

## Task 2: `BuildCompactionSummaryMessageAsync` + compose summary/tail in `PrepareMessagesForAssistantAsync`, with a safe fallback (5.1, 5.3 confirmed untouched, 5.4)

**Files:**
- Modify: `src/server/GuideAntsApi/Services/Conversations/Mapping/ConversationHistoryBuilder.cs`
- Create: `src/server/GuideAntsApi.Tests/Services/Conversations/ConversationHistoryBuilderCompactionTests.cs`

**Interfaces:**
- Consumes: `AntRunner.Chat.Compaction.CompactionEngine.Compact(IReadOnlyList<ChatMessage>, IReadOnlyList<TurnFacts>) -> CompactionResult` and `TurnFacts(int, IReadOnlyList<string>, IReadOnlyList<string>)` (W3); `ConversationMessageMapper.FilterDuplicateAssistantMessages(ICollection<NotebookConversationMessage>) -> List<NotebookConversationMessage>` and `ToChatMessage(NotebookConversationMessage) -> ChatMessage` (existing); Task 1's `BuildOpenAiMessagesAsync`/`ApplyAssistantSwitchLogicAsync` boundary parameter.
- Produces: `PrepareMessagesForAssistantAsync`'s returned list now begins with `[system: summary]` immediately before the post-boundary tail whenever `conv.CompactionBoundaryTurnIndex` is set, for both the switch and non-switch branches; falls back to the pre-W5 full-history call on any exception in the compaction path.

- [ ] **Step 1: Write the failing tests**

Create `ConversationHistoryBuilderCompactionTests.cs`. This is a fresh fixture (not a reuse of `ConversationHistoryBuilderBatchingTests`'s) because it needs `ConversationTurn` rows — with `Status`, `FilesCreated`/`FilesModified`, and (for the switch test) differing `AssistantName` — which that file's base fixture deliberately doesn't seed:

```csharp
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
        AssistantUtility.ClearCache(SwitchAssistantName);
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

        // "Claude" resolves to null via AssistantUtility here (not cache-seeded), so no
        // instructions/context messages precede the summary - it is message 0.
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
        conv.CompactionBoundaryTurnIndex = 3;
        var secondPass = await _builder.PrepareMessagesForAssistantAsync(conv, "Claude", Guid.NewGuid());
        var secondSummary = secondPass[0].GetText();

        secondSummary.Should().NotContain(firstSummary); // not built by wrapping the first summary
        secondSummary.Should().Contain("[Compacted 6 earlier message(s).]"); // all 3 turns this time
        secondSummary.Should().Contain("user message 3"); // turn 3's content now folded into the summary itself
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
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test src/server/GuideAntsApi.Tests/GuideAntsApi.Tests.csproj --filter "FullyQualifiedName~ConversationHistoryBuilderCompactionTests"`
Expected: FAIL — no summary is ever produced today (`PrepareMessages_WithBoundary_LeadsWithSummaryThenTail` etc. fail their assertions); `PrepareMessages_NoBoundary_NeverCallsTheEngine` passes already (nothing to regress yet).

- [ ] **Step 3: Implement**

Add to `ConversationHistoryBuilder.cs`'s usings:

```csharp
using AntRunner.Chat.Compaction;
```

Replace `PrepareMessagesForAssistantAsync`'s tail from the `if (isAssistantSwitch)` block onward:

```csharp
        if (isAssistantSwitch)
        {
            var switchMessages = await BuildHistoryTailAsync(
                conv, assistantName, isAssistantSwitch: true, cancellationToken);
            string? ctxContent = null;
            var ctxIndex = messages.FindIndex(m => m.Role == ChatMessageRole.System && m.GetText().StartsWith("{\"contextOptions\""));
            if (ctxIndex >= 0)
            {
                ctxContent = messages[ctxIndex].GetText();
                messages.RemoveAt(ctxIndex);
            }

            messages.AddRange(switchMessages);

            if (ctxContent != null)
            {
                messages.Add(new ChatMessage(ChatMessageRole.System, ctxContent));
            }

            return messages;
        }

        var conversationMessages = await BuildHistoryTailAsync(
            conv, assistantName, isAssistantSwitch: false, cancellationToken);
        messages.AddRange(conversationMessages);
        return messages;
    }

    /// <summary>
    /// Composes the post-instructions portion of history for one assistant path (switch or
    /// continuation): when <see cref="NotebookConversation.CompactionBoundaryTurnIndex"/> is set,
    /// this is <c>[system: summary] + verbatim tail</c> (D3); otherwise it is the unmodified full
    /// history, exactly as before this plan (D7 - zero behavior change when never compacted).
    /// Per the spec's Error handling table, any failure while building the compacted form falls
    /// back to the full-history call rather than failing the turn.
    /// </summary>
    private async Task<List<ChatMessage>> BuildHistoryTailAsync(
        NotebookConversation conv,
        string assistantName,
        bool isAssistantSwitch,
        CancellationToken cancellationToken)
    {
        var boundary = conv.CompactionBoundaryTurnIndex;
        if (!boundary.HasValue)
        {
            return isAssistantSwitch
                ? await ApplyAssistantSwitchLogicAsync(conv, assistantName, cancellationToken: cancellationToken)
                : await BuildOpenAiMessagesAsync(conv, assistantName, cancellationToken: cancellationToken);
        }

        try
        {
            var tail = isAssistantSwitch
                ? await ApplyAssistantSwitchLogicAsync(conv, assistantName, boundary, cancellationToken)
                : await BuildOpenAiMessagesAsync(conv, assistantName, boundary, cancellationToken);
            var summary = await BuildCompactionSummaryMessageAsync(conv.Id, boundary.Value, cancellationToken);

            var result = new List<ChatMessage>(tail.Count + 1) { summary };
            result.AddRange(tail);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Compaction failed for conversation {ConversationId} at boundary {BoundaryTurnIndex}; falling back to full uncompacted history",
                conv.Id, boundary.Value);

            return isAssistantSwitch
                ? await ApplyAssistantSwitchLogicAsync(conv, assistantName, cancellationToken: cancellationToken)
                : await BuildOpenAiMessagesAsync(conv, assistantName, cancellationToken: cancellationToken);
        }
    }

    /// <summary>
    /// Loads everything before the compaction boundary and runs it through W3's pure
    /// <see cref="CompactionEngine"/>. Recomputed from source messages every call (never from a
    /// previously-stored summary), so repeated compaction does not compound loss - see D3.
    /// </summary>
    private async Task<ChatMessage> BuildCompactionSummaryMessageAsync(
        Guid conversationId, int boundaryTurnIndex, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var preBoundaryMessages = await db.NotebookConversationMessages
            .AsNoTracking()
            .Where(m => m.NotebookConversationId == conversationId && m.TurnIndex <= boundaryTurnIndex)
            .OrderBy(m => m.TurnIndex)
            .ThenBy(m => m.MessageSequence)
            .ToListAsync(cancellationToken);

        var deduped = ConversationMessageMapper.FilterDuplicateAssistantMessages(preBoundaryMessages);
        var chatMessages = deduped.Select(ConversationMessageMapper.ToChatMessage).ToList();

        var turnRows = await db.ConversationTurns
            .AsNoTracking()
            .Where(t => t.NotebookConversationId == conversationId && t.TurnIndex <= boundaryTurnIndex)
            .Select(t => new { t.TurnIndex, t.FilesCreated, t.FilesModified })
            .ToListAsync(cancellationToken);

        var turnFacts = turnRows
            .Select(t => new TurnFacts(t.TurnIndex, ParseFileList(t.FilesCreated), ParseFileList(t.FilesModified)))
            .ToList();

        var compaction = CompactionEngine.Compact(chatMessages, turnFacts);

        return new ChatMessage(ChatMessageRole.System, compaction.SummaryText);
    }

    private static List<string> ParseFileList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
```

`JsonOptions` here is the class's existing shared field (`ConversationHistoryBuilder.cs:24-28`) — no new options object needed. `ChatMessageRole`/`DataModelChatRole` aliases already exist at the top of the file.

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test src/server/GuideAntsApi.Tests/GuideAntsApi.Tests.csproj --filter "FullyQualifiedName~ConversationHistoryBuilderCompactionTests"`
Expected: PASS (6 tests).

Then the full regression sweep from Task 1's Step 4 again, plus this new file:

Run: `dotnet test src/server/GuideAntsApi.Tests/GuideAntsApi.Tests.csproj --filter "FullyQualifiedName~ConversationHistoryBuilder|FullyQualifiedName~ConversationServiceAssistantSwitchingTests|FullyQualifiedName~ConversationServicePreflightTests"`
Expected: PASS, no regressions — this is the concrete evidence for 5.2 and 5.3.

- [ ] **Step 5: Commit (ask first)**

```bash
git add src/server/GuideAntsApi/Services/Conversations/Mapping/ConversationHistoryBuilder.cs src/server/GuideAntsApi.Tests/Services/Conversations/ConversationHistoryBuilderCompactionTests.cs
git commit -m "Wires the compaction summary into the conversation history builder"
```

---

## Task 3: `CaptureCompaction` trace signal and the client boundary-marker stream event (5.5, 5.6)

**Files:**
- Modify: `src/server/AntRunner.Chat/AntRunner.Chat/ThreadRunTrace.cs`
- Modify: `src/server/GuideAntsApi/Services/Conversations/Tracing/TurnTraceCollector.cs`
- Modify: `src/server/GuideAntsApi/Services/Conversations/Tracing/TurnTracePayload.cs`
- Modify: `src/server/GuideAntsApi/Models/Conversations/StreamingEvent.cs`
- Modify: `src/server/GuideAntsApi/Services/Conversations/Streaming/StreamingEvents.cs`
- Modify: `src/server/GuideAntsApi/Services/Conversations/Streaming/ConversationStreamEngine.cs`
- Test: `src/server/GuideAntsApi.Tests/Services/Conversations/Tracing/TurnTraceCollectorTests.cs`
- Test: `src/server/GuideAntsApi.Tests/Services/Conversations/Streaming/CompactionBoundaryMarkerTests.cs`

**Interfaces:**
- Consumes: `ConversationStreamRunContext.Conversation` (`.CompactionBoundaryTurnIndex`) and `.TurnIndex` (already present, `IConversationStreamEngine.cs:8-27`); `TurnTraceCollector` (already constructed at `ConversationStreamEngine.cs:257`).
- Produces: `IThreadRunTraceCollector.CaptureCompaction(int boundaryTurnIndex)`; `TurnTraceSegment.CompactionBoundaryTurnIndex` (`int?`); `StreamingEventTypes.CompactionBoundaryMarker`; `StreamingEvents.BuildCompactionBoundaryMarkerEvent(int boundaryTurnIndex, Guid? turnId = null) -> StreamingEvent`; `ConversationStreamEngine.ShouldEmitCompactionBoundaryMarker(int? compactionBoundaryTurnIndex, int currentTurnIndex) -> bool` (`internal static`, directly unit-tested per the `RecordLearnedContextWindow` precedent).

- [ ] **Step 1: Write the failing tests**

`TurnTraceCollectorTests.cs`:

```csharp
using FluentAssertions;
using GuideAntsApi.Services.Conversations.Tracing;

namespace GuideAntsApi.Tests.Services.Conversations.Tracing;

[TestClass]
public sealed class TurnTraceCollectorTests
{
    [TestMethod]
    public void CaptureCompaction_SetsBoundaryOnTheFinalizedSegment()
    {
        var collector = new TurnTraceCollector("Claude", "gpt-4o-mini");

        collector.CaptureCompaction(3);
        var segment = collector.BuildFinalizedSegment("completed");

        segment.CompactionBoundaryTurnIndex.Should().Be(3);
    }

    [TestMethod]
    public void NoCompaction_LeavesBoundaryNull()
    {
        var collector = new TurnTraceCollector("Claude", "gpt-4o-mini");

        var segment = collector.BuildFinalizedSegment("completed");

        segment.CompactionBoundaryTurnIndex.Should().BeNull();
    }
}
```

`CompactionBoundaryMarkerTests.cs` (mirrors `ContextOverflowLearningTests.cs`'s style of testing an `internal static` method on `ConversationStreamEngine` directly):

```csharp
using FluentAssertions;
using GuideAntsApi.Services.Conversations.Streaming;

namespace GuideAntsApi.Tests.Services.Conversations.Streaming;

[TestClass]
public sealed class CompactionBoundaryMarkerTests
{
    [TestMethod]
    public void NoBoundary_NeverEmits()
    {
        ConversationStreamEngine.ShouldEmitCompactionBoundaryMarker(null, currentTurnIndex: 5)
            .Should().BeFalse();
    }

    [TestMethod]
    public void FirstTurnAfterBoundary_Emits()
    {
        ConversationStreamEngine.ShouldEmitCompactionBoundaryMarker(3, currentTurnIndex: 4)
            .Should().BeTrue();
    }

    [TestMethod]
    public void LaterTurnAfterBoundary_DoesNotReEmit()
    {
        ConversationStreamEngine.ShouldEmitCompactionBoundaryMarker(3, currentTurnIndex: 5)
            .Should().BeFalse();
    }

    [TestMethod]
    public void TheBoundaryTurnItself_DoesNotEmit()
    {
        ConversationStreamEngine.ShouldEmitCompactionBoundaryMarker(3, currentTurnIndex: 3)
            .Should().BeFalse();
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test src/server/GuideAntsApi.Tests/GuideAntsApi.Tests.csproj --filter "FullyQualifiedName~TurnTraceCollectorTests|FullyQualifiedName~CompactionBoundaryMarkerTests"`
Expected: FAIL to compile — `CaptureCompaction` and `ShouldEmitCompactionBoundaryMarker` don't exist yet.

- [ ] **Step 3: Implement**

In `ThreadRunTrace.cs`, add to the interface (next to `CaptureToolLimitState`):

```csharp
    void CaptureCompaction(int boundaryTurnIndex);
```

In `TurnTracePayload.cs`, add to `TurnTraceSegment` (next to `ToolLimitEscalationPhase`):

```csharp
    public int? CompactionBoundaryTurnIndex { get; set; }
```

In `TurnTraceCollector.cs`, add (next to `CaptureToolLimitState`):

```csharp
    public void CaptureCompaction(int boundaryTurnIndex)
    {
        lock (_sync)
        {
            _segment.CompactionBoundaryTurnIndex = boundaryTurnIndex;
        }
    }
```

In `StreamingEvent.cs`, add to `StreamingEventTypes` (next to `PendingClientTool`):

```csharp
    public const string CompactionBoundaryMarker = "compaction_boundary_marker"; // Compacted history now backs this turn
```

In `StreamingEvents.cs`, add (next to `BuildToolActivityProgress`):

```csharp
    public static StreamingEvent BuildCompactionBoundaryMarkerEvent(int boundaryTurnIndex, Guid? turnId = null)
    {
        var payload = new
        {
            turnId,
            boundaryTurnIndex,
            timestamp = DateTime.UtcNow
        };

        return new StreamingEvent(
            StreamingEventTypes.CompactionBoundaryMarker,
            JsonSerializer.Serialize(payload, JsonOptions));
    }
```

In `ConversationStreamEngine.cs`, add the new `internal static` method next to `RecordLearnedContextWindow`:

```csharp
    /// <summary>
    /// The marker fires once, on the first turn whose history was built from the compacted form -
    /// not on every subsequent turn, since a live-connected observer only needs it at the moment
    /// the transcript's shape actually changes. A client that connects later gets the same fact
    /// from the conversation's context-status read instead (contextStatus.boundaryTurnIndex).
    /// </summary>
    internal static bool ShouldEmitCompactionBoundaryMarker(int? compactionBoundaryTurnIndex, int currentTurnIndex) =>
        compactionBoundaryTurnIndex.HasValue && currentTurnIndex == compactionBoundaryTurnIndex.Value + 1;
```

Then wire both signals in at the point the trace collector is created:

```csharp
            var turnTraceCollector = new TurnTraceCollector(context.AssistantName, context.ModelDeploymentId);
            context.ChatOptions.TraceCollector = turnTraceCollector;

            if (context.Conversation.CompactionBoundaryTurnIndex is int boundaryTurnIndex)
            {
                turnTraceCollector.CaptureCompaction(boundaryTurnIndex);

                if (ShouldEmitCompactionBoundaryMarker(boundaryTurnIndex, context.TurnIndex))
                {
                    TryWrite(StreamingEvents.BuildCompactionBoundaryMarkerEvent(boundaryTurnIndex, context.DbTurn.Id));
                }
            }
```

This replaces the original two lines at `ConversationStreamEngine.cs:257-258` — `TryWrite` is a local function declared later in the same enclosing lambda (`:276`) and is callable here regardless of declaration order.

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test src/server/GuideAntsApi.Tests/GuideAntsApi.Tests.csproj --filter "FullyQualifiedName~TurnTraceCollectorTests|FullyQualifiedName~CompactionBoundaryMarkerTests"`
Expected: PASS (6 tests total).

Then confirm the whole server solution still builds (this task touches a shared, actively-running streaming file):

Run: `dotnet build src/server/GuideAntsApi.sln`
Expected: builds clean.

- [ ] **Step 5: Commit (ask first)**

```bash
git add src/server/AntRunner.Chat/AntRunner.Chat/ThreadRunTrace.cs src/server/GuideAntsApi/Services/Conversations/Tracing/TurnTraceCollector.cs src/server/GuideAntsApi/Services/Conversations/Tracing/TurnTracePayload.cs src/server/GuideAntsApi/Models/Conversations/StreamingEvent.cs src/server/GuideAntsApi/Services/Conversations/Streaming/StreamingEvents.cs src/server/GuideAntsApi/Services/Conversations/Streaming/ConversationStreamEngine.cs src/server/GuideAntsApi.Tests/Services/Conversations/Tracing/TurnTraceCollectorTests.cs src/server/GuideAntsApi.Tests/Services/Conversations/Streaming/CompactionBoundaryMarkerTests.cs
git commit -m "Adds a compaction trace signal and a client boundary-marker stream event"
```

---

## Task 4: Full verification (5.7 rollup)

- [ ] **Step 1: Server unit tests and build**

Run: `dotnet build src/server/GuideAntsApi.sln && dotnet test src/server/GuideAntsApi.sln --filter "TestCategory!=Integration"`
Expected: build clean, no new failures anywhere in the solution (not just the files this plan touched — `IConversationHistoryBuilder`'s signature change is a shared interface).

- [ ] **Step 2: Confirm no schema or migration changes**

Run: `git diff --stat <task-1-base-commit>..HEAD -- src/server/GuideAntsApi.DataModel/Migrations/`
Expected: empty. This workstream adds no new columns — `CompactionBoundaryTurnIndex` already exists from W4.

- [ ] **Step 3: Manual end-to-end check against a running API (if a dev DB is available)**

Run the API (`dotnet run --project src/server/GuideAntsApi`), send a couple of messages in a notebook conversation so at least two turns complete, `POST .../compact` (W4) to set a boundary, then send one more message. Confirm: (a) the assistant's request history now starts with a system message containing "condensed handoff briefing" rather than full replay of the pre-boundary turns; (b) an SSE `compaction_boundary_marker` event arrives exactly once, on that first post-compaction turn, and not again on a second follow-up message; (c) switching assistants after compaction still shows a summary followed by the tail followed by the existing handoff notice. If no dev DB is available in this environment, skip this step and say so explicitly rather than claiming it passed.

- [ ] **Step 4: Tick W5 in the checklist**

In `docs/context-compaction-plan.md`, tick 5.1–5.7 and add a line under W5 linking this plan, matching the "Implementation plan written" pattern used for W1–W4.

---

## Self-review

**Spec coverage.**
- 5.1 (history builder: boundary set → load pre-boundary turns as TurnFacts, call the engine, emit `[system: summary]` + verbatim tail) → Task 2's `BuildHistoryTailAsync`/`BuildCompactionSummaryMessageAsync`, built on Task 1's tail-filtering plumbing.
- 5.2 (no boundary → entirely unchanged, no engine call) → `BuildHistoryTailAsync`'s early-return guard when `boundary` is null; proven by the full pre-existing test suites in `ConversationHistoryBuilderBatchingTests.cs`/`ConversationServiceAssistantSwitchingTests.cs` continuing to pass unmodified (Task 1 Step 4, Task 2 Step 4) plus `PrepareMessages_NoBoundary_NeverCallsTheEngine`.
- 5.3 (leave the three `BuildPublishedMessagesForAssistantAsync` call sites untouched) → Global Constraints states this explicitly; no task modifies that method, `PublishedConversationService.cs`, or `SandboxWireConversationService.cs`.
- 5.4 (assistant-switch path composes correctly with a boundary) → Task 1's `ApplyAssistantSwitchLogic_WithBoundary_ExcludesMessagesAtOrBeforeBoundary` (tail-only filtering) and Task 2's `PrepareMessages_AssistantSwitchWithBoundary_SummaryPrecedesTailWhichPrecedesHandoff` (full ordering: summary → tail → handoff notice, with the handoff-notice-presence check deliberately still reading the original `conv.Messages`, per the Findings note).
- 5.5 (`CaptureCompaction` on `IThreadRunTraceCollector`, following `CaptureToolLimitState`) → Task 3.
- 5.6 (stream event for the client boundary marker) → Task 3's `BuildCompactionBoundaryMarkerEvent` + `ShouldEmitCompactionBoundaryMarker` gating.
- 5.7 (tests: with and without boundary, assistant switch, repeated compaction loses no more than a single compaction) → "with/without boundary" and "assistant switch" covered across Tasks 1–2 as above; "repeated compaction" covered by Task 2's `PrepareMessages_RepeatedCompaction_RecomputesFromOriginalMessages_NotFromThePriorSummary`, which asserts the second summary is not built by wrapping the first (D3's no-summary-of-summary-degradation guarantee).
- Spec's *Error handling* → "Engine throws: Log, proceed with uncompacted history. Compaction failing must never fail a turn." → Task 2's `BuildHistoryTailAsync` try/catch + `PrepareMessages_CompactionPathThrows_FallsBackToFullUncompactedHistory`.
- Spec's *Section vocabulary → Placement* ("injected as a system message... before the kept tail") → Task 2's message ordering (`[summary, ...tail]`), verified in every Task 2 composition test.

**Known limitations, deliberately accepted.**
- `CaptureCompaction` records only the boundary turn index, not a message count — see the Findings entry explaining why threading a message count into `ConversationStreamEngine` isn't worth the extra DB query or the interface churn, given the count already ships via the compact endpoint's HTTP response (W4).
- The boundary marker fires once (first post-boundary turn only), not on every turn while compacted. An observer who connects to a live stream after that turn has already completed will not see the SSE event, but will see the same fact from the ordinary conversation/context-status read (`boundaryTurnIndex`) — this is a client rendering concern for W8, not a gap in this plan.
- `BuildHistoryTailAsync`'s fallback re-runs the *entire* uncompacted-history call rather than trying to salvage partial work (e.g. reusing an already-built tail if only the summary step failed) — intentional, since "proceed with uncompacted history" in the spec's error table means full history, not a partially-compacted hybrid that was never validated as correct.

**Placeholder scan.** Every code step has complete code; no "TODO," "similar to Task N," or unshown logic. The one "read X first" instruction from the W3/W4 plans' style (confirming exact signatures before writing) was resolved during this plan's own research rather than deferred to the implementer, since every signature referenced here (`CompactionEngine.Compact`, `TurnFacts`, `ConversationMessageMapper.*`, `ConversationStreamRunContext`, `TurnTraceSegment`) was read directly from the current branch, not recalled from the W3/W4 plans' text.

**Type consistency.** `BuildOpenAiMessagesAsync`/`ApplyAssistantSwitchLogicAsync`'s new `int? compactionBoundaryTurnIndex = null` parameter matches between the interface (Task 1), the implementation (Task 1), and every call site (Task 1's two fixed call sites, Task 2's `BuildHistoryTailAsync`). `CompactionEngine.Compact`/`TurnFacts`/`CompactionResult` are used with the exact shapes confirmed against the shipped W3 source, not from memory of the W3 plan text. `IThreadRunTraceCollector.CaptureCompaction(int)` matches its `TurnTraceCollector` implementation and its one call site in Task 3.
