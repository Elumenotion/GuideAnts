# Guide Builder Skills Support Proposal

Status: Draft / design proposal
Owner: Guide Builder / conversation runtime / published wire API
Related:
- `src/server/GuideAntsApi.DataModel/Models/AssistantFile.cs`
- `src/server/AntRunner.Chat/AntRunner.ToolCalling/AssistantDefinitions.Storage/DatabaseStorage.cs`
- `src/server/AntRunner.Chat/AntRunner.ToolCalling/AssistantDefinitions/AssistantDefinition.cs`
- `src/server/AntRunner.Chat/AntRunner.Chat/ThreadRun.cs`
- `src/server/GuideAntsApi/Services/Conversations/Mapping/ConversationHistoryBuilder.cs`
- `src/server/GuideAntsApi/Services/Guides/GuidesService.cs`
- `src/server/GuideAntsApi/Services/Guides/GuideExportImportService.cs`
- `src/server/GuideAntsApi/Endpoints/PublishedWire/*`
- `src/server/GuideAntsApi/Services/Mcp/ClaudeSkillPackService.cs` (existing SKILL.md export precedent)
- `src/client/src/components/guides/editor/BaseEntityEditor.tsx`
- `CodexTrace.md` (reference capture of an external client using skills over the wire)

## 1. Problem Summary

Anthropic/Claude-style **Skills** (a `SKILL.md` file plus optional `references/`, `scripts/`, and `assets/`) have become the portable, ecosystem-standard way to package specialized instructions and workflows. External clients already expect them: the `CodexTrace.md` capture shows the OpenAI Codex CLI injecting a `<skills_instructions>` block into a `developer` message and declaring skill/resource tools over the GuideAnts wire API, which GuideAnts faithfully passed through.

GuideAnts today has **no first-class notion of a skill on an assistant definition**. The only existing touchpoint is `ClaudeSkillPackService`, which *exports* a published guide as a downloadable Claude skill pack. There is no way to:

1. Import a `SKILL.md` package into an assistant/guide definition.
2. Create an assistant by importing one or more skills.
3. Surface a definition's skills to the model during inference (own conversations and published wire clients).

This proposal adds skills as a first-class, definition-level capability by **reusing the existing `AssistantFile` storage** and the existing prompt-assembly and tool-execution runtime, following the same "keep the canonical internal contract, improve the authoring model around it" philosophy as the Tool Sources proposal.

## 2. Firm Decisions

1. Skills are stored as `AssistantFile` rows under a new `FolderKind = "Skill"`, using a `Skills/<skill-name>/...` relative-path convention. No new tables in the first phase.
2. The canonical on-definition skill format is the **agentskills.io / Anthropic `SKILL.md`** dialect (YAML frontmatter + markdown body + optional `references/`, `scripts/`, `assets/`).
3. Progressive disclosure is mandatory: only `name` + `description` (+ a locator) are injected at prompt time; bodies and reference files load on demand.
4. On-demand loading is served by **server-handled** tools (`skills.list`, `skills.read`) executed inside `ThreadRun`, so skills work for GuideAnts' own conversations and for any wire client, and stay opaque on the wire.
5. Skills are **inert to the vector-store / Kernel Memory pipeline**. A `Skill` file is never shadowed or indexed.
6. Tool gating (`requires_toolsets` / `requires_tools` / `fallback_for_*`) is **offer-time only** — it hides a skill from discovery when prerequisites are absent. It never injects tools or fabricates behavior.
7. The existing `ClaudeSkillPackService` export stays as-is and is orthogonal to this feature.

## 3. Goals

1. Import a `SKILL.md` package (folder or zip) into an assistant or guide definition.
2. Create a new assistant/guide from one or more imported skills.
3. Author and edit skills in the Guide Builder without hand-writing storage rows.
4. Inject a skills discovery block into the model prompt for both private and published conversations.
5. Serve skill bodies and `references/` on demand via `skills.list` / `skills.read`; materialize `scripts/` and `assets/` into the notebook sandbox at notebook creation (same model as `CodeInterpreter` files).
6. Expose a definition's skills to external agents as MCP resources / orchestrator resources over the published wire API.
7. Round-trip skills through guide export/import and bootstrap seeding.
8. Keep skills out of RAG search by construction.

## 4. Non-goals

