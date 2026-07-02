# UI Gate — Guide Builder Skills Tab (Skills Support)

Companion to `00-orchestration.md`. Run after Phase 2.

This is the concrete UX contract for the **Guide Builder Skills tab** required by proposal
§12–§13. It covers **only the Guide Builder authoring surface** (the Electron/React editor
under `src/client/src/components/guides/editor/`). The notebook chat consumer is **out of
scope** — once server-side skills work (Phase 1), chat rendering follows.

---

## 1. Gate intent

Pass when all are true:

- An author can **import** a `SKILL.md` package (folder or zip), **author** a new skill, and
  **create an assistant from skill(s)** — all without hand-editing storage rows.
- The Skills tab shows, per skill, whether its declared `requires_*` prerequisites are
  **satisfied by the current assistant's tools**, honestly (a gated skill is shown as
  "won't be offered", not hidden as if broken, in the authoring view).
- Enable/disable + ordering are editable and persist (frontmatter-backed in phase 1).
- The tab **reuses** existing upload, dialog, and toast primitives and respects the
  one-component-per-file / ≤120-char decomposition rule.

---

## 2. Required UI contract

### 2.1 Skills tab entry

- A `skills` tab is added in `EditorTabs.tsx`, visible for both guides and assistants
  (parallel to Tools and Files). `BaseEntityEditor` `FormData` gains
  `skills: AssistantSkillDto[]`.
- Empty state: explains what a skill is and offers **Import SKILL.md** and **Author skill**.

### 2.2 Skill list / card

Each skill card shows:

- Skill **name** + **description**.
- **Source badge:** `Imported` / `Authored` / `Bootstrap`.
- **Enabled** toggle + **display order** control.
- **Gating summary:** required toolsets/tools and a satisfied/unsatisfied indicator computed
  against the assistant's currently-selected tools. Unsatisfied → "Will not be offered to
  the model until <tool> is added" (honest, not hidden, not auto-added).
- **Files:** count of `references/`/`scripts/`/`assets/` with a way to view them.

### 2.3 Import SKILL.md

- Accept a folder drop or `.zip`. Parse frontmatter + body + support dirs.
- Reuse the **Files-tab upload plumbing** to carry the payload; write `Skill`
  `AssistantFile` rows under `Skills/<name>/` on save.
- Show parse errors inline (missing `name`/`description`, no `SKILL.md`) — explicit, not
  swallowed.
- Accept agentskills.io, hermes, and Claude-Code dialects (S10); display the normalized
  name/description while preserving the original file.

### 2.4 Author new skill

- Form: `name`, `description`, body (reuse the existing Lexical markdown editor used for
  Instructions), optional reference files. Writes a canonical `SKILL.md`.

### 2.5 Create assistant from skill(s)

- Entry point (from the Skills tab or guides dashboard) that takes one or more `SKILL.md`
  packages and creates a new assistant: seeds name/description/instructions, attaches the
  skills, and maps `requires_toolsets`/`requires_tools` to concrete GuideAnts tools via the
  **explicit** mapping (S9). Show the user which tools were added and why. Never silently
  guess.

### 2.6 Loading / empty / error / accessibility / responsive

- Keep the established Tool Sources / Files contract: explicit loading/empty/error/retry,
  `role="alert"` / `aria-live="polite"` for validation, keyboard reachability, focus
  management in any modal, single-column mobile flow with footer actions visible.

---

## 3. Reuse existing mechanisms (do not reinvent)

| Concern | Reuse this (path) | Rule |
|---|---|---|
| File upload | The existing Files-tab upload path (`Files.tsx` + `FileUploadDto` plumbing) | Skill payload uploads through the same mechanism with `FolderKind="Skill"`. Do **not** add a second uploader. |
| Confirm dialog (create-from-skill, delete) | `src/client/src/components/common/ConfirmationDialog.tsx` | Use for confirmations; do not hand-roll an overlay. |
| Toasts | `.../common/Toast.tsx` (`useToast`) | Import/create success + errors via toast, as the editor already does. |
| Markdown editor | The Lexical editor used by the Instructions sub-tab | Reuse for skill body authoring. |
| Loading / empty | `.../LoadingSpinner.tsx`, `.../guides/EmptyState.tsx`, inline `FaSpinner` | Use existing patterns. |
| Chips / badges | The existing card view-model chip pattern (`inline-flex px-2 py-0.5 rounded text-xs font-medium`) | Add source/gating badges as new className helpers in a view-model, matching the pattern. |

