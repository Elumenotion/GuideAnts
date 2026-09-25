# W7: Recall Tool Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give a compacted conversation a way back to what the summary left out — a server-executed `conversation_recall(query, page?)` tool that searches the pre-boundary messages of *this* conversation and nothing else, exposed to the model only when the conversation actually has a compaction boundary.

**Architecture:** Two new pieces and one new wiring channel. `ConversationRecallService` (scoped, EF + authorization-by-construction) does the query, the in-memory relevance scan, the pagination and the excerpting. `ConversationRecallTools` is a static `[Tool]` class in the exact shape of `MemoryTools`/`SkillTools` — it takes only `query` and `page` from the model and gets `ConversationId` injected by the framework through a `[Parameter(Hidden = true)] InvocationContext`, so the model has no way to name another conversation. Exposure is the interesting part: `AssistantUtility`'s definition cache is keyed by **assistant name and shared process-wide**, so the `skills_list`/`skills_read` injection trick there cannot express a *per-conversation* gate. Instead a new `ChatRunOptions.EnableConversationRecall` flag carries the decision from `ConversationService` (which already holds the loaded `NotebookConversation`) into `ThreadRun`, which advertises the tool for that run only. The dispatch-side request builder is registered unconditionally, because the model can only emit a call for a tool that was advertised.

**Tech Stack:** `GuideAntsApi` (ASP.NET Core 8, EF Core 8), `AntRunner.Chat` / `AntRunner.ToolCalling` (reflection-driven tool contracts), MSTest + FluentAssertions + Moq + EF Core InMemory.

**Spec:** [`docs/superpowers/specs/2026-09-21-context-compaction-design.md`](../specs/2026-09-21-context-compaction-design.md) — *Recall*, *Decisions* D7, *Testing* item 6. Checklist: [`docs/context-compaction-plan.md`](../../context-compaction-plan.md) W7 (7.1–7.7).

## Global Constraints

- **Sequencing precondition met.** W7 depends on W4's `CompactionBoundaryTurnIndex` column (done) and W5's history-builder wiring (done). Nothing blocks starting.
- **The model never supplies a conversation id.** `ConversationId` comes from `InvocationContext`, which the runtime injects for `[RequiresNotebookContext]` methods. The tool method must not declare a conversation parameter under any name, and no task may add one.
- **`[RequiresNotebookContext]` is mandatory on the tool method.** Context injection is gated on it: `ThreadRun.cs:1516` only injects `builder.Params["context"]` when `RequiresNotebookContext(builder.Path)` is true. Without the attribute the tool silently runs with `context == null` and can never resolve a conversation.
- **Operation id is exactly `conversation_recall`** (spec *Recall*). It is already wire-safe under `ToolOperationIdSanitizer.ToWireName` (`^[a-zA-Z0-9_-]{1,64}$`), so the wire name and the operation id are the same string. Do not rename it.
- **Search space is `TurnIndex <= CompactionBoundaryTurnIndex`, not `<`.** See *Findings* — the spec says `<`, the shipped W5 history builder uses `<=`, and the history builder is what actually defines "compacted away." Recall must cover exactly the set the summary replaced.
- **No DB `Tools` row, no Guide Builder checkbox.** `conversation_recall` is auto-injected like `skills_list`/`skills_read`, which have no `ApplicationDbContext` seed rows either. Do not add a seed row or a migration.
- **v1 is the private notebook path only** (spec *Non-goals*). `PublishedConversationService`, `SandboxWireConversationService`, `ConversationManager` and `Agent` all construct their own `ChatRunOptions`; leaving the new flag at its `false` default keeps every one of them byte-identical to today. Do not touch them.
- **Server tests: MSTest (`[TestClass]`/`[TestMethod]`) + FluentAssertions**, Moq for interfaces, EF Core InMemory for the DB. `AssistantUtility`'s static cache is process-global — any test that seeds it via reflection carries `[DoNotParallelize]` and clears the cache in both `[TestInitialize]` and `[TestCleanup]`, exactly as `ThreadRunContextOverflowTests.cs` and `ConversationServicePreflightTests.cs` already do.
- Never co-author commits, and only commit when the user has said to (per `CLAUDE.md` and memory `no-commits-without-permission`). The commit steps below are proposed checkpoints — **ask first**.

## Findings that shaped this plan

Every claim below is grep-verified against `feature/compaction` HEAD `7d789dd`.

- **The spec's `TurnIndex < boundary` is off by one turn against shipped code.** `ConversationHistoryBuilder.BuildCompactionSummaryMessageAsync` (`ConversationHistoryBuilder.cs:231`) selects pre-boundary messages with `m.TurnIndex <= boundaryTurnIndex`, and the verbatim tail with `m.TurnIndex > compactionBoundaryTurnIndex.Value` (`:291`, `:430`). So the boundary turn itself is *summarized*, not kept. If recall used `<`, the boundary turn would be the one turn that is neither in the tail nor reachable by recall — a hole in the middle of the feature's whole promise. **This plan uses `<=` and the spec line should be read as corrected.**
- **`AssistantUtility`'s injection point cannot express a per-conversation gate.** `GetAssistantCreateRequest` caches by assistant name in a process-global `ConcurrentDictionary` (`AssistantUtility.cs:22`, `:96` — `GenerateCacheKey` is the identity function) and hands out the *same mutable instance* to every caller. `TryInjectRegisteredTool(options, "skills_list")` (`:229-233`) works only because "does this assistant have skills" is a property of the assistant. "Does this conversation have a boundary" is not. Adding the recall tool there would leak it into every conversation using that guide, violating D7 ("zero behavior change for conversations that were never compacted"). Hence the run-scoped `ChatRunOptions` flag.
- **`ClientToolDefinitions` is the wrong channel.** It is the only existing run-scoped tool channel on `ChatRunOptions`, but everything on it is added to `clientHandledToolNames` (`ThreadRun.cs:542`) and *never executed server-side*. Recall needs server execution. A new flag is the smaller change.
- **The advertise path and the dispatch path are separate and separately cached.** Advertising happens in `ThreadRun.ExecuteAsync`'s tool assembly (`ThreadRun.cs:498-524`) from `assistantDef.Tools`. Dispatch happens through `RequestBuilderCache`, filled by `EnsureRequestBuilderCache` (`ThreadRun.cs:2048`), which is keyed by assistant name and gates annotated tools on `assistantOperationIds` — the *cached* definition's tool list. A run-scoped advertised tool therefore gets no builder unless one is registered explicitly. Task 3 registers it unconditionally; that is safe because a tool that was never advertised can never be called, and the crew-bridge block immediately below (`:2134-2167`) is the existing precedent for adding builders outside the `assistantOperationIds` gate.
- **Missing builders fail quietly and badly.** In the `tool_calls` branch, a name with no builder is routed to `serverHandled` (`ThreadRun.cs:696-699`), and in `ExecuteToolCallsAsync` the `builders.TryGetValue` miss (`:1502`) simply skips it — no `ToolOutput`, so no `tool_result` message for that `tool_call_id`, which most providers reject on the next round. This is the concrete failure Task 3's unconditional registration prevents.
- **Cross-conversation reads fail structurally, not just by convention.** `ToolCaller`'s parameter validation rejects any supplied key that is neither a declared parameter nor a runtime-injected one (`ToolCaller.cs:718-736`, and the public `ValidateParamsAgainstSchema()` at `:902`, which returns `(bool IsValid, string? ErrorMessage)` and builds the message `` `conversationId` is not a valid parameter. `` at `:1010-1013`). A model that invents `conversationId` produces an unknown-parameter validation failure, not a silent read of someone else's conversation. The schema generator also strips `Hidden` parameters from the LLM-facing request body (`ToolContractRegistry.cs:218-219`), so `context` never appears in the advertised schema at all.
- **`tool://localhost` is what makes a generated schema dispatch to a static method.** `ToolContractRegistry.GenerateSchemaFromMethodAndAttributes` emits `servers: [{ url: "tool://localhost" }]` and a path of `{type.FullName}.{method.Name}` (`:186-196`); `ToolCaller` maps the `tool` scheme to `ActionType.LocalFunction` (`ToolCaller.cs:242`). Nothing extra is needed to make a new `[Tool]` static method executable — only discovery (automatic, via assembly scan) and a registered builder.
- **The client needs no work.** `conversation_recall` renders through the generic tool-call cell; the client's only hardcoded tool names are in `skillToolsetMapping.ts` (Guide Builder toolset presets, which recall is deliberately not part of) and test fixtures. W7's checklist has no client items, and this plan adds none.
- **`ConversationService` already holds the loaded conversation entity at the wiring point.** `LoadStreamMetadataAsync` loads the full `NotebookConversation` (`ConversationService.cs:799-810` — a plain `FirstOrDefaultAsync`, not a projection, so `CompactionBoundaryTurnIndex` is populated), and `BuildRunContext` (`:988-1015`) has it as `ctx.Conversation`. The wiring is one line at one call site.
- **`ConversationServicePreflightTests.cs` is a working end-to-end harness for this seam.** It drives the real `ConversationStreamEngine` with a mocked `IChatCompletionClient` over an InMemory DB. Capturing the `ChatCompletionRequest` there proves the flag travels `NotebookConversation` → `ChatRunOptions` → `ThreadRun` → provider request, which is a far better test of Task 4 than reflecting into the private `BuildRunContext`.
- **A `"stop"` finish reason never touches `EnsureRequestBuilderCache`.** Both call sites (`ThreadRun.cs:659`, `:1487`) are inside the `tool_calls` path. Tests that assert advertising can use a mock client returning `"stop"` and will not need a DB-backed `ToolCaller.GetToolCallers`.
- **`CompactionText.Truncate` (the surrogate-safe truncator) is `internal` to `AntRunner.Chat`**, visible to `GuideAntsApi.Tests` but **not** to `GuideAntsApi` (`AntRunner.Chat.csproj:27` grants only the test assembly). The recall service needs its own surrogate-safe slicing; it cannot reuse that helper.

