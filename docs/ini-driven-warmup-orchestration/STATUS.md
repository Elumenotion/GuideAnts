# INI-driven Warmup Orchestration — Execution Status Ledger

State values: `BLOCKED` · `READY` · `IN_PROGRESS` · `GATE_FAILED` · `DONE`.

---

## Baseline (pre-flight)

| Check | Command | Result | Date |
|---|---|---|---|
| `main` includes #68 | `git log -1 --oneline origin/main` | **PASS** (`49f87d9`) | 2026-07-12 |
| Feature branch | `feature/ini-driven-warmup-orchestration` | **CREATED** | 2026-07-12 |
| Plan committed | `docs/ini-driven-warmup-orchestration/` | **DONE** | 2026-07-12 |

---

## Phase ledger

| Phase | Brief | State | Attempts | Gate result | Notes |
|---|---|---|---|---|---|
| 1 — INI contract | `task-phase-1-ini-contract.md` | DONE | 1 | **PASS** | `warmup_desired_ini.py` + `warmup_state.py` + unit tests |
| 2 — Orchestrator | `task-phase-2-orchestrator.md` | DONE | 1 | **PASS** | `warmup_orchestrator.py` + `warmup_engine_client.py` |
| 3 — ga-admin routes | `task-phase-3-ga-admin-routes.md` | DONE | 1 | **PASS** | `warmup_routes.py` + startup auto-apply |
| 4 — API client | `task-phase-4-api-desired-builder.md` | DONE | 1 | **PASS** | `LocalAiDesiredStateBuilder` + `LocalAiWarmupOrchestrationClient` |
| 5 — API call sites | `task-phase-5-api-call-sites.md` | DONE | 1 | **PASS** | `LocalAiStartupWarmupService` delegates to INI+apply |
| 6 — Readiness/UI | `task-phase-6-readiness-ui.md` | DONE | 1 | **PASS** | `RoutingReadinessService` orchestrator blockers |
| 7 — Tests/regression | `task-phase-7-tests.md` | DONE | 2 | **PASS** | Python (16) + C# unit/integration green; fake-engine reconcile test added |

---

## Open deviations

- D6 chat exception: notebook **load** may still call `ILlamaServerRuntimeClient` for the multi-alias delta between orchestrator applies (aux drain → direct multi-alias delta → aux restore with `LlamaRouterAliasOverride`). Settings `/runtime/load` and notebook **unload** are INI+apply only.

## Resolved (this pass)

- Test doubles: `NoOpLocalAiStartupWarmupService` (both integration factories) now implement `ILocalAiWarmupService`, so the DI `ILocalAiWarmupService` registration (cast of the startup singleton) resolves without `InvalidCastException`.
- Notebook runtime service injects `ILocalAiWarmupService` explicitly (no runtime cast).
- Notebook **unload** (`StartUnloadForNotebookContextAsync`) now returns to the default routed state via `SyncDesiredAndApplyAsync()` instead of direct `_llamaClient.UnloadModelAsync` loops — orchestrator is the single unload authority.
- Orchestrator bug fixed: a llama change that triggers GPU drain now **reloads** the warm aux it drained (previously their transition was `noop`, so the load phase skipped them and they stayed unloaded). Covered by `test_llama_change_drains_all_warm_aux_then_restores_in_d11_order`.

---

## Verification

```bash
python -m pytest docker/build/guideants-ai/admin-service/tests -q            # 16 passed
dotnet test src/server/GuideAntsApi.Tests/GuideAntsApi.Tests.csproj \
  --filter "FullyQualifiedName~Bootstrap|FullyQualifiedName~NotebookModelRuntimeServiceTests"   # 42 passed
dotnet test src/server/GuideAntsApi.IntegrationTests/GuideAntsApi.IntegrationTests.csproj \
  --filter "FullyQualifiedName~Load_|FullyQualifiedName~Unload_|FullyQualifiedName~LlamaRoutes_|FullyQualifiedName~DispatchChat_"   # 9 passed, 6 skipped (need engine)
```
