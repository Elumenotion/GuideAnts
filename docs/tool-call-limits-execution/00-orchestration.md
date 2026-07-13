# Per-Assistant Tool Call Limits — Execution & Orchestration Guide

Last updated: 2026-07-12

This is the **conductor** document for executing
[`../tool-call-limits-proposal.md`](../tool-call-limits-proposal.md). It is written for the
**top-level (orchestrating) agent**. It defines how the work is split into **subagent task
briefs**, the **dependency order**, the **verification gates** the orchestrator runs after
each phase, and the **deviation/failure protocol** that keeps the plan on-rails so it is
executed correctly the first time.

> **Audience split**
>
> - **You (orchestrator)** read this file plus [`DECISIONS.md`](./DECISIONS.md),
>   [`STATUS.md`](./STATUS.md), [`provider-safe-completion-gate.md`](./provider-safe-completion-gate.md),
>   [`runtime-parity-gate.md`](./runtime-parity-gate.md), and [`ui-gate.md`](./ui-gate.md).
>   Read [`codeql-gate.md`](./codeql-gate.md) before Phase 7 only — CodeQL runs once at
>   close-out, not after intermediate phases. You dispatch subagents, run gates, and update
>   `STATUS.md`.
> - **Subagents** read only their own `task-phase-N-*.md` brief, the proposal sections it
>   cites, and `DECISIONS.md`. A subagent should **not** need any other context.

---

## 0. How to use this folder

| File | Owner | Purpose |
|------|-------|---------|
| `00-orchestration.md` (this) | Orchestrator | Dispatch order, gates, deviation protocol, cross-plan alignment. |
| `DECISIONS.md` | Orchestrator (locked before dispatch) | Locks proposal §15 + T1–T15. Single source of truth. |
| `STATUS.md` | Orchestrator (update after every gate) | Living ledger: phase state, gate results, deviations, re-dispatches. |
| `provider-safe-completion-gate.md` | Orchestrator + Phases 2,3,6,7 | Upstream-safe escalation; no 400s from tool-history violations. |
| `runtime-parity-gate.md` | Orchestrator + Phases 2,3,5,7 | All `ThreadRun` paths + nested crew + evaluator inherit. |
| `ui-gate.md` | Orchestrator + Phase 4,6,7 | Full UX contract; §3 reuse + §4 decomposition (inherits skills §3–4). |
| `codeql-gate.md` | Orchestrator + Phase 7 only | Baseline captured in pre-flight; diff run once at close-out. |
| `task-phase-1-schema-dto-materialization.md` | Subagent | DB + DTO + `AssistantDefinition` + validation. |
| `task-phase-2-tier1-runtime-enforcement.md` | Subagent | `ToolLimitState` + Tier 1 soft block in `ThreadRun`. |
| `task-phase-3-tier2-tier3-escalation.md` | Subagent | `tool_choice: none` + force-complete + runtime instruction override. |
| `task-phase-4-builder-ui.md` | Subagent | Tools tab limits + Crew tab summary. |
| `task-phase-5-export-import-bootstrap.md` | Subagent | `GuideExportImportService` + Creative Guide Search default. |
| `task-phase-6-rounds-crew-trace.md` | Subagent | Rounds, crew overrides, Tier 4, trace. |
| `task-phase-7-tests-docs-acceptance.md` | Subagent | Cross-cutting tests, docs, stream-reconnect verification. |
| `acceptance-evidence.md` | Orchestrator + Phase 7 | Captured commands/outputs proving final acceptance. |

Each task brief follows the **same template**: Mission → Read first → Preconditions →
Guardrails → Tasks → Files in/out of scope → Self-verification → Definition of Done →
Report-back contract.

---

## 1. Pre-flight (do this once, before any subagent is dispatched)

**Do not dispatch Phase 1 until all of the following are true.**

- [ ] **`DECISIONS.md` is fully LOCKED** (T1–T15). Confirm T9 (Search default `12`) with the
      user if bootstrap defaults must differ.
