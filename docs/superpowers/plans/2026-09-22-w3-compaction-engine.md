# W3: Compaction Engine Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship `CompactionEngine.Compact(preBoundaryMessages, turnFacts) -> CompactionResult` — a pure, deterministic, algorithmic summarizer that later workstreams (W4's compact endpoint, W5's history-builder wiring) call to turn everything before a user-chosen boundary into a fixed-vocabulary handoff briefing.

**Architecture:** A five-stage pipeline, each stage its own `internal` static type: normalize `ChatMessage[]` into uniform `CompactionBlock`s → filter noise (empty blocks, tool-limit scaffolding) → run five independent extractors (Goal, Artifacts, Activity ledger, Unresolved errors, Directives) → render into one system-message body with a handoff framing line. Only `CompactionEngine`, `CompactionResult`, and `TurnFacts` are public; everything in between is an implementation detail the pipeline is free to refactor. No cut-selection stage — the user's compact press already supplies the boundary (W4), so there is no token-budget backwalk here.

**Tech Stack:** .NET 8, `AntRunner.Chat` (pure library, no ASP.NET/EF), MSTest + FluentAssertions.

**Spec:** [`docs/superpowers/specs/2026-09-21-context-compaction-design.md`](../specs/2026-09-21-context-compaction-design.md) — *Architecture §1*, *Section vocabulary*. Checklist: [`docs/context-compaction-plan.md`](../../context-compaction-plan.md) W3 (3.1–3.11).

## Global Constraints

- **Pure, deterministic, no I/O.** `CompactionEngine` and every pipeline stage must not reference `GuideAntsApi` or `GuideAntsApi.DataModel`, must not touch the filesystem, the clock, `Guid.NewGuid()`, or any DB — matching the `ToolOutputTruncator` precedent already in `AntRunner.Chat`.
- **Determinism is the property D3 rests on.** Identical input must yield byte-identical `CompactionResult.SummaryText`. Build rendered text with explicit `"\n"` joins, never `StringBuilder.AppendLine`/`Environment.NewLine` — the latter is a portability foot-gun for a value that gets persisted and diffed.
- **No cut-selection stage.** The engine never decides *how much* to summarize — it summarizes exactly the `preBoundaryMessages` it's given. Boundary selection (last complete turn, `tool_calls`/`tool_result` pairing invariants) is W4's job.
- **Fixed section vocabulary only (D6).** Goal, Artifacts, Activity ledger, Unresolved errors, Directives — sourced from protocol-level facts (tool-call shape, `ConversationTurn.FilesCreated`/`FilesModified`, `ScriptExecutionResult.ExitCode`), never content heuristics beyond the two documented regexes (scope-change markers, directive words). No LLM call anywhere in this workstream (D2).
- **Minimal public surface.** Only `CompactionEngine`, `CompactionResult`, and `TurnFacts` are `public` — these are the only three names the spec's Architecture §1 documents as the engine's contract. Every pipeline-internal type (`CompactionBlock`, the five extractors, the renderer) is `internal`. Tests reach them through the existing `<InternalsVisibleTo Include="GuideAntsApi.Tests" />` already declared on `AntRunner.Chat.csproj`.
- **`TurnFacts` is a projection, not a query.** The engine takes already-materialized `IReadOnlyList<TurnFacts>` — turn index plus deserialized file lists. It does not parse `ConversationTurn.FilesCreated`/`FilesModified` JSON itself; that's W5's job when it loads pre-boundary turns.
- **Never fail on degenerate input.** Empty `preBoundaryMessages`, empty `turnFacts`, `null` in either — all return a normal `CompactionResult` with placeholder text (`"(none)"`, `"(none recorded)"`), never throw. The Error handling table in the spec treats "engine cannot reduce" as a normal return, not an exception.
- **Test location.** There is no separate `AntRunner.Chat` test project — W2 put `PromptTokenEstimatorTests.cs` in `GuideAntsApi.Tests/ChatLayer/`. This plan follows the same convention under `GuideAntsApi.Tests/ChatLayer/Compaction/`. MSTest (`[TestClass]`/`[TestMethod]`) + FluentAssertions, matching every existing test in that folder.
- Never co-author commits, and only commit when the user has said to (per `CLAUDE.md` and memory `no-commits-without-permission`). The commit steps below are proposed checkpoints — ask first.

## Findings that shaped this plan