## File Structure

**Created:**
- `src/server/GuideAntsApi/Services/Conversations/ConversationRecallService.cs` — `IConversationRecallService`, `ConversationRecallService`, and the two result records. Owns the EF query, the scan, the ranking, the paging and the excerpting. One responsibility: "given a conversation and a query, what pre-boundary messages matter."
- `src/server/GuideAntsApi/Services/ConversationRecallTools.cs` — the static `[Tool]` surface. Deliberately thin (argument guards, scope, serialize), sited beside `MemoryTools.cs`/`SkillTools.cs` because that is where this codebase keeps static tool classes.
- `src/server/GuideAntsApi.Tests/Services/Conversations/ConversationRecallServiceTests.cs`
- `src/server/GuideAntsApi.Tests/Services/ConversationRecallToolsTests.cs`
- `src/server/GuideAntsApi.Tests/ChatLayer/ConversationRecallExposureTests.cs`
- `src/server/GuideAntsApi.Tests/Services/Conversations/ConversationRecallWiringTests.cs`

**Modified:**
- `src/server/AntRunner.Chat/AntRunner.Chat/ChatRunOptions.cs` — add `EnableConversationRecall`
- `src/server/AntRunner.Chat/AntRunner.Chat/ThreadRun.cs` — advertise the tool when the flag is set; register its builder unconditionally
- `src/server/GuideAntsApi/Services/Conversations/ConversationService.cs` — set the flag from the conversation's boundary
- `src/server/GuideAntsApi/Configuration/StartupConfiguration.cs` — register `IConversationRecallService`
- `src/server/GuideAntsApi/Program.cs` — `ConversationRecallTools.InitializeServiceProvider`
- `docs/context-compaction-plan.md` — tick W7

## Review Focus

Five input classes the spec implies but never names. Each one's test is written into the task that owns the code.

1. **Empty or whitespace-only `query`.** A model will send one. The tool must return a JSON error and the service an empty page — never an unfiltered dump of the whole pre-boundary history into the context we are trying to shrink. *(Tests: Task 1 step 1, Task 2 step 1.)*
2. **`page` of 0, negative, or past the last page.** `(page - 1) * PageSize` goes negative and `Skip` throws on a negative count. Must clamp to page 1 and return an empty result set past the end, with `totalMatches` still honest. *(Test: Task 1 step 1.)*
3. **Pre-boundary messages with empty or whitespace content.** Every conversation has them (streaming placeholders, tool calls with no text). Scoring and excerpting must not throw and must not produce zero-length phantom hits. *(Test: Task 1 step 1.)*
4. **A long message whose match sits at the very end, and content containing astral characters at the excerpt cut.** Excerpts are bounded, so the cut lands mid-string; a split surrogate pair produces invalid UTF-16 that can fail JSON serialization downstream. *(Test: Task 1 step 1.)*
5. **The model inventing an extra argument such as `conversationId`.** This is the literal attack the spec's *Testing* item 6 names. It must fail parameter validation rather than read another conversation. *(Test: Task 2 step 1.)*

---

## Task 1: `ConversationRecallService` — search, rank, page, excerpt

**Files:**
- Create: `src/server/GuideAntsApi/Services/Conversations/ConversationRecallService.cs`
- Test: `src/server/GuideAntsApi.Tests/Services/Conversations/ConversationRecallServiceTests.cs`

**Interfaces:**
- Consumes: `ApplicationDbContext` (`GuideAntsApi.DataModel`), `NotebookConversation.CompactionBoundaryTurnIndex` (`int?`, added in W4), `NotebookConversationMessage` (`TurnIndex`, `MessageSequence`, `Role`, `FunctionName`, `Created`, `Content`), `IServiceScopeFactory`.
- Produces, for Task 2:
  - `interface IConversationRecallService { Task<ConversationRecallPage> RecallAsync(Guid conversationId, string query, int page, CancellationToken ct = default); }`
  - `sealed record ConversationRecallHit(int TurnIndex, int MessageSequence, string Role, string? FunctionName, DateTime Created, string Excerpt)`
  - `sealed record ConversationRecallPage(string Query, int Page, int PageSize, int TotalMatches, bool HasMore, int? BoundaryTurnIndex, IReadOnlyList<ConversationRecallHit> Results)`
  - Namespace `GuideAntsApi.Services.Conversations`.

- [ ] **Step 1: Write the failing tests**

Create `src/server/GuideAntsApi.Tests/Services/Conversations/ConversationRecallServiceTests.cs`:

```csharp
using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services.Conversations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using DataModelChatRole = GuideAntsApi.DataModel.Models.ChatRole;

namespace GuideAntsApi.Tests.Services.Conversations;

[TestClass]
public sealed class ConversationRecallServiceTests
{
    private static IServiceScopeFactory BuildScopeFactory(string dbName)
    {
        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(o => o.UseInMemoryDatabase(dbName));
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private static NotebookConversationMessage Msg(
        Guid conversationId, int turnIndex, int sequence, string content,
        DataModelChatRole role = DataModelChatRole.Assistant, string? functionName = null) => new()
        {
            NotebookConversationId = conversationId,
            TurnIndex = turnIndex,
            MessageSequence = sequence,
            Role = role,
            FunctionName = functionName,
            Content = content,
            Created = new DateTime(2026, 9, 24, 0, 0, 0, DateTimeKind.Utc).AddMinutes(turnIndex)
        };

    /// <summary>Seeds one conversation with a boundary and returns a ready service.</summary>
    private static async Task<(ConversationRecallService Service, Guid ConversationId)> SeedAsync(
        string dbName, int? boundary, params NotebookConversationMessage[] messages)
    {
        var scopeFactory = BuildScopeFactory(dbName);
        var conversationId = messages.Length > 0 ? messages[0].NotebookConversationId : Guid.NewGuid();

        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.NotebookConversations.Add(new NotebookConversation
            {
                Id = conversationId,
                NotebookId = Guid.NewGuid(),
                Title = "t",
                CompactionBoundaryTurnIndex = boundary
            });
            db.NotebookConversationMessages.AddRange(messages);
            await db.SaveChangesAsync();
        }

        return (new ConversationRecallService(scopeFactory, NullLogger<ConversationRecallService>.Instance),
                conversationId);
    }

    [TestMethod]
    public async Task RecallAsync_RanksBroaderTermCoverageAboveRepetitionOfACommonTerm()
    {
        var conversationId = Guid.NewGuid();
        var (service, _) = await SeedAsync(
            nameof(RecallAsync_RanksBroaderTermCoverageAboveRepetitionOfACommonTerm),
            boundary: 5,
            Msg(conversationId, 1, 0, "alpha beta"),
            Msg(conversationId, 2, 0, "alpha alpha alpha alpha"),
            Msg(conversationId, 3, 0, "alpha gamma"));

        var page = await service.RecallAsync(conversationId, "alpha beta", 1);

        page.TotalMatches.Should().Be(3);
        page.Results.Select(r => r.TurnIndex).Should().ContainInOrder(1, 2, 3,
            "matching both terms must outrank repeating the term that appears everywhere");
    }

    [TestMethod]
    public async Task RecallAsync_SearchesTheBoundaryTurnItselfButNothingAfterIt()
    {
        var conversationId = Guid.NewGuid();
        var (service, _) = await SeedAsync(
            nameof(RecallAsync_SearchesTheBoundaryTurnItselfButNothingAfterIt),
            boundary: 2,
            Msg(conversationId, 1, 0, "widget in the first turn"),
            Msg(conversationId, 2, 0, "widget on the boundary turn"),
            Msg(conversationId, 3, 0, "widget after the boundary"));

        var page = await service.RecallAsync(conversationId, "widget", 1);

        page.Results.Select(r => r.TurnIndex).Should().BeEquivalentTo(new[] { 1, 2 },
            "the history builder summarizes TurnIndex <= boundary, so recall must cover exactly that set");
    }

    [TestMethod]
    public async Task RecallAsync_NeverReturnsMessagesFromAnotherConversation()
    {
        var conversationId = Guid.NewGuid();
        var otherConversationId = Guid.NewGuid();
        var scopeFactory = BuildScopeFactory(nameof(RecallAsync_NeverReturnsMessagesFromAnotherConversation));

        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.NotebookConversations.AddRange(
                new NotebookConversation { Id = conversationId, NotebookId = Guid.NewGuid(), Title = "mine", CompactionBoundaryTurnIndex = 5 },
                new NotebookConversation { Id = otherConversationId, NotebookId = Guid.NewGuid(), Title = "theirs", CompactionBoundaryTurnIndex = 5 });
            db.NotebookConversationMessages.AddRange(
                Msg(conversationId, 1, 0, "my own secret"),
                Msg(otherConversationId, 1, 0, "someone else's secret"));
            await db.SaveChangesAsync();
        }

        var service = new ConversationRecallService(scopeFactory, NullLogger<ConversationRecallService>.Instance);

        var page = await service.RecallAsync(conversationId, "secret", 1);

        page.TotalMatches.Should().Be(1);
        page.Results.Single().Excerpt.Should().Contain("my own secret");
        page.Results.Should().NotContain(r => r.Excerpt.Contains("someone else"));
    }

    [TestMethod]
    public async Task RecallAsync_PaginatesWithHasMoreOnAllButTheLastPage()
    {
        var conversationId = Guid.NewGuid();
        var messages = Enumerable.Range(1, 7)
            .Select(i => Msg(conversationId, i, 0, $"widget number {i}"))
            .ToArray();
        var (service, _) = await SeedAsync(
            nameof(RecallAsync_PaginatesWithHasMoreOnAllButTheLastPage), boundary: 10, messages);

        var first = await service.RecallAsync(conversationId, "widget", 1);
        var second = await service.RecallAsync(conversationId, "widget", 2);

        first.PageSize.Should().Be(5);
        first.Results.Should().HaveCount(5);
        first.TotalMatches.Should().Be(7);
        first.HasMore.Should().BeTrue();

        second.Results.Should().HaveCount(2);
        second.HasMore.Should().BeFalse();
        second.Results.Select(r => r.TurnIndex).Should().NotIntersectWith(first.Results.Select(r => r.TurnIndex));
    }

    [DataTestMethod]
    [DataRow(0)]
    [DataRow(-3)]
    public async Task RecallAsync_NonPositivePage_ClampsToPageOne(int requestedPage)
    {
        var conversationId = Guid.NewGuid();
        var (service, _) = await SeedAsync(
            $"{nameof(RecallAsync_NonPositivePage_ClampsToPageOne)}-{requestedPage}",
            boundary: 5,
            Msg(conversationId, 1, 0, "widget"));

        var page = await service.RecallAsync(conversationId, "widget", requestedPage);

        page.Page.Should().Be(1);
        page.Results.Should().HaveCount(1);
    }

    [TestMethod]
    public async Task RecallAsync_PageBeyondTheEnd_ReturnsEmptyResultsButHonestTotal()
    {
        var conversationId = Guid.NewGuid();
        var (service, _) = await SeedAsync(
            nameof(RecallAsync_PageBeyondTheEnd_ReturnsEmptyResultsButHonestTotal),
            boundary: 5,
            Msg(conversationId, 1, 0, "widget"));

        var page = await service.RecallAsync(conversationId, "widget", 9);

        page.Results.Should().BeEmpty();
        page.TotalMatches.Should().Be(1);
        page.HasMore.Should().BeFalse();
    }

    [DataTestMethod]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("a !")]
    public async Task RecallAsync_QueryWithNoUsableTerms_ReturnsAnEmptyPage(string query)
    {
        var conversationId = Guid.NewGuid();
        var (service, _) = await SeedAsync(
            $"{nameof(RecallAsync_QueryWithNoUsableTerms_ReturnsAnEmptyPage)}-{query.Length}-{query.Trim()}",
            boundary: 5,
            Msg(conversationId, 1, 0, "widget"));

        var page = await service.RecallAsync(conversationId, query, 1);

        page.Results.Should().BeEmpty();
        page.TotalMatches.Should().Be(0,
            "an unusable query must never dump the whole pre-boundary history back into the context");
    }

    [TestMethod]
    public async Task RecallAsync_NoBoundary_ReturnsAnEmptyPage()
    {
        var conversationId = Guid.NewGuid();
        var (service, _) = await SeedAsync(
            nameof(RecallAsync_NoBoundary_ReturnsAnEmptyPage),
            boundary: null,
            Msg(conversationId, 1, 0, "widget"));

        var page = await service.RecallAsync(conversationId, "widget", 1);

        page.BoundaryTurnIndex.Should().BeNull();
        page.Results.Should().BeEmpty();
        page.TotalMatches.Should().Be(0);
    }

    [TestMethod]
    public async Task RecallAsync_EmptyAndWhitespaceContent_NeitherThrowsNorMatches()
    {
        var conversationId = Guid.NewGuid();
        var (service, _) = await SeedAsync(
            nameof(RecallAsync_EmptyAndWhitespaceContent_NeitherThrowsNorMatches),
            boundary: 5,
            Msg(conversationId, 1, 0, string.Empty),
            Msg(conversationId, 2, 0, "    "),
            Msg(conversationId, 3, 0, "widget"));

        var page = await service.RecallAsync(conversationId, "widget", 1);

        page.Results.Should().ContainSingle();
        page.Results.Single().TurnIndex.Should().Be(3);
    }

    [TestMethod]
    public async Task RecallAsync_MatchAtTheEndOfALongMessage_ReturnsABoundedExcerptAroundTheMatch()
    {
        var conversationId = Guid.NewGuid();
        var content = new string('x', 5000) + " widget tail";
        var (service, _) = await SeedAsync(
            nameof(RecallAsync_MatchAtTheEndOfALongMessage_ReturnsABoundedExcerptAroundTheMatch),
            boundary: 5,
            Msg(conversationId, 1, 0, content));

        var excerpt = (await service.RecallAsync(conversationId, "widget", 1)).Results.Single().Excerpt;

        excerpt.Should().Contain("widget");
        excerpt.Length.Should().BeLessThanOrEqualTo(602, "600 content chars plus at most two ellipses");
        excerpt.Should().StartWith("…", "the excerpt window opens after the start of the message");
    }

    [TestMethod]
    public async Task RecallAsync_ExcerptCutLandingOnAnAstralCharacter_NeverSplitsASurrogatePair()
    {
        var conversationId = Guid.NewGuid();
        // "widget " is 7 units, so the emoji run starts at index 7 and every ODD index is a high
        // surrogate. An unguarded 600-unit window ends at index 599 -- odd -- and would split a pair.
        var content = "widget " + string.Concat(Enumerable.Repeat("\U0001F600", 2000));
        var (service, _) = await SeedAsync(
            nameof(RecallAsync_ExcerptCutLandingOnAnAstralCharacter_NeverSplitsASurrogatePair),
            boundary: 5,
            Msg(conversationId, 1, 0, content));

        var excerpt = (await service.RecallAsync(conversationId, "widget", 1)).Results.Single().Excerpt;
        var body = excerpt.Trim('…');

        body.Should().NotBeEmpty();
        char.IsHighSurrogate(body[^1]).Should().BeFalse("a trailing high surrogate is half of a split pair");
        char.IsLowSurrogate(body[0]).Should().BeFalse("a leading low surrogate is half of a split pair");
        System.Text.Encoding.UTF8.GetString(System.Text.Encoding.UTF8.GetBytes(body))
            .Should().Be(body, "invalid UTF-16 does not survive a UTF-8 round trip");
    }

    [TestMethod]
    public async Task RecallAsync_EqualScores_OrdersByTurnThenSequenceDeterministically()
    {
        var conversationId = Guid.NewGuid();
        var (service, _) = await SeedAsync(
            nameof(RecallAsync_EqualScores_OrdersByTurnThenSequenceDeterministically),
            boundary: 9,
            Msg(conversationId, 4, 1, "widget"),
            Msg(conversationId, 2, 1, "widget"),
            Msg(conversationId, 2, 0, "widget"));

        var first = await service.RecallAsync(conversationId, "widget", 1);
        var second = await service.RecallAsync(conversationId, "widget", 1);

        first.Results.Select(r => (r.TurnIndex, r.MessageSequence))
            .Should().ContainInOrder((2, 0), (2, 1), (4, 1));
        second.Results.Should().BeEquivalentTo(first.Results, o => o.WithStrictOrdering());
    }

    [TestMethod]
    public async Task RecallAsync_PreservesMessageStructureOnEachHit()
    {
        var conversationId = Guid.NewGuid();
        var (service, _) = await SeedAsync(
            nameof(RecallAsync_PreservesMessageStructureOnEachHit),
            boundary: 5,
            Msg(conversationId, 2, 3, "{\"stdout\":\"widget built\"}",
                DataModelChatRole.Tool, functionName: "run_python"));

        var hit = (await service.RecallAsync(conversationId, "widget", 1)).Results.Single();

        hit.TurnIndex.Should().Be(2);
        hit.MessageSequence.Should().Be(3);
        hit.Role.Should().Be("Tool");
        hit.FunctionName.Should().Be("run_python");
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test GuideAntsApi.Tests/GuideAntsApi.Tests.csproj --filter "FullyQualifiedName~ConversationRecallServiceTests"` from `src/server`
Expected: build failure — `ConversationRecallService` and `ConversationRecallPage` do not exist yet.

