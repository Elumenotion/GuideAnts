# Local Model Onboarding Unification Plan (Settings + Wizard)

This document defines a concrete plan to unify local model onboarding services behind:

- **Settings UI** (`Settings -> Models & Runtime -> Add Model`)
- **Home Add AI Services Wizard** (Local AI path)

Goal: **one domain workflow, one validation source of truth, one operation state model**, reused by both UIs.

## 1. Full Inventory (Current Surface Area)

### 1.1 Client: Request Construction and Validation

1. Settings add-model request builder and validation:
   - `src/client/src/pages/settings/utils.ts`
   - `buildAddModelRequest(...)`
   - `createEmptyAddModelWizardState(...)`
2. Wizard local-model request builder and validation:
   - `src/client/src/components/home/addAiServicesWizard/utils.ts`
   - `buildLocalAiModelRequest(...)`
3. Shared wire contracts (used by both paths but interpreted differently):
   - `src/client/src/types/settings.ts`
   - `AddModelRequest`, `AddModelInstallDto`, `StartModelDownloadRequest`, `ModelDownloadOperationDto`
4. API wrappers:
   - `src/client/src/services/api.ts`
   - `settings.addModel(...)`
   - `settings.getDownloadStatus(...)`
   - Legacy/overlap surface: `settings.startModelDownload(...)`, `settings.attachExistingAlias(...)`

### 1.2 Client: UI State Machines and Polling

1. Settings AddModel operation state and status mapping:
   - `src/client/src/pages/settings/components/catalog/AddModelWizard.tsx`
   - `operationStep(...)`
   - local polling effect against `getDownloadStatus(...)`
2. Wizard local operation state and status mapping:
   - `src/client/src/components/home/addAiServicesWizard/useLocalAiWizardState.ts`
   - `normalizeDownloadStatus(...)`
   - `isDownloadInFlight(...)`
   - per-draft polling loop
3. Third poller for top-level settings active operation:
   - `src/client/src/pages/Settings.tsx`
   - `activeAddOperation` polling effect

### 1.3 Client: Attach Existing Alias Eligibility

1. Settings attach-existing-alias selector:
   - `src/client/src/pages/settings/components/catalog/providers/LlamaCppForm.tsx`
   - filters `inventory` rows for attachable aliases
2. Wizard attach-existing-alias selector:
   - `src/client/src/components/home/addAiServicesWizard/steps/LocalAiModelsStep.tsx`
   - `unattachedAliases` filter

### 1.4 Server: API Entry and Validation

1. Main add-model endpoint and request validation:
   - `src/server/GuideAntsApi/Endpoints/SettingsEndpoints.cs`
   - `MapPost("/models:add", ...)`
   - `ValidateAddModelRequestAsync(...)`
   - `BuildLlamaLocalRuntimeJson(...)`
   - `BuildStartModelDownloadRequest(...)`
2. Download operation endpoints:
   - `src/server/GuideAntsApi/Endpoints/SettingsEndpoints.cs`
   - `MapPost("/downloads", ...)`
   - `MapGet("/downloads/{operationId}", ...)`

### 1.5 Server: Llama Orchestration and Runtime Admin Adapter

1. Download service orchestration and validation:
   - `src/server/GuideAntsApi/Services/LlamaCpp/HuggingFaceModelDownloadService.cs`
   - `StartDownloadAsync(...)`
   - `AttachExistingAliasAsync(...)`
   - `ValidateRequest(...)`
2. Inventory projection used by both UIs:
   - `src/server/GuideAntsApi/Services/LlamaCpp/LlamaRuntimeInventoryService.cs`
3. Llama-admin API adapter:
   - `src/server/GuideAntsApi/Services/LlamaCpp/LlamaRuntimeAdminClient.cs`
   - `StartDownloadAsync(...)`
   - `AddOrUpdateRouterEntryAsync(...)`
   - conflict parsing (`TryParseConflictOperation(...)`)

### 1.6 Runtime Worker (llama-admin)

1. Download lifecycle + status model:
   - `docker/build/guideants-ai/llama-admin-service/llama_admin_service.py`
   - `/downloads` start and dedupe checks
   - `/downloads/{operation_id}` status read
   - statuses (`queued`, `resolvingFiles`, `downloading`, `registeringAlias`, `completed`, failure paths)
