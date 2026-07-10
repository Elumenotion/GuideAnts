# Curated Local Llama — Execution & Orchestration Guide

Last updated: 2026-07-10

This is the conductor document for fully executing
[`../llama-router-preset-ui-proposal.md`](../llama-router-preset-ui-proposal.md).
It converts the proposal into independently verifiable subagent briefs, dependency
gates, and an audit trail.

## 0. Audience and files

- The orchestrator reads this file, [`DECISIONS.md`](./DECISIONS.md), and
  [`STATUS.md`](./STATUS.md), dispatches one brief at a time, independently runs
  every gate, and updates the ledger.
- A subagent reads only its task brief, the proposal sections cited there,
  `DECISIONS.md`, and the source files named by the brief.
- A subagent report is evidence to check, not proof that a gate passed.

| File | Owner | Purpose |
|---|---|---|
| `00-orchestration.md` | Orchestrator | Order, gates, failure protocol, final acceptance |
| `DECISIONS.md` | Orchestrator | Locked cross-layer contracts |
| `STATUS.md` | Orchestrator | Baselines, phase states, reports, deviations, qualification evidence |
| `codeql-gate.md` | Orchestrator + sensitive phases | Local C#/Python/JS security gate |
| `task-phase-0-contracts-baseline.md` | Phase 0 agent | Physical contract proofs and clean execution baseline |
| `task-phase-1a-catalog-discovery.md` | Phase 1A agent | Llama manifest, catalog, revision and quant-group discovery |
| `task-phase-1b-persistence-profiles.md` | Phase 1B agent | Provenance/operation/settings persistence and runtime-profile contract |
| `task-phase-2-router-download.md` | Phase 2 agent | Exact artifacts, sharded downloads, complete alias presets |
| `task-phase-3-runtime-fleet-migration.md` | Phase 3 agent | Fleet settings, minimal runtime JSON, deterministic migration |
| `task-phase-4-curated-install.md` | Phase 4 agent | Authoritative curated resolution and durable install finalization |
| `task-phase-5-lifecycle-api.md` | Phase 5 agent | Change quant, repair, adoption, custom and attach APIs |
| `task-phase-6-curated-frontend.md` | Phase 6 agent | Curated model/quant/review/progress UX in both entry points |
| `task-phase-7-advanced-frontend.md` | Phase 7 agent | Custom, attach, preset, fleet and installed-model management UX |
| `task-phase-8a-release-automation.md` | Phase 8A agent | Migration fixtures, API snapshot, full automated closeout |
| `task-phase-8b-live-qualification.md` | Phase 8B agent | Live 14-repository and representative hardware qualification |

Every task brief uses the same contract:
Mission → Read first → Preconditions → Hard guardrails → Tasks → Files in/out of
scope → Self-verification → Definition of Done → Report-back.

## 1. Non-negotiable product invariants

These are checked at every relevant gate.

1. The normal curated flow asks for exactly a curated model and a user-selected
   quant. It does not request alias, profile, projector, context/cache, load JSON,
   tool policy, target directory, or preset input.
2. No quant is selected automatically. Curator recommendations are labels only.
3. Repository contents are transient discovery data and never mutate the manifest.
4. Incomplete shard groups are rejected. Shards are deterministic and ordered.
5. A curated request sends identities; the server resolves repository paths,
   artifacts, profile, alias, projector, and preset from the versioned definition.
6. No model identity, artifact, revision, quant, projector, profile, preset, or
   operation result is silently substituted.
7. Each concern has one authoritative store:
   - fleet preset: SQL, with a revisioned runtime projection;
   - alias preset and artifact paths: `router-models.ini`;
   - catalog routing identity: minimal `Models.RuntimeConfigJson`;
   - chat behavior: runtime profiles;
   - installation source and resolved artifacts: installation provenance.
8. Compose `GA_LLAMA_*` values seed an empty fleet-settings row only. They are not
   the ongoing operator write path.
9. Custom and attach flows remain available and explicit. No repository, revision,
   quant, or behavior is inferred for operator-managed models.
10. Existing hand-edited INI extras are preserved and classified as
    operator-managed until the operator explicitly adopts a curated definition.
11. Errors and partial state remain visible. Do not add alternate values, catches,
    retries, defaults, or compatibility branches that hide a broken contract.

## 2. Pre-flight

Do not dispatch product-code phases until Phase 0 passes.

