# Test Gate (build + unit/integration)

Companion to [`00-orchestration.md`](./00-orchestration.md). This is the
**build + test** verification gate the orchestrator runs after **every** phase.
The CodeQL security gate is separate ([`codeql-gate.md`](./codeql-gate.md)).

The bar is simple and absolute: **green build + no new test failures vs the
pre-flight baseline.** A phase that turns a test red — even a "flaky" one — does
not pass until it is green again.

---

## 1. Baseline (Pre-flight, once)

At the starting commit on `feature/guideants-guide`, before Phase 1:

```powershell
# server
cd src/server
dotnet build GuideAntsApi.sln
dotnet test GuideAntsApi.sln

# client
cd ../client
npm ci   # if node_modules not present
npm run build
npm test -- --run
```

Record the **pass/total counts** for server (unit + integration) and client in
[`STATUS.md`](./STATUS.md) → Baseline table. Every later gate compares against
these numbers. A later run with **fewer passing** or **more failing** tests than
baseline (excluding tests the phase legitimately adds) is a **FAIL**.

> Do not edit existing tests to make them pass unless the brief authorizes a
> behavior change. Changing assertions to hide a regression is a `fallback/masking`
> deviation (orchestration §5).

---

## 2. Per-phase commands

Run from the repo root unless noted. After **every** phase run the full set; the
phase-specific tests below are *in addition*.

```powershell
# global (every phase)
cd src/server; dotnet build GuideAntsApi.sln; dotnet test GuideAntsApi.sln
cd ../client;  npm run build; npm test -- --run
```

| Phase | Adds / must cover | Targeted command |
|---|---|---|
| 1 — Data model | migration applies on fresh DB; backfill correct | `dotnet ef database drop --force …` then `dotnet ef database update …`; `dotnet test GuideAntsApi.DataModel.Tests/…` |
| 2 — Published auth | all 4 auth modes; AppIdentity valid/missing/expired; identity persisted; publish/update reject AppIdentity | `dotnet test GuideAntsApi.sln` (auth + published conversation suites) |
| 3 — Seeder | seeder idempotency (1st vs 2nd run); settings round-trip | `dotnet test GuideAntsApi.sln` (bootstrap/seeder suites) |
| 4 — System API/authz | non-admin → 404 on system project; session role-correct pub-id; workspace admin-only | `dotnet test GuideAntsApi.sln` (integration: SystemGuide) |
| 5 — Publish UI | AppIdentity → read-only panel + controls disabled | `npm test -- --run` (AuthTab/PublishGuideDialog) |
| 6 — Flyout | button visibility by role; flyout open/close; session mock → `setAuthToken` + `pub-id` | `npm test -- --run` (guideantsGuide) |
| 7 — Settings access | link admin-only; non-admin route redirect | `npm test -- --run` (Settings / route guard) |
| 8 — Tests/docs | full §10.1/§10.2 matrix; manual §10.3 recorded | full server + client suites |

---

## 3. Pass criteria (every phase)

- [ ] `dotnet build GuideAntsApi.sln` — 0 errors; warnings **not worse** than baseline.
- [ ] `dotnet test GuideAntsApi.sln` — pass count **≥ baseline + tests this phase adds**; **0** new failures.
- [ ] `npm run build` — tsc + vite, 0 errors.
- [ ] `npm test -- --run` — same bar as server tests.
- [ ] New tests for the phase's behavior **exist and pass** (a phase that adds
      behavior but no test is a `missing DoD` deviation).
- [ ] No test was weakened/deleted to pass (diff the test files).

## 4. Flaky-test handling

A genuinely flaky test (passes on isolated re-run, unrelated to the diff) is
**re-run once isolated** and, if green, recorded in `STATUS.md` deviation log as
`build/test red → flaky, re-ran`. Do **not** mark a phase DONE while any test the
phase touched is red.
