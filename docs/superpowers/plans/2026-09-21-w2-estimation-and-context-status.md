# W2: Estimation and Context Status Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the conversation read return `{ contextWindowTokens, estimatedPromptTokens, boundaryTurnIndex }` so the client meter (W8) can show how full a conversation's context is.

**Architecture:** `ThreadRun` records the *last round's* provider-reported prompt tokens (plus the character count it sent) on the turn's persisted `UsageResponse`. A `ConversationContextStatusService` reads the newest such turn, resolves the model's window through the W1 `IContextWindowResolver`, and falls back to a calibrated chars/token estimate when no provider usage exists. Calibration is computed from persisted turns, so there is no in-process state to lose on restart. The status is attached only by the private notebook conversation endpoint.

**Tech Stack:** ASP.NET Core 8, EF Core 8 (EF InMemory in tests), MSTest + FluentAssertions + Moq, TypeScript (type only; UI is W8).

**Spec:** [`docs/superpowers/specs/2026-09-21-context-compaction-design.md`](../specs/2026-09-21-context-compaction-design.md) — *Architecture §2*, *API surface → Context status*. Checklist: [`docs/context-compaction-plan.md`](../../context-compaction-plan.md) W2 (2.1–2.4).

## Global Constraints

- **Null is safe; wrong is harmful.** When the window is unknown, `contextWindowTokens` is `null` and the meter renders "unknown". Never substitute a default window.
- **Estimate only.** This workstream must not trigger compaction, alter request shaping, or change any existing chat behavior (Spec D1).
- **Do not change accumulation.** `MergeRoundUsage` (`ThreadRun.cs:1470`) **sums** prompt tokens across rounds; usage reporting depends on that. Only *add* last-round fields alongside.
- `AntRunner.Chat` and `AntRunner.Chat.Abstractions` must not reference `GuideAntsApi` or `GuideAntsApi.DataModel`.
- Published guide, public API and MCP paths keep current behavior: `GetConversationWithMessagesAsync` in `ConversationQueryService` is shared with them, so the status is attached at the private endpoint, not in the query service.
- Server tests: MSTest (`[TestClass]`/`[TestMethod]`) + FluentAssertions; in-memory DbContext via `BackgroundJobTestHelpers.CreateInMemoryOptions(name)`.
- EF migrations: none needed in W2 (new usage fields ride in the existing `UsageJson`/`ChatRunOutputJson` text columns).
- Never co-author commits, and only commit when the user has said to (per `CLAUDE.md` and memory `no-commits-without-permission`). The commit steps below are proposed checkpoints — ask first.

## Findings that shaped this plan

- W1 built `IContextWindowResolver` and `ILearnedContextWindowCache` and registered them in `StartupConfiguration.cs:408,414`, but **nothing calls `Resolve` and nothing calls `ILearnedContextWindowCache.Record`**. Task 4 wires `Resolve`; Task 3 wires `Record`.
- Anthropic's client already folds cache-read and cache-creation tokens into `PromptTokens` (`AnthropicChatClient.cs:751`), so `PromptTokens` is the full context size for every provider. No provider special-casing needed.
- The spec's DTO includes `boundaryTurnIndex`, but the column arrives in W4 (task 4.1). W2 ships the field always `null`; W4 populates it.
- Local (llama.cpp) models have a *loaded* window in the router entry (`RouterModelEntry.ContextSize` via `IRouterModelsConfigService`), keyed by the alias in the model's `RuntimeConfigJson` (`LocalRuntimeConfigurationParser.Parse(modelId, json).RouterModelId`). That is the "live runtime" source for the resolver.
- The unwind in `ThreadRun` (removed in W6) retries silently, so until W6 only overflows the unwind could not absorb reach the stream engine's failure path. Task 3 learns from that path; after W6 every overflow reaches it.

---

## File Structure

**Created:**
- `src/server/AntRunner.Chat/AntRunner.Chat/PromptTokenEstimator.cs` — pure chars/token estimation + calibration
- `src/server/GuideAntsApi/Services/Conversations/ConversationContextStatusService.cs` — builds the status
- `src/server/GuideAntsApi/Services/Conversations/IConversationContextStatusService.cs`
- `src/server/AntRunner.Chat/AntRunner.Chat.Tests/PromptTokenEstimatorTests.cs` *(see Task 1 note on test project location)*
- `src/server/GuideAntsApi.Tests/Services/Conversations/ConversationContextStatusServiceTests.cs`
- `src/server/GuideAntsApi.Tests/Services/Conversations/Streaming/ContextOverflowLearningTests.cs`

**Modified:**
- `src/server/AntRunner.Chat/AntRunner.Chat/ChatRunOutput.cs:62` — two fields on `UsageResponse`
- `src/server/AntRunner.Chat/AntRunner.Chat/ThreadRun.cs:649,1403,1470,1538` — record last-round values
- `src/server/GuideAntsApi/Services/Conversations/Streaming/ConversationStreamEngine.cs:~840` — learn window on overflow
- `src/server/GuideAntsApi/Models/Conversations/ConversationDto.cs:71` — `ContextStatus` on the DTO
- `src/server/GuideAntsApi/Endpoints/NotebookConversationsEndpoints.cs:34` — attach status
- `src/server/GuideAntsApi/Configuration/StartupConfiguration.cs:~414` — DI
- `src/client/src/services/` conversation DTO type (located in Task 5)

---

## Task 1: Token estimator and calibration (2.1)

**Files:**
- Create: `src/server/AntRunner.Chat/AntRunner.Chat/PromptTokenEstimator.cs`
- Test: find the existing test project for `AntRunner.Chat` first: `ls src/server | grep -i test` and `grep -rl "ToolOutputTruncator" src/server --include=*Tests.cs`. Put `PromptTokenEstimatorTests.cs` beside the `ToolOutputTruncator` tests; if those live in `GuideAntsApi.Tests`, use `src/server/GuideAntsApi.Tests/ChatLayer/PromptTokenEstimatorTests.cs`.

