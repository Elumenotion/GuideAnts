# Task — Phase 3: Usage recorder and metering wrappers

> Subagent brief. Execute top to bottom and return the Report-back contract
> verbatim.

## Mission

Make successful billable wire API usage impossible to miss.

## Read first

- `../published-wire-api-implementation-plan.md` (Phase 3 section)
- `./DECISIONS.md` (usage attribution contract)
- `./test-gate.md`
- Existing `IUsageRecorder` and published STT usage paths.

## Preconditions

- Phase 2 gate is green.

## Guardrails (hard)

- Successful billable wire calls must always produce at least one usage event.
- Usage write failures on wire APIs must surface as server errors (no silent
  success).
- Existing conversation usage semantics must remain unchanged except additive
  attribution fields.

## Tasks

1. Extend `IUsageRecorder.RecordAsync` with optional published/source/request
   attribution fields while keeping old call sites source-compatible.
2. Add `RecordEmbeddingsAsync`.
3. Add `PublishedWireUsageRecorder` (or equivalent wrapper) requiring:
   - project id
   - notebook id
   - published guide id
   - source channel
   - external request id
   - operation/endpoint
4. Route published STT usage through the same wrapper.
5. Add wire usage metadata schema with:
   - endpoint
   - alias
   - provider model/service mode
   - status
   - request byte count
   - input count
   - output count
6. Add tests proving attribution fields persist in usage rows.

## Files in scope

- Usage recorder interfaces/implementations/wrappers
- Published STT usage call sites
- Usage event metadata helpers/types
- Unit/integration tests for usage attribution and failure behavior

Out of scope:

- Endpoint contract adapters
- UI/reporting pages

## Self-verification

```powershell
cd src/server
dotnet build GuideAntsApi.sln
dotnet test GuideAntsApi.sln

cd ../client
npm run build
npm test -- --run
```

## Definition of Done

- [ ] Recorder interface extension is source-compatible.
- [ ] `RecordEmbeddingsAsync` exists and is wired.
- [ ] Wire usage wrapper requires and persists project/notebook/published/source/request attribution.
- [ ] Published STT usage goes through the shared wrapper.
- [ ] Usage-write failure behavior is explicit and tested.
- [ ] Global test gate passes vs baseline.

## Report-back contract (return exactly this)

```text
PHASE 3 REPORT
- IUsageRecorder extension: <what changed + compatibility status>
- RecordEmbeddingsAsync: <added yes/no + path>
- PublishedWireUsageRecorder (or equivalent): <yes/no + required fields>
- Published STT path updated: <yes/no + path>
- Wire usage metadata schema: <fields>
- Attribution tests: <paths + summary>
- Usage-write failure behavior verified: <how>
- Build/tests: <counts>
- Files touched: <list>
- Deviations: <list or none>
```
