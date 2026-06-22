# Published Wire APIs — Test Gate

Companion to [`00-orchestration.md`](./00-orchestration.md).

This gate runs after every phase. It defines the baseline and "no regression"
standard for the execution run.

## 1. Gate commands

Run in this order:

```powershell
cd src/server
dotnet build GuideAntsApi.sln
dotnet test GuideAntsApi.sln

cd ../client
npm run build
npm test -- --run
```

## 2. Baseline capture (Phase 0)

Before Phase 1:

- Run full gate once.
- Record pass/fail and test counts in `STATUS.md`.
- Record known flakes/failures with clear classification.

Baseline does not require perfect green; it requires explicit accounting.

## 3. Pass criteria (all phases)

- No new failures versus baseline.
- No test weakening/removal to hide failures.
- No new secrets introduced.
- No swallowed `401`, `403`, `404`, or usage-write errors.
- No fallback behavior that masks auth/cost/metering failures.

## 4. Phase-specific additions

- CodeQL cadence:
  - Phases 2/4/6 run changed-scope CodeQL (`scripts/run-codeql-changed.ps1`).
  - Phase 8 runs full all-language CodeQL diff vs baseline.
- Phase 1:
  - EF migration generated and applies on fresh + existing DB.
  - DTOs and usage schema compile across server/client.
- Phase 2:
  - Auth-mode tests for anonymous/API-key/webhook/app identity pass.
  - Cost-limit denial returns stable OpenAI-shaped error.
- Phase 3:
  - Usage attribution tests prove project/notebook/published/source/request
    fields are populated.
- Phase 4:
  - Endpoint contract snapshots for all supported endpoints pass.
  - Provider-routing tests prove configured route selection.
- Phase 5:
  - Reporting tests include conversation and non-conversation usage.
  - Daily/monthly limit exceedance tests pass.
- Phase 6:
  - UI component tests cover toggles, DTO round-trip, readiness, and examples.
- Phase 7:
  - Docs/example smoke checks pass (route/error-name parity).
- Phase 8:
  - Full regression rerun + manual acceptance matrix complete.

## 5. Report-back addendum

Each phase report should include:

```text
TEST GATE
- server-build: <pass/fail>
- server-tests: <count pass/fail + known failures>
- client-build: <pass/fail>
- client-tests: <count pass/fail + known failures>
- regression-vs-baseline: <none/list>
```