2. Router entry write/update API:
   - same file, `/router/entries`

## 2. Overlap and Smells (Concrete)

1. **Duplicate request builders**: two client code paths build near-identical `AddModelRequest` payloads.
2. **Duplicate status mapping**: status normalization lives in at least two components (`operationStep`, `normalizeDownloadStatus`) plus top-level poller behavior.
3. **Duplicate install validation**: client(s) + endpoint + service + llama-admin all enforce parts of the same constraints.
4. **Duplicate attach eligibility logic**: Settings and Wizard each re-implement filter rules over inventory.
5. **Split orchestration ownership**: endpoint helpers and service both perform business decisions for same workflow.
6. **API surface overlap**: `models:add` and direct `llama/downloads` endpoints coexist for onboarding semantics, enabling bypass paths.
7. **Semantic drift risk**: even with identical DTOs, per-layer assumptions differ (required/optional fields, fallback defaults, in-flight state handling).

## 3. Target Architecture (Single Ownership Boundaries)

### 3.1 Domain Contract

Introduce a single local onboarding command model:

- `LocalModelOnboardingCommand`
- `LocalModelInstallSource` (`huggingface` | `existingAlias`)
- `LocalModelOnboardingResult`
- `LocalModelOnboardingOperationStatus`

The current `AddModelRequest` remains wire-compatible during migration, but internal logic converts immediately to this canonical command.

### 3.2 Server Ownership

1. **One validator**: `LocalModelOnboardingValidator` (authoritative rules).
2. **One orchestrator**: `LocalModelOnboardingOrchestrator` handling both install sources.
3. **Endpoint thinness**: endpoint only parses, calls validator/orchestrator, maps response.
4. **Download worker adapter**: `LlamaRuntimeAdminClient` becomes transport-only; no business branching beyond protocol translation.

### 3.3 Client Ownership

1. **One shared onboarding module** used by both Settings and Wizard:
   - request mapping
   - client-side pre-validation
   - status normalization
   - in-flight predicate
2. **One polling hook** used by both UIs:
   - `useLocalModelOnboardingOperation(...)`
3. **One attach-alias selector utility** used by both UIs.

## 4. Migration Plan (Phased, Non-Disruptive)

## Phase 0: Freeze and Guardrails

1. Add architecture note in code comments: `models:add` is authoritative onboarding write API.
2. Add telemetry event fields for source UI (`settings` vs `wizard`) and command source (`huggingface` vs `existingAlias`) to compare behavior while migrating.

## Phase 1: Client Unification Library

Create `src/client/src/features/localModelOnboarding/`:

1. `contracts.ts`
   - normalized local types shared by UI surfaces
2. `buildCommand.ts`
   - replaces duplicated request construction logic
3. `validateDraft.ts`
   - shared client preflight validation/errors
4. `status.ts`
   - one status normalizer + one `isInFlight(...)`
5. `selectors.ts`
   - `selectAttachableAliases(inventory)`
6. `useOperationPolling.ts`
   - shared operation polling behavior

Then wire in:

1. Settings `AddModelWizard.tsx` uses new shared builder/status hook.
2. Wizard `useLocalAiWizardState.ts` + `LocalAiModelsStep.tsx` use same builder/status/selectors.
3. `Settings.tsx` poller adopts same hook or is collapsed into single ownership path.

## Phase 2: Server Domain Consolidation

Create `src/server/GuideAntsApi/Services/LlamaCpp/LocalModelOnboarding/`:

1. `LocalModelOnboardingCommand.cs`
2. `LocalModelOnboardingValidator.cs`
3. `LocalModelOnboardingOrchestrator.cs`
4. `LocalModelOnboardingResult.cs`

Refactor:

1. `SettingsEndpoints.cs`
   - `MapPost("/models:add")` delegates to orchestrator.
   - remove install business logic from endpoint helper methods.
2. `HuggingFaceModelDownloadService.cs`
   - either reduced to a dependency of orchestrator or merged into orchestrator and retired.

## Phase 3: Runtime Adapter Simplification

