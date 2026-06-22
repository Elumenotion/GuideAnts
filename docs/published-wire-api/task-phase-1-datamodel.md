# Task — Phase 1: Data model and usage schema

> Subagent brief. Execute top to bottom and return the Report-back contract
> verbatim.

## Mission

Add storage shape only for published wire APIs: config storage, DTOs, usage
attribution fields, embeddings usage category, and indexes/migration support.

No endpoint behavior in this phase.

## Read first

- `../published-wire-api-implementation-plan.md` (Phase 1 section)
- `./DECISIONS.md` (all locked decisions)
- `./test-gate.md`
- Data model files for `PublishedGuide`, `UsageEvent`, usage category enums, and
  related DTOs.

## Preconditions

- Phase 0 baseline is recorded in `STATUS.md`.
- EF tooling verified (`dotnet ef --version`).

## Guardrails (hard)

- No endpoint or handler behavior changes.
- Existing usage calls remain source-compatible with unchanged semantics.
- New schema fields are nullable/backward compatible unless explicitly required.
- Migration must apply on fresh DB and existing DB.

## Tasks

1. Add published wire API config storage to `PublishedGuide`, preferably
   `WireApiConfigJson`.
2. Add server/client DTO support for:
   - `wireApiConfig.enabled`
   - `profile`
   - endpoint flags
   - alias map
   - max request sizes
3. Extend `UsageEvent` with:
   - `PublishedGuideId`
   - `SourceChannel`
   - `ExternalRequestId`
   - `ExternalUserIdentity`
4. Add `UsageCategory.Embeddings = 9` in data model and usage package.
5. Add indexes for:
   - `PublishedGuideId + Created`
   - `SourceChannel + Created`
   - `ExternalRequestId`
6. Generate migration and verify fresh + existing DB apply.

## Files in scope

- Data model entities and EF configuration for `PublishedGuide`/`UsageEvent`
- Usage category enums/packages shared by usage recording and reporting
- API/client DTOs for published guide config
- EF migrations/snapshot and related tests

Out of scope:

- Endpoint handlers
- Auth/cost enforcement logic
- UI wiring beyond DTO compatibility

## Self-verification

```powershell
cd src/server
dotnet build GuideAntsApi.sln
dotnet ef migrations list --project GuideAntsApi.DataModel/GuideAntsApi.DataModel.csproj --startup-project GuideAntsApi/GuideAntsApi.csproj
dotnet test GuideAntsApi.sln

cd ../client
npm run build
npm test -- --run
```

Plus migration apply checks on fresh and existing DB.

## Definition of Done

- [ ] `WireApiConfigJson` (or equivalent) added to `PublishedGuide`.
- [ ] DTOs cover enabled/profile/endpoint flags/alias map/max request sizes.
- [ ] `UsageEvent` attribution fields added and backward compatible.
- [ ] `UsageCategory.Embeddings` exists with value `9` in both model and usage
      package.
- [ ] Indexes added (`PublishedGuideId+Created`, `SourceChannel+Created`,
      `ExternalRequestId`).
- [ ] Migration generated and applies on fresh and existing DB.
- [ ] Global test gate passes versus baseline.

## Report-back contract (return exactly this)

```text
PHASE 1 REPORT
- WireApiConfigJson added: <yes/no>
- UsageEvent attribution fields added: <list>
- Embeddings category value: <number>
- Migration name: <name>
- Fresh DB apply: <pass/fail>
- Existing DB apply/backward compatibility: <how verified>
- Build/tests: <counts>
- Files touched: <list>
- Deviations: <list or none>
```
