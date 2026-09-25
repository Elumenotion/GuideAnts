# W9: Validation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Produce the evidence the compaction feature needs before it ships. That means a cross-domain read of the engine's output on real transcripts (9.1), an end-to-end run of *long conversation → overflow → compact → continue → recall* (9.3), and a CI-faithful run of every test job with each failure classified as branch-caused or pre-existing (9.2, 9.4). Fix the one branch-caused failure this research already found.

**Architecture:** Mostly test and tooling code, plus two one-line production changes. (1) A hermetic end-to-end integration test drives the real HTTP → `ConversationService` → `ThreadRun` → SQL Server path. The existing fake provider gains an "overflow above a prompt-size budget" scenario, so it runs in CI on every push. (2) The same scenario runs against a real llama.cpp server as an opt-in test (env-gated, `Assert.Inconclusive` when absent), with a runbook. It is the only test that exercises llama.cpp's real overflow error body end to end. (3) The 9.1 benchmark is an env-gated test in the unit project. It calls the production `ConversationHistoryBuilder` summary path against a real database and writes per-conversation markdown reports to a git-ignored folder. A reading task turns those reports into a committed findings document against a fixed rubric. (4) A CI-mirror task runs all three workflow jobs locally with CI's toolchain and compares every failure against `main`.

**Tech Stack:** ASP.NET Core 8, EF Core 8 (SQL Server + InMemory), MSTest + FluentAssertions + Moq, `WebApplicationFactory` + Testcontainers (`GuideAntsApi.IntegrationTests`), llama.cpp `llama-server`, Vitest under Node 22.

**Spec:** [`docs/superpowers/specs/2026-09-21-context-compaction-design.md`](../specs/2026-09-21-context-compaction-design.md). The relevant sections are *Testing* items 2, 4, 5, 6 and 7, the *Integration* paragraph, and *Risks accepted* (row 2, "Extraction quality may be poor for non-coding domains"). Checklist: [`docs/context-compaction-plan.md`](../../context-compaction-plan.md) W9 (9.1–9.4).

## Global Constraints

- **Commits carry no `Co-Authored-By` trailer.** `CLAUDE.md` says "Never co-author commits (no `Co-Authored-By` trailer)" and outranks any harness attribution default. **Only commit when the user has said to** (memory `no-commits-without-permission`). Every commit step below is a proposed checkpoint: ask first.
- **No git worktrees** (`CLAUDE.md`). Comparing against `main` means `git switch main` on a clean tree, then `git switch feature/compaction` back. Never stash-and-forget: check `git status --short` before and after every switch.
- **CI parity values, from `.github/workflows/client-test.yml`:** .NET `8.0.x`; server tests run `--configuration Release`; the client runs `npm run test:coverage` on **Node 22**. Local Node is 26.3.0 (jsdom `localStorage` breaks under it, per W8), so run client commands through `npx -y -p node@22 …` (verified here: gives `v22.23.3`). Integration tests need Docker and `GA_INTEGRATION_TEST_MSSQL_IMAGE`. CI uses `ghcr.io/<owner>/mssql2025-express-fts:latest`; locally use `ghcr.io/elumenotion/mssql2025-express-fts:main` (already pulled). CI also sets `GA_ENABLE_RUNTIME_LOAD_TESTS=0`.
- **An env-gated test that is not configured reports Inconclusive, never Passed.** Use `Assert.Inconclusive("<what to set>")`, the pattern already used in `HostFolderMountReconcileIntegrationTests.cs:85`. A report says "skipped", not "passed".
- **Benchmark output never enters git.** Reports quote real transcript content, so they go to `src/server/.compaction-benchmark/` (git-ignored in Task 4). The committed findings document paraphrases. It identifies conversations by the first 8 hex characters of their id and quotes no transcript text.
- **The real-llama.cpp run asserts only that `conversation_recall` is *offered*.** A 0.5B model's decision to call a tool is not deterministic. The hermetic test (Task 2) proves recall *executes* and returns the right content.
- **Production changes are limited to two.** Task 1 adds a `[JsonConverter]` attribute on two enums; server wire output is unchanged, since a global `JsonStringEnumConverter` already writes these as strings (`Program.cs:150`). Task 4 changes one method's visibility from `private` to `internal`. Anything else a task seems to need in production code is a stop-and-report, not a fix.
- **Server tests use MSTest (`[TestClass]`/`[TestMethod]`) + FluentAssertions + Moq.** `AssistantUtility` and `FakeChatCompletionBehavior.Instance` are process-global, and the existing base classes reset both. Copy their conventions rather than inventing new ones.

## Findings that shaped this plan

Every claim below was checked against `feature/compaction` HEAD `7d6e8d7` on 2026-09-24.

- **The integration-test failures called "pre-existing" in W5's and W7's notes are a regression from this branch.** Running `CreateConversation_And_SendMessage_Flow` and `SendMessage_NewConversation_CreatesConversationWithSelectedAssistant` fails both with `System.Text.Json.JsonException: The JSON value could not be converted to …ConversationContextStatusDto. Path: $.contextStatus.estimateSource`. W2 added `ContextStatus` to `ConversationDto` (`Models/Conversations/ConversationDto.cs:82`), carrying two enums: `ContextEstimateSource` and `ContextWindowSource`. The server writes enums as strings through the global converter. The ~12 existing integration tests that read `ConversationDto` with default options cannot read them back. W7's Task 4 only reverted W7's own one-line change, so its "pre-existing" check could not tell a branch regression from a `main` one. **CI's `server-integration-test` job, which runs on every push to `feature/**`, fails today because of this.** Task 1 fixes it.
  - Why the fix goes on the types: the repo's existing workaround is per-test `JsonSerializerOptions` with `JsonStringEnumConverter` (`ProjectScheduledJobEndpointsTests.cs:17`, `HostFolderMountEndpointsTests.cs:19`). That would mean editing a dozen test files, and every *external* consumer of the conversation API would still break the same way. The attribute fixes all of them.
- **The integration suite is fully hermetic.** `TestWebApplicationFactory` replaces `IChatCompletionClientFactory` with `FakeChatCompletionClientFactory` (`TestWebApplicationFactory.cs`, "Integration tests should not depend on external LLM providers"). The llama.cpp-named suites (`Qwen36ChatWalkthroughTests`, `RuntimeConcurrencyTests`) use `StubLlamaServerRuntimeClient`, not a real server. Nothing in the repo talks to a real llama.cpp in tests. That is why 9.3 splits into a CI-runnable hermetic test (Task 2) and an opt-in real one (Task 3).
- **This machine has no model a real llama.cpp run can use.** The running `guideants-ai` container's router lists `{"data":[]}` at `/llama-cpp/v1/models`, and its port 80 is not published to the host (`docker inspect`: `"80/tcp":null`). Task 3's runbook therefore starts a separate throwaway `llama-server` on host port 8199 with a ~400 MB model. It never touches the user's `guideants-ai` container.
- **The fake provider bypasses overflow *classification*; the real run does not.** The fake throws `ChatContextOverflowException` directly. A real llama-server returns HTTP 400 with `exceed_context_size_error`, which `LlamaCppChatClient.cs:406` must classify through `ChatContextOverflowClassifier.TryClassifyBody`. Only Task 3 exercises that path through the full stack.
- **The existing fake already supports a recall round-trip.** `FakeChatScenario.ToolCallThenReply` emits one tool call named by `ToolFunctionName`, with `ToolArgumentsJson`, then replies. Setting those to `conversation_recall` and `{"query":…}` makes the real `ThreadRun` dispatch the real tool. This works because W7 registers its request builder unconditionally (`ThreadRun.cs`, `EnsureRequestBuilderCache`). Only the overflow scenario is new.
- **W7's final review found that the only exposure test for a *compacted* conversation used a stale boundary.** `ConversationRecallWiringTests.SendMessageStream_CompactedConversation_OffersRecall` seeds `boundary: 2` with no turns, which is exactly the stale case `ConversationHistoryBuilder.cs:176-185` falls back from. No test yet covers the first send after a real compaction. Task 2's phase 4 does.
- **The real-transcript corpus for 9.1 is small, and the non-coding half is thin.** Only the `guideants` database on the local SQL Server holds conversations (23 total; `guideants-dev` has none). Six have ≥5 turns. Classifying by tool name, where `Code_Executor` is a crew coding sub-agent:
  - **Coding (3):** `FAC2F4F7`, 8 turns, 132k chars; `504058DC`, 7 turns, 116k chars; `DD0DDA4B`, 7 turns, 249k chars.
  - **Non-coding (3):** `716804F2`, 14 turns, 1.6k chars, no tools; `7EEAA723`, 11 turns, 7.9k chars, client-side `external_tool`; `D486EE32`, 6 turns, 11k chars, `SearchAssistantFiles` knowledge Q&A.
  - No notebook producing audio, images or documents is represented. Task 5 records that gap and has an optional step to add conversations of that kind before the reading.
