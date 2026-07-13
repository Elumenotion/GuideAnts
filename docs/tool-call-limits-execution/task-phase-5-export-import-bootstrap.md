# Task — Phase 5: Export/import + bootstrap defaults

> Subagent brief. Execute top to bottom. Return the Report-back contract verbatim.

## Mission

Round-trip tool limit fields through guide export/import and set the Creative Guide Search
assistant bootstrap default (`max_tool_calls_per_turn: 12`). Ensure bootstrap seeding
materializes limits on first install.

## Read first

- `../tool-call-limits-proposal.md` §4.2 (surfaces), §4.3 (bootstrap defaults), §13 (export
  test).
- `./DECISIONS.md` — T9, T15.
- `./runtime-parity-gate.md` §bootstrap row.
- `src/server/GuideAntsApi/Services/Guides/GuideExportImportService.cs`
- `src/server/GuideAntsApi/Resources/bootstrap/guides/creative-guide/assistants/Search/`
  (manifest + `instructions.md` — **do not** change Search retry policy in instructions)
- Follow skills Phase 3 export/import brief as a structural template.

## Preconditions

- Phase 1 gate green (schema + DTOs).
- Confirm T9 with orchestrator if Search default must differ from `12`.

## Guardrails (hard)

- **Export/import lossless** for limit fields on assistants and guide members.
- **Bootstrap JSON** uses snake_case keys per T15.
- **Do not modify Search retry instructions** (proposal §3 non-goals). Only add manifest
  limit field.
- **Do not change** unrelated bootstrap assistants unless adding explicit defaults per proposal
  §4.3 table (orchestrator may scope to Search only for this phase).

## Tasks

1. **GuideExportImportService:** read/write `max_tool_calls_per_turn`,
   `max_tool_rounds_per_turn` on assistant manifests; `max_tool_calls_per_invocation` on crew
   member entries if present in export format.
2. **Creative Guide Search manifest:** add `"max_tool_calls_per_turn": 12` (T9).
3. **Tests:**
   - Export guide with limits → import → DB values match.
   - Bootstrap seed loads Search with limit 12 (integration or seeder test).
4. **Document** bootstrap default in proposal cross-link or execution STATUS if needed.

## Files in scope

- `src/server/GuideAntsApi/Services/Guides/GuideExportImportService.cs`
- `src/server/GuideAntsApi/Resources/bootstrap/guides/creative-guide/**` (Search manifest)
- `src/server/GuideAntsApi.Tests/**` or integration tests for export/import

## Files out of scope

- `ThreadRun` changes.
- Client UI (Phase 4).
- Search `instructions.md` content changes.

## Self-verification

```bash
cd src/server && dotnet build GuideAntsApi.sln && dotnet test GuideAntsApi.sln
```

## Definition of Done

- [ ] Export/import round-trip preserves limits.
- [ ] Search bootstrap default 12 materializes to DB.
- [ ] runtime-parity gate bootstrap row pass.

## Report-back contract

1. Export JSON shape for limit fields (example snippet).
2. Bootstrap file(s) changed.
3. Test names + results.
4. Confirmation Search instructions unchanged.
5. Files touched.
