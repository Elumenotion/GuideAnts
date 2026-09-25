# W4: Boundary and the Compact Endpoint Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a user press "compact" and get a durable, monotonic `CompactionBoundaryTurnIndex` on their conversation, via `POST .../conversations/{convoId}/compact`, which returns `{ boundaryTurnIndex, messagesSummarized, estimatedTokensBefore, estimatedTokensAfter }` and requires the conversation to be idle.

**Architecture:** One new nullable column on `NotebookConversation`. One new service (`CompactionService`, in `Services/Conversations/Commands/` alongside `ConversationUndoService`, its closest precedent) that acquires the same distributed conversation lock the stream engine uses, computes the new boundary as the highest `TurnIndex` among `"completed"` turns, writes it monotonically, then calls W3's `CompactionEngine.Compact` once (for immediate user feedback — the summary text itself is discarded here and recomputed later at history-build time per D3; only the token/message counts from this call are returned). One new endpoint, following the extract-to-named-static-method pattern `NotebookConversationsEndpoints.GetConversationAsync` already established (not the older inline-lambda-with-try/catch style the DELETE undo endpoints use), so it's directly unit-testable without a `WebApplicationFactory`.

**Tech Stack:** ASP.NET Core 8, EF Core 8 (EF InMemory in tests), MSTest + FluentAssertions + Moq, `AntRunner.Chat`/`AntRunner.Chat.Compaction` (W3, already merged on this branch).

**Spec:** [`docs/superpowers/specs/2026-09-21-context-compaction-design.md`](../specs/2026-09-21-context-compaction-design.md) — *"The boundary"*, *API surface → Compact*. Checklist: [`docs/context-compaction-plan.md`](../../context-compaction-plan.md) W4 (4.1–4.8).

## Global Constraints

