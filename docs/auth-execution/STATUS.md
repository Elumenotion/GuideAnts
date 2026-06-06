# Auth System — Execution Status Ledger

The orchestrator updates this after every dispatch and gate. It is the audit trail
that proves the plan was executed fully and surfaces any open deviations.

State values: `BLOCKED` · `READY` · `IN_PROGRESS` · `GATE_FAILED` · `DONE`.

---

## Baseline (Pre-flight, section 1 of orchestration)

| Check | Command | Result | Date |
|---|---|---|---|
| Server build | `dotnet build GuideAntsApi.sln` (in `src/server`) | **pass** | 2026-06-05 |
| Server tests | `dotnet test GuideAntsApi.sln` (in `src/server`) | **pass** (`602/602`: 511 unit + 91 integration) | 2026-06-05 |
| Client build | `npm run build` (in `src/client`) | **pass** | 2026-06-05 |
| Client tests | `npm test -- --run` (in `src/client`) | **pass** (`903/903`) | 2026-06-05 |
| CodeQL baseline | `codeql-gate.md` §4.1 (local, no GitHub) — save `.codeql/baseline/` | **captured** (`C#=7, Python=0, JS=3`) | 2026-06-05 |
| `dotnet ef` available | `dotnet ef --version` | **pass** (`9.0.12`) | 2026-06-05 |
| Clean tree | `git status` | **not clean (expected pre-existing docs/plan artifacts)** | 2026-06-05 |
| DECISIONS finalized | D1=**JWT** ✅, D2=**UserRoles table** ✅, D3 Appendix-A questions ✅ (delete=Admin, usage=Admin, speech=Contributor, llama load=Contributor / unload+restart=Admin, ext-auth cfg=Admin, name-avatars=Public, header-toolbar=Admin + new chat-readiness=ApprovedUser split), D4(a)=**state table** ✅, D4(b)=**token per (User,Provider)** ✅ | **complete** | 2026-06-05 |

---

## Phase ledger

| Phase | Brief | State | Attempts | Gate result | Notes / deviations |
|---|---|---|---|---|---|
| 0 — Cleanup | `task-phase-0-cleanup.md` | **DONE** | 1 | **PASS** | Removed dead auth scaffolding; tests unchanged (`602/602` server, `903/903` client). |
| 1 — Data model | `task-phase-1-datamodel.md` | **DONE** | 1 | **PASS** | Added `UserRoles`, auth fields, and seed-user delete migration; fresh DB users count `0`. |
| 2 — Backend auth | `task-phase-2-backend-auth.md` | **DONE** | 1 | **PASS** | JWT auth pipeline + register/login/me + first-user race guard; CodeQL new-vs-baseline `0`. |
| 3 — Authorization | `task-phase-3-authorization.md` | **DONE** | 1 | **PASS** | Applied policy guards across endpoint surface + toolbar read split; CodeQL new-vs-baseline `0`. |
| 4 — Admin users | `task-phase-4-admin-users.md` | **DONE** | 1 | **PASS** | Added `/api/admin/users` endpoints, last-admin safeguards, password reset revocation; CodeQL `0` new. |
| 4.5 — Tool OAuth | `task-phase-4.5-tool-oauth.md` | **DONE** | 1 | **PASS** | Moved OAuth tokens server-side encrypted; removed client token storage/transmit paths; CodeQL `0` new. |
| 5 — Frontend | `task-phase-5-frontend.md` | **DONE** | 2 | **PASS** | Attempt 1 interrupted; attempt 2 completed auth pages/context/guards/users tab. Fixed pending redirect loop and restored orphan delta to baseline. |
| 6 — OpenAPI/tests/docs | `task-phase-6-openapi-tests-docs.md` | **DONE** | 1 | **PASS** | Added Swagger bearer security, role-matrix integration coverage, JWT config placeholders, and auth-flow docs. |

---

## CodeQL findings ledger (local, no GitHub parity)

Baseline counts and per-gate **new-finding** diffs (`codeql-gate.md`). Target: every
"new vs baseline" cell is **0**.

| Scan point | C# | Python | JS | New vs baseline | Notes |
|---|---|---|---|---|---|
| Baseline (pre-flight) | 7 | 0 | 3 | — | baseline SARIFs saved to `.codeql/baseline/` |
| After Phase 2 | 7 | 0 | 3 | **0** | auth pipeline + JWT issuance checks |
| After Phase 3 | 7 | 0 | 3 | **0** | endpoint wiring + policy sweep |
| After Phase 4 | 7 | 0 | 3 | **0** | admin set-password + lockout safeguards |
| After Phase 4.5 | 7 | 0 | 2 | **0** | JS clear-text storage finding dropped after client token removal |
| After Phase 5 | 7 | 0 | 2 | **0** | frontend auth plumbing + storage/logging checks |
| Final acceptance | 7 | 0 | 2 | **0** | final close-out scan; no new findings |

---

## Deviation log

Record every gate failure, scope-creep revert, and decision change here.

| # | Phase | Attempt | Classification | What failed | Action taken | Re-gate result |
|---|---|---|---|---|---|---|
| 1 | 1 | 1 | build/test red | One client test failed flakily during first gate run (`ChatModelConfigurator`) | Re-ran isolated failing test + full client suite | Pass |
| 2 | 5 | 1 | missing DoD | Subagent execution interrupted before report/verification | Re-dispatched Phase 5 and completed full report + gates | Pass |
| 3 | 5 | 2 | missing DoD | `find-orphans` increased from baseline by one new unused file (`src/client/src/services/permissions.ts`) | Removed obsolete unused file and re-ran client gates | Pass |
| 4 | 3/4/5 gate runs | 1 | build/test red | Intermittent file-lock build failures from concurrent process access | Re-ran build/tests sequentially | Pass |

Classifications (orchestration §5): `build/test red` · `missing DoD` ·
`scope creep` · `decision drift` · `fallback/masking`.

---

## Final acceptance (orchestration §6)

- [x] All Phase 0–6 checkboxes in `../auth-system-plan.md` §4 satisfiable.
- [x] Every Appendix A endpoint has its stated guard (grep + role-matrix test).
- [x] Fresh-install bootstrap proven (0 users → Admin → Pending → approve).
- [x] No `localStorage` tool-OAuth tokens remain.
- [x] Global invariants green on final tree.
- [x] No open deviations above.
