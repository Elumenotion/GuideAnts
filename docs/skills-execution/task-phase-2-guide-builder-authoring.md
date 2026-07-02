# Task — Phase 2: Guide Builder authoring

> Subagent brief. Execute top to bottom. Return the Report-back contract verbatim.

## Mission

Add the Guide Builder authoring surface for skills: a **Skills tab** that imports a
`SKILL.md` package, authors a new skill, and shows gating; server DTO + `GuidesService`
wiring to persist `Skill` `AssistantFile` rows; a `SkillPackageParser`/`ISkillImportService`
for parsing; and a **create-assistant-from-skill(s)** entry point that maps declared
prerequisites to concrete GuideAnts tools. Runtime already consumes skills (Phase 1); this
phase lets authors put them there.

## Read first

- `../skills-support-proposal.md` §12 (UX), §13 (DTO/service wiring), §16 (validation).
- `./DECISIONS.md` — S1, S2, S6, S9, S10, S11, S12, S13 + invariants.
- `./ui-gate.md` (full), `./no-index-gate.md`.
- Existing touchpoints:
  - `src/client/src/components/guides/editor/{BaseEntityEditor,EditorTabs}.tsx`,
    `Files.tsx` (upload plumbing to reuse), the Instructions Lexical editor
  - `src/client/src/components/common/{ConfirmationDialog,Toast}.tsx`
  - `src/server/GuideAntsApi/Models/Guides/{GuideDto,AssistantDto}.cs`
  - `src/server/GuideAntsApi/Services/Guides/GuidesService.cs` (create/update; file persist
    paths ~L470/L815/L1976 — reuse the `VectorStore`/`CodeInterpreter` file pattern)

## Preconditions

- **Phase 1 gate green** (Skill storage + runtime live).
- `DECISIONS.md` locked; S9 mapping table agreed; S13 limits set.

## Guardrails (hard)

- Reuse the **Files-tab upload plumbing**; do not add a second uploader.
- Persist skills as `Skill` `AssistantFile` rows via `GuidesService`, mirroring how
  `VectorStore`/`CodeInterpreter` files persist. **Never** create a shadow or enqueue
  extraction for a `Skill` file (no-index gate).
- **Create-from-skill maps prerequisites explicitly (S9).** Show the user which tools were
  added and why. No silent capability guessing; no fallback masking.
- Gating display must be **honest**: an unsatisfied skill is shown as "won't be offered
  until <tool> added", not hidden and not auto-fixed.
- One component per file; ≤120-char lines; business logic in tested `*.ts` helpers, not JSX.
- Preserve the original `SKILL.md` verbatim on import (S10).

## Tasks

1. **DTOs.** Add `AssistantSkillDto` (name, description, enabled, displayOrder, files[]);
   extend `Create/UpdateGuideDto` + assistant equivalents + `GuideDetailsDto`/`AssistantDetailsDto`.
2. **Server import service.** Add `SkillPackageParser` / `ISkillImportService`: parse a
   folder or zip into `{ SKILL.md, references/, scripts/, assets/ }`, validate (S16), and
   produce `Skill` `AssistantFile` rows. Reuse `SkillFrontmatter` from Phase 1 (do not fork).
3. **GuidesService.** Persist/round-trip skills on create/update (mirror the VectorStore/
   CodeInterpreter file handling); enforce S13 limits; return skills in details DTOs.
4. **Skills tab (client).** Add the `skills` tab (`EditorTabs`), `FormData.skills`, list +
   card (source badge, gating summary vs selected tools, enable/order, file view), Import
   dialog (folder/zip, explicit parse errors), Author editor (reuse Lexical). Decompose per
   `ui-gate.md` §4; reuse `ConfirmationDialog`/`useToast`/upload path.
5. **Create-from-skill.** Entry point that seeds a new assistant, attaches parsed skills,
   and applies the S9 mapping (declared `requires_*` → concrete tools) with a visible
   summary + confirm dialog.
6. **Tests.** Frontmatter/gating pure-helper unit tests; import (folder+zip, 3 dialects,
   error cases); create-from-skill mapping; `GuidesService` round-trip; no-index for the
   upload path.

## Files in scope

Client:

- `src/client/src/components/guides/editor/EditorTabs.tsx`, `BaseEntityEditor.tsx`
- New `src/client/src/components/guides/editor/skills/*` (tab, list, card, import dialog,
  author editor, `skillFrontmatter.ts`, `skillGating.ts`, `skillCardViewModel.ts`,
  `useSkillImport.ts`) + `__tests__/`

Backend:

- `src/server/GuideAntsApi/Models/Guides/{GuideDto,AssistantDto}.cs`
- `src/server/GuideAntsApi/Services/Guides/GuidesService.cs`
- New `SkillPackageParser`/`ISkillImportService` under `Services/Guides/` (or `Services/Skills/`)
- Tests under `src/server/GuideAntsApi.Tests/Services/*`.

Out of scope:

- Runtime materialization / tools / discovery block (Phase 1 — reuse, do not re-implement).
- Export/import + bootstrap (Phase 3).
- Wire/MCP exposure (Phase 4). Sidecar (Phase 5).

## Self-verification

```bash
cd src/server && dotnet build GuideAntsApi.sln && dotnet test GuideAntsApi.sln
cd src/client && npm run build && npm test -- --run
```

Run required gates: `ui-gate.md`, `no-index-gate.md`.

## Definition of Done

- [ ] `AssistantSkillDto` + DTO extensions; `GuidesService` persists/round-trips skills.
- [ ] `SkillPackageParser`/`ISkillImportService` parse folder+zip (3 dialects), validate,
      produce `Skill` rows; original preserved.
- [ ] Skills tab: import + author + gating display + enable/order; decomposed + reuse per ui-gate.
- [ ] Create-from-skill seeds assistant + attaches skills + applies S9 mapping with summary.
- [ ] no-index holds for the upload path.
- [ ] Build/tests green; ui-gate + no-index gate pass.

## Report-back contract (return exactly this)

```text
PHASE 2 REPORT
- AssistantSkillDto + DTO extensions + GuidesService round-trip: <pass/fail + paths>
- SkillPackageParser/ISkillImportService (folder+zip, 3 dialects, validation): <pass/fail + tests>
- Skills tab (import/author/gating/enable-order): <pass/fail>
- Create-from-skill + S9 mapping summary (no silent guess): <pass/fail>
- UI GATE: tab=<p/f> import=<p/f> author=<p/f> create-from-skill=<p/f> gating=<p/f> reuse=<p/f> decomposition=<p/f> a11y=<p/f>
- NO-INDEX GATE (upload path): no-shadow=<p/f> no-job=<p/f>
- Verification: server-build=<p/f> server-tests=<counts> client-build=<p/f> client-tests=<counts>
- Files touched: <exhaustive list>
- Deviations / surprises: <list or "none">
```
