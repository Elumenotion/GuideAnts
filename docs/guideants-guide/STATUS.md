# GuideAnts Guide — Execution Status Ledger

The orchestrator updates this after every dispatch and gate. It is the audit trail
that proves the plan was executed fully and surfaces any open deviations.

State values: `BLOCKED` · `READY` · `IN_PROGRESS` · `GATE_FAILED` · `DONE`.

---

## Baseline (Pre-flight, section 1 of orchestration)

| Check | Command | Result | Date |
|---|---|---|---|
| Server build | `dotnet build GuideAntsApi.sln` (in `src/server`) | **PASS** — 0 errors, 8 warnings (pre-existing MSTEST0044 obsolete-API) | 2026-06-21 |
| Server tests | `dotnet test GuideAntsApi.sln` (in `src/server`) | **PASS w/ 1 known-env failure** — `GuideAntsApi.Tests` 1591/1591; `GuideAntsApi.IntegrationTests` 198 pass + 6 skip; `ScriptExecutionAgent.Tests` 53 pass + **1 FAIL** + 3 skip | 2026-06-21 |
| Client build | `npm run build` (in `src/client`) | **PASS** — vite built, 0 errors | 2026-06-21 |
| Client tests | `npm test -- --run` (in `src/client`) | **PASS w/ 2 known-flaky** — 2965/2967 pass; 2 timeout-flaky (`TelemetryTab`, `AddModelWizard.flow`) | 2026-06-21 |
| CodeQL baseline | `codeql-gate.md` §4.1 (local, no GitHub) — saved to `.codeql/baseline/` | **DONE** — C#=5, Python=1, JS=1 (see ledger) | 2026-06-21 |
| `dotnet ef` available | `dotnet ef --version` | **PASS** — EF Core 9.0.6 | 2026-06-21 |
| Clean tree | `git status` on `feature/guideants-guide` | **PASS** — on branch; only EOL-noise on `Notebook.cs` (empty `git diff`), plan docs (this), and bin/obj artifacts | 2026-06-21 |
| DECISIONS locked | D-GG-1 (same-host cookie auth), D-GG-2 (`/settings/system-guides`), D-GG-3 (chat-only), D-GG-4 (no default limits, editable via UI), D-GG-5 (panel + admin badge + ASR on); invariants A–J | **LOCKED** | 2026-06-21 |

