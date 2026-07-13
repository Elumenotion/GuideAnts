# Per-Assistant Tool Call Limits — Locked Decisions (single source of truth)

Last updated: 2026-07-12  
Status: LOCKED (recommended defaults; confirm T9 before Phase 5 bootstrap)

This file freezes the design decisions from
[`../tool-call-limits-proposal.md`](../tool-call-limits-proposal.md) §15 and resolves
implementation choices before dispatch starts.

Rules:

- If a decision below is `UNDECIDED`, any phase listed under "Blocks" is blocked.
- Changing a locked decision after a phase ships requires reverting and re-dispatching the
  impacted phases (see `00-orchestration.md` §5).
- Subagents must not reinterpret values in this file. The proposal is context; this file is
  the contract.

---

## Part A — Locked decisions (T1–T15)

| ID | Decision | Resolved value | Blocks |
|----|----------|----------------|--------|
| T1 | Limit scope | Per `ThreadRun` with nested budget inheritance for `Agent.Invoke`. Parent `Agent.Invoke` = 1 parent tool call; child run gets its own `ToolLimitState` scoped per proposal §5. | 2,3,6 |
| T2 | Null semantics | `null` on all limit fields = **unlimited** (backward compatible). Existing guides unchanged. | 1,4 |
| T3 | Terminal behavior | **Never throw** on limit exhaustion. Turn ends `completed` (not `error` / `cancelled`). | 2,3 |
| T4 | Strip `tools` from request | **No.** Do not omit `tools` while history still contains `tool_calls` / `role: tool` messages. | 3,6 |
| T5 | Primary safe completion | Tier 1: synthetic `tool` result per `tool_call_id`, **full `tools` array retained** on next request. Mirrors existing `DoToolCalls` `ERROR:` strings. | 2 |
| T6 | `tool_choice: "none"` | Tier 2 when provider supports it (`SupportsToolChoiceNone`). Skip to Tier 3 when unsupported — no fallback retry of Tier 1. | 3 |
| T7 | Force complete | Tier 3: server-authored assistant message + `continueChat = false`. No further tool-capable LLM call. | 3 |
| T8 | Client-handled tools | Count toward the same budget when `external_tool_call` / `pending_client_tool` is emitted (proposal §5). | 2 |
| T9 | Bootstrap Search default | `max_tool_calls_per_turn: 12` for Creative Guide Search assistant. **Recommended; confirm before Phase 5.** | 5 |
| T10 | `max_tool_rounds_per_turn` | Nullable field (`null` = unlimited). Phase 6: enforce + UI. Counts LLM rounds with `finish_reason: tool_calls`. | 6 |
| T11 | `max_tool_calls_per_invocation` | Nullable field on `GuideMember` (`null` = use child assistant limit). Phase 6: enforce + Crew tab UI. Child budget = `min(remaining parent, member override ?? child assistant limit)`. | 6 |
| T12 | Tier 4 compaction | Phase 6: `BuildCompactedHistoryForLimitSummary` on force-complete path; tool-free single summarization call per proposal §8 Tier 4. Tier 3 stub fallback if summarization fails. | 6 |
| T13 | Stream reconnect coordination | Limit exhaustion uses the normal `completed` turn path so `ConversationLock` releases. Reconnect proposal applies while under budget; no special client work required for limit hits. See [`../conversation-stream-reconnect-and-cancel-proposal.md`](../conversation-stream-reconnect-and-cancel-proposal.md) §14. | 2,7 |
| T14 | Evaluator reopen | Evaluator loop must **inherit** `ToolLimitState`; budget is not reset on reopen. | 2 |
| T15 | Serialization naming | JSON manifests / export / client DTOs: `max_tool_calls_per_turn`, `max_tool_rounds_per_turn`, `max_tool_calls_per_invocation`. C# entities: `MaxToolCallsPerTurn`, etc. | 1,4,5 |

---

## Part B — Frozen invariants (not open for reinterpretation)

From proposal §7–§9 and standing project rules:

- **One enforcement choke point:** `ThreadRun.ExecuteAsync`, `case "tool_calls"` — before
  `DoToolCalls`. `ConversationStreamEngine` and wire handlers delegate here; do not add
  parallel limit checks downstream.
