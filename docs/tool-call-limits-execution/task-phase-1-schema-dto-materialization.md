# Task — Phase 1: Schema + DTO + materialization

> Subagent brief. Execute top to bottom. Return the Report-back contract verbatim.

## Mission

Add persistent storage and API surfaces for per-assistant tool call limits so runtime and UI
can consume them. Ship the EF migration, server DTOs, client types, and
`AssistantDefinition` materialization. No `ThreadRun` enforcement yet — Phase 2.

## Read first

- `../tool-call-limits-proposal.md` §4 (configuration model), §11 (API changes).
- `./DECISIONS.md` — T2, T15 + frozen invariants.
- `src/server/GuideAntsApi.DataModel/Models/Assistant.cs`
- `src/server/GuideAntsApi.DataModel/Models/GuideMember.cs`
- `src/server/AntRunner.Chat/AntRunner.ToolCalling/AssistantDefinitions/AssistantDefinition.cs`
- `src/server/AntRunner.Chat/AntRunner.ToolCalling/AssistantDefinitions.Storage/DatabaseStorage.cs`
- `src/server/GuideAntsApi/Models/Guides/*Dto*.cs` (follow existing `MaxTurns` DTO patterns on
  published guide — but these fields are on **Assistant**, not `PublishedGuide`)
- `src/client/src/types/guides.ts` (or project equivalent)

## Preconditions

- `DECISIONS.md` T1–T15 LOCKED.
- Pre-flight baselines in `STATUS.md`.

## Guardrails (hard)

- **Do not conflate with `PublishedGuide.MaxTurns`.** Tool limits live on `Assistant` /
  `GuideMember`, not published guide limits.
- **`null` = unlimited** (T2). No default of 0 or implicit cap.
- **Validate on save:** reject negative integers; allow `null`.
- **Materialization choke point only:** limits on `AssistantDefinition` (or nested limit DTO),
  populated in `DatabaseStorage.MaterializeAssistant` — not read from DB inside `ThreadRun` in
  this phase.
- No `ThreadRun` changes in this phase.

## Tasks

1. **EF migration:** Add nullable `MaxToolCallsPerTurn`, `MaxToolRoundsPerTurn` to
   `Assistants`; nullable `MaxToolCallsPerInvocation` to `GuideMembers`.
2. **Entity models:** Map columns on `Assistant` and `GuideMember`.
3. **Server DTOs:** Extend `CreateAssistantDto`, `UpdateAssistantDto`, `CreateGuideDto`,
   `UpdateGuideDto`, and any crew-member DTOs with JSON names per T15.
4. **GuidesService:** Persist and return limits on create/update/get guide flows.
5. **AssistantDefinition:** Add limit properties; wire in `DatabaseStorage.MaterializeAssistant`.
6. **Client types:** Mirror server DTO fields in TypeScript guide/assistant types.
7. **Tests:** Migration smoke; DTO round-trip unit test; materialization includes limits from
   DB rows; negative value validation rejected.

## Files in scope

- `src/server/GuideAntsApi.DataModel/Models/Assistant.cs`
- `src/server/GuideAntsApi.DataModel/Models/GuideMember.cs`
- `src/server/GuideAntsApi.DataModel/Migrations/*` (new migration only)
- `src/server/AntRunner.Chat/AntRunner.ToolCalling/AssistantDefinitions/AssistantDefinition.cs`
- `src/server/AntRunner.Chat/AntRunner.ToolCalling/AssistantDefinitions.Storage/DatabaseStorage.cs`
- `src/server/GuideAntsApi/Models/Guides/*.cs` (DTOs touched)
- `src/server/GuideAntsApi/Services/Guides/GuidesService.cs`
- `src/server/GuideAntsApi.Tests/**` (new tests)
- `src/client/src/types/guides.ts` (or equivalent)

## Files out of scope

- `ThreadRun.cs`, chat clients, UI components, `GuideExportImportService` (Phase 5),
  bootstrap manifests (Phase 5).

## Self-verification

```bash
cd src/server && dotnet build GuideAntsApi.sln && dotnet test GuideAntsApi.sln
cd src/client && npm run build
```

## Definition of Done

- [ ] Migration applies; columns exist.
- [ ] Save/load guide preserves limit fields.
- [ ] `AssistantDefinition` carries limits after materialization.
- [ ] Client types compile.
- [ ] No runtime enforcement code merged.

## Report-back contract

Return exactly:

1. Migration name and columns added.
2. List of DTO/type files changed.
3. Test names added and pass counts.
4. Any DECISIONS ambiguity encountered (should be none).
5. Files touched (full paths).
6. Gate self-check: build/test green.
