# INI-driven Warmup Orchestration — Execution Guide

Last updated: 2026-07-12

Orchestration document for implementing [`PLAN.md`](./PLAN.md).

> **Audience split**
>
> - **Orchestrator** reads this file + [`DECISIONS.md`](./DECISIONS.md) + [`STATUS.md`](./STATUS.md).
> - **Subagents** read only their phase brief (when added) and cited plan sections.

**Prerequisite:** Local model onboarding (#67 / #68) merged to `main`.

---

## Dependency graph

```
 Phase 1  INI contract + warmup_desired_ini.py (atomic write, revision bump)
    │
    ▼
 Phase 2  warmup_orchestrator.py (incremental reconcile, D11 order, engine calls)
    │
    ▼
 Phase 3  ga-admin routes (PUT/POST/GET warmup) + container startup auto-apply
    │
    ▼
 Phase 4  API LocalAiDesiredStateBuilder + LocalAiWarmupOrchestrationClient
    │
    ▼
 Phase 5  Migrate API call sites; delete LocalAiStartupWarmupService HTTP loops
    │
    ▼
 Phase 6  Readiness/UI wiring + RoutingReadinessService blockers
    │
    ▼
 Phase 7  Tests (Python + C#) + cuda-stack regression (provider switch, llama 502)
```

Phases 1–3 can ship in the `guideants-ai` image before API migration (orchestrator idle until API writes INI).

---

## Phase ledger (summary)

| Phase | Brief | Gate |
|---|---|---|
| 1 — INI contract | `task-phase-1-ini-contract.md` | Round-trip tests; atomic write under lock |
| 2 — Orchestrator | `task-phase-2-orchestrator.md` | Incremental idle for one service; D11 order |
| 3 — ga-admin routes | `task-phase-3-ga-admin-routes.md` | PUT/POST/GET via nginx; startup auto-apply |
| 4 — API client | `task-phase-4-api-desired-builder.md` | Builder + client contract tests |
| 5 — API call sites | `task-phase-5-api-call-sites.md` | No direct engine load/unload from API |
| 6 — Readiness/UI | `task-phase-6-readiness-ui.md` | Warmup-pending label fixed; blockers wired |
| 7 — Tests/regression | `task-phase-7-tests.md` | CI green; cuda provider-switch manual gate |

See [`STATUS.md`](./STATUS.md) for live state.

---

## Dispatch rules

1. One phase per subagent unless the orchestrator expands scope.
2. Do not start Phase 5 until Phase 3 endpoints are callable from integration tests.
3. Delete `LocalAiStartupWarmupService` bulk logic in Phase 5 — do not leave dead code.
4. Rebuild `guideants-ai` after Phases 1–3 before end-to-end API migration.