- [ ] **Read cross-plan posture** (no blocking, but gates must verify):
  - [`../conversation-stream-reconnect-and-cancel-proposal.md`](../conversation-stream-reconnect-and-cancel-proposal.md) §14 — limit-completed turns release lock and rehydrate.
  - [`../mcp-tool-execution/STATUS.md`](../mcp-tool-execution/STATUS.md) — MCP tools are server-handled in `ThreadRun`.
  - [`../skills-execution/STATUS.md`](../skills-execution/STATUS.md) — `skills.list`/`skills.read` are server-handled.
- [ ] **Capture a clean baseline** and record it in `STATUS.md`:
  - `cd src/server && dotnet build GuideAntsApi.sln`
  - `cd src/server && dotnet test GuideAntsApi.sln`
  - `cd src/client && npm run build`
  - `cd src/client && npm test -- --run`
- [ ] **Capture CodeQL baseline SARIF only** (`codeql-gate.md` §1) →
      `.codeql/baseline/tool-call-limits/`. Do **not** run a diff until Phase 7.
- [ ] **Inventory touchpoints** (grep before editing):
  - `src/server/GuideAntsApi.DataModel/Models/Assistant.cs`
  - `src/server/GuideAntsApi.DataModel/Models/GuideMember.cs`
  - `src/server/AntRunner.Chat/AntRunner.ToolCalling/InvocationContext.cs`
  - `src/server/AntRunner.Chat/AntRunner.ToolCalling/AssistantDefinitions/AssistantDefinition.cs`
  - `src/server/AntRunner.Chat/AntRunner.ToolCalling/AssistantDefinitions.Storage/DatabaseStorage.cs`
  - `src/server/AntRunner.Chat/AntRunner.Chat/ThreadRun.cs` (`case "tool_calls"`, `DoToolCalls`)
  - `src/server/AntRunner.Chat/AntRunner.Chat.Abstractions/ChatCompletionRequest.cs`
  - `src/server/AntRunner.Chat/AntRunner.Chat/OpenAiChatClient.cs` (and `LlamaCppChatClient`, Anthropic clients)
  - `src/server/GuideAntsApi/Services/Conversations/Agent.cs` (`Agent.Invoke` nested run)
  - `src/server/GuideAntsApi/Services/Guides/GuideExportImportService.cs`
  - `src/client/src/components/guides/editor/ToolsTab.tsx`, `CrewTab.tsx`, `BaseEntityEditor.tsx`
  - `src/client/src/types/guides.ts` (or equivalent DTO types)
- [ ] Confirm clean working tree and feature branch `feature/tool-call-limits` from updated
      `main`. **Never** set upstream to `origin/main`.
- [ ] Confirm `dotnet ef --version` is available (Phase 1 adds DB columns).

---

## 2. Dependency graph (dispatch order)

```text
                 Phase 1  Schema + DTO + materialization
                 (Assistants + GuideMembers columns; DTOs; AssistantDefinition;
                  validation; EF migration)  T2/T15
                          │
             ┌────────────┼────────────────┐
             ▼            ▼                │
         Phase 2      Phase 4             │
   (ToolLimitState;  (Builder UI:         │
    Tier 1 soft      Tools + Crew tabs)  │
    block in         T2/T15               │
    ThreadRun)                           │
    T1/T3/T5/T8/T13/T14                  │
             │            │                │
             ▼            │                │
         Phase 3          │                │
   (ToolChoice field;    │                │
    Tier 2–3 ladder;     │                │
    provider caps;       │                │
    runtime override)    │                │
    T4/T6/T7             │                │
             │            │                │
             └─────┬──────┴────────────────┘
                   ▼
              Phase 5  Export/import + bootstrap
              (GuideExportImportService; Search default 12)  T9/T15
                   │
                   ▼
              Phase 6  Rounds + crew overrides + Tier 4 + trace  T10/T11/T12
                   │
                   ▼
              Phase 7  Tests, docs, acceptance + stream-reconnect check
```

**Design-phase mapping (proposal §12):**

| Proposal phase | Orchestration phases |
|----------------|----------------------|
| Phase A — Core limit + Tier 1 | Phases 1, 2, 4 (UI can parallel 2 after 1) |
| Phase B — Tier 2 + Tier 3 | Phase 3 |
| Phase C — Tier 4 + polish | Phases 5, 6, 7 |

