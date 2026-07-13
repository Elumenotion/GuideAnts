# UI Gate — Guide Builder Tool Execution Limits

Companion to `00-orchestration.md`. Run after Phase 4 and final Phase 7.

This is the concrete UX contract for **Guide Builder** changes required by
[`../tool-call-limits-proposal.md`](../tool-call-limits-proposal.md) §10. It inherits the
reuse and decomposition rules from
[`../skills-execution/ui-gate.md`](../skills-execution/ui-gate.md) §3–§4 and
[`src/.cursor/rules/project-rules.mdc`](../../src/.cursor/rules/project-rules.mdc) (one
component per file, functional React, Tailwind, ≤120-char lines).

> **Scope boundary:** Guide Builder authoring only (`src/client/src/components/guides/editor/`).
> Published-guide **conversation** limits stay on `configTabs/LimitsTab.tsx` inside
> `PublishGuideDialog` — do not add tool-call fields there.

---

## 1. Gate intent

Pass when all are true:

- An author can set **per-assistant tool execution limits** on the **Tools** tab (not publish
  Limits).
- **Crew** tab shows each member's limits; Phase 4 = read-only summary + edit link; Phase 6 =
  editable `max_tool_calls_per_invocation` per member.
- Blank numeric fields mean **unlimited** (`null`/`undefined` in API); never default to `0`.
- Help copy distinguishes **tool calls per turn** from **Max Conversation Turns**
  (`PublishedGuide.maxTurns`).
- The UI **reuses** existing editor primitives and respects decomposition (no monolith, no
  duplicate controls).

---

## 2. Required UI contract

### 2.1 Tools tab — "Execution limits" section

**Placement:** `ToolsTab.tsx` → **Global Tools** sub-tab (`toolsSubTab=global`), below
`ToolsSelector` (same panel as `SandboxWireApiPanel` — use `border-t border-gray-200 pt-6`
section divider like `SandboxWireApiPanel.tsx`).

**Fields:**

| Field | Control | Notes |
|-------|---------|-------|
| Max tool calls per turn | `type="number"` `min={1}` | Blank → unlimited. Proposal §10. |
| Max tool rounds per turn | Same, inside collapsed **Advanced** | Round-trip Phase 4; enforced Phase 6. |

**Help text (required):**

> When reached, the assistant receives a limit message and must finish the turn with gathered
> results. Does not affect published conversation turn limits.

**Do not** reuse labels/copy from `configTabs/LimitsTab.tsx` ("Max Conversation Turns",
"Usage Limits").

### 2.2 Crew tab — member limits

**Phase 4:** Each selected crew member row (or card) shows:

- Member name.
- Read-only: `max_tool_calls_per_turn` from member assistant (or "Unlimited").
- Link/button: **Edit limits** → navigates to that assistant's editor Tools tab (same pattern
  as `SkillsTab.tsx` `useNavigate` to assistant routes).

**Phase 6:** Add editable **Max tool calls per invocation** per `GuideMember` (nullable,
blank = use child assistant default). Persist on guide save via crew member DTO.

### 2.3 `BaseEntityEditor` / `FormData`

- Add limit fields to `FormData` in `BaseEntityEditor.tsx` (Phase 4 wires UI; Phase 1 owns
  types/DTO alignment).
- Load from `getGuide` / `getAssistant` response; include in `createGuide` / `updateGuide`
  payload.
- `normalizeFormValue`: empty string → `undefined` for limit fields (mirror `reasoningEffort`
  empty-string handling).

### 2.4 Loading / empty / error / accessibility / responsive

- Follow `SandboxWireApiPanel` / `GuideCrewManager` patterns: explicit loading text for async
  crew member metadata; `role="alert"` for validation errors on invalid integers (negative).
- Labels use `htmlFor` + `id` (see `LimitsTab.tsx` number fields).
- Advanced section: keyboard-reachable toggle (`<button type="button">` or `<details>`).
- Single-column layout on mobile; inputs `w-full` like existing editor fields.

---

## 3. Reuse existing mechanisms (do not reinvent)

| Concern | Reuse this (path) | Rule |
|---------|-------------------|------|
| Nullable number input | `configTabs/LimitsTab.tsx` (`maxTurns`, `maxUserMessageLength`) | Same `value={n \|\| ''}`, `onChange` → `parseInt` or `undefined`, `placeholder="No limit"`, input classes `w-full px-3 py-2 border border-gray-300 rounded-md focus:ring-2 focus:ring-blue-500`. **Copy pattern, not component** — limits are not publish config. |
| Section divider below tools | `SandboxWireApiPanel.tsx` | `mt-6 border-t border-gray-200 pt-6 space-y-5` under Global Tools. |
| Crew list / badges | `guideEditor/GuideCrewManager.tsx` | Chip classes: `text-xs px-2 py-0.5 rounded`, global/custom badge colors. Extend selected-member row; do not replace crew manager. |
| Navigate to assistant | `skills/SkillsTab.tsx` | `useNavigate` to assistant editor route for "Edit limits" link. |
| Toasts | `components/common/Toast.tsx` (`useToast`) | Client-side validation errors only; save errors use existing editor flow. |
| Loading | `components/common/LoadingSpinner.tsx` or inline "Loading…" | Same as `GuideCrewManager` / `SandboxWireApiPanel`. |
| Save / dirty | `BaseEntityEditor.tsx` `updateForm` + `onDirtyChange` | Limit edits call `onDirtyChange` like other Tools tab fields. |

