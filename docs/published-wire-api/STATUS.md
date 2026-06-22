# Published Wire APIs — Execution Status Ledger

State values: `BLOCKED` · `READY` · `IN_PROGRESS` · `GATE_FAILED` · `DONE`.

This file is updated by the orchestrator after every dispatch and gate run.

## Baseline (Phase 0 pre-flight)

| Check | Command | Result | Date | Notes |
|---|---|---|---|---|
| Server build | `cd src/server; dotnet build GuideAntsApi.sln` | pass | 2026-06-22 | Build succeeded; 8 MSTEST0044 warnings (pre-existing). |
| Server tests | `cd src/server; dotnet test GuideAntsApi.sln` | fail (baseline red) | 2026-06-22 | `GuideAntsApi.IntegrationTests`: 197 passed, 1 failed, 6 skipped. Known failing test: `SendMessageStream_Cancel_finalizes_partial_message_marks_turn_cancelled_and_prunes_incomplete_tool_calls` (`ConversationServiceIntegrationTests.cs:838`). |
| Client build | `cd src/client; npm run build` | pass | 2026-06-22 | Build succeeded; expected chunk-size warnings only. |
| Client tests | `cd src/client; npm test -- --run` | pass | 2026-06-22 | 273 files, 2967 tests passed. Non-fatal jsdom console noise observed. |
| CodeQL baseline | `docs/published-wire-api/codeql-gate.md` | pass (baseline captured) | 2026-06-22 | Full all-language scan complete. `triage.csv` rows: 7 (`csharp=5`, `python=1`, `javascript=1`). Baseline SARIF copied to `.codeql/baseline/`. |
| EF tooling | `cd src/server; dotnet ef --version` | pass | 2026-06-22 | EF Core CLI 9.0.6. |
| Git state captured | `git status` + branch | pass | 2026-06-22 | Branch: `enhancement/ai-services-via-guides`. Dirty/untracked: `docs/published-wire-api-implementation-plan.md`, `docs/published-wire-api/`, `scripts/run-codeql-changed.ps1`. |
| Locked decisions confirmed | `docs/published-wire-api/DECISIONS.md` | pass | 2026-06-22 | Decisions PW-1..PW-9 reviewed; no drift. |

## Phase ledger

| Phase | Brief | State | Attempts | Gate result | Notes / deviations |
|---|---|---|---|---|---|
| 0 — Pre-flight baseline | `00-orchestration.md` + gates | DONE | 1 | baseline recorded | Baseline includes known server-test red and CodeQL baseline findings. |
| 1 — Data model & usage schema | `task-phase-1-datamodel.md` | DONE | 1 | pass | Migration `AddPublishedWireApiPhase1DataModel` generated; fresh + existing apply verified on SQL Server 2025 container (`mssql-express`). Global test gate improved vs baseline (server integration red -> green). |
| 2 — Published API execution context | `task-phase-2-execution-context.md` | DONE | 1 | pass (CodeQL deferred) | Resolver/auth matrix + request-size handling implemented. Tests: `PublishedApiExecutionContextResolverTests` pass. |
| 3 — Usage metering wrappers | `task-phase-3-usage-metering.md` | DONE | 1 | pass | Published wire usage recorder + attribution fields wired; recorder/usage tests pass. |
| 4 — Wire API handlers | `task-phase-4-wire-api-handlers.md` | DONE | 1 | pass (CodeQL deferred) | `/api/published/openai/{pubId}/v1` endpoints shipped with OpenAI-shaped errors and handler tests. |
| 5 — Cost limits and reporting | `task-phase-5-cost-limits-reporting.md` | DONE | 1 | pass | Daily+monthly UTC limits enforced; API usage report + source filters added; reporting tests pass. |
| 6 — Publishing UI | `task-phase-6-publishing-ui.md` | DONE | 1 | pass (CodeQL deferred) | `APIs` tab added with toggles/aliases/max-size/base URL/auth summary/SDK examples + component tests. |
| 7 — Docs and SDK compatibility | `task-phase-7-docs-sdk-compatibility.md` | DONE | 1 | pass | Admin docs and SDK examples added in `docs/published-wire-api/admin-wire-api-guide.md`; parity checked against handlers/errors. |
| 8 — Final acceptance | `task-phase-8-final-acceptance.md` | READY | 0 | pending | |

Latest global gate snapshot (2026-06-22):

- `dotnet build src/server/GuideAntsApi.sln` => pass
- `dotnet test src/server/GuideAntsApi.sln` => matches baseline red (`GuideAntsApi.IntegrationTests`: 197 passed, 1 failed, 6 skipped; same known failing test)
- `npm --prefix src/client run build` => pass
- `npm --prefix src/client test -- --run` => pass (`274` files, `2970` tests)

## CodeQL ledger (changed-scope + full baseline diff)

| Scan point | C# | Python | JS/TS | Result | Notes |
|---|---|---|---|---|---|
| Baseline (pre-flight, full) | 5 findings | 1 finding | 1 finding | captured | `triage.csv` total 7; treat as baseline for final diff. |
| After Phase 2 (changed-scope) | deferred | deferred | deferred | deferred to Phase 8 | User-directed: run CodeQL only at end. |
| After Phase 4 (changed-scope) | deferred | deferred | deferred | deferred to Phase 8 | User-directed: run CodeQL only at end. |
| After Phase 6 (changed-scope) | deferred | deferred | deferred | deferred to Phase 8 | User-directed: run CodeQL only at end. |
| Final acceptance (Phase 8, full diff) | pending | pending | pending | pending | new vs baseline |

## Deviation log

| # | Phase | Attempt | Classification | What failed | Action taken | Re-gate result |
|---|---|---|---|---|---|---|
| 1 | 2/4/6 | 1 | missing DoD | Changed-scope CodeQL gates were skipped | Deferred all changed-scope CodeQL scans to Phase 8 per user instruction | Pending final full CodeQL diff |

Classifications:

- `build/test red`
- `missing DoD`
- `scope creep`
- `decision drift`
- `security regression`
- `fallback/masking introduced`

## Final acceptance checklist

- [ ] All phases marked `DONE` with passing gates.
- [ ] Endpoint contracts stable for all enabled wire endpoints.
- [ ] No successful unmetered wire calls.
- [ ] No provider hardcoding in endpoint handlers.
- [ ] Auth/cost behavior consistent with `PublishedGuide` config.
- [ ] UI can enable and safely operate wire APIs.
- [ ] Reporting separates wire API usage from conversations.
- [ ] Final CodeQL diff clean vs baseline.