- [ ] **Step 3: Write the implementation**

Create `src/server/GuideAntsApi/Services/Conversations/ConversationRecallService.cs`:

```csharp
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

    private static readonly Regex TermSplitter = new("[^a-z0-9]+", RegexOptions.Compiled);

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
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test GuideAntsApi.Tests/GuideAntsApi.Tests.csproj --filter "FullyQualifiedName~ConversationRecallServiceTests"` from `src/server`
Expected: PASS, all tests.

- [ ] **Step 5: Commit (ask the user first)**

```bash
git add src/server/GuideAntsApi/Services/Conversations/ConversationRecallService.cs \
        src/server/GuideAntsApi.Tests/Services/Conversations/ConversationRecallServiceTests.cs
git commit -m "feat(compaction): add ConversationRecallService for pre-boundary search

Ranks pre-boundary messages with an in-memory tf-idf scan (no SQL Server
full-text dependency), pages them, and returns bounded excerpts that keep
each hit's turn/role/tool structure. Search space matches the history
builder's pre-boundary selection exactly: TurnIndex <= boundary.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

## Task 2: `conversation_recall` static tool, DI, and startup registration

**Files:**
- Create: `src/server/GuideAntsApi/Services/ConversationRecallTools.cs`
- Modify: `src/server/GuideAntsApi/Configuration/StartupConfiguration.cs` (one line, beside `ICompactionService` at line 164)
- Modify: `src/server/GuideAntsApi/Program.cs` (one call, beside `SkillTools.InitializeServiceProvider` at line 321)
- Test: `src/server/GuideAntsApi.Tests/Services/ConversationRecallToolsTests.cs`

**Interfaces:**
- Consumes: `IConversationRecallService`/`ConversationRecallPage` (Task 1), `ToolAttribute`/`ParameterAttribute`/`RequiresNotebookContextAttribute` (`AntRunner.ToolCalling.Attributes`), `InvocationContext` (`AntRunner.ToolCalling`).
- Produces, for Tasks 3 and 4: a tool discoverable by `ToolContractRegistry` under operation id `conversation_recall`, whose fully qualified method name is `GuideAntsApi.Services.ConversationRecallTools.RecallConversation`.

- [ ] **Step 1: Write the failing tests**

Create `src/server/GuideAntsApi.Tests/Services/ConversationRecallToolsTests.cs`:

```csharp
using System.Text.Json;
using AntRunner.ToolCalling;
using AntRunner.ToolCalling.Functions;
using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services;
using GuideAntsApi.Services.Conversations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using DataModelChatRole = GuideAntsApi.DataModel.Models.ChatRole;

namespace GuideAntsApi.Tests.Services;

[TestClass]
[DoNotParallelize] // ConversationRecallTools holds a process-global service provider
public sealed class ConversationRecallToolsTests
{
    private const string MethodName = "GuideAntsApi.Services.ConversationRecallTools.RecallConversation";

    private static ServiceProvider BuildProvider(string dbName)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<ApplicationDbContext>(o => o.UseInMemoryDatabase(dbName));
        services.AddScoped<IConversationRecallService, ConversationRecallService>();
        return services.BuildServiceProvider();
    }

    private static async Task<Guid> SeedCompactedConversationAsync(ServiceProvider provider)
    {
        var conversationId = Guid.NewGuid();
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.NotebookConversations.Add(new NotebookConversation
        {
            Id = conversationId,
            NotebookId = Guid.NewGuid(),
            Title = "t",
            CompactionBoundaryTurnIndex = 3
        });
        db.NotebookConversationMessages.Add(new NotebookConversationMessage
        {
            NotebookConversationId = conversationId,
            TurnIndex = 1,
            MessageSequence = 0,
            Role = DataModelChatRole.User,
            Content = "please remember the widget calibration constant is 7"
        });
        await db.SaveChangesAsync();
        return conversationId;
    }

    [TestMethod]
    public async Task RecallConversation_ScopesTheSearchToTheContextsConversation()
    {
        var provider = BuildProvider(nameof(RecallConversation_ScopesTheSearchToTheContextsConversation));
        ConversationRecallTools.InitializeServiceProvider(provider);
        var conversationId = await SeedCompactedConversationAsync(provider);

        var context = new InvocationContext(Guid.NewGuid(), Guid.NewGuid(), conversationId);

        var json = await ConversationRecallTools.RecallConversation("widget calibration", 1, context);

        json.Should().Contain("widget calibration constant is 7");
        using var document = JsonDocument.Parse(json);
        document.RootElement.GetProperty("totalMatches").GetInt32().Should().Be(1);
        document.RootElement.GetProperty("boundaryTurnIndex").GetInt32().Should().Be(3);
    }

    [DataTestMethod]
    [DataRow("")]
    [DataRow("   ")]
    public async Task RecallConversation_BlankQuery_ReturnsAJsonErrorRatherThanAnything(string query)
    {
        var provider = BuildProvider($"{nameof(RecallConversation_BlankQuery_ReturnsAJsonErrorRatherThanAnything)}-{query.Length}");
        ConversationRecallTools.InitializeServiceProvider(provider);
        var conversationId = await SeedCompactedConversationAsync(provider);

        var json = await ConversationRecallTools.RecallConversation(
            query, 1, new InvocationContext(Guid.NewGuid(), Guid.NewGuid(), conversationId));

        using var document = JsonDocument.Parse(json);
        document.RootElement.TryGetProperty("error", out _).Should().BeTrue();
        json.Should().NotContain("widget calibration constant");
    }

    [TestMethod]
    public async Task RecallConversation_WithoutContext_ReturnsAJsonError()
    {
        var provider = BuildProvider(nameof(RecallConversation_WithoutContext_ReturnsAJsonError));
        ConversationRecallTools.InitializeServiceProvider(provider);

        var json = await ConversationRecallTools.RecallConversation("widget", 1, context: null);

        using var document = JsonDocument.Parse(json);
        document.RootElement.TryGetProperty("error", out _).Should().BeTrue();
    }

    [TestMethod]
    public void GeneratedSchema_ExposesOnlyQueryAndPage_AndNeverAConversationId()
    {
        ToolContractRegistry.RefreshContracts();

        var schemaJson = ToolContractRegistry.GenerateOpenApiSchema(MethodName);

        using var document = JsonDocument.Parse(schemaJson);
        var properties = document.RootElement
            .GetProperty("paths").GetProperty(MethodName).GetProperty("post")
            .GetProperty("requestBody").GetProperty("content").GetProperty("application_json")
            .GetProperty("schema").GetProperty("properties");

        properties.EnumerateObject().Select(p => p.Name)
            .Should().BeEquivalentTo(new[] { "query", "page" },
                "the model may only supply the query and the page; scoping is structural");

        schemaJson.Should().NotContain("conversationId");
        schemaJson.Should().NotContain("context");
    }

    [TestMethod]
    public void ToolContract_IsRegisteredAsRequiringNotebookContext()
    {
        ToolContractRegistry.RefreshContracts();

        var contract = ToolContractRegistry.GetContract(MethodName);

        contract.ToolMetadata.Should().NotBeNull();
        contract.ToolMetadata!.OperationId.Should().Be("conversation_recall");
        contract.RequiresNotebookContext.Should().BeTrue(
            "without this attribute ThreadRun never injects InvocationContext and the tool cannot resolve a conversation");
    }

    [TestMethod]
    public void ToolCall_SupplyingAnExtraConversationId_FailsValidationInsteadOfReadingAnotherConversation()
    {
        ToolContractRegistry.RefreshContracts();
        var schemaJson = ToolContractRegistry.GenerateOpenApiSchema(MethodName);
        var validation = OpenApiHelper.ValidateAndParseOpenApiSpec(schemaJson);
        validation.Status.Should().BeTrue();

        // The single-argument overload is deliberate: the assistant-name overload is async and
        // reads DomainAuth from the database, which this unit test has no reason to stand up.
        var builders = ToolCaller.GetToolCallers(validation.Spec!);
        var builder = builders["conversation_recall"].Clone();
        builder.Params = new Dictionary<string, object>
        {
            ["query"] = "widget",
            ["conversationId"] = Guid.NewGuid().ToString()
        };

        var (isValid, errorMessage) = builder.ValidateParamsAgainstSchema();

        isValid.Should().BeFalse("an invented conversation id must be rejected, not silently honored");
        errorMessage.Should().Contain("conversationId");
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test GuideAntsApi.Tests/GuideAntsApi.Tests.csproj --filter "FullyQualifiedName~ConversationRecallToolsTests"` from `src/server`
Expected: build failure — `ConversationRecallTools` does not exist.

- [ ] **Step 3: Write the tool class**

Create `src/server/GuideAntsApi/Services/ConversationRecallTools.cs`:

```csharp
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
        [Parameter(Description = "1-based page of results. Omit for the first page.", Default = 1)] int page = 1,
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

        var result = await recall.RecallAsync(context.ConversationId, query, page, cancellationToken);
        return JsonSerializer.Serialize(result, JsonOptions);
    }

    private static string JsonError(string message) =>
        JsonSerializer.Serialize(new { error = message });
}
```

- [ ] **Step 4: Register the service and the static provider**

In `src/server/GuideAntsApi/Configuration/StartupConfiguration.cs`, immediately after the `ICompactionService` registration (line 164):

```csharp
        services.AddScoped<ICompactionService, CompactionService>();
        services.AddScoped<IConversationRecallService, ConversationRecallService>();
