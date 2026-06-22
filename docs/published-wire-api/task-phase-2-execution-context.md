# Task — Phase 2: Published API execution context

> Subagent brief. Execute top to bottom and return the Report-back contract
> verbatim.

## Mission

Implement the shared auth/cost/context layer for published wire API execution.

## Read first

- `../published-wire-api-implementation-plan.md` (Phase 2 section)
- `./DECISIONS.md`
- `./test-gate.md`
- `./codeql-gate.md`
- Existing published-guide auth/cost services and request-context patterns.

## Preconditions

- Phase 1 gate is green.
- Locked decisions are unchanged.

## Guardrails (hard)

- No anonymous fallback when guide auth is required.
- Reuse existing token validation/crypto paths; do not duplicate.
- Do not log keys/tokens/raw auth headers.
- Keep app identity cookie behavior unchanged.
- Do not alter MCP/conversation auth behavior except safe shared-helper
  extraction.

## Tasks

1. Add `PublishedApiExecutionContext`.
2. Add resolver/service that:
   - resolves `{pubId}`
   - loads active `PublishedGuide`
   - validates `PublishedGuide.AuthMode`
   - enforces cost limits
   - checks `WireApiConfigJson.enabled`
   - writes request metadata into execution context
3. Support API key auth from:
   - `Authorization: Bearer <key>`
   - `x-guideants-apikey: <key>`
4. Support webhook auth from:
   - `Authorization: Bearer <token>`
   - `X-Published-Auth: <token>`
5. Add OpenAI-shaped error helper for:
   - auth failure
   - endpoint disabled
   - missing model alias
   - provider not ready
   - request too large
   - limit exceeded
6. Add/expand tests for anonymous, API key, webhook, and app identity paths.

## Files in scope

- Published guide execution context/resolver/services
- Shared auth/cost helper wiring needed by wire API stack
- Tests for auth and cost denial behavior

Out of scope:

- Wire endpoint handlers
- Reporting UI

## Self-verification

```powershell
cd src/server
dotnet build GuideAntsApi.sln
dotnet test GuideAntsApi.sln

cd ../..
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/run-codeql-changed.ps1 -BaseRef origin/main -IncludeWorkingTree
```

Also run client build/tests if shared DTO/contracts changed.

## Definition of Done

- [ ] `PublishedApiExecutionContext` and resolver/service are implemented.
- [ ] Auth mode support covers API key + webhook header variants.
- [ ] Cost limit denial returns stable OpenAI-shaped error.
- [ ] Disabled endpoints and alias/provider/request-size failures map to
      OpenAI-shaped errors.
- [ ] Tests cover anonymous/API-key/webhook/app-identity behavior.
- [ ] Required changed-scope CodeQL scan is clean for changed files.

## Report-back contract (return exactly this)

```text
PHASE 2 REPORT
- PublishedApiExecutionContext added: <yes/no>
- Resolver responsibilities implemented: <list>
- Auth header variants supported: <list>
- OpenAI error helper coverage: <list>
- Auth tests added/updated: <paths + summary>
- Cost-limit denial shape verified: <how>
- CODEQL changed-file findings: <count + ids/files or none>
- Build/tests: <counts>
- Files touched: <list>
- Deviations: <list or none>
```
