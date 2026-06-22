# Task — Phase 8: Final acceptance and hardening

> Subagent brief. Execute top to bottom and return the Report-back contract
> verbatim.

## Mission

Run final regression, acceptance checks, and close all remaining gaps.

## Read first

- `../published-wire-api-implementation-plan.md` (Phase 8 section)
- `./00-orchestration.md` (final acceptance section)
- `./test-gate.md`
- `./codeql-gate.md`
- `./STATUS.md`

## Preconditions

- Phases 1–7 are `DONE` with passing gates.

## Guardrails (hard)

- Do not declare done with unresolved gate failures.
- Do not suppress CodeQL findings to force a pass.
- Manual acceptance must include both auth and metering verification.

## Tasks

1. Run full global test gate.
2. Run final local CodeQL diff gate (baseline-vs-current).
3. Run manual/live acceptance scenarios:
   - API key guide with OpenAI SDK chat call
   - webhook guide auth failure and success
   - anonymous guide (if enabled)
   - OpenRouter-backed chat route
   - one configured non-chat route (local/HF/OpenAI service mode)
   - cost-limit exceeded response
   - usage appears in guide API usage reporting
4. Confirm endpoint contracts are stable.
5. Confirm successful wire calls are always metered.
6. Confirm no provider hardcoding leaked into endpoint handlers.
7. Update `STATUS.md` final matrix, codeql ledger, and deferred-items notes.

## Files in scope

- `docs/published-wire-api/STATUS.md`
- Final acceptance evidence docs/logs if created
- Fixes required to satisfy final gates (minimal, targeted)

Out of scope:

- New feature additions not required for acceptance criteria

## Self-verification

```powershell
cd src/server
dotnet build GuideAntsApi.sln
dotnet test GuideAntsApi.sln

cd ../client
npm run build
npm test -- --run

cd ../..
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/run-codeql-sln-triage.ps1 -Languages all -CleanCodeqlOutputs -SkipGitHubParityCheck
```

## Definition of Done

- [ ] Full server/client build and tests pass versus baseline policy.
- [ ] Final CodeQL diff is clean.
- [ ] Manual acceptance scenarios completed and recorded.
- [ ] Endpoint contracts stable.
- [ ] No successful unmetered wire calls.
- [ ] Auth/cost behavior consistent with published guide configuration.
- [ ] `STATUS.md` updated with final acceptance checklist and any deferrals.

## Report-back contract (return exactly this)

```text
PHASE 8 REPORT
- Full test gate: <pass/fail + counts>
- Final CODEQL diff vs baseline: <count + ids/files or none>
- Manual acceptance scenarios: <result matrix>
- Endpoint contract stability: <how verified>
- Metering completeness verified: <how>
- Provider hardcoding audit: <result>
- STATUS.md updated: <yes/no + sections>
- Files touched: <list>
- Deviations / deferred follow-ups: <list or none>
```