- **`Development`'s configured database (`guideants-dev-open-router-tests`) does not exist here**, so the benchmark takes an explicit connection string instead of reading appsettings.
- **Boundary selection is inline, not reusable.** `CompactionService.cs:104` computes `max(TurnIndex where Status == "completed")` inline. The benchmark mirrors that one rule, citing the line, rather than extracting a shared helper for a single test caller.
- **The production summary path is private.** `ConversationHistoryBuilder.BuildCompactionSummaryMessageAsync` (`ConversationHistoryBuilder.cs:223`) deduplicates messages, maps them, loads `TurnFacts` and runs the engine. A benchmark that re-implemented that would test the re-implementation. Making the method `internal` lets `GuideAntsApi.Tests` call the real one; `InternalsVisibleTo` is already granted in `GuideAntsApi/Properties/AssemblyInfo.cs:3`.
- **The integration assembly starts a SQL Server container on every run** (`TestAssemblySetup.AssemblyInitialize`). The benchmark reads an existing database and needs no container, so it lives in the unit project.
- **String tool results are stored double-encoded.** `ThreadRun` passes every local-function result through `JsonSerializer.Serialize` (`ThreadRun.cs:1831` → `:2319`). A tool returning a JSON *string*, as `conversation_recall` and all of `MemoryTools` do, is therefore stored as a JSON string literal wrapping that JSON. This predates the compaction work and affects every such tool, so W9 does not change it. Task 2's recall assertion unwraps it before parsing.
- **The client coverage gate is not actually enforced.** W8 found `vitest.config.ts` uses flat threshold keys that Vitest 4 ignores; `CLAUDE.md` says "≥85% lines enforced in CI". That is pre-existing on `main` and out of W9's scope. Task 6 records it for W10 so it isn't lost.

## File Structure

**Created:**
- `src/server/GuideAntsApi.Tests/Services/Conversations/ConversationContextStatusDtoSerializationTests.cs`: wire-contract test for the two enums (Task 1)
- `src/server/GuideAntsApi.IntegrationTests/Infrastructure/ConversationStreamTestHelpers.cs`: seeding and SSE helpers shared by Task 2, Task 3 and the existing `ToolLimitIntegrationTests` (Task 2)
- `src/server/GuideAntsApi.IntegrationTests/Services/Conversations/CompactionEndToEndTests.cs`: hermetic 9.3 (Task 2)
- `src/server/GuideAntsApi.IntegrationTests/Infrastructure/LlamaCompactionWebApplicationFactory.cs`: factory plus recording client for the real run (Task 3)
- `src/server/GuideAntsApi.IntegrationTests/Services/Conversations/CompactionLlamaCppEndToEndTests.cs`: opt-in real 9.3 (Task 3)
- `docs/compaction-llamacpp-e2e-runbook.md`: how to run Task 3 (Task 3)
- `src/server/GuideAntsApi.Tests/Benchmarks/CompactionBenchmarkRunner.cs`: candidate selection, stats and markdown rendering (Task 4)
- `src/server/GuideAntsApi.Tests/Benchmarks/CompactionBenchmarkTests.cs`: always-run harness tests plus the env-gated real run (Task 4)
- `docs/compaction-benchmark-findings.md`: the 9.1 verdict (Task 5)

**Modified:**
- `src/server/GuideAntsApi/Services/Conversations/IConversationContextStatusService.cs`: attribute on `ContextEstimateSource` (Task 1)
- `src/server/GuideAntsApi/Services/Routing/IContextWindowResolver.cs`: attribute on `ContextWindowSource` (Task 1)
- `src/server/GuideAntsApi.IntegrationTests/Infrastructure/FakeChatCompletionClientFactory.cs`: `OverflowAbovePromptBudget` scenario, tool-name capture (Task 2)
- `src/server/GuideAntsApi.IntegrationTests/Services/Conversations/ToolLimitIntegrationTests.cs`: use the shared helpers instead of private copies (Task 2)
- `src/server/GuideAntsApi/Services/Conversations/Mapping/ConversationHistoryBuilder.cs`: `BuildCompactionSummaryMessageAsync` goes from `private` to `internal` (Task 4)
- `.gitignore`: `src/server/.compaction-benchmark/` (Task 4)
- `docs/context-compaction-plan.md`: tick W9, correct W5's "pre-existing" note, record W10 carry-overs (Task 6)

## Review Focus

Five conditions the spec implies but no existing test covers. Each line's test is written into the task that owns it.

