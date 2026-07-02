# Skills Support — Locked Decisions (single source of truth)

Last updated: 2026-07-01
Status: LOCKED (S7 and S13 confirmed with recommended defaults during pre-flight)

This file freezes the design decisions from
[`../skills-support-proposal.md`](../skills-support-proposal.md) §2 (firm decisions) and
resolves §20 (open questions) before implementation starts.

Rules:

- If a decision below is `UNDECIDED`, any phase listed under "Blocks" is blocked.
- Changing a locked decision after a phase ships requires reverting and re-dispatching the
  impacted phases (see `00-orchestration.md` §5).
- Subagents must not reinterpret values in this file. The proposal is context; this file is
  the contract.

---

## Part A — Locked decisions (S1–S13)

| ID | Decision | Resolved value | Blocks |
|----|----------|----------------|--------|
| S1 | Storage model | `Skill` files are `AssistantFile` rows with `FolderKind = "Skill"` under a `Skills/<skill-name>/...` relative-path convention. **No new tables in Phases 1–4.** | 1,2,3 |
| S2 | Canonical dialect | agentskills.io / Anthropic `SKILL.md` (YAML frontmatter + body + optional `references/`/`scripts/`/`assets/`). | 1,2 |
| S3 | Progressive disclosure | Mandatory. Only `name` + `description` + locator injected at prompt time; bodies/reference files load on demand. **No skill body in `AssistantDefinition`.** | 1,4 |
| S4 | On-demand loading | Server-handled `skills.list` / `skills.read` tools in `ThreadRun` (`ActionType.LocalFunction`), executed between rounds. **Opaque on the wire — never `pending_client_tool`.** | 1,4 |
| S5 | No indexing | A `Skill` file is inert to the vector-store / Kernel Memory pipeline: never shadowed, never enqueued for extraction, never in `file_search`. | 1,2,3 |
| S6 | Tool gating | Offer-time only. `requires_toolsets`/`requires_tools`/`fallback_for_*`/`platforms` **hide** a skill from discovery when unmet. `skills.read` on an explicit locator always works. Gating **never** injects tools or fabricates capability. | 1,2 |
| S7 | Locator scheme | Internal: `skill://<assistantId>/<name>`. Published/MCP exposure: `skill://<guide>/<name>` (+ `/references/<path>`). **Recommended; confirm before Phase 4.** | 1,4 |
| S8 | Tool availability | `skills.list`/`skills.read` are auto-enabled for any assistant with ≥1 enabled `Skill`; they are not a separately-toggled catalog tool. | 1 |
| S9 | Toolset mapping | `requires_toolsets`/`requires_tools` → concrete GuideAnts tools via a small **explicit** static mapping maintained in code inside the import service (e.g. `sandbox`/`terminal` → code interpreter / sandbox source; `web` → `WebSearch`/`ReadWeb`). No implicit guessing. | 2 |
| S10 | Import normalization | Accept agentskills.io, hermes `metadata.hermes.*`, and Claude-Code (`allowed-tools`/`argument-hint`/`$ARGUMENTS`) on import. Normalize to the canonical dialect for discovery; **preserve the full original `SKILL.md` verbatim** in storage so nothing is lost. | 1,2 |
| S11 | Scripts/assets | Copied to notebook `Resources/` (+ `Output/` symlinks) at notebook creation, same as CodeInterpreter files. `SKILL.md` and `references/` stay on-demand via `skills.read`. **Not** auto-executed by the host; the model runs scripts via existing sandbox/terminal tools. | 1,2 |
| S12 | Enable/order source | Phase 1: frontmatter (`metadata.guideants.enabled` default true, `display_order`). Sidecar (`AssistantSkillMeta`) deferred to Phase 5 and only if perf requires. | 1,5 |
| S13 | Limits | Reuse existing `AssistantFile` size limits. Per-assistant cap: **50 skills**, `SKILL.md` ≤ **100,000 chars** (matches hermes). **Recommended; confirm before Phase 1.** | 1,2 |