**Known pre-existing baseline failures (NOT regressions; later gates exclude these):**
- Server: `ScriptExecutionAgent.Tests.Integration.ScriptExecutionAgentEndpointTests.Execute_happy_path_runs_script_when_interpreter_available` — environmental (HTTP connection to interpreter service that isn't running locally). Unrelated to GuideAnts Guide.
- Client (flaky/timeout under load, pass in isolation): `TelemetryTab > renders telemetry settings and saves advanced category edits`; `AddModelWizard flow > walks provider through review and completes sync add`. Unrelated to GuideAnts Guide.
- CodeQL: `C:\Users\dougl\tools\codeql\codeql\codeql.exe` (nested path, not the doc's `\codeql\codeql.exe`).

---

## Phase ledger

| Phase | Brief | State | Attempts | Gate result | Notes / deviations |
|---|---|---|---|---|---|
| 1 — Data model & migrations | `task-phase-1-datamodel.md` | `DONE` | 1 | **PASS** | migration `AddGuideAntsGuideSchema`; enum 0–3; backfill ApiKey>Webhook; fresh-DB apply OK; no behavior change |
| 2 — Published AppIdentity auth | `task-phase-2-published-auth.md` | `DONE` | 1 | **PASS** (build/test only) | 20 targeted tests green; cookie auth + identity persist + API guards; **CodeQL deferred to Phase 8 per user** |
| 3 — System seeder | `task-phase-3-system-seeder.md` | `DONE` | 1 | **PASS** | seeder after RequiredGuides; 3/3 seeder tests; bootstrap guides + AppIdentity internal publish |
| 4 — System API & authz | `task-phase-4-system-api-authz.md` | `DONE` | 1 | **PASS** | 16 system-guide tests; session config-only; 404 guard; listings filtered |
| 5 — Publish UI indication | `task-phase-5-publish-ui.md` | `DONE` | 1 | **PASS** (scoped) | AuthTab 3/3 confirmed; authMode read-only; full client build pending Phase 6 flyout fix |
| 6 — Guide flyout | `task-phase-6-guide-flyout.md` | `DONE` | 1 | **PASS** | 8/8 flyout tests; 10 call sites; no setAuthToken/token storage; build green |
| 7 — Settings access | `task-phase-7-settings-access.md` | `DONE` | 1 | **PASS** | SystemGuidesAccess 3/3; /settings/system-guides + admin link; client build green |
| 8 — Tests, docs, acceptance | `task-phase-8-tests-docs.md` | `DONE` | 2 | **PASS** (CodeQL skipped) | subagent stuck after reading docs; orchestrator finished: fixed SystemGuideEndpointsTests cleanup, 14/14 integration + matrix covered; docs §7 present |

---

## CodeQL findings ledger (local, no GitHub parity)

Baseline counts and per-gate **new-finding** diffs (`codeql-gate.md`). Target: every
"new vs baseline" cell is **0**.

| Scan point | C# | Python | JS | New vs baseline | Notes |
|---|---|---|---|---|---|
| Baseline (pre-flight) | 5 | 1 | 1 | — | C#: cs/web/missing-x-frame-options ×4, cs/user-controlled-bypass ×1 · Py: py/clear-text-logging-sensitive-data ×1 · JS: js/missing-rate-limiting ×1. None are feature-sensitive (log-forging/clear-text-storage/hardcoded-cred/path-injection). Saved to `.codeql/baseline/` |
| After Phase 2 | | | | | published AppIdentity cookie validation |
| After Phase 4 | | | | | system endpoints (session = config only) |
| After Phase 6 | | | | | flyout cookie auth, no token storage (JS focus) |
| Final acceptance | — | — | — | **skipped per user** | baseline unchanged; run `codeql-gate.md` manually if needed |

---

## Deviation log

Record every gate failure, scope-creep revert, and decision change here.

| # | Phase | Attempt | Classification | What failed | Action taken | Re-gate result |
|---|---|---|---|---|---|---|
| 2 | 8 | 1 | stuck subagent | Phase 8 subagent hung after reading task docs (likely CodeQL) | Orchestrator took over; fixed integration test cleanup | **PASS** |
| 3 | 8 | — | user decision | CodeQL slow / blocks progress | **Skipped final CodeQL scan** — baseline at `.codeql/baseline/` unchanged; run manually when needed | n/a |

Classifications (orchestration §5): `build/test red` · `missing DoD` ·
`scope creep` · `decision drift` · `fallback/masking`.

---

## Final acceptance (orchestration §6)

- [x] All §1 success criteria in the master plan satisfiable (code + tests mapped).
- [x] Every §10.1/§10.2 test row covered by automated tests (see matrix below); §10.3 manual — **pending live run** (requires running app + LLM).
- [x] Seeder idempotent; one system project + 2 AppIdentity published guides (`GuideAntsSystemSeederTests`).
- [ ] Contributor + Admin flyout chat works with role-correct guide — **manual §10.3 steps 1–2** (automated: session role mapping + flyout mount tests).
- [x] System project hidden + 404 for non-admins (integration + guard unit tests).
- [x] `AppIdentity` not settable from any UI/API path (publish/update 400 + AuthTab read-only).
- [x] No token minted or stored client-side; flyout auth is the same-host cookie (flyout tests assert no `setAuthToken`).
- [x] Global invariants (4.1) green on final tree (build + targeted suites; known baseline exclusions unchanged).
- [ ] Final CodeQL diff clean — **skipped per user** (baseline captured pre-flight).
- [x] No open deviations blocking ship (CodeQL deferred is recorded).

### §10.3 manual acceptance (live — not run in CI)

| Step | Status | Notes |
|---|---|---|
| 1 Contributor flyout chat | **PENDING** | Needs live login + streamed reply |
| 2 Admin flyout admin guide | **PENDING** | Check admin badge + session guide name |
| 3 Contributor no system project in listings | **PASS (automated)** | `GetProjects_excludes_system_project` |
| 4 Contributor blocked on system project URL | **PASS (automated)** | Reader → 404 on `GET /api/projects/{systemId}` |
| 5 Admin Auth tab AppIdentity read-only | **PASS (automated)** | `AuthTab.test.tsx` |
| 6 Admin edit guide instructions | **PENDING** | Needs live workspace |
| 7 Optional AppEcho | **PENDING** | Manual prompt per D-GG-3 |

### §10.1 / §10.2 automated coverage map

| Row | Covered by |
|---|---|
| AppIdentity valid JWT | `PublishedGuideAuthServiceTests`, `PublishedGuidesAppIdentityEndpointsTests` |
| AppIdentity missing/expired → 401 | same |
| Publish/update reject AppIdentity | integration publish/update tests |
| Message UserId + ExternalUserIdentity | `PublishedGuidesAppIdentityEndpointsTests` |
| GET published authMode/requiresAuth | integration |
| Auth tab read-only | `AuthTab.test.tsx` |
| Seeder first/second run + repair | `GuideAntsSystemSeederTests` |
| Projects exclude system / 404 reader / 200 admin | `SystemGuideEndpointsTests`, guard unit tests |
| Session admin vs contributor / Pending 403 | `SystemGuideEndpointsTests`, `SystemGuideSessionServiceTests` |
| Guide button visible/hidden | `GuideAntsGuideButton.test.tsx` |
| Flyout pub-id, no setAuthToken | `GuideAntsGuideFlyout.test.tsx` |
| Settings link admin-only + route redirect | `SystemGuidesAccess.test.tsx` |