```

In `src/server/GuideAntsApi/Program.cs`, immediately after the `SkillTools` initializer (line 321):

```csharp
        // Initialize static service provider for SkillTools (skills_list/skills_read)
        GuideAntsApi.Services.SkillTools.InitializeServiceProvider(app.Services);

        // Initialize static service provider for ConversationRecallTools (conversation_recall)
        GuideAntsApi.Services.ConversationRecallTools.InitializeServiceProvider(app.Services);
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test GuideAntsApi.Tests/GuideAntsApi.Tests.csproj --filter "FullyQualifiedName~ConversationRecallToolsTests"` from `src/server`
Expected: PASS, all tests.

- [ ] **Step 6: Commit (ask the user first)**

```bash
git add src/server/GuideAntsApi/Services/ConversationRecallTools.cs \
        src/server/GuideAntsApi/Configuration/StartupConfiguration.cs \
        src/server/GuideAntsApi/Program.cs \
        src/server/GuideAntsApi.Tests/Services/ConversationRecallToolsTests.cs
git commit -m "feat(compaction): expose conversation_recall as a server-handled tool

Static [Tool] method in the MemoryTools/SkillTools shape. The model supplies
only query and page; ConversationId arrives through the framework-injected
InvocationContext, so a cross-conversation read is structurally impossible
rather than merely discouraged.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

## Task 3: Run-scoped exposure in `ThreadRun`

**Files:**
- Modify: `src/server/AntRunner.Chat/AntRunner.Chat/ChatRunOptions.cs` (add one property after `ClientToolDefinitions`, line 64)
- Modify: `src/server/AntRunner.Chat/AntRunner.Chat/ThreadRun.cs` (advertise in the tool assembly at ~line 524; register the builder in `EnsureRequestBuilderCache` at ~line 2131; two new private helpers)
- Test: `src/server/GuideAntsApi.Tests/ChatLayer/ConversationRecallExposureTests.cs`

**Interfaces:**
- Consumes: `ToolContractRegistry.GetAllToolOperations()` → `Dictionary<string /*operationId*/, string /*fully qualified method name*/>`, `ToolContractRegistry.GenerateOpenApiSchema(string)` → schema JSON, `OpenApiHelper.GetToolDefinitionsFromJson(string)` → `List<ToolDefinition>`, `OpenApiHelper.ValidateAndParseOpenApiSpec(string)`, `ToolCaller.GetToolCallers(spec, assistantName)`, `ToolOperationIdSanitizer.ToWireName(string)`, `ChatToolDefinition`/`ChatFunctionDefinition` (`AntRunner.Chat.Abstractions`), `ThreadRunTraceToolDefinitionSnapshot`.
- Produces, for Task 4: `ChatRunOptions.EnableConversationRecall` (`bool`, default `false`) and `ThreadRun.ConversationRecallToolName` (`internal const string` = `"conversation_recall"`).

- [ ] **Step 1: Write the failing test**

Create `src/server/GuideAntsApi.Tests/ChatLayer/ConversationRecallExposureTests.cs`:

```csharp
using System.Collections;
using System.Reflection;
using System.Text.Json;
using AntRunner.Chat;
using AntRunner.Chat.Abstractions;
using AntRunner.ToolCalling;
using AntRunner.ToolCalling.AssistantDefinitions;
using FluentAssertions;

namespace GuideAntsApi.Tests.ChatLayer;

[TestClass]
[DoNotParallelize]
public sealed class ConversationRecallExposureTests
{
    [TestInitialize]
    public void SetUp()
    {
        AssistantUtility.ClearAllCache();
        ToolContractRegistry.RefreshContracts();
    }

    [TestCleanup]
    public void TearDown() => AssistantUtility.ClearAllCache();

    [TestMethod]
    public async Task ExecuteAsync_FlagOff_DoesNotAdvertiseConversationRecall()
    {
        var request = await RunOnceAsync(enableConversationRecall: false);

        ToolNames(request).Should().NotContain("conversation_recall",
            "D7: a conversation that was never compacted must see zero behavior change");
    }

    [TestMethod]
    public async Task ExecuteAsync_FlagOn_AdvertisesConversationRecallExactlyOnce()
    {
        var request = await RunOnceAsync(enableConversationRecall: true);

        ToolNames(request).Should().ContainSingle(n => n == "conversation_recall");
    }

    [TestMethod]
    public async Task ExecuteAsync_FlagOn_AdvertisedSchemaExposesOnlyQueryAndPage()
    {
        var request = await RunOnceAsync(enableConversationRecall: true);

        var recall = request.Tools!.Single(t => t.Function?.Name == "conversation_recall");
        var parameters = recall.Function!.Parameters!.ToJsonString();

        parameters.Should().Contain("query");
        parameters.Should().Contain("page");
        parameters.Should().NotContain("context");
        parameters.Should().NotContain("conversationId");
    }

    private static IEnumerable<string> ToolNames(ChatCompletionRequest request) =>
        (request.Tools ?? []).Select(t => t.Function?.Name ?? string.Empty);

    private static async Task<ChatCompletionRequest> RunOnceAsync(bool enableConversationRecall)
    {
        const string assistantName = "Recall Exposure Test Assistant";
        SeedAssistantCache(assistantName, new AssistantDefinition { Name = assistantName, Model = "gpt-4o-mini" });

        var client = new StoppingClient();
        var options = new ChatRunOptions
        {
            AssistantName = assistantName,
            Instructions = "hello",
            EnableConversationRecall = enableConversationRecall,
            ExecutionPolicy = new ResolvedExecutionPolicy(
                "gpt-4o-mini",
                "openai-chat",
                ParameterAuthority.AssistantDefinition,
                new Dictionary<string, JsonElement>())
        };

        await ThreadRun.ExecuteAsync(
            options,
            new SingleClientFactory(client),
            previous: null,
            httpClient: null,
            onMessage: null,
            onStream: null,
            new InvocationContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()),
            CancellationToken.None);

        client.CapturedRequests.Should().NotBeEmpty();
        return client.CapturedRequests[0];
    }

    private static void SeedAssistantCache(string name, AssistantDefinition definition)
    {
        var cacheType = typeof(AssistantUtility).GetNestedType("CachedAssistant", BindingFlags.NonPublic)!;
        var entry = Activator.CreateInstance(cacheType, definition)!;
        var cache = (IDictionary)typeof(AssistantUtility)
            .GetField("AssistantDefinitionCache", BindingFlags.Static | BindingFlags.NonPublic)!
            .GetValue(null)!;
        cache[name] = entry;
    }

    private sealed class StoppingClient : IChatCompletionClient
    {
        public bool SupportsToolChoiceNone => true;
        public List<ChatCompletionRequest> CapturedRequests { get; } = [];

        public Task<ChatCompletionResponse> GetCompletionAsync(
            ChatCompletionRequest request, CancellationToken cancellationToken = default)
        {
            CapturedRequests.Add(request);
            return Task.FromResult(new ChatCompletionResponse(
                new[] { new ChatChoice(new ChatMessage(ChatRole.Assistant, "done"), "stop") }, null));
        }

        public Task<ChatCompletionResponse> StreamCompletionAsync(
            ChatCompletionRequest request,
            Action<ChatCompletionChunk> onChunk,
            CancellationToken cancellationToken = default) =>
            GetCompletionAsync(request, cancellationToken);
    }

    private sealed class SingleClientFactory : IChatCompletionClientFactory
    {
        private readonly IChatCompletionClient _client;
        public SingleClientFactory(IChatCompletionClient client) => _client = client;
        public string? DefaultDeploymentId => "gpt-4o-mini";
        public IChatCompletionClient CreateClient(string? deploymentId, HttpClient? httpClient = null) => _client;
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test GuideAntsApi.Tests/GuideAntsApi.Tests.csproj --filter "FullyQualifiedName~ConversationRecallExposureTests"` from `src/server`
Expected: build failure — `ChatRunOptions.EnableConversationRecall` does not exist.

- [ ] **Step 3: Add the flag to `ChatRunOptions`**

In `src/server/AntRunner.Chat/AntRunner.Chat/ChatRunOptions.cs`, after the `ClientToolDefinitions` property:

```csharp
        /// <summary>
        /// When true, this run advertises the server-executed <c>conversation_recall</c> tool so the
        /// model can search the part of the conversation that compaction replaced with a summary.
        ///
        /// It is a run-scoped flag rather than an entry in the assistant's tool list because
        /// <see cref="AssistantUtility"/> caches definitions per assistant name and shares one
        /// instance process-wide — "this conversation has a compaction boundary" is a property of
        /// the conversation, not of the guide. Defaults to false, so every caller that does not set
        /// it (published, sandbox-wire, agent invocations) keeps its current behavior.
        /// </summary>
        public bool EnableConversationRecall { get; set; }
```

- [ ] **Step 4: Advertise the tool in `ThreadRun`'s tool assembly**

In `src/server/AntRunner.Chat/AntRunner.Chat/ThreadRun.cs`, immediately after the `if (assistantDef.Tools != null) { ... }` block that ends at line 524 and before `if (options.ClientToolDefinitions != null)`:

```csharp
            if (options.EnableConversationRecall)
            {
                TryAdvertiseRegisteredTool(
                    ConversationRecallToolName, "compaction", tools, registeredToolNames, traceTools);
            }
```

Add the constant beside the other `ThreadRun` statics (near `RequestBuilderCache`, line 36):

```csharp
        /// <summary>
        /// Operation id of the recall tool injected per run when a conversation has a compaction
        /// boundary. Never present in an assistant's persisted tool list.
        /// </summary>
        internal const string ConversationRecallToolName = "conversation_recall";
```

Add the helper beside `ResolveToolTraceSource` (line 937):

```csharp
        /// <summary>
        /// Adds a registry-discovered static tool to this run's advertised tool list. Used for tools
        /// whose exposure is decided per run rather than by the assistant's persisted tool list.
        /// Failures are logged and skipped: an unadvertised tool is a missing capability, never a
        /// failed turn.
        /// </summary>
        private static void TryAdvertiseRegisteredTool(
            string operationId,
            string traceSource,
            List<ChatToolDefinition> tools,
            HashSet<string> registeredToolNames,
            List<ThreadRunTraceToolDefinitionSnapshot> traceTools)
        {
            var wireName = ToolOperationIdSanitizer.ToWireName(operationId);
            if (registeredToolNames.Contains(wireName))
            {
                return;
            }

            var match = ToolContractRegistry.GetAllToolOperations()
                .FirstOrDefault(kvp => string.Equals(kvp.Key, operationId, StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrEmpty(match.Key))
            {
                Logger.LogWarning(
                    "Run-scoped tool {OperationId} is not registered in the tool contract registry",
                    LogValueSanitizer.Sanitize(operationId));
                return;
            }

            try
            {
                var schema = ToolContractRegistry.GenerateOpenApiSchema(match.Value);
                foreach (var def in OpenApiHelper.GetToolDefinitionsFromJson(schema))
                {
                    var function = def.Function?.AsObject;
                    if (function?.Name != wireName)
                    {
                        continue;
                    }

                    var parametersJsonNode = JsonNode.Parse(JsonSerializer.Serialize(function.Parameters));
                    tools.Add(new ChatToolDefinition(
                        new ChatFunctionDefinition(function.Name!, function.Description, parametersJsonNode)));
                    registeredToolNames.Add(function.Name!);
                    traceTools.Add(new ThreadRunTraceToolDefinitionSnapshot(
                        function.Name!, function.Description, parametersJsonNode?.ToJsonString(), traceSource));
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning(
                    ex,
                    "Failed to advertise run-scoped tool {OperationId}",
                    LogValueSanitizer.Sanitize(operationId));
            }
        }
```

- [ ] **Step 5: Register the dispatch-side builder unconditionally**

In `EnsureRequestBuilderCache`, immediately after the annotated-tool loop that ends at line 2131 (the `}` closing `if (assistantDef?.Tools != null)`) and before the crew-bridge block:

```csharp
            // conversation_recall never appears in an assistant's persisted tool list -- ThreadRun
            // injects it per run when the conversation has a compaction boundary (W7/D7), so the
            // assistantOperationIds gate above can never see it. Register its builder unconditionally:
            // a tool that was not advertised can never be called, and a call with no builder is
            // silently dropped (no tool_result for its tool_call_id), which providers reject.
            await TryRegisterRegisteredToolBuilder(
                ConversationRecallToolName, assistantName, assistantRequestBuilders);
```

Add the helper next to `TryAdvertiseRegisteredTool`:

```csharp
        /// <summary>
        /// Registers the request builder for a registry-discovered static tool, outside the
        /// assistant's own tool list. Mirrors the crew-bridge registration immediately below its
        /// call site.
        /// </summary>
        private static async Task TryRegisterRegisteredToolBuilder(
            string operationId,
            string assistantName,
            Dictionary<string, ToolCaller> builders)
        {
            var match = ToolContractRegistry.GetAllToolOperations()
                .FirstOrDefault(kvp => string.Equals(kvp.Key, operationId, StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrEmpty(match.Key))
            {
                return;
            }

            try
            {
                var schema = ToolContractRegistry.GenerateOpenApiSchema(match.Value);
                var validationResult = OpenApiHelper.ValidateAndParseOpenApiSpec(schema);
                if (!validationResult.Status || validationResult.Spec == null)
                {
                    return;
                }

                var requestBuilders = await ToolCaller.GetToolCallers(validationResult.Spec, assistantName);
                var wireName = ToolOperationIdSanitizer.ToWireName(match.Key);
                if (requestBuilders.TryGetValue(wireName, out var builder))
                {
                    builders[wireName] = builder;
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning(
                    ex,
                    "Failed to register request builder for run-scoped tool {OperationId}",
                    LogValueSanitizer.Sanitize(operationId));
            }
        }
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test GuideAntsApi.Tests/GuideAntsApi.Tests.csproj --filter "FullyQualifiedName~ConversationRecallExposureTests"` from `src/server`
Expected: PASS, all three tests.

