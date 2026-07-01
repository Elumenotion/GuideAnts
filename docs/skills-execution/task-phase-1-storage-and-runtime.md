# Task — Phase 1: Storage + runtime (own conversations)

> Subagent brief. Execute top to bottom. Return the Report-back contract verbatim.

## Mission

Make skills real end-to-end for GuideAnts' **own** conversations. Introduce the `Skill`
`AssistantFile` storage convention, parse `SKILL.md` frontmatter into tier-1 descriptors,
materialize those descriptors onto the runtime definition, serve bodies on demand through
two server-handled tools, and inject a progressive-disclosure discovery block into the
prompt. No authoring UI, no export/import, no wire/MCP work — those are Phases 2–4. This
phase is the foundation all of them depend on.

## Read first

- `../skills-support-proposal.md` §5–§10, §14, §16 (storage, materialization, injection,
  tools, gating, validation).
- `./DECISIONS.md` — S1–S6, S8, S10, S11, S12, S13 + all frozen invariants.
- `./progressive-disclosure-gate.md`, `./no-index-gate.md`, `./codeql-gate.md`.
- Existing touchpoints (inventory before editing):
  - `src/server/GuideAntsApi.DataModel/Models/AssistantFile.cs`
  - `src/server/AntRunner.Chat/AntRunner.ToolCalling/AssistantDefinitions.Storage/DatabaseStorage.cs`
    (`MaterializeAssistant`, `BuildToolsArray` ~L369, `BuildToolResources` ~L397)
  - `src/server/AntRunner.Chat/AntRunner.ToolCalling/AssistantDefinitions/AssistantDefinition.cs`
  - `src/server/AntRunner.Chat/AntRunner.Chat/ThreadRun.cs` (`DoToolCalls`; the
    `SearchAssistantFiles` local-tool pattern is your template)
  - `src/server/GuideAntsApi/Services/Conversations/Mapping/ConversationHistoryBuilder.cs`
    (`BuildPublishedMessagesForAssistantAsync`, `PrepareMessagesForAssistantAsync`,
    `SplitPublishedClientPrefix`)

## Preconditions

- `DECISIONS.md` S1–S13 LOCKED. (Confirm S13 limits with the user if defaults are wrong.)
- Pre-flight baselines captured in `STATUS.md`, including the no-index enqueue-site capture.

## Guardrails (hard)

- A `Skill` file is distinguished by `FolderKind = "Skill"` + a `Skills/<name>/` path. It does
  **not** add `tool_resources`. `BuildToolsArray` enables `code_interpreter` when a skill has
  `scripts/` or `assets/` payload (S11); manifest-only skills remain inert to tools.
- **Never index a skill.** Do not create an `AssistantFileMarkdownShadow`, do not enqueue
  extraction, do not add it to `file_search`. Do not weaken the `FolderKind == "VectorStore"`
  gating anywhere.
- **No skill body in `AssistantDefinition`.** `Skills` carries `SkillDescriptor`s (tier-1 +
  file-path inventory) only.
- **`skills.read` path-safety:** canonicalize `Skills/<name>/` + `file_path`; reject `..`,
  absolute paths, and cross-skill escapes with an explicit error. No clamp, no silent empty.
- **Gating hides; it never injects.** Do not add tools because a skill declares
  `requires_toolsets`. Do not fabricate capability. No fallback masking; parse/validation
  failures are explicit errors.
- `skills.list`/`skills.read` are **server-handled** (`ActionType.LocalFunction`) and must
  never produce `pending_client_tool`.
- Preserve the original `SKILL.md` bytes verbatim in storage (S10); the parser reads a
  normalized view but does not rewrite the file.

## Tasks

1. **Storage convention.** Establish `Skill` as a valid `AssistantFile.FolderKind` and the
   `Skills/<skill-name>/SKILL.md` (+ `references/`/`scripts/`/`assets/`) layout. (No schema
   change — reuse the `AssistantFile` table. Update any `FolderKind` allow-list/validation
   to accept `Skill`.)
2. **Frontmatter parser.** Add `SkillFrontmatter` (+ `SkillFrontmatter.Parse`) reading the
   agentskills.io dialect (`name`, `description`, `metadata.guideants.{enabled,display_order,
   requires_toolsets,requires_tools,fallback_for_toolsets,fallback_for_tools}`, `platforms`);
   tolerate hermes `metadata.hermes.*` and Claude-Code `allowed-tools`/`argument-hint`
   without failing (S2, S10).
3. **Descriptor + definition.** Add `SkillDescriptor` (name, description, folderPath,
   locator `skill://<assistantId>/<name>`, requires/fallback/platforms, displayOrder,
   files[]). Add `AssistantDefinition.Skills`.
4. **Materialization.** Add `DatabaseStorage.BuildSkills(assistant)` (sibling to
   `BuildToolsArray`/`BuildToolResources`): group `Skill` files by `Skills/<name>/`, require a
   `SKILL.md` with `name`+`description`, skip `enabled: false`, order by `display_order` then
   name, project descriptors (no bytes). Populate `AssistantDefinition.Skills` in
   `MaterializeAssistant`.