1. **A compacted conversation that still overflows.** A single new message larger than the whole window must still fail cleanly: a `chat_context_overflow` error, and no stored message changed. It must not crash or silently truncate. *(Task 2, `CompactedConversation_StillOverflowing_FailsCleanly_WithoutMutatingContent`.)*
2. **Compact pressed right after an overflow.** The overflowed turn is failed, not completed, so the boundary must land on the last *completed* turn. *(Task 2, asserted in `BuildOverflowedThenCompactedConversationAsync`.)*
3. **The first send after a real compaction goes through the compacted path and offers recall.** It must not hit the stale-boundary fallback (the gap from W7's final review). *(Task 2, phase 4.)*
4. **Benchmark input that isn't a clean long conversation.** Zero completed turns, only failed turns, a conversation already compacted, or a tool-less chat. The harness must skip or handle each one and never throw partway through a run. *(Task 4, `SelectCandidatesAsync_*` tests.)*
5. **Benchmark output leaking transcript text into git.** *(Task 4, Step 7: `git check-ignore` on the output folder.)*

---

## Task 1: Fix the `ContextStatus` enum wire contract (branch regression)

**Files:**
- Modify: `src/server/GuideAntsApi/Services/Conversations/IConversationContextStatusService.cs:1-10`
- Modify: `src/server/GuideAntsApi/Services/Routing/IContextWindowResolver.cs:1-10`
- Test: `src/server/GuideAntsApi.Tests/Services/Conversations/ConversationContextStatusDtoSerializationTests.cs`

**Interfaces:**
- Consumes: `ConversationContextStatusDto(int? ContextWindowTokens, int? EstimatedPromptTokens, int? BoundaryTurnIndex, ContextEstimateSource EstimateSource, string? ModelDeploymentId, ContextWindowSource ContextWindowSource)`.
- Produces: nothing new. After this task, `ConversationDto` round-trips through `JsonSerializerDefaults.Web`, which Task 2's and Task 3's HTTP reads rely on.

- [ ] **Step 1: Write the failing test**

Create `src/server/GuideAntsApi.Tests/Services/Conversations/ConversationContextStatusDtoSerializationTests.cs`:

```csharp
using System.Text.Json;
using FluentAssertions;
using GuideAntsApi.Services.Conversations;
using GuideAntsApi.Services.Routing;

namespace GuideAntsApi.Tests.Services.Conversations;

/// <summary>
/// The server writes these enums as strings (global JsonStringEnumConverter, Program.cs). Any consumer
/// reading the conversation API with default web options -- HttpContent.ReadFromJsonAsync, the integration
/// suite, an external client -- must be able to read them back. W2 broke that; see the W9 plan.
/// </summary>
[TestClass]
public sealed class ConversationContextStatusDtoSerializationTests
{
    private static readonly JsonSerializerOptions WebDefaults = new(JsonSerializerDefaults.Web);

    [TestMethod]
    public void Deserialize_StringEnumsWithDefaultWebOptions_ReadsBothEnums()
    {
        const string json =
            """{"contextWindowTokens":8192,"estimatedPromptTokens":1200,"boundaryTurnIndex":3,""" +
            """"estimateSource":"Characters","modelDeploymentId":"m","contextWindowSource":"LiveRuntime"}""";

        var dto = JsonSerializer.Deserialize<ConversationContextStatusDto>(json, WebDefaults);

        dto.Should().NotBeNull();
        dto!.EstimateSource.Should().Be(ContextEstimateSource.Characters);
        dto.ContextWindowSource.Should().Be(ContextWindowSource.LiveRuntime);
    }

    [TestMethod]
    public void Serialize_WithDefaultWebOptions_WritesEnumsAsTheSameStringsTheServerSends()
    {
        var dto = new ConversationContextStatusDto(
            8192, 1200, 3, ContextEstimateSource.ProviderUsage, "m", ContextWindowSource.Catalog);

        var json = JsonSerializer.Serialize(dto, WebDefaults);

        json.Should().Contain("\"estimateSource\":\"ProviderUsage\"");
        json.Should().Contain("\"contextWindowSource\":\"Catalog\"");
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run, from `src/server`: `dotnet test GuideAntsApi.Tests/GuideAntsApi.Tests.csproj --filter "FullyQualifiedName~ConversationContextStatusDtoSerializationTests"`
Expected: both FAIL. The first throws `JsonException … Path: $.estimateSource`; the second finds `"estimateSource":1` instead of `"ProviderUsage"`.

- [ ] **Step 3: Annotate both enums**

In `src/server/GuideAntsApi/Services/Conversations/IConversationContextStatusService.cs`, add the using and the attribute:

```csharp
using System.Text.Json.Serialization;
using GuideAntsApi.Services.Routing;

namespace GuideAntsApi.Services.Conversations;

// Wire contract is the enum name. The server already writes it that way via the global converter;
// the attribute makes every other reader (tests, external clients) agree without extra options.
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ContextEstimateSource
{
    None,
    ProviderUsage,
    Characters
}
```

In `src/server/GuideAntsApi/Services/Routing/IContextWindowResolver.cs`, add `using System.Text.Json.Serialization;` at the top and the same attribute plus comment above `public enum ContextWindowSource`. Leave the members unchanged.

- [ ] **Step 4: Run the unit test to verify it passes**

Run: `dotnet test GuideAntsApi.Tests/GuideAntsApi.Tests.csproj --filter "FullyQualifiedName~ConversationContextStatusDtoSerializationTests"`
Expected: PASS, 2/2.

- [ ] **Step 5: Confirm the integration regression is gone**

Run, from `src/server` (needs Docker):

```bash
GA_INTEGRATION_TEST_MSSQL_IMAGE=ghcr.io/elumenotion/mssql2025-express-fts:main \
  dotnet test GuideAntsApi.IntegrationTests/GuideAntsApi.IntegrationTests.csproj \
  --filter "FullyQualifiedName~NotebookConversationEndpointsTests|FullyQualifiedName~NotebookConversationAssistantSwitchingTests|FullyQualifiedName~ConversationServiceIntegrationTests"
```

Expected: no failure whose message contains `ConversationContextStatusDto` or `estimateSource`. If any *other* failure appears, record its test name and first error line in the report. Do not fix it here; Task 6 classifies it.

- [ ] **Step 6: Commit (ask the user first)**

```bash
git add src/server/GuideAntsApi/Services/Conversations/IConversationContextStatusService.cs \
        src/server/GuideAntsApi/Services/Routing/IContextWindowResolver.cs \
        src/server/GuideAntsApi.Tests/Services/Conversations/ConversationContextStatusDtoSerializationTests.cs
git commit -m "fix(compaction): make ContextStatus enums readable with default JSON options

W2 added ContextEstimateSource and ContextWindowSource to ConversationDto. The
server writes them as strings through a global converter, but any reader using
default options (the integration suite, external clients) failed with a
JsonException at \$.contextStatus.estimateSource. Annotating the enum types
makes the string contract self-describing; server output is unchanged."
```

---

## Task 2: Hermetic end-to-end test — overflow → compact → continue → recall (9.3, CI half)

**Files:**
- Modify: `src/server/GuideAntsApi.IntegrationTests/Infrastructure/FakeChatCompletionClientFactory.cs`
- Create: `src/server/GuideAntsApi.IntegrationTests/Infrastructure/ConversationStreamTestHelpers.cs`
- Modify: `src/server/GuideAntsApi.IntegrationTests/Services/Conversations/ToolLimitIntegrationTests.cs` (delete its private `SeedProjectNotebookAsync`, `SeedConversationAsync` and `SendConversationStreamToCompletionAsync`, and call the shared helpers instead)
- Test: `src/server/GuideAntsApi.IntegrationTests/Services/Conversations/CompactionEndToEndTests.cs`

**Interfaces:**
- Consumes: Task 1, since `ConversationDto` reads must not throw. Also the compact route `POST /api/projects/{projectId}/notebooks/{notebookId}/conversations/{convoId}/compact` → `200` + `CompactionResultDto(int? BoundaryTurnIndex, int MessagesSummarized, int? EstimatedTokensBefore, int? EstimatedTokensAfter)`, and the send route `POST …/conversations/{convoId}/messages` (SSE). SSE error events are `event: error` (`StreamingEventTypes.Error`) with a JSON payload whose `code` is `chat_context_overflow` (`StreamingErrorEnvelope.cs:103`).
- Produces, for Task 3:
  - `internal static class ConversationStreamTestHelpers` with:
    - `Task<(Guid ProjectId, Guid NotebookId)> SeedProjectNotebookAsync(ApplicationDbContext db, string label)`
    - `Task<Guid> SeedConversationAsync(ApplicationDbContext db, Guid notebookId, string title)`
    - `Task<List<(string EventType, string Payload)>> SendMessageStreamAsync(HttpClient client, Guid projectId, Guid notebookId, Guid conversationId, object requestBody)`
    - `Task<Dictionary<Guid, string>> SnapshotMessageContentAsync(ApplicationDbContext db, Guid conversationId)`
    - `Task<int> LastCompletedTurnIndexAsync(ApplicationDbContext db, Guid conversationId)`
    - `const string HandoffFramingFragment = "condensed handoff briefing"`
  - `FakeChatScenario.OverflowAbovePromptBudget`, `FakeChatCompletionBehavior.PromptCharBudget` (`int`), `FakeChatCompletionBehavior.LastRequestToolNames` (`IReadOnlyList<string>?`), and `static int FakeChatCompletionBehavior.PromptChars(IEnumerable<ChatMessage>)`.

- [ ] **Step 1: Extract the shared helpers**

Create `src/server/GuideAntsApi.IntegrationTests/Infrastructure/ConversationStreamTestHelpers.cs`. The seeding and SSE code moves verbatim from `ToolLimitIntegrationTests.cs` (lines 208–292 at `7d6e8d7`), made static and parameterized:

```csharp
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.IntegrationTests.Infrastructure;

/// <summary>
/// Seeding and SSE helpers for tests that drive the real conversation streaming path
/// (fake or real chat provider, real SQL persistence).
/// </summary>
internal static class ConversationStreamTestHelpers
{
    /// <summary>Distinctive fragment of CompactionSummaryRenderer's framing line.</summary>
    public const string HandoffFramingFragment = "condensed handoff briefing";

    public static async Task<(Guid ProjectId, Guid NotebookId)> SeedProjectNotebookAsync(
        ApplicationDbContext db, string label)
    {
        var guideId = await db.Assistants
            .Where(a => a.Kind == AssistantKind.Guide && a.IsActive)
            .Select(a => a.Id)
            .FirstAsync();

        var project = new Project
        {
            Id = Guid.NewGuid(),
            Title = $"{label} Project {Guid.NewGuid():N}",
            Slug = $"it-{Guid.NewGuid():N}",
            Description = "integration",
            Created = DateTime.UtcNow
        };
        db.Projects.Add(project);

        var notebook = new Notebook
        {
            Id = Guid.NewGuid(),
            Title = $"{label} Notebook {Guid.NewGuid():N}",
            Slug = $"it-nb-{Guid.NewGuid():N}",
            ProjectId = project.Id,
            GuideId = guideId,
            Created = DateTime.UtcNow
        };
        db.Notebooks.Add(notebook);
        await db.SaveChangesAsync();
        return (project.Id, notebook.Id);
    }

    public static async Task<Guid> SeedConversationAsync(ApplicationDbContext db, Guid notebookId, string title)
    {
        var conv = new NotebookConversation { NotebookId = notebookId, Title = title };
        db.NotebookConversations.Add(conv);
        await db.SaveChangesAsync();
        return conv.Id;
    }

    public static async Task<List<(string EventType, string Payload)>> SendMessageStreamAsync(
        HttpClient client,
        Guid projectId,
        Guid notebookId,
        Guid conversationId,
        object requestBody)
    {
        using var req = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/projects/{projectId}/notebooks/{notebookId}/conversations/{conversationId}/messages")
        {
            Content = JsonContent.Create(requestBody)
        };
        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();

        var events = new List<(string EventType, string Payload)>();
        await using var stream = await resp.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream, Encoding.UTF8);

        string? currentEvent = null;
        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync();
            if (line == null)
            {
                break;
            }

            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                currentEvent = line["event:".Length..].Trim();
                continue;
            }

            if (line.StartsWith("data:", StringComparison.Ordinal) && currentEvent != null)
            {
                events.Add((currentEvent, line["data:".Length..].Trim()));
            }
        }

        return events;
    }

    /// <summary>Every persisted message's content, keyed by id -- the "nothing was mutated" baseline.</summary>
    public static Task<Dictionary<Guid, string>> SnapshotMessageContentAsync(ApplicationDbContext db, Guid conversationId) =>
        db.NotebookConversationMessages
            .AsNoTracking()
            .Where(m => m.NotebookConversationId == conversationId)
            .ToDictionaryAsync(m => m.Id, m => m.Content);

    /// <summary>The same rule CompactionService uses for the boundary (CompactionService.cs:104).</summary>
    public static Task<int> LastCompletedTurnIndexAsync(ApplicationDbContext db, Guid conversationId) =>
        db.ConversationTurns
            .AsNoTracking()
            .Where(t => t.NotebookConversationId == conversationId && t.Status == "completed")
            .MaxAsync(t => t.TurnIndex);
}
```

In `ToolLimitIntegrationTests.cs`:
- Delete the three private methods `SeedProjectNotebookAsync`, `SeedConversationAsync` and `SendConversationStreamToCompletionAsync`.
- Replace each `SeedProjectNotebookAsync(db)` with `ConversationStreamTestHelpers.SeedProjectNotebookAsync(db, "Tool Limit")`.
- Replace each `SeedConversationAsync(db, notebookId, "…")` with `ConversationStreamTestHelpers.SeedConversationAsync(db, notebookId, "…")`.
- Replace each `SendConversationStreamToCompletionAsync(projectId, notebookId, conversationId, body)` with `ConversationStreamTestHelpers.SendMessageStreamAsync(Client, projectId, notebookId, conversationId, body)`.
- Remove `using` directives the file no longer needs (`System.Net.Http.Headers`, `System.Net.Http.Json`, `System.Text`) only if the compiler flags them unused.

- [ ] **Step 2: Prove the refactor changed nothing**

Run, from `src/server`: `GA_INTEGRATION_TEST_MSSQL_IMAGE=ghcr.io/elumenotion/mssql2025-express-fts:main dotnet test GuideAntsApi.IntegrationTests/GuideAntsApi.IntegrationTests.csproj --filter "FullyQualifiedName~ToolLimitIntegrationTests"`
Expected: PASS, the same 3 tests as before.

- [ ] **Step 3: Add the overflow scenario to the fake provider**

In `src/server/GuideAntsApi.IntegrationTests/Infrastructure/FakeChatCompletionClientFactory.cs`:

Add the enum member at the end of `FakeChatScenario`:

```csharp
    NestedAgentBlocking,
    OverflowAbovePromptBudget,
}
```

Add these to `FakeChatCompletionBehavior`, below `LastRequestMessages`:

```csharp
    /// <summary>
    /// For <see cref="FakeChatScenario.OverflowAbovePromptBudget"/>: a streamed request whose messages total
    /// more characters than this is rejected with <see cref="ChatContextOverflowException"/>, as a provider
    /// rejecting an oversized prompt would be. At or under the budget the reply is the default one.
    /// </summary>
    public int PromptCharBudget { get; set; } = int.MaxValue;

    public IReadOnlyList<string>? LastRequestToolNames { get; private set; }

    public static int PromptChars(IEnumerable<ChatMessage> messages) =>
        messages.Sum(m => m.GetText()?.Length ?? 0);
