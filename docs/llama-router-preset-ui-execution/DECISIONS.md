# Curated Local Llama — Locked Decisions

Last updated: 2026-07-10 · Status: **LOCKED, subject only to Phase 0 physical proof**

Every subagent reads this file. A physical contradiction found in Phase 0 stops
execution; the orchestrator updates this file and every dependent brief before any
product implementation continues.

## D1. Sharded GGUF representation

- A quant is one ordered artifact group: either one GGUF or all shards from
  `00001-of-N` through `N-of-N`.
- The stable quant ID is derived from the normalized quant label, never array order.
- Incomplete, duplicate, mixed-total, or ambiguously named shard groups are errors.
- All shards are downloaded into one target directory and recorded individually.
- The INI `model` field points to the first ordered shard, provided Phase 0 proves
  that contract against the bundled llama.cpp. No repeated `model` keys are invented.
- If Phase 0 disproves first-shard loading, stop and record the exact supported
  representation before Phase 2. Do not substitute a single shard.

## D2. Immutable resolution and durable operations

- Curated clients submit `catalogId`, `catalogVersion`, `quantId`, and
  `resolvedRevision`; they do not submit repository paths, alias, profile, projector,
  target directory, or preset.
- The server reloads the shipped definition by ID and exact version, resolves the
  selected group at the supplied commit, validates the profile/projector/preset, and
  persists one immutable operation input before work begins.
- A missing definition version, changed commit, incomplete group, or identity
  conflict is a hard error. The server never moves to current `main`, picks another
  quant, changes an alias, or renames a model.
- Operation state is durable across API and llama-admin restarts. Each step records
  desired input, completed side effects, error code, remediation, and timestamps.
- SQL `LocalModelOperation` is the authoritative operation and finalization record.
  llama-admin keeps a subordinate runtime journal keyed by the same operation ID and
  immutable-input hash for download/staging/INI side effects only. A journal cannot
  create a catalog row or declare end-to-end completion.
- Phase 2 implements and proves the subordinate journal. Phase 4 makes SQL the sole
  finalization authority and reconciles journal side effects into the SQL operation.
- Completion means artifacts verified, alias section committed, catalog model
  committed, and installation provenance committed. A partial state is never
  returned as completed.

## D3. Installation provenance and artifact integrity

- `LocalModelInstallation` is a dedicated one-to-one record keyed to the catalog
  model. It records management mode, curated ID/version, repository, requested and
  resolved revisions, quant ID/label, exact ordered model/projector artifacts,
  complete alias preset snapshot, and timestamps.
- Each artifact record contains repository path, installed relative path, byte size,
  and digest/ETag when the Hugging Face response provides one.
- Repair verifies size and digest when available. If integrity cannot be proven, the
  UI/API says so; it does not claim a corruption check passed.
- Operation logs are not provenance.
- Deleting a catalog model may cascade its database provenance only after the
  existing explicit runtime/artifact deletion operation succeeds.

## D4. Fleet settings: desired SQL state and applied projection (**SUPERSEDED in Phase 2 cleanup**)

- Phase 2 backend cleanup removes Fleet Llama SQL + API machinery.
- Fleet preset routes and persistence are no longer part of the supported contract.

## D5. Preset scope and write semantics

- Alias presets are `Record<string,string>` using llama.cpp option names without
  leading `--`.
- Infrastructure keys `model`, `mmproj`, and `version` cannot appear in `preset`.
- Curated install/repair uses `presetMode: "replace"` and replaces all alias extras
  with the resolved curated preset.
- Operator Customize/custom patching may use `presetMode: "merge"` and changes only
  submitted extras.
- Known fleet-only keys are rejected by the alias API with an error that identifies
  the Fleet llama server editor.
- The fleet editor is schema-driven and accepts supported fleet keys only. Unknown
  keys are not sent to compose.
- Unknown well-formed keys in the alias editor are accepted as alias-scoped, logged
  in the operation, and any child-spawn error is surfaced against that key.
- Duplicate keys under case/alias normalization, control characters, blank keys,
  invalid value types, and shell fragments are rejected.

## D6. Runtime profile and catalog boundaries

- `Models.RuntimeConfigJson` contains only `routerModelId` and `runtimeProfileId`.
- All model loads send the alias only.
- `RuntimeProfile` gains validated `requestFieldsWhenToolsPresent`.
- `LlamaCppChatClient` applies those exact fields only when tools are present.
- `deepseek_r1` uses `parallel_tool_calls: false`; `qwen3_coder`, `qwen3_5`,
  `qwen3_6`, `gemma4`, and `gpt_oss` use `true` as specified by the proposal.
- Display labels never drive request shaping, projector behavior, or server arguments.

## D7. Migration behavior

- `routerContextSize` and `routerCacheRamMib` move to the matching INI alias as
  `ctx-size` and `cache-ram`, then are removed from runtime JSON.