- **The compact endpoint requires an idle conversation (D8).** Acquire the same `IDistributedConversationLock` the stream engine uses before writing the boundary; release it in a `finally`. Never use `ConversationStreamRunRegistry.IsAnyActiveForConversation` for this — it's per-process/in-memory and only closes the race in one direction; the distributed lock is authoritative across API instances and closes it in both.
- **Monotonic.** `CompactionBoundaryTurnIndex` only ever increases. A press that would compute a boundary at or behind the current one is a no-op write (the column is left untouched, though the response's numbers are still recomputed fresh).
- **Idempotent.** Pressing compact again with no new complete turn since the last press returns the existing boundary, not an error.
- **No complete turn at all → no-op**, per the spec's Error handling table: returns the existing boundary (possibly `null`), not an exception.
- **Never split a turn.** A turn is only eligible to be (or precede) the boundary once its `ConversationTurn.Status == "completed"`. A turn with `Status` `"pending_client_tool"` or `"streaming"` has an assistant `tool_calls` message with no paired tool-result yet (or is mid-stream) and must never be selected — this is the exact invariant commit `91398ae3` fixed for a related bug (see Task 2's Findings).
- **Route and auth follow this file's existing convention exactly**, not the spec's shorthand path. Every route in `NotebookConversationsEndpoints.cs` is nested under `/api/projects/{projectId:guid}/notebooks/{notebookId:guid}/conversations`, and the per-conversation route parameter is named `convoId`, not `conversationId`. State-mutating endpoints in this file use `RequireAuthorization("RequireContributor")` (the group's own baseline is the weaker `RequireApprovedUser`). The spec's literal `POST /api/notebooks/{notebookId}/conversations/{conversationId}/compact` is shorthand, not the literal route to implement.
- **`CompactionEngine.Compact` is called here for its return values only, not for the persisted summary.** Per D3, the actual `[system: summary]` message is recomputed at history-build time (W5) from the persisted boundary, not stored from this call. This endpoint runs the engine once purely to give the user accurate before/after numbers at press time; W5 will call it again later and — because the engine is pure and deterministic — get byte-identical `SummaryText` for the same boundary.
- **Server tests: MSTest (`[TestClass]`/`[TestMethod]`) + FluentAssertions + Moq**; EF InMemory for anything touching `ApplicationDbContext`, following `ConversationContextStatusServiceTests.cs`'s establishzed pattern (Task 2 tells you exactly where to look).
- Never co-author commits, and only commit when the user has said to (per `CLAUDE.md` and memory `no-commits-without-permission`). The commit steps below are proposed checkpoints — ask first.

## Findings that shaped this plan

- **`ConversationTurn.Status` already encodes the tool_calls/tool_result-pairing invariant** — there is no need to scan `NotebookConversationMessage.ToolCalls`/`ToolCallId` directly. A turn stays `"streaming"` or transitions to `"pending_client_tool"` until its tool calls are resolved, and only becomes `"completed"` once they are (`ConversationTurnTerminalizer.cs:100-101`). So "last complete turn" reduces to a single query: the highest `TurnIndex` among turns with `Status == "completed"` — not necessarily the highest `TurnIndex` overall, since a later turn can be interrupted/failed while an earlier one completed.
- **Commit `91398ae3`** ("fixes pending_client_tool not being accepted on pending tool call runs") fixed `WireToolResultContinuation.cs` to recognize `"pending_client_tool"` (not just legacy `"streaming"`) as "this turn is paused awaiting a client tool." It added `IsAwaitingToolResults(status)` checking both strings. This confirms `"pending_client_tool"` — not just `"streaming"` — must be excluded from "complete," which is already naturally true since neither string equals `"completed"`.
- **`IDistributedConversationLock` is not acquired by `ConversationStreamEngine` directly** — acquisition for streams goes through `ConversationStreamLockCoordinator`. The precedent for a *non-streaming write endpoint* acquiring the same lock directly is `ConversationUndoService` (`Services/Conversations/Commands/ConversationUndoService.cs`), which this plan's `CompactionService` is modeled on — but simplified: undo also coordinates a local `PrivateConversationStreamPolicy` semaphore gate and a 4-attempt "release until confirmed" retry loop, both of which exist because undo races an in-process stream *worker*, not just the DB lock. Compaction has no in-process worker to coordinate with, so a plain `try { ... } finally { await _distributedLock.ReleaseLockAsync(...); }` — logging a warning on failure rather than retrying — is sufficient; an unreleased lease still degrades safely to "wait out the 5-minute TTL," never a stuck lock.
- **The exact 409 shape to reuse** is already established: a service throws `InvalidOperationException("Conversation is locked by {name}")`, and the endpoint returns `Results.Conflict(new { error = ex.Message })`. Precedent: `NotebookConversationsEndpoints.cs`'s DELETE-undo routes (`:326-331`, `:351-356`) and proven end-to-end by `NotebookConversationStreamingEndpointsTests.cs`'s `SendMessage_Stream_WhenConversationIsLocked_Returns409Conflict`.
- **W2 already stubbed the DTO field W4 must fill in.** `ConversationContextStatusDto.BoundaryTurnIndex` is hardcoded `null` at both return sites in `ConversationContextStatusService.GetAsync` (`:54`, `:130`), with a doc comment saying "always null until the compaction boundary column exists (W4)." This isn't one of the checklist's 4.1–4.8 items, but leaving it stubbed after this workstream ships the column would mean the context meter (W8) can never show "compacted" state even once real data exists — so this plan adds it as Task 5, matching how W1's plan added an unlisted backfill task (1.4) the checklist didn't enumerate.
- **The newer, more testable endpoint pattern in this file is `GetConversationAsync`-style**, not the older inline-lambda DELETE-undo style: `internal static async Task<IResult> GetConversationAsync(...)` is a named method the `MapGet` lambda calls, and `NotebookConversationContextStatusEndpointTests.cs` unit-tests it directly (`DefaultHttpContext` + a bare `ServiceCollection`, no `WebApplicationFactory`, no Docker). Task 4 follows this newer pattern.
- **`PromptTokenEstimator` lives in `namespace AntRunner.Chat`** (confirmed directly: `AntRunner.Chat/AntRunner.Chat/PromptTokenEstimator.cs:3`), pulled in via a plain `using AntRunner.Chat;` — `ConversationContextStatusService.cs:2` already does exactly this. (An earlier research pass misattributed it to `GuideAntsApi.Services.LlamaCpp`, which is wrong — that using is for the local-runtime router config service, unrelated.)
- **`FilesCreated`/`FilesModified` JSON deserialization already has an established pattern** — `ConversationQueryService.cs:131-155` deserializes with `JsonSerializer.Deserialize<List<string>>(turn.FilesCreated, JsonOptions)` (`JsonOptions` = `{ PropertyNamingPolicy = JsonNamingPolicy.CamelCase }`) wrapped in try/catch, treating a parse failure or `null`/empty string as "no files." `CompactionService` reuses this exact shape to build W3's `TurnFacts`.
- **`ConversationMessageMapper.ToChatMessage(NotebookConversationMessage m) -> ChatMessage`** (`Services/Conversations/Mapping/ConversationMessageMapper.cs:157`) is the single-message converter; `ConversationMessageMapper.FilterDuplicateAssistantMessages(ICollection<NotebookConversationMessage>) -> List<NotebookConversationMessage>` (`:57`) is the dedup step `ConversationHistoryBuilder.BuildOpenAiMessagesAsync` runs before mapping. `CompactionService` uses both, in that order, mirroring what the history builder does (minus attachment expansion, which doesn't affect token-count estimates enough to justify the extra complexity here).

---

## File Structure

**Created:**
- `src/server/GuideAntsApi.DataModel/Migrations/{timestamp}_AddCompactionBoundaryTurnIndex.cs` (+ `.Designer.cs`, generated by `dotnet ef migrations add`)
- `src/server/GuideAntsApi/Services/Conversations/Commands/CompactionService.cs` — `ICompactionService`, `CompactionOutcome`, `CompactionService` (interface + record + class in one file, matching `ConversationUndoService.cs`'s precedent)
- `src/server/GuideAntsApi/Models/Conversations/CompactionResultDto.cs`
- `src/server/GuideAntsApi.Tests/Services/Conversations/CompactionServiceTests.cs`
- `src/server/GuideAntsApi.Tests/Endpoints/NotebookConversationCompactEndpointTests.cs`

**Modified:**
- `src/server/GuideAntsApi.DataModel/Models/NotebookConversation.cs` — add `CompactionBoundaryTurnIndex` (`int?`)
- `src/server/GuideAntsApi/Endpoints/NotebookConversationsEndpoints.cs` — add `CompactConversationAsync` handler + `POST /{convoId:guid}/compact` route
- `src/server/GuideAntsApi/Configuration/StartupConfiguration.cs` — register `ICompactionService`
- `src/server/GuideAntsApi/Services/Conversations/ConversationContextStatusService.cs` — populate `BoundaryTurnIndex` instead of hardcoding `null`
- `src/server/GuideAntsApi.Tests/Services/Conversations/ConversationContextStatusServiceTests.cs` — one new test for the above

---

## Task 1: `CompactionBoundaryTurnIndex` column + migration (4.1, 4.2)

**Files:**
- Modify: `src/server/GuideAntsApi.DataModel/Models/NotebookConversation.cs`
- Create: `src/server/GuideAntsApi.DataModel/Migrations/{timestamp}_AddCompactionBoundaryTurnIndex.cs` (via `dotnet ef migrations add`, not hand-written)
- Test: `src/server/GuideAntsApi.Tests/DataModel/NotebookConversationCompactionBoundaryTests.cs`

**Interfaces:**
- Produces: `NotebookConversation.CompactionBoundaryTurnIndex` (`int?`, nullable, no `[Required]`) — consumed by every later task in this plan.

- [ ] **Step 1: Write the failing test**

```csharp
using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.Tests.DataModel;

[TestClass]
public sealed class NotebookConversationCompactionBoundaryTests
{
    [TestMethod]
    public async Task CompactionBoundaryTurnIndex_DefaultsToNull_AndRoundTrips()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var notebookId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();

        using (var db = new ApplicationDbContext(options))
        {
            db.NotebookConversations.Add(new NotebookConversation
            {
                Id = conversationId,
                NotebookId = notebookId,
                Title = "t"
            });
            await db.SaveChangesAsync();
        }

        using (var db = new ApplicationDbContext(options))
        {
            var conv = await db.NotebookConversations.FirstAsync(c => c.Id == conversationId);
            conv.CompactionBoundaryTurnIndex.Should().BeNull();

            conv.CompactionBoundaryTurnIndex = 7;
            await db.SaveChangesAsync();
        }

        using (var db = new ApplicationDbContext(options))
        {
            var conv = await db.NotebookConversations.FirstAsync(c => c.Id == conversationId);
            conv.CompactionBoundaryTurnIndex.Should().Be(7);
        }
    }
}
```

Before writing, confirm `ApplicationDbContext`'s constructor and `NotebookConversation`'s required fields by reading `src/server/GuideAntsApi.DataModel/Models/NotebookConversation.cs` and `src/server/GuideAntsApi.DataModel/ApplicationDbContext.cs` — `Id`/`NotebookId`/`Title` are the only required-looking fields today; `Notebook` nav, `Messages`, `Turns`, `Created` all have defaults.

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test src/server/GuideAntsApi.Tests/GuideAntsApi.Tests.csproj --filter "FullyQualifiedName~NotebookConversationCompactionBoundaryTests"`
Expected: FAIL to compile — no `CompactionBoundaryTurnIndex` property.

- [ ] **Step 3: Add the property**

In `NotebookConversation.cs`, add after `Created`:

```csharp
    /// <summary>
    /// Turn index at which the user last compacted. Messages with a lower TurnIndex are
    /// replaced by a generated summary at history-build time. Null = never compacted.
    /// Monotonic: this value only ever increases (see CompactionService).
    /// </summary>
    public int? CompactionBoundaryTurnIndex { get; set; }
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test src/server/GuideAntsApi.Tests/GuideAntsApi.Tests.csproj --filter "FullyQualifiedName~NotebookConversationCompactionBoundaryTests"`
Expected: PASS. (EF InMemory doesn't require a real migration to exercise the new property — this proves the model change itself is correct before you touch the database provider.)

- [ ] **Step 5: Generate and apply the EF migration**

Run from `src/server`:

```bash
dotnet ef migrations add AddCompactionBoundaryTurnIndex --project GuideAntsApi.DataModel/GuideAntsApi.DataModel.csproj --startup-project GuideAntsApi/GuideAntsApi.csproj
```

Open the generated migration and confirm it matches this shape (precedent: `20260921204053_AddModelContextWindow.cs`, a recent single-nullable-int-column migration):

```csharp
protected override void Up(MigrationBuilder migrationBuilder)
{
    migrationBuilder.AddColumn<int>(
        name: "CompactionBoundaryTurnIndex",
        table: "NotebookConversations",
        type: "int",
        nullable: true);
}

protected override void Down(MigrationBuilder migrationBuilder)
{
    migrationBuilder.DropColumn(
        name: "CompactionBoundaryTurnIndex",
        table: "NotebookConversations");
}
```

If the generated migration differs (e.g. EF also touches an unrelated pending model change), stop and report it — don't hand-edit `*.Designer.cs` or `ApplicationDbContextModelSnapshot.cs`; those are regenerated by the tool.

Do **not** run `dotnet ef database update` against a real database as part of this task — that requires a live SQL Server connection this plan doesn't assume is available. Confirm the migration builds:

Run: `dotnet build src/server/GuideAntsApi.DataModel/GuideAntsApi.DataModel.csproj`
Expected: builds clean, migration file compiles.

- [ ] **Step 6: Commit (ask first)**

```bash
git add src/server/GuideAntsApi.DataModel/Models/NotebookConversation.cs src/server/GuideAntsApi.DataModel/Migrations src/server/GuideAntsApi.Tests/DataModel/NotebookConversationCompactionBoundaryTests.cs
git commit -m "Adds CompactionBoundaryTurnIndex to NotebookConversation"
```

---

## Task 2: `CompactionService` — boundary selection, monotonicity, idempotency, lock/409 (4.3, 4.5, 4.6, 4.7)

**Files:**
- Create: `src/server/GuideAntsApi/Services/Conversations/Commands/CompactionService.cs`
- Test: `src/server/GuideAntsApi.Tests/Services/Conversations/CompactionServiceTests.cs`

**Interfaces:**
- Consumes: `IDistributedConversationLock` (`TryAcquireLockAsync(Guid, string, CancellationToken) -> Task<LockAcquisitionResult>`, `ReleaseLockAsync(Guid, Guid, CancellationToken) -> Task<bool>`), `LockAcquisitionResult` (`Status`, `Lock?.LeaseId`, `LockedByUserName`), `LockAcquisitionStatus` enum (`Acquired`, `AlreadyLocked`, `ConversationNotFound`, `RaceCondition`) — all in `GuideAntsApi.Services.Conversations`. `ApplicationDbContext.NotebookConversations`, `NotebookConversation.Turns`/`CompactionBoundaryTurnIndex` (Task 1). `ConversationTurn.TurnIndex`/`Status`.
- Produces:

```csharp
public sealed record CompactionOutcome(
    int? BoundaryTurnIndex,
    int MessagesSummarized,
    int? EstimatedTokensBefore,
    int? EstimatedTokensAfter);

public interface ICompactionService
{
    Task<CompactionOutcome> CompactConversationAsync(Guid conversationId, CancellationToken ct = default);
}
```

This task's `CompactConversationAsync` always returns `MessagesSummarized: 0, EstimatedTokensBefore: null, EstimatedTokensAfter: null` — Task 3 replaces those three literal values with the real computation. The boundary/lock/idempotency/monotonicity behavior built here does not change in Task 3.

- [ ] **Step 1: Write the failing tests**

Before writing, read `src/server/GuideAntsApi.Tests/Services/Conversations/ConversationContextStatusServiceTests.cs` (W2) for the established EF-InMemory-plus-`IServiceScopeFactory` test setup this codebase already uses for a scoped-DbContext service, and `src/server/GuideAntsApi.Tests/Services/Conversations/ConversationUndoServiceStreamGuardTests.cs:120-172` for the `Mock<IDistributedConversationLock>` pattern for a 409-producing lock test. Confirm `ConversationLock`'s full field list (`src/server/GuideAntsApi.DataModel/Models/ConversationLock.cs`) before constructing one — `ConversationId`, `LeaseId` (`Guid`), and `LockedByUserName` (`[Required] string`) are confirmed; there may be additional required timestamp fields (`LockedAt`/`ExpiresAt`) to set.

```csharp
using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services.Conversations;
using GuideAntsApi.Services.Conversations.Commands;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace GuideAntsApi.Tests.Services.Conversations;

[TestClass]
public sealed class CompactionServiceTests
{
    private static IServiceScopeFactory BuildScopeFactory(string dbName)
    {
        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(o => o.UseInMemoryDatabase(dbName));
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private static ConversationTurn Turn(Guid conversationId, int index, string status) => new()
    {
        NotebookConversationId = conversationId,
        TurnIndex = index,
        AssistantName = "a",
        Instructions = "i",
        Status = status
    };

    private static Mock<IDistributedConversationLock> AcquiringLock(Guid conversationId)
    {
        var mock = new Mock<IDistributedConversationLock>(MockBehavior.Strict);
        mock.Setup(l => l.TryAcquireLockAsync(conversationId, "User", It.IsAny<CancellationToken>()))
            .ReturnsAsync(LockAcquisitionResult.Acquired(new ConversationLock
            {
                ConversationId = conversationId,
                LeaseId = Guid.NewGuid(),
                LockedByUserName = "tester"
            }));
        mock.Setup(l => l.ReleaseLockAsync(conversationId, It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        return mock;
    }

    [TestMethod]
    public async Task CompactConversationAsync_NoCompletedTurns_ReturnsExistingBoundaryUnchanged()
    {
        var conversationId = Guid.NewGuid();
        var scopeFactory = BuildScopeFactory(nameof(CompactConversationAsync_NoCompletedTurns_ReturnsExistingBoundaryUnchanged));

        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.NotebookConversations.Add(new NotebookConversation { Id = conversationId, NotebookId = Guid.NewGuid(), Title = "t" });
            db.ConversationTurns.Add(Turn(conversationId, 1, "streaming"));
            await db.SaveChangesAsync();
        }

        var distributedLock = AcquiringLock(conversationId);
        var service = new CompactionService(distributedLock.Object, scopeFactory, NullLogger<CompactionService>.Instance);

        var result = await service.CompactConversationAsync(conversationId);

        result.BoundaryTurnIndex.Should().BeNull();
        result.MessagesSummarized.Should().Be(0);
    }

    [TestMethod]
    public async Task CompactConversationAsync_OneCompletedTurn_SetsBoundaryToItsIndex()
    {
        var conversationId = Guid.NewGuid();
        var scopeFactory = BuildScopeFactory(nameof(CompactConversationAsync_OneCompletedTurn_SetsBoundaryToItsIndex));

        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.NotebookConversations.Add(new NotebookConversation { Id = conversationId, NotebookId = Guid.NewGuid(), Title = "t" });
            db.ConversationTurns.Add(Turn(conversationId, 1, "completed"));
            db.ConversationTurns.Add(Turn(conversationId, 2, "pending_client_tool"));
            await db.SaveChangesAsync();
        }

        var distributedLock = AcquiringLock(conversationId);
        var service = new CompactionService(distributedLock.Object, scopeFactory, NullLogger<CompactionService>.Instance);

        var result = await service.CompactConversationAsync(conversationId);

        // Turn 2 is pending_client_tool - never eligible, even though it has a higher index.
        result.BoundaryTurnIndex.Should().Be(1);
    }

    [TestMethod]
    public async Task CompactConversationAsync_PressedAgainWithNoNewCompletedTurn_IsIdempotent()
    {
        var conversationId = Guid.NewGuid();
        var scopeFactory = BuildScopeFactory(nameof(CompactConversationAsync_PressedAgainWithNoNewCompletedTurn_IsIdempotent));

        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.NotebookConversations.Add(new NotebookConversation
            {
                Id = conversationId, NotebookId = Guid.NewGuid(), Title = "t", CompactionBoundaryTurnIndex = 1
            });
            db.ConversationTurns.Add(Turn(conversationId, 1, "completed"));
            await db.SaveChangesAsync();
        }

        var distributedLock = AcquiringLock(conversationId);
        var service = new CompactionService(distributedLock.Object, scopeFactory, NullLogger<CompactionService>.Instance);

        var result = await service.CompactConversationAsync(conversationId);

        result.BoundaryTurnIndex.Should().Be(1);
    }

    [TestMethod]
    public async Task CompactConversationAsync_NeverMovesTheBoundaryBackward()
    {
        var conversationId = Guid.NewGuid();
        var scopeFactory = BuildScopeFactory(nameof(CompactConversationAsync_NeverMovesTheBoundaryBackward));

        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            // Existing boundary (5) is ahead of what "last complete turn" would compute today (2) -
            // a contrived setup that proves the monotonic guard, not just realistic data.
            db.NotebookConversations.Add(new NotebookConversation
            {
                Id = conversationId, NotebookId = Guid.NewGuid(), Title = "t", CompactionBoundaryTurnIndex = 5
            });
            db.ConversationTurns.Add(Turn(conversationId, 2, "completed"));
            await db.SaveChangesAsync();
        }

        var distributedLock = AcquiringLock(conversationId);
        var service = new CompactionService(distributedLock.Object, scopeFactory, NullLogger<CompactionService>.Instance);

        var result = await service.CompactConversationAsync(conversationId);

        result.BoundaryTurnIndex.Should().Be(5);
    }

    [TestMethod]
    public async Task CompactConversationAsync_ConversationLocked_ThrowsNamingTheHolder()
    {
        var conversationId = Guid.NewGuid();
        var scopeFactory = BuildScopeFactory(nameof(CompactConversationAsync_ConversationLocked_ThrowsNamingTheHolder));

        var distributedLock = new Mock<IDistributedConversationLock>(MockBehavior.Strict);
        distributedLock
            .Setup(l => l.TryAcquireLockAsync(conversationId, "User", It.IsAny<CancellationToken>()))
            .ReturnsAsync(LockAcquisitionResult.AlreadyLocked("remote-worker"));

        var service = new CompactionService(distributedLock.Object, scopeFactory, NullLogger<CompactionService>.Instance);

        var act = () => service.CompactConversationAsync(conversationId);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*locked by remote-worker*");
    }

    [TestMethod]
    public async Task CompactConversationAsync_ConversationNotFound_ThrowsKeyNotFound()
    {
        var conversationId = Guid.NewGuid();
        var scopeFactory = BuildScopeFactory(nameof(CompactConversationAsync_ConversationNotFound_ThrowsKeyNotFound));

        var distributedLock = new Mock<IDistributedConversationLock>(MockBehavior.Strict);
        distributedLock
            .Setup(l => l.TryAcquireLockAsync(conversationId, "User", It.IsAny<CancellationToken>()))
            .ReturnsAsync(LockAcquisitionResult.NotFound());

        var service = new CompactionService(distributedLock.Object, scopeFactory, NullLogger<CompactionService>.Instance);

        var act = () => service.CompactConversationAsync(conversationId);

        await act.Should().ThrowAsync<KeyNotFoundException>();
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test src/server/GuideAntsApi.Tests/GuideAntsApi.Tests.csproj --filter "FullyQualifiedName~CompactionServiceTests"`
Expected: FAIL to compile — `CompactionService`/`ICompactionService`/`CompactionOutcome` do not exist.

- [ ] **Step 3: Implement**

```csharp
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.Services.Conversations.Commands;

public sealed record CompactionOutcome(
    int? BoundaryTurnIndex,
    int MessagesSummarized,
    int? EstimatedTokensBefore,
    int? EstimatedTokensAfter);

public interface ICompactionService
{
    Task<CompactionOutcome> CompactConversationAsync(Guid conversationId, CancellationToken ct = default);
}

public sealed class CompactionService : ICompactionService
{
    private readonly IDistributedConversationLock _distributedLock;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<CompactionService> _logger;

    public CompactionService(
        IDistributedConversationLock distributedLock,
        IServiceScopeFactory scopeFactory,
        ILogger<CompactionService> logger)
    {
        _distributedLock = distributedLock;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<CompactionOutcome> CompactConversationAsync(Guid conversationId, CancellationToken ct = default)
    {
        var lockResult = await _distributedLock.TryAcquireLockAsync(conversationId, "User", ct);

        if (lockResult.Status == LockAcquisitionStatus.ConversationNotFound)
        {
            throw new KeyNotFoundException("Conversation not found");
        }

        if (lockResult.Status != LockAcquisitionStatus.Acquired)
        {
            // A lock not visible in this process may belong to a worker on another API instance -
            // never infer availability from anything but the distributed lock itself (D8).
            throw new InvalidOperationException(
                $"Conversation is locked by {lockResult.LockedByUserName ?? "another user"}");
        }

        var acquiredLock = lockResult.Lock
            ?? throw new InvalidOperationException("Distributed lock acquisition returned no lease.");

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var conv = await db.NotebookConversations
                .Include(c => c.Turns)
                .FirstOrDefaultAsync(c => c.Id == conversationId, ct);

            if (conv == null)
            {
                throw new KeyNotFoundException("Conversation not found");
            }

            var lastCompleteTurnIndex = conv.Turns
                .Where(t => t.Status == "completed")
                .Select(t => (int?)t.TurnIndex)
                .DefaultIfEmpty()
                .Max();

            if (lastCompleteTurnIndex == null)
            {
                // Nothing eligible to compact yet. No-op: return the existing boundary unchanged.
                return new CompactionOutcome(conv.CompactionBoundaryTurnIndex, 0, null, null);
            }

            // Monotonic (D8/4.6): only ever move the boundary forward, and only write when it
            // actually changes, so a repeated press with no new complete turn is a true no-op.
            if (!conv.CompactionBoundaryTurnIndex.HasValue
                || lastCompleteTurnIndex.Value > conv.CompactionBoundaryTurnIndex.Value)
            {
                conv.CompactionBoundaryTurnIndex = lastCompleteTurnIndex.Value;
                await db.SaveChangesAsync(ct);
            }

            return new CompactionOutcome(conv.CompactionBoundaryTurnIndex, 0, null, null);
        }
        finally
        {
            await ReleaseLockAsync(conversationId, acquiredLock.LeaseId);
        }
    }

    private async Task ReleaseLockAsync(Guid conversationId, Guid leaseId)
    {
        try
        {
            await _distributedLock.ReleaseLockAsync(conversationId, leaseId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Best-effort: an unreleased lease still degrades safely to "wait out the TTL,"
            // never a permanently stuck lock, so a release failure is a warning, not a rethrow.
            _logger.LogWarning(ex, "Failed to release compaction lock for conversation {ConversationId}", conversationId);
        }
    }
}
```

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test src/server/GuideAntsApi.Tests/GuideAntsApi.Tests.csproj --filter "FullyQualifiedName~CompactionServiceTests"`
Expected: PASS (7 tests).

- [ ] **Step 5: Commit (ask first)**

```bash
git add src/server/GuideAntsApi/Services/Conversations/Commands/CompactionService.cs src/server/GuideAntsApi.Tests/Services/Conversations/CompactionServiceTests.cs
git commit -m "Adds CompactionService with monotonic, idle-only boundary selection"
```

---

## Task 3: Wire `CompactionEngine` and `PromptTokenEstimator` into `CompactionService` (completes 4.4's payload)

**Files:**
- Modify: `src/server/GuideAntsApi/Services/Conversations/Commands/CompactionService.cs`
- Modify: `src/server/GuideAntsApi.Tests/Services/Conversations/CompactionServiceTests.cs`

**Interfaces:**
- Consumes: `AntRunner.Chat.Compaction.CompactionEngine.Compact(IReadOnlyList<ChatMessage>, IReadOnlyList<TurnFacts>) -> CompactionResult` (W3, already on this branch); `CompactionResult(string SummaryText, int SummarizedMessageCount, int SummarizedTurnCount)`; `TurnFacts(int TurnIndex, IReadOnlyList<string> FilesCreated, IReadOnlyList<string> FilesModified)`; `AntRunner.Chat.PromptTokenEstimator.CountChars(IEnumerable<ChatMessage>) -> int`, `EstimateTokens(int chars, double charsPerToken = DefaultCharsPerToken) -> int`; `ConversationMessageMapper.ToChatMessage(NotebookConversationMessage) -> ChatMessage`, `ConversationMessageMapper.FilterDuplicateAssistantMessages(ICollection<NotebookConversationMessage>) -> List<NotebookConversationMessage>` (`GuideAntsApi.Services.Conversations.Mapping`).
- Produces: `CompactConversationAsync` now returns real `MessagesSummarized`/`EstimatedTokensBefore`/`EstimatedTokensAfter` values instead of Task 2's `0`/`null`/`null` placeholders.

- [ ] **Step 1: Write the failing tests**

Add to `CompactionServiceTests.cs` (keep every existing test; these are additive):

```csharp
using AntRunner.Chat.Abstractions;
using GuideAntsApi.DataModel.Models;

// ... inside CompactionServiceTests, add these helpers and tests:

    private static NotebookConversationMessage Message(
        Guid conversationId, int turnIndex, int sequence, DataModelChatRole role, string content) => new()
    {
        NotebookConversationId = conversationId,
        TurnIndex = turnIndex,
        MessageSequence = sequence,
        Role = role,
        Content = content
    };

    [TestMethod]
    public async Task CompactConversationAsync_ComputesMessagesSummarizedFromPreBoundaryMessages()
    {
        var conversationId = Guid.NewGuid();
        var scopeFactory = BuildScopeFactory(nameof(CompactConversationAsync_ComputesMessagesSummarizedFromPreBoundaryMessages));

        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.NotebookConversations.Add(new NotebookConversation { Id = conversationId, NotebookId = Guid.NewGuid(), Title = "t" });
            db.ConversationTurns.Add(Turn(conversationId, 1, "completed"));
            db.NotebookConversationMessages.Add(Message(conversationId, 1, 0, DataModelChatRole.User, "Build a CSV export feature"));
            db.NotebookConversationMessages.Add(Message(conversationId, 1, 1, DataModelChatRole.Assistant, "On it."));
            await db.SaveChangesAsync();
        }

        var distributedLock = AcquiringLock(conversationId);
        var service = new CompactionService(distributedLock.Object, scopeFactory, NullLogger<CompactionService>.Instance);

        var result = await service.CompactConversationAsync(conversationId);

        result.MessagesSummarized.Should().Be(2);
        result.EstimatedTokensBefore.Should().NotBeNull();
        result.EstimatedTokensAfter.Should().NotBeNull();
    }

    [TestMethod]
    public async Task CompactConversationAsync_NoCompletedTurns_LeavesTokenEstimatesNull()
    {
        var conversationId = Guid.NewGuid();
        var scopeFactory = BuildScopeFactory(nameof(CompactConversationAsync_NoCompletedTurns_LeavesTokenEstimatesNull));

        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.NotebookConversations.Add(new NotebookConversation { Id = conversationId, NotebookId = Guid.NewGuid(), Title = "t" });
            db.ConversationTurns.Add(Turn(conversationId, 1, "streaming"));
            await db.SaveChangesAsync();
        }

        var distributedLock = AcquiringLock(conversationId);
        var service = new CompactionService(distributedLock.Object, scopeFactory, NullLogger<CompactionService>.Instance);

        var result = await service.CompactConversationAsync(conversationId);

        result.MessagesSummarized.Should().Be(0);
        result.EstimatedTokensBefore.Should().BeNull();
        result.EstimatedTokensAfter.Should().BeNull();
    }
```

`DataModelChatRole` here is `GuideAntsApi.DataModel.Models.ChatRole` — add `using DataModelChatRole = GuideAntsApi.DataModel.Models.ChatRole;` at the top of the test file if it isn't already aliased (check the existing `using` block first; other test files in this folder already use this exact alias).

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test src/server/GuideAntsApi.Tests/GuideAntsApi.Tests.csproj --filter "FullyQualifiedName~CompactConversationAsync_ComputesMessagesSummarizedFromPreBoundaryMessages|FullyQualifiedName~CompactConversationAsync_NoCompletedTurns_LeavesTokenEstimatesNull"`
Expected: FAIL — `MessagesSummarized` is always `0` and both token fields are always `null` today (Task 2's placeholders), so the first new test fails its `NotBeNull()`/`Be(2)` assertions.

- [ ] **Step 3: Implement**

Add these usings to `CompactionService.cs`:

```csharp
using AntRunner.Chat;
using AntRunner.Chat.Abstractions;
using AntRunner.Chat.Compaction;
using GuideAntsApi.Services.Conversations.Mapping;
using System.Text.Json;
```

Add a shared `JsonSerializerOptions` field (matching `ConversationQueryService`'s convention for this exact pair of columns):

```csharp
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
```

Replace the `return new CompactionOutcome(conv.CompactionBoundaryTurnIndex, 0, null, null);` at the end of the "nothing eligible" branch — leave that one alone, it's still correct (no complete turn means nothing to estimate). Replace the final `return new CompactionOutcome(conv.CompactionBoundaryTurnIndex, 0, null, null);` (the success-path one, after the monotonic-write `if`) with a call to a new private helper:

```csharp
            var boundary = conv.CompactionBoundaryTurnIndex!.Value;
            var (messagesSummarized, tokensBefore, tokensAfter) =
                await ComputeCompactionNumbersAsync(db, conversationId, boundary, ct);

            return new CompactionOutcome(boundary, messagesSummarized, tokensBefore, tokensAfter);
```

Add the helper method:

```csharp
    private async Task<(int MessagesSummarized, int TokensBefore, int TokensAfter)> ComputeCompactionNumbersAsync(
        ApplicationDbContext db, Guid conversationId, int boundaryTurnIndex, CancellationToken ct)
    {
        var allMessages = await db.NotebookConversationMessages
            .AsNoTracking()
            .Where(m => m.NotebookConversationId == conversationId && m.IsStreaming != true)
            .OrderBy(m => m.TurnIndex)
            .ThenBy(m => m.MessageSequence)
            .ToListAsync(ct);

        var deduped = ConversationMessageMapper.FilterDuplicateAssistantMessages(allMessages);

        var preBoundary = deduped.Where(m => m.TurnIndex <= boundaryTurnIndex).ToList();
        var tail = deduped.Where(m => m.TurnIndex > boundaryTurnIndex).ToList();

        var preBoundaryChatMessages = preBoundary.Select(ConversationMessageMapper.ToChatMessage).ToList();
        var tailChatMessages = tail.Select(ConversationMessageMapper.ToChatMessage).ToList();
        var allChatMessages = deduped.Select(ConversationMessageMapper.ToChatMessage).ToList();

        var turnFacts = await db.ConversationTurns
            .AsNoTracking()
            .Where(t => t.NotebookConversationId == conversationId && t.TurnIndex <= boundaryTurnIndex)
            .Select(t => new { t.TurnIndex, t.FilesCreated, t.FilesModified })
            .ToListAsync(ct);

        var turnFactsList = turnFacts
            .Select(t => new TurnFacts(t.TurnIndex, ParseFileList(t.FilesCreated), ParseFileList(t.FilesModified)))
            .ToList();

        var compaction = CompactionEngine.Compact(preBoundaryChatMessages, turnFactsList);

        var tokensBefore = PromptTokenEstimator.EstimateTokens(PromptTokenEstimator.CountChars(allChatMessages));
        var tailChars = PromptTokenEstimator.CountChars(tailChatMessages);
        var tokensAfter = PromptTokenEstimator.EstimateTokens(compaction.SummaryText.Length + tailChars);

        return (compaction.SummarizedMessageCount, tokensBefore, tokensAfter);
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

`DataModelChatRole`/`ChatRole` naming: `ConversationMessageMapper.ToChatMessage` already handles the `GuideAntsApi.DataModel.Models.ChatRole` → `AntRunner.Chat.Abstractions.ChatRole` mapping internally — `CompactionService` never needs to touch that conversion directly.

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test src/server/GuideAntsApi.Tests/GuideAntsApi.Tests.csproj --filter "FullyQualifiedName~CompactionServiceTests"`
Expected: PASS (all 9 tests — the 7 from Task 2 plus the 2 new ones).

- [ ] **Step 5: Commit (ask first)**

```bash
git add src/server/GuideAntsApi/Services/Conversations/Commands/CompactionService.cs src/server/GuideAntsApi.Tests/Services/Conversations/CompactionServiceTests.cs
git commit -m "Computes real before/after token estimates in CompactionService"
```

---

## Task 4: `POST .../compact` endpoint (4.4)

**Files:**
- Create: `src/server/GuideAntsApi/Models/Conversations/CompactionResultDto.cs`
- Modify: `src/server/GuideAntsApi/Endpoints/NotebookConversationsEndpoints.cs`
- Modify: `src/server/GuideAntsApi/Configuration/StartupConfiguration.cs`
- Test: `src/server/GuideAntsApi.Tests/Endpoints/NotebookConversationCompactEndpointTests.cs`

**Interfaces:**
- Consumes: `ICompactionService.CompactConversationAsync(Guid, CancellationToken) -> Task<CompactionOutcome>` (Task 3).
- Produces: `CompactionResultDto(int? BoundaryTurnIndex, int MessagesSummarized, int? EstimatedTokensBefore, int? EstimatedTokensAfter)`, serialized camelCase by the API's default JSON options (confirmed convention: every existing DTO in this file relies on the default minimal-API camelCase policy, no `[JsonPropertyName]` needed).

- [ ] **Step 1: Write the failing test**

Before writing, read `src/server/GuideAntsApi.Tests/Endpoints/NotebookConversationContextStatusEndpointTests.cs` in full — it's the exact precedent for testing a named static endpoint-handler method directly (no `WebApplicationFactory`, no Docker) via a bare `DefaultHttpContext` + `ServiceCollection`.

```csharp
using System.Text.Json;
using FluentAssertions;
using GuideAntsApi.Endpoints;
using GuideAntsApi.Services.Conversations.Commands;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace GuideAntsApi.Tests.Endpoints;

[TestClass]
public sealed class NotebookConversationCompactEndpointTests
{
    [TestMethod]
    public async Task Compact_ReturnsBoundaryAndEstimates()
    {
        var convoId = Guid.NewGuid();
        var service = new Mock<ICompactionService>();
        service.Setup(s => s.CompactConversationAsync(convoId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CompactionOutcome(4, 12, 5000, 900));

        var result = await NotebookConversationsEndpoints.CompactConversationAsync(
            service.Object, NullLogger.Instance, convoId, CancellationToken.None);
        var (code, body) = await ExecuteAsync(result);

        code.Should().Be(StatusCodes.Status200OK);
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("boundaryTurnIndex").GetInt32().Should().Be(4);
        doc.RootElement.GetProperty("messagesSummarized").GetInt32().Should().Be(12);
        doc.RootElement.GetProperty("estimatedTokensBefore").GetInt32().Should().Be(5000);
        doc.RootElement.GetProperty("estimatedTokensAfter").GetInt32().Should().Be(900);
    }

    [TestMethod]
    public async Task Compact_ConversationNotFound_Returns404()
    {
        var convoId = Guid.NewGuid();
        var service = new Mock<ICompactionService>();
        service.Setup(s => s.CompactConversationAsync(convoId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new KeyNotFoundException("Conversation not found"));

        var result = await NotebookConversationsEndpoints.CompactConversationAsync(
            service.Object, NullLogger.Instance, convoId, CancellationToken.None);
        var (code, _) = await ExecuteAsync(result);

        code.Should().Be(StatusCodes.Status404NotFound);
    }

    [TestMethod]
    public async Task Compact_ConversationLocked_Returns409NamingTheHolder()
    {
        var convoId = Guid.NewGuid();
        var service = new Mock<ICompactionService>();
        service.Setup(s => s.CompactConversationAsync(convoId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Conversation is locked by someone-else"));

        var result = await NotebookConversationsEndpoints.CompactConversationAsync(
            service.Object, NullLogger.Instance, convoId, CancellationToken.None);
        var (code, body) = await ExecuteAsync(result);

        code.Should().Be(StatusCodes.Status409Conflict);
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("error").GetString().Should().Contain("Conversation is locked by");
    }

    private static async Task<(int Code, string Body)> ExecuteAsync(IResult result)
    {
        var ctx = new DefaultHttpContext();
        ctx.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        ctx.Response.Body = new MemoryStream();
        await result.ExecuteAsync(ctx);
        ctx.Response.Body.Position = 0;
        return (ctx.Response.StatusCode, await new StreamReader(ctx.Response.Body).ReadToEndAsync());
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test src/server/GuideAntsApi.Tests/GuideAntsApi.Tests.csproj --filter "FullyQualifiedName~NotebookConversationCompactEndpointTests"`
Expected: FAIL to compile — `NotebookConversationsEndpoints.CompactConversationAsync` doesn't exist.

- [ ] **Step 3: Implement**

`CompactionResultDto.cs`:

```csharp
namespace GuideAntsApi.Models.Conversations;

public sealed record CompactionResultDto(
    int? BoundaryTurnIndex,
    int MessagesSummarized,
    int? EstimatedTokensBefore,
    int? EstimatedTokensAfter);
```

In `NotebookConversationsEndpoints.cs`: read the existing `GetConversationAsync` method (`:18` onward) first to match its exact style (logger usage, `internal static async Task<IResult>` signature shape). Add near it:

```csharp
    internal static async Task<IResult> CompactConversationAsync(
        ICompactionService compactionService,
        ILogger logger,
        Guid convoId,
        CancellationToken ct)
    {
        try
        {
            var outcome = await compactionService.CompactConversationAsync(convoId, ct);
            return Results.Ok(new CompactionResultDto(
                outcome.BoundaryTurnIndex,
                outcome.MessagesSummarized,
                outcome.EstimatedTokensBefore,
                outcome.EstimatedTokensAfter));
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound(new { error = "Conversation not found" });
        }
        catch (InvalidOperationException ex) when (
            ex.Message.Contains("Conversation is locked by", StringComparison.OrdinalIgnoreCase))
        {
            return Results.Conflict(new { error = ex.Message });
        }
    }
```

Add `using GuideAntsApi.Services.Conversations.Commands;` to the file's top if not already present (check first — the file already has several `using GuideAntsApi.Services.Conversations...` lines; add only what's missing).

Wire the route into `MapNotebookConversationsEndpoints`, next to the other `/{convoId:guid}/...` POST routes:

```csharp
        group.MapPost("/{convoId:guid}/compact", async (
                [FromServices] ICompactionService compactionService,
                [FromServices] ILoggerFactory loggerFactory,
                Guid notebookId,
                Guid convoId,
                CancellationToken ct) =>
            await CompactConversationAsync(compactionService, loggerFactory.CreateLogger("NotebookConversations"), convoId, ct))
            .RequireAuthorization("RequireContributor")
            .Produces<CompactionResultDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);
```

Match this to exactly how `GetConversationAsync`'s own `MapGet` call site passes `loggerFactory.CreateLogger("NotebookConversations")` (`:66` per the earlier `grep` — read that call site first and mirror its `ILoggerFactory`/`Guid`/`CancellationToken` parameter wiring precisely, since minimal-API parameter binding is sensitive to attribute placement).

In `StartupConfiguration.cs`, next to `services.AddScoped<IConversationUndoService, ConversationUndoService>();` (`:163`):

```csharp
        services.AddScoped<ICompactionService, CompactionService>();
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test src/server/GuideAntsApi.Tests/GuideAntsApi.Tests.csproj --filter "FullyQualifiedName~NotebookConversationCompactEndpointTests"`
Expected: PASS (3 tests).

Then confirm the whole project still builds (the route wiring touches a large shared file):

Run: `dotnet build src/server/GuideAntsApi.sln`
Expected: builds clean.

- [ ] **Step 5: Commit (ask first)**

```bash
git add src/server/GuideAntsApi/Models/Conversations/CompactionResultDto.cs src/server/GuideAntsApi/Endpoints/NotebookConversationsEndpoints.cs src/server/GuideAntsApi/Configuration/StartupConfiguration.cs src/server/GuideAntsApi.Tests/Endpoints/NotebookConversationCompactEndpointTests.cs
git commit -m "Adds the POST compact endpoint"
```

---

## Task 5: Wire `BoundaryTurnIndex` into `ConversationContextStatusService` (closes the W2-flagged gap)

**Files:**
- Modify: `src/server/GuideAntsApi/Services/Conversations/ConversationContextStatusService.cs`
- Modify: `src/server/GuideAntsApi.Tests/Services/Conversations/ConversationContextStatusServiceTests.cs`

**Interfaces:**
- Consumes: `NotebookConversation.CompactionBoundaryTurnIndex` (Task 1).
- Produces: `ConversationContextStatusDto.BoundaryTurnIndex` now reflects the real persisted value instead of always `null`. `ConversationContextStatusDto`'s shape is unchanged (still 6 positional fields, same order) — only the value populated into the 3rd slot changes.

- [ ] **Step 1: Write the failing test**

Read `ConversationContextStatusServiceTests.cs`'s existing `BoundaryTurnIndex_IsNullUntilW4` test first (added by the W2 plan) — this task replaces it, since the reason it asserted null no longer holds.

```csharp
[TestMethod]
public async Task BoundaryTurnIndex_ReflectsThePersistedColumn()
{
    var conversationId = Guid.NewGuid();
    // ... seed a conversation with CompactionBoundaryTurnIndex = 3 and at least one completed
    // turn with usage, using this file's existing seeding helpers ...

    var status = await service.GetAsync(conversationId);

    status.BoundaryTurnIndex.Should().Be(3);
}

[TestMethod]
public async Task BoundaryTurnIndex_IsNull_WhenConversationNeverCompacted()
{
    // ... seed a conversation with CompactionBoundaryTurnIndex left at its default (null) ...

    var status = await service.GetAsync(conversationId);

    status.BoundaryTurnIndex.Should().BeNull();
}
```

Adapt these to the file's actual existing seeding helpers and constructor calls — read the file first rather than guessing its exact fixture-building methods, since W2's plan built a specific `Turn(...)` helper and DbContext setup this task must reuse, not duplicate.

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test src/server/GuideAntsApi.Tests/GuideAntsApi.Tests.csproj --filter "FullyQualifiedName~BoundaryTurnIndex"`
Expected: FAIL — `BoundaryTurnIndex_ReflectsThePersistedColumn` fails because `GetAsync` still hardcodes `null`; the old `BoundaryTurnIndex_IsNullUntilW4` test (if not yet removed) still passes but is now testing stale behavior — delete it as part of this step, don't leave a passing test asserting the pre-W4 contract.

- [ ] **Step 3: Implement**

In `ConversationContextStatusService.cs`, add a lookup near the top of `GetAsync` (before either existing `return` statement) and thread it into both:

```csharp
        var boundaryTurnIndex = await db.NotebookConversations
            .Where(c => c.Id == conversationId)
            .Select(c => c.CompactionBoundaryTurnIndex)
            .FirstOrDefaultAsync(ct);
```

Change the no-completed-turns return (`:54`) from:
```csharp
return new ConversationContextStatusDto(null, null, null, ContextEstimateSource.None, null, ContextWindowSource.Unknown);
```
to:
```csharp
return new ConversationContextStatusDto(null, null, boundaryTurnIndex, ContextEstimateSource.None, null, ContextWindowSource.Unknown);
```

And the normal-path return (`:130`) from:
```csharp
return new ConversationContextStatusDto(window, estimate, null, source, modelId, windowSource);
```
to:
```csharp
return new ConversationContextStatusDto(window, estimate, boundaryTurnIndex, source, modelId, windowSource);
```

Also update the class's doc comment on `ConversationContextStatusDto.BoundaryTurnIndex` in `IConversationContextStatusService.cs` — it currently says "always null until the compaction boundary column exists (W4)"; change it to describe the real behavior now that W4 exists, e.g. "Null when the conversation has never been compacted."

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test src/server/GuideAntsApi.Tests/GuideAntsApi.Tests.csproj --filter "FullyQualifiedName~ConversationContextStatusServiceTests"`
Expected: PASS, all tests in the file including the two new ones and no leftover stale test.

- [ ] **Step 5: Commit (ask first)**

```bash
git add src/server/GuideAntsApi/Services/Conversations/ConversationContextStatusService.cs src/server/GuideAntsApi/Services/Conversations/IConversationContextStatusService.cs src/server/GuideAntsApi.Tests/Services/Conversations/ConversationContextStatusServiceTests.cs
git commit -m "Wires the compaction boundary into the conversation context status DTO"
```

---

## Task 6: Full verification (4.8 rollup)

- [ ] **Step 1: Server unit tests and build**

Run: `dotnet build src/server/GuideAntsApi.sln && dotnet test src/server/GuideAntsApi.sln --filter "TestCategory!=Integration"`
Expected: build clean, no new failures.

- [ ] **Step 2: Confirm the migration is the only schema change**

Run: `git diff --stat <task-1-base-commit>..HEAD -- src/server/GuideAntsApi.DataModel/Migrations/`
Expected: exactly one new migration pair (`.cs` + `.Designer.cs`) plus the regenerated `ApplicationDbContextModelSnapshot.cs`, all from Task 1 — no other task should have touched migrations.

- [ ] **Step 3: Manual end-to-end check against a running API (if a dev DB is available)**

Run the API (`dotnet run --project src/server/GuideAntsApi`), send a couple of messages in a notebook conversation so at least one turn completes, then `POST /api/projects/{projectId}/notebooks/{notebookId}/conversations/{convoId}/compact`. Confirm: (a) the response's `boundaryTurnIndex` matches the last completed turn's index; (b) pressing it again immediately returns the same `boundaryTurnIndex` (idempotent); (c) `GET .../conversations/{convoId}` now shows a non-null `contextStatus.boundaryTurnIndex`. If no dev DB is available in this environment, skip this step and say so explicitly rather than claiming it passed.

- [ ] **Step 4: Tick W4 in the checklist**

In `docs/context-compaction-plan.md`, tick 4.1–4.8 and add a line under W4 linking this plan (matching the "Implementation plan written" pattern used for W1/W2/W3), plus a note that Task 5 (wiring `BoundaryTurnIndex` into `ConversationContextStatusService`) was added beyond the checklist's 8 items to close a gap W2's plan explicitly flagged as "until W4 adds the column."

---

## Self-review

**Spec coverage.**
- 4.1 (column) → Task 1.
- 4.2 (migration) → Task 1, Step 5.
- 4.3 (boundary selection: last complete turn, never split a paused/streaming turn) → Task 2; the invariant is enforced structurally by filtering on `Status == "completed"`, verified by `CompactConversationAsync_OneCompletedTurn_SetsBoundaryToItsIndex`'s `"pending_client_tool"` turn at a higher index.
- 4.4 (POST endpoint + response shape) → Task 4 (route/DTO), built on Task 2+3's `CompactionOutcome`.
- 4.5 (idempotent) → Task 2's `CompactConversationAsync_PressedAgainWithNoNewCompletedTurn_IsIdempotent`.
- 4.6 (monotonic) → Task 2's `CompactConversationAsync_NeverMovesTheBoundaryBackward`, deliberately contrived to prove the guard rather than relying on realistic data happening to only increase.
- 4.7 (idle-only via the distributed lock, 409 naming the holder, not the in-process registry) → Task 2 (service-level lock acquisition) + Task 4 (409 mapping); Global Constraints explicitly rules out `ConversationStreamRunRegistry.IsAnyActiveForConversation`.
- 4.8 (tests: invariants, idempotency, monotonicity, no-complete-turn no-op, 409 while streaming) → covered across Task 2 (`NoCompletedTurns_ReturnsExistingBoundaryUnchanged`, `ConversationLocked_ThrowsNamingTheHolder`) and Task 4 (`Compact_ConversationLocked_Returns409NamingTheHolder`).
- Spec's "Compact is idempotent when no new complete turn exists" and "Compact pressed with no complete turn: no-op" (Error handling table) → same test, Task 2.
- Spec's "extends the existing conversation read rather than adding an endpoint" for `boundaryTurnIndex` → Task 5, added beyond the literal 4.1–4.8 list because W2's own plan flagged it as W4's job.

**Known limitations, deliberately accepted.**
- `estimatedTokensBefore`/`estimatedTokensAfter` use `PromptTokenEstimator`'s default chars-per-token ratio, not the per-conversation calibration `ConversationContextStatusService` (W2) uses for the persistent meter. These two numbers are point-in-time press-time feedback, not the meter itself — the meter (unaffected by this plan) keeps its calibrated estimate.
- `CompactionEngine.Compact` is invoked once per `POST /compact` purely to compute `messagesSummarized`/`estimatedTokensAfter`; the resulting `SummaryText` is discarded here and recomputed later by W5 at history-build time. This is intentional (D3: the boundary is the persisted decision, the summary is always recomputed), not wasted work by accident.
- The lock-release path logs and swallows failures rather than retrying (unlike `ConversationUndoService`'s 4-attempt confirmed-release loop), because compaction has no local semaphore gate to clean up — an unreleased lease is recoverable via its TTL alone. If this proves too lossy in practice, the retry wrapper is available to copy from `ConversationUndoService.ReleaseDistributedLockUntilConfirmedAsync`.

**Placeholder scan.** Every code step has complete code. The "read X first" instructions (Task 2's `ConversationLock` field list, `ConversationContextStatusServiceTests.cs`'s exact fixture helpers in Task 5, `GetConversationAsync`'s exact parameter-binding style in Task 4) name the exact file to read because the real content could not be pinned from research alone — each says precisely what to verify.

**Type consistency.** `CompactionOutcome` (Task 2) is consumed unchanged by Task 4's endpoint and mapped 1:1 into `CompactionResultDto`. `ICompactionService.CompactConversationAsync(Guid, CancellationToken)` signature matches between Task 2's definition, Task 3's implementation, and Task 4's endpoint call site. `TurnFacts`/`CompactionEngine.Compact`/`CompactionResult` match W3's actual shipped signatures (verified directly against `src/server/AntRunner.Chat/AntRunner.Chat/Compaction/CompactionEngine.cs` and `TurnFacts.cs` on this branch, not from memory of the W3 plan).