```

In `Reset()`, add:

```csharp
        PromptCharBudget = int.MaxValue;
        LastRequestToolNames = null;
```

Replace `CaptureRequest` with:

```csharp
    public void CaptureRequest(ChatCompletionRequest request)
    {
        LastRequestMessages = request.Messages.ToList();
        LastRequestToolNames = (request.Tools ?? [])
            .Select(t => t.Function?.Name ?? string.Empty)
            .ToList();
    }
```

In `FakeChatCompletionClient.StreamCompletionAsync`'s `switch`, add before the `_ =>` arm:

```csharp
            FakeChatScenario.OverflowAbovePromptBudget => OverflowAbovePromptBudgetAsync(request, onChunk),
```

Add this method to `FakeChatCompletionClient`:

```csharp
    private Task<ChatCompletionResponse> OverflowAbovePromptBudgetAsync(
        ChatCompletionRequest request,
        Action<ChatCompletionChunk> onChunk)
    {
        var promptChars = FakeChatCompletionBehavior.PromptChars(request.Messages);
        if (promptChars > _behavior.PromptCharBudget)
        {
            throw new ChatContextOverflowException(
                "exceed_context_size_error: the request exceeds the available context size",
                promptTokens: promptChars / 4,
                contextSize: _behavior.PromptCharBudget / 4);
        }

        return DefaultStreamAsync(onChunk);
    }
```

`GetCompletionAsync` (non-streaming; used by title generation) deliberately stays budget-free, so title generation never trips the overflow.

- [ ] **Step 4: Write the end-to-end tests**

Create `src/server/GuideAntsApi.IntegrationTests/Services/Conversations/CompactionEndToEndTests.cs`:

```csharp
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
```

- [ ] **Step 5: Run the new tests**

Run, from `src/server`: `GA_INTEGRATION_TEST_MSSQL_IMAGE=ghcr.io/elumenotion/mssql2025-express-fts:main dotnet test GuideAntsApi.IntegrationTests/GuideAntsApi.IntegrationTests.csproj --filter "FullyQualifiedName~CompactionEndToEndTests"`
Expected: PASS, 2/2.

A failure here is a real finding about the assembled feature, not a test to adjust. Write the exact failing assertion and its message into the task report before touching anything. Two failures to recognize:
- `BoundaryTurnIndex` equals the failed turn: the compact rule is including failed turns.
- The phase 4 request lacks `conversation_recall`: the first send after compaction is taking the stale-boundary fallback.

For either, stop and report; do not change production code (Global Constraints).

- [ ] **Step 6: Commit (ask the user first)**

```bash
git add src/server/GuideAntsApi.IntegrationTests/Infrastructure/FakeChatCompletionClientFactory.cs \
        src/server/GuideAntsApi.IntegrationTests/Infrastructure/ConversationStreamTestHelpers.cs \
        src/server/GuideAntsApi.IntegrationTests/Services/Conversations/ToolLimitIntegrationTests.cs \
        src/server/GuideAntsApi.IntegrationTests/Services/Conversations/CompactionEndToEndTests.cs
git commit -m "test(compaction): end-to-end overflow, compact, continue and recall

Drives the real HTTP, ConversationService, ThreadRun and SQL Server path
with a fake provider that rejects prompts over a character budget. Covers
the first send after a real (non-stale) compaction, a boundary that skips
the failed overflow turn, recall returning replaced content, and an
over-window message after compaction failing without mutating history.
Seeding and SSE helpers move out of ToolLimitIntegrationTests so both
suites share them."
```

---

## Task 3: Opt-in end-to-end run against a real llama.cpp server (9.3, real-model half)

**Files:**
- Create: `src/server/GuideAntsApi.IntegrationTests/Infrastructure/LlamaCompactionWebApplicationFactory.cs`
- Create: `src/server/GuideAntsApi.IntegrationTests/Services/Conversations/CompactionLlamaCppEndToEndTests.cs`
- Create: `docs/compaction-llamacpp-e2e-runbook.md`

**Interfaces:**
- Consumes: `ConversationStreamTestHelpers` (Task 2); `LlamaCppChatClient(HttpClient httpClient, LlamaCppConfig config, string? deploymentId, …)` (`AntRunner.Chat.LlamaCpp`), which posts to `{BaseUrl}/v1/chat/completions`; `LlamaCppConfig { BaseUrl, TimeoutSeconds }`; `TestWebApplicationFactory` (its `ConfigureWebHost` is overridable); the protected static `BaseIntegrationTest.SharedFactory`.
- Produces: nothing downstream.

- [ ] **Step 1: Write the factory and recording client**

Create `src/server/GuideAntsApi.IntegrationTests/Infrastructure/LlamaCompactionWebApplicationFactory.cs`:

```csharp
using AntRunner.Chat.Abstractions;
using AntRunner.Chat.LlamaCpp;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GuideAntsApi.IntegrationTests.Infrastructure;

/// <summary>
/// Same host as <see cref="TestWebApplicationFactory"/> except chat completions go to a real llama-server,
/// through the production <see cref="LlamaCppChatClient"/> (so real overflow bodies get classified).
/// </summary>
internal sealed class LlamaCompactionWebApplicationFactory : TestWebApplicationFactory
{
    public RecordingChatCompletionClient Recorder { get; }

    public LlamaCompactionWebApplicationFactory(string baseUrl, string modelId)
    {
        var inner = new LlamaCppChatClient(
            new HttpClient { Timeout = TimeSpan.FromMinutes(5) },
            new LlamaCppConfig { BaseUrl = baseUrl, TimeoutSeconds = 300 },
            modelId);
        Recorder = new RecordingChatCompletionClient(inner);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IChatCompletionClientFactory>();
            services.AddSingleton<IChatCompletionClientFactory>(new SingleClientFactory(Recorder));
        });
    }

    private sealed class SingleClientFactory(IChatCompletionClient client) : IChatCompletionClientFactory
    {
        public string? DefaultDeploymentId => "llama-e2e";
        public IChatCompletionClient CreateClient(string? deploymentId, HttpClient? httpClient = null) => client;
    }
}

/// <summary>Delegating client that remembers the last request, so tests can see what the model was offered.</summary>
internal sealed class RecordingChatCompletionClient(IChatCompletionClient inner) : IChatCompletionClient
{
    public IReadOnlyList<ChatMessage>? LastRequestMessages { get; private set; }
    public IReadOnlyList<string>? LastRequestToolNames { get; private set; }

    public bool SupportsToolChoiceNone => inner.SupportsToolChoiceNone;

    public Task<ChatCompletionResponse> GetCompletionAsync(
        ChatCompletionRequest request, CancellationToken cancellationToken = default)
    {
        Record(request);
        return inner.GetCompletionAsync(request, cancellationToken);
    }

    public Task<ChatCompletionResponse> StreamCompletionAsync(
        ChatCompletionRequest request, Action<ChatCompletionChunk> onChunk, CancellationToken cancellationToken = default)
    {
        Record(request);
        return inner.StreamCompletionAsync(request, onChunk, cancellationToken);
    }