Introducing a duplicate uploader, dialog, toast, or spinner is an automatic FAIL.

---

## 4. Component decomposition contract (anti-monolith)

`project-rules.mdc` mandates one component per file and ≤120-char lines. Required split
(names indicative; follow existing `editor/` conventions):

| File | Kind | Responsibility |
|---|---|---|
| `skills/SkillsTab.tsx` *(new)* | presentational shell | Thin composition; no business logic. |
| `skills/SkillList.tsx` / `SkillCard.tsx` *(new)* | presentational | List + card (badges, gating summary, enable/order). |
| `skills/ImportSkillDialog.tsx` *(new)* | presentational | Import flow, reuses upload plumbing + `ConfirmationDialog`. |
| `skills/AuthorSkillEditor.tsx` *(new)* | presentational | Author form, reuses the Lexical editor. |
| `skills/skillFrontmatter.ts` *(new)* | pure helper | Parse/normalize frontmatter (3 dialects); unit-tested. `No JSX`. |
| `skills/skillGating.ts` *(new)* | pure helper | Compute satisfied/unsatisfied against selected tools; unit-tested. |
| `skills/skillCardViewModel.ts` *(new)* | view-model | Source/gating badge className helpers (pure). |
| `skills/useSkillImport.ts` *(new hook)* | side-effect hook | Parse + stage import; calls the upload/save path. |

Hard rules (gate-enforced):

- **No business logic in JSX/effects**: parsing, dialect normalization, and gating
  computation live in `*.ts` helpers with `__tests__/` coverage.
- No new presentational `.tsx` exceeds ~250 lines.
- Reuse §3 primitives; a duplicate dialog/uploader/toast is an automatic FAIL.

---

## 5. Phase gate checks (Phase 2)

- [ ] Skills tab present for guides + assistants; `FormData.skills` wired; save round-trips.
- [ ] Import SKILL.md (folder + zip) parses + persists `Skill` rows; parse errors explicit.
- [ ] Author new skill writes a valid `SKILL.md`.
- [ ] Create-from-skill seeds an assistant, attaches skills, applies the mapping (S9) with a
      visible summary; no silent tool guessing.
- [ ] Gating summary reflects the assistant's selected tools honestly.
- [ ] Enable/disable + ordering persist.
- [ ] **Reuse (§3):** upload/dialog/toast/markdown-editor reused; no duplicate primitive.
- [ ] **Decomposition (§4):** helpers are pure + tested; hook owns side-effects; new `.tsx`
      ≤ ~250 lines; no business logic in JSX.
- [ ] Accessibility + responsive + loading/empty/error.
- [ ] **no-index gate** passes for the UI upload path (skill uploads create no shadow).

---

## 6. Required UI test matrix

**Component/unit:**

- Frontmatter parse for all three dialects; missing `name`/`description` → explicit error.
- Gating computation: satisfied vs unsatisfied for a given tool set.
- Card renders source + gating badges; enable/order edits update state.

**Interaction:**

- Import folder → parsed → saved → skill appears; zip import equivalent.
- Author skill → save → appears with correct tier-1 metadata.
- Create-from-skill → confirm dialog states added tools → new assistant created.
- Keyboard path through tab → import dialog (focus trap + restore).

**Responsive:** mobile single-column; dialog footer reachable.

**Decomposition/reuse (structural — review + grep):**

- Pure helpers have `__tests__/` coverage; hook owns side-effects; grep proves reuse of
  `ConfirmationDialog` / upload path / `useToast` (no new overlay/uploader).

---

## 7. Report-back addition (Phase 2)

```text
UI GATE (Guide Builder Skills):
- Skills tab + FormData.skills round-trip: <pass/fail>
- Import SKILL.md (folder + zip, 3 dialects, explicit errors): <pass/fail>
- Author new skill: <pass/fail>
- Create-from-skill + mapping summary (no silent guess): <pass/fail>
- Gating summary honest vs selected tools: <pass/fail>
- Enable/disable + ordering persist: <pass/fail>
- Reuse (upload/dialog/toast/markdown, no duplicates): <pass/fail + any duplication>
- Decomposition (pure tested helpers, hook, .tsx ≤~250 lines, no logic in JSX): <pass/fail + file map>
- no-index for UI upload path: <pass/fail>
- UI test matrix additions: <paths>
```