1. Replacing `AssistantFile` storage or introducing a skills database migration in phase 1.
2. Indexing skill content into Kernel Memory / vector search.
3. Executing skill scripts server-side automatically when a skill is loaded (scripts are copied to the notebook sandbox at creation; the **model** runs them via existing sandbox/terminal tools when it chooses).
4. Building a public skills marketplace or remote skill registry in the first release.
5. Changing the existing `ClaudeSkillPackService` publish-time export.
6. Supporting every third-party `SKILL.md` extension field; unknown frontmatter is preserved but not necessarily acted upon.

## 5. Current Behavior and Existing Assets

### 5.1 `AssistantFile` already models a file tree

`AssistantFile` is documented as "a binary file resource attached to an assistant/guide … logical folder kind to mirror disk locations." Relevant columns:

| Column | Fit for skills |
|--------|----------------|
| `FolderKind` (`StringLength(32)`) | Add `Skill` as a new bucket value. |
| `RelativePath` (`StringLength(1024)`) | Holds `Skills/<name>/SKILL.md`, `Skills/<name>/references/x.md`, etc. |
| `ContentBytes` / `ContentType` | Skill body / reference / script / asset payload. |
| Unique `(AssistantId, RelativePath)` | Dedupe/upsert per skill file. |
| `Created` / `Updated` | Provenance timestamps. |

### 5.2 `FolderKind` is a pure behavior switch

`DatabaseStorage.BuildToolsArray` / `BuildToolResources` special-case `CodeInterpreter`, `VectorStore`, and skill executable payload:

```text
FolderKind == "CodeInterpreter"  -> adds { type: code_interpreter }
FolderKind == "VectorStore"      -> adds { type: file_search } + vector_store_ids
FolderKind == "Skill" with scripts/ or assets/ -> adds { type: code_interpreter }
(anything else, e.g. HostExtensions, Skill manifest-only) -> no tools, no resources
```

At notebook creation, `NotebookService.CopyGuideFilesToNotebookAsync` copies `CodeInterpreter` files and skill `scripts/`/`assets/` into `Resources/` and projects them into `Output/` via symlinks (see `docs/project-and-notebook-files-system.md` §7.4). `SKILL.md` and `references/` are not copied.

### 5.3 The indexing guardrail already exists

Every markdown-shadow / extraction enqueue site is gated on `FolderKind == "VectorStore"` (`GuidesService` create-guide, create-assistant, and file-save paths; shadows are only read back for VectorStore files). A `Skill` file therefore never gets a shadow row, never enqueues `ExtractAssistantFileMarkdownJob`, and never reaches Kernel Memory. No new "do-not-index" flag is required.

### 5.4 External clients already speak skills over the wire

`CodexTrace.md` shows the Codex CLI:
- injecting `<skills_instructions>` as a leading `developer` message (preserved by `ConversationHistoryBuilder.SplitPublishedClientPrefix`), and
- declaring resource tools (`list_mcp_resources`, `read_mcp_resource`) that GuideAnts round-trips via `pending_client_tool`.

The Codex skill contract defines four source locators: `file`, `environment resource`, **`orchestrator resource`** (accessed via `skills.list` / `skills.read`), and `custom resource`. GuideAnts can act as the **orchestrator**, serving definition-level skills as orchestrator resources.

### 5.5 Existing SKILL.md precedent

`ClaudeSkillPackService` + `Templates/ClaudeSkill/SKILL.md.template` already emit a `SKILL.md` (in the Claude-Code slash-command dialect: `allowed-tools`, `argument-hint`, `$ARGUMENTS`). This is export-only and uses a different dialect than the canonical import format chosen here.

## 6. Proposed Product Model

Introduce a user-facing **Skill** concept on assistant/guide definitions. A skill is an authoring wrapper around a `Skills/<name>/` file group whose manifest is `SKILL.md`.

| Concept | Meaning |
|---------|---------|
| Skill | A `SKILL.md` + optional `references/`, `scripts/`, `assets/`, stored as `Skill` `AssistantFile` rows under `Skills/<name>/`. |
| Skill descriptor | The tier-1 projection (`name`, `description`, locator, gating) computed at materialization and injected into the prompt. |
| Skill body | The markdown after frontmatter, loaded on demand via `skills.read`. |
| Skill script/asset payload | `scripts/` and `assets/` files, stored as `Skill` `AssistantFile` rows and **materialized into the notebook** `Resources/` + `Output/` at notebook creation for sandbox execution. |
| Skill locator | `skill://<assistantId>/<skill-name>` — an orchestrator-resource identifier, not a filesystem path. |

### 6.1 Dialect decision

Adopt the **agentskills.io / Anthropic** dialect as canonical for import and storage:

