# Task — Phase 2: Tier 1 runtime enforcement

> Subagent brief. Execute top to bottom. Return the Report-back contract verbatim.

## Mission

Enforce `max_tool_calls_per_turn` in `ThreadRun` with Tier 1 soft block: when the budget is
exhausted and the model returns `tool_calls`, inject synthetic limit `tool` messages (one per
`tool_call_id`), emit SSE, and continue the loop — **without** executing tools and **without**
throwing. Wire `ToolLimitState` through nested `Agent.Invoke` and evaluator reopen.

## Read first

- `../tool-call-limits-proposal.md` §5 (counting), §6 (enforcement), §8 Tier 1, §11
  (pseudocode), §9 (anti-patterns).
- `./DECISIONS.md` — T1, T3, T5, T8, T13, T14.
- `./provider-safe-completion-gate.md`, `./runtime-parity-gate.md`.
- `src/server/AntRunner.Chat/AntRunner.Chat/ThreadRun.cs` — `case "tool_calls"` ~L429,
  `DoToolCalls` ~L1021, existing `ERROR:` tool failure strings (mirror structure).
- `src/server/AntRunner.Chat/AntRunner.ToolCalling/InvocationContext.cs`
- `src/server/GuideAntsApi/Services/Conversations/Agent.cs` — nested `ThreadRun` creation.

## Preconditions

- Phase 1 gate green (`AssistantDefinition` has limits).
- `DECISIONS.md` locked.

## Guardrails (hard)

- **Enforcement only in `case "tool_calls"` before `DoToolCalls`.** No checks in
  `ConversationStreamEngine` except passing state through.
- **Never throw** on limit (T3). Turn must reach `completed`.
- **Every blocked `tool_call_id` gets a synthetic `tool` message** (T5). Same pairing rules as
  existing error tool results.
- **Keep full `tools` array** on the next completion request (T4/T5).
- **Do not execute** any tool in the batch when limit blocks the batch (or block per-call within
  batch per proposal: each requested call gets synthetic result).
- **Client-handled partition:** when limit applies to client-handled tools, count toward budget
  when emitting `external_tool_call` (T8).
- **Evaluator reopen inherits `ToolLimitState`** (T14) — find evaluator loop entry and pass
  state through.
- No Tier 2/3 logic in this phase (no `ToolChoice` yet).

## Tasks

1. **Add `ToolLimitState` + `LimitEscalationPhase`** per proposal §5 (record on
   `InvocationContext` or dedicated run-scoped object referenced from context).
2. **Initialize state** at `ThreadRun` start from `AssistantDefinition.MaxToolCallsPerTurn`
   (`null` → no limit).
3. **Limit check** in `case "tool_calls"` after server/client partition, before `DoToolCalls`:
   - If `WouldExceedLimit(serverHandled, state)` → `InjectLimitToolResults`, set
     `Phase = SoftBlocked`, skip `DoToolCalls` for blocked calls.
   - Else → `DoToolCalls`, increment counters by executed count.
4. **Limit message text** per proposal §8 Tier 1 (include configured max and used count).
5. **System nudge** after soft block (proposal §8 Tier 1 step 5).
6. **`Agent.Invoke`:** child `ThreadRun` gets fresh child budget from child assistant limit;
   parent decrements by 1 for the invoke itself (T1).
7. **Tests:**
   - 12th tool runs; 13th batch → synthetic results, no execution.
   - Parallel batch counts correctly.
   - Nested invoke child budget independent.
   - Evaluator reopen does not reset counters.
   - Null limit → no behavior change vs baseline simple turn.

## Files in scope

- `src/server/AntRunner.Chat/AntRunner.ToolCalling/InvocationContext.cs` (or new
  `ToolLimitState.cs` in ToolCalling)
- `src/server/AntRunner.Chat/AntRunner.Chat/ThreadRun.cs`
- `src/server/GuideAntsApi/Services/Conversations/Agent.cs`
- `src/server/GuideAntsApi.IntegrationTests/**` or `AntRunner.Chat.Tests/**`
- Evaluator reopen path files (grep `ToolLimitState` / evaluator + `ThreadRun`)

## Files out of scope

- Chat client `ToolChoice` mapping (Phase 3).
- Builder UI (Phase 4).
- Export/import (Phase 5).

## Self-verification

```bash
cd src/server && dotnet build GuideAntsApi.sln && dotnet test GuideAntsApi.sln
```

Run provider-safe-completion gate §1–2 manually against new tests.

## Definition of Done

- [ ] Tier 1 soft block works on private + published paths (integration test).
- [ ] Synthetic tool messages persisted; SSE `tool_result` emitted.
- [ ] Turn does not throw or enter `error` on limit.
- [ ] Runtime-parity gate §1–2 pass.

## Report-back contract

1. Where `ToolLimitState` lives and how it flows through `Agent.Invoke` + evaluator.
2. Exact limit-check insertion point (line ref in `ThreadRun`).
3. Test names + scenarios covered.
4. Confirmation Tier 2/3 not implemented.
5. Files touched.
6. Gate self-check results.