- `loadParams.model` is discarded after proving it equals the alias. Other
  `loadParams` keys require an explicit reviewed mapping; unmapped keys create a
  migration issue and leave that model operator-managed.
- Row-level `parallelToolCalls` moves to a profile only when all rows using that
  profile agree. Disagreement creates a migration issue; behavior is not changed.
- Existing INI extras are preserved exactly and the model remains operator-managed.
- No source repository, revision, quant, curated identity, or digest is inferred for
  an existing model.
- Migration is idempotent, reportable, and re-runnable. An unresolved issue blocks
  removal of the corresponding legacy field for that model.

## D8. API surface

Canonical routes:

- `GET /api/settings/llama/catalog`
- `GET /api/settings/llama/catalog/{catalogId}/quants`
- `POST /api/settings/models:add` for curated, custom, and attach onboarding
- `GET /api/settings/llama/operations/{operationId}`
- `GET /api/settings/llama/installations/{modelId}`
- `POST /api/settings/llama/installations/{modelId}/change-quant`
- `POST /api/settings/llama/installations/{modelId}/repair`
- `POST /api/settings/llama/installations/{modelId}/customize`
- `POST /api/settings/llama/installations/{modelId}/adopt`
- `GET /api/settings/llama/router/entries`
- `PUT /api/settings/llama/router/entries/{alias}`
- fleet routes from D4.
- `GET /api/settings/llama/migration/status`
- `GET /api/settings/llama/migration/issues`

Internal llama-admin routes and payload fixtures frozen in Phase 0:

- `GET /admin/catalog`
- `GET /admin/catalog/{catalogId}/quants`
- `POST /downloads` and `GET /downloads/{operationId}`
- `GET /router/entries` and `POST /router/entries`
- `GET /runtime/fleet-preset` and `PUT /runtime/fleet-preset`

The old internal download/status routes may remain temporarily for in-flight upgrade
compatibility, but new clients use operations. Compatibility code must preserve the
same error and partial-state semantics.

Lifecycle route mapping:

- Proposal §3.6 Change quant and Repair map to D8 installation action routes.
- Proposal §3.6 Customize maps to D8 `customize`.
- Proposal §3.7 custom installation maps to `POST /api/settings/models:add`.
- Proposal §3.8/§7.2 attach/adoption map to `models:add` existing-alias mode and D8
  `adopt`.

Every route in D8 uses the existing Admin settings authorization policy. Integration
tests require unauthenticated `401`, non-Admin `403`, and Admin contract behavior.

## D9. UI state and selection (**SUPERSEDED in Phase 2 cleanup**)

- Settings and Home map into one shared onboarding state machine and request builder.
- Entering curated quant selection starts with no selected quant, even when the
  manifest carries recommendation labels.
- A repository refresh that removes the selected group clears the selection and
  shows an explicit changed-repository error.
- Technical details are read-only in curated mode.
- Phase 2 cleanup removes Customize and Fleet editors from the active backend contract.
- UI state continues to support curated install, repair, adopt, router entries, and runtime inventory flows.

## D10. Verification tiers

- Deterministic unit/integration tests use local repository fixtures and run on every
  phase gate.
- A live manifest-drift suite resolves all 14 repositories and runs in release CI or
  an equivalent recorded gate with the required Hugging Face token.
- Hardware qualification uses the six representatives required by proposal §8 and
  records exact image, accelerator, model commit, quant, preset, and result.
- A required live or hardware lane without infrastructure is `BLOCKED`, not passed
  or skipped.
- Security findings are fixed in code. No alert suppression is accepted.

## D11. Manifest shape and version semantics

- Llama uses a dedicated
  `docs/native-ai-migration/catalog/schema.llama.json`; do not make the existing
  `entries[]` model schema accept two incompatible root shapes.
- The llama manifest root is
  `{ "schemaVersion": 1, "task": "llama", "version": "...", "models": [...] }`.
- In schema v1, `catalogVersion` is the manifest-level `version` inherited by every
  definition. Definitions do not advance independently.
- Independently versioned definitions require an explicit future schema version and
  migration; the v1 server rejects mixed/unknown version semantics.
- Committed Phase 0 contract fixtures are mandatory and are parsed by Python, C#,
  and TypeScript contract tests.
- Python schema tests use `jsonschema`'s Draft 2020-12 validator
  (`Draft202012Validator.check_schema` plus instance validation). Phase 1A adds the
  package through the project dependency mechanism if it is not already available.

## D12. Proposal conflict: MTP preset authority

Proposal §§4.10, 4.14, and 4.16 are authoritative for MTP entries. The illustrative
§5.3 resolution object incorrectly includes `image-min-tokens` for an MTP model.

- MTP definitions have `mmproj: null`.
- MTP presets contain `ctx-size`, `spec-type=draft-mtp`, and
  `spec-draft-n-max=2`.
- MTP definitions do not contain `image-min-tokens`.
- Schema and resolution tests reject projector/vision keys on MTP rows.