```yaml
---
name: pptx-author
description: "Build export-ready PowerPoint decks from an outline."
version: 1.0.0
author: GuideAnts
license: MIT
platforms: [linux, macos, windows]
metadata:
  guideants:
    tags: [presentation, pptx, slides]
    requires_toolsets: [sandbox]
    enabled: true
    display_order: 10
---
```

Recognize (but do not require) the hermes `metadata.hermes.*` block and the Claude-Code `allowed-tools` / `argument-hint` fields on import so third-party skills round-trip.

## 7. Storage Model

Phase 1 uses `AssistantFile` with `FolderKind = "Skill"`:

```text
AssistantFile
- AssistantId
- FolderKind      = "Skill"
- RelativePath    = "Skills/<skill-name>/SKILL.md"
                  | "Skills/<skill-name>/references/<file>"
                  | "Skills/<skill-name>/scripts/<file>"
                  | "Skills/<skill-name>/assets/<file>"
- ContentBytes    = file payload
- ContentType     = "text/markdown" | script/asset media type
```

Rules:
1. A skill is the set of `Skill` rows sharing a `Skills/<name>/` prefix.
2. A skill is valid only if it contains a `Skills/<name>/SKILL.md` row with `name` + `description` frontmatter.
3. `VectorStoreName` is unused for skills (remains null).
4. Enable/disable and ordering live in frontmatter (`metadata.guideants.enabled`, `display_order`) in phase 1.

### 7.1 Optional later cleanup (not phase 1)

If listing performance or UI toggles require it, add a metadata-only sidecar (never bodies), analogous to `AssistantFileMarkdownShadow`:

```text
AssistantSkillMeta
- AssistantId
- SkillName
- Description
- Enabled
- DisplayOrder
- ContentHash
```

## 8. Skill Package Format and Frontmatter Contract

The importer/materializer reads a minimal frontmatter contract:

| Field | Required | Runtime use |
|-------|----------|-------------|
| `name` | yes | Tier-1 discovery id; locator; uniqueness within an assistant. |
| `description` | yes | Tier-1 discovery text injected into the prompt. |
| `metadata.guideants.requires_toolsets` | no | Offer-time hide when a required toolset is absent. |
| `metadata.guideants.requires_tools` | no | Offer-time hide when a required tool is absent. |
| `metadata.guideants.fallback_for_toolsets` / `fallback_for_tools` | no | Offer-time hide when the primary tool/toolset IS present. |
| `metadata.guideants.enabled` | no (default true) | Exclude from materialization when false. |
| `metadata.guideants.display_order` | no | Ordering in discovery list. |
| `platforms` | no | Visibility filter. |
| `version`, `author`, `license`, `tags` | no | Catalog/provenance only. |

Body: everything after the closing `---` is the skill body served by `skills.read`.

## 9. Materialization

Add a grouping helper to `DatabaseStorage`, sibling to `BuildToolsArray` / `BuildToolResources`, that projects `Skill` files into tier-1 descriptors. Bodies are never embedded in the definition.

```csharp
private static List<SkillDescriptor> BuildSkills(Assistant assistant)
{
    var skillFiles = assistant.Files.Where(f => f.FolderKind == "Skill").ToList();
    if (skillFiles.Count == 0) return new();

    var descriptors = new List<SkillDescriptor>();

    foreach (var group in skillFiles
                 .GroupBy(f => SkillFolderKey(f.RelativePath))   // "Skills/<name>"
                 .Where(g => g.Key is not null))
    {
        var manifest = group.FirstOrDefault(f =>
            f.RelativePath.EndsWith("/SKILL.md", StringComparison.OrdinalIgnoreCase));
        if (manifest?.ContentBytes is null) continue;

        var fm = SkillFrontmatter.Parse(Encoding.UTF8.GetString(manifest.ContentBytes));
        if (string.IsNullOrWhiteSpace(fm.Name) ||
            string.IsNullOrWhiteSpace(fm.Description) ||
            !fm.Enabled) continue;

        descriptors.Add(new SkillDescriptor
        {
            Name             = fm.Name,
            Description      = fm.Description,
            FolderPath       = group.Key!,                       // "Skills/<name>"
            Locator          = $"skill://{assistant.Id}/{fm.Name}",
            RequiresTools    = fm.RequiresTools,
            RequiresToolsets = fm.RequiresToolsets,
            FallbackForTools = fm.FallbackForTools,
            Platforms        = fm.Platforms,
            DisplayOrder     = fm.DisplayOrder,
            Files            = group.Select(f => f.RelativePath).OrderBy(p => p).ToList(),
        });
    }

    return descriptors
        .OrderBy(d => d.DisplayOrder)
        .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
        .ToList();
}

private static string? SkillFolderKey(string relativePath)
{
    var parts = relativePath.Split('/');
    return parts.Length >= 2 && parts[0].Equals("Skills", StringComparison.OrdinalIgnoreCase)
        ? $"{parts[0]}/{parts[1]}"
        : null;
}
```

