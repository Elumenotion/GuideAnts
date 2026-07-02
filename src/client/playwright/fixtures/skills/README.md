# Skill fixtures for Playwright UI testing

Real-world `SKILL.md` packages vendored for exercising the Guide Builder Skills
tab (import, gating, size/count limits) end to end, plus one hand-authored
invalid fixture. See `docs/skills-execution/ui-testing-plan.md` for the
scenario matrix that uses these.

## Provenance

All skills below except `invalid-missing-name` were copied verbatim (no
content edits) from a local clone of
[`NousResearch/hermes-agent`](https://github.com/NousResearch/hermes-agent)
(MIT License, Copyright (c) 2025 Nous Research). `research-paper-writing` had
its `references/` and `templates/` subfolders removed — the fixture is used
only to test SKILL.md-size-cap rejection, which happens before any other file
in the package is read, so the large LaTeX conference templates it normally
ships with are not needed here.

| Fixture folder | Source path in hermes-agent | Dialect / fields exercised | Test purpose |
|---|---|---|---|
| `arxiv/` | `skills/research/arxiv` | hermes (`metadata.hermes.*`, `related_skills`), has `scripts/` | Baseline "small skill with a script" import |
| `ocr-and-documents/` | `skills/productivity/ocr-and-documents` | hermes, has `scripts/` + an unrecognized `DESCRIPTION.md` | Import preserves files it doesn't understand |
| `research-paper-writing/` | `skills/research/research-paper-writing` | hermes; **`SKILL.md` is 105,111 chars** | Real-world trip of the 100,000-char cap (`SkillFrontmatter.MaxSkillMarkdownChars`) — must be rejected |
| `kanban-video-orchestrator/` | `optional-skills/creative/kanban-video-orchestrator` | hermes; has all three canonical subfolders (`references/`, `scripts/`, `assets/`) together | Full-tree import; confirms scripts/assets are stored but never auto-run/auto-copied (S11) |
| `searxng-search/` | `optional-skills/research/searxng-search` | hermes; `fallback_for_toolsets: [web]` | Gating: GuideAnts maps `web` → `WebSearch`/`ReadWeb` (`SkillToolsetMapping.ts`) — offered only when those tools are **not** enabled (fallback suppressed when web search is available) |
| `docker-management/` | `optional-skills/devops/docker-management` | hermes; `requires_toolsets: [terminal]` | Gating: `terminal` → `code_interpreter` — visible only when the assistant has the sandbox/code-interpreter capability |
| `pptx-author-collision/` | `optional-skills/finance/pptx-author` | agentskills.io-style, Apache-2.0 (adapted from `anthropics/financial-services`); **`name: pptx-author`** | Name collision: same skill `name` as the shipped bootstrap skill at `Resources/bootstrap/guides/pptx-guide/Skills/pptx-author/` |
| `invalid-missing-name/` | Hand-authored (not from hermes-agent) | n/a | Missing required `name` field — import must reject explicitly, no silent partial acceptance |

No fixture here needs network access, secrets, or heavy dependencies to
import — these SKILL.md bodies reference tools like `pip install marker-pdf`
or `curl` but that code is never executed by the importer or by GuideAnts;
skill bodies/scripts are inert payload per S11.