**Interfaces:**
- Consumes: `ChatMessage`, `ChatContent`, `ChatToolCall` from `AntRunner.Chat.Abstractions`
- Produces:
  - `PromptTokenEstimator.DefaultCharsPerToken` (`const double` = 4.0)
  - `PromptTokenEstimator.CountChars(IEnumerable<ChatMessage> messages) -> int`
  - `PromptTokenEstimator.CharsPerToken(IEnumerable<(int Tokens, int Chars)> observations) -> double`
  - `PromptTokenEstimator.EstimateTokens(int chars, double charsPerToken = DefaultCharsPerToken) -> int`

Design notes: images contribute 0 chars (their token cost is provider-specific; a known undercount, surfaced to users through `estimateSource`). Calibration pools observations (sum tokens / sum chars) rather than averaging ratios so one tiny turn cannot dominate, and clamps to `[1.5, 8.0]` so a bad observation cannot make the meter absurd.

- [ ] **Step 1: Write the failing test**

```csharp
using AntRunner.Chat;
using AntRunner.Chat.Abstractions;
using FluentAssertions;

namespace GuideAntsApi.Tests.ChatLayer;

[TestClass]
public sealed class PromptTokenEstimatorTests
{
    [TestMethod]
    public void CountChars_SumsTextAcrossMessages()
    {
        var messages = new[]
        {
            new ChatMessage(ChatRole.User, "hello"),      // 5
            new ChatMessage(ChatRole.Assistant, "world!!") // 7
        };

        PromptTokenEstimator.CountChars(messages).Should().Be(12);
    }

    [TestMethod]
    public void CountChars_IncludesToolCallNameAndArguments()
    {
        var call = new ChatToolCall
        {
            Id = "c1",
            Function = new ChatToolCallFunction { Name = "search", Arguments = "{\"q\":\"x\"}" }
        };
        var message = new ChatMessage(ChatRole.Assistant, [], [call]);

        // "search" (6) + "{\"q\":\"x\"}" (9)
        PromptTokenEstimator.CountChars([message]).Should().Be(15);
    }

    [TestMethod]
    public void CountChars_IgnoresImageContent()
    {
        var message = new ChatMessage(ChatRole.User,
            [new ChatContent(new ChatImageUrl { Url = "data:image/png;base64,AAAA" })]);

        PromptTokenEstimator.CountChars([message]).Should().Be(0);
    }

    [TestMethod]
    public void EstimateTokens_UsesDefaultRatio()
    {
        PromptTokenEstimator.EstimateTokens(400).Should().Be(100);
    }

    [TestMethod]
    public void EstimateTokens_RoundsUp()
    {
        PromptTokenEstimator.EstimateTokens(401).Should().Be(101);
    }

    [TestMethod]
    public void CharsPerToken_NoObservations_ReturnsDefault()
    {
        PromptTokenEstimator.CharsPerToken([]).Should().Be(PromptTokenEstimator.DefaultCharsPerToken);
    }

    [TestMethod]
    public void CharsPerToken_PoolsObservations()
    {
        // 300 chars / 100 tokens and 100 chars / 50 tokens => 400 / 150
        var ratio = PromptTokenEstimator.CharsPerToken([(100, 300), (50, 100)]);

        ratio.Should().BeApproximately(400.0 / 150.0, 1e-9);
    }

    [TestMethod]
    public void CharsPerToken_IgnoresNonPositiveObservations()
    {
        PromptTokenEstimator.CharsPerToken([(0, 500), (100, 0)])
            .Should().Be(PromptTokenEstimator.DefaultCharsPerToken);
    }

    [TestMethod]
    public void CharsPerToken_ClampsToSaneRange()
    {
        PromptTokenEstimator.CharsPerToken([(1, 1000)]).Should().Be(8.0);
        PromptTokenEstimator.CharsPerToken([(1000, 1)]).Should().Be(1.5);
    }
}
```

Before writing this, confirm `ChatImageUrl` has a settable `Url` (`grep -n "class ChatImageUrl" -A8 src/server/AntRunner.Chat/AntRunner.Chat.Abstractions/*.cs`) and adjust the initializer to match.

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test src/server/GuideAntsApi.Tests/GuideAntsApi.Tests.csproj --filter "FullyQualifiedName~PromptTokenEstimatorTests"`
Expected: FAIL to compile — `PromptTokenEstimator` does not exist.

- [ ] **Step 3: Implement**

```csharp
using AntRunner.Chat.Abstractions;

namespace AntRunner.Chat;

/// <summary>
/// Estimates prompt size from character counts. Provider-reported usage is always preferred;
/// this exists for the cases where none has been observed yet, and to project the small
/// amount of content added since the last provider-reported round.
/// </summary>
public static class PromptTokenEstimator
{
    public const double DefaultCharsPerToken = 4.0;

    private const double MinCharsPerToken = 1.5;
    private const double MaxCharsPerToken = 8.0;

    /// <summary>
    /// Counts the characters a provider would tokenize: text content, tool-call names and
    /// tool-call arguments. Image content counts as zero — its token cost is provider-specific.
    /// </summary>
    public static int CountChars(IEnumerable<ChatMessage> messages)
    {
        long total = 0;
        foreach (var message in messages)
        {
            foreach (var content in message.Content)
            {
                total += content.Text?.Length ?? 0;
            }

            if (message.ToolCalls != null)
            {
                foreach (var call in message.ToolCalls)
                {
                    total += call.Function.Name?.Length ?? 0;
                    total += call.Function.Arguments?.Length ?? 0;
                }
            }
        }

        return (int)Math.Min(total, int.MaxValue);
    }

    public static int EstimateTokens(int chars, double charsPerToken = DefaultCharsPerToken)
    {
        if (chars <= 0)
        {
            return 0;
        }

        return (int)Math.Ceiling(chars / charsPerToken);
    }