5. **Server tools.** Add `skills.list` (returns filtered descriptors + file inventory) and
   `skills.read` (locator + optional `file_path` → text from `AssistantFile`, path-safe) as
   `ActionType.LocalFunction` tools dispatched in `ThreadRun.DoToolCalls`, following the
   `SearchAssistantFiles` pattern. Auto-enable for assistants with ≥1 enabled skill (S8).
6. **Discovery block.** In `ConversationHistoryBuilder` (both published and private
   assembly), inject a tier-1 `developer`/`system` skills block (name + description +
   locator + "how to use") built from `def.Skills`, with the gating visibility filter (S6)
   applied against the assistant's available tools. Inject only for skill-bearing assistants.
7. **Notebook sandbox materialization (S11).** Add `SkillNotebookMaterializer` and extend
   `NotebookService.CopyGuideFilesToNotebookAsync` to copy skill `scripts/` and `assets/` into
   `Resources/` with path-preserving `Output/` symlinks (same model as `CodeInterpreter`
   files). Do not copy `SKILL.md` or `references/`. Enable `code_interpreter` in
   `BuildToolsArray` when materializable skill payload exists.
8. **Tests.** Frontmatter parse (3 dialects; missing name/description → explicit error);
   `BuildSkills` grouping + enabled/order; descriptor has no body; `skills.read` body +
   reference + path-safety rejections; discovery block gating filter; notebook copy of skill
   scripts; and the **no-index regression test** (a `Skill` file creates no shadow and enqueues
   no extraction job).

## Files in scope

Backend:

- `src/server/GuideAntsApi.DataModel/Models/AssistantFile.cs` (FolderKind allow-list only, if any)
- `src/server/AntRunner.Chat/AntRunner.ToolCalling/AssistantDefinitions/AssistantDefinition.cs`
- `src/server/AntRunner.Chat/AntRunner.ToolCalling/AssistantDefinitions.Storage/DatabaseStorage.cs`
- `src/server/AntRunner.Chat/AntRunner.Chat/ThreadRun.cs` (skills.list/read dispatch)
- New: `SkillFrontmatter`, `SkillDescriptor`, `SkillNotebookMaterializer`,
  `skills.list`/`skills.read` executor (place alongside existing local-tool/`SearchAssistantFiles` code)
- `src/server/GuideAntsApi/Services/Components/NotebookService.cs` (skill payload copy)
- `src/server/GuideAntsApi/Services/Conversations/Mapping/ConversationHistoryBuilder.cs`
- Tests under `src/server/GuideAntsApi.Tests/*` and `AntRunner`-level tests as appropriate.

Out of scope:

- Guide Builder UI, DTOs, `GuidesService` create/update wiring (Phase 2).
- Export/import + bootstrap (Phase 3).
- Wire/MCP resource exposure + trace `Source=skills` (Phase 4).
- `AssistantSkillMeta` sidecar (Phase 5).

## Self-verification

```bash
cd src/server && dotnet build GuideAntsApi.sln && dotnet test GuideAntsApi.sln
cd src/client && npm run build && npm test -- --run
```

Run required gates: `progressive-disclosure-gate.md`, `no-index-gate.md`, `codeql-gate.md`.

## Definition of Done

- [ ] `Skill` FolderKind + `Skills/<name>/` convention accepted end-to-end.
- [ ] `SkillFrontmatter.Parse` handles 3 dialects; explicit errors on missing required fields.
- [ ] `BuildSkills` groups/filters/orders and yields tier-1 descriptors; `AssistantDefinition.Skills` populated; no body/bytes in the descriptor.
- [ ] `skills.list`/`skills.read` server-handled; path-safe; no `pending_client_tool`.
- [ ] Discovery block injected (private + published) with gating filter; skill-bearing only.
- [ ] no-index regression test green; skills never shadowed/indexed/in file_search.
- [ ] Build/tests green; progressive-disclosure + no-index + CodeQL gates pass.

## Report-back contract (return exactly this)

```text
PHASE 1 REPORT
- Skill FolderKind + Skills/<name>/ convention: <paths>
- SkillFrontmatter.Parse (agentskills/hermes/claude-code; explicit errors): <pass/fail + test refs>
- SkillDescriptor + AssistantDefinition.Skills (no body): <pass/fail>
- BuildSkills grouping/enabled/order: <pass/fail + path>
- skills.list/skills.read server-handled + path-safe + no pending_client_tool: <pass/fail + test refs>
- Discovery block injected (private+published) + gating filter: <pass/fail + trace ref>
- PROGRESSIVE DISCLOSURE GATE: tier1-only=<p/f> no-body-in-def=<p/f> path-safety=<p/f>
- NO-INDEX GATE: no-shadow=<p/f> no-job=<p/f> not-in-file_search=<p/f> sites-still-vectorstore=<p/f> regression-test=<path>
- CODEQL: new-vs-baseline=<count → ids/files or none>
- Verification: server-build=<p/f> server-tests=<counts> client-build=<p/f> client-tests=<counts>
- Files touched: <exhaustive list>
- Deviations / surprises: <list or "none">
```
