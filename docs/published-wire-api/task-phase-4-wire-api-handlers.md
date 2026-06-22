# Task — Phase 4: Wire API handlers

> Subagent brief. Execute top to bottom and return the Report-back contract
> verbatim.

## Mission

Implement OpenAI-compatible published wire API endpoint handlers.

## Read first

- `../published-wire-api-implementation-plan.md` (Phase 4 section)
- `./DECISIONS.md`
- `./test-gate.md`
- `./codeql-gate.md`
- Existing published conversation execution and provider-routed non-chat services.

## Preconditions

- Phase 3 gate is green.

## Guardrails (hard)

- No provider-specific hardcoding in endpoint handlers.
- No raw provider model IDs exposed unless intentionally configured as aliases.
- Unsupported OpenAI features must return explicit OpenAI-shaped errors.
- Implement non-streaming first; streaming only after green non-streaming tests.

## Tasks

1. Add endpoint group:
   - `/api/published/openai/{pubId}/v1`
2. Implement:
   - `GET /models` (enabled aliases only)
   - `POST /chat/completions` via published conversation execution
   - `POST /responses` via published conversation execution
   - `POST /embeddings` via `IEmbeddingService`
   - `POST /images/generations` via existing image routing
   - `POST /audio/transcriptions` via `ISpeechTranscriptionService`
   - `POST /audio/speech` via `ISpeechSynthesisService`
3. Enforce request-size validation per endpoint.
4. Add OpenAI-like response adapters:
   - IDs
   - timestamps
   - object names
   - choices
   - usage
   - errors
5. Add contract snapshots for each endpoint.
6. Add provider-routing tests to prove configured mode/provider usage.

## Files in scope

- Published wire endpoint registration and handlers
- Request/response adapters and validation
- Endpoint contract tests and provider-routing tests

Out of scope:

- Cost reporting UI
- Publishing dialog UI controls

## Self-verification

```powershell
cd src/server
dotnet build GuideAntsApi.sln
dotnet test GuideAntsApi.sln

cd ../..
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/run-codeql-changed.ps1 -BaseRef origin/main -IncludeWorkingTree
```

Run client build/tests only if shared API contracts used by client changed.

## Definition of Done

- [ ] Endpoint group exists at locked base URL.
- [ ] All seven supported endpoints implemented.
- [ ] Request-size validation is enforced.
- [ ] OpenAI-shaped response/error adapters are used consistently.
- [ ] Contract snapshots exist for each endpoint.
- [ ] Provider-routing tests pass.
- [ ] Required changed-scope CodeQL scan is clean for changed files.

## Report-back contract (return exactly this)

```text
PHASE 4 REPORT
- Endpoint group path: <value>
- Implemented endpoints: <list>
- Request-size validation: <how/where>
- Response/error adapter coverage: <list>
- Contract snapshots: <paths>
- Provider-routing tests: <paths + summary>
- CODEQL changed-file findings: <count + ids/files or none>
- Build/tests: <counts>
- Files touched: <list>
- Deviations: <list or none>
```
