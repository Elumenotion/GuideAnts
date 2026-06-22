# Host Folder Notebook Mounts — Execution Status Ledger

The orchestrator updates this after every dispatch and gate. It is the audit trail
that proves the plan was executed fully and surfaces any open deviations.

State values: `BLOCKED` · `READY` · `IN_PROGRESS` · `GATE_FAILED` · `DONE`.

---

## Baseline (Pre-flight, section 1 of orchestration)

| Check | Command | Result | Date |
|---|---|---|---|
| Server build | `dotnet build GuideAntsApi.sln` (in `src/server`) | **PASS** (0 errors, 0 warnings) | 2026-06-17 |
| Server tests | `dotnet test GuideAntsApi.sln` (in `src/server`) | **PASS** (1686: 31+1467+188, 0 failed) | 2026-06-17 |
| Client build | `npm run build` (in `src/client`) | **PASS** | 2026-06-17 |
| Client tests | `npm test -- --run` (in `src/client`) | **PASS** (2898 passed, 262 files) | 2026-06-17 |
| Docker baseline | `docker compose -f docker/docker-compose.ghcr-cpu.yml config` (no override) | **PASS** | 2026-06-17 |
| CodeQL baseline | `codeql-gate.md` §Baseline → `.codeql/baseline/` | **PASS** (C#=5, Python=2, JS=5; saved to `.codeql/baseline/`) | 2026-06-17 |
| `dotnet ef` available | `dotnet ef --version` | **PASS** (9.0.12) | 2026-06-17 |
| Symlink spike still valid | plan §3.1 on target stack | **ACCEPTED** per plan §3.1 doc; live re-verify at Phase 6 gate | 2026-06-17 |
| Clean tree | `git status` on `feature/add-project-folders` | **OK** (untracked docs; no host-mounts code yet) | 2026-06-17 |
| DECISIONS resolved | D1 indexing, D2 services, D3 command-source (D4/D5 deferred) | **LOCKED** | 2026-06-17 |

---

## Phase ledger

| Phase | Brief | State | Attempts | Gate result | Notes / deviations |
|---|---|---|---|---|---|
| 1 — Runtime config + compose include | `task-phase-1-runtime-config-compose.md` | DONE | 1 | **PASS** | 2026-06-17 |
| 2 — Host helper scripts | `task-phase-2-host-helper-scripts.md` | DONE | 1 | **PASS** | api-plan w/ explicit -HostPath when API unavailable (pre-P5) |
| 3 — Data model | `task-phase-3-data-model.md` | DONE | 1 | **PASS** | NotebookId FK Restrict (SQL Server cascade paths) |
| 4 — Mount service core | `task-phase-4-mount-service-core.md` | DONE | 1 | **PASS** | 31 unit tests |
| 5 — API endpoints | `task-phase-5-api-endpoints.md` | DONE | 1 | **PASS** | admin endpoints + internal compose-plan |
| 6 — Symlink + registry | `task-phase-6-symlink-registry.md` | DONE | 1 | **PASS** | symlink + mounts.json |
| 7 — Script-agent guard | `task-phase-7-script-agent-guard.md` | DONE | 1 | **PASS** | registered-links-only; neg unregistered case |
| 8 — Notebook sync | `task-phase-8-notebook-sync.md` | DONE | 1 | **PASS** | no recursive reparse descent |
| 9 — New-notebook + reconcile | `task-phase-9-new-notebook-reconcile.md` | DONE | 1 | **PASS** | project-scope auto-link |
| 10 — Folder tree UI | `task-phase-10-folder-tree-ui.md` | DONE | 1 | **PASS** | admin menus + display states |
| 11 — Remove flow + reconcile | `task-phase-11-remove-flow.md` | DONE | 1 | **PASS** | unlink-before-command; stale reconcile |
| 12 — Tests + OpenAPI + docs | `task-phase-12-tests-openapi-docs.md` | DONE | 1 | **PASS** | final acceptance 2026-06-17 |

---

## Docker gate ledger

`docker-gate.md`. Run after Phases 1, 2, 11, 12 and any `docker/` / `start_*` /
override-generator change.

| Scan point | `compose config` resolves (no override) | `compose config` resolves (with override) | Restart `--no-deps` scoped | SMB creds not inlined | Notes |
|---|---|---|---|---|---|
| Baseline | **PASS** | n/a | n/a | n/a | ghcr-cpu |
| After Phase 1 | **PASS** | **PASS** | n/a | n/a | example override |
| After Phase 2 | **PASS** | **PASS** | **PASS** (script uses `--no-deps`) | n/a | local bind only |
| After Phase 11 | **PASS** | **PASS** | **PASS** | n/a | remove flow |
| Final acceptance | **PASS** | **PASS** | **PASS** | n/a | `up` smoke + health 200 @ :5107 |

---

## CodeQL findings ledger (local, no GitHub parity)

`codeql-gate.md`. Target: every "new vs baseline" cell is **0**.

| Scan point | C# | Python | JS | New vs baseline | Notes |
|---|---|---|---|---|---|
| Baseline (pre-flight) | 5 | 2 | 5 | — | `.codeql/baseline/` |
| After Phase 5 | 5 | 2 | 5 | **0** | endpoint surface |
| After Phase 6 | 5 | 2 | 5 | **0** | symlink + registry |
| After Phase 7 | 5 | 2 | 5 | **0** | path-guard rework |
| Final acceptance | 5 | 2 | 5 | **0** | close-out scan 2026-06-17 |

---

## Deviation log

Record every gate failure, scope-creep revert, and decision change here.

| # | Phase | Attempt | Classification | What failed | Action taken | Re-gate result |
|---|---|---|---|---|---|---|
| 1 | 12 | 1 | `build/test red` | IntegrationTests MSBuild/CodeQL tripped on `docker/volumes` runtime symlinks | Excluded `docker/**` from default item globs in csproj; removed runtime volume tree before CodeQL | **PASS** |

Classifications (orchestration §5): `build/test red` · `docker gate red` ·
`missing DoD` · `scope creep` · `decision drift` · `fallback/masking` ·
`security regression`.

---

## Final acceptance (orchestration §6)

- [x] Plan §4–§19 satisfiable by commit/file/test.
- [x] End-to-end local-folder flow proven (map → command → restart → reconcile →
      `{notebookRoot}/{leafName}` → read/write → remove without host-content loss).
- [x] Script execution works under a mapped folder; unregistered symlinks rejected.
- [x] No recursive indexing by default; both sync paths consistent.
- [x] Global invariants green; docker gate green; final CodeQL diff clean.
- [x] Docs merged; SMB carried into a Deferred follow-on section.
- [x] No open deviations above.
