# Task - Phase 2: Guided creation for existing schemes

> Subagent brief. Execute top to bottom. Return the Report-back contract verbatim.

## Mission

Ship guided Tool Source authoring for Web API, Client Actions, and Sandbox Module,
plus a structured operation editor focused on model-facing tool definition fields.
Preserve advanced JSON escape hatches and compatibility behavior.

## Read first

- `../tool-sources-guide-builder-proposal.md` sections 6, 7, 8.1, 8.2, 8.3, 11, 12,
  13, 15 (Phase 2), 16, and 18.
- `./DECISIONS.md` decisions D4-D7.
- `./ui-gate.md` sections 2.3, 2.4, 2.5, 2.7, and 2.8.
- `./runtime-parity-gate.md`.
- Existing client integration points:
  - `src/client/src/components/guides/editor/OpenApiSchemas.tsx`
  - `src/client/src/components/guides/editor/OperationEditor.tsx`
  - `src/client/src/services/api.ts` (`guides.operations.preview`).

## Preconditions

- Phase 1 gate green.
- D4, D5, D6, and D7 resolved in `DECISIONS.md`.

## Guardrails (hard)

- Guided forms must generate valid OpenAPI descriptors.
- Raw JSON mode remains available.
- Existing import/paste OpenAPI flow remains supported.
- No runtime dispatch changes in this phase.
- No lossy overwrite of advanced/custom JSON fields.
- Reuse existing modal/button/validation component patterns.
- Unsaved changes must not be silently discarded.

## Tasks

1. Add "Add Tool Source" picker with options:
   - Web API
   - Client Actions
   - Sandbox Module
   - (Advanced) Local Function
   - (Advanced) Raw OpenAPI.
2. Implement source-specific connection editors:
   - Web API:
     - server URL
     - auth summary/status
     - import descriptor affordance
   - Client Actions:
     - client bridge id
     - status copy ("Handled by client application")
   - Sandbox Module:
     - init module filename
     - function discovery mode per D5.
3. Implement operation editor container and sections:
   - Tool Definition
   - Parameters
   - Execution Mapping
   - Response Schema
   - Preview
   - Advanced Fragment.
4. Build parameter editor behaviors per D6 and D7:
   - row-level editing for name/type/required/description/default/example/enum
   - arrays/items support
   - nested object support according to D6
   - hidden/default-injected parameter presentation per D7.
5. Build execution mapping editor (scheme-specific controls):
   - Web API -> method + path controls
   - Client Actions -> bridge/action mapping controls
   - Sandbox -> init module + function mapping controls.
6. Build response schema editor:
   - none/object/array/raw-json modes
   - inline schema validation.
7. Implement descriptor generation helpers:
   - `toolDefinitionModel.ts`
   - `openApiDescriptorBuilder.ts`
   - `operationFragmentBuilder.ts`.
8. Wire preview panel to backend preview endpoint and show:
   - tool definition payload
   - action type/source kind
   - hidden/default-injected parameter notes
   - validation messages.
9. Implement custom descriptor mode behavior (D4):
   - detect non-roundtrippable advanced JSON
   - display "Custom descriptor" state
   - preserve raw descriptor
   - provide guided-mode re-entry when parseable.
10. Add unsaved-change prompts for:
    - closing operation editor with dirty state
    - switching tabs that would discard local edits.
11. Add frontend tests for guided creation, editing, preview, and custom mode.

## UI behavior details (required)

1. Picker flow
   - `Add Tool Source` opens selector.
   - selecting an option creates a draft source and focuses first required field.
2. Validation flow
   - field-level errors shown inline on blur and on save.
   - non-field errors shown in a dismissible summary region.
3. Operation editor flow
   - save disabled when invalid.
   - preview available even before save when fragment is valid.
   - switching source/operation preserves unsaved local state per existing editor
     conventions or prompts before discard.
4. Responsive flow
   - desktop: editor sections can split into two columns where readable.
   - mobile: stack all sections with sticky footer actions.

## Test matrix additions (required)

- Picker tests:
  - correct options shown for normal vs advanced mode.
  - focus behavior after source creation.
- Guided descriptor generation tests:
  - web/client/sandbox generated OpenAPI shape snapshots.
- Operation editor tests:
  - field updates map to operation fragment JSON.
  - defaults/enums/required flags reflected in preview.
  - custom descriptor mode entry and preservation.
  - unsaved-change dialog behavior.
- Accessibility tests:
  - tab roles
  - modal focus trap and restore
  - error announcement region.
- Responsive smoke tests:
  - mobile layout render path does not hide primary save/cancel controls.

## Files in scope

- `src/client/src/components/guides/editor/OpenApiSchemas.tsx`
- `src/client/src/components/guides/editor/OperationEditor.tsx` (or replacement modal)
- `src/client/src/components/guides/editor/toolSources/*`
- `src/client/src/services/api.ts` (typed preview usage if needed)
- `src/client/src/types/guides.ts` (typing updates for editor state)
- Client tests under `src/client/src/components/guides/editor/**` and
  `src/client/src/services/**`.

Out of scope:

- MCP source/discovery/runtime strategy (Phase 3).
- Backend scheme/publish validation endpoints (Phase 4).
- Storage migration/cleanup (Phase 5).

## Self-verification

```bash
cd src/client && npm run build && npm test -- --run
cd src/server && dotnet build GuideAntsApi.sln && dotnet test GuideAntsApi.sln
```

Required gate checks:

- UI gate phase 2 checks in `ui-gate.md`.
- Runtime parity checks in `runtime-parity-gate.md` sections 3.1 through 3.6
  for web/client/sandbox.

## Definition of Done

- [ ] Guided source creation works for web/client/sandbox without manual raw JSON.
- [ ] Picker, editor states, and validation behavior match UI gate contract.
- [ ] Structured operation editor covers required tool-definition fields.
- [ ] Parameter editing behavior follows locked D6 and D7 decisions.
- [ ] Advanced JSON remains available and supports custom descriptor mode.
- [ ] Unsaved-change behavior is explicit and tested.
- [ ] Preview in editor uses backend runtime-compatible transformation.
- [ ] Existing import/paste and saved descriptor compatibility remain intact.
- [ ] Build/tests green and UI + parity gates pass.

## Report-back contract (return exactly this)

```text
PHASE 2 REPORT
- Add Tool Source picker implemented with source types: <list>
- Connection editors shipped: <web/client/sandbox details>
- Structured operation editor surfaces implemented: <list>
- Descriptor generation helpers added/updated: <paths>
- D6 schema-depth behavior implemented as: <value + notes>
- D7 hidden/default parameter UX implemented as: <value + notes>
- Custom descriptor mode behavior: <implemented/not implemented + details>
- Unsaved-change handling implemented: <yes/no + details>
- Backend preview wiring used for UI preview: <yes/no>
- UI GATE: picker-flow=<pass/fail> operation-editor=<pass/fail> accessibility=<pass/fail> responsive=<pass/fail>
- RUNTIME PARITY GATE: classification=<pass/fail> generation=<pass/fail> preview=<pass/fail> compatibility=<pass/fail>
- Verification: server-build=<pass/fail> server-tests=<counts> client-build=<pass/fail> client-tests=<counts>
- Files touched: <exhaustive list>
- Deviations / surprises: <list or "none">
```
