# Task - Phase 1: Scheme-aware Tool Sources UI over existing data

> Subagent brief. Execute top to bottom. Return the Report-back contract verbatim.

## Mission

Refactor the guide editor framing from "Web Connectors" to "Tool Sources" while
keeping existing data contracts and runtime behavior unchanged. This phase is
labeling + classification + card/state model extraction, not guided creation.

## Read first

- `../tool-sources-guide-builder-proposal.md` sections 1, 5, 6, 7, 11, 12, 15
  (Phase 1), and 18.
- `./DECISIONS.md` Part B invariants.
- `./ui-gate.md` sections 2.1, 2.2, and 2.6.
- `./runtime-parity-gate.md`.
- Client files:
  - `src/client/src/components/guides/editor/ToolsTab.tsx`
  - `src/client/src/components/guides/editor/OpenApiSchemas.tsx`
  - `src/client/src/components/guides/editor/OperationEditor.tsx`
  - `src/client/src/types/guides.ts`

## Preconditions

- Pre-flight baseline complete and recorded in `STATUS.md`.
- Any decision needed for wording/labels is resolved in `DECISIONS.md`.

## Guardrails (hard)

- Keep `CustomToolDto` payload compatibility (`name`, `openApiSpec`, `apiHost`,
  `authConfig`, `operations`).
- Do not change backend persistence/runtime semantics in this phase.
- Keep raw JSON editing available.
- No fallback behavior that silently reclassifies invalid descriptors.
- Do not call non-web connector identifiers "API host" in UI labels.
- Preserve existing visual language: reuse current dialog/button/toast patterns and
  avoid introducing a parallel design system.
- Keyboard and screen-reader semantics from `ui-gate.md` section 2.6 are mandatory
  in this phase for tabs and source-card expansion controls.

## Tasks

1. Rename tools subtab language from "Web Connectors" to "Tool Sources" and update
   related empty/add button copy.
2. Add scheme-aware helper modules under
   `src/client/src/components/guides/editor/toolSources/`:
   - `toolSourceClassification.ts`
   - `openApiToolSource.ts`
   - `validation.ts`
   - optional `toolSourceCardViewModel.ts`.
3. Move URL/scheme parsing, operation extraction, and source-kind derivation out of
   `OpenApiSchemas.tsx` component body into helpers.
4. Implement a source-card view model builder:
   - input: `CustomToolDto`
   - output fields:
     - `sourceKind`
     - `connectorKeyLabel`
     - `connectorKeyValue`
     - `operationCount`
     - `status` (`valid`, `needs-attention`, `custom`, `invalid-json`)
     - `isCustomDescriptor`.
5. Add source badges and connector-key labels by source kind:
   - Web API -> API host
   - Client Actions -> Client bridge
   - Sandbox Module -> Init module
   - Local Function -> Local tool host
6. Add status chip derivation with deterministic priority:
   - `Invalid JSON` (descriptor parse fails)
   - `Needs attention` (schema valid but has validation issues)
   - `Custom descriptor`
   - `Valid`.
7. Keep compatibility mapping to `apiHost` internally, but stop presenting it as
   web-only terminology for non-web sources.
8. Add keyboard behavior for source cards:
   - expand/collapse by button + `Enter`/`Space`
   - `aria-expanded` and `aria-controls`
   - focus-visible styles aligned with existing controls.
9. Add/adjust frontend tests for helper logic and source card rendering.

## UI behavior details (required)

1. Card layout
   - Header row: source name, source-kind badge, status chip.
   - Metadata row: connector label/value plus operation count.
   - Actions row with existing-pattern buttons.
2. Error behavior
   - Invalid JSON does not break list rendering.
   - Invalid sources still allow opening advanced JSON editing.
3. Navigation stability
   - Existing `toolsSubTab` query-param behavior is unchanged.
4. Empty state copy
   - Copy explicitly references multiple source kinds, not web-only language.

## Test matrix additions (required)

- Helper tests:
  - `https://` -> Web API
  - `client://` -> Client Actions
  - `sandbox://` -> Sandbox Module
  - `tool://` -> Local Function
  - invalid/missing URL -> invalid classification state.
- UI tests:
  - "Tool Sources" label render in tools subtab.
  - badge plus connector label render for non-web sources.
  - status chip priority ordering.
  - expand/collapse keyboard behavior and ARIA attributes.
- Regression tests:
  - existing bootstrap client/sandbox descriptors still render as non-web cards.

## Files in scope

- `src/client/src/components/guides/editor/ToolsTab.tsx`
- `src/client/src/components/guides/editor/OpenApiSchemas.tsx`
- `src/client/src/components/guides/editor/toolSources/*` (new helpers)
- `src/client/src/types/guides.ts` (if required for display-level source-kind typing)
- Relevant test files under `src/client/src/components/guides/editor/**/__tests__`
  or nearby test paths.

Out of scope:

- Guided source creation UX (Phase 2).
- MCP source discovery/runtime support (Phase 3).
- Backend validation/publish checks (Phase 4).
- Storage migrations (Phase 5).

## Self-verification

```bash
cd src/client && npm run build && npm test -- --run
cd src/server && dotnet build GuideAntsApi.sln && dotnet test GuideAntsApi.sln
```

Also run:

- UI gate checks: `ui-gate.md` phase 1 requirements.
- Runtime parity gate checks: `runtime-parity-gate.md` sections 3.1, 3.2, 3.6.

## Definition of Done

- [ ] UI says "Tool Sources" (not "Web Connectors") in guide editor surfaces.
- [ ] Source classification is scheme-aware and helper-driven.
- [ ] Source card view-model layer exists (no ad-hoc JSON parsing in JSX render).
- [ ] Non-web sources no longer show misleading API-host language.
- [ ] Status chips render with deterministic priority.
- [ ] Keyboard and ARIA behavior for tabs/cards pass UI gate.
- [ ] JSON editor remains available and functional.
- [ ] Existing descriptors still render and save without runtime change.
- [ ] Build/tests green and UI + parity gate checks pass.

## Report-back contract (return exactly this)

```text
PHASE 1 REPORT
- Tool Sources rename completed in: <paths>
- Scheme classification helper modules added/updated: <paths>
- Source card view model implemented: <yes/no + path>
- Connector key label map implemented: <yes/no + labels>
- Source status model implemented: <yes/no + states>
- Compatibility with existing CustomToolDto/apiHost preserved: <yes/no>
- UI GATE: list-card-contract=<pass/fail> accessibility=<pass/fail> responsive-smoke=<pass/fail>
- RUNTIME PARITY GATE: classification=<pass/fail> labels=<pass/fail> bootstrap-compat=<pass/fail>
- Verification: server-build=<pass/fail> server-tests=<counts> client-build=<pass/fail> client-tests=<counts>
- Files touched: <exhaustive list>
- Deviations / surprises: <list or "none">
```