- [ ] Read and accept every locked value in `DECISIONS.md`.
- [ ] Inventory the current modified and untracked files. The repository is not
      assumed clean. Assign each pre-existing change to a phase or isolate it with
      a user-approved commit/worktree. Never stash, discard, reset, or overwrite
      user work without explicit approval.
- [ ] Capture server, client, Python, shell, Docker-image, CodeQL, and migration
      baselines in `STATUS.md`.
- [ ] Confirm required tools: .NET SDK, `dotnet ef`, Node/npm, Python, Docker,
      Bash, CodeQL, and JSON-schema validation.
- [ ] Confirm release credentials are available without being committed:
      Hugging Face token for gated repositories and credentials/hardware for each
      claimed CPU/CUDA/ROCm/Vulkan qualification lane.
- [ ] Prove the bundled llama.cpp sharded-preset contract and the fleet settings
      materialization/restart contract described in `DECISIONS.md`.
- [ ] Freeze request/response fixtures for catalog, quants, router entries,
      fleet desired/applied state, install operations, provenance, and lifecycle
      actions as committed files under this execution folder; parse them across
      Python, C#, and TypeScript.
- [ ] Record exact baseline test counts and pre-existing failures. A later phase
      may not relabel an old failure as a pass or introduce a new failure.

If a physical proof contradicts a locked technical decision, stop before product
changes. Record the evidence, revise `DECISIONS.md`, and update every affected
brief first.

## 3. Dependency graph and allowed parallelism

```text
Phase 0 — contracts, physical proofs, baseline
       |
       +-------------------+
       v                   v
Phase 1A               Phase 1B
catalog/discovery      persistence/profiles
       |                   |
       v                   |
Phase 2                   |
router/download            |
       +---------+---------+
                 v
Phase 3 — runtime cleanup, fleet settings, deterministic migration
                 |
                 v
Phase 4 — curated install orchestration and durable finalization
       +-------------------+
       v                   v
Phase 5               Phase 6
lifecycle APIs        curated frontend
       +---------+---------+
                 v
Phase 7 — advanced/custom/fleet/installed frontend
       +-------------------+
       v                   v
Phase 8A              Phase 8B
automated closeout    live qualification
       +---------+---------+
                 v
          FINAL ACCEPTANCE
```

Allowed concurrency:

- Phase 1A and 1B may run together: their owned files do not overlap.
- Phase 5 and 6 may run together after Phase 4: Phase 5 owns backend lifecycle
  APIs; Phase 6 owns the curated client flow against the frozen Phase 4 contracts.
- Phase 8A and 8B may run together: 8A owns source/docs/tests; 8B is
  qualification-only and must not edit product code.
- No other overlap is allowed. In particular, Phase 1A and Phase 2 both touch
  llama-admin Python surfaces, and Phases 6 and 7 share frontend state.

A phase is not done until the orchestrator independently passes its gate. Never
start a dependent phase on a failed or incomplete gate.

## 4. Dispatch protocol

For each ready phase:

1. Confirm the brief's preconditions and prior gates.
2. Update `STATUS.md` to `IN_PROGRESS` with attempt number and agent.
3. Dispatch exactly:

   > Read and execute
   > `docs/llama-router-preset-ui-execution/<brief>.md` end to end. Obey its
   > guardrails and Definition of Done. Return the Report-back contract verbatim.

4. Compare the returned file list and claims to the brief.
5. Inspect the diff. Check ownership, migrations, tests, contracts, and security.
6. Run the phase gate and all global invariants yourself.
7. Mark `DONE` only on a complete pass. Otherwise use section 7.

The implementing subagent must not update `STATUS.md`, `DECISIONS.md`, or this
orchestration file.

## 5. Global invariants at every gate

- [ ] `cd src/server && dotnet build GuideAntsApi.sln` succeeds with no new warnings.
- [ ] `cd src/server && dotnet test GuideAntsApi.sln` has no new failures.
- [ ] `cd src/client && npm run build` succeeds.
- [ ] `cd src/client && npm test -- --run` has no new failures.
- [ ] `cd src/client && npm run find-orphans` has no increase versus baseline after
      any client phase.
- [ ] Targeted Python tests for `guideants_hf` and llama-admin succeed; modified
      Python files compile with `python -m compileall`.
- [ ] Modified shell entrypoints pass `bash -n`.
- [ ] No new client/server contract mismatch; generated OpenAPI is deferred to 8A,
      but DTO fixtures remain synchronized in every phase.
