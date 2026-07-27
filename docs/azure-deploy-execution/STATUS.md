# GuideAnts Azure Deploy — Execution Status Ledger

The implementer updates this after each phase gate. Audit trail for the single-PR delivery.

State values: `BLOCKED` · `READY` · `IN_PROGRESS` · `GATE_FAILED` · `DONE` · `SKIPPED`.

Last updated: 2026-07-23 — **Implementation complete; deploy-gate pending test subscription**

---

## Baseline (Pre-flight, orchestration §2)

| Check | Command | Result | Date |
|---|---|---|---|
| Server build | `dotnet build GuideAntsApi.sln` (in `src/server`) | pass — 0 errors, 14 warnings | 2026-07-23 |
| Server tests | `dotnet test GuideAntsApi.sln` (in `src/server`) | pass — 2460 passed, 7 skipped, 0 failed | 2026-07-23 |
| DECISIONS resolved | A1–A12 | LOCKED | 2026-07-23 |
| Waterfall Azure reviewed | `waterfall/src/server/Azure/` | done (patterns adapted) | 2026-07-23 |
| Slim compose reviewed | `docker/docker-compose.ghcr-slim.yml` | done | 2026-07-23 |
| GHCR public confirmed | `ghcr.io/elumenotion/*` | documented requirement (consumer must verify) | 2026-07-23 |
| Feature branch | `feature/azure-deploy-slim` | created from `origin/main` | 2026-07-23 |

---

## Phase ledger (one branch — one PR)

| Phase | Brief | State | Gate result | Notes |
|---|---|---|---|---|
| 1 — Bicep infrastructure | `task-phase-1-bicep-infra.md` | DONE | `az bicep build` pass | `main.bicep` + 5 modules; no container apps |
| 2 — Container apps | `task-phase-2-container-apps.md` | DONE | `az bicep build` pass | `apps.bicep` + `container-apps.bicep` (6 apps) |
| 3 — Deploy scripts | `task-phase-3-deploy-scripts.md` | DONE | grep clean | `deploy.ps1/sh`, `generate-secrets`, `manage.ps1` |
| 4 — Docs + acceptance | `task-phase-4-docs-acceptance.md` | DONE | docs complete | README, ARCHITECTURE, setup-guide link |

---

## Deploy gate ledger

| Scan point | All apps Running | Web UI HTTP | Migrations | Notes |
|---|---|---|---|---|
| Baseline | — | — | — | not deployed |
| After Phase 2 | pending | pending | — | requires test subscription deploy |
| After Phase 3 | pending | pending | pending | run `./deploy.ps1` against test sub |
| **Final acceptance** | pending | pending | pending | consumer/agent with Azure access |

---

## Deviation log

| # | Phase | Classification | What failed | Action taken | Re-gate result |
|---|---|---|---|---|---|
| — | — | — | — | — | — |

---

## Final acceptance checklist (orchestration §6)

- [x] Phases 1–4 `DONE` (implementation)
- [ ] Deploy gate green (requires test subscription)
- [x] No secrets in git diff
- [x] DocumentServer on by default (`documentServerEnabled=true`)
- [x] `-CustomDomain` documented (README § Custom domain)
- [x] `acceptance-evidence.md` complete (template validation)
- [ ] Single PR opened (ready — user to open)
