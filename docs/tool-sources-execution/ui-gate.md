# UI Gate (Tool Sources Guide Builder)

Companion to `00-orchestration.md`.

This gate is the concrete UX contract for the Tool Sources editor. It exists to
prevent "feature complete but hard to use" outcomes and to make UI quality
verifiable phase-by-phase.

---

## 1. Gate intent

Pass this gate when all are true:

- Users can understand source kind, connection key, operation status, and validation
  state without opening raw JSON.
- Guided flows are faster than manual JSON editing for common cases.
- Advanced mode is available and safe for power users.
- Keyboard, screen-reader semantics, and mobile layout are first-class.

---

## 2. Required UI contract

### 2.1 Tools tab structure

- Keep `ToolsTab` as integration boundary.
- Subtabs:
  - `Global Tools`
  - `Tool Sources`
- URL state persistence for subtab remains stable (`toolsSubTab` query param).

### 2.2 Tool source list and card contract

Each source card (collapsed and expanded) must show:

- Source name.
- Source kind badge (Web API, Client Actions, Sandbox Module, MCP, Local Function).
- Connector key label and value (scheme-aware).
- Operation count (`enabled/total` if disabled operations exist).
- Validation status chip:
  - `Valid`
  - `Needs attention`
  - `Custom descriptor`
  - `Invalid JSON`
- Connection/auth status line (for scheme-relevant states only).

Card interactions:

- Expand/collapse by button and keyboard (`Enter`/`Space`).
- Primary actions:
  - Edit connection
  - Edit operations
  - Advanced JSON
  - Delete source
- Destructive actions require confirmation dialog.

### 2.3 Add Tool Source picker contract

Picker options:

- Web API
- Client Actions
- Sandbox Module
- MCP Connection (when phase enabled)
- Local Function (advanced mode only)
- Raw OpenAPI (advanced mode only)

Picker behavior:

- One-click create with sensible defaults.
- Focus lands in first required field after create.
- Cancel returns focus to Add button.

### 2.4 Operation editor contract

Operation editor must provide these sections (tabs or stacked panes):

- Tool Definition:
  - function name
  - summary
  - description
  - content type
- Parameters:
  - row editor for name/type/required/description/default/example/enum
  - array item and object-property support per decision D6
- Execution Mapping:
  - scheme-specific fields only
  - web method/path only for Web API source
- Response Schema:
  - none/object/array/raw JSON modes
- Preview:
  - exact backend preview output
  - hidden/default-injected parameters
  - source kind and action type
- Advanced Fragment:
  - raw `{ path, method, operation }` JSON.

### 2.5 Guided vs custom descriptor mode

- If advanced edits are not round-trippable:
  - show `Custom descriptor` badge
  - preserve JSON verbatim
  - disable conflicting guided controls with clear message.
- If round-trippable, allow "Return to guided mode" action.

### 2.6 Accessibility contract

- Tabs use semantic `role="tablist"` and `role="tab"` with `aria-selected`.
- Expand/collapse controls expose `aria-expanded`.
- Validation errors are announced with `aria-live="polite"`.
- All actionable controls are keyboard reachable with visible focus styles.
- Modal traps focus and restores focus to trigger on close.

### 2.7 Responsive contract

Desktop (`>=1024px`):

- Source list and editor panels can use split layout.
- Operation editor shows side-by-side where readable.

Tablet/mobile (`<1024px`):

- Source cards stack.
- Editors switch to single-column flow.
- Modals/drawers remain scroll-safe and keep footer actions visible.

### 2.8 Loading/empty/error states

Required states:

- Empty sources state with clear "Add Tool Source" CTA.
- Loading skeleton/spinner for source fetch and preview calls.
- Inline field-level validation errors.
- Non-field errors in dismissible alert panel.
- Retry affordance for network failures.

---

## 3. Phase gate checks

### Phase 1 UI gate checks

- 2.1 and 2.2 contracts implemented for existing sources.
- Scheme-aware labels visible for non-web sources.
- Accessibility checks for tabs and expand/collapse pass.

### Phase 2 UI gate checks

- 2.3, 2.4, 2.5, 2.8 contracts implemented.
- Unsaved-change warning when closing editor with dirty state.
- Preview panel reflects backend output.

### Phase 3 UI gate checks (MCP)

- MCP connection panel follows 2.3 and 2.8 state patterns.
- Discovery refresh diff state chips visible:
  - Added
  - Changed
  - Removed
  - Disabled.

### Final acceptance checks

- 2.1 through 2.8 fully pass on desktop and mobile.
- No regression from existing guide editor patterns and shared components.

---

## 4. Required UI test matrix

### Component/unit tests

- Source classification -> badge/label mapping.
- Card status badges for valid/invalid/custom states.
- Add picker visibility rules for advanced options.
- Operation editor field mapping to schema fragments.
- Custom descriptor mode transitions.
- Validation rendering and error announcements.

### Interaction tests

- Keyboard navigation across tabs/cards/dialogs.
- Create source -> edit -> save -> reload roundtrip.
- Dirty-form close confirmation.
- Preview request success/failure handling.

### Responsive tests

- Mobile layout smoke for source list and operation editor.
- Action button visibility and reachability at small widths.

---

## 5. Report-back addition

Subagents for UI-heavy phases append:

```text
UI GATE:
- Source list/card contract: <pass/fail>
- Picker and guided creation flow: <pass/fail>
- Operation editor contract: <pass/fail>
- Guided/custom mode behavior: <pass/fail>
- Accessibility checks: <pass/fail>
- Responsive checks (desktop/mobile): <pass/fail>
- UI test matrix additions: <paths>
```