Then run the neighboring suites that touch the same code, to catch a regression in tool assembly:

Run: `dotnet test GuideAntsApi.Tests/GuideAntsApi.Tests.csproj --filter "FullyQualifiedName~ToolContractRegistryTests|FullyQualifiedName~ThreadRun"` from `src/server`
Expected: PASS.

- [ ] **Step 7: Commit (ask the user first)**

```bash
git add src/server/AntRunner.Chat/AntRunner.Chat/ChatRunOptions.cs \
        src/server/AntRunner.Chat/AntRunner.Chat/ThreadRun.cs \
        src/server/GuideAntsApi.Tests/ChatLayer/ConversationRecallExposureTests.cs
git commit -m "feat(compaction): advertise conversation_recall per run, not per assistant

AssistantUtility caches definitions per assistant name and shares one
instance process-wide, so it cannot express a per-conversation gate.
ChatRunOptions.EnableConversationRecall carries the decision into ThreadRun
instead. The dispatch-side builder is registered unconditionally, since a
tool that was never advertised can never be called.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

## Task 4: Wire the flag to the conversation's boundary

**Files:**
- Modify: `src/server/GuideAntsApi/Services/Conversations/ConversationService.cs` (`BuildRunContext`, the `ChatOptions` initializer at lines 1003-1011)
- Test: `src/server/GuideAntsApi.Tests/Services/Conversations/ConversationRecallWiringTests.cs`

**Interfaces:**
- Consumes: `ChatRunOptions.EnableConversationRecall` (Task 3), `NotebookConversation.CompactionBoundaryTurnIndex` (W4), `StreamSendContext.Conversation` (already the fully loaded entity).
- Produces: nothing new. This is the last link in the chain.

- [ ] **Step 1: Write the failing test**

Create `src/server/GuideAntsApi.Tests/Services/Conversations/ConversationRecallWiringTests.cs`. It drives the real stream engine over an InMemory DB and captures the provider-bound request, so it proves the whole chain (`NotebookConversation` → `ChatRunOptions` → `ThreadRun` → request) rather than one assignment.

```csharp
using System.Collections;
using System.Reflection;
using AntRunner.Chat;
using AntRunner.Chat.Abstractions;
using AntRunner.ToolCalling;
using AntRunner.ToolCalling.AssistantDefinitions;
using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models.Conversations;
using GuideAntsApi.Services.Conversations;
using GuideAntsApi.Services.Core;
using GuideAntsApi.Services.Routing;
using GuideAntsApi.Tests.TestUtils;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Moq;
using ChatRole = AntRunner.Chat.Abstractions.ChatRole;

namespace GuideAntsApi.Tests.Services.Conversations;

[TestClass]
[DoNotParallelize]
public sealed class ConversationRecallWiringTests
{
    private const string AssistantName = "Claude";

    private ApplicationDbContext _dbContext = null!;
    private Guid _userId;
    private Guid _projectId;
    private Guid _notebookId;
    private Guid _conversationId;
    private List<ChatCompletionRequest> _capturedRequests = null!;

    [TestInitialize]
    public void TestInitialize()
    {
        AssistantUtility.ClearAllCache();
        ToolContractRegistry.RefreshContracts();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        _dbContext = new ApplicationDbContext(options);

        _userId = Guid.NewGuid();
        _projectId = Guid.NewGuid();
        _notebookId = Guid.NewGuid();
        _conversationId = Guid.NewGuid();
        _capturedRequests = [];

        SeedAssistantDefinitionCache(
            AssistantName, new AssistantDefinition { Name = AssistantName, Model = "gpt-4o-mini" });
    }

    [TestCleanup]
    public void TestCleanup()
    {
        AssistantUtility.ClearAllCache();
        _dbContext.Dispose();
    }

    [TestMethod]
    public async Task SendMessageStream_ConversationWithNoBoundary_DoesNotOfferRecall()
    {
        await SeedConversationAsync(boundary: null);

        await RunStreamAsync();

        AdvertisedToolNames().Should().NotContain("conversation_recall");
    }

    [TestMethod]
    public async Task SendMessageStream_CompactedConversation_OffersRecall()
    {
        await SeedConversationAsync(boundary: 2);

        await RunStreamAsync();

        AdvertisedToolNames().Should().Contain("conversation_recall",
            "a compacted conversation must be able to reach what the summary replaced");
    }

    private IEnumerable<string> AdvertisedToolNames() =>
        _capturedRequests
            .SelectMany(r => r.Tools ?? [])
            .Select(t => t.Function?.Name ?? string.Empty);

    private async Task SeedConversationAsync(int? boundary)
    {
        _dbContext.Users.Add(new User { Id = _userId, Email = "t@example.com", Name = "T" });
        _dbContext.Projects.Add(new Project { Id = _projectId, Name = "P" });
        _dbContext.Notebooks.Add(new Notebook { Id = _notebookId, ProjectId = _projectId, Name = "N" });
        _dbContext.NotebookConversations.Add(new NotebookConversation
        {
            Id = _conversationId,
            NotebookId = _notebookId,
            Title = "C",
            CompactionBoundaryTurnIndex = boundary
        });
        await _dbContext.SaveChangesAsync();
    }

    private async Task RunStreamAsync()
    {
        var service = CreateFixture();
        await foreach (var _ in service.SendMessageStreamToConversationAsUserAsync(
            _conversationId,
            new SendMessageRequest { Instructions = "Hi", AssistantName = AssistantName },
            _userId))
        {
            // drain
        }
    }