- `ChatToolCallFunction.Arguments` is a `JsonElement`, not a string (`AntRunner.Chat.Abstractions/ChatToolCall.cs:28`). W2's `PromptTokenEstimator.ArgumentsLength` already handles this with a `ValueKind` switch — the normalizer in this plan follows the same shape (`GetRawText()`, not `ToString()`).
- `ScriptExecutionResult` (`AntRunner.ToolCalling.Functions`, used by `ToolOutputTruncator.cs`) with its `ExitCode` is the *only* protocol-guaranteed success/failure signal available. Tool results from `search_project`, `search_notebook`, web search, etc. have no such shape — their outcome is legitimately "unknown," not "ok." This is D2's accepted domain-fit risk; W9's cross-domain benchmark is where it gets checked against real transcripts, not here.
- `AntRunner.Chat` already project-references `AntRunner.ToolCalling` (`ToolOutputTruncator` depends on `AntRunner.ToolCalling.Functions.ScriptExecutionResult`), so referencing `AntRunner.ToolCalling.ToolLimitState` from the new `Compaction` namespace introduces no new project dependency.
- `ToolLimitState` (`AntRunner.Chat/AntRunner.ToolCalling/ToolLimitState.cs`) already exposes the exact scaffolding text the noise filter needs to recognize: `RuntimeOverrideMarker` (`"[Runtime:"`), `BuildSystemNudgeMessage(...)` (contains `"was reached for this turn"`), and `BuildForceCompleteAssistantMessage(...)`. Reusing these instead of re-deriving the strings means the filter can't silently drift out of sync if `ThreadRun.cs`'s wording changes.
- `ChatMessage` has no `TurnIndex`. The only extractor that needs turn boundaries is Artifacts, and it gets them from the separately-passed `TurnFacts` list, not from the message stream — the other four extractors only need message *order*, which the caller already preserves.
- `<InternalsVisibleTo Include="GuideAntsApi.Tests" />` is already declared on `AntRunner.Chat.csproj` (used by W2's `ThreadRun` tests), so no project-file change is needed to make internal pipeline types testable.

---

## File Structure

**Created — implementation (`src/server/AntRunner.Chat/AntRunner.Chat/Compaction/`):**
- `CompactionBlock.cs` — `CompactionBlockKind` enum + `CompactionBlock` record (internal)
- `MessageNormalizer.cs` — `ChatMessage[]` → `List<CompactionBlock>` (internal)
- `NoiseFilter.cs` — drops empty blocks + tool-limit scaffolding (internal)
- `CompactionText.cs` — shared deterministic truncation helper (internal)
- `GoalExtractor.cs` + `GoalSection` (internal)
- `TurnFacts.cs` — **public** projection type
- `ArtifactsExtractor.cs` + `ArtifactsSection` (internal)
- `ActivityLedgerExtractor.cs` + `ActivityLedgerEntry` (internal)
- `UnresolvedErrorsExtractor.cs` + `UnresolvedError` (internal)
- `DirectivesExtractor.cs` + `Directive` (internal)
- `CompactionSummaryRenderer.cs` (internal)
- `CompactionEngine.cs` + `CompactionResult` — **public** entry point

**Created — tests (`src/server/GuideAntsApi.Tests/ChatLayer/Compaction/`):**
- `MessageNormalizerTests.cs`
- `NoiseFilterTests.cs`
- `GoalExtractorTests.cs`
- `ArtifactsExtractorTests.cs`
- `ActivityLedgerExtractorTests.cs`
- `UnresolvedErrorsExtractorTests.cs`
- `DirectivesExtractorTests.cs`
- `CompactionSummaryRendererTests.cs`
- `CompactionEngineTests.cs`

**Modified:** none. W3 is purely additive — no existing file changes anywhere in the repo.

---

## Task 1: Normalize `ChatMessage[]` into uniform blocks (3.3)

**Files:**
- Create: `src/server/AntRunner.Chat/AntRunner.Chat/Compaction/CompactionBlock.cs`
- Create: `src/server/AntRunner.Chat/AntRunner.Chat/Compaction/MessageNormalizer.cs`
- Test: `src/server/GuideAntsApi.Tests/ChatLayer/Compaction/MessageNormalizerTests.cs`

**Interfaces:**
- Consumes: `ChatMessage`, `ChatRole`, `ChatToolCall` from `AntRunner.Chat.Abstractions`; `ScriptExecutionResult` from `AntRunner.ToolCalling.Functions`
- Produces:
  - `internal enum CompactionBlockKind { UserMessage, AssistantMessage, SystemMessage, ToolCall, ToolResult }`
  - `internal sealed record CompactionBlock(CompactionBlockKind Kind, string Text, string? ToolCallId = null, string? ToolName = null, string? ToolArguments = null, bool? ToolSucceeded = null)`
  - `internal static class MessageNormalizer { static List<CompactionBlock> Normalize(IReadOnlyList<ChatMessage> messages) }`

- [ ] **Step 1: Write the failing test**

```csharp
using AntRunner.Chat.Abstractions;
using AntRunner.Chat.Compaction;
using FluentAssertions;

namespace GuideAntsApi.Tests.ChatLayer.Compaction;

[TestClass]
public sealed class MessageNormalizerTests
{
    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [TestMethod]
    public void Normalize_UserMessage_ProducesUserMessageBlock()
    {
        var messages = new[] { new ChatMessage(ChatRole.User, "hello there") };

        var blocks = MessageNormalizer.Normalize(messages);

        blocks.Should().ContainSingle();
        blocks[0].Kind.Should().Be(CompactionBlockKind.UserMessage);
        blocks[0].Text.Should().Be("hello there");
    }

    [TestMethod]
    public void Normalize_SystemAndDeveloperMessages_ProduceSystemMessageBlocks()
    {
        var messages = new[]
        {
            new ChatMessage(ChatRole.System, "system prompt"),
            new ChatMessage(ChatRole.Developer, "developer note")
        };

        var blocks = MessageNormalizer.Normalize(messages);

        blocks.Should().HaveCount(2);
        blocks.Should().OnlyContain(b => b.Kind == CompactionBlockKind.SystemMessage);
    }

    [TestMethod]
    public void Normalize_AssistantTextOnly_ProducesAssistantMessageBlock()
    {
        var messages = new[] { new ChatMessage(ChatRole.Assistant, "here is my answer") };

        var blocks = MessageNormalizer.Normalize(messages);

        blocks.Should().ContainSingle();
        blocks[0].Kind.Should().Be(CompactionBlockKind.AssistantMessage);
    }

    [TestMethod]
    public void Normalize_AssistantWithToolCalls_ProducesToolCallBlocksAndOmitsEmptyText()
    {
        var call = new ChatToolCall
        {
            Id = "call-1",
            Function = new ChatToolCallFunction { Name = "ReadFile", Arguments = Args("{\"path\":\"a.txt\"}") }
        };
        var messages = new[] { new ChatMessage(ChatRole.Assistant, [], [call]) };

        var blocks = MessageNormalizer.Normalize(messages);

        blocks.Should().ContainSingle("the assistant message had no text, only a tool call");
        blocks[0].Kind.Should().Be(CompactionBlockKind.ToolCall);
        blocks[0].ToolCallId.Should().Be("call-1");
        blocks[0].ToolName.Should().Be("ReadFile");
        blocks[0].ToolArguments.Should().Be("{\"path\":\"a.txt\"}");
    }

    [TestMethod]
    public void Normalize_ToolResult_CapturesSuccessFromExitCode()
    {
        var messages = new[]
        {
            new ChatMessage("call-1", "ReadFile",
                [new ChatContent("{\"standardOutput\":\"hi\",\"standardError\":\"\",\"exitCode\":0}")])
        };

        var blocks = MessageNormalizer.Normalize(messages);

        blocks[0].Kind.Should().Be(CompactionBlockKind.ToolResult);
        blocks[0].ToolSucceeded.Should().BeTrue();
    }

    [TestMethod]
    public void Normalize_ToolResult_NonZeroExitCode_IsFailure()
    {
        var messages = new[]
        {
            new ChatMessage("call-1", "RunScript",
                [new ChatContent("{\"standardOutput\":\"\",\"standardError\":\"boom\",\"exitCode\":1}")])
        };

        var blocks = MessageNormalizer.Normalize(messages);

        blocks[0].ToolSucceeded.Should().BeFalse();
    }

    [TestMethod]
    public void Normalize_ToolResult_NonScriptExecutionShape_SucceededIsNull()
    {
        var messages = new[]
        {
            new ChatMessage("call-1", "SearchProject", [new ChatContent("[{\"score\":0.9}]")])
        };

        var blocks = MessageNormalizer.Normalize(messages);

        blocks[0].ToolSucceeded.Should().BeNull();
    }

    [TestMethod]
    public void Normalize_EmptyMessageList_ReturnsEmptyList()
    {
        MessageNormalizer.Normalize(Array.Empty<ChatMessage>()).Should().BeEmpty();
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test src/server/GuideAntsApi.Tests/GuideAntsApi.Tests.csproj --filter "FullyQualifiedName~MessageNormalizerTests"`
Expected: FAIL to compile — `AntRunner.Chat.Compaction` namespace does not exist.

- [ ] **Step 3: Implement**

`CompactionBlock.cs`:

```csharp
namespace AntRunner.Chat.Compaction;

internal enum CompactionBlockKind
{
    UserMessage,
    AssistantMessage,
    SystemMessage,
    ToolCall,
    ToolResult
}

/// <summary>
/// One piece of conversation content, normalized to a shape every extractor can scan without
/// caring whether it came from a <c>ChatMessage</c>'s text, a tool call, or a tool result.
/// </summary>
internal sealed record CompactionBlock(
    CompactionBlockKind Kind,
    string Text,
    string? ToolCallId = null,
    string? ToolName = null,
    string? ToolArguments = null,
    bool? ToolSucceeded = null);
```

`MessageNormalizer.cs`:

```csharp
using AntRunner.Chat.Abstractions;
using AntRunner.ToolCalling.Functions;

namespace AntRunner.Chat.Compaction;

internal static class MessageNormalizer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public static List<CompactionBlock> Normalize(IReadOnlyList<ChatMessage> messages)
    {
        var blocks = new List<CompactionBlock>();
        foreach (var message in messages)
        {
            switch (message.Role)
            {
                case ChatRole.User:
                    blocks.Add(new CompactionBlock(CompactionBlockKind.UserMessage, message.GetText()));
                    break;

                case ChatRole.System:
                case ChatRole.Developer:
                    blocks.Add(new CompactionBlock(CompactionBlockKind.SystemMessage, message.GetText()));
                    break;

                case ChatRole.Assistant:
                    NormalizeAssistantMessage(message, blocks);
                    break;

                case ChatRole.Tool:
                    NormalizeToolResultMessage(message, blocks);
                    break;
            }
        }

        return blocks;
    }

    private static void NormalizeAssistantMessage(ChatMessage message, List<CompactionBlock> blocks)
    {
        var text = message.GetText();
        if (!string.IsNullOrWhiteSpace(text))
        {
            blocks.Add(new CompactionBlock(CompactionBlockKind.AssistantMessage, text));
        }

        if (message.ToolCalls == null)
        {
            return;
        }

        foreach (var call in message.ToolCalls)
        {
            if (!call.IsFunction)
            {
                continue;
            }

            blocks.Add(new CompactionBlock(
                CompactionBlockKind.ToolCall,
                Text: string.Empty,
                ToolCallId: call.Id,
                ToolName: call.Function.Name,
                ToolArguments: call.Function.Arguments.ValueKind == JsonValueKind.Undefined
                    ? null
                    : call.Function.Arguments.GetRawText()));
        }
    }

    private static void NormalizeToolResultMessage(ChatMessage message, List<CompactionBlock> blocks)
    {
        var text = message.GetText();
        blocks.Add(new CompactionBlock(
            CompactionBlockKind.ToolResult,
            text,
            ToolCallId: message.ToolCallId,
            ToolName: message.FunctionName,
            ToolSucceeded: TryGetToolSuccess(text)));
    }

    // ScriptExecutionResult.ExitCode is the only protocol-guaranteed success signal (D2: no
    // content heuristics). Any other JSON-or-not shape is neither ok nor error - "unknown".
    private static bool? TryGetToolSuccess(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<ScriptExecutionResult>(text, JsonOptions);
            return parsed?.ExitCode is int code ? code == 0 : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
```

`System.Text.Json` is already a global using in this project (`AntRunner.Chat/GlobalUsings.cs:3`), so no explicit `using System.Text.Json;` is needed here.

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test src/server/GuideAntsApi.Tests/GuideAntsApi.Tests.csproj --filter "FullyQualifiedName~MessageNormalizerTests"`
Expected: PASS (7 tests).

- [ ] **Step 5: Commit (ask first)**

```bash
git add src/server/AntRunner.Chat/AntRunner.Chat/Compaction/CompactionBlock.cs src/server/AntRunner.Chat/AntRunner.Chat/Compaction/MessageNormalizer.cs src/server/GuideAntsApi.Tests/ChatLayer/Compaction/MessageNormalizerTests.cs
git commit -m "Normalizes chat messages into uniform compaction blocks"
```

---

## Task 2: Filter noise (3.4)

**Files:**
- Create: `src/server/AntRunner.Chat/AntRunner.Chat/Compaction/NoiseFilter.cs`
- Test: `src/server/GuideAntsApi.Tests/ChatLayer/Compaction/NoiseFilterTests.cs`

**Interfaces:**
- Consumes: `CompactionBlock`, `CompactionBlockKind` (Task 1); `AntRunner.ToolCalling.ToolLimitState` (`RuntimeOverrideMarker`, `BuildForceCompleteAssistantMessage`)
- Produces: `internal static class NoiseFilter { static List<CompactionBlock> Filter(IReadOnlyList<CompactionBlock> blocks) }`

- [ ] **Step 1: Write the failing test**

```csharp
using AntRunner.Chat.Compaction;
using AntRunner.ToolCalling;
using FluentAssertions;

namespace GuideAntsApi.Tests.ChatLayer.Compaction;

[TestClass]
public sealed class NoiseFilterTests
{
    [TestMethod]
    public void Filter_RemovesEmptyTextBlocks_ButKeepsToolBlocksWithEmptyText()
    {
        var blocks = new List<CompactionBlock>
        {
            new(CompactionBlockKind.UserMessage, "   "),
            new(CompactionBlockKind.AssistantMessage, ""),
            new(CompactionBlockKind.SystemMessage, ""),
            new(CompactionBlockKind.ToolCall, "", ToolCallId: "c1", ToolName: "ReadFile"),
            new(CompactionBlockKind.ToolResult, "", ToolCallId: "c1", ToolName: "ReadFile")
        };

        var filtered = NoiseFilter.Filter(blocks);

        filtered.Should().HaveCount(2);
        filtered.Should().OnlyContain(b => b.Kind is CompactionBlockKind.ToolCall or CompactionBlockKind.ToolResult);
    }

    [TestMethod]
    public void Filter_RemovesRuntimeOverrideSystemMessage()
    {
        var limitState = new ToolLimitState(5, 5, LimitEscalationPhase.SoftBlocked, ToolLimitHitKind.ToolCalls);
        var blocks = new List<CompactionBlock>
        {
            new(CompactionBlockKind.SystemMessage, limitState.BuildRuntimeOverrideSystemMessage())
        };

        NoiseFilter.Filter(blocks).Should().BeEmpty();
    }

    [TestMethod]
    public void Filter_RemovesToolLimitReachedSystemNudge()
    {
        var limitState = new ToolLimitState(5, 5, LimitEscalationPhase.None);
        var blocks = new List<CompactionBlock>
        {
            new(CompactionBlockKind.SystemMessage, limitState.BuildSystemNudgeMessage(ToolLimitHitKind.ToolCalls))
        };

        NoiseFilter.Filter(blocks).Should().BeEmpty();
    }

    [TestMethod]
    public void Filter_RemovesForceCompleteAssistantMessage()
    {
        var blocks = new List<CompactionBlock>
        {
            new(CompactionBlockKind.AssistantMessage,
                ToolLimitState.BuildForceCompleteAssistantMessage(ToolLimitHitKind.ToolCalls))
        };

        NoiseFilter.Filter(blocks).Should().BeEmpty();
    }

    [TestMethod]
    public void Filter_KeepsOrdinaryContent()
    {
        var blocks = new List<CompactionBlock>
        {
            new(CompactionBlockKind.UserMessage, "please read the file"),
            new(CompactionBlockKind.AssistantMessage, "sure, one moment")
        };

        NoiseFilter.Filter(blocks).Should().HaveCount(2);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test src/server/GuideAntsApi.Tests/GuideAntsApi.Tests.csproj --filter "FullyQualifiedName~NoiseFilterTests"`
Expected: FAIL to compile — `NoiseFilter` does not exist.

- [ ] **Step 3: Implement**

```csharp
using AntRunner.ToolCalling;

namespace AntRunner.Chat.Compaction;

internal static class NoiseFilter
{
    private static readonly string ForceCompleteMessage =
        ToolLimitState.BuildForceCompleteAssistantMessage(ToolLimitHitKind.ToolCalls);

    public static List<CompactionBlock> Filter(IReadOnlyList<CompactionBlock> blocks)
    {
        var filtered = new List<CompactionBlock>();
        foreach (var block in blocks)
        {
            if (IsEmptyContentBlock(block) || IsToolLimitScaffolding(block))
            {
                continue;
            }

            filtered.Add(block);
        }

        return filtered;
    }

    private static bool IsEmptyContentBlock(CompactionBlock block) =>
        block.Kind is CompactionBlockKind.UserMessage or CompactionBlockKind.AssistantMessage
            or CompactionBlockKind.SystemMessage
        && string.IsNullOrWhiteSpace(block.Text);

    // Mirrors the exact text ThreadRun.cs injects for tool-limit scaffolding
    // (EnsureRuntimeToolLimitOverrideMessage / EnsureLimitReachedSystemNudge / ForceCompleteOnToolLimit)
    // so this filter can't drift out of sync with that wording.
    private static bool IsToolLimitScaffolding(CompactionBlock block)
    {
        if (block.Kind == CompactionBlockKind.SystemMessage)
        {
            return block.Text.Contains(ToolLimitState.RuntimeOverrideMarker, StringComparison.Ordinal)
                   || block.Text.Contains("was reached for this turn", StringComparison.OrdinalIgnoreCase);
        }

        if (block.Kind == CompactionBlockKind.AssistantMessage)
        {
            return string.Equals(block.Text, ForceCompleteMessage, StringComparison.Ordinal);
        }

        return false;
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test src/server/GuideAntsApi.Tests/GuideAntsApi.Tests.csproj --filter "FullyQualifiedName~NoiseFilterTests"`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit (ask first)**

```bash
git add src/server/AntRunner.Chat/AntRunner.Chat/Compaction/NoiseFilter.cs src/server/GuideAntsApi.Tests/ChatLayer/Compaction/NoiseFilterTests.cs
git commit -m "Filters tool-limit scaffolding out of compaction input"
```

---

## Task 3: Goal extractor (3.5)

**Files:**
- Create: `src/server/AntRunner.Chat/AntRunner.Chat/Compaction/CompactionText.cs`
- Create: `src/server/AntRunner.Chat/AntRunner.Chat/Compaction/GoalExtractor.cs`
- Test: `src/server/GuideAntsApi.Tests/ChatLayer/Compaction/GoalExtractorTests.cs`

**Interfaces:**
- Consumes: `CompactionBlock`, `CompactionBlockKind` (Task 1)
- Produces:
  - `internal static class CompactionText { static string Truncate(string text, int maxChars) }` — shared by every extractor that needs a bounded-length line in the summary
  - `internal sealed record GoalSection(string? InitialGoal, IReadOnlyList<string> ScopeChanges)`
  - `internal static class GoalExtractor { static GoalSection Extract(IReadOnlyList<CompactionBlock> blocks) }`

- [ ] **Step 1: Write the failing test**

```csharp
using AntRunner.Chat.Compaction;
using FluentAssertions;

namespace GuideAntsApi.Tests.ChatLayer.Compaction;

[TestClass]
public sealed class GoalExtractorTests
{
    [TestMethod]
    public void Extract_NoUserMessages_ReturnsNullGoalAndNoScopeChanges()
    {
        var result = GoalExtractor.Extract([]);

        result.InitialGoal.Should().BeNull();
        result.ScopeChanges.Should().BeEmpty();
    }

    [TestMethod]
    public void Extract_FirstUserMessage_BecomesTheGoal()
    {
        var blocks = new List<CompactionBlock>
        {
            new(CompactionBlockKind.UserMessage, "Build a CSV export feature"),
            new(CompactionBlockKind.AssistantMessage, "Sure, starting now")
        };

        GoalExtractor.Extract(blocks).InitialGoal.Should().Be("Build a CSV export feature");
    }

    [TestMethod]
    public void Extract_LaterUserMessageWithScopeMarker_IsRecordedAsAScopeChange()
    {
        var blocks = new List<CompactionBlock>
        {
            new(CompactionBlockKind.UserMessage, "Build a CSV export feature"),
            new(CompactionBlockKind.UserMessage, "Actually, let's do JSON export instead")
        };

        var result = GoalExtractor.Extract(blocks);

        result.ScopeChanges.Should().ContainSingle()
            .Which.Should().Be("Actually, let's do JSON export instead");
    }

    [TestMethod]
    public void Extract_LaterUserMessageWithoutScopeMarker_IsNotRecorded()
    {
        var blocks = new List<CompactionBlock>
        {
            new(CompactionBlockKind.UserMessage, "Build a CSV export feature"),
            new(CompactionBlockKind.UserMessage, "Also add unit tests please")
        };

        GoalExtractor.Extract(blocks).ScopeChanges.Should().BeEmpty();
    }

    [TestMethod]
    public void Extract_IgnoresNonUserBlocks()
    {
        var blocks = new List<CompactionBlock>
        {
            new(CompactionBlockKind.SystemMessage, "system prompt"),
            new(CompactionBlockKind.UserMessage, "Build a CSV export feature")
        };

        GoalExtractor.Extract(blocks).InitialGoal.Should().Be("Build a CSV export feature");
    }

    [TestMethod]
    public void Extract_LongInitialGoal_IsTruncated()
    {
        var longText = new string('a', 600);
        var blocks = new List<CompactionBlock> { new(CompactionBlockKind.UserMessage, longText) };

        var goal = GoalExtractor.Extract(blocks).InitialGoal!;

        goal.Length.Should().Be(501); // 500 chars + ellipsis
        goal.Should().EndWith("…");
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test src/server/GuideAntsApi.Tests/GuideAntsApi.Tests.csproj --filter "FullyQualifiedName~GoalExtractorTests"`
Expected: FAIL to compile — `GoalExtractor` does not exist.

- [ ] **Step 3: Implement**

`CompactionText.cs`:

```csharp
namespace AntRunner.Chat.Compaction;

internal static class CompactionText
{
    public static string Truncate(string text, int maxChars)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxChars)
        {
            return text;
        }

        return text[..maxChars] + "…";
    }
}
```

`GoalExtractor.cs`:

```csharp
using System.Text.RegularExpressions;

namespace AntRunner.Chat.Compaction;

internal sealed record GoalSection(string? InitialGoal, IReadOnlyList<string> ScopeChanges);

internal static class GoalExtractor
{
    private const int GoalMaxChars = 500;
    private const int ScopeChangeMaxChars = 300;

    private static readonly Regex ScopeChangeMarkerRegex = new(
        @"\b(actually|instead|scratch that|never mind|change of plan|new plan|forget (that|it)|let'?s switch|hold on)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static GoalSection Extract(IReadOnlyList<CompactionBlock> blocks)
    {
        var userMessages = blocks.Where(b => b.Kind == CompactionBlockKind.UserMessage).ToList();
        if (userMessages.Count == 0)
        {
            return new GoalSection(null, []);
        }

        var initialGoal = CompactionText.Truncate(userMessages[0].Text, GoalMaxChars);

        var scopeChanges = userMessages
            .Skip(1)
            .Where(m => ScopeChangeMarkerRegex.IsMatch(m.Text))
            .Select(m => CompactionText.Truncate(m.Text, ScopeChangeMaxChars))
            .ToList();

        return new GoalSection(initialGoal, scopeChanges);
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test src/server/GuideAntsApi.Tests/GuideAntsApi.Tests.csproj --filter "FullyQualifiedName~GoalExtractorTests"`
Expected: PASS (6 tests).

- [ ] **Step 5: Commit (ask first)**

```bash
git add src/server/AntRunner.Chat/AntRunner.Chat/Compaction/CompactionText.cs src/server/AntRunner.Chat/AntRunner.Chat/Compaction/GoalExtractor.cs src/server/GuideAntsApi.Tests/ChatLayer/Compaction/GoalExtractorTests.cs
git commit -m "Extracts the compaction summary's Goal section"
```

---

## Task 4: `TurnFacts` and the Artifacts extractor (3.2, 3.6)

**Files:**
- Create: `src/server/AntRunner.Chat/AntRunner.Chat/Compaction/TurnFacts.cs`
- Create: `src/server/AntRunner.Chat/AntRunner.Chat/Compaction/ArtifactsExtractor.cs`
- Test: `src/server/GuideAntsApi.Tests/ChatLayer/Compaction/ArtifactsExtractorTests.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks (independent branch of the pipeline)
- Produces:
  - `public sealed record TurnFacts(int TurnIndex, IReadOnlyList<string> FilesCreated, IReadOnlyList<string> FilesModified)` — **public**; W5 constructs these from persisted `ConversationTurn` rows
  - `internal sealed record ArtifactsSection(IReadOnlyList<string> Created, IReadOnlyList<string> Modified)`
  - `internal static class ArtifactsExtractor { static ArtifactsSection Extract(IReadOnlyList<TurnFacts> turnFacts) }`

- [ ] **Step 1: Write the failing test**

```csharp
using AntRunner.Chat.Compaction;
using FluentAssertions;

namespace GuideAntsApi.Tests.ChatLayer.Compaction;

[TestClass]
public sealed class ArtifactsExtractorTests
{
    [TestMethod]
    public void Extract_NoTurns_ReturnsEmptySections()
    {
        var result = ArtifactsExtractor.Extract([]);

        result.Created.Should().BeEmpty();
        result.Modified.Should().BeEmpty();
    }

    [TestMethod]
    public void Extract_CollectsCreatedAndModifiedAcrossTurns()
    {
        var turns = new[]
        {
            new TurnFacts(1, ["out/report.csv"], []),
            new TurnFacts(2, [], ["out/report.csv", "notes.md"])
        };

        var result = ArtifactsExtractor.Extract(turns);

        // out/report.csv was created, then merely touched again later - it stays a "created" artifact.
        result.Created.Should().ContainSingle().Which.Should().Be("out/report.csv");
        result.Modified.Should().ContainSingle().Which.Should().Be("notes.md");
    }

    [TestMethod]
    public void Extract_DedupesWithinTheSameList()
    {
        var turns = new[] { new TurnFacts(1, ["a.txt", "a.txt"], []) };

        ArtifactsExtractor.Extract(turns).Created.Should().ContainSingle();
    }

    [TestMethod]
    public void Extract_OrdersByTurnIndexRegardlessOfInputOrder()
    {
        var turns = new[]
        {
            new TurnFacts(2, ["second.txt"], []),
            new TurnFacts(1, ["first.txt"], [])
        };

        ArtifactsExtractor.Extract(turns).Created.Should().Equal("first.txt", "second.txt");
    }

    [TestMethod]
    public void Extract_IgnoresBlankPaths()
    {
        var turns = new[] { new TurnFacts(1, ["", "  ", "real.txt"], []) };

        ArtifactsExtractor.Extract(turns).Created.Should().Equal("real.txt");
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test src/server/GuideAntsApi.Tests/GuideAntsApi.Tests.csproj --filter "FullyQualifiedName~ArtifactsExtractorTests"`
Expected: FAIL to compile — `TurnFacts`/`ArtifactsExtractor` do not exist.

- [ ] **Step 3: Implement**

`TurnFacts.cs`:

```csharp
namespace AntRunner.Chat.Compaction;

/// <summary>
/// Provider-neutral, already-materialized projection of one turn's file activity. The caller
/// (the history builder, W5) loads these from persisted <c>ConversationTurn.FilesCreated</c> /
/// <c>FilesModified</c> - the engine performs no JSON parsing or DB access of its own.
/// </summary>
public sealed record TurnFacts(
    int TurnIndex,
    IReadOnlyList<string> FilesCreated,
    IReadOnlyList<string> FilesModified);
```

`ArtifactsExtractor.cs`:

```csharp
namespace AntRunner.Chat.Compaction;

internal sealed record ArtifactsSection(IReadOnlyList<string> Created, IReadOnlyList<string> Modified);

internal static class ArtifactsExtractor
{
    public static ArtifactsSection Extract(IReadOnlyList<TurnFacts> turnFacts)
    {
        var orderedTurns = turnFacts.OrderBy(t => t.TurnIndex).ToList();

        var created = new List<string>();
        var createdSet = new HashSet<string>(StringComparer.Ordinal);
        foreach (var turn in orderedTurns)
        {
            foreach (var path in turn.FilesCreated)
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                if (createdSet.Add(path))
                {
                    created.Add(path);
                }
            }
        }

        var modified = new List<string>();
        var modifiedSet = new HashSet<string>(StringComparer.Ordinal);
        foreach (var turn in orderedTurns)
        {
            foreach (var path in turn.FilesModified)
            {
                if (string.IsNullOrWhiteSpace(path) || createdSet.Contains(path))
                {
                    continue;
                }

                if (modifiedSet.Add(path))
                {
                    modified.Add(path);
                }
            }
        }

        return new ArtifactsSection(created, modified);
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test src/server/GuideAntsApi.Tests/GuideAntsApi.Tests.csproj --filter "FullyQualifiedName~ArtifactsExtractorTests"`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit (ask first)**

```bash
git add src/server/AntRunner.Chat/AntRunner.Chat/Compaction/TurnFacts.cs src/server/AntRunner.Chat/AntRunner.Chat/Compaction/ArtifactsExtractor.cs src/server/GuideAntsApi.Tests/ChatLayer/Compaction/ArtifactsExtractorTests.cs
git commit -m "Adds TurnFacts and the compaction summary's Artifacts section"
```

---

## Task 5: Activity ledger extractor (3.7)

**Files:**
- Create: `src/server/AntRunner.Chat/AntRunner.Chat/Compaction/ActivityLedgerExtractor.cs`
- Test: `src/server/GuideAntsApi.Tests/ChatLayer/Compaction/ActivityLedgerExtractorTests.cs`

**Interfaces:**
- Consumes: `CompactionBlock`, `CompactionBlockKind` (Task 1); `CompactionText.Truncate` (Task 3)
- Produces:
  - `internal sealed record ActivityLedgerEntry(string ToolName, string? KeyArgument, bool? Succeeded)`
  - `internal static class ActivityLedgerExtractor { static List<ActivityLedgerEntry> Extract(IReadOnlyList<CompactionBlock> blocks) }`

- [ ] **Step 1: Write the failing test**

```csharp
using AntRunner.Chat.Compaction;
using FluentAssertions;

namespace GuideAntsApi.Tests.ChatLayer.Compaction;

[TestClass]
public sealed class ActivityLedgerExtractorTests
{
    private static CompactionBlock ToolCall(string id, string name, string? argsJson) =>
        new(CompactionBlockKind.ToolCall, string.Empty, ToolCallId: id, ToolName: name, ToolArguments: argsJson);

    private static CompactionBlock ToolResult(string id, string name, bool? succeeded) =>
        new(CompactionBlockKind.ToolResult, "result", ToolCallId: id, ToolName: name, ToolSucceeded: succeeded);

    [TestMethod]
    public void Extract_NoToolCalls_ReturnsEmptyLedger()
    {
        ActivityLedgerExtractor.Extract([]).Should().BeEmpty();
    }

    [TestMethod]
    public void Extract_PairsCallWithItsResult()
    {
        var blocks = new List<CompactionBlock>
        {
            ToolCall("c1", "ReadFile", "{\"path\":\"a.txt\"}"),
            ToolResult("c1", "ReadFile", true)
        };

        var ledger = ActivityLedgerExtractor.Extract(blocks);

        ledger.Should().ContainSingle();
        ledger[0].ToolName.Should().Be("ReadFile");
        ledger[0].KeyArgument.Should().Be("a.txt");
        ledger[0].Succeeded.Should().BeTrue();
    }

    [TestMethod]
    public void Extract_PicksThePriorityKeyWhenPresent()
    {
        var blocks = new List<CompactionBlock>
        {
            ToolCall("c1", "WebSearch", "{\"count\":5,\"query\":\"llama.cpp context window\"}")
        };

        ActivityLedgerExtractor.Extract(blocks)[0].KeyArgument.Should().Be("llama.cpp context window");
    }

    [TestMethod]
    public void Extract_FallsBackToFirstPropertyWhenNoPriorityKeyMatches()
    {
        var blocks = new List<CompactionBlock> { ToolCall("c1", "CustomTool", "{\"target\":\"thing\"}") };

        ActivityLedgerExtractor.Extract(blocks)[0].KeyArgument.Should().Be("thing");
    }

    [TestMethod]
    public void Extract_NoResultYet_SucceededIsNull()
    {
        var blocks = new List<CompactionBlock> { ToolCall("c1", "ReadFile", null) };

        ActivityLedgerExtractor.Extract(blocks)[0].Succeeded.Should().BeNull();
    }

    [TestMethod]
    public void Extract_MalformedArguments_FallsBackToTruncatedRawText()
    {
        var blocks = new List<CompactionBlock> { ToolCall("c1", "Weird", "not json") };

        ActivityLedgerExtractor.Extract(blocks)[0].KeyArgument.Should().Be("not json");
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test src/server/GuideAntsApi.Tests/GuideAntsApi.Tests.csproj --filter "FullyQualifiedName~ActivityLedgerExtractorTests"`
Expected: FAIL to compile — `ActivityLedgerExtractor` does not exist.

- [ ] **Step 3: Implement**

```csharp
namespace AntRunner.Chat.Compaction;

internal sealed record ActivityLedgerEntry(string ToolName, string? KeyArgument, bool? Succeeded);

internal static class ActivityLedgerExtractor
{
    private const int KeyArgumentMaxChars = 80;

    private static readonly string[] PriorityArgumentKeys =
        { "path", "filePath", "file", "query", "q", "command", "url" };

    public static List<ActivityLedgerEntry> Extract(IReadOnlyList<CompactionBlock> blocks)
    {
        var resultsByCallId = blocks
            .Where(b => b.Kind == CompactionBlockKind.ToolResult && !string.IsNullOrEmpty(b.ToolCallId))
            .GroupBy(b => b.ToolCallId!)
            .ToDictionary(g => g.Key, g => g.First());

        var entries = new List<ActivityLedgerEntry>();
        foreach (var block in blocks)
        {
            if (block.Kind != CompactionBlockKind.ToolCall)
            {
                continue;
            }

            var toolName = string.IsNullOrEmpty(block.ToolName) ? "(unknown tool)" : block.ToolName;
            var keyArgument = ExtractKeyArgument(block.ToolArguments);

            bool? succeeded = null;
            if (block.ToolCallId != null && resultsByCallId.TryGetValue(block.ToolCallId, out var result))
            {
                succeeded = result.ToolSucceeded;
            }

            entries.Add(new ActivityLedgerEntry(toolName, keyArgument, succeeded));
        }

        return entries;
    }

    private static string? ExtractKeyArgument(string? rawArgumentsJson)
    {
        if (string.IsNullOrWhiteSpace(rawArgumentsJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(rawArgumentsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return CompactionText.Truncate(rawArgumentsJson, KeyArgumentMaxChars);
            }

            foreach (var key in PriorityArgumentKeys)
            {
                if (doc.RootElement.TryGetProperty(key, out var value))
                {
                    return FormatArgumentValue(value);
                }
            }

            using var enumerator = doc.RootElement.EnumerateObject();
            return enumerator.MoveNext() ? FormatArgumentValue(enumerator.Current.Value) : null;
        }
        catch (JsonException)
        {
            return CompactionText.Truncate(rawArgumentsJson, KeyArgumentMaxChars);
        }
    }

    private static string? FormatArgumentValue(JsonElement value)
    {
        var text = value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
        return string.IsNullOrEmpty(text) ? null : CompactionText.Truncate(text, KeyArgumentMaxChars);
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test src/server/GuideAntsApi.Tests/GuideAntsApi.Tests.csproj --filter "FullyQualifiedName~ActivityLedgerExtractorTests"`
Expected: PASS (6 tests).

- [ ] **Step 5: Commit (ask first)**

```bash
git add src/server/AntRunner.Chat/AntRunner.Chat/Compaction/ActivityLedgerExtractor.cs src/server/GuideAntsApi.Tests/ChatLayer/Compaction/ActivityLedgerExtractorTests.cs
git commit -m "Extracts the compaction summary's Activity ledger section"
```

---

## Task 6: Unresolved errors extractor (3.8)

**Files:**
- Create: `src/server/AntRunner.Chat/AntRunner.Chat/Compaction/UnresolvedErrorsExtractor.cs`
- Test: `src/server/GuideAntsApi.Tests/ChatLayer/Compaction/UnresolvedErrorsExtractorTests.cs`

**Interfaces:**
- Consumes: `ActivityLedgerEntry` (Task 5) — reuses its `ToolName`/`Succeeded` pairing rather than re-deriving tool-call/tool-result matching from raw blocks
- Produces:
  - `internal sealed record UnresolvedError(string ToolName, string? KeyArgument)`
  - `internal static class UnresolvedErrorsExtractor { static List<UnresolvedError> Extract(IReadOnlyList<ActivityLedgerEntry> ledger) }`

- [ ] **Step 1: Write the failing test**

```csharp
using AntRunner.Chat.Compaction;
using FluentAssertions;

namespace GuideAntsApi.Tests.ChatLayer.Compaction;

[TestClass]
public sealed class UnresolvedErrorsExtractorTests
{
    [TestMethod]
    public void Extract_NoErrors_ReturnsEmpty()
    {
        var ledger = new List<ActivityLedgerEntry> { new("ReadFile", "a.txt", true) };

        UnresolvedErrorsExtractor.Extract(ledger).Should().BeEmpty();
    }

    [TestMethod]
    public void Extract_ErrorWithNoLaterSuccess_IsUnresolved()
    {
        var ledger = new List<ActivityLedgerEntry> { new("RunScript", "build.sh", false) };

        var result = UnresolvedErrorsExtractor.Extract(ledger);

        result.Should().ContainSingle();
        result[0].ToolName.Should().Be("RunScript");
    }

    [TestMethod]
    public void Extract_ErrorFollowedByLaterSuccessOnSameTool_IsResolved()
    {
        var ledger = new List<ActivityLedgerEntry>
        {
            new("RunScript", "build.sh", false),
            new("RunScript", "build.sh", true)
        };

        UnresolvedErrorsExtractor.Extract(ledger).Should().BeEmpty();
    }

    [TestMethod]
    public void Extract_LaterSuccessOnADifferentTool_DoesNotResolveTheError()
    {
        var ledger = new List<ActivityLedgerEntry>
        {
            new("RunScript", "build.sh", false),
            new("ReadFile", "log.txt", true)
        };

        UnresolvedErrorsExtractor.Extract(ledger).Should().ContainSingle();
    }

    [TestMethod]
    public void Extract_UnknownOutcome_IsNotTreatedAsAnError()
    {
        var ledger = new List<ActivityLedgerEntry> { new("SearchProject", "q", null) };

        UnresolvedErrorsExtractor.Extract(ledger).Should().BeEmpty();
    }

    [TestMethod]
    public void Extract_EarlierSuccessDoesNotResolveALaterError()
    {
        var ledger = new List<ActivityLedgerEntry>
        {
            new("RunScript", "build.sh", true),
            new("RunScript", "build.sh", false)
        };

        UnresolvedErrorsExtractor.Extract(ledger).Should().ContainSingle();
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test src/server/GuideAntsApi.Tests/GuideAntsApi.Tests.csproj --filter "FullyQualifiedName~UnresolvedErrorsExtractorTests"`
Expected: FAIL to compile — `UnresolvedErrorsExtractor` does not exist.

- [ ] **Step 3: Implement**

```csharp
namespace AntRunner.Chat.Compaction;

internal sealed record UnresolvedError(string ToolName, string? KeyArgument);

internal static class UnresolvedErrorsExtractor
{
    public static List<UnresolvedError> Extract(IReadOnlyList<ActivityLedgerEntry> ledger)
    {
        var unresolved = new List<UnresolvedError>();

        for (var i = 0; i < ledger.Count; i++)
        {
            var entry = ledger[i];
            if (entry.Succeeded != false)
            {
                continue; // only explicit failures count; unknown (null) and success (true) are not errors
            }

            var hasLaterSuccess = false;
            for (var j = i + 1; j < ledger.Count; j++)
            {
                if (string.Equals(ledger[j].ToolName, entry.ToolName, StringComparison.Ordinal)
                    && ledger[j].Succeeded == true)
                {
                    hasLaterSuccess = true;
                    break;
                }
            }

            if (!hasLaterSuccess)
            {
                unresolved.Add(new UnresolvedError(entry.ToolName, entry.KeyArgument));
            }
        }

        return unresolved;
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test src/server/GuideAntsApi.Tests/GuideAntsApi.Tests.csproj --filter "FullyQualifiedName~UnresolvedErrorsExtractorTests"`
Expected: PASS (6 tests).

- [ ] **Step 5: Commit (ask first)**

```bash
git add src/server/AntRunner.Chat/AntRunner.Chat/Compaction/UnresolvedErrorsExtractor.cs src/server/GuideAntsApi.Tests/ChatLayer/Compaction/UnresolvedErrorsExtractorTests.cs
git commit -m "Extracts the compaction summary's Unresolved errors section"
```

---

## Task 7: Directives extractor (3.9)

**Files:**
- Create: `src/server/AntRunner.Chat/AntRunner.Chat/Compaction/DirectivesExtractor.cs`
- Test: `src/server/GuideAntsApi.Tests/ChatLayer/Compaction/DirectivesExtractorTests.cs`

**Interfaces:**
- Consumes: `CompactionBlock`, `CompactionBlockKind` (Task 1); `CompactionText.Truncate` (Task 3)
- Produces:
  - `internal sealed record Directive(string Text)`
  - `internal static class DirectivesExtractor { static List<Directive> Extract(IReadOnlyList<CompactionBlock> blocks) }`

- [ ] **Step 1: Write the failing test**

```csharp
using AntRunner.Chat.Compaction;
using FluentAssertions;

namespace GuideAntsApi.Tests.ChatLayer.Compaction;

[TestClass]
public sealed class DirectivesExtractorTests
{
    [TestMethod]
    public void Extract_NoUserMessages_ReturnsEmpty()
    {
        DirectivesExtractor.Extract([]).Should().BeEmpty();
    }

    [TestMethod]
    public void Extract_MatchesAlwaysNeverPreferDont()
    {
        var blocks = new List<CompactionBlock>
        {
            new(CompactionBlockKind.UserMessage, "Always run tests before committing"),
            new(CompactionBlockKind.UserMessage, "Never commit directly to main"),
            new(CompactionBlockKind.UserMessage, "I prefer tabs over spaces"),
            new(CompactionBlockKind.UserMessage, "Don't use --force"),
            new(CompactionBlockKind.UserMessage, "What time is it?")
        };

        var directives = DirectivesExtractor.Extract(blocks);

        directives.Should().HaveCount(4);
        directives.Select(d => d.Text).Should().NotContain(t => t.Contains("What time"));
    }

    [TestMethod]
    public void Extract_IgnoresNonUserBlocks()
    {
        var blocks = new List<CompactionBlock>
        {
            new(CompactionBlockKind.AssistantMessage, "I will always confirm before deleting files")
        };

        DirectivesExtractor.Extract(blocks).Should().BeEmpty();
    }

    [TestMethod]
    public void Extract_IsCaseInsensitive()
    {
        var blocks = new List<CompactionBlock> { new(CompactionBlockKind.UserMessage, "ALWAYS ask first") };

        DirectivesExtractor.Extract(blocks).Should().ContainSingle();
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test src/server/GuideAntsApi.Tests/GuideAntsApi.Tests.csproj --filter "FullyQualifiedName~DirectivesExtractorTests"`
Expected: FAIL to compile — `DirectivesExtractor` does not exist.

- [ ] **Step 3: Implement**

```csharp
using System.Text.RegularExpressions;

namespace AntRunner.Chat.Compaction;

internal sealed record Directive(string Text);

internal static class DirectivesExtractor
{
    private const int DirectiveMaxChars = 200;

    private static readonly Regex DirectiveRegex = new(
        @"\b(always|never|prefer|don'?t)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static List<Directive> Extract(IReadOnlyList<CompactionBlock> blocks)
    {
        var directives = new List<Directive>();
        foreach (var block in blocks)
        {
            if (block.Kind != CompactionBlockKind.UserMessage || string.IsNullOrWhiteSpace(block.Text))
            {
                continue;
            }

            if (DirectiveRegex.IsMatch(block.Text))
            {
                directives.Add(new Directive(CompactionText.Truncate(block.Text, DirectiveMaxChars)));
            }
        }

        return directives;
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test src/server/GuideAntsApi.Tests/GuideAntsApi.Tests.csproj --filter "FullyQualifiedName~DirectivesExtractorTests"`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit (ask first)**

```bash
git add src/server/AntRunner.Chat/AntRunner.Chat/Compaction/DirectivesExtractor.cs src/server/GuideAntsApi.Tests/ChatLayer/Compaction/DirectivesExtractorTests.cs
git commit -m "Extracts the compaction summary's Directives section"
```

---

## Task 8: Renderer (3.10)

**Files:**
- Create: `src/server/AntRunner.Chat/AntRunner.Chat/Compaction/CompactionSummaryRenderer.cs`
- Test: `src/server/GuideAntsApi.Tests/ChatLayer/Compaction/CompactionSummaryRendererTests.cs`

**Interfaces:**
- Consumes: `GoalSection` (Task 3), `ArtifactsSection` (Task 4), `ActivityLedgerEntry` (Task 5), `UnresolvedError` (Task 6), `Directive` (Task 7)
- Produces: `internal static class CompactionSummaryRenderer { static string Render(GoalSection goal, ArtifactsSection artifacts, IReadOnlyList<ActivityLedgerEntry> activityLedger, IReadOnlyList<UnresolvedError> unresolvedErrors, IReadOnlyList<Directive> directives, int summarizedMessageCount) }`

- [ ] **Step 1: Write the failing test**

```csharp
using AntRunner.Chat.Compaction;
using FluentAssertions;

namespace GuideAntsApi.Tests.ChatLayer.Compaction;

[TestClass]
public sealed class CompactionSummaryRendererTests
{
    private static readonly GoalSection EmptyGoal = new(null, []);
    private static readonly ArtifactsSection EmptyArtifacts = new([], []);

    [TestMethod]
    public void Render_StartsWithTheHandoffFramingLine()
    {
        var text = CompactionSummaryRenderer.Render(EmptyGoal, EmptyArtifacts, [], [], [], summarizedMessageCount: 0);

        text.Should().StartWith("The following is a condensed handoff briefing");
    }

    [TestMethod]
    public void Render_IncludesAllSixSectionHeadings()
    {
        var text = CompactionSummaryRenderer.Render(EmptyGoal, EmptyArtifacts, [], [], [], summarizedMessageCount: 0);

        text.Should().Contain("## Goal");
        text.Should().Contain("## Artifacts");
        text.Should().Contain("## Activity ledger");
        text.Should().Contain("## Unresolved errors");
        text.Should().Contain("## Directives");
    }

    [TestMethod]
    public void Render_DegenerateInput_ShowsPlaceholdersInsteadOfBlankSections()
    {
        var text = CompactionSummaryRenderer.Render(EmptyGoal, EmptyArtifacts, [], [], [], summarizedMessageCount: 0);

        text.Should().Contain("(none recorded)");
        text.Should().Contain("(none)");
        text.Should().Contain("(no tool calls)");
    }

    [TestMethod]
    public void Render_IncludesGoalArtifactsLedgerAndDirectiveContent()
    {
        var goal = new GoalSection("Build a CSV export feature", ["Actually, do JSON instead"]);
        var artifacts = new ArtifactsSection(["out/report.csv"], ["notes.md"]);
        var ledger = new List<ActivityLedgerEntry> { new("ReadFile", "a.txt", true) };
        var errors = new List<UnresolvedError> { new("RunScript", "build.sh") };
        var directives = new List<Directive> { new("Always run tests first") };

        var text = CompactionSummaryRenderer.Render(goal, artifacts, ledger, errors, directives, summarizedMessageCount: 12);

        text.Should().Contain("Build a CSV export feature");
        text.Should().Contain("Actually, do JSON instead");
        text.Should().Contain("out/report.csv");
        text.Should().Contain("notes.md");
        text.Should().Contain("ReadFile");
        text.Should().Contain("RunScript");
        text.Should().Contain("Always run tests first");
        text.Should().Contain("12 earlier message");
    }

    [TestMethod]
    public void Render_SameInputTwice_IsByteIdentical()
    {
        var goal = new GoalSection("Goal text", []);

        var first = CompactionSummaryRenderer.Render(goal, EmptyArtifacts, [], [], [], 3);
        var second = CompactionSummaryRenderer.Render(goal, EmptyArtifacts, [], [], [], 3);

        first.Should().Be(second);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test src/server/GuideAntsApi.Tests/GuideAntsApi.Tests.csproj --filter "FullyQualifiedName~CompactionSummaryRendererTests"`
Expected: FAIL to compile — `CompactionSummaryRenderer` does not exist.

- [ ] **Step 3: Implement**

```csharp
namespace AntRunner.Chat.Compaction;

/// <summary>
/// Renders the five extracted sections into one system-message body. The caller (W5) is
/// responsible for wrapping this text in a <c>[system: ...]</c> message and appending the
/// verbatim tail after it - the Tail section in the spec's vocabulary is not rendered here.
/// </summary>
internal static class CompactionSummaryRenderer
{
    private const string HandoffFramingLine =
        "The following is a condensed handoff briefing summarizing an earlier part of this " +
        "conversation that has been compacted. It is reference material, not something to " +
        "continue or respond to directly.";

    public static string Render(
        GoalSection goal,
        ArtifactsSection artifacts,
        IReadOnlyList<ActivityLedgerEntry> activityLedger,
        IReadOnlyList<UnresolvedError> unresolvedErrors,
        IReadOnlyList<Directive> directives,
        int summarizedMessageCount)
    {
        var lines = new List<string>
        {
            HandoffFramingLine,
            string.Empty,
            $"[Compacted {summarizedMessageCount} earlier message(s).]",
            string.Empty,
            "## Goal",
            string.IsNullOrWhiteSpace(goal.InitialGoal) ? "(none recorded)" : goal.InitialGoal
        };

        foreach (var change in goal.ScopeChanges)
        {
            lines.Add($"- Scope change: {change}");
        }

        lines.Add(string.Empty);
        lines.Add("## Artifacts");
        if (artifacts.Created.Count == 0 && artifacts.Modified.Count == 0)
        {
            lines.Add("(none)");
        }
        else
        {
            lines.AddRange(artifacts.Created.Select(path => $"- created: {path}"));
            lines.AddRange(artifacts.Modified.Select(path => $"- modified: {path}"));
        }

        lines.Add(string.Empty);
        lines.Add("## Activity ledger");
        if (activityLedger.Count == 0)
        {
            lines.Add("(no tool calls)");
        }
        else
        {
            lines.AddRange(activityLedger.Select(FormatLedgerEntry));
        }

        lines.Add(string.Empty);
        lines.Add("## Unresolved errors");
        if (unresolvedErrors.Count == 0)
        {
            lines.Add("(none)");
        }
        else
        {
            lines.AddRange(unresolvedErrors.Select(FormatUnresolvedError));
        }

        lines.Add(string.Empty);
        lines.Add("## Directives");
        lines.AddRange(directives.Count == 0
            ? ["(none)"]
            : directives.Select(d => $"- {d.Text}"));

        return string.Join("\n", lines);
    }

    private static string FormatLedgerEntry(ActivityLedgerEntry entry)
    {
        var status = entry.Succeeded switch { true => "ok", false => "error", null => "unknown" };
        var arg = string.IsNullOrEmpty(entry.KeyArgument) ? string.Empty : $" {entry.KeyArgument}";
        return $"- {entry.ToolName}{arg} — {status}";
    }

    private static string FormatUnresolvedError(UnresolvedError error)
    {
        var arg = string.IsNullOrEmpty(error.KeyArgument) ? string.Empty : $" {error.KeyArgument}";
        return $"- {error.ToolName}{arg}";
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test src/server/GuideAntsApi.Tests/GuideAntsApi.Tests.csproj --filter "FullyQualifiedName~CompactionSummaryRendererTests"`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit (ask first)**

```bash
git add src/server/AntRunner.Chat/AntRunner.Chat/Compaction/CompactionSummaryRenderer.cs src/server/GuideAntsApi.Tests/ChatLayer/Compaction/CompactionSummaryRendererTests.cs
git commit -m "Renders the compaction summary with a handoff framing line"
```

---

## Task 9: `CompactionEngine` orchestration (3.1)

**Files:**
- Create: `src/server/AntRunner.Chat/AntRunner.Chat/Compaction/CompactionEngine.cs`
- Test: `src/server/GuideAntsApi.Tests/ChatLayer/Compaction/CompactionEngineTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 1–8 (`MessageNormalizer`, `NoiseFilter`, `GoalExtractor`, `ArtifactsExtractor`, `ActivityLedgerExtractor`, `UnresolvedErrorsExtractor`, `DirectivesExtractor`, `CompactionSummaryRenderer`)
- Produces (the workstream's entire public surface):
  - `public sealed record CompactionResult(string SummaryText, int SummarizedMessageCount, int SummarizedTurnCount)`
  - `public static class CompactionEngine { static CompactionResult Compact(IReadOnlyList<ChatMessage> preBoundaryMessages, IReadOnlyList<TurnFacts> turnFacts) }`

- [ ] **Step 1: Write the failing test**

```csharp
using AntRunner.Chat.Abstractions;
using AntRunner.Chat.Compaction;
using AntRunner.ToolCalling;
using FluentAssertions;

namespace GuideAntsApi.Tests.ChatLayer.Compaction;

[TestClass]
public sealed class CompactionEngineTests
{
    [TestMethod]
    public void Compact_EmptyInputs_ReturnsAWellFormedSummaryWithNoExceptions()
    {
        var result = CompactionEngine.Compact([], []);

        result.SummaryText.Should().Contain("(none recorded)");
        result.SummarizedMessageCount.Should().Be(0);
        result.SummarizedTurnCount.Should().Be(0);
    }

    [TestMethod]
    public void Compact_TypicalConversation_ProducesAllSections()
    {
        var call = new ChatToolCall
        {
            Id = "c1",
            Function = new ChatToolCallFunction
            {
                Name = "RunScript",
                Arguments = JsonDocument.Parse("{\"command\":\"pytest\"}").RootElement.Clone()
            }
        };

        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "Build a CSV export feature"),
            new(ChatRole.Assistant, [], [call]),
            new("c1", "RunScript", [new ChatContent("{\"standardOutput\":\"\",\"standardError\":\"failed\",\"exitCode\":1}")]),
            new(ChatRole.User, "Always write a test for every fix"),
            new(ChatRole.Assistant, "Understood, I'll add tests going forward.")
        };

        var turnFacts = new List<TurnFacts> { new(1, ["out/report.csv"], []) };

        var result = CompactionEngine.Compact(messages, turnFacts);

        result.SummarizedMessageCount.Should().Be(5);
        result.SummarizedTurnCount.Should().Be(1);
        result.SummaryText.Should().Contain("Build a CSV export feature");
        result.SummaryText.Should().Contain("out/report.csv");
        result.SummaryText.Should().Contain("RunScript");
        result.SummaryText.Should().Contain("Always write a test for every fix");
    }

    [TestMethod]
    public void Compact_FiltersToolLimitScaffoldingBeforeExtraction()
    {
        var limitState = new ToolLimitState(2, 2, LimitEscalationPhase.SoftBlocked, ToolLimitHitKind.ToolCalls);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "Do the thing"),
            new(ChatRole.System, limitState.BuildRuntimeOverrideSystemMessage())
        };

        var result = CompactionEngine.Compact(messages, []);

        result.SummaryText.Should().NotContain(ToolLimitState.RuntimeOverrideMarker);
    }

    [TestMethod]
    public void Compact_SameInputTwice_IsDeterministic()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "Build a CSV export feature"),
            new(ChatRole.Assistant, "On it.")
        };
        var turnFacts = new List<TurnFacts> { new(1, ["out.csv"], []) };

        var first = CompactionEngine.Compact(messages, turnFacts);
        var second = CompactionEngine.Compact(messages, turnFacts);

        first.SummaryText.Should().Be(second.SummaryText);
        first.SummarizedMessageCount.Should().Be(second.SummarizedMessageCount);
    }

    [TestMethod]
    public void Compact_NullInputs_DoesNotThrow()
    {
        var act = () => CompactionEngine.Compact(null!, null!);

        act.Should().NotThrow();
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test src/server/GuideAntsApi.Tests/GuideAntsApi.Tests.csproj --filter "FullyQualifiedName~CompactionEngineTests"`
Expected: FAIL to compile — `CompactionEngine`/`CompactionResult` do not exist.

- [ ] **Step 3: Implement**

```csharp
using AntRunner.Chat.Abstractions;

namespace AntRunner.Chat.Compaction;

/// <summary>
/// Deterministic outcome of compacting the messages before a user-chosen boundary. The caller
/// (W5) wraps <see cref="SummaryText"/> in a <c>[system: ...]</c> message and appends the
/// verbatim post-boundary tail after it.
/// </summary>
public sealed record CompactionResult(string SummaryText, int SummarizedMessageCount, int SummarizedTurnCount);

/// <summary>
/// Pure, deterministic compaction of everything before a user-chosen boundary into a fixed,
/// protocol-derived section vocabulary (Goal, Artifacts, Activity ledger, Unresolved errors,
/// Directives). No I/O, no DB, no vendor knowledge, no LLM call - see
/// docs/superpowers/specs/2026-09-21-context-compaction-design.md, Decisions D2/D3/D6.
/// </summary>
public static class CompactionEngine
{
    public static CompactionResult Compact(
        IReadOnlyList<ChatMessage> preBoundaryMessages,
        IReadOnlyList<TurnFacts> turnFacts)
    {
        preBoundaryMessages ??= [];
        turnFacts ??= [];

        var blocks = MessageNormalizer.Normalize(preBoundaryMessages);
        var filtered = NoiseFilter.Filter(blocks);

        var goal = GoalExtractor.Extract(filtered);
        var artifacts = ArtifactsExtractor.Extract(turnFacts);
        var activityLedger = ActivityLedgerExtractor.Extract(filtered);
        var unresolvedErrors = UnresolvedErrorsExtractor.Extract(activityLedger);
        var directives = DirectivesExtractor.Extract(filtered);

        var summaryText = CompactionSummaryRenderer.Render(
            goal, artifacts, activityLedger, unresolvedErrors, directives, preBoundaryMessages.Count);

        var summarizedTurnCount = turnFacts.Select(t => t.TurnIndex).Distinct().Count();

        return new CompactionResult(summaryText, preBoundaryMessages.Count, summarizedTurnCount);
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test src/server/GuideAntsApi.Tests/GuideAntsApi.Tests.csproj --filter "FullyQualifiedName~CompactionEngineTests"`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit (ask first)**

```bash
git add src/server/AntRunner.Chat/AntRunner.Chat/Compaction/CompactionEngine.cs src/server/GuideAntsApi.Tests/ChatLayer/Compaction/CompactionEngineTests.cs
git commit -m "Adds the CompactionEngine entry point tying the pipeline together"
```

---

## Task 10: Full verification (3.11)

- [ ] **Step 1: Build and run the whole Compaction test folder**

Run: `dotnet build src/server/GuideAntsApi.sln && dotnet test src/server/GuideAntsApi.Tests/GuideAntsApi.Tests.csproj --filter "FullyQualifiedName~ChatLayer.Compaction"`
Expected: build clean; all ~49 tests across the nine test files pass.

- [ ] **Step 2: Confirm no accidental dependency leak**

Run: `grep -rn "GuideAntsApi\b" src/server/AntRunner.Chat/AntRunner.Chat/Compaction/*.cs`
Expected: no matches. If any file references `GuideAntsApi` or `GuideAntsApi.DataModel`, that's a Global Constraints violation — fix it before moving on.

- [ ] **Step 3: Run the full server unit suite for regressions**

Run: `dotnet test src/server/GuideAntsApi.sln --filter "TestCategory!=Integration"`
Expected: no new failures. W3 adds files only — it should not be possible for this to regress anything outside the new `Compaction` folder.

- [ ] **Step 4: Tick W3 in the checklist**

In `docs/context-compaction-plan.md`, tick 3.1–3.11 and add a line under W3 linking this plan (matching the "Implementation plan written" pattern already used for W1 and W2), plus a note that: (a) only `CompactionEngine`, `CompactionResult`, and `TurnFacts` are public — every pipeline stage is `internal`, reached in tests via the existing `InternalsVisibleTo`; (b) the full cross-domain benchmark against real non-coding transcripts (spec's Testing item 5) is W9's job, not this workstream's — W3's tests only prove the pipeline is correct and deterministic on synthetic input.

---

## Self-review

**Spec coverage.**
- Checklist 3.1 (`CompactionEngine.Compact(preBoundaryMessages, turnFacts) -> CompactionResult`) → Task 9.
- 3.2 (`TurnFacts` projection, sourced from persisted columns not `ThreadRun`'s in-memory sets) → Task 4; `TurnFacts` carries no DB/JSON-parsing logic itself, matching the Global Constraints note that the caller (W5) does that.
- 3.3 (normalize `ChatMessage[]` → uniform blocks) → Task 1.
- 3.4 (filter noise: empty blocks, superseded system nudges, tool-limit scaffolding) → Task 2; reuses `ToolLimitState`'s own text-building methods so the filter can't drift from `ThreadRun.cs`'s actual wording.
- 3.5 (Goal: first user message + scope-change markers) → Task 3.
- 3.6 (Artifacts from `TurnFacts`) → Task 4.
- 3.7 (Activity ledger: name, key args, ok/error) → Task 5.
- 3.8 (Unresolved errors: errored results with no later success for the same tool) → Task 6, built directly on Task 5's ledger rather than re-deriving pairing logic.
- 3.9 (Directives: regex over user messages) → Task 7.
- 3.10 (Renderer + handoff framing line) → Task 8.
- 3.11 (determinism, each extractor, degenerate inputs) → every task's test file covers its own extractor; Task 9's `CompactionEngineTests` covers end-to-end determinism and degenerate (empty/null) input; Task 10 runs the full folder together.
- Spec's "No cut-selection stage" → Global Constraints states it explicitly; no task implements token-budget logic.
- Spec's "must not reference GuideAntsApi or GuideAntsApi.DataModel" → Global Constraints + Task 10 Step 2's grep check.

**Known limitations, deliberately accepted (call these out in the PR, not fixed here).**
- Scope-change and directive detection are both fixed regexes over English words/phrases (`actually`, `instead`, `always`, `never`, `prefer`, `don't`, etc.) — D2 accepts this as a domain-fit risk with no LLM fallback. The spec's cross-domain benchmark (Testing item 5) is where this gets validated against real transcripts; that's W9, not W3.
- `ActivityLedgerExtractor`'s "key argument" heuristic (a fixed priority-key list, else first JSON property, else truncated raw text) is a deterministic best-effort summarization, not a semantic one. Good enough for a ledger line; not meant to reproduce full tool-call context (that's what W7's recall tool is for).
- `ToolSucceeded` is `null` (unknown, not "ok") for any tool result that isn't `ScriptExecutionResult`-shaped with an `ExitCode`. This means non-sandbox tools (search, web search, recall) never appear as errors in the ledger even if they logically failed — accepted per D2, since GuideAnts' protocol only guarantees an error shape for script execution.

**Placeholder scan.** Every code step has complete code — no "TBD"/"add error handling"/"similar to Task N" placeholders. The one deliberately open-ended piece is Task 10 Step 4's checklist note, which is documentation, not implementation.

**Type consistency.** `CompactionBlock`/`CompactionBlockKind` (Task 1) are consumed unchanged by Tasks 2, 3, 5, 7. `CompactionText.Truncate` (Task 3) is consumed by Tasks 5 and 7 with matching signature `(string, int) -> string`. `ActivityLedgerEntry` (Task 5) is consumed unchanged by Task 6 (`UnresolvedErrorsExtractor.Extract(IReadOnlyList<ActivityLedgerEntry>)`) and Task 8 (`Render(..., IReadOnlyList<ActivityLedgerEntry>, ...)`). `GoalSection`/`ArtifactsSection`/`UnresolvedError`/`Directive` all match between the extractor that produces them and the renderer signature in Task 8, which in turn matches the call in Task 9's `CompactionEngine.Compact`. `TurnFacts` (Task 4, public) matches the parameter type in Task 9's public `Compact` signature — the only two public types besides `CompactionEngine`/`CompactionResult` itself.