    private void Record(ChatCompletionRequest request)
    {
        LastRequestMessages = request.Messages.ToList();
        LastRequestToolNames = (request.Tools ?? []).Select(t => t.Function?.Name ?? string.Empty).ToList();
    }
}
```

- [ ] **Step 2: Write the gated test**

Create `src/server/GuideAntsApi.IntegrationTests/Services/Conversations/CompactionLlamaCppEndToEndTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using AntRunner.Chat.Abstractions;
using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.IntegrationTests.Infrastructure;
using GuideAntsApi.Models.Conversations;
using Microsoft.Extensions.DependencyInjection;

namespace GuideAntsApi.IntegrationTests.Services.Conversations;

/// <summary>
/// Spec "Integration": the compaction story against a real small-context llama.cpp. Opt-in -- set
/// GA_COMPACTION_E2E_LLAMA_URL (see docs/compaction-llamacpp-e2e-runbook.md). Without it every test here
/// is Inconclusive, never Passed. Asserts that recall is offered, not that a small model chooses to call
/// it; CompactionEndToEndTests proves recall executes.
/// </summary>
[TestClass]
[TestCategory("LocalLlamaE2E")]
public sealed class CompactionLlamaCppEndToEndTests : BaseEndpointTest
{
    private const string BaseUrlEnv = "GA_COMPACTION_E2E_LLAMA_URL";
    private const string ModelEnv = "GA_COMPACTION_E2E_LLAMA_MODEL";
    private const int MaxTurnsBeforeOverflow = 40;
    private const int PaddingChars = 1500;

    private static LlamaCompactionWebApplicationFactory? _llamaFactory;

    [ClassInitialize]
    public static async Task ClassInitialize(TestContext context)
    {
        var baseUrl = Environment.GetEnvironmentVariable(BaseUrlEnv);
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return;
        }

        _llamaFactory = new LlamaCompactionWebApplicationFactory(
            baseUrl, Environment.GetEnvironmentVariable(ModelEnv) ?? "local");
        SharedFactory = _llamaFactory;
        await SharedFactory.InitializeAsync();
    }

    [ClassCleanup]
    public static async Task ClassCleanup()
    {
        await DisposeSharedFactoryAsync();
        _llamaFactory = null;
    }

    [TestInitialize]
    public override async Task BaseTestInitialize()
    {
        if (_llamaFactory == null)
        {
            Assert.Inconclusive(
                $"Set {BaseUrlEnv} (and optionally {ModelEnv}) to a llama-server started per " +
                "docs/compaction-llamacpp-e2e-runbook.md to run this test.");
        }

        await base.BaseTestInitialize();
    }

    [TestMethod]
    public async Task RealLlamaCpp_Overflows_Compacts_Continues_AndOffersRecall()
    {
        Guid projectId, notebookId, conversationId;
        using (var scope = SharedFactory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (projectId, notebookId) = await ConversationStreamTestHelpers.SeedProjectNotebookAsync(db, "Llama E2E");
            conversationId = await ConversationStreamTestHelpers.SeedConversationAsync(db, notebookId, "Llama E2E");
        }

        Task<List<(string EventType, string Payload)>> Send(string text) =>
            ConversationStreamTestHelpers.SendMessageStreamAsync(
                Client, projectId, notebookId, conversationId, new { instructions = text, assistantName = "assistant" });

        // Grow the conversation until the real server rejects it.
        Dictionary<Guid, string>? beforeOverflow = null;
        var overflowed = false;
        for (var turn = 1; turn <= MaxTurnsBeforeOverflow && !overflowed; turn++)
        {
            using (var scope = SharedFactory!.Services.CreateScope())
            {
                beforeOverflow = await ConversationStreamTestHelpers.SnapshotMessageContentAsync(
                    scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(), conversationId);
            }

            var events = await Send($"Note {turn}: " + new string('x', PaddingChars) + " Reply with the single word OK.");
            var error = events.FirstOrDefault(e => e.EventType == StreamingEventTypes.Error);
            if (error != default)
            {
                error.Payload.Should().Contain("chat_context_overflow",
                    "the only acceptable failure while growing the conversation is a classified overflow");
                overflowed = true;
            }
        }

        overflowed.Should().BeTrue(
            $"a 2048-token window must overflow within {MaxTurnsBeforeOverflow} padded turns; if not, the " +
            "server is running with a larger -c or with context shift enabled (see the runbook)");

        using (var scope = SharedFactory!.Services.CreateScope())
        {
            var after = await ConversationStreamTestHelpers.SnapshotMessageContentAsync(
                scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(), conversationId);
            foreach (var (id, content) in beforeOverflow!)
            {
                after[id].Should().Be(content, "D5: overflow must never rewrite a stored message");
            }
        }

        var compactResponse = await Client.PostAsync(
            $"/api/projects/{projectId}/notebooks/{notebookId}/conversations/{conversationId}/compact", content: null);
        compactResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        (await compactResponse.Content.ReadFromJsonAsync<CompactionResultDto>())!
            .BoundaryTurnIndex.Should().NotBeNull();

        var continued = await Send("Reply with the single word OK.");
        continued.Should().NotContain(e => e.EventType == StreamingEventTypes.Error,
            "after compaction the same model must accept the next turn");
        continued.Should().Contain(e => e.EventType == StreamingEventTypes.Complete);

        var recorder = _llamaFactory!.Recorder;
        string.Join("\n", recorder.LastRequestMessages!.Select(m => m.GetText()))
            .Should().Contain(ConversationStreamTestHelpers.HandoffFramingFragment);
        recorder.LastRequestToolNames.Should().Contain("conversation_recall");
    }
}
```

- [ ] **Step 3: Confirm it reports Inconclusive when unconfigured**

Run, from `src/server`, with `GA_COMPACTION_E2E_LLAMA_URL` **unset**: `GA_INTEGRATION_TEST_MSSQL_IMAGE=ghcr.io/elumenotion/mssql2025-express-fts:main dotnet test GuideAntsApi.IntegrationTests/GuideAntsApi.IntegrationTests.csproj --filter "FullyQualifiedName~CompactionLlamaCppEndToEndTests"`
Expected: `Skipped: 1` (MSTest counts Inconclusive as skipped), `Failed: 0`. This is also what CI will do.

- [ ] **Step 4: Write the runbook**

Create `docs/compaction-llamacpp-e2e-runbook.md`:

````markdown
# Running the compaction end-to-end test against a real llama.cpp

`CompactionLlamaCppEndToEndTests` exercises the path the hermetic suite cannot: llama-server's real
`exceed_context_size_error` response, classified by `LlamaCppChatClient`, surfacing as `chat_context_overflow`.
It is opt-in and skipped (Inconclusive) unless `GA_COMPACTION_E2E_LLAMA_URL` is set.

This starts a **separate, throwaway** llama-server on host port 8199. It does not touch the `guideants-ai`
container.

## 1. Get a small model (~400 MB, once)

```bash
mkdir -p ~/models
curl -L -o ~/models/qwen2.5-0.5b-instruct-q4_k_m.gguf \
  https://huggingface.co/Qwen/Qwen2.5-0.5B-Instruct-GGUF/resolve/main/qwen2.5-0.5b-instruct-q4_k_m.gguf
```

## 2. Start llama-server with a 2048-token window

```bash
docker run --rm -d --name ga-compaction-e2e -p 8199:8080 -v ~/models:/models \
  ghcr.io/ggml-org/llama.cpp:server \
  -m /models/qwen2.5-0.5b-instruct-q4_k_m.gguf -c 2048 --host 0.0.0.0 --port 8080 --jinja
```

`--jinja` lets the server accept OpenAI-style `tools`. Do not pass `--context-shift`: with it the server
silently drops old tokens instead of rejecting, and the test can never see an overflow.

## 3. Verify the server rejects an oversized prompt (the test depends on this)

```bash
python3 -c "import json;print(json.dumps({'messages':[{'role':'user','content':'x '*6000}]}))" \
  | curl -s -o /dev/stderr -w '%{http_code}\n' -H 'Content-Type: application/json' \
      -d @- http://localhost:8199/v1/chat/completions
```

Expected: HTTP `400`, with a body containing `exceed_context_size_error`. If you get `200`, the build has
context shift on by default: restart with `--no-context-shift` added.

## 4. Run the test

From `src/server`:

```bash
GA_INTEGRATION_TEST_MSSQL_IMAGE=ghcr.io/elumenotion/mssql2025-express-fts:main \
GA_COMPACTION_E2E_LLAMA_URL=http://localhost:8199 \
GA_COMPACTION_E2E_LLAMA_MODEL=qwen2.5-0.5b-instruct \
  dotnet test GuideAntsApi.IntegrationTests/GuideAntsApi.IntegrationTests.csproj \
  --filter "FullyQualifiedName~CompactionLlamaCppEndToEndTests"
