# Llama.cpp Multi-Model Support Plan (Qwen + Gemma + Guide Builder Parameters)

## Summary
This plan adds Gemma support **alongside** existing Qwen support in the current `llama.cpp` infrastructure, while redesigning local model abstractions so future model families can be onboarded with small, well-scoped code changes rather than bespoke code paths.

The design is strict and fail-closed:
- No fallback behavior.
- Notebook/Guide model pinning remains authoritative.
- `provider = "llama-cpp"` models must use one canonical `LocalRuntimeJson` shape.
- Guide Builder model parameter UI is a first-class part of this design, not an afterthought.

This is a dev-only system. Runtime config is validated on read/write, and normal JSON deserialization ignores extra fields.

## Goals
1. Keep current architecture and add Gemma as an option (not a replacement).
2. Make `llama.cpp` model behavior model-family-aware through explicit abstractions.
3. Close current configuration expressiveness gaps (already visible with Qwen recommendations).
4. Keep DB table shape unchanged while enforcing a strict canonical local runtime config.
5. Ensure Guide Builder exposes model-specific recommended parameter values for guides/assistants.
6. Ensure future local model families can be added with a small, well-scoped code change (new profile handler + tests).

## Non-Negotiables
- No runtime fallbacks.
- No silent defaults for missing required local model config.
- If a required local model config is invalid, load/inference must fail with actionable errors.
- Models are pinned by Guide/Notebook selection; runtime loading must follow that selection.
- Guide-level parameter values must be validated against the selected model profile rules.
- The existing notebook-level and conversation-level runtime check and load orchestration flow is canon and must be preserved.

---

## Current State Snapshot
- Local runtime and scripts are Qwen-centric today (`docker/llama`, compose, upsert script).
- `llama-cpp` provider routing already depends on `Models.LocalRuntimeJson.routerModelId`.
- Runtime load/unload orchestration exists and already gates conversation streaming.
- Model catalog UI/API stores `LocalRuntimeJson` as a free-form string with only syntactic JSON checks.
- `LlamaCppChatClient` currently has Qwen-oriented message normalization and reasoning toggling behavior.
- Guide Builder currently supports `temperature`, `topP`, and `reasoningEffort` with generic UI rules.

### Prerequisites
The following runtime check bugs must be verified as fixed before this work begins:
- `AssistantDefinitionDto` must include `Id` so client-side runtime checks fire.
- Notebook-level runtime check must fire in `NotebookDetailsContent` when assistants load (not only inside `ConversationProvider`).
- 409 from the send-message endpoint must be parsed and trigger the load dialog.

---

## Canonical `LocalRuntimeJson` (Required for `provider="llama-cpp"`)

### Canonical Shape
```json
{
  "routerModelId": "string",
  "runtimeProfileId": "string",
  "loadParams": { "model": "..." }
}
```

### Field Rules
- `routerModelId` (required): router/model identifier used for runtime calls; `.gguf` suffix is not allowed.
- `runtimeProfileId` (required): stable ID that selects model-family request behavior (request shaping, reasoning mapping, sampling defaults, guide parameter policy).
- `loadParams` (optional object): explicit `/models/load` parameters passed to the llama server. Serialized to JSON at the HTTP call boundary, not stored as a stringified JSON string.

### Jinja Templates
The `--jinja` flag is a server-level startup option, not a per-model setting. All current and planned model families (Qwen, Gemma) require Jinja for proper chat template and tool call support. The llama server must always be started with `--jinja`. There is no per-model `requiresJinja` field.

### Enforcement
- Required for all `llama-cpp` models at create/update.
- Unknown fields are ignored by normal JSON deserialization.
- Invalid canonical shape blocks save and returns detailed validation errors.

---

## Runtime Profile Abstraction

### Purpose
Decouple model-family behavior from hardcoded client logic by introducing a profile registry keyed by `runtimeProfileId`.

### Initial Profiles
- `qwen3_5`
- `gemma4`

### Profile Responsibilities
- Request content shaping expectations.
- Reasoning/thinking parameter mapping.
- Sampling constraints/default policy.
- Optional request extras required by that family.
- Guide parameter policy (supported fields, allowed ranges/options, recommended defaults).

