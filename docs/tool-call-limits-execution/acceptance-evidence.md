# Tool Call Limits — Acceptance Evidence

Last updated: 2026-07-12  
Branch: `feature/tool-call-limits`

## Automated — server unit tests

```bash
cd src/server
dotnet build GuideAntsApi.sln
dotnet test GuideAntsApi.Tests --filter "FullyQualifiedName~ToolLimit"
dotnet test GuideAntsApi.Tests
```

| Test class / area | Count | Result | Notes |
|-------------------|-------|--------|-------|
| `ToolLimitStateTests` | 9 | PASS | Budget math, nested invoke, 12th/13th scenario |
| `GuidesServiceToolLimitsTests` | 4 | PASS | CRUD validation, crew member limits |
| `GuideExportImportServiceToolLimitsTests` | 2 | PASS | Export/import round-trip + bootstrap |
| `ThreadRunTests` (limit helpers) | 4 | PASS | Inject synthetic results, nudge, force-complete, compaction |
| `LlamaCppChatClientTests.GetCompletionAsync_SendsToolChoiceNone_WhenLimitEscalationRequestsIt` | 1 | PASS | `tool_choice: none` wire serialization |
| **Total unit (full suite)** | **2001** | **PASS** | 16 skipped (platform-specific) |

### Proposal §13 scenario coverage (automated)

| Scenario | Test | Result |
|----------|------|--------|
| 12th executes; 13th synthetic | `ToolLimitStateTests.WouldExceedToolCalls_12thExecutes_13thSynthetic` | PASS |
| Retry after soft block → Tier 2/3, `completed` | `ToolLimitIntegrationTests.SendMessageStream_ToolLimit_AllowsMaxThenSyntheticResult_AndEscalatesToForceComplete` | PASS |
| llama.cpp `tool_choice: none` wire | `LlamaCppChatClientTests` (above) | PASS |
| Nested `Agent.Invoke` budgets | `ToolLimitStateTests.ForNestedInvoke_*` | PASS |
| Evaluator inherit | `ThreadRun.ExecuteAsync` passes `ctx` (code review); `ToolLimitState` not reset on evaluator reopen | PASS (static analysis) |
| Export/import | `GuideExportImportServiceToolLimitsTests` | PASS |

## Automated — integration tests (authored)

```bash
cd src/server
dotnet test GuideAntsApi.IntegrationTests --filter "FullyQualifiedName~ToolLimitIntegrationTests"
```

| Test | Result | Notes |
|------|--------|-------|
| `SendMessageStream_ToolLimit_AllowsMaxThenSyntheticResult_AndEscalatesToForceComplete` | PASS | Synthetic limit tool result, Tier 2 `tool_choice: none`, turn `completed` |
| `SendMessageStream_ToolLimit_CompletedTurn_RehydratesOnGetReload_T13` | PASS | GET reload returns non-empty final assistant (Tier 4 summarize path) |

**Fake scenario:** `FakeChatScenario.RepeatedToolCalls` — model always returns `tool_calls`; asserts synthetic limit tool result, Tier 2 `tool_choice: none`, Tier 3 force-complete assistant message, turn `completed`.

## Automated — client

```bash
cd src/client
npm run build
npm test -- --run toolLimit
```

| Test file | Count | Result |
|-----------|-------|--------|
| `toolLimitForm.test.ts` | 3 | PASS |
| `toolLimitDisplay.test.ts` | 3 | PASS |

## Manual matrix (proposal §13)

| Scenario | Steps | Expected | Result |
|----------|-------|----------|--------|
| Search limit 5 | Set limit in builder; research question | Limit message in workflow; turn completes with partial answer | _pending operator_ |
| Lock release | Complete limit-hit turn | Conversation lock released; new message sendable | _pending operator_ |
| Export/import | Export guide with limits; import | Limits preserved | Covered by automated export/import tests |
| Stream reconnect (T13) | Limit-complete turn; reload conversation | Final assistant message visible; no empty cell | Test authored; run locally after D3 resolved |

## T13 verification

- **Designed check:** `ToolLimitIntegrationTests.SendMessageStream_ToolLimit_CompletedTurn_RehydratesOnGetReload_T13` calls `IConversationService.GetConversationByIdAsync` after a limit-completed stream and asserts a non-empty final assistant message containing the force-complete text.
- **Outcome:** PASS — integration tests green with `GA_INTEGRATION_TEST_MSSQL_IMAGE=ghcr.io/elumenotion/mssql2025-express-fts:main`.

## Gate results (final)

| Gate | Result | Evidence |
|------|--------|----------|
| provider-safe-completion | PASS | Synthetic tool results paired with `tool_call_id`; Tier 2 `tool_choice: none`; Tier 3 force-complete; Llama wire test |
| runtime-parity | PASS | Integration + unit tests; `ThreadRun` / `Agent.Invoke` paths |
| ui | PASS | Client build + toolLimit tests; Tools tab limits section shipped in Phase 4 |
| codeql | PASS | 0 new on 48 feature-changed files vs baseline (`origin/main` @ 49f87d9) |

## CodeQL close-out (2026-07-12)

CLI: `C:\Users\dougl\tools\codeql\codeql.exe` (CodeQL 2.26.0). Not on default PATH — use `-CodeqlPath` with repo scripts.

Baseline SARIFs saved to `.codeql/baseline/tool-call-limits/` from `origin/main` worktree: **C#=2**, **JS=1**.

Current scan on `feature/tool-call-limits` (C# `build-mode=none`, JS `source-root=src/client`):

| Metric | Value |
|--------|-------|
| NEW findings vs baseline on **48 changed source files** | **0** |
| Full-tree NEW (environmental: `.codeql/db-*`, `.build/`, `publish/web.config`) | 11 — not feature code |

## Recommended follow-ups

1. Operator manual pass: Search limit 5 + lock release in running stack.
2. Commit feature branch when ready (all gates green).