```

Expected: `Passed: 1`. A CPU-only run takes a few minutes.

## 5. Clean up

```bash
docker stop ga-compaction-e2e
```
````

- [ ] **Step 5: Run it for real, if the user has approved the download and the container**

This step needs a ~400 MB download and a new container on the user's machine, so **ask before doing it**. If approved, follow the runbook's steps 1–4 and record the full test output in the report. If declined or impossible, write "Task 3 Step 5 not run: <reason>" in the report. Do **not** describe the test as passing.

- [ ] **Step 6: Commit (ask the user first)**

```bash
git add src/server/GuideAntsApi.IntegrationTests/Infrastructure/LlamaCompactionWebApplicationFactory.cs \
        src/server/GuideAntsApi.IntegrationTests/Services/Conversations/CompactionLlamaCppEndToEndTests.cs \
        docs/compaction-llamacpp-e2e-runbook.md
git commit -m "test(compaction): opt-in end-to-end run against a real llama.cpp

Same story as the hermetic test but through the production LlamaCppChatClient,
so llama-server's real exceed_context_size_error response is classified end to
end. Inconclusive unless GA_COMPACTION_E2E_LLAMA_URL is set; the runbook
starts a throwaway 2048-token llama-server on port 8199."
```

---

## Task 4: Cross-domain benchmark harness (9.1, tooling half)

**Files:**
- Modify: `src/server/GuideAntsApi/Services/Conversations/Mapping/ConversationHistoryBuilder.cs:223` (`private` → `internal`)
- Modify: `.gitignore`
- Create: `src/server/GuideAntsApi.Tests/Benchmarks/CompactionBenchmarkRunner.cs`
- Test: `src/server/GuideAntsApi.Tests/Benchmarks/CompactionBenchmarkTests.cs`

**Interfaces:**
- Consumes: `ConversationHistoryBuilder(IServiceScopeFactory, IContextOptionsService, IAttachmentContentService, ILogger<ConversationHistoryBuilder>)` and `ConversationHistoryBuilder.BuildCompactionSummaryMessageAsync(Guid conversationId, int boundaryTurnIndex, CancellationToken)` → `ChatMessage` (made `internal` here). The summary method uses only the scope factory; the other three dependencies can be mocks.
- Produces, for Task 5:
  - `internal sealed record BenchmarkCandidate(Guid ConversationId, string ShortId, int BoundaryTurnIndex, int CompletedTurns, string Domain, IReadOnlyList<string> ToolNames)`
  - `internal sealed record BenchmarkEntry(BenchmarkCandidate Candidate, int PreBoundaryChars, string SummaryText, IReadOnlyDictionary<string, int> SectionLineCounts, int UnknownOutcomeLines, bool Deterministic)`
  - `internal static class CompactionBenchmarkRunner` with `SelectCandidatesAsync`, `RunAsync`, `CountSectionLines`, `RenderConversationReport` and `RenderIndex`.
  - An output folder, `src/server/.compaction-benchmark/`, holding `index.md` plus one `<shortId>.md` per conversation.

- [ ] **Step 1: Make the production summary path reachable from tests**

In `ConversationHistoryBuilder.cs`, change only the modifier at line 223:

```csharp
    internal async Task<ChatMessage> BuildCompactionSummaryMessageAsync(
        Guid conversationId, int boundaryTurnIndex, CancellationToken cancellationToken)
```

Append one sentence to its existing `<summary>`: `Internal (not private) so the W9 benchmark measures this exact path rather than a re-implementation.`

- [ ] **Step 2: Git-ignore the output folder**

Append to `.gitignore`, beside the existing `src/server/TestResults/` entry:

```gitignore
# W9 compaction benchmark output -- quotes real transcript content, never commit
src/server/.compaction-benchmark/
```

- [ ] **Step 3: Write the failing harness tests**

Create `src/server/GuideAntsApi.Tests/Benchmarks/CompactionBenchmarkTests.cs`:

```csharp
using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services.Conversations;
using GuideAntsApi.Services.Conversations.Mapping;
using GuideAntsApi.Tests.Benchmarks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using DataModelChatRole = GuideAntsApi.DataModel.Models.ChatRole;

namespace GuideAntsApi.Tests.Benchmarks;

[TestClass]
public sealed class CompactionBenchmarkTests
{
    private const string ConnectionEnv = "GA_COMPACTION_BENCHMARK_DB";
    private const string OutputEnv = "GA_COMPACTION_BENCHMARK_OUT";

    private static IServiceProvider InMemory(string name)
    {
        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(o => o.UseInMemoryDatabase(name));
        return services.BuildServiceProvider();
    }

    private static void AddConversation(
        ApplicationDbContext db, Guid id, IEnumerable<(int Index, string Status)> turns, params string[] toolNames)
    {
        db.NotebookConversations.Add(new NotebookConversation { Id = id, NotebookId = Guid.NewGuid(), Title = "t" });
        foreach (var (index, status) in turns)
        {
            db.ConversationTurns.Add(new ConversationTurn
            {
                NotebookConversationId = id, TurnIndex = index, AssistantName = "a", Instructions = "i", Status = status
            });
            db.NotebookConversationMessages.Add(new NotebookConversationMessage
            {
                NotebookConversationId = id, TurnIndex = index, MessageSequence = 0,
                Role = DataModelChatRole.User, Content = $"message {index}"
            });
        }

        var seq = 1;
        foreach (var tool in toolNames)
        {
            db.NotebookConversationMessages.Add(new NotebookConversationMessage
            {
                NotebookConversationId = id, TurnIndex = 1, MessageSequence = seq++,
                Role = DataModelChatRole.Tool, FunctionName = tool, ToolCallId = $"c{seq}", Content = "{}"
            });
        }
    }

    [TestMethod]
    public async Task SelectCandidatesAsync_SkipsConversationsBelowTheCompletedTurnFloor()
    {
        var sp = InMemory(nameof(SelectCandidatesAsync_SkipsConversationsBelowTheCompletedTurnFloor));
        var db = sp.GetRequiredService<ApplicationDbContext>();
        var longOne = Guid.NewGuid();
        var failedOnly = Guid.NewGuid();
        var none = Guid.NewGuid();
        AddConversation(db, longOne, Enumerable.Range(1, 6).Select(i => (i, "completed")));
        AddConversation(db, failedOnly, Enumerable.Range(1, 6).Select(i => (i, "failed")));
        AddConversation(db, none, []);
        await db.SaveChangesAsync();

        var candidates = await CompactionBenchmarkRunner.SelectCandidatesAsync(db, minCompletedTurns: 5, CancellationToken.None);

        candidates.Select(c => c.ConversationId).Should().Equal(longOne);
    }

    [TestMethod]
    public async Task SelectCandidatesAsync_BoundaryIsTheLastCompletedTurn_IgnoringLaterFailedTurns()
    {
        var sp = InMemory(nameof(SelectCandidatesAsync_BoundaryIsTheLastCompletedTurn_IgnoringLaterFailedTurns));
        var db = sp.GetRequiredService<ApplicationDbContext>();
        var id = Guid.NewGuid();
        AddConversation(db, id, Enumerable.Range(1, 5).Select(i => (i, "completed")).Append((6, "failed")));
        await db.SaveChangesAsync();

        var candidate = (await CompactionBenchmarkRunner.SelectCandidatesAsync(db, 5, CancellationToken.None)).Single();

        candidate.BoundaryTurnIndex.Should().Be(5);
        candidate.CompletedTurns.Should().Be(5);
    }

    [TestMethod]
    public async Task SelectCandidatesAsync_ClassifiesCodingByToolName_IncludingCrewCodeExecutor()
    {
        var sp = InMemory(nameof(SelectCandidatesAsync_ClassifiesCodingByToolName_IncludingCrewCodeExecutor));
        var db = sp.GetRequiredService<ApplicationDbContext>();
        var crew = Guid.NewGuid();
        var search = Guid.NewGuid();
        var chat = Guid.NewGuid();
        AddConversation(db, crew, Enumerable.Range(1, 5).Select(i => (i, "completed")), "Code_Executor");
        AddConversation(db, search, Enumerable.Range(1, 5).Select(i => (i, "completed")), "SearchAssistantFiles");
        AddConversation(db, chat, Enumerable.Range(1, 5).Select(i => (i, "completed")));
        await db.SaveChangesAsync();

        var byId = (await CompactionBenchmarkRunner.SelectCandidatesAsync(db, 5, CancellationToken.None))
            .ToDictionary(c => c.ConversationId);

        byId[crew].Domain.Should().Be("coding");
        byId[search].Domain.Should().Be("non-coding");
        byId[chat].Domain.Should().Be("non-coding");
        byId[search].ToolNames.Should().Equal("SearchAssistantFiles");
    }

    [TestMethod]
    public void CountSectionLines_IgnoresPlaceholdersAndCountsUnknownOutcomes()
    {
        const string summary =
            "framing\n\n[Compacted 4 earlier message(s).]\n\n## Goal\nBuild a report\n\n## Artifacts\n(none)\n\n" +
            "## Activity ledger\n- search_notebook q — unknown\n- run_python — ok\n\n## Unresolved errors\n(none)\n\n" +
            "## Directives\n(none)";

        var counts = CompactionBenchmarkRunner.CountSectionLines(summary);

        counts["Goal"].Should().Be(1);
        counts["Artifacts"].Should().Be(0);
        counts["Activity ledger"].Should().Be(2);
        counts["Unresolved errors"].Should().Be(0);
        counts["Directives"].Should().Be(0);
        CompactionBenchmarkRunner.CountUnknownOutcomes(summary).Should().Be(1);
    }

