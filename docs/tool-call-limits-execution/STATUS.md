# Tool Call Limits — Execution Status

Last updated: 2026-07-12  
Branch: `feature/tool-call-limits`  
Orchestrator: agent session (Phases 1–7)  
Proposal: [`../tool-call-limits-proposal.md`](../tool-call-limits-proposal.md) *(not committed to repo; execution docs reference it)*  
Conductor: [`00-orchestration.md`](./00-orchestration.md)

---

## Phase ledger

| Phase | Brief | Status | Gate | Notes |
|-------|-------|--------|------|-------|
| 0 | Pre-flight | DONE | — | DECISIONS T1–T15 locked in `DECISIONS.md` |
| 1 | Schema + DTO + materialization | DONE | — | Migration, DTOs, `AssistantDefinition`, `GuidesService` validation |
| 2 | Tier 1 runtime enforcement | DONE | provider-safe-completion | `ToolLimitState`, synthetic tool results, soft block |
| 3 | Tier 2–3 escalation + provider flags | DONE | provider-safe-completion | `tool_choice: none`, force-complete, `SupportsToolChoiceNone` |
| 4 | Builder UI (Tools + Crew tabs) | DONE | ui | `toolLimits/` module, Tools tab section, crew override field |
| 5 | Export/import + bootstrap defaults | DONE | runtime-parity | Round-trip tests; Creative Guide Search `max_tool_calls_per_turn: 12` |
| 6 | Rounds, crew overrides, Tier 4, trace | DONE | runtime-parity | Rounds, `Agent.Invoke` budget, trace capture, Tier 4 stub |
| 7 | Tests, docs, acceptance | DONE | codeql PASS | All gates green; CodeQL 0 new on changed files |

---

## Baseline (capture in pre-flight)

| Check | Before | After (final) |
|-------|--------|---------------|
| `dotnet build GuideAntsApi.sln` | — | PASS (0 warnings) |
| `dotnet test GuideAntsApi.Tests` | — | PASS (2001 passed, 16 skipped) |
| `dotnet test GuideAntsApi.IntegrationTests` (ToolLimit) | — | PASS (2) |
| `npm run build` (client) | — | PASS |
| `npm test -- --run toolLimit` (client) | — | PASS (6) |
| CodeQL baseline SARIF (pre-flight capture only) | — | CAPTURED (C#=2, JS=1 @ `origin/main` 49f87d9) |
| CodeQL diff (Phase 7 close-out only) | — | PASS (0 new on 48 changed source files) |

---

## Deviations

| ID | Item | Resolution |
|----|------|------------|
| D1 | `docs/tool-call-limits-proposal.md` not on disk / not in git | **RESOLVED** — restored from `origin/docs/tool-call-limits-proposal` with execution link |
| D2 | CodeQL gate | **RESOLVED** — scan via `C:\Users\dougl\tools\codeql\codeql.exe`; 0 new on changed files |
| D3 | Integration tests not executed | **RESOLVED** — pass with GHCR SQL image |

---

## Cross-plan coordination

| Plan | Status | Notes |
|------|--------|-------|
| Stream reconnect | VERIFIED | T13 integration test PASS |
| MCP tool execution | N/A | MCP tools count toward budget |
| Skills execution | N/A | `skills.list`/`skills.read` count toward budget |
| Published wire | N/A | Same `ThreadRun` path |

---

## Final acceptance (proposal §16 / orchestration §6)

- [x] Builder exposes configurable per-assistant tool call limits (Tools tab; blank = unlimited).
- [x] Limits enforced private notebook path (`ThreadRun` + `ConversationService`).
- [x] Limits enforced nested `Agent.Invoke` (`ToolLimitState.ForNestedInvoke`).
- [x] Limit message persisted (synthetic `tool` role messages + system nudge).
- [x] Turn `completed` when model retries after soft block (Tier 2 → Tier 3 force-complete).
- [x] No upstream 400s from `tool_choice: none` wire (`LlamaCppChatClientTests`).
- [x] Bootstrap Search default limit shipped (`max_tool_calls_per_turn: 12`).
- [x] Export/import preserves limits (`GuideExportImportServiceToolLimitsTests`).
- [x] Crew member `max_tool_calls_per_invocation` override in builder + runtime.
- [x] Trace captures limit state (`TurnTraceCollector.CaptureToolLimitState`).
- [x] CodeQL diff vs baseline — **PASS** (0 new on feature-changed files).
- [x] Full `GuideAntsApi.IntegrationTests` green — **ToolLimit filter** (2 tests).

---

## Gate summary

| Gate | Result |
|------|--------|
| provider-safe-completion | PASS |
| runtime-parity | PASS |
| ui | PASS (build + 6 client tests) |
| codeql | PASS |