    private ConversationService CreateFixture()
    {
        var scopeFactory = new TestServiceScopeFactory(_dbContext);

        var lockMock = new Mock<IDistributedConversationLock>();
        lockMock.Setup(l => l.TryAcquireLockAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, string userName, CancellationToken _) =>
                LockAcquisitionResult.Acquired(new ConversationLock
                {
                    ConversationId = id,
                    LockedByUserName = userName,
                    LockedAt = DateTime.UtcNow,
                    ExpiresAt = DateTime.UtcNow.AddMinutes(5)
                }));
        lockMock.Setup(l => l.RenewLockAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        lockMock.Setup(l => l.ReleaseLockAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var completedResponse = new ChatCompletionResponse(
            new[] { new ChatChoice(new ChatMessage(ChatRole.Assistant, "Hello from the mock model."), "stop") },
            null);

        var chatClient = new Mock<IChatCompletionClient>();
        chatClient.SetupGet(c => c.SupportsToolChoiceNone).Returns(true);
        chatClient.Setup(c => c.GetCompletionAsync(It.IsAny<ChatCompletionRequest>(), It.IsAny<CancellationToken>()))
            .Callback((ChatCompletionRequest r, CancellationToken _) => _capturedRequests.Add(r))
            .ReturnsAsync(completedResponse);
        chatClient.Setup(c => c.StreamCompletionAsync(
                It.IsAny<ChatCompletionRequest>(), It.IsAny<Action<ChatCompletionChunk>>(), It.IsAny<CancellationToken>()))
            .Callback((ChatCompletionRequest r, Action<ChatCompletionChunk> _, CancellationToken __) => _capturedRequests.Add(r))
            .ReturnsAsync(completedResponse);

        var chatClientFactory = new Mock<IChatCompletionClientFactory>();
        chatClientFactory.Setup(f => f.CreateClient(It.IsAny<string>(), It.IsAny<HttpClient>()))
            .Returns(chatClient.Object);

        var chatModelResolver = new Mock<IChatModelResolver>();
        chatModelResolver.Setup(r => r.Resolve(It.IsAny<string?>()))
            .Returns((string? id) => new ResolvedChatModel(
                string.IsNullOrWhiteSpace(id) ? "gpt-4o-mini" : id!,
                ChatModelReferenceKind.Direct,
                new ResolvedExecutionPolicy(
                    string.IsNullOrWhiteSpace(id) ? "gpt-4o-mini" : id!,
                    "openai-chat",
                    ParameterAuthority.AssistantDefinition,
                    new Dictionary<string, System.Text.Json.JsonElement>())));

        var contextOptions = new Mock<IContextOptionsService>();
        contextOptions.Setup(m => m.BuildContextMessageAsync(
                It.IsAny<AssistantDefinition>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        var configuration = new Mock<IConfiguration>();
        configuration.Setup(x => x["FileStorage:Path"]).Returns("/tmp/recall-wiring-test-storage");

        var (queryService, commandService, historyBuilder, attachmentService) = ConversationTestServices.Create(
            scopeFactory, contextOptions.Object, Mock.Of<IMarkdownExtractionService>(), configuration.Object);
        var (persistence, usageReporter) = ConversationTestServices.CreatePersistence(scopeFactory);

        return ConversationTestServices.CreateConversationService(
            scopeFactory,
            chatModelResolver.Object,
            queryService,
            commandService,
            historyBuilder,
            attachmentService,
            persistence,
            usageReporter,
            chatClientFactory.Object,
            lockMock.Object);
    }

    private static void SeedAssistantDefinitionCache(string assistantName, AssistantDefinition definition)
    {
        var cacheType = typeof(AssistantUtility).GetNestedType("CachedAssistant", BindingFlags.NonPublic)!;
        var entry = Activator.CreateInstance(cacheType, definition)!;
        var cache = (IDictionary)typeof(AssistantUtility)
            .GetField("AssistantDefinitionCache", BindingFlags.Static | BindingFlags.NonPublic)!
            .GetValue(null)!;
        cache[assistantName] = entry;
    }
}
```

> **Note for the implementer:** the fixture is copied from `ConversationServicePreflightTests.CreateFixture` (`ConversationServicePreflightTests.cs:83-178`) with one change — the chat client mock now records every `ChatCompletionRequest`. If that file's seeding helper or `ConversationTestServices` signature has drifted, copy the current version rather than this snapshot; the two assertions are what matter.

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test GuideAntsApi.Tests/GuideAntsApi.Tests.csproj --filter "FullyQualifiedName~ConversationRecallWiringTests"` from `src/server`
Expected: `SendMessageStream_CompactedConversation_OffersRecall` FAILS (no tool named `conversation_recall` is advertised); `SendMessageStream_ConversationWithNoBoundary_DoesNotOfferRecall` passes vacuously.

- [ ] **Step 3: Wire the flag**

In `src/server/GuideAntsApi/Services/Conversations/ConversationService.cs`, in `BuildRunContext`'s `ChatOptions` initializer:

```csharp
            ChatOptions = new ChatRunOptions
            {
                AssistantName = ctx.AssistantName,
                DeploymentId = ctx.ModelDeploymentId,
                Instructions = ctx.Request.Instructions,
                oAuthUserAccessToken = ctx.ExternalAuthTokens.FirstOrDefault().Value,
                ExternalAuthTokens = ctx.ExternalAuthTokens,
                ClientToolDefinitions = ctx.Request.ClientToolDefinitions,
                ExecutionPolicy = ctx.ExecutionPolicy,
                // D7: recall exists only for a conversation the user actually compacted. A
                // conversation with no boundary has nothing to recall and sees no new tool.
                EnableConversationRecall = ctx.Conversation.CompactionBoundaryTurnIndex.HasValue
            },
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test GuideAntsApi.Tests/GuideAntsApi.Tests.csproj --filter "FullyQualifiedName~ConversationRecallWiringTests"` from `src/server`
Expected: PASS, both tests.

- [ ] **Step 5: Commit (ask the user first)**

```bash
git add src/server/GuideAntsApi/Services/Conversations/ConversationService.cs \
        src/server/GuideAntsApi.Tests/Services/Conversations/ConversationRecallWiringTests.cs
git commit -m "feat(compaction): offer conversation_recall only to compacted conversations

Sets ChatRunOptions.EnableConversationRecall from the conversation's
CompactionBoundaryTurnIndex at the single private-path call site. Published,
sandbox-wire and agent runs keep the false default, so v1 stays notebook-only.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

## Task 5: Rollup verification and checklist update

**Files:**
- Modify: `docs/context-compaction-plan.md` (tick W7 7.1–7.7, add the implementation-plan link and a notes paragraph in the style of W3–W6)

**Interfaces:**
- Consumes: everything above.
- Produces: nothing code-facing.

- [ ] **Step 1: Run the whole server unit suite**

Run: `dotnet test GuideAntsApi.Tests/GuideAntsApi.Tests.csproj` from `src/server`
Expected: PASS. Record the pass/fail counts.

- [ ] **Step 2: Confirm no pre-existing failure is being blamed on this plan**

If anything fails, check it against `main` before attributing it to W7:

```bash
git stash && git checkout main -- . 2>/dev/null; dotnet test GuideAntsApi.Tests/GuideAntsApi.Tests.csproj --filter "FullyQualifiedName~<FailingClass>"
```

Restore the branch afterwards (`git checkout feature/compaction -- .` then `git stash pop`). Note any pre-existing failure verbatim in the plan notes; do not fix out-of-scope failures here.

- [ ] **Step 3: Confirm the tool is discoverable end to end**

Run: `dotnet build GuideAntsApi.sln` from `src/server`
Then, from `src/server`, run the two registry tests that prove discovery works against the real assembly:

Run: `dotnet test GuideAntsApi.Tests/GuideAntsApi.Tests.csproj --filter "FullyQualifiedName~ConversationRecallToolsTests.ToolContract_IsRegisteredAsRequiringNotebookContext|FullyQualifiedName~ConversationRecallToolsTests.GeneratedSchema_ExposesOnlyQueryAndPage_AndNeverAConversationId"`
Expected: PASS.

- [ ] **Step 4: Update the checklist**

In `docs/context-compaction-plan.md`, replace the W7 section header block with:

```markdown
## W7 — Recall tool

**Implementation plan written:** [`docs/superpowers/plans/2026-09-24-w7-recall-tool.md`](superpowers/plans/2026-09-24-w7-recall-tool.md) — 5 tasks. Two corrections to the checklist and spec fell out of the research: (1) **7.3's search space is `TurnIndex <= boundary`, not `<`** — the shipped `ConversationHistoryBuilder` summarizes `<= boundary` and keeps `> boundary` verbatim, so a `<` recall would leave the boundary turn reachable by neither the tail nor recall; (2) **7.5 could not use the `AssistantUtility` injection point** that `skills_list`/`skills_read` use, because that cache is keyed by assistant name and shared process-wide, and "has a compaction boundary" is a property of the conversation. Exposure travels instead on a new run-scoped `ChatRunOptions.EnableConversationRecall`, set at the one private-path call site in `ConversationService.BuildRunContext`. The plan also adds a task beyond the 7 items: registering the dispatch-side request builder unconditionally in `EnsureRequestBuilderCache`, since a run-scoped tool is invisible to that method's `assistantOperationIds` gate and a missing builder drops the tool result silently.

- [x] 7.1 `conversation_recall(query, page?)` as a static `[Tool]` method following `MemoryTools`
- [x] 7.2 Scope via `[Parameter(Hidden = true)] InvocationContext? context` — `ConversationId`
      is framework-injected, never model-supplied
- [x] 7.3 Search space: this conversation, `TurnIndex <= CompactionBoundaryTurnIndex`
- [x] 7.4 In-memory relevance ranking. No SQL Server full-text dependency, so behavior is
      identical on every deployment
- [x] 7.5 Attach the tool only when a boundary exists
- [x] 7.6 Paginated, structure-preserving result formatting
- [x] 7.7 Tests: ranking, pagination, and a cross-conversation read that must fail
```

Also correct the spec's `<` in `docs/superpowers/specs/2026-09-21-context-compaction-design.md` under *Recall / Search space*:

```markdown
**Search space.** Messages in this conversation with `TurnIndex <= CompactionBoundaryTurnIndex` —
exactly the set `ConversationHistoryBuilder` replaces with the generated summary. (An earlier draft
said `<`, which would have left the boundary turn itself in neither the verbatim tail nor recall.)
```

- [ ] **Step 5: Commit (ask the user first)**

```bash
git add docs/context-compaction-plan.md \
        docs/superpowers/specs/2026-09-21-context-compaction-design.md \
        docs/superpowers/plans/2026-09-24-w7-recall-tool.md
git commit -m "docs: tick W7 and correct the recall search-space boundary

The spec said TurnIndex < boundary; the shipped history builder summarizes
<= boundary. Recall must cover exactly what the summary replaced, so the
spec line is corrected rather than the code.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```
