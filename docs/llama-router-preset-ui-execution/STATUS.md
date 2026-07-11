# Curated Local Llama — Execution Status Ledger

The orchestrator updates this after every dispatch and independent gate.

States: `BLOCKED` · `READY` · `IN_PROGRESS` · `GATE_FAILED` · `DONE`.

**Worktree:** `feature/curated-local-llama` (uncommitted implementation; user-owned commits)

**Orchestration policy:** no agent commits/pushes; CodeQL run once at 8A closeout only.

## Pre-flight baseline

| Check | Command/evidence | Baseline result | Date |
|---|---|---|---|
| Worktree ownership | Phase 0 inventory | **captured** | 2026-07-10 |
| Server build | `dotnet build GuideAntsApi.sln` | **pass** | 2026-07-11 |
| Server tests | `dotnet test GuideAntsApi.sln` | **~2300 pass / 1 fail / 33 skip** (warmup timeout pre-existing) | 2026-07-11 |
| Client build | `npm run build` | **pass** | 2026-07-11 |
| Client tests | `npm test -- --run` | **3357 pass** | 2026-07-11 |
| Client orphan scan | `npm run find-orphans` | **2 issues** (1 unused file, 1 unlisted dep; improved from 3) | 2026-07-11 |
| Python deterministic | `unittest discover … llama-admin-service/tests` | **44/44 pass** (live drift excluded) | 2026-07-11 |
| Shell syntax | `bash -n start-llama.sh entrypoint.sh` | **pass** | 2026-07-11 |
| EF migrations | head `20260711013948_AddCuratedLocalLlamaPersistencePhase1B` | **pass** | 2026-07-11 |
| CodeQL baseline | `run-codeql-sln-triage.ps1 -SkipGitHubParityCheck` | **captured** — C#=7 Py=4 JS=1; 0 new in feature code | 2026-07-11 |
| Sharded preset / fleet spike | Phase 0 proofs | **proven** | 2026-07-10 |
| Frozen contracts | C#/Python/TS validators | **27 fixtures + schema** | 2026-07-10 |

### Accepted server test failure

| Project | Test |
|---|---|
| GuideAntsApi.Tests | `LocalAiStartupWarmupServiceTests.ReconcileLocalServiceAsync_SelectActive_AutoActivatesLocalProvider_WhenRoutingMissing` (timeout, pre-existing) |

## Phase ledger

| Phase | Brief | State | Attempts | Gate | Notes |
|---|---|---:|---:|---|---|
| 0 | `task-phase-0-contracts-baseline.md` | **DONE** | 1 | **pass** | contracts + D1/D4 proofs |
| 1A | `task-phase-1a-catalog-discovery.md` | **DONE** | 1 | **pass** | 14-def manifest + catalog/quants API |
| 1B | `task-phase-1b-persistence-profiles.md` | **DONE** | 1 | **pass** | persistence entities + profile tool fields |
| 2 | `task-phase-2-router-download.md` | **DONE** | 1 | **pass** | exact download, preset replace/merge, journal |
| 3 | `task-phase-3-runtime-fleet-migration.md` | **DONE** | 1 | **pass** | minimal runtime JSON, fleet preset, migration |
| 4 | `task-phase-4-curated-install.md` | **DONE** | 1 | **pass** | identity-only add, SQL operation authority |
| 5 | `task-phase-5-lifecycle-api.md` | **DONE** | 1 | **pass** | change-quant, repair, adopt, custom, attach |
| 6 | `task-phase-6-curated-frontend.md` | **DONE** | 1 | **pass** | Settings+Home curated flow |
| 7 | `task-phase-7-advanced-frontend.md` | **DONE** | 1 | **pass** | fleet panel, lifecycle UI, three-choice |
| 8A | `task-phase-8a-release-automation.md` | **DONE** | 1 | **pass*** | *image rebuild + client endpoint scan pending |
| 8B | `task-phase-8b-live-qualification.md` | **BLOCKED** | 1 | **blocked** | no HF token; stale local images |