Attach descriptors (only) to the runtime definition:

```csharp
// AssistantDefinition.cs
[JsonPropertyName("skills")]
public List<SkillDescriptor>? Skills { get; set; }
```

`SkillDescriptor` contract:

```csharp
public sealed class SkillDescriptor
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string FolderPath { get; set; } = "";   // "Skills/<name>"
    public string Locator { get; set; } = "";       // "skill://<assistantId>/<name>"
    public List<string>? RequiresTools { get; set; }
    public List<string>? RequiresToolsets { get; set; }
    public List<string>? FallbackForTools { get; set; }
    public List<string>? Platforms { get; set; }
    public int DisplayOrder { get; set; }
    public List<string> Files { get; set; } = new(); // relative paths, no bytes
}
```

## 10. Inference-time Injection

### 10.1 Discovery block

`ConversationHistoryBuilder.BuildPublishedMessagesForAssistantAsync` (and the private-notebook sibling `PrepareMessagesForAssistantAsync`) read `def.Skills`, apply the visibility filter (Section 14), and emit a leading `developer` (preferred) or `system` message. The block mirrors the Codex/hermes format and uses orchestrator-resource locators:

```text
## Skills
A skill is a set of instructions provided through a SKILL.md source. If the task
matches a skill's description, call skills.read on its locator and follow it
before acting.

### Available skills
- pptx-author: Build export-ready PowerPoint decks from an outline. (orchestrator resource: skill://<assistantId>/pptx-author)
- <name>: <description> (orchestrator resource: <locator>)

### How to use skills
- Call skills.list to see available skills for this assistant.
- Call skills.read with a locator (and optional file_path for references/foo.md) to load the full instructions.
- Only proceed without loading a skill if none are relevant.
```

Only `name` + `description` + locator are injected. This keeps prompt cost bounded regardless of skill body size.

### 10.2 On-demand tools

Register two server-handled local tools (`ActionType.LocalFunction` in `ThreadRun.DoToolCalls`, like `SearchAssistantFiles`):

`skills.list`

```json
{
  "name": "skills.list",
  "description": "List the skills available to this assistant (name, description, locator, files).",
  "parameters": { "type": "object", "properties": {}, "required": [] }
}
```

Returns the filtered descriptors and their `Files` inventory (no bytes).

`skills.read`

```json
{
  "name": "skills.read",
  "description": "Read a skill's SKILL.md body, or a specific reference/script/asset file.",
  "parameters": {
    "type": "object",
    "properties": {
      "locator":   { "type": "string", "description": "skill://<assistantId>/<name>" },
      "file_path": { "type": "string", "description": "Optional path within the skill, e.g. references/foo.md" }
    },
    "required": ["locator"]
  }
}
```

Resolution: parse `locator` -> `assistantId` + skill name -> `FolderPath`; load the `AssistantFile` row where `RelativePath == FolderPath + "/" + (file_path ?? "SKILL.md")`; return `ContentBytes` as text. Enforce that the resolved path stays under `FolderPath` (path-safety), rejecting `..` traversal.

Because both tools execute inside `ThreadRun` between model rounds, they stay opaque on the wire (no `pending_client_tool`), and GuideAnts' own conversations get skills without any client changes.

## 11. Published Wire API and MCP Distribution

Two complementary modes:

### 11.1 Mode A — server-resolved skills (default)

The discovery block and `skills.list` / `skills.read` are injected/executed server-side. Any wire client (OpenAI `/chat/completions` and `/responses`, Anthropic `/messages`) automatically operates against the guide's skills with zero client changes. This also fills the `/api/published/guides/{pubId}/invoke` gap, which does not accept client tools/messages.

### 11.2 Mode B — orchestrator resources over MCP

Surface each definition skill as an MCP resource on the existing `/api/published/mcp` endpoint:

| Resource URI | Content |
|--------------|---------|
| `skill://<guide>/<name>` | `SKILL.md` body |
| `skill://<guide>/<name>/references/<path>` | reference file |