Per-family profiles handle the "how to talk to this model family" concern. Per-quant parameter tuning is handled at the guide/assistant level within the bounds defined by the profile.

### Strictness
- Unknown `runtimeProfileId` blocks runtime use with an actionable error.
- Profile-validation mismatch between config and request behavior blocks inference.

---

## Implementation Plan

### 1) Backend: Canonical Local Runtime Validation
- Add typed DTO/parser for canonical `LocalRuntimeJson`.
- Validate in settings model create/update path for `provider="llama-cpp"`.
- Return specific errors indicating missing/invalid field names.
- Keep DB columns unchanged (`Model` entity remains as-is).

### 2) Backend: Runtime Profile Registry (Single Source of Truth)
- Introduce a typed runtime profile registry on server keyed by `runtimeProfileId`.
- For each profile, define:
  - Llama request mapping policy.
  - Guide parameter policy:
    - Supported guide parameters.
    - Allowed ranges/options.
    - Recommended defaults (sourced from current llama.cpp + model-family guidance).
- Keep policy definitions in code so Qwen/Gemma recommendations are explicit, testable, and version-controlled.

### 3) Backend: Routing + Runtime Resolution
- Extend routing path to resolve local model runtime profile from `LocalRuntimeJson`.
- Pass resolved profile into llama client request mapping and guide parameter validation paths.
- Preserve existing model pinning behavior in conversation flow.

### 4) Backend: Llama Request Mapping by Profile
- Refactor current Qwen-oriented mapping into profile-aware strategy.
- Keep common OpenAI-compatible envelope; vary family-specific mappings via profile handlers.
- Ensure reasoning behavior is explicit per profile (no implicit fallback behavior).

### 5) Backend: Guide/Assistant Parameter Validation by Model Profile
- Validate guide/assistant `temperature`, `topP`, and `reasoningEffort` against selected model profile policy.
- Reject out-of-policy values with clear validation errors.
- Keep existing `ReasoningChoicesJson` support, but derive effective policy from runtime profile to avoid drift.

### 6) Backend: Catalog Response Enrichment for Guide Builder
- Extend model catalog DTO returned to Guide Builder with resolved guide parameter policy:
  - Supported fields.
  - UI bounds/options.
  - Recommended defaults.
- This lets Guide Builder render model-specific controls and defaults without hardcoding Qwen/Gemma logic in frontend.

### 7) Backend: Runtime Load/Unload Orchestration
- Keep current runtime orchestration endpoints and state machine.
- Ensure load requests are catalog-driven from canonical `LocalRuntimeJson`.
- Keep readiness preflight as a hard gate.

### 8) Settings UI: Hybrid Form + JSON
- Keep raw JSON capability for advanced edits.
- Add guided fields for canonical required properties:
  - Router Model ID
  - Runtime Profile
  - Load Params
- Validate client-side for quick feedback; server remains source of truth.

### 9) Guide Builder UI: Model-Specific Parameter UX
- Update Guide Builder configuration panel to consume profile-backed parameter policy from model catalog.
- Keep current polished sliders/select UX, but drive it from model policy rather than fixed generic assumptions.
- For each selected model:
  - Auto-apply recommended defaults only when fields are unset.
  - Show model-specific recommended values in labels/help text.
  - Constrain inputs to model-allowed ranges/options.
  - Preserve user-entered overrides when still valid for that model.
  - Reconcile invalid values immediately when switching models (with clear UI indication).
- Ensure guide-level and assistant-level editors behave consistently.

### 10) Docker/Script Alignment
- Align model path usage between compose and scripts.
- Align container naming between start/stop scripts.
- Ensure llama server is always started with `--jinja`.
- Keep explicit runtime loading model (no autoload assumptions).
- Update `docker/llama/README.md` to reflect profile-based multi-model behavior.
- Depends on the canonical shape (step 1) and model definitions (step 11) being finalized.