**Forbidden:**

- Adding tool-call limit fields to `configTabs/LimitsTab.tsx` or `PublishGuideDialog`.
- Duplicating publish limit controls inside the assistant editor.
- A second number-input component when the LimitsTab markup pattern suffices.
- Hand-rolled modal/toast/spinner primitives.

---

## 4. Component decomposition contract (anti-monolith)

`project-rules.mdc` + skills §4 apply. Required split:

| File | Kind | Responsibility |
|------|------|----------------|
| `toolLimits/ToolExecutionLimitsSection.tsx` *(new)* | presentational | Section shell: heading, help text, primary + advanced fields. Props in, callbacks out. |
| `toolLimits/LimitNumberField.tsx` *(new)* | presentational | Single labeled nullable integer field (reusable for calls + rounds). |
| `toolLimits/toolLimitForm.ts` *(new)* | pure helper | `parseOptionalPositiveInt(raw) → number \| undefined`; reject negative; unit-tested. |
| `toolLimits/toolLimitDisplay.ts` *(new)* | pure helper | `formatLimitDisplay(n) → "12" \| "Unlimited"` for crew summary. |
| `crew/CrewMemberLimitsRow.tsx` *(new)* | presentational | Read-only summary + edit link (Phase 4). |
| `crew/CrewMemberLimitOverrideField.tsx` *(new, Phase 6)* | presentational | Per-member invocation override input. |

`ToolsTab.tsx` and `CrewTab.tsx` stay thin: import and compose; **no** limit parsing logic in JSX.

Hard rules (gate-enforced):

- **No business logic in JSX/effects** — parsing and display strings live in `toolLimits/*.ts`
  with `__tests__/`.
- No new presentational `.tsx` exceeds ~250 lines.
- Reuse §3 primitives; duplicate dialog/toast/spinner is an automatic **FAIL**.

---

## 5. Phase gate checks

### Phase 4

- [ ] Execution limits section on Tools → Global Tools, below tool selection.
- [ ] Max tool calls per turn round-trips; blank = unlimited.
- [ ] Max tool rounds per turn visible under Advanced (round-trip; enforcement Phase 6).
- [ ] Help text distinguishes from published `MaxTurns`.
- [ ] Crew tab: per-member read-only limit + edit link.
- [ ] **Not** on `configTabs/LimitsTab.tsx`.
- [ ] **Reuse (§3):** number-input pattern, section divider, navigate, dirty callback.
- [ ] **Decomposition (§4):** helpers pure + tested; `ToolsTab`/`CrewTab` thin.
- [ ] Accessibility + responsive per §2.4.

### Phase 6 (additional)

- [ ] `max_tool_calls_per_invocation` editable per crew member; round-trip on guide save.
- [ ] `max_tool_rounds_per_turn` enforced server-side (not UI-only).
- [ ] `ui-gate` Phase 4 checks still pass.

---

## 6. Required UI test matrix

**Unit (`toolLimits/__tests__/`):**

- `parseOptionalPositiveInt('')` → `undefined`.
- `parseOptionalPositiveInt('12')` → `12`.
- `parseOptionalPositiveInt('-1')` → explicit error / rejected.
- `formatLimitDisplay(undefined)` → `"Unlimited"`.

**Component (if project pattern exists):**

- `ToolExecutionLimitsSection` renders fields; blank submit payload omits limit or sends `null`.
- `CrewMemberLimitsRow` shows unlimited vs numeric; edit link has correct `href`/navigate target.

**Manual:**

| Step | Expected |
|------|----------|
| Set max tool calls = 5, save, reload | Shows 5 |
| Clear field, save | Unlimited |
| Open Publish → Limits tab | No tool-call fields; Max Turns unchanged |
| Crew member with limit | Summary + edit link opens member Tools tab |
| Phase 6: set member invocation override | Saves and reloads on guide |

---

## 7. Fail modes (automatic FAIL)

| Code | Meaning |
|------|---------|
| `wrong-tab` | Limits on publish `LimitsTab` or wrong Tools sub-tab |
| `conflated-copy` | Copy confuses tool limits with `maxTurns` / usage limits |
| `no-round-trip` | FormData / save payload not wired |
| `ui-monolith` | Limit logic embedded in 250+ line panel without decomposition |
| `reinvented-primitive` | New toast/dialog/spinner/number-input component duplicating §3 |
| `default-zero` | Empty field saves as `0` instead of unlimited |

---

## 8. Report-back addition (Phase 4 / 6)

```text
UI GATE (Tool execution limits):
- Tools tab section placement + round-trip: <pass/fail>
- Help text distinct from MaxTurns: <pass/fail>
- Crew summary + edit link: <pass/fail>
- Phase 6 member override (if applicable): <pass/fail>
- Reuse (§3, no duplicates): <pass/fail + notes>
- Decomposition (§4, file map, .tsx line counts): <pass/fail>
- UI test matrix: <paths>
- Publish LimitsTab untouched: <pass/fail>
```
