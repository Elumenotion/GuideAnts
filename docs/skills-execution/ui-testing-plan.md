# Skills Support — Playwright UI Testing Plan

Last updated: 2026-07-01
Status: Approved — execution in progress

This plan defines how the completed Skills feature (see
[`00-orchestration.md`](./00-orchestration.md), [`DECISIONS.md`](./DECISIONS.md),
[`STATUS.md`](./STATUS.md) — all phases `DONE`) gets exercised end to end
through the real Guide Builder UI and chat runtime, using Playwright. It is a
QA pass on top of the existing xUnit/vitest coverage, not a replacement for
it — anything already proven at the unit/API level (path-safety, MCP resource
plumbing) is explicitly out of scope here.

## 1. Resolved decisions

| Decision | Resolution |
|---|---|
| Execution mode | Agentic/exploratory: drive the user's real Chrome via `playwright-cli --extension` (already connected, already authenticated) rather than a scripted, isolated browser. No durable `@playwright/test` suite is being written yet. |
| Fixture location | `src/client/playwright/fixtures/skills/` (vendored, see its `README.md` for provenance/licensing). Chosen over `e2e/` naming; anticipates a future durable Playwright suite under `src/client/playwright/`. |
| Test workspace | A new, dedicated project (**"Skills QA"**) so test artifacts don't mix with real project data. Created fresh, not reused. |
| Chat/runtime backend | The local llama runtime model already configured in this dev environment (no cloud provider calls). |
| First execution step | Started 2026-07-01 — exploratory pass via `playwright-cli --extension` on the **Skills QA** project. See §6 execution log. |

## 2. Fixture inventory

See [`src/client/playwright/fixtures/skills/README.md`](../../src/client/playwright/fixtures/skills/README.md)
for full provenance. Summary:

| Fixture | Purpose |
|---|---|
| `arxiv/` | Baseline small skill, hermes dialect, has `scripts/` |
| `ocr-and-documents/` | hermes dialect, `scripts/` + an unrecognized extra file (`DESCRIPTION.md`) |
| `research-paper-writing/` | `SKILL.md` is 105,111 chars — real trip of the 100,000-char cap |
| `kanban-video-orchestrator/` | Full `references/` + `scripts/` + `assets/` tree in one package |
| `searxng-search/` | `fallback_for_toolsets: [web]` — real gating fixture |
| `docker-management/` | `requires_toolsets: [terminal]` — real gating fixture |
| `pptx-author-collision/` | Same `name: pptx-author` as the shipped bootstrap skill — collision case |
| `invalid-missing-name/` | Hand-authored; missing required `name` field |

Also in scope without any new fixture: the already-shipped bootstrap skill at
`src/server/GuideAntsApi/Resources/bootstrap/guides/pptx-guide/Skills/pptx-author/`.

## 3. Scenario matrix

Each scenario cites the code/DECISIONS item it verifies so a failure maps
directly back to an owner.

### A — Authoring (Guide Builder Skills tab)