1. Ensure `LlamaRuntimeAdminClient` strictly maps DTOs and HTTP errors.
2. Keep 409 conflict-to-existing-operation translation, but move semantic decisions (retry/dedupe policy) to orchestrator.
3. Enforce mmproj optionality only once (validator + worker schema), avoid re-checks in adapters.

## Phase 4: API Surface Rationalization

1. Keep `POST /api/settings/models:add` as canonical onboarding API.
2. Mark direct onboarding-like usage of `/api/settings/llama/downloads` as internal; prevent UI from bypassing canonical path.
3. Remove dead client wrappers once call graph is clean:
   - `settings.attachExistingAlias(...)` if unused
   - `settings.startModelDownload(...)` for onboarding flows

## Phase 5: Tests and Contract Enforcement

### 5.1 Client Tests

1. Shared builder tests:
   - equivalent inputs from Settings and Wizard produce identical payloads.
2. Shared selector tests:
   - attachable aliases list identical between UIs.
3. Shared status tests:
   - all known statuses map consistently.

### 5.2 Server Tests

1. Validator matrix tests:
   - huggingface text-only
   - huggingface with mmproj
   - existing alias attach
   - duplicate alias and duplicate model id conditions
2. Orchestrator tests:
   - sync attach path
   - async download path
   - retry/conflict behavior reuse

### 5.3 End-to-End Tests

Run same scenarios in both UIs:

1. HF text-only install (no mmproj)
2. HF multimodal install (with mmproj)
3. Attach orphan alias
4. Interrupted download + retry
5. Duplicate submit clicks/race checks

## 6. Exact Work Breakdown by PR

## PR1: Shared client module scaffold (no behavior change)

1. Add `features/localModelOnboarding/*`.
2. Add unit tests for status/selectors/contracts.

## PR2: Settings UI migration

1. Replace Settings builder and status mapping with shared module.
2. Replace Settings attach filter with shared selector.
3. Ensure modal and top-level settings pollers are not duplicating state transitions.

## PR3: Wizard migration

1. Replace Wizard builder and status mapping with shared module.
2. Replace Wizard alias filter with shared selector.
3. Use same polling hook as Settings.

## PR4: Server orchestrator + validator

1. Add orchestrator/validator and migrate endpoint.
2. Minimize or retire duplicated service validation.
3. Preserve wire compatibility for existing clients.

## PR5: API cleanup + regression hardening

1. Remove dead client API wrappers.
2. Add cross-UI parity tests and end-to-end scenarios.
3. Update docs (`adding-models.md`, wizard docs) to match unified flow.

## 7. Acceptance Criteria (Hard Gates)

1. One authoritative server validator for local onboarding.
2. One authoritative client request builder for both UIs.
3. One authoritative client status mapping and in-flight predicate.
4. One authoritative attach-alias eligibility selector.
5. Both UIs produce identical payloads for equivalent user intent.
6. Text-only model onboarding succeeds without mmproj across both UIs.
7. Retry after restart does not create stuck/ghost operation states.
8. No unused onboarding API wrappers remain in client code.

## 8. Deletion Candidates After Migration

1. Duplicate local request construction in:
   - `src/client/src/pages/settings/utils.ts` (llama path)
   - `src/client/src/components/home/addAiServicesWizard/utils.ts` (llama path)
2. Duplicate status mapping in:
   - `src/client/src/pages/settings/components/catalog/AddModelWizard.tsx`
   - `src/client/src/components/home/addAiServicesWizard/useLocalAiWizardState.ts`
3. Potentially unused wrappers in:
   - `src/client/src/services/api.ts` (`attachExistingAlias`, `startModelDownload` for onboarding)
4. Endpoint-level business helpers in:
   - `src/server/GuideAntsApi/Endpoints/SettingsEndpoints.cs` once orchestrator owns flow.

## 9. Execution Controls (Spec-Locked, Non-Incremental Behavior)

1. Freeze onboarding behavior spec before implementation; no untracked interpretation changes during execution.
2. Implement unification under user-managed source-control workflow; do not ship partial behavior changes to main.
3. Merge only when both UIs are fully migrated to shared modules and parity tests pass as a complete set.
4. Treat any spec deviation discovered during implementation as a stop-the-line issue: update spec first, then code.
5. Do not introduce compatibility shims or fallback duplicate paths; remove legacy paths as part of the unification merge.