**Rules:**

- Phases run in dependency order. **A phase is not "done" until its gate (section 4)
  passes.**
- **Phases 2 and 4 may run in parallel** after Phase 1's gate is green — Phase 2 is
  `ThreadRun`; Phase 4 is client + DTO wiring. Prefer sequential (2 before 4) so UI can be
  manually tested against working enforcement.
- **Phase 5** requires Phase 1 (schema + DTOs); can run after Phase 4 or in parallel with
  Phase 3 if export paths are stable.
- **Phase 6** ships rounds enforcement, crew member overrides, Tier 4 compaction, and trace
  metadata (T10–T12). All proposal Phase C items except bootstrap (Phase 5).
- **Phase 7** always runs last; it owns cross-plan stream-reconnect verification (T13) and
  CodeQL close-out.
- One subagent per phase brief.

---

## 3. Dispatch protocol (per phase)

1. **Confirm preconditions** in the brief. Update `STATUS.md` → phase `IN_PROGRESS`.
2. **Dispatch one subagent** with exactly: *"Read and execute
   `docs/tool-call-limits-execution/task-phase-N-*.md` end to end. Obey its guardrails and
   Definition of Done. Return the Report-back contract verbatim."*
3. **Receive the Report-back** as a claim, not proof.
4. **Run the gate** (section 4) with your own tools.
5. **Decide:** PASS → mark phase `DONE`, proceed. FAIL → section 5.

> You verify; the subagent implements.

---

## 4. Verification gates

### 4.1 Global invariants — checked at **every** gate

- [ ] Server build + tests green (vs baseline).
- [ ] Client build + tests green (vs baseline).
- [ ] **Never throw on limit** — turn status `completed`, not `error` (T3).
- [ ] **No strip-tools-with-tool-history** (T4).
- [ ] **Synthetic tool results for every blocked call** — pairing preserved (T5).
- [ ] **Distinct from `MaxTurns`** — grep UI/copy for conflation.
- [ ] **No fallback masking** — no silent retry after limit, no hidden tool execution.
- [ ] **One choke point** — limit logic only in `ThreadRun` `tool_calls` branch before
      `DoToolCalls`.
- [ ] **Matches `DECISIONS.md`**.

### 4.2 Per-phase gate criteria

**Phase 1 — Schema + DTO + materialization**

- [ ] `Assistants.MaxToolCallsPerTurn`, `Assistants.MaxToolRoundsPerTurn` nullable ints.
- [ ] `GuideMembers.MaxToolCallsPerInvocation` nullable int (column exists even if UI ships
      in Phase 6).
- [ ] DTOs + client types + `AssistantDefinition` carry limits; `DatabaseStorage` materializes.
- [ ] Validation: negative values rejected at save; `null` = unlimited.
- [ ] EF migration applies cleanly.

**Phase 2 — Tier 1 runtime**

- [ ] `ToolLimitState` on `InvocationContext` (or parallel run-scoped carrier passed through
      `ThreadRun` + `Agent.Invoke`).
- [ ] Before `DoToolCalls`: if budget exhausted → synthetic limit `tool` messages per
      `tool_call_id`, `tool_result` SSE, system nudge, `Phase = SoftBlocked`.
- [ ] Counter increments for executed and blocked server-handled tools; client-handled
      emission counts (T8).
- [ ] Nested `Agent.Invoke` gets child state; parent decrements; evaluator inherits (T14).
- [ ] **provider-safe-completion gate** §1–2 pass; **runtime-parity gate** §1–2 pass.

**Phase 3 — Tier 2–3 escalation**

- [ ] `ChatCompletionRequest.ToolChoice` (`null` | `"none"`) + provider mapping.
- [ ] `SupportsToolChoiceNone` capability flag per client/profile.
- [ ] After soft block + model retries tools → Tier 2 when supported, else Tier 3.
- [ ] Tier 3: server-authored assistant message, `continueChat = false`, `completed`.
- [ ] Runtime instruction override when `Phase >= SoftBlocked` (proposal Tier 5).
- [ ] **provider-safe-completion gate** full pass including retry-after-soft-block test.

**Phase 4 — Builder UI**

