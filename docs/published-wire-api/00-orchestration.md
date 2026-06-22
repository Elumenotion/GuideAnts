# Published Wire APIs — Execution & Orchestration Guide

Last updated: 2026-06-22

This is the conductor document for executing
[`../published-wire-api-implementation-plan.md`](../published-wire-api-implementation-plan.md).
It defines phase dispatch order, verification gates, and deviation protocol.

> Audience split
>
> - Orchestrator reads: this file + [`DECISIONS.md`](./DECISIONS.md) +
>   [`STATUS.md`](./STATUS.md) + [`test-gate.md`](./test-gate.md) +
>   [`codeql-gate.md`](./codeql-gate.md).
> - Subagents read: their own `task-phase-N-*.md` brief, cited plan sections,
>   and locked decisions.

## 0. Folder map

| File | Owner | Purpose |
|---|---|---|
| `00-orchestration.md` | Orchestrator | Dispatch protocol and gates. |
| `DECISIONS.md` | Orchestrator | Locked invariants for implementation. |
| `STATUS.md` | Orchestrator | Phase ledger + baseline + deviations. |
| `test-gate.md` | Orchestrator + all phases | Global build/test gate and pass criteria. |
| `codeql-gate.md` | Orchestrator + security phases | Two-tier CodeQL gate: fast changed-scope + full final diff. |
| `task-phase-1-datamodel.md` | Subagent | Phase 1 brief. |
| `task-phase-2-execution-context.md` | Subagent | Phase 2 brief. |
| `task-phase-3-usage-metering.md` | Subagent | Phase 3 brief. |
| `task-phase-4-wire-api-handlers.md` | Subagent | Phase 4 brief. |
| `task-phase-5-cost-limits-reporting.md` | Subagent | Phase 5 brief. |
| `task-phase-6-publishing-ui.md` | Subagent | Phase 6 brief. |
| `task-phase-7-docs-sdk-compatibility.md` | Subagent | Phase 7 brief. |
| `task-phase-8-final-acceptance.md` | Subagent | Phase 8 brief. |

## 1. Pre-flight (Phase 0 baseline)

Do not dispatch Phase 1 until all are done:

- Capture `git status`, branch name, known dirty files.
- Run baseline global test gate from `test-gate.md`.
- Capture local CodeQL baseline from `codeql-gate.md` using full all-language scan (no GitHub parity).
- Confirm `dotnet ef --version`.
- Record all outcomes and known flakes in `STATUS.md`.
- Confirm all entries in `DECISIONS.md` remain locked.

Phase 0 does not require perfect green; it requires explicit classification of
known failures before coding begins.

## 2. Dependency order

```text
Phase 0 baseline
   |
   v
Phase 1 data model
   |
   v
Phase 2 execution context (auth/cost)
   |
   v
Phase 3 usage metering wrappers
   |
   v
Phase 4 wire API handlers
   |
   v
Phase 5 cost limits + reporting
   |
   v
Phase 6 publishing UI
   |
   v
Phase 7 docs + SDK examples
   |
   v
Phase 8 final acceptance
```

Rules:

- Execute phases in order.
- A phase is done only when its gate passes.
- Do not start downstream work on top of a failed gate.

## 3. Dispatch protocol

For each phase:

1. Mark phase `IN_PROGRESS` in `STATUS.md`.
2. Dispatch one subagent with prompt:
   `Read and execute docs/published-wire-api/task-phase-N-*.md end to end. Obey guardrails and Definition of Done. Return the Report-back contract verbatim.`
3. Receive report-back.
4. Run independent gate checks (global + phase-specific + CodeQL when required).
5. Mark `DONE` only when all required checks pass.

## 4. Verification gates

Global gate runs after every phase using `test-gate.md`.

Additional required checks:

- Phase 1: migration creation + fresh/existing DB apply proof.
- Phase 2: auth-mode matrix tests + cost-limit OpenAI-shaped denial + changed-scope CodeQL.
- Phase 3: metering attribution tests proving project/notebook/published/source/request attribution.
- Phase 4: endpoint contract snapshots + provider-routing tests + changed-scope CodeQL.
- Phase 5: reporting + daily/monthly cost-limit tests.
- Phase 6: UI component tests + DTO round-trip + changed-scope CodeQL.
- Phase 7: docs endpoint/error name parity + smoke examples.
- Phase 8: full regression rerun + manual acceptance matrix + full all-language final CodeQL.

## 5. Deviation protocol

When a gate fails, stop and classify in `STATUS.md`:

- `build/test red`
- `missing DoD`
- `scope creep`
- `decision drift`
- `security regression`
- `fallback/masking introduced`

Then re-dispatch the same phase with targeted correction notes and re-run the
full gate. Cap retries at 2; escalate on a third failure.

## 6. Final acceptance

Feature closes only when all are true:

- All phase briefs are `DONE` with passing gates.
- Final CodeQL diff is clean vs baseline.
- OpenAI-compatible endpoint contracts are stable.
- Successful wire calls always meter usage.
- Auth and cost behavior align with `PublishedGuide` config.
- UI enables and explains wire API operation safely.
- Reporting separates wire API usage from conversation usage.