A client's own `list_mcp_resources` / `read_mcp_resource` (exactly the tools in `CodexTrace.md`) then resolve against GuideAnts skills, making GuideAnts a drop-in skills provider for the MCP-resource ecosystem.

Both modes reuse existing infrastructure and avoid touching the client-tool passthrough path.

## 12. Guide Builder UX

Add a **Skills** tab to `BaseEntityEditor`, parallel to Tools and Files (`EditorTabs.tsx`).

Skill list card shows:
- Skill name and description.
- Source badge: Imported / Authored / Bootstrap.
- Enabled toggle and display order.
- Gating summary (required toolsets/tools) and whether they are satisfied by the current assistant.
- Reference/script/asset file count.

Add actions:
1. **Import SKILL.md** — drag a folder or zip; parse frontmatter + body + `references/`/`scripts/`/`assets/`; write `Skill` `AssistantFile` rows.
2. **Author new skill** — form for `name`, `description`, body (Lexical markdown), and optional reference files; writes `SKILL.md`.
3. **Create assistant from skill(s)** — an import entry point (Section 12.1).

Reuse the Files-tab upload plumbing for reference/script/asset payloads.

### 12.1 Create assistant from skills

Given one or more `SKILL.md` packages:
1. Seed a new `Assistant`: `Name` / `Description` / `Instructions` from the primary skill (or a thin wrapper instruction).
2. Attach the parsed skills as `Skill` `AssistantFile` rows.
3. Map declared `requires_toolsets` / `requires_tools` to concrete GuideAnts tools via an explicit mapping table (e.g. `sandbox`/`terminal` -> code interpreter / sandbox tool source; `web` -> `WebSearch` / `ReadWeb`) so the assistant actually has the capabilities its skills assume.

The mapping table is the one genuinely new authoring artifact; keep it explicit and small.

## 13. DTO and Service Wiring

1. Extend `FormData` in `BaseEntityEditor.tsx` with `skills: AssistantSkillDto[]`.
2. Add `AssistantSkillDto` (name, description, enabled, displayOrder, files[]) to the guide/assistant DTOs (`Models/Guides/GuideDto.cs`, `AssistantDto.cs`).
3. Extend `CreateGuideDto` / `UpdateGuideDto` (and assistant equivalents) and `GuidesService` create/update to persist `Skill` files, mirroring how VectorStore/CodeInterpreter files are handled today.
4. Add a `SkillPackageParser` / `ISkillImportService` in the API layer for frontmatter + package parsing (used by import and by "create from skill").

## 14. Tool Gating Rules

Offer-time only, applied when building the discovery block and `skills.list` output. Never load-time; `skills.read` on an explicit locator always works.

| Field | Hide the skill when |
|-------|---------------------|
| `requires_toolsets` | ANY listed toolset is NOT available to the assistant. |
| `requires_tools` | ANY listed tool is NOT available to the assistant. |
| `fallback_for_toolsets` | ANY listed toolset IS available. |
| `fallback_for_tools` | ANY listed tool IS available. |
| `platforms` | Current platform not in the list. |

"Available" is derived from the assistant's actually-attached tools (global tools + tool sources). Gating never injects tools or fabricates capability; it is an observable visibility decision.

## 15. Export/Import and Bootstrap Format

1. **Export** — `GuideExportImportService.WriteAssistantFilesAsync` already writes any non-VectorStore/CodeInterpreter file at its raw `RelativePath` via the `else` branch, so `Skills/<name>/...` exports for free.
2. **Import** — add a `Skills/` loop to the importer (parallel to the existing `VectorStores/` and `CodeInterpreter/` loops) that creates `FolderKind = "Skill"` rows preserving the relative path.
3. **Bootstrap** — seeded guides can ship curated skills under `Resources/bootstrap/guides/<guide>/Skills/<name>/SKILL.md`, imported by the existing seeder.

## 16. Validation Rules

1. A skill folder must contain exactly one `SKILL.md`.
2. `SKILL.md` frontmatter must have `name` and `description`.
3. `name` must be unique within an assistant and safe for a locator segment.
4. Reference/script/asset paths must stay under `Skills/<name>/` (reject `..`).
5. `requires_*` values should reference known toolset/tool identifiers; unknown values warn but do not block (they simply never satisfy).
6. Total skill payload per assistant should respect existing file-size limits used for `AssistantFile`.

## 17. Trace and Observability