- **Provider-safe pairing:** Every blocked `tool_call_id` gets a persisted `tool` message.
  Never silently drop tool calls. Never orphan `tool_calls` without results.
- **Full proposal scope.** Every phase (1–7) and every escalation tier (1–5) in the proposal is
  in scope. Nullable limit fields (`null` = unlimited) are operator configuration, not
  permission to skip implementation. No orchestration phase or deliverable may be marked
  SKIPPED.
- **No fallback masking** (user rule). No silent `catch {}`, no hidden retry after limit,
  no "assume unlimited on parse failure". Invalid limit values are explicit validation errors
  at save/import time; runtime treats missing limits as unlimited (T2).
- **Distinct from published `MaxTurns`.** Tool limits are per-assistant, per-turn, inside
  `ThreadRun`. Published `LimitsTab` / `PublishedGuide.MaxTurns` are conversation-level and
  must not be conflated in UI copy or DTO naming.
- **All server-handled tool kinds count equally:** `LocalFunction`, `WebApi`, `SandboxHandled`,
  `McpApi`, and `Agent.Invoke` parent attribution per proposal §5.
- **`ReadWeb` / `GetContentFromUrl`:** 1 count per call in the parent `ThreadRun` (not a nested
  run).
- **Runtime instruction override (Tier 5):** When `Phase >= SoftBlocked`, prepend system context
  telling the model to ignore prior retry instructions for this turn. Phase 3 owns this.
- **One materialization choke point:** Limits flow from DB → DTO → `AssistantDefinition` (or
  `ChatRunOptions`) via `DatabaseStorage.MaterializeAssistant`; runtime reads materialized
  values, not ad-hoc DB queries mid-turn.
- **Wire opacity preserved:** Published wire does not surface limit escalation internals as
  provider tool events; limits behave like other server-side tool execution (same as MCP/skills
  wire posture).

---

## Part C — Cross-plan alignment

| Related plan | Relationship |
|--------------|--------------|
| [`conversation-stream-reconnect-and-cancel-proposal.md`](../conversation-stream-reconnect-and-cancel-proposal.md) | Complementary. Limits bound worst-case turn duration; reconnect/cancel handles client disconnect. Limit-completed turns must rehydrate via GET (no empty cell). Verify in Phase 7 gate. |
| [`mcp-tool-execution`](../mcp-tool-execution/00-orchestration.md) | MCP `McpApi` / `McpSandbox` tool calls count toward the same budget. No MCP-specific exemption. |
| [`skills-execution`](../skills-execution/00-orchestration.md) | `skills.list` / `skills.read` are server-handled; they count. |
| [`published-wire-execution`](../published-wire-execution/00-orchestration.md) | All surfaces use `SendMessageStreamAsync` → `ThreadRun`; limits apply without wire-handler forks. |

---

## Part D — Decision ledger

| ID | Decision | Status | Resolved value | Date |
|----|----------|--------|----------------|------|
| T1 | Limit scope | LOCKED | Per `ThreadRun` + nested inheritance | 2026-07-12 |
| T2 | Null semantics | LOCKED | null = unlimited | 2026-07-12 |
| T3 | Terminal behavior | LOCKED | completed, never throw | 2026-07-12 |
| T4 | Strip tools | LOCKED | never | 2026-07-12 |
| T5 | Primary completion | LOCKED | synthetic tool results | 2026-07-12 |
| T6 | tool_choice none | LOCKED | when supported | 2026-07-12 |
| T7 | Force complete | LOCKED | server assistant message | 2026-07-12 |
| T8 | Client-handled count | LOCKED | yes | 2026-07-12 |
| T9 | Search bootstrap default | LOCKED (recommended) | 12 | 2026-07-12 |
| T10 | Tool rounds field | LOCKED | nullable; Phase 6 enforce + UI | 2026-07-12 |
| T11 | Crew member override | LOCKED | nullable; Phase 6 enforce + UI | 2026-07-12 |
| T12 | Tier 4 compaction | LOCKED | Phase 6; summarization on force-complete path | 2026-07-12 |
| T13 | Stream reconnect | LOCKED | normal completed path | 2026-07-12 |
| T14 | Evaluator inherit | LOCKED | inherit state | 2026-07-12 |
| T15 | Naming | LOCKED | snake_case JSON / Pascal C# | 2026-07-12 |
