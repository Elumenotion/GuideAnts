# Curated Local Llama — Execution Status Ledger

The orchestrator updates this after every dispatch and independent gate.

States: `BLOCKED` · `READY` · `IN_PROGRESS` · `GATE_FAILED` · `DONE`.

## Pre-flight baseline

**Worktree identity:** branch `fix/windows-rocm-wsl-detection` @ `fc293bf` (dirty).

### Worktree ownership inventory (2026-07-10)

| Path(s) | Classification | Notes |
|---|---|---|
| `docs/llama-router-preset-ui-execution/*`, `docs/llama-router-preset-ui-proposal.md` | Execution plan artifacts | Orchestrator-owned; not product code |
| `docker/build/guideants-ai/lib/guideants_hf/*` | Accepted starting work — **Phase 1A/2** | New HF catalog/download lib; overlaps 1A+2 if both touch llama-admin |
| `docker/build/guideants-ai/llama-admin-service/llama_admin_service.py` | Accepted starting work — **Phase 1A/2** | Same overlap risk as above |
| `docker/build/guideants-ai/{asr,emb,sd,tts}-service/*`, `start-{asr,tts}.sh` | Unrelated parallel work | HF lib integration in non-llama services |
| `docker/build/guideants-ai/Dockerfile.*` | Unrelated parallel work | Image build changes |
| `docker/docker-compose*.yml`, `docker/.env`, `docker/guideants-ai-build.md` | Unrelated parallel work | ROCm/compose; `.env` is secrets — never commit |
| `installer/**` (compose, scripts, README, `.env`) | Unrelated parallel work | ROCm/WSL installer lane on current branch |
| `installer/scripts/rocm-probe.{ps1,sh}` | Unrelated parallel work | New probe scripts |
| `scripts/setup-dev-environment.ps1` | Unrelated parallel work | Dev setup |
| `start_{linux,macos,windows}.*` | Unrelated parallel work | Startup script changes |
| `src/server/GuideAntsApi/**` (LlamaCpp, Bootstrap) | Accepted starting work — **Phase 3** | Runtime/fleet prep; must not advance before Phase 0 gate |
| `src/server/GuideAntsApi.Tests/**` (LlamaCpp, Bootstrap) | Accepted starting work — **Phase 3** | Matching test updates |
| `src/client/src/pages/settings/**`, `addAiServicesWizard/utils.ts` | Ambiguous — **Phase 6 or unrelated** | Settings/connections UI; classify in Phase 0 report |

**Environment blockers (orchestrator pre-check):** `python`/`py` not on PATH; `dotnet ef` not installed; `bash` broken (WSL relay error); `codeql` not on PATH. Docker 29.6.1 available; dotnet 8.0.422; node v24.18.0; npm 11.16.0.

| Check | Command/evidence | Baseline result | Date |
|---|---|---|---|
| Worktree ownership | modified/untracked file inventory and phase assignment | **captured** (table above) | 2026-07-10 |
| Server build | `dotnet build GuideAntsApi.sln` in `src/server` | **not run** | — |
| Server tests | `dotnet test GuideAntsApi.sln` in `src/server` | **not run** | — |
| Client build | `npm run build` in `src/client` | **not run** | — |
| Client tests | `npm test -- --run` in `src/client` | **not run** | — |
| Client orphan scan | `npm run find-orphans` in `src/client` | **not run** | — |
| Python compile/tests | commands from Phase 0 brief | **not run** | — |
| Shell syntax | `bash -n` on modified runtime scripts | **not run** | — |
| Docker images | CPU/CUDA/ROCm/Vulkan build or existing project smoke command | **not run** | — |
| EF migrations | `dotnet ef migrations list ...` | **not run** | — |
| `dotnet ef` tool | `dotnet ef --version` | **not run** | — |
| CodeQL baseline | `codeql-gate.md` | **not captured** | — |
| Sharded preset proof | bundled llama.cpp + two-shard fixture | **not proven** | — |
| Fleet projection proof | desired/applied projection + respawn fixture | **not proven** | — |
| Frozen contract fixtures | C#/Python/TypeScript parse checks | **not captured** | — |

## Phase ledger

| Phase | Brief | State | Attempts | Gate | Notes |
|---|---|---:|---:|---|---|
| 0 — Contracts/baseline | `task-phase-0-contracts-baseline.md` | **IN_PROGRESS** | 1 | — | Dispatched; env gaps noted above |
| 1A — Catalog/discovery | `task-phase-1a-catalog-discovery.md` | **BLOCKED** | 0 | — | Needs Phase 0 |
| 1B — Persistence/profiles | `task-phase-1b-persistence-profiles.md` | **BLOCKED** | 0 | — | Needs Phase 0; may parallel 1A |
| 2 — Router/download | `task-phase-2-router-download.md` | **BLOCKED** | 0 | — | Needs 1A contract |
| 3 — Runtime/fleet/migration | `task-phase-3-runtime-fleet-migration.md` | **BLOCKED** | 0 | — | Needs 1B + 2 |
| 4 — Curated install | `task-phase-4-curated-install.md` | **BLOCKED** | 0 | — | Needs 1A + 1B + 2 + 3 |
| 5 — Lifecycle API | `task-phase-5-lifecycle-api.md` | **BLOCKED** | 0 | — | Needs 4; may parallel 6 |
| 6 — Curated frontend | `task-phase-6-curated-frontend.md` | **BLOCKED** | 0 | — | Needs 4; may parallel 5 |
| 7 — Advanced frontend | `task-phase-7-advanced-frontend.md` | **BLOCKED** | 0 | — | Needs 5 + 6 |
| 8A — Release automation | `task-phase-8a-release-automation.md` | **BLOCKED** | 0 | — | Needs 7; may parallel 8B |
| 8B — Live qualification | `task-phase-8b-live-qualification.md` | **BLOCKED** | 0 | — | Needs 7; may parallel 8A |

