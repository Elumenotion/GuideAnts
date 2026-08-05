# Local-AI Regression Recovery — Execution Status Ledger

The implementer updates this after each phase gate. Audit trail for the work.

State values: `BLOCKED` · `READY` · `IN_PROGRESS` · `GATE_FAILED` · `DONE` · `SKIPPED`.

Last updated: 2026-08-05 — **Orchestration authored; pre-flight not started**

---

## Baseline (Pre-flight, orchestration §2)

| Check | Command / source | Result | Date |
|---|---|---|---|
| Server build | `cd src/server && dotnet build GuideAntsApi.sln` | pending | |
| Server tests | `cd src/server && dotnet test GuideAntsApi.sln` | pending | |
| SEA tests | `dotnet test src/server/ScriptExecutionAgent.Tests/ScriptExecutionAgent.Tests.csproj` | pending | |
| Active flavor (B2) | `docker/.env` → `GA_AI_*_IMAGE` | pending | |
| Image under test | `docker inspect --format '{{.Id}}' <tag>` | pending | |
| Running stack | `docker compose -f docker/docker-compose.<flavor>.yml ps` | pending | |
| Control model read | `DECISIONS.md` Part A | pending | |
| Flavor set | `DECISIONS.md` D1 (default: active `GA_AI_*_IMAGE`) | pending | |
| Spec read | `docs/local-ai-regression-recovery-spec.md` | pending | |

### As-built confirmation (orchestration §1.1)

Confirm each row still holds before Phase 1. A `no` here becomes a Phase 1 finding.

| Spec clause | Expected as-built | Confirmed? | Notes |
|---|---|---|---|
| §2.2 env defaults in all 5 flavors | yes | pending | |
| §2.3 durable + runtime + content mounts | yes | pending | |
| §3.1.3 missing runtime root fails startup | yes | pending | |
| §3.1.4 no startup fleet walk in SEA | yes | pending | |
| §3.3 additive, no uninstall path | yes | pending | |
| §3.4 on-demand single-scope provisioning | yes | pending | |
| §3.5 scoped + global admin apply with jobs | yes | pending | |
| §3.1.6 entrypoint bootstrap is global + hash-gated | yes | pending | |

### Gap confirmation (orchestration §1.2)

| # | Gap | Confirmed present? | Owning phase |
|---|---|---|---|
| G1 | Entrypoint bootstrap runs before SEA start | pending | 2 |
| G2 | No execute↔apply mutex on same scope | pending | 1 |
| G3 | Scoped status cannot report runtime hydration | pending | 1 |
| G4 | HEALTHCHECK OR-chain can mask dead SEA | pending | 3 |
| G5 | Dependents use `service_started` | pending | 3 |
| G6 | Readiness enforced by health gate, not retry | pending | 3 |
| G7 | §6.1 coverage incomplete | pending | 1 |
| G8 | No proof flavors share one SEA publish | pending | 2 |
| G9 | §8 control plane absent | pending | 5 |
| G10 | Duplicate guideScopeId resolvers | pending | 5 |

---

## Phase ledger

| Phase | Brief | State | Gate result | Notes |
|---|---|---|---|---|
| 1 — SEA runtime invariants | `task-phase-1-sea-runtime-invariants.md` | BLOCKED (pre-flight) | — | §3.1/§3.6/§3.7 + §6.1 tests |
| 2 — Image + payload contract | `task-phase-2-image-payload-contract.md` | BLOCKED | — | §2.1/§2.2/§3.1.6 |
| 3 — Readiness contract | `task-phase-3-readiness-contract.md` | BLOCKED | — | §4 via health gate (B7) |
| 4 — Runtime acceptance A1–A5 | `task-phase-4-runtime-acceptance-a1-a5.md` | BLOCKED | — | requires approved recreate |
| 5 — API hydration control plane (job type + scheduler, B16) | `task-phase-5-api-hydration-control-plane.md` | BLOCKED | — | §8.1–8.5 |
| 6 — Hydration acceptance + docs | `task-phase-6-hydration-acceptance-docs.md` | BLOCKED | — | A6–A8, §6.4, remaining flavors |

---

## Acceptance ledger (spec §5)

Re-recorded at every runtime gate run. One row per run; keep history.

| Run | Date | Image ID | A1 | A2 | A3 | A4 | A5 | A6 | A7 | A8 |
|---|---|---|---|---|---|---|---|---|---|---|
| Baseline (pre-change) | | | | | | | | | | |
| After Phase 4 | | | | | | | — | — | — |
| After Phase 6 | | | | | | | | | |

---

## Control-plane ledger (spec §6.4)

| Run | Source of truth | Ranking | Idle gate | Cap | No global apply |
|---|---|---|---|---|---|
| After Phase 5 (unit) | pending | pending | pending | pending | pending |
| After Phase 6 (runtime) | pending | pending | pending | pending | pending |

---

## Container recreate log

Every recreate needs explicit user approval in the requesting message (repo rule).

| # | Date | Service | Reason | Approved by user? | New image ID |
|---|---|---|---|---|---|
| — | — | — | — | — | — |

---

## Deviation log

| # | Phase | Classification | What failed | Action taken | Re-gate result |
|---|---|---|---|---|---|
| — | — | — | — | — | — |

Classifications (orchestration §6): `bind regression` · `fleet walk` ·
`additive violation` · `source-of-truth violation` · `readiness gap` · `payload drift` ·
`idle-gate leak` · `scope creep`.

---

## Final acceptance checklist (orchestration §7)

- [ ] Phases 1–6 `DONE`
- [ ] §6.1 automated coverage green; every required row has a named test
- [ ] A1–A5 green on active flavor with image ID captured
- [ ] A6–A8 green with control plane enabled
- [ ] §6.4 matrix green
- [ ] All run/published flavors from one SEA publish (§2.1.2)
- [ ] §8.6 preserved (bind-first, no fleet reconcile, additive, single-scope)
- [ ] Anti-goals audited — none present in diff
- [ ] `acceptance-evidence.md` complete