    /// <summary>
    /// Learns chars-per-token from turns where the provider reported real prompt tokens.
    /// Pooled (sum chars / sum tokens) so one tiny turn cannot dominate; clamped so a bad
    /// observation cannot produce an absurd meter.
    /// </summary>
    public static double CharsPerToken(IEnumerable<(int Tokens, int Chars)> observations)
    {
        long tokens = 0;
        long chars = 0;
        foreach (var (t, c) in observations)
        {
            if (t <= 0 || c <= 0)
            {
                continue;
            }

            tokens += t;
            chars += c;
        }

        if (tokens == 0)
        {
            return DefaultCharsPerToken;
        }

        return Math.Clamp((double)chars / tokens, MinCharsPerToken, MaxCharsPerToken);
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test src/server/GuideAntsApi.Tests/GuideAntsApi.Tests.csproj --filter "FullyQualifiedName~PromptTokenEstimatorTests"`
Expected: PASS (8 tests).

- [ ] **Step 5: Commit (ask first)**

```bash
git add src/server/AntRunner.Chat/AntRunner.Chat/PromptTokenEstimator.cs src/server/GuideAntsApi.Tests/ChatLayer/PromptTokenEstimatorTests.cs
git commit -m "Adds a calibrated chars-per-token prompt estimator"
```

---

## Task 2: Record last-round prompt size on the turn's usage (2.2)

**Files:**
- Modify: `src/server/AntRunner.Chat/AntRunner.Chat/ChatRunOutput.cs:62-73`
- Modify: `src/server/AntRunner.Chat/AntRunner.Chat/ThreadRun.cs:~614-650, ~1395-1405, 1470-1494, 1533-1541`
- Test: `src/server/GuideAntsApi.Tests/ChatLayer/ThreadRunLastRoundUsageTests.cs`

**Interfaces:**
- Consumes: `PromptTokenEstimator.CountChars` (Task 1)
- Produces on `UsageResponse`:
  - `int? LastRoundPromptTokens` — JSON `last_round_prompt_tokens`; the provider-reported prompt size of the final round (= context size at that round)
  - `int? LastRoundPromptChars` — JSON `last_round_prompt_chars`; `CountChars` of the request messages for that same round
- `MergeRoundUsage(UsageResponse? accumulated, ChatCompletionUsage roundUsage, int roundPromptChars)` — new third parameter. `PromptTokens`/`CompletionTokens`/`CachedPromptTokens`/`TotalTokens` accumulation is **unchanged**.

Semantics: last-round fields are *overwritten* each round (not summed), and only when the round reported `PromptTokens > 0`. A round that reports no prompt tokens keeps the previous round's pair, so the two fields always describe the same round.

- [ ] **Step 1: Write the failing test**

`MergeRoundUsage` is `private static`. Find how existing tests reach `ThreadRun` internals (`grep -rn "MergeRoundUsage\|InternalsVisibleTo" src/server --include=*.cs --include=*.csproj | head`). If it is not reachable, change it to `internal static` and confirm `AntRunner.Chat.csproj` has `InternalsVisibleTo` for the test assembly (add it if missing). Then:

```csharp
using AntRunner.Chat;
using AntRunner.Chat.Abstractions;
using FluentAssertions;

namespace GuideAntsApi.Tests.ChatLayer;

[TestClass]
public sealed class ThreadRunLastRoundUsageTests
{
    private static ChatCompletionUsage Usage(int prompt, int completion) =>
        new() { PromptTokens = prompt, CompletionTokens = completion, TotalTokens = prompt + completion };

    [TestMethod]
    public void FirstRound_RecordsLastRoundValues()
    {
        var result = ThreadRun.MergeRoundUsage(null, Usage(1_000, 50), roundPromptChars: 4_000);

        result.LastRoundPromptTokens.Should().Be(1_000);
        result.LastRoundPromptChars.Should().Be(4_000);
    }

    [TestMethod]
    public void LaterRound_OverwritesLastRound_ButStillSumsTotals()
    {
        var first = ThreadRun.MergeRoundUsage(null, Usage(1_000, 50), 4_000);
        var second = ThreadRun.MergeRoundUsage(first, Usage(1_200, 30), 4_900);

        second.LastRoundPromptTokens.Should().Be(1_200);
        second.LastRoundPromptChars.Should().Be(4_900);
        second.PromptTokens.Should().Be(2_200, "accumulation is cumulative spend and must not change");
        second.CompletionTokens.Should().Be(80);
    }

    [TestMethod]
    public void RoundWithoutPromptTokens_KeepsPreviousLastRoundPair()
    {
        var first = ThreadRun.MergeRoundUsage(null, Usage(1_000, 50), 4_000);
        var second = ThreadRun.MergeRoundUsage(first, Usage(0, 30), 5_000);

        second.LastRoundPromptTokens.Should().Be(1_000);
        second.LastRoundPromptChars.Should().Be(4_000);
    }

    [TestMethod]
    public void FirstRoundWithoutPromptTokens_LeavesLastRoundNull()
    {
        var result = ThreadRun.MergeRoundUsage(null, Usage(0, 30), 5_000);

        result.LastRoundPromptTokens.Should().BeNull();
        result.LastRoundPromptChars.Should().BeNull();
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test src/server/GuideAntsApi.Tests/GuideAntsApi.Tests.csproj --filter "FullyQualifiedName~ThreadRunLastRoundUsageTests"`
Expected: FAIL to compile — no `LastRoundPromptTokens` / no third parameter.

- [ ] **Step 3: Implement**

In `ChatRunOutput.cs`, extend `UsageResponse`:

```csharp
        // TODO: Refactor this for general use
        public int? CachedPromptTokens { get; set; }

        /// <summary>
        /// Provider-reported prompt size of the final round — the actual context size at that
        /// point. <see cref="PromptTokens"/> is a cross-round sum (cumulative spend) and must
        /// not be read as context size.
        /// </summary>
        [JsonPropertyName("last_round_prompt_tokens")]
        public int? LastRoundPromptTokens { get; set; }

        /// <summary>
        /// Characters in the request messages of the same round as
        /// <see cref="LastRoundPromptTokens"/>; the pair calibrates chars-per-token.
        /// </summary>
        [JsonPropertyName("last_round_prompt_chars")]
        public int? LastRoundPromptChars { get; set; }
```

In `ThreadRun.MergeRoundUsage`, add the parameter and set the pair on both branches:

```csharp
        internal static UsageResponse MergeRoundUsage(
            UsageResponse? accumulated, ChatCompletionUsage roundUsage, int roundPromptChars)
        {
            var roundCached = roundUsage.PromptTokensDetails?.CachedTokens ?? 0;
            var roundPrompt = roundUsage.PromptTokens ?? 0;
            var roundCompletion = roundUsage.CompletionTokens ?? 0;
            var roundTotal = roundUsage.TotalTokens ?? (roundPrompt + roundCompletion);

            // Only a round that reported prompt tokens updates the pair, so the two fields
            // always describe the same round.
            var lastTokens = roundPrompt > 0 ? roundPrompt : accumulated?.LastRoundPromptTokens;
            var lastChars = roundPrompt > 0 ? roundPromptChars : accumulated?.LastRoundPromptChars;

            if (accumulated == null)
            {
                return new UsageResponse
                {
                    PromptTokens = roundPrompt,
                    CompletionTokens = roundCompletion,
                    CachedPromptTokens = roundCached,
                    TotalTokens = roundTotal,
                    LastRoundPromptTokens = lastTokens,
                    LastRoundPromptChars = lastChars
                };
            }

            return new UsageResponse
            {
                PromptTokens = (accumulated.PromptTokens ?? 0) + roundPrompt,
                CompletionTokens = (accumulated.CompletionTokens ?? 0) + roundCompletion,
                CachedPromptTokens = (accumulated.CachedPromptTokens ?? 0) + roundCached,
                TotalTokens = (accumulated.TotalTokens ?? 0) + roundTotal,
                LastRoundPromptTokens = lastTokens,
                LastRoundPromptChars = lastChars
            };
        }
```

Call site at `ThreadRun.cs:~620` (main loop): the response message is appended to `messages` *after* the call, so capture the request size before the call and pass it after:

```csharp
                    var requestPromptChars = PromptTokenEstimator.CountChars(messages);   // just before InvokeCompletionAsync
                    ...
                    accumulatedUsage = MergeRoundUsage(accumulatedUsage, response.Usage, requestPromptChars);
```

Place `requestPromptChars` immediately above `LogOutboundChatRequest(roundIndex, chatRequest);` so it measures exactly what was sent (after `EnsureRuntimeToolLimitOverrideMessage`, which can add a message).

Call site at `ThreadRun.cs:~1403` (partial response on cancel): the request size for that round is not in scope. Read the enclosing method; if the round's request messages are not available, pass `0` — a `0` char count is treated as "no calibration pair" by Task 4 (it filters `Chars <= 0`), and the token value is still valid. Use the same `roundPrompt > 0` rule, so add this guard inside `MergeRoundUsage`'s pair computation: `roundPrompt > 0 && roundPromptChars > 0` for the *chars* only, i.e. keep `lastTokens` as written and set `lastChars = roundPromptChars > 0 ? roundPromptChars : (int?)null`. Update the "keeps previous pair" test accordingly only if it fails for that reason.

Call site at `ThreadRun.cs:~1538` (`BuildRunResults` copies `response.Usage` into `runResults.Usage`): this builds the *final* `UsageResponse` from the last response only. Read the surrounding code to see how it relates to `accumulatedUsage`. If `runResults.Usage` is later replaced by `accumulatedUsage`, nothing to do. If it is what is persisted, copy the pair from `accumulatedUsage` into it. Add a test that exercises the persisted path (see Step 4b).

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test src/server/GuideAntsApi.Tests/GuideAntsApi.Tests.csproj --filter "FullyQualifiedName~ThreadRunLastRoundUsageTests"`
Expected: PASS (4 tests).

- [ ] **Step 4b: Prove the value survives to the persisted output**

Find an existing `ThreadRun`/`ChatRunner` test that runs a fake `IChatCompletionClient` end to end (`grep -rln "IChatCompletionClient" src/server/GuideAntsApi.Tests | head`). Add a test there: two rounds (a tool call round, then a final answer) with the fake reporting prompt tokens 1_000 then 1_400; assert `output.Usage.LastRoundPromptTokens == 1_400` and `output.Usage.PromptTokens == 2_400`.

Run the full chat-layer tests: `dotnet test src/server/GuideAntsApi.Tests/GuideAntsApi.Tests.csproj --filter "FullyQualifiedName~ChatLayer"`
Expected: PASS, no regressions in existing usage tests.

- [ ] **Step 5: Commit (ask first)**

```bash
git add src/server/AntRunner.Chat src/server/GuideAntsApi.Tests/ChatLayer
git commit -m "Records last-round prompt size on turn usage"
```

---

## Task 3: Learn the context window from an overflow rejection (wires W1's cache)

**Files:**
- Modify: `src/server/GuideAntsApi/Services/Conversations/Streaming/ConversationStreamEngine.cs` (ctor at `:36`; failure path near `:836`)
- Test: `src/server/GuideAntsApi.Tests/Services/Conversations/Streaming/ContextOverflowLearningTests.cs`

**Interfaces:**
- Consumes: `ILearnedContextWindowCache.Record(string modelId, int? contextSize)` (W1); `ChatContextOverflowException.ContextSize` (`int?`)
- Produces: `ConversationStreamEngine` ctor gains `ILearnedContextWindowCache? learnedContextWindows = null` (last parameter, optional, matching how `notebookFileSyncService` is already optional so existing test constructions still compile). Also a static helper `ConversationStreamEngine.RecordLearnedContextWindow(ILearnedContextWindowCache?, string? modelId, Exception)` — static so it is testable without standing up the engine.

Metadata only: recording must never alter the turn's outcome, retry, or shape a request. Unwrap the same way `MapTerminationCode` does (`ChatConversationException.InnerException`).

- [ ] **Step 1: Write the failing test**

```csharp
using AntRunner.Chat.Abstractions;
using FluentAssertions;
using GuideAntsApi.Services.Conversations.Streaming;
using GuideAntsApi.Services.Routing;
using Moq;

namespace GuideAntsApi.Tests.Services.Conversations.Streaming;

[TestClass]
public sealed class ContextOverflowLearningTests
{
    private static ChatContextOverflowException Overflow(int? contextSize) =>
        new("too big", promptTokens: 9_000, contextSize: contextSize);

    [TestMethod]
    public void Overflow_RecordsReportedWindow()
    {
        var cache = new Mock<ILearnedContextWindowCache>();

        ConversationStreamEngine.RecordLearnedContextWindow(cache.Object, "model-a", Overflow(8_192));

        cache.Verify(c => c.Record("model-a", 8_192), Times.Once);
    }

    [TestMethod]
    public void Overflow_WrappedInChatConversationException_IsUnwrapped()
    {
        var cache = new Mock<ILearnedContextWindowCache>();
        var wrapped = new ChatConversationException("failed", Overflow(4_096));

        ConversationStreamEngine.RecordLearnedContextWindow(cache.Object, "model-a", wrapped);

        cache.Verify(c => c.Record("model-a", 4_096), Times.Once);
    }

    [TestMethod]
    public void NonOverflowException_RecordsNothing()
    {
        var cache = new Mock<ILearnedContextWindowCache>();

        ConversationStreamEngine.RecordLearnedContextWindow(
            cache.Object, "model-a", new InvalidOperationException("boom"));

        cache.Verify(c => c.Record(It.IsAny<string>(), It.IsAny<int?>()), Times.Never);
    }

    [TestMethod]
    public void MissingModelIdOrCache_IsANoOp()
    {
        var cache = new Mock<ILearnedContextWindowCache>();

        ConversationStreamEngine.RecordLearnedContextWindow(cache.Object, null, Overflow(8_192));
        ConversationStreamEngine.RecordLearnedContextWindow(null, "model-a", Overflow(8_192));

        cache.Verify(c => c.Record(It.IsAny<string>(), It.IsAny<int?>()), Times.Never);
    }
}
```

Before writing, read the real constructors: `sed -n 1,60p src/server/AntRunner.Chat/AntRunner.Chat.Abstractions/ChatContextOverflowException.cs` and `grep -n "class ChatConversationException" -A15 -r src/server --include=*.cs`. Adjust the two constructor calls to their real signatures.

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test src/server/GuideAntsApi.Tests/GuideAntsApi.Tests.csproj --filter "FullyQualifiedName~ContextOverflowLearningTests"`
Expected: FAIL to compile — `RecordLearnedContextWindow` does not exist.

- [ ] **Step 3: Implement**

Add to `ConversationStreamEngine`:

```csharp
    internal static void RecordLearnedContextWindow(
        ILearnedContextWindowCache? cache, string? modelId, Exception ex)
    {
        if (cache == null || string.IsNullOrWhiteSpace(modelId))
        {
            return;
        }

        var inner = ex is ChatConversationException chatEx ? chatEx.InnerException : ex.InnerException;
        var overflow = ex as ChatContextOverflowException ?? inner as ChatContextOverflowException;
        if (overflow != null)
        {
            cache.Record(modelId, overflow.ContextSize);
        }
    }
```

Add the ctor parameter and field (`_learnedContextWindows`). In the failure path, immediately before `var terminalStatus = ConversationTurnTerminalizer.MapTerminalStatus(partialOutput, ex);` (`:~840`), add:

```csharp
                RecordLearnedContextWindow(_learnedContextWindows, context.DbTurn.ModelDeploymentId, ex);
```

Confirm `context.DbTurn.ModelDeploymentId` is the same id space `IContextWindowResolver.Resolve` receives in Task 4 (both must round-trip through `IChatTargetResolver.Resolve`). Check with `grep -n "ModelDeploymentId" src/server/GuideAntsApi/Services/Conversations/Streaming/ConversationStreamEngine.cs | head`. If the turn stores a resolved deployment id rather than the catalog id, record under the id the resolver uses and note the mapping in a comment.

Registration: `ConversationStreamEngine` is DI-constructed; the optional parameter resolves automatically because `ILearnedContextWindowCache` is already a singleton (`StartupConfiguration.cs:414`). Verify with `dotnet build src/server/GuideAntsApi.sln`.

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test src/server/GuideAntsApi.Tests/GuideAntsApi.Tests.csproj --filter "FullyQualifiedName~ContextOverflowLearningTests|FullyQualifiedName~ConversationStreamEngine"`
Expected: PASS, including existing stream-engine tests.

- [ ] **Step 5: Commit (ask first)**

```bash
git add src/server/GuideAntsApi/Services/Conversations/Streaming/ConversationStreamEngine.cs src/server/GuideAntsApi.Tests/Services/Conversations/Streaming
git commit -m "Learns a model's context window from overflow rejections"
```

---

## Task 4: ConversationContextStatusService (2.3, service half)

**Files:**
- Create: `src/server/GuideAntsApi/Services/Conversations/IConversationContextStatusService.cs`
- Create: `src/server/GuideAntsApi/Services/Conversations/ConversationContextStatusService.cs`
- Modify: `src/server/GuideAntsApi/Configuration/StartupConfiguration.cs` (register near `:414`)
- Test: `src/server/GuideAntsApi.Tests/Services/Conversations/ConversationContextStatusServiceTests.cs`

**Interfaces:**
- Consumes: `IContextWindowResolver.Resolve(string modelId, int? liveRuntimeContextSize) -> ContextWindowInfo` (W1); `PromptTokenEstimator` (Task 1); `UsageResponse.LastRoundPromptTokens/LastRoundPromptChars` (Task 2); `IRouterModelsConfigService.GetEntriesAsync`; `LocalRuntimeConfigurationParser.Parse(modelId, json).RouterModelId`; `ApplicationDbContext` (`ConversationTurns`, `NotebookConversationMessages`, `Models`)
- Produces:

```csharp
public enum ContextEstimateSource { None, ProviderUsage, Characters }

public sealed record ConversationContextStatusDto(
    int? ContextWindowTokens,
    int? EstimatedPromptTokens,
    int? BoundaryTurnIndex,          // always null until W4 adds the column
    ContextEstimateSource EstimateSource);

public interface IConversationContextStatusService
{
    Task<ConversationContextStatusDto> GetAsync(Guid conversationId, CancellationToken ct = default);
}
```

Estimate algorithm (all in `GetAsync`):

1. Load the conversation's turns ordered by `TurnIndex` descending, `Status == "completed"` only (a streaming or failed turn has partial or missing usage), take the newest 10 with `UsageJson`. Deserialize each `UsageJson` to `UsageResponse` with the same `JsonSerializerOptions` `ConversationPersistence` uses.
2. `modelId` = newest completed turn's `ModelDeploymentId`. If there is no completed turn: return `(window: null, estimate: null, source: None)`. There is nothing in the conversation to size and no model to resolve.
3. `charsPerToken` = `PromptTokenEstimator.CharsPerToken` over the loaded turns' `(LastRoundPromptTokens, LastRoundPromptChars)` pairs.
4. **ProviderUsage path:** newest turn with `LastRoundPromptTokens > 0`. Estimate = `LastRoundPromptTokens` + `EstimateTokens(chars of every persisted message after that turn's final request, charsPerToken)`. "After the final request" is the final assistant reply of that turn plus any messages of later turns. Concretely: sum `Content.Length` of messages with `TurnIndex > T` plus the *last assistant message* of turn `T`, where `T` is that turn's index. This is an approximation (it excludes tool-call arguments on later messages); accepted, and the source is labeled.
5. **Characters path** (no turn has provider usage, e.g. local streaming that reports none, or turns predating W2): estimate = `EstimateTokens(sum of all persisted message content lengths, charsPerToken)`. This under-counts the system prompt and tool definitions; the source label lets the client show "~".
6. Window: `live` = router entry `ContextSize` when the model row's `Provider == "llama-cpp"` (look up `db.Models.FirstOrDefault(m => m.ModelId == modelId)`, parse `RuntimeConfigJson`, match `RouterModelEntry.Alias`); otherwise null. Any exception reading the router (admin service down) is swallowed to `live = null` — the meter must degrade to catalog/learned/unknown, never fail the conversation read. `window = resolver.Resolve(modelId, live).ContextWindowTokens`.

- [ ] **Step 1: Write the failing tests**

Follow the setup pattern in `ContextWindowResolverTests` for mocks, and `BackgroundJobTestHelpers.CreateInMemoryOptions("status-...")` for the DbContext (see `ModelContextWindowTests.cs` for how entities are constructed and saved; copy its required-field setup for `Notebook`/`NotebookConversation`). Each test seeds turns via a small local helper:

```csharp
private static ConversationTurn Turn(
    Guid conversationId, int index, string model,
    int? lastRoundTokens, int? lastRoundChars, string status = "completed") =>
    new()
    {
        NotebookConversationId = conversationId,
        TurnIndex = index,
        AssistantName = "a",
        ModelDeploymentId = model,
        Instructions = "i",
        Status = status,
        UsageJson = lastRoundTokens is null
            ? null
            : JsonSerializer.Serialize(new UsageResponse
            {
                PromptTokens = lastRoundTokens,
                LastRoundPromptTokens = lastRoundTokens,
                LastRoundPromptChars = lastRoundChars
            })
    };
```

Tests (write all; each asserts the DTO):

```csharp
[TestMethod] public async Task NoCompletedTurn_ReturnsNoneAndNulls()
    // no turns -> window null, estimate null, source None

[TestMethod] public async Task ProviderUsage_IsUsedAsTheBaseEstimate()
    // one completed turn, LastRoundPromptTokens 10_000, no later messages,
    // final assistant message "x" * 400 (=> +100 tokens at default 4.0)
    // -> EstimatedPromptTokens 10_100, source ProviderUsage

[TestMethod] public async Task ProviderUsage_AddsMessagesFromLaterTurns()
    // turn 1 usage 10_000; turn 2 completed with NO usage and 800 chars of messages
    // -> 10_000 + assistant-of-turn-1 chars + 800 chars, all / 4.0

[TestMethod] public async Task NoProviderUsage_FallsBackToCharacterEstimate()
    // messages totalling 4_000 chars, no usage anywhere -> 1_000, source Characters

[TestMethod] public async Task Calibration_ShiftsTheCharacterEstimate()
    // turns whose pairs are (tokens 1_000, chars 2_000) -> ratio 2.0;
    // then a turn with no usage: 2_000 chars of messages -> 1_000 tokens, not 500

[TestMethod] public async Task IgnoresIncompleteTurns()
    // newest turn has status "streaming" with usage 99_999; older completed turn 5_000
    // -> estimate reflects 5_000

[TestMethod] public async Task UnknownWindow_ReturnsNullWindowButKeepsTheEstimate()
    // resolver returns ContextWindowInfo(null, null, Unknown)
    // -> ContextWindowTokens null, EstimatedPromptTokens still set

[TestMethod] public async Task LocalModel_PassesRouterContextSizeAsLiveRuntimeValue()
    // Model row Provider "llama-cpp", RuntimeConfigJson with routerModelId "qwen"
    // router entries contain Alias "qwen", ContextSize 8_192
    // -> resolver.Resolve invoked with liveRuntimeContextSize 8_192 (Moq Verify)

[TestMethod] public async Task RouterLookupFailure_DegradesToNullLive()
    // IRouterModelsConfigService.GetEntriesAsync throws -> no exception escapes,
    // resolver.Resolve invoked with liveRuntimeContextSize null

[TestMethod] public async Task BoundaryTurnIndex_IsNullUntilW4()
    // asserts BoundaryTurnIndex is null
```

For the `LocalModel` test, read `LocalRuntimeConfigurationParser` to get a valid `RuntimeConfigJson` payload (`grep -n "RouterModelId" -B3 -A12 src/server/GuideAntsApi/Services/LlamaCpp/LocalRuntimeConfiguration.cs`).

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test src/server/GuideAntsApi.Tests/GuideAntsApi.Tests.csproj --filter "FullyQualifiedName~ConversationContextStatusServiceTests"`
Expected: FAIL to compile — service and DTO do not exist.

- [ ] **Step 3: Implement**

Create the interface and DTO as above. Implement `ConversationContextStatusService` with constructor `(IServiceScopeFactory scopeFactory, IContextWindowResolver resolver, IRouterModelsConfigService routerModels, ILogger<ConversationContextStatusService> logger)`, using a scope per call like `ConversationQueryService` does (`_scopeFactory.CreateScope()` → `ApplicationDbContext`). Use `AsNoTracking()` on every query. For message content length use a projection that selects only `m.Content.Length`-style data — do not materialize whole messages: `.Select(m => new { m.TurnIndex, m.Role, Len = m.Content == null ? 0 : m.Content.Length })`. Skip `m.IsStreaming == true` rows, matching `ConversationQueryService`.

Register in `StartupConfiguration.cs` next to the resolver:

```csharp
        services.AddScoped<IConversationContextStatusService, ConversationContextStatusService>();
```

Match the lifetime the neighboring conversation services use (`grep -n "IConversationQueryService" src/server/GuideAntsApi/Configuration/StartupConfiguration.cs`); use the same one. `IRouterModelsConfigService` is already registered.

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test src/server/GuideAntsApi.Tests/GuideAntsApi.Tests.csproj --filter "FullyQualifiedName~ConversationContextStatusServiceTests"`
Expected: PASS (10 tests).

- [ ] **Step 5: Commit (ask first)**

```bash
git add src/server/GuideAntsApi/Services/Conversations src/server/GuideAntsApi/Configuration/StartupConfiguration.cs src/server/GuideAntsApi.Tests/Services/Conversations
git commit -m "Adds conversation context status service"
```

---

## Task 5: Expose the status on the conversation read (2.3, endpoint half) and client type

**Files:**
- Modify: `src/server/GuideAntsApi/Models/Conversations/ConversationDto.cs:71-79`
- Modify: `src/server/GuideAntsApi/Endpoints/NotebookConversationsEndpoints.cs:34-40`
- Test: `src/server/GuideAntsApi.Tests/Endpoints/NotebookConversationContextStatusEndpointTests.cs`
- Modify: the client conversation DTO type (locate with `grep -rn "StreamingPreview\|streamingPreview" src/client/src --include=*.ts -l | head`)

**Interfaces:**
- Consumes: `IConversationContextStatusService.GetAsync`, `ConversationContextStatusDto` (Task 4)
- Produces: `NotebookConversationWithMessagesDto.ContextStatus` (`ConversationContextStatusDto?`, default null) — appended as the **last** optional record parameter so every existing positional construction still compiles. Only `GET /api/notebooks/{notebookId}/conversations/{convoId}` populates it; the published/MCP callers of `GetConversationWithMessagesAsync` leave it null.

- [ ] **Step 1: Write the failing test**

Look at how existing endpoint tests are built (`ls src/server/GuideAntsApi.Tests/Endpoints`; reuse the same host/`WebApplicationFactory` or handler-invocation pattern rather than inventing one). Two tests:

```csharp
[TestMethod] public async Task GetConversation_IncludesContextStatus()
    // conversation service returns a NotebookConversationWithMessagesDto;
    // status service returns (200_000, 12_345, null, ProviderUsage)
    // -> response body contextStatus.contextWindowTokens == 200000,
    //    estimatedPromptTokens == 12345, estimateSource == "ProviderUsage" (or the
    //    serializer's configured casing — assert what the API actually emits)

[TestMethod] public async Task GetConversation_StatusFailure_StillReturnsTheConversation()
    // status service throws -> 200 with contextStatus null (the meter is advisory;
    // it must never break loading a conversation)
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test src/server/GuideAntsApi.Tests/GuideAntsApi.Tests.csproj --filter "FullyQualifiedName~NotebookConversationContextStatusEndpointTests"`
Expected: FAIL — no `ContextStatus` on the DTO.

- [ ] **Step 3: Implement**

DTO:

```csharp
public record NotebookConversationWithMessagesDto(
    Guid Id,
    string Title,
    string? AssistantName,
    DateTime Created,
    DateTime? LastActivity,
    IReadOnlyList<MessageDto> Messages,
    ConversationTurnStatusDto? ActiveTurn = null,
    ConversationLockStatusDto? Lock = null,
    ConversationStreamingPreviewDto? StreamingPreview = null,
    ConversationContextStatusDto? ContextStatus = null);
```

Endpoint handler:

```csharp
        group.MapGet("/{convoId:guid}", async (
            [FromServices] IConversationService svc,
            [FromServices] IConversationContextStatusService contextStatus,
            [FromServices] ILogger<NotebookConversationsEndpoints> logger,
            Guid convoId,
            CancellationToken ct) =>
        {
            var conversation = await svc.GetConversationWithMessagesAsync(convoId);
            if (conversation == null)
            {
                return Results.NotFound();
            }

            try
            {
                var status = await contextStatus.GetAsync(convoId, ct);
                return Results.Ok(conversation with { ContextStatus = status });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The meter is advisory. Never fail a conversation load because of it.
                logger.LogWarning(ex, "Failed to compute context status for conversation {ConversationId}", convoId);
                return Results.Ok(conversation);
            }
        })
```

Check that `NotebookConversationsEndpoints` is a static class (`ILogger<T>` with a static type argument is illegal): `sed -n 1,25p src/server/GuideAntsApi/Endpoints/NotebookConversationsEndpoints.cs`. If it is static, inject `ILoggerFactory` and `CreateLogger("NotebookConversations")` instead, following whatever the other handlers in that file already do for logging.

Client: add to the conversation-with-messages TS type:

```ts
export type ContextEstimateSource = 'None' | 'ProviderUsage' | 'Characters';

export interface ConversationContextStatus {
  contextWindowTokens: number | null;
  estimatedPromptTokens: number | null;
  boundaryTurnIndex: number | null;
  estimateSource: ContextEstimateSource;
}
```

and `contextStatus?: ConversationContextStatus | null;` on the conversation type. Match the serializer's enum casing to whatever Step 1 showed the API emits (if the API emits enums as strings in camelCase, adjust the union). This is a type addition only; the meter UI is W8.

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test src/server/GuideAntsApi.Tests/GuideAntsApi.Tests.csproj --filter "FullyQualifiedName~NotebookConversationContextStatusEndpointTests"` then `cd src/client && npm run typecheck`
Expected: PASS; typecheck clean for both tsconfigs.

- [ ] **Step 5: Commit (ask first)**

```bash
git add src/server/GuideAntsApi/Models/Conversations src/server/GuideAntsApi/Endpoints/NotebookConversationsEndpoints.cs src/server/GuideAntsApi.Tests/Endpoints src/client/src
git commit -m "Returns context status on the notebook conversation read"
```

---

## Task 6: Full verification (2.4)

- [ ] **Step 1: Server unit tests and build**

Run: `dotnet build src/server/GuideAntsApi.sln && dotnet test src/server/GuideAntsApi.sln --filter "TestCategory!=Integration"`
Expected: build clean; no new failures. Integration tests need Docker and are not required for W2, but confirm none of them construct `ConversationStreamEngine` positionally in a way the new optional parameter breaks: `grep -rn "new ConversationStreamEngine" src/server`.

- [ ] **Step 2: Client checks**

Run: `cd src/client && npm run typecheck && npx vitest run`
Expected: PASS. (W2 adds a type only; this confirms no client regressions.)

- [ ] **Step 3: Read one real conversation end to end**

Run the API (`dotnet run --project src/server/GuideAntsApi`) against a dev DB, send two messages in a notebook conversation, then `GET /api/notebooks/{notebookId}/conversations/{convoId}` and read `contextStatus`. Confirm `estimateSource` is `ProviderUsage`, `estimatedPromptTokens` is in the range the provider's dashboard/usage shows for that final round, and `contextWindowTokens` is non-null for a catalog model with a value (or `null` for one without). Record what you saw in the PR description. A green suite does not prove the estimate is *right*.

- [ ] **Step 4: Tick W2 in the checklist**

In `docs/context-compaction-plan.md`, tick 2.1–2.4 and add under W2 a line linking this plan, plus these three notes: the learned-window wiring (Task 3) and live-size lookup (Task 4) were gaps left by W1; `boundaryTurnIndex` is returned `null` until W4; `estimateSource` was added to the DTO beyond the spec's three fields so the client can render "~" for character-based estimates.

---

## Self-review

**Spec coverage.**
- Checklist 2.1 (estimator + per-model calibration) → Task 1 (pure) + Task 4 step 3 (calibration computed per conversation from the model's persisted turns).
- 2.2 (`lastRoundPromptTokens`, leave accumulation alone) → Task 2; the "sum unchanged" assertion is in `LaterRound_OverwritesLastRound_ButStillSumsTotals`.
- 2.3 (conversation read gains the three fields) → Tasks 4–5. `boundaryTurnIndex` is null until W4 (called out).
- 2.4 (tests: calibration, nullable window) → Task 1 calibration tests, Task 4 `Calibration_ShiftsTheCharacterEstimate` and `UnknownWindow_*`.
- Spec Architecture §2 precedence step 3 (learned value) → Task 3 wires the write side; W1's resolver already reads it.
- Spec states "meter renders unknown, never a guess" → Global Constraints + Task 4 `UnknownWindow_*`.

**Known limitations, deliberately accepted.**
- Calibration is per conversation (pooled over that conversation's last 10 completed turns), not a global per-model table. A brand-new conversation with no usage yet uses the 4.0 default. A cross-conversation per-model cache would need shared mutable state and a restart story; not worth it for an advisory meter.
- The estimate ignores images, and the character fallback ignores the system prompt and tool definitions. Both undercount; `estimateSource` tells the client which case it is.
- Until W6 removes the unwind, overflows the unwind absorbs do not reach Task 3's learning hook.

**Placeholder scan.** Every code step has code. The steps that say "read X first" (Task 1 `ChatImageUrl`, Task 2 `ThreadRun.cs:1403/1538`, Task 3 exception constructors, Task 5 static-class check) exist because the real signature could not be pinned from the reads done while planning; each names the exact file and command and says what to adjust.

**Type consistency.** `LastRoundPromptTokens`/`LastRoundPromptChars` (Task 2) are consumed by name in Task 4. `PromptTokenEstimator.CountChars/EstimateTokens/CharsPerToken` signatures match across Tasks 1, 2, 4. `ConversationContextStatusDto` fields match Task 5's DTO and TS type. `IContextWindowResolver.Resolve(string, int?)` matches W1.