| # | Scenario | Verifies | Fixture |
|---|---|---|---|
| A1 | Import a small folder-based skill onto a test assistant; confirm it appears in the Skills tab with correct name/description. | Basic import path, `SkillPackageParser.ParseFolderEntries` | `arxiv/` |
| A2 | Import the same skill as a zip instead of a folder. | `SkillPackageParser.ParseZip` | `arxiv/` (zipped) |
| A3 | Import a skill whose package has `scripts/` + an extra unrecognized file; confirm all files persist as `Skill` `AssistantFile` rows, not just `SKILL.md`. | Whole-tree import is folder-name-agnostic | `ocr-and-documents/` |
| A4 | Import the full `references/`+`scripts/`+`assets/` skill; spot-check that a reference file's content is byte-identical after import; create a notebook and confirm `scripts/`/`assets/` exist under `Resources/Skills/...` with `Output/` symlinks. | S11 (scripts/assets materialized at notebook create; SKILL.md/references on-demand); full subtree persistence | `kanban-video-orchestrator/` |
| A5 | Attempt to import `research-paper-writing/` (105,111 chars). Must be rejected with a clear error, not silently truncated or accepted. | S13 cap; `SkillFrontmatter.MaxSkillMarkdownChars` | `research-paper-writing/` |
| A6 | Attempt to import `invalid-missing-name/`. Must be rejected with a clear error naming the missing field. | No fallback masking (user rule); `SkillFrontmatter.Parse` required-field check | `invalid-missing-name/` |
| A7 | Import `pptx-author-collision/` onto an assistant that already has the bootstrap `pptx-author` skill. Observe exact behavior (reject / rename / overwrite) and confirm it's an explicit, visible outcome — not silent data loss. | Name-collision handling (not explicitly covered by an existing DECISIONS line — this scenario is partly exploratory/discovery) | `pptx-author-collision/` + bootstrap `pptx-author` |
| A8 | Author a brand-new skill from scratch using the Skills tab's own form (no import). | Author-new path | none (UI-entered) |
| A9 | Import `docker-management/` (`requires_toolsets: [terminal]`) onto an assistant **without** sandbox/code-interpreter enabled; confirm the tab shows the prerequisite as unsatisfied. Then enable code interpreter on that assistant and confirm it flips to satisfied. | S6 gating (offer-time hide, no injection); S9 mapping `terminal`→`code_interpreter` | `docker-management/` |
| A10 | Import `searxng-search/` (`fallback_for_toolsets: [web]`) onto an assistant with and without `WebSearch`/`ReadWeb` tools; confirm gating display responds correctly to `fallback_for_toolsets` specifically (distinct code path from `requires_toolsets`). | S6 gating; S9 mapping `web`→`WebSearch`/`ReadWeb` | `searxng-search/` |
| A11 | Use "create assistant from skill(s)" with `docker-management/` selected; confirm the wizard offers to add code-interpreter capability rather than silently fabricating it. | S9; no capability fabrication | `docker-management/` |
| A12 | Toggle a skill disabled; confirm it disappears from the assistant's active/discoverable skill set without deleting the underlying files. | `metadata.guideants.enabled` / sidecar toggle | `arxiv/` |
| A13 | Reorder two skills on one assistant; confirm `display_order` persists after reload. | S12 | `arxiv/`, `ocr-and-documents/` |
| A14 | Add skills up to the 50-skill cap on one assistant (script the repeat-import, not 50 manual clicks), then attempt a 51st; confirm explicit rejection. | S13; `DatabaseStorage.MaxSkillsPerAssistant` / `SkillDtoBuilder` check | `arxiv/` (imported 51× under distinct names) |

### B — Export/import + bootstrap

| # | Scenario | Verifies |
|---|---|---|
| B1 | Export the Skills QA test guide (with 2–3 skills attached), re-import it as a new guide, and diff the resulting skill set/paths/content against the original. | Lossless round-trip (Phase 3 gate) |
| B2 | Confirm the bootstrap `pptx-author` skill is present and editable out of the box on a fresh pptx-guide-derived assistant, with no import step needed. | Bootstrap seeding |

### C — Runtime/chat usage (local llama backend)

| # | Scenario | Verifies |
|---|---|---|
| C1 | Start a conversation with an assistant that has `arxiv/` attached; ask "what skills do you have access to?" and confirm the model can name it (proves the discovery block reached the prompt). | S3 discovery block injection |
| C2 | Ask a question that should trigger the attached skill (e.g., an arXiv search question); check the prompt-trace panel for a `skills.read` call tagged `Source=skills`. | S4 on-demand loading; trace tagging |
| C3 | On an assistant with `docker-management/` attached but gated unsatisfied (no code interpreter), confirm the discovery block does **not** list it, and the model doesn't claim the capability. | S6 gating hides, never fabricates |

Track C assertions are evidence-based (trace panel, discovery block content),
not graded on response quality — local model output isn't asserted verbatim.

### Explicitly out of scope for this Playwright pass