    [TestMethod]
    public async Task RunAsync_UsesTheProductionSummaryPath_AndIsDeterministic()
    {
        var name = nameof(RunAsync_UsesTheProductionSummaryPath_AndIsDeterministic);
        var sp = InMemory(name);
        var id = Guid.NewGuid();
        using (var scope = sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            AddConversation(db, id, Enumerable.Range(1, 5).Select(i => (i, "completed")));
            await db.SaveChangesAsync();
        }

        var builder = new ConversationHistoryBuilder(
            sp.GetRequiredService<IServiceScopeFactory>(),
            Mock.Of<IContextOptionsService>(),
            Mock.Of<IAttachmentContentService>(),
            NullLogger<ConversationHistoryBuilder>.Instance);

        using var readScope = sp.CreateScope();
        var readDb = readScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var candidate = (await CompactionBenchmarkRunner.SelectCandidatesAsync(readDb, 5, CancellationToken.None)).Single();

        var entry = await CompactionBenchmarkRunner.RunAsync(builder, readDb, candidate, CancellationToken.None);

        entry.SummaryText.Should().Contain("## Goal").And.Contain("message 1");
        entry.Deterministic.Should().BeTrue("spec Testing item 2: identical input yields byte-identical output");
        entry.PreBoundaryChars.Should().Be(Enumerable.Range(1, 5).Sum(i => $"message {i}".Length));
        CompactionBenchmarkRunner.RenderConversationReport(entry).Should().Contain(candidate.ShortId);
    }

    [TestMethod]
    [TestCategory("Benchmark")]
    public async Task Benchmark_RealTranscripts_WritesReports()
    {
        var connection = Environment.GetEnvironmentVariable(ConnectionEnv);
        if (string.IsNullOrWhiteSpace(connection))
        {
            Assert.Inconclusive($"Set {ConnectionEnv} to a SQL Server connection string (read-only use) to run the benchmark.");
        }

        var output = Environment.GetEnvironmentVariable(OutputEnv)
            ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".compaction-benchmark"));
        Directory.CreateDirectory(output);

        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(o => o.UseSqlServer(connection));
        var sp = services.BuildServiceProvider();
        var builder = new ConversationHistoryBuilder(
            sp.GetRequiredService<IServiceScopeFactory>(),
            Mock.Of<IContextOptionsService>(),
            Mock.Of<IAttachmentContentService>(),
            NullLogger<ConversationHistoryBuilder>.Instance);

        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var candidates = await CompactionBenchmarkRunner.SelectCandidatesAsync(db, minCompletedTurns: 5, CancellationToken.None);
        candidates.Should().NotBeEmpty($"{ConnectionEnv} points at a database with no conversation of 5+ completed turns");

        var entries = new List<BenchmarkEntry>();
        foreach (var candidate in candidates)
        {
            var entry = await CompactionBenchmarkRunner.RunAsync(builder, db, candidate, CancellationToken.None);
            entries.Add(entry);
            await File.WriteAllTextAsync(
                Path.Combine(output, $"{candidate.ShortId}.md"), CompactionBenchmarkRunner.RenderConversationReport(entry));
        }

        await File.WriteAllTextAsync(Path.Combine(output, "index.md"), CompactionBenchmarkRunner.RenderIndex(entries));

        entries.Should().OnlyContain(e => e.Deterministic, "spec Testing item 2 must hold on real transcripts too");
    }
}
```

- [ ] **Step 4: Run to verify it fails**

Run, from `src/server`: `dotnet test GuideAntsApi.Tests/GuideAntsApi.Tests.csproj --filter "FullyQualifiedName~CompactionBenchmarkTests"`
Expected: build failure, since `CompactionBenchmarkRunner` does not exist.

- [ ] **Step 5: Write the runner**

Create `src/server/GuideAntsApi.Tests/Benchmarks/CompactionBenchmarkRunner.cs`:

```csharp
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
```

- [ ] **Step 6: Run the harness tests**

Run, from `src/server`: `dotnet test GuideAntsApi.Tests/GuideAntsApi.Tests.csproj --filter "FullyQualifiedName~CompactionBenchmarkTests"`
Expected: 5 passed, 1 skipped. The skipped one is `Benchmark_RealTranscripts_WritesReports`, Inconclusive with `GA_COMPACTION_BENCHMARK_DB` unset.

- [ ] **Step 7: Prove the output folder is ignored**

Run, from the repo root:

```bash
mkdir -p src/server/.compaction-benchmark && touch src/server/.compaction-benchmark/probe.md
git check-ignore -v src/server/.compaction-benchmark/probe.md
rm src/server/.compaction-benchmark/probe.md
```

Expected: `git check-ignore` prints the `.gitignore` line that matched. Empty output is a failure: stop and fix the `.gitignore` entry.

- [ ] **Step 8: Commit (ask the user first)**

```bash
git add .gitignore \
        src/server/GuideAntsApi/Services/Conversations/Mapping/ConversationHistoryBuilder.cs \
        src/server/GuideAntsApi.Tests/Benchmarks/CompactionBenchmarkRunner.cs \
        src/server/GuideAntsApi.Tests/Benchmarks/CompactionBenchmarkTests.cs
git commit -m "test(compaction): cross-domain benchmark harness

Runs the production compaction summary path over real conversations and
writes per-conversation markdown reports for a human read (spec Testing
item 5). Env-gated on GA_COMPACTION_BENCHMARK_DB; output goes to a
git-ignored folder because it quotes real transcripts.
BuildCompactionSummaryMessageAsync becomes internal so the benchmark
measures the real path, not a copy."
```

---

## Task 5: Run the benchmark and write the findings (9.1, judgment half)

**Files:**
- Create: `docs/compaction-benchmark-findings.md`

**Interfaces:**
- Consumes: Task 4's harness, and the local SQL Server's `guideants` database (the only one here with conversations).
- Produces: a verdict that W10's ADR for D2 cites.

This task is judgment, not code. Its output is a claim about whether the feature works outside sandbox guides, so **the user reviews the drafted verdicts before commit**.

- [ ] **Step 1 (optional, ask the user): widen the non-coding corpus**

The non-coding half is three small transcripts (see *Findings*), with no notebook producing documents, images or audio. Ask whether to add conversations first. If yes, the user creates, in the running app, **at least two** new non-coding conversations of **6+ turns** each, from domains not yet covered. Examples:
- Q&A over an uploaded document
- A notebook that generates images or a podcast

That needs a working chat model in the app. Record which conversations were added (first 8 id characters and domain). If the user declines, record "corpus not widened" — Step 5 carries it as a stated limitation.

- [ ] **Step 2: Run the benchmark against the dev database (read-only)**

From `src/server`, build the connection string from the local container and run the gated test:

```bash
PW=$(python3 -c "import json,re;s=json.load(open('GuideAntsApi/appsettings.Development.json'))['ConnectionStrings']['DefaultConnection'];print(re.search(r'Password=([^;]*)',s).group(1))")
GA_COMPACTION_BENCHMARK_DB="Server=localhost,1434;Initial Catalog=guideants;User ID=sa;Password=$PW;Encrypt=False;TrustServerCertificate=True;ApplicationIntent=ReadOnly" \
  dotnet test GuideAntsApi.Tests/GuideAntsApi.Tests.csproj --filter "FullyQualifiedName~Benchmark_RealTranscripts_WritesReports"
```

Expected: `Passed: 1`, with `src/server/.compaction-benchmark/index.md` plus one `<shortId>.md` per conversation of ≥5 completed turns. Never paste the password into the report or the findings document.

- [ ] **Step 3: Check that the run is enough to judge**

Open `index.md`. The run counts only if it holds **≥2 coding and ≥2 non-coding** conversations. If a domain has fewer, do not draft a verdict for it. Write "insufficient corpus: N conversations" in its place and go to Step 5.

- [ ] **Step 4: Read every report against the rubric**

For each `<shortId>.md`, answer these six questions. Look at the underlying conversation read-only where needed, for example via the app's UI or `NotebookConversationMessages`. Each answer is one word from the allowed set plus one line of reason:

| # | Question | Allowed answers |
|---|---|---|
| R1 | **Goal:** does the Goal line state what the user was trying to do? | yes / partial / no |
| R2 | **Artifacts:** do the listed files match what the conversation produced? `(none)` is correct for a conversation that produced none. | yes / partial / no / n/a |
| R3 | **Activity ledger:** would a reader know what was done? Note the share of `unknown` outcomes. | yes / partial / no / n/a |
| R4 | **Unresolved errors:** any listed error that was later fixed (false positive), or a live error missing (false negative)? | clean / false-positive / false-negative |
| R5 | **Directives:** are real user preferences captured? Any false positives, such as an ordinary "don't" in prose? | clean / missed / false-positive / n/a |
| R6 | **Overall:** could a model resuming from this summary, with recall available, continue the work? | useful / partial / misleading |

- [ ] **Step 5: Write the findings document**

Create `docs/compaction-benchmark-findings.md` with exactly these sections:

```markdown
# Compaction benchmark — findings (W9 / spec Testing item 5)

