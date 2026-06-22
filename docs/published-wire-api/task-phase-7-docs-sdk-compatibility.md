# Task — Phase 7: Docs, examples, and SDK compatibility

> Subagent brief. Execute top to bottom and return the Report-back contract
> verbatim.

## Mission

Produce operator-facing docs and SDK examples that match implemented wire API
behavior.

## Read first

- `../published-wire-api-implementation-plan.md` (Phase 7 section)
- `./DECISIONS.md`
- `./test-gate.md`
- Existing docs under `docs/` for publishing/auth/usage guidance.

## Preconditions

- Phase 6 gate is green.

## Guardrails (hard)

- Docs must match actual routes and error names.
- Examples must use alias-based model names, not raw provider model IDs.
- Unsupported-field guidance must match real handler behavior.

## Tasks

1. Add/update admin-facing docs for:
   - base URL
   - auth headers
   - endpoint support matrix
   - alias behavior
   - known unsupported fields
   - cost attribution
2. Add examples for:
   - curl
   - OpenAI JS SDK
   - OpenAI Python SDK
3. Add troubleshooting guidance for:
   - provider not configured
   - endpoint disabled
   - cost limit exceeded
   - auth failed
   - unsupported feature
4. Add smoke-style checks for example requests where practical.
5. Record documentation paths and verification notes in phase report.

## Files in scope

- Docs under `docs/` related to published wire APIs
- Example snippets/assets referenced by docs
- Lightweight tests/scripts used to smoke-check examples

Out of scope:

- New API behavior changes
- UI behavior changes beyond documentation fixes

## Self-verification

```powershell
cd src/server
dotnet build GuideAntsApi.sln
dotnet test GuideAntsApi.sln

cd ../client
npm run build
npm test -- --run
```

Plus any smoke checks for documented examples.

## Definition of Done

- [ ] Admin docs cover URL/auth/matrix/aliases/unsupported fields/attribution.
- [ ] Curl + OpenAI JS + OpenAI Python examples added.
- [ ] Troubleshooting guidance covers required failure modes.
- [ ] Docs verified against implemented route and error names.
- [ ] Smoke checks for examples recorded where practical.
- [ ] Global test gate passes vs baseline.

## Report-back contract (return exactly this)

```text
PHASE 7 REPORT
- Admin docs updated: <paths>
- Endpoint support matrix doc: <path>
- SDK examples added: <paths>
- Troubleshooting docs added: <paths>
- Route/error-name parity verification: <how>
- Example smoke checks: <paths/results or n/a>
- Build/tests: <counts>
- Files touched: <list>
- Deviations: <list or none>
```
