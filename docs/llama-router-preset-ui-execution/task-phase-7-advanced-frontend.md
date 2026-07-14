# Task — Phase 7: Advanced and installed-model frontend

> Subagent brief. Execute top to bottom and return the report contract verbatim.

## Mission

Complete the operator-managed surfaces: custom HF, attach existing alias, full
alias-preset editor, installed curated summary, change quant,
repair, Customize, and curated adoption. Remove legacy advanced fields from the
normal curated add/edit experience.

> **Fleet panel superseded:** There is no Fleet llama Settings panel. Router-process keys
> (`parallel`, `threads`, …) use `GA_LLAMA_*` in docker-compose. Do not build fleet UI.

## Read first

- Proposal §§3.6–3.8, 4.7.3–4.9, 5.5–5.8, 6.4, 7.2–7.4, 9.
- `./DECISIONS.md` D3–D10.
- Phase 5 lifecycle fixtures and Phase 6 shared onboarding state.
- `LlamaCppForm`, `CatalogRowEditModal`, `ModelsRuntimeWorkspace`,
  `LocalLlamaRuntimeTab`, runtime-profile UI, Settings/Home wizard tests.
- Existing key-value editors, ConfirmationDialog, action buttons, Toast,
  LoadingSpinner, error and technical disclosure conventions.
- `./codeql-gate.md`

## Preconditions

- Phase 5 and Phase 6 gates passed.

## Hard guardrails

- Curated normal edit remains presentation + read-only technical/install state.
- Router-process-scoped keys cannot be saved through the alias preset editor; errors point to `GA_LLAMA_*` in docker-compose.
- Custom flow infers no profile, projector, context, preset, revision, or artifacts.
- Attach does not offer to rewrite the alias preset.
- Customize requires explicit confirmation and explains tracking consequences.
- Adoption shows every difference before confirmation and never fills unknown data.
- Migration issues remain visible when applicable.
- Reuse existing UI primitives and icon libraries.

## Tasks

1. Complete shared mode selection UI: Curated default, Custom HF advanced, Attach
   existing alias.
2. Move the current free-form HF browser under Custom and change selection from one
   include pattern to explicit complete artifact groups, including ordered shards.
3. Build Custom inputs for revision, artifacts, projector, catalog identity, alias,
   profile, target directory, and full alias preset. Require all necessary values.
4. Build structured alias preset editor:
   key/value rows, add/remove, duplicate/reserved/value validation, live INI preview,
   replace/merge intent; router-process keys rejected with `GA_LLAMA_*` guidance.
5. Build Attach flow listing unbound artifact-backed aliases; require catalog
   identity/profile and show existing preset read-only.
6. Replace normal llama catalog edit fields with:
   presentation fields, curated ID/version, quant, repository/commit, exact
   artifacts, effective profile, effective alias preset/runtime state, and
   management mode.
7. Add actions:
   - Change quant modal using current live groups and explicit choice;
   - Repair confirmation/progress using recorded source;
   - technical configuration view (effective alias preset);
   - Customize confirmation and operator editor;
   - adoption comparison/diff and confirmation.
8. ~~Add Fleet llama server panel under Models & Runtime~~ **SUPERSEDED — never shipped; do not build.**
   Router-process keys use `GA_LLAMA_*` in docker-compose instead.
9. Add migration issue panel/actions. Show unmapped `loadParams`, profile policy
   disagreement, and operator-managed classification without hiding models.
10. Remove normal add/edit fields and serializers for load JSON, row-level parallel
    tools, context/cache, direct profile/alias/projector/preset selection in curated
    mode. Retain profile editing only in Runtime Profiles.
11. Keep Settings and Home custom/attach behavior mapped through shared contracts.
12. Add component/parity tests for all modes/actions, preset validation/preview,
    loaded lifecycle progress, migration issues,
    management transitions, and removed-field absence.
13. Run client-focused CodeQL/manual checks for unsafe URL/HTML/secret rendering and
    advanced input handling.

## Files in scope

- Client settings/home local model onboarding and shared feature state.
- Catalog edit/installed detail, models/runtime workspace, llama runtime tab.
- Client API/types and focused tests.
- Existing common UI primitives only for shared extensions.

Out of scope: server/Python behavior, manifest/schema, EF migrations, changing
runtime semantics.

## Self-verification

```text
cd src/client
npm run build
npm test -- --run
npm run find-orphans
```

Search client source: removed normal-form fields must not be submitted by curated
mode. Manually exercise desired/applied mismatch and every lifecycle terminal error.
Run Phase 7 CodeQL.

## Definition of Done

- [ ] Custom and attach flows are explicit, shared, and shard-capable.
- [ ] Alias preset editor and Fleet panel are separate and validated.
- [ ] Installed model summary/actions and management transitions are complete.
- [ ] Migration issues and desired/applied status are visible.
- [ ] Legacy advanced curated fields are absent from normal add/edit and payloads.
- [ ] Build/tests/orphan/security gates pass.

## Report-back contract

```text
PHASE 7 REPORT
- Mode flows: curated=<existing> custom=<p/f> attach=<p/f> shared-between-entry-points=<yes>
- Custom explicit fields/shards: <list/results>
- Alias preset editor: validation=<p/f> preview=<p/f> scope-routing=<p/f> modes=<p/f>
- Fleet panel: fields=<list> desired/applied=<p/f> error/retry=<p/f>
- Installed summary/actions: detail=<p/f> quant=<p/f> repair=<p/f> customize=<p/f> adopt=<p/f>
- Migration issues: <rendered/actions>
- Removed normal curated fields: load-json=<absent> row-tool-policy=<absent> context/cache=<absent> alias/profile/preset inputs=<absent>
- Verification: build=<p/f> tests=<counts> orphans=<delta>
- CODEQL REPORT: <required block from codeql-gate.md>
- Files touched: <exhaustive list>
- Deviations / surprises: <list or none>
```