## Contract evidence

| Contract | Expected | Evidence/result |
|---|---|---|
| Sharded `model` path | first ordered shard, all shards co-located | **pending Phase 0** |
| Preset replace | prior extras removed, resolved preset written atomically | **pending Phase 2** |
| Preset merge | only supplied extras changed | **pending Phase 2** |
| Fleet desired/applied | revision mismatch visible until confirmed restart | **pending Phase 3** |
| Minimal runtime JSON | router/profile only | **pending Phase 3** |
| Profile tool fields | applied only when tools exist | **pending Phase 3** |
| Durable operation | survives API and llama-admin restart | **pending Phase 4** |
| Catalog finalization | after artifacts + INI only | **pending Phase 4** |
| Repair | recorded commit and artifact set | **pending Phase 5** |
| Change quant | staged activation; obsolete files removed last | **pending Phase 5** |
| No default quant | server and both UIs | **pending Phase 6** |

## Security scan ledger

Target for every scan: zero new findings versus Phase 0.

| Scan point | C# | Python | JavaScript | New findings | Notes |
|---|---:|---:|---:|---:|---|
| Phase 0 baseline | — | — | — | — | not captured |
| Phase 1A | — | — | — | — | catalog/HF inputs |
| Phase 1B | — | — | — | — | persisted JSON/data contracts |
| Phase 2 | — | — | — | — | filesystem/download/INI |
| Phase 3 | — | — | — | — | settings/process projection |
| Phase 4 | — | — | — | — | durable orchestration |
| Phase 5 | — | — | — | — | destructive lifecycle actions |
| Phase 6 | — | — | — | — | curated repository/operation UI |
| Phase 7 | — | — | — | — | advanced operator input |
| Final | — | — | — | — | full tree |

## Migration fixtures

| Fixture | Expected classification | Result |
|---|---|---|
| Fresh database | no legacy issues; 14 definitions available | **pending** |
| Legacy clean runtime JSON | deterministic minimal JSON + INI keys | **pending** |
| Non-model `loadParams` | explicit unresolved issue | **pending** |
| Profile rows agree on tool policy | profile policy migrated once | **pending** |
| Profile rows disagree | explicit unresolved issue; behavior unchanged | **pending** |
| Existing hand-edited INI extras | preserved; operator-managed | **pending** |
| Existing alias without provenance | no invented source/quant/revision | **pending** |
| Interrupted install | durable partial state + remediation | **pending** |
| Re-run migration | no duplicate writes or issues | **pending** |

## Live catalog qualification

Record resolved commit, discovered quant labels, shard groups, projector result, and
date for all 14 definitions.

| Definition | Repository check | Commit | Quant/shard check | Projector | Result |
|---|---|---|---|---|---|
| `qwen3.6-35b-a3b` | — | — | — | — | **pending** |
| `qwen3.6-27b` | — | — | — | — | **pending** |
| `qwen3.6-35b-a3b-mtp` | — | — | — | — | **pending** |
| `qwen3.6-27b-mtp` | — | — | — | — | **pending** |
| `qwen3.5-35b-a3b` | — | — | — | — | **pending** |
| `qwen3.5-27b` | — | — | — | — | **pending** |
| `qwen3.5-9b` | — | — | — | — | **pending** |
| `gemma4-31b` | — | — | — | — | **pending** |
| `gemma4-26b-a4b` | — | — | — | — | **pending** |
| `gemma4-12b` | — | — | — | — | **pending** |
| `gemma4-e4b` | — | — | — | — | **pending** |
| `gpt-oss-20b` | — | — | — | — | **pending** |
| `deepseek-r1-14b` | — | — | — | — | **pending** |
| `qwen3-coder-30b` | — | — | — | — | **pending** |

## Representative runtime qualification

| Definition | Backend/image | Quant/commit | Required capabilities | Result/evidence |
|---|---|---|---|---|
| `qwen3.6-35b-a3b` | — | — | install, vision, reasoning, tools, restart, repair, quant change | **pending** |
| `qwen3.6-35b-a3b-mtp` | — | — | install, text, reasoning, tools, MTP, restart | **pending** |
| `gemma4-31b` | — | — | install, vision, reasoning, tools | **pending** |
| `deepseek-r1-14b` | — | — | install, reasoning, single tool policy | **pending** |
| `qwen3-coder-30b` | — | — | install, coding, parallel tools | **pending** |
| `gpt-oss-20b` | — | — | install, reasoning, tools | **pending** |

## Deviation log

| # | Phase | Attempt | Classification | Evidence/failure | Action | Re-gate |
|---:|---|---:|---|---|---|---|
| — | — | — | — | No deviations recorded | — | — |

## Final acceptance

- [ ] Every proposal §9 criterion has linked evidence.
- [ ] All phases are `DONE`.
- [ ] No open deviation or migration issue.
- [ ] Final build/test/schema/image/security gates pass.
- [ ] Live 14-repository qualification passes.
- [ ] Representative runtime qualification passes on every claimed release lane.
- [ ] Documentation and generated API contracts match the final implementation.