- [ ] **ui-gate** passes.
- [ ] DTO round-trip from Phase 1 verified through UI.

**Phase 5 — Export/import + bootstrap**

- [ ] `GuideExportImportService` reads/writes `max_tool_calls_per_turn` (and rounds if column
      populated).
- [ ] Creative Guide Search manifest: `max_tool_calls_per_turn: 12` (T9).
- [ ] **runtime-parity gate** — bootstrap materializes limit on Search assistant.

**Phase 6 — Rounds, crew overrides, Tier 4, trace**

- [ ] `max_tool_rounds_per_turn` enforced (separate counter) and editable in Tools tab
      advanced section (T10).
- [ ] `GuideMember.MaxToolCallsPerInvocation` editable on Crew tab and enforced in nested
      budget formula (T11).
- [ ] Tier 4 compacted summarization on force-complete path (T12); Tier 3 stub fallback if
      summarization fails.
- [ ] Turn trace exposes limit-hit count / phase reached.
- [ ] **ui-gate** §5 Phase 6 passes.
- [ ] **runtime-parity gate** §3 and **provider-safe-completion gate** §4 pass.

**Phase 7 — Tests, docs, acceptance**

- [ ] Proposal §13 automated tests present and green.
- [ ] Manual matrix documented in `acceptance-evidence.md`.
- [ ] **Stream reconnect (T13):** limit-completed turn visible on GET reload (no empty assistant
      gap when final server message persisted).
- [ ] Proposal §16 success criteria checked in `STATUS.md`.
- [ ] **codeql-gate** — baseline-vs-current diff clean (only CodeQL run of the plan).
- [ ] All other gates green; `acceptance-evidence.md` captured.

### 4.3 UI gate (summary)

Defined in `ui-gate.md` (inherits reuse/decomposition from `skills-execution/ui-gate.md` §3–4).
Run after Phase 4 and Phase 7. Pass when Tools-tab execution limits and Crew-tab member summary
meet §2–§4, with §3 reuse table satisfied and no §7 fail modes.

### 4.4 CodeQL gate (summary)

Defined in `codeql-gate.md`. **Run only in Phase 7** after Phases 1–6 are complete. Pre-flight
captures baseline SARIF; the orchestrator does not run intermediate CodeQL diffs between phases.

---

## 5. Deviation & failure protocol

When a gate fails, **stop the line.**

1. **Classify** in `STATUS.md`:
   - `build/test red` → re-dispatch with exact error.
   - `provider-400` → strip-tools, orphan pairing, or invalid history. Hard reject.
   - `throw-on-limit` → hard reject (T3).
   - `path-skipped` → limit not on a `ThreadRun` entry path. Hard reject.
   - `budget-reset` → evaluator reopen bug (T14).
   - `wrong-tab` / `conflated-copy` → UI gate fail.
   - `fallback/masking` → hard reject (user rule).
   - `ui monolith / reinvention` (Phases 4, 6) → limit logic in monolith `ToolsTab`/`GuideCrewManager`,
     duplicate number-input/toast, or new panel over ~250 lines. Hard reject per `ui-gate.md` §7.
   - `decision drift` → revert and re-dispatch with DECISIONS re-quoted.
2. **Re-dispatch** same brief with focused correction. Re-run **full** gate.
3. **Cap retries at 2.** Third failure → escalate to user.
4. **Record** attempt #, failure mode, corrective diff, re-gate result.

---

## 6. Final acceptance (after Phase 7 gate)

- [ ] Proposal §16 success criteria — all boxes checked.
- [ ] Builder exposes configurable per-assistant tool call limits (Tools tab; blank = unlimited).
- [ ] Limits enforced private, published, nested `Agent.Invoke`.
- [ ] Limit message persisted and visible to user and model.
- [ ] Turn `completed` when model retries (Search-style).
- [ ] No upstream 400s in llama.cpp / OpenAI integration tests.
- [ ] Bootstrap Search default limit shipped.
- [ ] All gates green (provider-safe-completion, runtime-parity, ui, codeql at close-out);
      `STATUS.md` every dispatched phase `DONE`; no open deviations.

When complete, summarize for the user: phases run, retries, and stream-reconnect verification
outcome.
