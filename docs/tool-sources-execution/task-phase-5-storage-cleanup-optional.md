# Task - Phase 5: Optional storage cleanup

> Subagent brief. Execute only if explicitly approved. Return the Report-back
> contract verbatim.

## Mission

Optionally introduce clearer storage names for Tool Sources while preserving backward
compatibility and keeping OpenAPI descriptor JSON as canonical runtime payload.

## Read first

- `../tool-sources-guide-builder-proposal.md` sections 10 and 15 (Phase 5).
- `./DECISIONS.md` decision D3 and Part B invariants.
- `./codeql-gate.md`.
- Current storage/runtime mappings:
  - `src/server/GuideAntsApi.DataModel/Models/AssistantOpenApiSchema.cs`
  - `src/server/GuideAntsApi.DataModel/Models/AssistantOpenApiOperation.cs`
  - `src/server/GuideAntsApi/Services/Guides/GuidesService.cs`
  - `src/server/AntRunner.Chat/AntRunner.ToolCalling/AssistantDefinitions.Storage/*`

## Preconditions

- Phase 4 gate green.
- User/orchestrator explicitly approved Phase 5 execution.
- D3 storage strategy locked.

## Guardrails (hard)

- `SpecificationJson` (or equivalent canonical descriptor JSON) must remain the
  runtime source of truth.
- Existing rows in `AssistantOpenApiSchema` must continue to read/write correctly
  during transition.
- No destructive migration without tested rollback.
- No auth-provider linkage regressions.

## Tasks

1. Implement approved storage cleanup strategy (if any), such as introducing clearer
   source naming entities/columns while preserving compatibility.
2. Add migration/backfill logic with explicit compatibility reads/writes.
3. Keep export/import and runtime storage readers compatible with existing data.
4. Validate auth-provider matching behavior remains correct after cleanup.
5. Add migration and compatibility tests (including pre-migration data snapshots).
6. Document rollback and post-migration verification steps.

## Files in scope

- `src/server/GuideAntsApi.DataModel/Models/*` (tool source related only)
- `src/server/GuideAntsApi.DataModel/ApplicationDbContext.cs`
- `src/server/GuideAntsApi.DataModel/Migrations/*` (new migration if needed)
- `src/server/GuideAntsApi/Services/Guides/GuidesService.cs`
- `src/server/AntRunner.Chat/AntRunner.ToolCalling/AssistantDefinitions.Storage/*`
- Relevant tests under `src/server/GuideAntsApi.Tests/*`
- Optional docs updates for migration notes.

Out of scope:

- New UI authoring features.
- Runtime scheme dispatch changes not required by cleanup.

## Self-verification

```bash
cd src/server && dotnet build GuideAntsApi.sln && dotnet test GuideAntsApi.sln
cd src/server && dotnet ef migrations list --project GuideAntsApi.DataModel/GuideAntsApi.DataModel.csproj --startup-project GuideAntsApi/GuideAntsApi.csproj
```

Run CodeQL diff gate after changes.

## Definition of Done

- [ ] Storage cleanup implemented per approved scope (or phase intentionally aborted).
- [ ] Existing data remains readable/writable with compatibility path.
- [ ] Migrations/backfill tested with rollback plan documented.
- [ ] Auth/provider linkage unaffected.
- [ ] Build/tests green and CodeQL diff clean.

## Report-back contract (return exactly this)

```text
PHASE 5 REPORT
- Phase approved and executed: <yes/no>
- Storage cleanup strategy implemented: <summary>
- Migrations added: <list or none>
- Compatibility read/write path preserved: <yes/no + details>
- Auth/provider linkage regression check: <pass/fail>
- CODEQL: new-vs-baseline=<count -> ids/files or none>
- Verification: server-build=<pass/fail> server-tests=<counts> ef-migrations-check=<pass/fail>
- Files touched: <exhaustive list>
- Deviations / surprises: <list or "none">
```
