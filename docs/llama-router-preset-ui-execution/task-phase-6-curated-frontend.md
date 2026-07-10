# Task — Phase 6: Curated onboarding frontend

> Subagent brief. May run in parallel with Phase 5. Return the report contract verbatim.

## Mission

Build one shared curated model → quant → review → progress → completion flow and
mount it in both Settings and Home onboarding. The normal path must require only
model and explicit quant selection.

## Read first

- Proposal §§2.1–2.5, 3.1–3.5, 4.3, 5.1–5.4, 9.
- `./DECISIONS.md` D1, D8–D10.
- Phase 4 frozen catalog/quant/add/operation fixtures.
- `src/client/src/features/localModelOnboarding/*`
- Settings `AddModelWizard`, `LlamaCppForm`, catalog types/tests.
- Home `AddAiServicesWizard`, `LocalAiModelsStep`, shared parity tests.
- `services/api.ts`, `types/settings.ts`.
- `ConfirmationDialog`, settings shared `ActionButtons`, Toast, LoadingSpinner,
  `PersonalizationTab` form/card styling, and existing `react-icons` packages.
- `./codeql-gate.md`

## Preconditions

- Phase 4 gate passed. Phase 5 may run concurrently but its action UI is Phase 7.

## Hard guardrails

- Shared state machine and request builder; no duplicated Settings/Home flow logic.
- Curated is selected by default. Preserve currently functional custom/attach entry
  paths, but do not present the new three-choice first screen until Phase 7 completes
  those advanced contracts.
- Quant selection initially has no selected row.
- Recommendation badges do not trigger state changes.
- Curated request sends identity fields only.
- A repository refresh that invalidates selection clears it and blocks review.
- Technical details are read-only.
- Display labels never affect behavior.
- API errors/partial operation state remain visible with server remediation.
- Preserve web and Electron router behavior and existing UI conventions.

## Tasks

1. Extend TypeScript DTOs/API methods for catalog, quant groups, curated add, and
   canonical `GET /api/settings/llama/operations/{operationId}` status using Phase 4
   fixtures. Update `services/__tests__/api.settings.test.ts`.
2. Refactor shared onboarding contracts/state into explicit modes:
   `curated` (default), `custom`, `existingAlias`; preserve current consumers while
   Phase 7 completes advanced mode UI.
3. Build `LlamaCuratedModelPicker`:
   searchable cards, display/license/gated info, labels, repository,
   documentation, hardware/curator notes, loading/error/retry.
4. Build quant selection:
   label, recommendation badge, total bytes, shard count, filenames summary,
   guidance, gated/hardware warnings; no initial choice.
5. On model selection/refresh, query the declared repository through the API. Keep
   the resolved revision bound to quant state.
6. Build review:
   display name, selected quant/bytes, repository, commit, ordered files, projector,
   context from preset, destination, warnings, and collapsed read-only profile/full
   preset technical details.
7. Submit only curated identities. Assert the request contains none of repository,
   paths, alias, profile, target, projector, or preset.
8. Move the shared operation poller from legacy `/downloads/{id}` to canonical
   `/operations/{id}`; assert curated flow never calls `getDownloadStatus` or the
   legacy route. Map stages `queued`, `resolvingFiles`, `downloading`,
   `registeringAlias`, `completed`, and structured errors. Keep polling tied to the
   operation ID and stop on terminal state.
9. Completion actions: Load now, Use as default chat model, View installed model.
   Use actual returned model/alias state.
10. Mount the same flow in:
    - Settings → Models & Runtime → Add Model → Local llama;
    - Home → Add AI Services → Local AI → Models.
11. Add unit/component tests for search, no quant choice, recommendation display,
    single/shards, refresh drift, projector, review, exact request shape, each
    operation stage/error, completion actions, both entry points, and both routers.
12. Update shared parity tests to compare state and final payload, not copied markup.

## Files in scope

- Client API/types.
- `features/localModelOnboarding/*` new/refactored shared flow.
- Settings add model local-llama flow and focused tests.
- Home local AI model step/wizard and focused tests.
- Existing common UI components only when a genuinely shared extension is needed.

Out of scope: server, advanced custom/preset editor, fleet editor, installed model
actions, adoption, migration issue UI.

## Self-verification

```text
cd src/client
npm run build
npm test -- --run
npm run find-orphans
```

Manually verify Settings and Home under browser routing and reason/run the Electron
hash route. Inspect the curated request in tests/network output. Search curated
client flow for `/downloads/` and require no match. Run Phase 6 CodeQL.

## Definition of Done

- [ ] Both entry points use one curated implementation.
- [ ] Model and explicit quant are the only operator choices.
- [ ] Complete shards and exact review details render.
- [ ] Curated payload is identity-only.
- [ ] Curated operation polling uses `/operations/{id}` only.
- [ ] Existing custom/attach paths remain functional and are not represented as the
      final advanced selector before Phase 7.
- [ ] Progress/errors/completion actions are functional.
- [ ] No recommendation auto-selects a quant.
- [ ] Build/tests/orphan/security gates pass.

## Report-back contract

```text
PHASE 6 REPORT
- Shared flow/state files: <paths>
- Entry points: Settings=<path> Home=<path> same-state-machine=<yes>
- Picker fields/search: <implemented/tested>
- Quant: initial-selection=<must be none> recommendation-display-only=<yes> shards=<p/f> drift-clears=<p/f>
- Review: commit=<yes> files=<yes> projector=<yes> preset/profile-readonly=<yes>
- Curated request allowed fields: <exact list> forbidden-field assertion=<p/f>
- Operation poll route: canonical-operations=<p/f> legacy-downloads-use=<must be none in curated flow>
- Interim custom/attach paths: functional=<p/f> final-three-choice-selector-not-yet-exposed=<yes>
- Operation stages/errors/completion actions: <results>
- Router verification: browser=<p/f> electron-hash=<p/f/reasoned>
- Verification: build=<p/f> tests=<counts> orphans=<delta>
- CODEQL REPORT: <required block from codeql-gate.md>
- Files touched: <exhaustive list>
- Deviations / surprises: <list or none>
```