---

## Part B — Frozen invariants (not open for reinterpretation)

From proposal §2 and the user's standing rules:

- **`AssistantFile` is the canonical skill body store.** No skill body/bytes are serialized
  into `AssistantDefinition`; the definition carries tier-1 `SkillDescriptor`s only.
- **`FolderKind` classification only.** A `Skill` file is distinguished solely by
  `FolderKind = "Skill"` + the `Skills/<name>/` path. It adds no tools and no
  `tool_resources`; it is inert to `BuildToolsArray`/`BuildToolResources`.
- **No indexing of skills.** The markdown-shadow / extraction / Kernel Memory path is gated
  on `FolderKind == "VectorStore"`; that gating must remain, and a `Skill` file must never
  reach it. A regression test proves this.
- **Progressive disclosure is architectural.** The prompt receives names + descriptions +
  locators; `skills.read` streams bodies/reference files on demand.
- **`skills.read` path-safety.** The resolved file path must stay under `Skills/<name>/`;
  `..` and absolute paths are rejected. No traversal, ever.
- **No fallback masking** (user rule: *fallback is a bug generator*). No silent `catch {}`,
  no "assume the skill is valid on parse failure", no capability fabrication when a required
  toolset is absent — gating **hides**, it does not **inject**.
- **Server-side resolution, opaque on wire.** `skills.list`/`skills.read` run inside
  `ThreadRun` between rounds and never surface `pending_client_tool`.
- **One materialization choke point.** Skill descriptors are produced only by
  `DatabaseStorage.BuildSkills`; consumers read the materialized definition.
- **Notebook payload choke point.** Skill `scripts/` and `assets/` copy into the notebook
  only in `NotebookService.CopyGuideFilesToNotebookAsync` via `SkillNotebookMaterializer`
  (same timing and visibility model as `CodeInterpreter` files). `SKILL.md` and `references/`
  are not copied.
- **`ClaudeSkillPackService` export is untouched.** Definition-time skills are a separate,
  additive path; the publish-time Claude skill pack stays as-is.
- **One published runtime.** Wire endpoints stay protocol adapters over
  `SendMessageStreamAsync`; no skills-specific orchestration is added to wire handlers.

---

## Part C — Decision ledger

| ID | Decision | Status | Resolved value | Date |
|----|----------|--------|----------------|------|
| S1 | Storage model | LOCKED | `AssistantFile` `FolderKind="Skill"`, `Skills/<name>/` | 2026-07-01 |
| S2 | Canonical dialect | LOCKED | agentskills.io / Anthropic `SKILL.md` | 2026-07-01 |
| S3 | Progressive disclosure | LOCKED | tier-1 only injected; no body in definition | 2026-07-01 |
| S4 | On-demand loading | LOCKED | server-handled `skills.list`/`skills.read`; opaque | 2026-07-01 |
| S5 | No indexing | LOCKED | `Skill` inert to shadow/index/file_search | 2026-07-01 |
| S6 | Tool gating | LOCKED | offer-time hide only; no injection | 2026-07-01 |
| S7 | Locator scheme | LOCKED (recommended) | `skill://<assistantId>/<name>`; published `skill://<guide>/<name>` | 2026-07-01 |
| S8 | Tool availability | LOCKED | auto-enabled for skill-bearing assistants | 2026-07-01 |
| S9 | Toolset mapping | LOCKED | explicit static map in import service | 2026-07-01 |
| S10 | Import normalization | LOCKED | accept 3 dialects; preserve original verbatim | 2026-07-01 |
| S11 | Scripts/assets notebook materialization | LOCKED | scripts/assets copied at notebook create; SKILL.md/references on-demand | 2026-07-01 |
| S12 | Enable/order source | LOCKED | frontmatter phase 1; sidecar phase 5 | 2026-07-01 |
| S13 | Limits | LOCKED (recommended) | 50 skills/assistant; `SKILL.md` ≤ 100k chars | 2026-07-01 |