### 11) Model Definition Updates
- Fix existing `llama-cpp` data to conform to canonical `LocalRuntimeJson` shape.
- Update Qwen rows using canonical shape and `runtimeProfileId = "qwen3_5"`.
- Add Gemma model rows in catalog using canonical shape and `runtimeProfileId = "gemma4"`.
- Model data is created as part of this plan using the provided documentation.

### 12) Tests
- Settings validation tests for canonical shape and strict errors:
  - Missing required field → specific error naming the field.
  - Unknown field present → rejected.
  - `routerModelId` with `.gguf` suffix → rejected.
  - `loadParams` as stringified JSON string instead of object → rejected.
  - Valid canonical shape → accepted.
- Runtime profile registry tests for Qwen and Gemma policies:
  - Known `runtimeProfileId` → profile resolved.
  - Unknown `runtimeProfileId` → actionable error.
  - Request payload snapshot tests per profile.
  - Guide parameter bounds enforced per profile.
- Routing tests verifying profile resolution and failure modes:
  - Model with valid `LocalRuntimeJson` → profile resolved, client created.
  - Model with missing `LocalRuntimeJson` → error before inference.
  - Model with unknown `runtimeProfileId` → error before inference.
- Llama client tests for profile-specific request payload behavior.
- Runtime orchestration tests for load/unload/readiness using catalog config.
- Guide/assistant validation tests for model-specific parameter constraints and recommended defaults behavior.
- Frontend Guide Builder tests for:
  - Model switch behavior.
  - Parameter bounds/options rendering.
  - Recommended default application.
  - Invalid-value reconciliation.
- End-to-end notebook flow tests:
  - Qwen pinned notebook path.
  - Gemma pinned notebook path.
  - Invalid config blocked with clear error.
  - Model switch: Qwen loaded → guide changed to Gemma → runtime unloads Qwen, loads Gemma, dialog shows correctly.

---

## Acceptance Criteria
- A `llama-cpp` model cannot be saved without canonical `LocalRuntimeJson`.
- Qwen and Gemma are both selectable and runnable through the same notebook flow.
- Runtime profile selection controls request behavior without hardcoding per-model IDs.
- Guide Builder shows model-specific parameter recommendations and enforces model-specific constraints.
- Guide/assistant saves fail when parameter values violate selected model policy.
- Invalid local model config fails before inference with actionable errors.
- No fallback loading or inference behavior exists for missing/invalid config.
- Switching a guide's model from Qwen to Gemma (or vice versa) on a notebook with a loaded model triggers unload of the current model and load of the new model through the existing runtime orchestration. The notebook-level and conversation-level runtime checks detect the mismatch and show the load dialog.
- The llama server always runs with `--jinja`.

---

## Rollout Sequence
1. Fix existing runtime check bugs (prerequisites).
2. Add canonical parser/validator + tests.
3. Add runtime profile registry (request policy + guide parameter policy) + tests.
4. Wire profile-based request mapping and guide/assistant parameter validation.
5. Enrich catalog model response with guide parameter policy.
6. Update Guide Builder UI to consume model-specific policy/recommended values.
7. Update settings API/UI to enforce/expose canonical local runtime fields.
8. Fix existing data and add Gemma model rows.
9. Align docker/scripts/docs (depends on canonical shape and model definitions).
10. Run test matrix and notebook validation scenarios.

---

## Risks and Mitigations
- Risk: Profile behavior drift vs model recommendations.
  - Mitigation: Profile-specific tests with fixed expected payload snapshots and explicit recommended defaults in registry.
- Risk: UI and API validation mismatch.
  - Mitigation: Server-side strict validation is authoritative; UI mirrors server rules.
- Risk: Hidden coupling to old Qwen assumptions.
  - Mitigation: Explicit profile abstraction and tests that exercise both Qwen and Gemma paths.
- Risk: Guide Builder UX regressions when switching models.
  - Mitigation: UI tests for switch/reconcile/default scenarios and clear inline validation messaging.
- Risk: Runtime orchestration bugs (observed during current development).
  - Mitigation: Prerequisites section requires existing runtime check bugs to be verified fixed before this work begins. Model-switch scenario is an explicit acceptance criterion with end-to-end test coverage.
