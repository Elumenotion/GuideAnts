# Task — Phase 3: Export/import + bootstrap

> Subagent brief. Execute top to bottom. Return the Report-back contract verbatim.

## Mission

Make skills portable: round-trip `Skill` files through guide export/import and ship at
least one curated **bootstrap** skill. Export is nearly free (the existing raw-`RelativePath`
`else` branch already writes `Skills/...`); the real work is a `Skills/` **import loop** and
a seeded skill that proves the path end-to-end.

## Read first

- `../skills-support-proposal.md` §15 (export/import + bootstrap).
- `./DECISIONS.md` — S1, S5, S10 + invariants.
- `./no-index-gate.md`.
- Existing touchpoints:
  - `src/server/GuideAntsApi/Services/Guides/GuideExportImportService.cs`
    (`WriteAssistantFilesAsync` — the `else` raw-path branch ~L238; `VectorStores/` import
    loop ~L843/L1541; `CodeInterpreter/` ~L889/L1584)
  - `src/server/GuideAntsApi/Resources/bootstrap/guides/*` (seed layout;
    `RequiredGuidesAssistantsSeeder`)

## Preconditions

- **Phase 1 gate green** (Skill storage convention exists). Phase 2 is **not** required, but
  if it landed, reuse `SkillPackageParser` where helpful (do not fork parsing).

## Guardrails (hard)

- **Do not duplicate export logic.** Confirm the existing `else` branch already writes
  `Skills/<name>/...` at raw `RelativePath`; only add the missing import side.
- The `Skills/` import loop creates `FolderKind = "Skill"` rows preserving the relative path
  under `Skills/<name>/`. **Never** create a shadow or enqueue extraction (no-index gate).
- Round-trip must be **lossless**: export → import reproduces the same `Skill` file set,
  relative paths, and bytes (S10 — original `SKILL.md` preserved).
- No fallback masking; malformed skill entries fail explicitly during import validation.

## Tasks

1. **Verify export.** Confirm `WriteAssistantFilesAsync` emits `Skill` files at
   `Skills/<name>/...` via the `else` branch for both the guide-root and nested-assistant
   export paths. Add a test asserting this; only add code if a gap exists.
2. **Add import loop.** Add a `Skills/` import loop (parallel to `VectorStores/` and
   `CodeInterpreter/`) in both the guide-root and nested-assistant import paths that creates
   `FolderKind = "Skill"` `AssistantFile` rows preserving the path after `Skills/`.
3. **Bootstrap skill.** Ship one curated skill under
   `Resources/bootstrap/guides/<guide>/Skills/<name>/SKILL.md` (+ a `references/` file to
   exercise the tree) and confirm `RequiredGuidesAssistantsSeeder` imports it cleanly.
4. **Tests.** Export writes `Skills/...`; import creates `Skill` rows; **round-trip lossless**
   (export → import → identical file set/paths/bytes); bootstrap seed import; no-index for
   the import path.

## Files in scope

- `src/server/GuideAntsApi/Services/Guides/GuideExportImportService.cs`
- `src/server/GuideAntsApi/Resources/bootstrap/guides/<guide>/Skills/<name>/*` (new seed)
- Tests: `src/server/GuideAntsApi.Tests/Services/Guides/GuideExportImportService*Tests.cs`

Out of scope:

- Runtime materialization / tools (Phase 1). Authoring UI/DTOs (Phase 2). Wire/MCP (Phase 4).
  Sidecar (Phase 5).

## Self-verification

```bash
cd src/server && dotnet build GuideAntsApi.sln && dotnet test GuideAntsApi.sln
```

Run required gate: `no-index-gate.md` (import path).

## Definition of Done

- [ ] Export writes `Skills/<name>/...` (verified, not duplicated).
- [ ] `Skills/` import loop creates `Skill` rows (guide-root + nested assistant paths).
- [ ] Round-trip lossless (test-proven).
- [ ] One bootstrap skill ships and seeds cleanly.
- [ ] no-index holds for the import path.
- [ ] Build/tests green.

## Report-back contract (return exactly this)

```text
PHASE 3 REPORT
- Export writes Skills/<name>/ (verified, no dup): <pass/fail + test ref>
- Skills/ import loop (root + nested): <pass/fail + path>
- Round-trip lossless: <pass/fail + test ref>
- Bootstrap skill seeds cleanly: <path + pass/fail>
- NO-INDEX GATE (import path): no-shadow=<p/f> no-job=<p/f>
- Verification: server-build=<p/f> server-tests=<counts>
- Files touched: <exhaustive list>
- Deviations / surprises: <list or "none">
```
