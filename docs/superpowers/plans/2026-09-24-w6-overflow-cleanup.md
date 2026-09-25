# W6: Overflow Cleanup Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remove `ThreadRun`'s destructive context-overflow "unwind" (the mechanism that silently replaces a message's content with an abort notice and retries) now that W4/W8 give the user a real remedy — the Compact button. On overflow, `ChatContextOverflowException` must propagate immediately, with **no message content mutated**, and the error the user sees must name compaction as the remedy.

**Architecture:** No new files, no new services — this is a removal plus two small, unrelated text fixes. Everything unwind-related lives in a single file, `ThreadRun.cs`, and comes out as one unit. `MessageAddedEventArgs.IsReplacement` — the flag unwind used to signal "update in place, don't insert" — turns out to have zero readers anywhere in the codebase (verified by exhaustive grep, see *Findings*), so it comes out too, as dead weight the unwind removal exposes rather than something the removal depends on. Two other files carry comments that explain themselves by referencing unwind; those get reworded, not restructured, since the behavior they describe (dedup by `ToolCallId`) is written generically and stays correct and non-dead regardless. Finally, `StreamingErrorEnvelope`'s `chat_context_overflow` message text gets sharpened to name Compact, matching what the client already does independently (W8.7, already shipped).

**Tech Stack:** `AntRunner.Chat` (pure C#, no ASP.NET/EF dependency), MSTest + FluentAssertions, reflection-based static-cache seeding (established pattern, see `AssistantUtilityCacheTests.cs`).

**Spec:** [`docs/superpowers/specs/2026-09-21-context-compaction-design.md`](../specs/2026-09-21-context-compaction-design.md) — *Decisions* D5, *Error handling*, *Testing* item 4, *Open questions for implementation planning* item 1. Checklist: [`docs/context-compaction-plan.md`](../../context-compaction-plan.md) W6 (6.1–6.5).

## Global Constraints

- **Sequencing precondition already met.** W6 was gated on "must land after W4 + W8's button" — both are checked off in `docs/context-compaction-plan.md` (W4: boundary + compact endpoint; W8: client meter + Compact button + the sharpened client-side overflow message). Nothing blocks starting this plan.
- **The exception must propagate as a plain, uncaught exception from `ThreadRun.ExecuteAsync`.** No replacement catch block, no new classification logic. `ConversationStreamEngine.cs`'s existing outer `catch (Exception ex)` (line ~856) already handles it: it calls `RecordLearnedContextWindow` (unaffected by this plan, see *Findings*) and surfaces the error via `StreamingErrorEnvelope`.
- **No message content may be swapped, ever, once this lands.** This is the literal acceptance test (spec *Testing* item 4, checklist 6.5) and the reason unwind exists to be removed — it's the single piece of code in the entire codebase that mutates persisted message content.
- **Server tests: MSTest (`[TestClass]`/`[TestMethod]`) + FluentAssertions**, no Moq needed for this plan — a bespoke `IChatCompletionClient`/`IChatCompletionClientFactory` fake (mirroring the established `FakeChatCompletionClientFactory` shape in `GuideAntsApi.IntegrationTests`) is simpler for a "throws on every call, count the calls" test than mocking two interfaces with Moq.
- **`AssistantUtility`'s static cache is process-global.** Any test that seeds it via reflection must clear it in both `[TestInitialize]` and `[TestCleanup]` and carry `[DoNotParallelize]`, exactly as `AssistantUtilityCacheTests.cs` already does — copy that convention, don't invent a new one.
- Never co-author commits, and only commit when the user has said to (per `CLAUDE.md` and memory `no-commits-without-permission`). The commit steps below are proposed checkpoints — ask first.

## Findings that shaped this plan

Two research passes (one Explore agent per open question) answered the checklist's own "verify first" items (6.1, 6.2) and the spec's "open question for implementation planning." Both are answered from code, not opinion — every claim below is grep-verified against `feature/compaction` HEAD `3822a96`.

- **6.1's premise needs correcting.** The checklist and spec both frame the risk as "does `IsReplacement` have consumers *beyond* the unwind path?" The real answer is stronger: **`IsReplacement` has zero readers anywhere**, unwind included. It's written once (`ThreadRun.cs:1355-1361`, inside `TryUnwindOversizedMessage`, the only call site across the whole repo that passes `isReplacement: true`) and never read — `grep -rn "\.IsReplacement\b"` across all of `src/` returns nothing. `ConversationStreamEngine.cs`'s `onMessageAdded` handler and its downstream `HandleAssistantMessageAdded`/`HandleToolMessageAdded` never touch it. It is deletable with zero blast radius, which is why Task 2 removes it as part of the same change rather than leaving it as inert vestige.
- **The checklist's own line citations for `ConversationPersistence.cs:769` and `ConversationHistoryBuilder.cs:607` are close but not exact, and don't point at `IsReplacement` usage — they point at *comments* that mention overflow-unwind in prose.** `ConversationPersistence.cs:769-770` is a comment on `CreateToolMessageAsync`'s dedup-by-`ToolCallId` logic; the actual dedup below it runs on **any** repeated `ToolCallId`, not gated on `IsReplacement` or any unwind-specific signal — the comment already says "(and retries)," i.e. it was written to cover more than just unwind. `ConversationHistoryBuilder.cs`'s equivalent comment is at **line 739**, not 607 (607 is an unrelated `Role` switch's fallback arm) — it's the doc comment on `IndexToolMessagesByCallId`, whose `GroupBy`/keep-latest logic is explicitly labeled "defense-in-depth" for any duplicate-`ToolCallId` row, not an unwind-only mechanism. **Conclusion: neither method becomes dead code when unwind is removed, and neither needs a functional change** — only their comments, which cite unwind as their motivating example, get reworded in Task 3 so they don't reference a mechanism that no longer exists.
- **6.2 confirmed: no existing test asserts unwind behavior.** `ConversationCoreTests.cs`'s `EventArgs_Constructors_assign_expected_properties` constructs a `MessageAddedEventArgs` but never passes or asserts `isReplacement`. `ContextOverflowLearningTests.cs` tests `ConversationStreamEngine.RecordLearnedContextWindow` in isolation, calling it directly with hand-built exceptions — it never goes through `ThreadRun`'s loop and is unaffected by anything in this plan. **`StreamingErrorEnvelopeTests.cs` has zero coverage of the `chat_context_overflow` branch at all** — that gap is real and Task 4 fills it. No test needs to be *updated* for the removal; nothing currently exercises `TryUnwindOversizedMessage`/`BuildAbortReplacement`/`BuildContextOverflowNotice`.
- **There is currently no test exercising `ThreadRun.ExecuteAsync`'s completion loop at all** — the closest existing file, `ThreadRunTests.cs`, only reaches `ThreadRun`'s pure static helpers via reflection, never the instance loop itself (it needs `AssistantUtility` + a client factory, which no test file currently stands up). Task 1 builds this from scratch, seeding `AssistantUtility`'s cache exactly as `AssistantUtilityCacheTests.cs` does (confirmed sufficient: `new AssistantDefinition { Name = ..., Model = ... }` reaches the first `api.GetCompletionAsync` call without touching any other DB-backed path, since `ReasoningEffort` stays null and `ResolvedExecutionPolicy.Parameters` stays empty — both short-circuit `DatabaseStorage.ResolveModelReasoningEffortAsync` before it opens a `DbContext`, per `DatabaseStorage.cs:197-200`).
- **Learning is decoupled from unwind and does not need to change.** `RecordLearnedContextWindow` (`ConversationStreamEngine.cs:65-79`) is called from exactly one place, the stream engine's outer `catch (Exception ex)` (`ConversationStreamEngine.cs:891`) — after `ThreadRun.ExecuteAsync` has already thrown all the way out. It reads only the exception's *type* and `ContextSize`, nothing from `ThreadRun`'s internals. Today, with unwind in place, this only fires for the rare "unwind exhausted, nothing left to shrink" case (matching the note already in `docs/context-compaction-plan.md:77`: *"until W6 removes the unwind, overflows the unwind absorbs are not learned"*). Removing unwind makes `ChatContextOverflowException` propagate on the **first** overflow instead of only the exhausted case — learning coverage improves, nothing breaks, and no code in `LearnedContextWindowCache.cs`/`ConversationStreamEngine.cs` needs to change for this.
- **6.4's target string is presently unused for the primary UI, but still worth sharpening.** `useStreamingEventHandler.ts:372-378` (shipped in commit `8753cb9`, part of the already-checked-off W8.7) unconditionally overwrites the displayed message for `code === 'chat_context_overflow'` with its own Compact-naming copy, for both the persistent banner and the toast — so the server string at `StreamingErrorEnvelope.cs:102` is dead for the app's main surface today. It's still worth fixing for consistency and any other consumer of the raw envelope (logs, the raw API, a future client) — small, low-risk text change, done in Task 4.
- **`ChatContextOverflowException` has no intermediate base class** — it derives directly from `Exception` (`AntRunner.Chat.Abstractions/ChatContextOverflowException.cs:9`). There is no `ChatCompletionException` type in this codebase; don't write a catch clause assuming one exists.
- **`ThreadRun.ExecuteAsync`'s message-seeding always includes at least one non-system message** before the first completion call (every branch at `ThreadRun.cs:433-495` unconditionally appends a `User` message). Concretely: today, a fake client that throws `ChatContextOverflowException` on every call is called **twice** before the exception reaches the caller — round 1 unwinds the single eligible (non-system) message and retries; round 2 finds nothing left to unwind and rethrows. This is the exact, reproducible red state Task 1's test starts from.

## File Structure

**Created:**
- `src/server/GuideAntsApi.Tests/ChatLayer/ThreadRunContextOverflowTests.cs`

**Modified:**
- `src/server/AntRunner.Chat/AntRunner.Chat/ThreadRun.cs` — remove `unwoundMessages`, the unwind catch block, `TryUnwindOversizedMessage`, `BuildAbortReplacement`, `BuildContextOverflowNotice`; reword two adjacent doc comments that reference "the engine's unwind/retry path"
- `src/server/AntRunner.Chat/AntRunner.Chat/Conversation.cs` — remove `MessageAddedEventArgs.IsReplacement` (dead flag) and its constructor parameter
- `src/server/GuideAntsApi/Services/Conversations/Persistence/ConversationPersistence.cs` — reword one comment (no functional change)
- `src/server/GuideAntsApi/Services/Conversations/Mapping/ConversationHistoryBuilder.cs` — reword one doc comment (no functional change)
- `src/server/GuideAntsApi/Services/Conversations/StreamingErrorEnvelope.cs` — sharpen the `chat_context_overflow` message text and its motivating comment
- `src/server/GuideAntsApi.Tests/Services/Conversations/StreamingErrorEnvelopeTests.cs` — add the missing overflow-branch coverage

---

## Task 1: Red test proving today's unwind behavior, written as the target behavior (6.5's core assertion)

**Files:**
- Create: `src/server/GuideAntsApi.Tests/ChatLayer/ThreadRunContextOverflowTests.cs`

**Interfaces:**
- Consumes: `ThreadRun.ExecuteAsync` (public, `AntRunner.Chat`), `AssistantUtility` (cache-seedable via reflection, `AntRunner.Chat`), `IChatCompletionClient`/`IChatCompletionClientFactory` (`AntRunner.Chat.Abstractions`), `ChatRunOptions`, `ResolvedExecutionPolicy`, `InvocationContext` (`AntRunner.ToolCalling`).
- Produces: nothing new — this is a pure test file, asserting the target behavior W6 is meant to deliver.

This test is written against the **target** (post-removal) behavior, not today's behavior, so it is expected to fail until Task 2 lands. That's the TDD contract for this plan: it is the executable form of checklist item 6.5 and spec *Testing* item 4 ("assert explicitly that no message content changed").

- [ ] **Step 1: Write the failing test**

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
public sealed class ThreadRunContextOverflowTests
{
    [TestInitialize]
    public void SetUp() => AssistantUtility.ClearAllCache();

    [TestCleanup]
    public void TearDown() => AssistantUtility.ClearAllCache();

    [TestMethod]
    public async Task ExecuteAsync_ContextOverflow_PropagatesImmediately_WithNoMessageContentMutated()
    {
        const string assistantName = "Overflow Test Assistant";
        SeedAssistantCache(assistantName, new AssistantDefinition { Name = assistantName, Model = "gpt-4o-mini" });

        var client = new ThrowingClient();
        var factory = new SingleClientFactory(client);

        var options = new ChatRunOptions
        {
            AssistantName = assistantName,
            Instructions = "original-instructions-marker",
            ExecutionPolicy = new ResolvedExecutionPolicy(
                "gpt-4o-mini",
                "openai-chat",
                ParameterAuthority.AssistantDefinition,
                new Dictionary<string, JsonElement>())
        };
        var ctx = new InvocationContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        var act = () => ThreadRun.ExecuteAsync(
            options,
            factory,
            previous: null,
            httpClient: null,
            onMessage: null,
            onStream: null,
            ctx,
            CancellationToken.None);

        await act.Should().ThrowAsync<ChatContextOverflowException>();

        // D5: overflow fails cleanly. No unwind, no retry -- exactly one call, ever.
        client.CallCount.Should().Be(1,
            "the exception must propagate on the very first overflow, not after an unwind-and-retry");

        client.CapturedRequests.Should().ContainSingle();
        var requestText = string.Join(" ", client.CapturedRequests[0].Messages.Select(m => m.GetText()));
        requestText.Should().Contain("original-instructions-marker",
            "the original message content must survive untouched");
        requestText.Should().NotContain("aborted due to size restrictions",
            "no message may ever be replaced with an abort notice");
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

    private sealed class ThrowingClient : IChatCompletionClient
    {
        public bool SupportsToolChoiceNone => true;
        public int CallCount { get; private set; }
        public List<ChatCompletionRequest> CapturedRequests { get; } = [];

        public Task<ChatCompletionResponse> GetCompletionAsync(
            ChatCompletionRequest request, CancellationToken cancellationToken = default)
        {
            CallCount++;
            CapturedRequests.Add(request);
            throw new ChatContextOverflowException(
                "The request exceeded the model's context window.",
                promptTokens: 9001,
                contextSize: 4096);
        }

        public Task<ChatCompletionResponse> StreamCompletionAsync(
            ChatCompletionRequest request,
            Action<ChatCompletionChunk> onChunk,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Test uses the non-streaming path only.");
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

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test src/server/GuideAntsApi.Tests/GuideAntsApi.Tests.csproj --filter "FullyQualifiedName~ThreadRunContextOverflowTests"`
Expected: **FAIL** — `client.CallCount.Should().Be(1)` fails with actual `2`. Per *Findings*, today's `TryUnwindOversizedMessage` finds the single eligible (non-system) message on round 1, replaces it, and retries; round 2 finds nothing left to unwind and rethrows. This is the reproducible red state confirming the test targets the right behavior.

- [ ] **Step 3: Commit (ask first)**

```bash
git add src/server/GuideAntsApi.Tests/ChatLayer/ThreadRunContextOverflowTests.cs
git commit -m "Adds a red test for immediate, non-mutating context-overflow propagation"
```

---

## Task 2: Remove the unwind mechanism (6.1, 6.3)

**Files:**
- Modify: `src/server/AntRunner.Chat/AntRunner.Chat/ThreadRun.cs`
- Modify: `src/server/AntRunner.Chat/AntRunner.Chat/Conversation.cs`

**Interfaces:**
- Removes: `ThreadRun.TryUnwindOversizedMessage`, `ThreadRun.BuildAbortReplacement`, `ThreadRun.BuildContextOverflowNotice` (all `private static`, zero external callers per *Findings*), the `unwoundMessages` local, and `MessageAddedEventArgs.IsReplacement` plus its constructor parameter (zero readers anywhere per *Findings* — safe to delete outright rather than leave as vestige).
- No other file's compiled behavior changes: `ConversationPersistence.cs` and `ConversationHistoryBuilder.cs`'s dedup-by-`ToolCallId` logic is untouched (it was never gated on `IsReplacement`), and `ConversationStreamEngine.cs`'s outer catch/learning path needs no change (see *Findings*).

- [ ] **Step 1: Run Task 1's test to confirm the starting red state**

Run: `dotnet test src/server/GuideAntsApi.Tests/GuideAntsApi.Tests.csproj --filter "FullyQualifiedName~ThreadRunContextOverflowTests"`
Expected: FAIL (as in Task 1, Step 2).

- [ ] **Step 2: Implement — `ThreadRun.cs`**

Remove the tracking field and its comment (currently `ThreadRun.cs:580-582`):

```csharp
            // Messages already replaced by an abort notice after a context-overflow rejection.
            // Tracked so each retry targets a different (next-largest) message and the loop converges.
            var unwoundMessages = new HashSet<ChatMessage>();
```

Delete this block entirely — no replacement.

Replace the try/catch around the completion call (currently `ThreadRun.cs:618-634`):

```csharp
                    ChatCompletionResponse response;
                    try
                    {
                        response = await InvokeCompletionAsync(api, chatRequest, onStream, token);
                    }
                    catch (ChatContextOverflowException overflowEx)
                    {
                        // Unwind the largest offending message in this turn, replace it with a short
                        // abort notice, and retry. If nothing can be unwound the request is
                        // irreducible (e.g. the system prompt alone overflows) so we rethrow.
                        if (TryUnwindOversizedMessage(messages, unwoundMessages, overflowEx, tracedMessageAdded))
                        {
                            continue;
                        }

                        throw;
                    }
```

with:

```csharp
                    // Context overflow fails cleanly (D5) -- no unwind, no retry, no message
                    // content ever mutated. ChatContextOverflowException propagates like any other
                    // exception; ConversationStreamEngine's outer catch surfaces it to the client as
                    // chat_context_overflow and still records the learned context window from it.
                    var response = await InvokeCompletionAsync(api, chatRequest, onStream, token);
```

Delete `TryUnwindOversizedMessage`, `BuildAbortReplacement`, and `BuildContextOverflowNotice` in full (currently `ThreadRun.cs:1301-1390`, including the doc comment on `TryUnwindOversizedMessage`) — nothing calls any of the three after the step above.

Reword `InvokeCompletionAsync`'s doc comment, which still references the removed path (currently `ThreadRun.cs:1263-1269`):

```csharp
        /// <summary>
        /// Single provider-agnostic chokepoint for issuing a completion. Normalizes any provider's
        /// "context window exceeded" failure into <see cref="ChatContextOverflowException"/> so the
        /// engine's unwind/retry path is identical regardless of which chat provider is in use.
        /// Raw-HTTP clients that strip their body (e.g. llama-server) already throw the typed
        /// exception; SDK-based clients (OpenAI, Anthropic) surface the marker text in their thrown
        /// exception and are translated here.
        /// </summary>
```

to:

```csharp
        /// <summary>
        /// Single provider-agnostic chokepoint for issuing a completion. Normalizes any provider's
        /// "context window exceeded" failure into <see cref="ChatContextOverflowException"/> so every
        /// provider surfaces the same typed exception, regardless of which chat provider is in use.
        /// Raw-HTTP clients that strip their body (e.g. llama-server) already throw the typed
        /// exception; SDK-based clients (OpenAI, Anthropic) surface the marker text in their thrown
        /// exception and are translated here. The caller lets it propagate uncaught (D5) -- no
        /// unwind, no retry.
        /// </summary>
```

And its inline comment on the pass-through catch (currently `ThreadRun.cs:1289`), from:

```csharp
            catch (ChatContextOverflowException)
            {
                // Already classified at the source (e.g. llama client) — let it flow to the unwinder.
                throw;
            }
```

to:

```csharp
            catch (ChatContextOverflowException)
            {
                // Already classified at the source (e.g. llama client) — let it propagate (D5).
                throw;
            }
```

- [ ] **Step 3: Implement — `Conversation.cs`**

Remove `IsReplacement` and the `isReplacement` constructor parameter from `MessageAddedEventArgs` (currently `Conversation.cs:8-37`):

```csharp
    public class MessageAddedEventArgs : EventArgs
    {
        public string Message { get; }
        public string Role { get; }
        public string? ToolCallId { get; }
        public string? FunctionName { get; }
        public string? ToolCallsJson { get; }

        /// <summary>
        /// True when this event replaces content already present for the same tool/assistant
        /// message (e.g. context-overflow unwind). Persistence must update in place, not insert.
        /// </summary>
        public bool IsReplacement { get; }

        public MessageAddedEventArgs(
            string role,
            string newMessage,
            string? toolCallId = null,
            string? functionName = null,
            string? toolCallsJson = null,
            bool isReplacement = false)
        {
            Message = newMessage;
            Role = role;
            ToolCallId = toolCallId;
            FunctionName = functionName;
            ToolCallsJson = toolCallsJson;
            IsReplacement = isReplacement;
        }
    }
```

to:

```csharp
    public class MessageAddedEventArgs : EventArgs
    {
        public string Message { get; }
        public string Role { get; }
        public string? ToolCallId { get; }
        public string? FunctionName { get; }
        public string? ToolCallsJson { get; }

        public MessageAddedEventArgs(
            string role,
            string newMessage,
            string? toolCallId = null,
            string? functionName = null,
            string? toolCallsJson = null)
        {
            Message = newMessage;
            Role = role;
            ToolCallId = toolCallId;
            FunctionName = functionName;
            ToolCallsJson = toolCallsJson;
        }
    }
```

This is safe because every other `new MessageAddedEventArgs(...)` call site across the repo (~13 in `ThreadRun.cs`, 1 in `ConversationCoreTests.cs`) already omits `isReplacement` — the only call site that ever passed it was inside `TryUnwindOversizedMessage`, deleted in Step 2.

- [ ] **Step 4: Run to verify Task 1's test passes**

Run: `dotnet test src/server/GuideAntsApi.Tests/GuideAntsApi.Tests.csproj --filter "FullyQualifiedName~ThreadRunContextOverflowTests"`
Expected: **PASS** — `CallCount` is now `1`, and the request text contains the original marker with no abort notice.

- [ ] **Step 5: Run the adjacent test files to confirm no regression**

Run: `dotnet test src/server/GuideAntsApi.Tests/GuideAntsApi.Tests.csproj --filter "FullyQualifiedName~ConversationCoreTests|FullyQualifiedName~ThreadRunTests|FullyQualifiedName~ThreadRunLastRoundUsageTests|FullyQualifiedName~ContextOverflowLearningTests"`
Expected: PASS, no changes needed to any of these four files — per *Findings*, none of them reference the removed symbols or `IsReplacement`.

- [ ] **Step 6: Commit (ask first)**

```bash
git add src/server/AntRunner.Chat/AntRunner.Chat/ThreadRun.cs src/server/AntRunner.Chat/AntRunner.Chat/Conversation.cs
git commit -m "Removes the context-overflow unwind and the dead IsReplacement flag"
```

---

## Task 3: Reword the two comments that explain themselves via the removed mechanism

**Files:**
- Modify: `src/server/GuideAntsApi/Services/Conversations/Persistence/ConversationPersistence.cs`
- Modify: `src/server/GuideAntsApi/Services/Conversations/Mapping/ConversationHistoryBuilder.cs`

**Interfaces:** None — comment-only changes. Confirmed by *Findings* that neither method's actual logic (dedup by `ToolCallId`) is gated on anything unwind produced, so nothing here is dead code and nothing here needs a behavioral change.

This task has no test step: it changes zero behavior, so there is nothing new to assert. `ConversationPersistence`/`ConversationHistoryBuilder`'s existing test suites are unaffected and are not expected to change.

- [ ] **Step 1: `ConversationPersistence.cs`**

Currently (`:769-770`):

```csharp
                // One tool result per ToolCallId. Context-overflow unwind (and retries) must update
                // in place — inserting a second row with the same id breaks history rebuild.
```

Change to:

```csharp
                // One tool result per ToolCallId. A retried tool call must update the existing row
                // in place — inserting a second row with the same id breaks history rebuild.
```

- [ ] **Step 2: `ConversationHistoryBuilder.cs`**

Currently (`:738-740`):

```csharp
    /// <summary>
    /// One tool result per call id. When duplicates exist (e.g. pre-fix overflow unwind inserts),
    /// keep the latest sequence so the model sees the replacement notice, not the oversized payload.
    /// </summary>
```

Change to:

```csharp
    /// <summary>
    /// One tool result per call id. Defense-in-depth: if a duplicate row for the same call id
    /// exists for any reason, keep only the latest sequence so history rebuild sees exactly one result.
    /// </summary>
```

- [ ] **Step 3: Confirm the build is unaffected**

Run: `dotnet build src/server/GuideAntsApi.sln`
Expected: builds clean — these are comment-only edits inside method bodies/doc comments.

- [ ] **Step 4: Commit (ask first)**

```bash
git add src/server/GuideAntsApi/Services/Conversations/Persistence/ConversationPersistence.cs src/server/GuideAntsApi/Services/Conversations/Mapping/ConversationHistoryBuilder.cs
git commit -m "Rewords two comments that cited the removed overflow-unwind mechanism"
```

---

## Task 4: Sharpen the `chat_context_overflow` message and fill the test gap (6.4, part of 6.5)

**Files:**
- Modify: `src/server/GuideAntsApi/Services/Conversations/StreamingErrorEnvelope.cs`
- Modify: `src/server/GuideAntsApi.Tests/Services/Conversations/StreamingErrorEnvelopeTests.cs`

**Interfaces:**
- Modifies: `StreamingErrorEnvelope.BuildCore`'s `chat_context_overflow` branch — same shape (`code`, `message`, `type`, `promptTokens`, `contextSize`, `innerMessage`, `timestamp`), only `message`'s text and the motivating comment above it change.
- Per *Findings*, this branch has **zero** existing test coverage — both new tests below are net new, not replacements.

- [ ] **Step 1: Write the failing tests**

Add to `StreamingErrorEnvelopeTests.cs`, matching the file's existing 2-space-indent style and its `Serialize(...)` helper:

```csharp
  [TestMethod]
  public void Build_Maps_context_overflow_to_chat_context_overflow_code_naming_compaction()
  {
    var ex = new ChatContextOverflowException(
      "The request exceeded the model's context window.",
      promptTokens: 9001,
      contextSize: 4096,
      upstreamDetail: "upstream detail text");

    var json = Serialize(StreamingErrorEnvelope.Build(ex));

    json.GetProperty("code").GetString().Should().Be("chat_context_overflow");
    json.GetProperty("type").GetString().Should().Be(nameof(ChatContextOverflowException));
    json.GetProperty("message").GetString().Should().ContainEquivalentOf("compact");
    json.GetProperty("promptTokens").GetInt32().Should().Be(9001);
    json.GetProperty("contextSize").GetInt32().Should().Be(4096);
    json.GetProperty("innerMessage").GetString().Should().Be("upstream detail text");
  }

  [TestMethod]
  public void Build_Wraps_chat_conversation_exception_inner_context_overflow()
  {
    var inner = new ChatContextOverflowException("too large", promptTokens: 100, contextSize: 50);
    var ex = new ChatConversationException(inner, chatRunOutput: null);

    var json = Serialize(StreamingErrorEnvelope.Build(ex));

    json.GetProperty("code").GetString().Should().Be("chat_context_overflow");
  }
```

`ChatContextOverflowException` (`AntRunner.Chat.Abstractions`) and `ChatConversationException` (`AntRunner.Chat`) are both already in scope via this file's existing `using AntRunner.Chat;` line — no new `using` needed.

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test src/server/GuideAntsApi.Tests/GuideAntsApi.Tests.csproj --filter "FullyQualifiedName~StreamingErrorEnvelopeTests"`
Expected: FAIL — `Build_Maps_context_overflow_to_chat_context_overflow_code_naming_compaction` fails on `message.Should().ContainEquivalentOf("compact")`; today's string is "Retry with a smaller message or a different approach," which doesn't mention compaction. The second test should already pass (it doesn't touch the message text), confirming the envelope's overflow branch itself is otherwise correct.

- [ ] **Step 3: Implement**

In `StreamingErrorEnvelope.cs`, currently (`:94-102`):

```csharp
        // Context overflow that survived the engine's unwind/retry (e.g. the system prompt alone
        // exceeds the window). Surface a distinct code so the UI can prompt for a smaller request.
        var overflow = ex as ChatContextOverflowException ?? inner as ChatContextOverflowException;
        if (overflow != null)
        {
            return new
            {
                code = "chat_context_overflow",
                message = "The request was too large for the model's context window. Retry with a smaller message or a different approach.",
```

Change to:

```csharp
        // Context overflow. No unwind/retry exists anymore (D5) -- this fires on the very first
        // round that exceeds the window. Compaction is the remedy; the client independently
        // overrides this message with the same framing (useStreamingEventHandler.ts), but this
        // string should say the same thing for any other consumer of the raw envelope.
        var overflow = ex as ChatContextOverflowException ?? inner as ChatContextOverflowException;
        if (overflow != null)
        {
            return new
            {
                code = "chat_context_overflow",
                message = "The request was too large for the model's context window. Use Compact in the conversation to summarize older messages, then retry.",
```

The remaining fields (`type`, `promptTokens`, `contextSize`, `innerMessage`, `timestamp`) are unchanged.

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test src/server/GuideAntsApi.Tests/GuideAntsApi.Tests.csproj --filter "FullyQualifiedName~StreamingErrorEnvelopeTests"`
Expected: PASS, all tests in the file including the two new ones.

- [ ] **Step 5: Commit (ask first)**

```bash
git add src/server/GuideAntsApi/Services/Conversations/StreamingErrorEnvelope.cs src/server/GuideAntsApi.Tests/Services/Conversations/StreamingErrorEnvelopeTests.cs
git commit -m "Names compaction as the remedy in the server-side chat_context_overflow message"
```

---

## Task 5: Full verification (6.5 rollup)

- [ ] **Step 1: Server build and unit tests**

Run: `dotnet build src/server/GuideAntsApi.sln && dotnet test src/server/GuideAntsApi.sln --filter "TestCategory!=Integration"`
Expected: builds clean, no new failures.

- [ ] **Step 2: Confirm the removal is complete and clean**

Run: `grep -rn "TryUnwindOversizedMessage\|BuildAbortReplacement\|BuildContextOverflowNotice\|unwoundMessages\|IsReplacement" src/server --include="*.cs"`
Expected: zero matches anywhere in `src/server` (docs/plan files are exempt — this plan and the design spec legitimately name these symbols historically).

- [ ] **Step 3: Confirm the independent `TerminationCode` producer is untouched**

`ConversationTurnTerminalizer.cs`'s `MapTerminationCode` maps `ChatContextOverflowException` to `"chat_context_overflow"` independently of `StreamingErrorEnvelope` (same exception type, persisted as the turn's DB-level termination code rather than the SSE `code` field). Confirm by reading `ConversationTurnTerminalizer.cs:62-89` that this mapping is unchanged and still correct — no test or code change is expected here, this is a read-only check.

- [ ] **Step 4: Manual end-to-end check (if a small-context local model is available)**

Per the design spec's integration test (*Testing*, "Integration"): run a long conversation against a small-context local llama.cpp model until it overflows, confirm the SSE error carries `chat_context_overflow` with the new message text, confirm no prior message's content changed in the transcript, then press Compact and confirm the conversation continues. If no local llama.cpp runtime is available in this environment, skip this step and say so explicitly rather than claiming it passed — this is W9's full integration-test job (`docs/context-compaction-plan.md` 9.3), not blocking for W6 itself.

- [ ] **Step 5: Tick W6 in the checklist and resolve Q-ii**

In `docs/context-compaction-plan.md`:
- Tick 6.1–6.5.
- Add a line under the W6 heading linking this plan, matching the "Implementation plan written" pattern used for W1–W5/W8.
- Under *Open questions*, mark Q-ii resolved: `IsReplacement` has zero readers anywhere (not just outside the unwind path), and the two cited call sites (`ConversationPersistence.cs`, `ConversationHistoryBuilder.cs`) reference generic dedup-by-`ToolCallId` comments, not `IsReplacement` reads — both stay correct and non-dead after removal, and their comments were reworded rather than their logic touched. Note the checklist's original line citations (`ConversationHistoryBuilder.cs:607`) pointed at an unrelated `Role` switch; the real comment was at line 739.

---

## Self-review

**Spec coverage.**
- 6.1 (verify `IsReplacement` consumers) → *Findings*, resolved as "zero consumers anywhere," stronger than the checklist's framing; folded into Task 2's removal rather than left as a separate no-op step.
- 6.2 (verify no test asserts unwind behavior) → *Findings*, confirmed via exhaustive grep across every test project; no existing test needed updating.
- 6.3 (remove the four unwind symbols) → Task 2.
- 6.4 (sharpen the message, name compaction) → Task 4.
- 6.5 (test: no message content mutated) → Task 1 (the `ThreadRun`-level test, the strongest possible evidence — it drives the actual completion loop and inspects the actual request sent) plus Task 4's new `StreamingErrorEnvelopeTests` coverage (fills a pre-existing gap in the error-shape layer). Task 5, Step 4 covers the full integration path when a local model is available.
- Spec's "Engine throws: log, proceed with uncompacted history" and "Compact pressed with no complete turn: no-op" rows are unaffected by this plan — they belong to W4/W5, already shipped.
- Design spec's *Open questions for implementation planning* item 1 → resolved in *Findings* and Task 5, Step 5.

**Known limitations, deliberately accepted.**
- Task 5's manual end-to-end check depends on a local llama.cpp runtime being available in the execution environment; if it isn't, that step is explicitly skipped rather than silently claimed, consistent with how W8's plan handled its own unavailable-dev-stack step.
- `StreamingErrorEnvelope`'s sharpened string is currently unreachable from the primary UI (the client already overrides it, per *Findings*) — this task ships it anyway for consistency and any other consumer, not because it's user-visible-blocking.

**Placeholder scan.** Every code step has complete code, not a sketch. Every "currently" code block quoted for replacement was read directly from the file at the cited line numbers as part of writing this plan (not reconstructed from the checklist's citations, which were shown to be slightly off for two of the four).

**Type consistency.** `MessageAddedEventArgs`'s constructor signature change (Task 2, Step 3) is checked against every call site across the repo (~13 in `ThreadRun.cs`, 1 in `ConversationCoreTests.cs`) — none pass `isReplacement`, so removing the trailing optional parameter cannot break a caller. `StreamingErrorEnvelope.BuildCore`'s anonymous-object shape for the overflow branch (Task 4) is unchanged field-for-field; only two string literals and a comment move.