- [ ] No new hidden alternate behavior: search the diff for broad catches,
      permissive defaults, silent retries, identity renames, first-item selection,
      ignored migration conflicts, and success responses after partial failure.
- [ ] No duplicated runtime authority reappears in `RuntimeConfigJson`, compose,
      the manifest, or frontend state.
- [ ] No secrets or tokens are logged, serialized to clients, or committed.
- [ ] Repository/artifact paths remain root-contained; user values never become
      shell fragments or unrestricted filesystem paths.
- [ ] The phase touched only its declared files. Unexpected overlap is a deviation.
- [ ] At scan points listed in `codeql-gate.md`, the CodeQL diff is clean relative
      to the Phase 0 baseline.
- [ ] User-owned pre-existing changes remain intact.
- [ ] Frontend phases use existing `ConfirmationDialog`, shared action buttons,
      Toast, LoadingSpinner, settings form/card styling, and current icon libraries;
      no bespoke modal/action primitive or new icon library is introduced.

## 6. Per-phase gates

### Phase 0

- [ ] Baselines and current-worktree ownership recorded.
- [ ] Two-shard fixture proves the exact `model` value consumed by bundled llama.cpp.
- [ ] Fleet desired/applied projection and restart sequence proven.
- [ ] All committed JSON fixtures parse in C#, Python, and TypeScript where applicable.
- [ ] Exact Python/schema and CPU/CUDA/ROCm/Vulkan commands are recorded.

### Phase 1A

- [ ] Manifest validates and contains exactly 14 unique definitions from proposal
      §4.16 with no file arrays or executable capability booleans.
- [ ] Fixture tests cover single GGUF, complete shards, incomplete shards,
      projector exclusion, separate projector repository, stable quant IDs, and
      deterministic order.
- [ ] Catalog and quant endpoints return resolved commit and exact artifact metadata.
- [ ] No quant is preselected by server or client-oriented response fields.
- [ ] Every definition has complete display/license/labels/documentation,
      quant-guidance, and hardware metadata required by proposal §§4.12–4.13.
- [ ] Vision and MTP entries obey proposal §4.10 and D12.
- [ ] Live 14-entry suite passes. An environmental block may be deferred to Phase
      8B only with the exact command/evidence, confirmed credentials plan, and user
      acknowledgment recorded in `STATUS.md`; final acceptance remains blocked.

### Phase 1B

- [ ] EF migration adds provenance, durable operation, fleet desired/applied state,
      migration-issue storage, and runtime-profile tool-request fields.
- [ ] Existing data upgrades without invented provenance.
- [ ] `deepseek_r1` and `qwen3_coder` plus four existing profile extensions validate.
- [ ] Tool request fields are typed/validated and never treated as model labels.

### Phase 2

- [ ] Router GET returns the complete preset; write supports exact `replace` and
      explicit `merge` semantics.
- [ ] Exact ordered artifacts download to staging, validate, and activate as one set.
- [ ] Path containment, duplicate keys, reserved keys, invalid values, incomplete
      shards, collisions, and restart failures are tested.
- [ ] C# admin client and Python service agree on frozen fixtures.
- [ ] GuideAnts `PUT` router proxy maps to llama-admin `POST` and round-trips the
      frozen preset fixture.
- [ ] Runtime journal is subordinate to SQL operation authority per D2.

### Phase 3

- [ ] Runtime JSON serializes only `routerModelId` and `runtimeProfileId`.
- [ ] Every model load path is alias-only.
- [ ] Tool-request fields come from the resolved runtime profile only.
- [ ] Fleet settings save exposes desired/applied revision and never reports an
      unapplied restart as success.
- [ ] Migration emits explicit issues for unmapped `loadParams` or profile conflicts.
- [ ] Migration status/issues routes match D8 fixtures.
- [ ] Integration stubs/walkthrough fixtures use alias-only loads and minimal runtime
      JSON; no llama-cpp fixture retains removed legacy fields.

### Phase 4

- [ ] Curated add accepts identities only and re-resolves against the exact manifest
      version and commit.
- [ ] Immutable input is durable before work starts.
- [ ] Model row and provenance are written only after artifacts and complete INI
      registration succeed.
- [ ] Restart recovery and every partial-state boundary are integration-tested.

### Phase 5