## Contract evidence (all product gates)

| Contract | Result |
|---|---|
| Sharded `model` path (D1) | **proven** Phase 0 |
| Preset replace/merge (D5) | **proven** Phase 2 |
| Fleet desired/applied (D4) | **proven** Phase 3 |
| Minimal runtime JSON | **proven** Phase 3 |
| Profile tool fields | **proven** Phase 3 |
| Durable operation / catalog finalization | **proven** Phase 4 |
| Repair / change quant | **proven** Phase 5 |
| No default quant (server + UI) | **proven** Phases 1A + 6 |

## Security scan ledger

| Scan | C# | Python | JS | New findings | Notes |
|---|---:|---:|---:|---:|---|
| Phase 8A baseline | 7 | 4 | 1 | **0** | SARIF in `.codeql/baseline/` |

## Migration fixtures (Phase 8A)

| Fixture | Result |
|---|---|
| Fresh database | **pass** |
| Legacy clean runtime JSON | **pass** |
| Non-model `loadParams` | **pass** (explicit issue) |
| Profile rows agree on tool policy | **pass** |
| Profile rows disagree | **pass** (explicit issue) |
| Hand-edited INI extras | **pass** (operator-managed) |
| No invented provenance | **pass** |
| Interrupted operation | **pass** (resume test) |
| Re-run migration | **pass** (idempotent) |

## Live catalog qualification (Phase 8B — all BLOCKED)

All 14 definitions: **BLOCKED** — no `HF_TOKEN` / `HUGGINGFACE_TOKEN` / `GA_LLAMA_LIVE_HF_TOKEN`.

Evidence: `docs/llama-router-preset-ui-execution/qualification/phase-8b-live-qualification-report-2026-07-11.md`

## Representative runtime qualification (Phase 8B — all BLOCKED)

All 6 representatives: **BLOCKED** — same HF token + stale `guideants-ai:rocm-latest` (missing `/admin/catalog`, manifest tree).

## Deviation log

| # | Phase | Classification | Status |
|---:|---|---|---|
| 1–2 | 0 | env tools missing | **resolved** |
| 3 | 0 | async POST /models/load | **accepted** |
| 4 | 0 | warmup timeout test | **accepted baseline** |
| 5 | 6 | knip orphans | **accepted** (improved in 7) |
| 6 | 1A/8B | no HF token | **open** — blocks live qual |
| 7 | 8A | ChatRole OpenAPI collision | **resolved** — `CustomSchemaIds` in StartupConfiguration |
| 8 | 8A/8B | stale/missing Docker images | **open** — rebuild `guideants-ai` per `FROZEN-COMMANDS.md` |

## Remaining before PR / final acceptance

1. **User commit** all uncommitted work on `feature/curated-local-llama` (agent does not commit).
2. **HF token** for live 14-repo + representative qualification (Phase 8B re-run).
3. **Rebuild guideants-ai images** (CPU/CUDA/ROCm/Vulkan) so containers include catalog manifest + fleet projection.
4. **Client endpoint coverage** — run `node scripts/find-unused-api-endpoints.mjs` after `guideants-swagger.json` export (swagger test now passes).
5. **Optional:** fix pre-existing warmup timeout test.

## Documentation added (Phase 8A)

- `docs/llama-router-preset-ui-execution/phase-8a-operator-guide.md`
- `docs/llama-router-preset-ui-execution/phase-8a-developer-testing.md`
- `docs/llama-router-preset-ui-execution/phase-8a-proposal-section9-evidence.md`

## Final acceptance

- [x] Phases 0–7 **DONE** with passing gates
- [x] Phase 8A deterministic closeout (fixtures, e2e, negatives, auth, CodeQL baseline, docs)
- [ ] Phase 8B live qualification (BLOCKED — HF token + image rebuild)
- [ ] Docker image smoke on rebuilt images
- [ ] User commit + push + PR
