# Task — Phase 6: Publishing UI

> Subagent brief. Execute top to bottom and return the Report-back contract
> verbatim.

## Mission

Add admin UI controls to safely enable and operate published wire APIs.

## Read first

- `../published-wire-api-implementation-plan.md` (Phase 6 section)
- `./DECISIONS.md`
- `./test-gate.md`
- `./codeql-gate.md`
- Existing publish dialog components and DTO contracts.

## Preconditions

- Phase 5 gate is green.

## Guardrails (hard)

- Do not introduce a new auth mode.
- Do not expose provider secrets/internal service credentials.
- Reuse existing publish dialog/tab styling and interaction patterns.
- Keep one-time API key display behavior.

## Tasks

1. Add `APIs` tab to `PublishGuideDialog`.
2. Add controls for:
   - enable OpenAI-compatible APIs
   - endpoint toggles
   - model alias fields
   - max request size fields
   - base URL copy
   - auth header summary
   - curl/OpenAI SDK examples
3. Add readiness states:
   - enabled
   - disabled
   - missing provider/service mode
   - missing chat model
   - auth mode unsuitable for server-to-server SDK use
4. Align General/Auth/API copy so `AuthMode` is the single source of truth.
5. Add last-rotated metadata if missing; otherwise capture as deferred follow-up.
6. Add component tests for enablement, disabled states, DTO round-trip,
   examples, and auth warnings.

## Files in scope

- Publish dialog UI/components/styles
- Published guide DTO/client contract wiring for wire API config
- Component tests for publish dialog API controls

Out of scope:

- Backend endpoint handler behavior
- Final docs package for admins (Phase 7)

## Self-verification

```powershell
cd src/client
npm run build
npm test -- --run

cd ../server
dotnet build GuideAntsApi.sln
dotnet test GuideAntsApi.sln

cd ../..
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/run-codeql-changed.ps1 -BaseRef origin/main -IncludeWorkingTree
```

## Definition of Done

- [ ] `APIs` tab exists in publish dialog.
- [ ] Controls and readiness states implemented as specified.
- [ ] Auth wording aligns to `AuthMode` single source of truth.
- [ ] One-time API key display behavior preserved.
- [ ] Required component tests pass.
- [ ] Required changed-scope CodeQL scan is clean for changed files.

## Report-back contract (return exactly this)

```text
PHASE 6 REPORT
- APIs tab added: <yes/no + path>
- Controls added: <list>
- Readiness states implemented: <list>
- Auth wording alignment changes: <paths>
- API key display behavior preserved: <how verified>
- Component tests: <paths + summary>
- CODEQL changed-file findings: <count + ids/files or none>
- Build/tests: <counts>
- Files touched: <list>
- Deviations: <list or none>
```