- [ ] Change-quant staging/activation, repair-at-recorded-commit, customize,
      adoption, custom install, and attach-existing behavior match the proposal.
- [ ] Loaded alias state is restored only when the operation has a verified prior
      loaded state; failure remains explicit.
- [ ] No lifecycle action invents provenance or mutates unrelated aliases.
- [ ] Every new catalog/fleet/lifecycle/migration route uses Admin settings policy;
      integration tests prove `401`/`403`/Admin behavior.
- [ ] Model deletion preserves catalog/provenance when runtime/artifact removal
      fails and removes them only after runtime cleanup succeeds.

### Phase 6

- [ ] Both entry points use the same curated state machine and request builder.
- [ ] No quant starts selected; review shows commit and exact files.
- [ ] Progress and completion actions use actual operation state.
- [ ] Settings/Home parity tests and both router modes pass.
- [ ] Client CodeQL/manual checks show no new repository metadata, URL, or operation
      rendering finding.
- [ ] Shared operation polling uses `/api/settings/llama/operations/{id}`; the
      curated flow does not call legacy `/downloads/{id}`.
- [ ] Existing custom/attach entry paths remain functional but are not presented as
      the new three-choice first screen until Phase 7 completes them.

### Phase 7

- [ ] Custom, attach, Customize, change quant, repair, adoption, fleet settings,
      installed summary, and preset editor are complete.
- [ ] Normal curated edit contains no removed advanced fields.
- [ ] Fleet/alias key-scope errors direct the operator to the correct editor.
- [ ] Desired/applied runtime state and migration issues are visible.

### Phase 8A

- [ ] Fresh, legacy-clean, legacy-conflict, hand-edited-INI, interrupted-operation,
      and upgrade fixtures all produce expected deterministic outcomes.
- [ ] OpenAPI snapshot regenerated from the running API and client contracts checked.
- [ ] Full builds/tests, schema tests, security scan, Docker image builds, and docs pass.

### Phase 8B

- [ ] All 14 live repository-resolution checks pass at recorded commits.
- [ ] The six proposal representatives pass their applicable download/load/chat/
      tools/reasoning/vision/MTP/restart/repair/change-quant matrix.
- [ ] Each claimed runtime image/hardware lane has evidence. Unavailable required
      hardware is `BLOCKED`, never recorded as a pass.

## 7. Deviation and failure protocol

When any gate fails, stop dependent dispatch.

1. Record the attempt and classify it in `STATUS.md`:
   `build/test red`, `missing DoD`, `scope overlap`, `contract drift`,
   `migration ambiguity`, `security finding`, `environment blocker`, or
   `hidden/masked failure`.
2. Preserve exact command output, fixture, request, response, and affected files.
3. Re-dispatch the same phase with only the failed checks and evidence. Re-run the
   full phase gate, not only the failed command.
4. Do not repair an owning phase from a later phase.
5. Do not discard user work or run destructive Git commands. If unwanted edits
   exist, present the file list and obtain approval for the remediation.
6. Cap correction attempts at two. A third attempt requires user review because
   the contract, brief, or environment may be wrong.
7. If a locked decision changes, mark every dependent completed phase stale and
   re-run it in dependency order.

## 8. Final acceptance

Execution is complete only when:

- [ ] Every proposal §9 acceptance criterion maps to a passing gate and evidence
      recorded in `STATUS.md`.
- [ ] Every phase is `DONE`; no deviation or migration issue remains open.
- [ ] Exactly 14 curated entries ship and pass schema plus live resolution.
- [ ] Curated model+quant install works from Settings and Home with no advanced
      knowledge required and no automatic quant choice.
- [ ] Complete sharded sets, projectors, preset, profile, minimal runtime identity,
      provenance, and operation history agree after install.
- [ ] Fleet and alias settings are both UI-writable in their proper stores; normal
      operation requires no compose or hand-edited INI changes.
- [ ] Repair uses recorded commit/artifacts; change quant is staged and atomic;
      existing models remain operator-managed until explicit adoption.
- [ ] Runtime profile fields govern tool requests; catalog JSON contains no server
      arguments or tool policy.
- [ ] Full automated, security, migration, image, live repository, and representative
      runtime qualification gates pass.
- [ ] Operator, migration, troubleshooting, manifest-authoring, and release docs
      match the shipped behavior.

The orchestrator then records final test counts, qualification evidence, migrations,
decision changes, retries, and remaining environmental limits for the user.