- `skills.read` path-traversal rejection — already covered by
  `SkillToolsTests`/`SkillPathSafety` at the unit level; no UI surface takes a
  raw locator string.
- MCP resource exposure on `/api/published/mcp` (`skill://guide/name`) —
  consumed by an external MCP client, not a browser. Would need a separate
  HTTP/MCP-client-based check, not Playwright.

## 4. Execution protocol

1. Connect to the user's real Chrome via `playwright-cli --extension`
   (already done; session is authenticated).
2. Create the **"Skills QA"** project once, reuse it for every scenario in
   this pass.
3. Within it, create one notebook/guide with a small number of test
   assistants (at least: one plain assistant with no sandbox/web tools, one
   with code interpreter enabled, one with web search enabled) so gating
   scenarios (A9, A10) don't require rebuilding assistants mid-run.
4. Run scenarios in matrix order (A1→A14, then B, then C) since later
   authoring scenarios assume earlier ones' assistants/skills exist.
5. For each scenario: snapshot before, perform the action, snapshot after,
   record pass/fail against the "Verifies" column.
6. Capture failures with a screenshot + snapshot + console/network log
   (`playwright-cli console`, `playwright-cli network`) attached to the
   finding, not just a pass/fail line.

## 5. Open items

- **A7 (name collision)** has no documented expected behavior in
  `DECISIONS.md` — this run will surface actual behavior first; if it's
  undesirable, that becomes a bug report, not a scenario failure.
- Whether any of these scenarios graduate into a durable `@playwright/test`
  suite under `src/client/playwright/` is a decision for after this
  exploratory pass, once we know which assertions are stable enough for CI.

## 6. Execution log

Workspace: project **Skills QA** (`a976c272-931f-47ee-8d92-34a5374a1199`).

| Guide | ID | Purpose |
|---|---|---|
| Skills QA Guide | `aab4c5fd-27bb-4774-a457-6abcfa13c50d` | Main import + runtime tool-calling |
| Skills QA Gating | `c4164aaf-ea21-4940-b88e-e21bb4f6d91a` | `requires_toolsets` + `fallback_for_toolsets` gating |

### Runtime / chat (section C + tool-calling extensions)

| # | Result | Notes |
|---|---|---|
| C1 | PASS | Discovery lists enabled skills; disabled `arxiv` omitted |
| C2 | PASS | `skills.read` on ocr skill; trace `Source=skills` |
| C3 / T19 | PASS | `docker-management` hidden from discovery when terminal tools off |
| T1–T8 | PASS | `skills.list`, progressive `skills.read`, path traversal, disabled skill |
| T13 | PASS | PDF extraction via `ocr-and-documents` script + `[@files]` |
| T20–T22 | PASS | Gated docker skill: empty list → explicit read works → listed after Run Python/Bash enabled |
| T24 | PASS | Disabled `arxiv` not claimed when asked directly |
| T18 runtime | PASS | Web off → `searxng-search` listed; Web on → suppressed (only `docker-management`) |
| T18 / A10 UI | **FAIL → fixed** | UI showed “Prerequisites met” for `searxng-search` while Web Search was on. Root cause: client `computeSkillGating` ignored `fallback_for_toolsets`. Fixed in `skillGating.ts` + DTO wiring; badge now **Suppressed** with explanatory summary. Re-verify in browser after rebuild. |

### Authoring (section A) — partial

| # | Result | Notes |
|---|---|---|
| A9 | PASS | `docker-management` card **Gated** without terminal; **Prerequisites met** after Run Python/Bash |
| A10 | FAIL (pre-fix) | See T18 UI row above |
| A1–A8, A11–A14, B | Not run / deferred | |

### Bugs found during pass

1. **SkillManifestUpdater** corrupted SKILL.md frontmatter on save — removed; sidecar-only sync (`AssistantSkillMetaSync`).
2. **Fallback gating UI gap** — Skills tab did not mirror server `SkillVisibilityFilter` for `fallback_for_toolsets` (fixed this session).
3. **Fixture README** — `searxng-search` gating description was inverted (fixed).