Extend `TurnTraceToolDefinition.Source` (currently `guide` | `client`) with `skills` so `skills.list` / `skills.read` calls and the discovery block are auditable in the prompt trace exactly like the `CodexTrace.md` capture.

## 18. Migration Plan

### Phase 1: Storage + runtime (own conversations)

1. Add `Skill` as a valid `FolderKind` value and the `Skills/<name>/` convention.
2. Add `SkillFrontmatter` parser and `SkillDescriptor`; add `BuildSkills` to `DatabaseStorage`; add `AssistantDefinition.Skills`.
3. Add `skills.list` / `skills.read` server tools in `ThreadRun`.
4. Inject the discovery block in `ConversationHistoryBuilder` (private + published assembly).
5. Confirm the VectorStore-only indexing guardrail is unaffected (add a regression test asserting `Skill` files create no shadow).

### Phase 2: Guide Builder authoring

1. Add the Skills tab, import SKILL.md, author-new-skill.
2. Extend DTOs and `GuidesService`; add `SkillPackageParser` / `ISkillImportService`.
3. Add "create assistant from skill(s)" with the toolset mapping table.

### Phase 3: Export/import + bootstrap

1. Add the `Skills/` import loop to `GuideExportImportService`.
2. Ship one curated bootstrap skill and verify round-trip.

### Phase 4: Wire + MCP distribution

1. Surface skills as MCP resources on `/api/published/mcp`.
2. Verify Codex/Claude clients resolve `orchestrator resource` locators.
3. Add `skills` source tagging to prompt traces.

### Phase 5: Optional metadata sidecar

1. Add `AssistantSkillMeta` only if listing/toggle performance requires it.
2. Keep `AssistantFile` as the canonical body store.

## 19. Acceptance Criteria

1. A user can import a `SKILL.md` package into a guide or assistant without editing storage rows.
2. A user can create a new assistant by importing one or more skills, and the assistant receives the capabilities its skills require.
3. During a GuideAnts conversation, the model sees a skills discovery block and can load bodies via `skills.read`; skill `scripts/` and `assets/` are on disk in the notebook sandbox after notebook creation.
4. A published wire client (OpenAI or Anthropic shape) benefits from server-resolved skills with no client changes.
5. Skill files never create markdown shadows and never appear in `file_search` results.
6. Skills round-trip through guide export/import and bootstrap seeding.
7. Gating hides a skill whose required toolset is missing, and `skills.read` on an explicit locator still works.
8. The prompt trace shows skill tool calls tagged with a `skills` source.
9. Existing guides, files, tools, and the `ClaudeSkillPackService` export are unaffected.

## 20. Open Questions

1. Locator scheme: `skill://<assistantId>/<name>` vs a guide-scoped `skill://<guide>/<name>` for wire/MCP exposure — do we need both an internal and a published form?
2. Should `skills.list` / `skills.read` be always-on for skill-bearing assistants, or gated behind an assistant/tool flag?
3. Toolset mapping table: where does it live (config, code, or a small seed table), and who maintains it?
4. Do we accept the Claude-Code slash-command dialect (`allowed-tools`, `$ARGUMENTS`) on import, or normalize everything to the agentskills.io dialect?
5. ~~Should scripts/assets be downloadable to the sandbox automatically when a skill is loaded, or only when the model explicitly acts?~~ **Resolved (S11):** `scripts/` and `assets/` copy to `Resources/` + `Output/` symlinks at **notebook creation** (not on `skills.read`). The host does not execute them; the model runs them via sandbox/terminal tools. `SKILL.md` and `references/` stay on-demand via `skills.read`.
6. Enable/disable + ordering: keep in frontmatter (phase 1) or promote to the sidecar earlier for a smoother UI?
7. Size/quantity limits per assistant for skills, independent of vector-store file limits?

## 21. Recommended First Slice

The smallest slice that makes skills real end-to-end for GuideAnts' own conversations:

1. Add `FolderKind = "Skill"` and the `Skills/<name>/` convention.
2. Add `SkillFrontmatter` parse + `BuildSkills` + `AssistantDefinition.Skills`.
3. Add `skills.list` / `skills.read` server tools.
4. Inject the discovery block in `ConversationHistoryBuilder`.
5. Add a minimal Skills tab with import-SKILL.md only.
6. Ship one bootstrap skill and a regression test proving `Skill` files are never indexed.

This keeps storage, indexing, and the wire runtime intact while giving assistants real, progressively-disclosed skills, with wire/MCP distribution and richer authoring following in later phases.