Date: <YYYY-MM-DD>. Engine at commit <short sha>. Corpus: `guideants` dev database<, plus N conversations added for this run>.

## Corpus

| Conversation | Domain | Completed turns | Tools | Pre-boundary chars → summary chars |
|---|---|---|---|---|
<one row per conversation, first 8 id characters only>

Coverage gaps: <domains not represented, e.g. document / image / audio notebooks; or "none">.

## Per-conversation verdicts

| Conversation | R1 Goal | R2 Artifacts | R3 Ledger | R4 Errors | R5 Directives | R6 Overall | Evidence (paraphrased) |
|---|---|---|---|---|---|---|---|

## By domain

- Coding: <one paragraph; counts of useful/partial/misleading>
- Non-coding: <one paragraph; counts of useful/partial/misleading>

## Verdict on D2's accepted risk

<Exactly one of:
 (a) "Not materialized": no non-coding conversation rated misleading, and at most one rated partial because of empty sections.
 (b) "Partially materialized": non-coding summaries are mostly partial -- sections empty, not wrong -- and recall is the mitigation.
 (c) "Materialized": at least one non-coding conversation rated misleading.
 Then one paragraph of reasons.>

## Carried to W10

<Anything the ADR for D2 must mention; any engine change this suggests, as a follow-up, not done here.>
```

Rules for the document:
- No verbatim transcript text; paraphrase evidence.
- Conversations are named by 8-character ids only.
- Every verdict cell uses the rubric's allowed words.

- [ ] **Step 6: Get the user's sign-off, then commit**

Show the user the *Verdict on D2's accepted risk* section and the per-conversation table. Change verdicts they dispute. Record in the document that they signed off, for example "Reviewed by the project owner on <date>". Then, with permission:

```bash
git add docs/compaction-benchmark-findings.md
git commit -m "docs(compaction): cross-domain benchmark findings

Reads the engine's summaries of real coding and non-coding conversations
against a fixed rubric and records the verdict on D2's accepted domain-fit
risk. Transcript text is paraphrased; raw reports stay in the git-ignored
.compaction-benchmark folder."
```

---

## Task 6: Mirror CI, classify every failure against `main`, and close W9 (9.2, 9.4)

**Files:**
- Modify: `docs/context-compaction-plan.md`

**Interfaces:**
- Consumes: Tasks 1–5.
- Produces: the W9 section ticked; a failure table; W10 carry-overs.

- [ ] **Step 1: Start from a clean tree**

Run, from the repo root: `git status --short`
Expected: only the untracked files that predate this plan (`CLAUDE.md`, `guideants-swagger.json`). Anything else means stop: commit it (with permission) or ask.

- [ ] **Step 2: Run CI's `server-unit-test` job**

From `src/server`:

```bash
dotnet restore GuideAntsApi.sln
dotnet test GuideAntsApi.Tests/GuideAntsApi.Tests.csproj --configuration Release --verbosity minimal
dotnet test ScriptExecutionAgent.Tests/ScriptExecutionAgent.Tests.csproj --configuration Release --verbosity minimal
```

Record each project's pass/fail/skip counts and every failing test's full name.

- [ ] **Step 3: Run CI's `server-integration-test` job**

From `src/server`:

```bash
GA_INTEGRATION_TEST_MSSQL_IMAGE=ghcr.io/elumenotion/mssql2025-express-fts:main GA_ENABLE_RUNTIME_LOAD_TESTS=0 \
  dotnet test GuideAntsApi.IntegrationTests/GuideAntsApi.IntegrationTests.csproj --configuration Release --verbosity minimal
```

Record the counts and every failing test's full name. Expected: `CompactionEndToEndTests` passes, `CompactionLlamaCppEndToEndTests` is skipped, and no failure mentions `estimateSource`.

- [ ] **Step 4: Run CI's `client-test` job under Node 22**

From `src/client`:

```bash
npx -y -p node@22 npm ci
npx -y -p node@22 npm run test:coverage
```

Record the counts, every failing test file, and the coverage summary line.

- [ ] **Step 5: Classify every failure against `main`**

Skip this step if Steps 2–4 had no failures. Otherwise:

1. `git status --short` must match Step 1. Then `git switch main`.
2. For server failures, run *only* the failing tests on `main`, using the same command as before with `--filter "FullyQualifiedName~<TestA>|FullyQualifiedName~<TestB>"`.
3. For client failures, run `npx -y -p node@22 npm ci`, then `npx -y -p node@22 npx vitest run <failing files>`.
4. `git switch feature/compaction`, then rerun `npx -y -p node@22 npm ci` in `src/client` to restore the branch's dependencies.
5. `git status --short` must again match Step 1.

Fill this table in the task report:

| Job | Test | Fails on branch | Fails on `main` | Classification |
|---|---|---|---|---|

Classifications:
- **pre-existing:** fails on both.
- **branch-caused:** fails only on the branch.
- **flaky:** passes on a single rerun on the branch. Say how many reruns.

**Any branch-caused failure stops W9.** Do not tick 9.2 or 9.4. Report the failure to the user with its first error line, since the fix is out of this plan's scope and needs its own decision.

- [ ] **Step 6: Update the checklist**

In `docs/context-compaction-plan.md`:

1. Replace the W9 section with the following. Fill each `<…>` from this plan's reports, and delete any bullet that does not apply.

```markdown
## W9 — Validation

**Implementation plan written:** [`docs/superpowers/plans/2026-09-24-w9-validation.md`](superpowers/plans/2026-09-24-w9-validation.md) — 6 tasks. The research found that the integration failures W5 and W7 called "pre-existing" were caused by this branch: W2's `ContextStatus` enums could not be read with default JSON options (`$.contextStatus.estimateSource`). Task 1 fixed that by annotating the enum types. 9.3 is split in two:
- A hermetic end-to-end test that runs in CI (`CompactionEndToEndTests`).
- An opt-in run against a real llama.cpp (`CompactionLlamaCppEndToEndTests`, runbook in `docs/compaction-llamacpp-e2e-runbook.md`). <Result: passed on <date> with <model>, or: not run — <reason>.>

9.1 findings: [`docs/compaction-benchmark-findings.md`](compaction-benchmark-findings.md) — D2 risk <not materialized / partially materialized / materialized>. <Corpus caveat if any.>

- [x] 9.1 **Cross-domain benchmark** — run the engine over real transcripts from coding *and*
      non-coding notebooks and read the output. With a fixed vocabulary and no LLM fallback,
      this is the only evidence the sections carry meaning outside sandbox guides. Not optional
- [x] 9.2 Server unit tests (`dotnet test GuideAntsApi.Tests/...`)
- [x] 9.3 Integration: long conversation → overflow error → compact → continue → recall, against
      a small-context local llama.cpp model (`run-test-coverage.ps1 -Scope Integration`, needs Docker)
- [x] 9.4 Mirror the CI jobs in `.github/workflows/client-test.yml`

CI mirror (<date>, commit <short sha>): server unit <p/f/s>, ScriptExecutionAgent <p/f/s>, integration <p/f/s>, client <p/f/s>. Pre-existing failures, all confirmed failing on `main` too: <list, or "none">.
```

2. In the W5 section, replace the clause "confirmed the remaining full-solution test failures (`ConversationContextStatusDto.estimateSource` JSON deserialization in `GuideAntsApi.IntegrationTests`, `ScriptExecutionAgent.Tests`' sandbox/MCP-stdio tests) are pre-existing and out of this workstream's scope" with "left two full-solution failure clusters out of scope — the `estimateSource` deserialization failures turned out to be caused by W2, not pre-existing, and were fixed in W9 Task 1; `ScriptExecutionAgent.Tests`' sandbox/MCP-stdio failures are classified in W9's CI-mirror table".

3. Under **W10 — Ship**, add these bullets directly below the heading, before 10.1:

```markdown
Carried from W9:
- `src/client/vitest.config.ts` puts coverage thresholds as flat keys that Vitest 4 ignores, so the ≥85% gate `CLAUDE.md` describes is not enforced in CI (pre-existing on `main`, found in W8).
- The client suite fails under Node 26 (jsdom `localStorage`); CI pins Node 22.
<- Anything Task 5's "Carried to W10" section lists.>
```

- [ ] **Step 7: Commit (ask the user first)**

```bash
git add docs/context-compaction-plan.md
git commit -m "docs: tick W9 and correct W5's pre-existing-failure note

Records the CI-mirror results and failure classification, links the
benchmark findings, and carries the unenforced client coverage gate into
W10. The integration failures W5 called pre-existing were caused by W2
and are fixed."
```
