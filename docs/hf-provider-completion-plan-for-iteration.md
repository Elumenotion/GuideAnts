# Hugging Face Provider Completion Plan (Iteration Base)

Last updated: 2026-05-18

## 1. Purpose

Create a status-aware, implementation-grounded plan to finish Hugging Face provider work across:

- Chat routing (`hf-inference-chat`)
- Non-chat routed services (Embeddings, Image Generation, Speech Transcription, Speech Synthesis)
- Settings UI and onboarding surfaces
- Validation/readiness/contracts/tests/docs

This document is a base plan for review, not final scope lock.

## 2. Current State Snapshot

Status legend:

- `Implemented`: backend runtime path exists and is wired
- `Exposed`: operator can select/configure via current Settings UX
- `Hidden`: code path exists but UI intentionally suppresses it
- `Partial`: implemented with known functional limitations

### 2.1 Chat (`hf-inference-chat`)

- Routing is implemented and wired in chat factory/validator/readiness:
  - `RoutingChatCompletionClientFactory`: parse + dispatch (`hf-inference-chat`) to `HuggingFaceChatClientFactory`.
  - `ChatTargetValidator.KnownProviders` includes `hf-inference-chat`.
  - `RoutingReadinessService.MapChatProviderToSection` maps to `HuggingFace`.
  - `LlmProviderResolver` usage mapping includes Hugging Face.
- Chat client is native HF Router chat-completions based:
  - Endpoint uses `HuggingFace:RouterBaseUrl` + `/chat/completions`.
  - Supports streaming/tool-calls mapping in `AntRunner.Chat.HuggingFace`.
- UI state: `Hidden` in Add Model and Runtime Profile picker:
  - `HIDDEN_CHAT_MODEL_PROVIDERS` includes `hf-inference-chat`.
  - Add wizard filters provider options by that set and clears preselected hidden values.
  - Runtime profile dialog also filters hidden providers.
- Edit surface exists but is minimal:
  - HF catalog provider form currently only shows informational text (no HF-specific fields).

### 2.2 Non-chat services (provider section `HuggingFace`)

- Backend contracts and metadata exist for all 4 services:
  - `SpeechTranscription.HuggingFace.Inference`
  - `SpeechSynthesis.HuggingFace.Inference`
  - `ImageGeneration.HuggingFace.Inference`
  - `Embeddings.HuggingFace.Inference`
- Provider field metadata exists (ModelId, AllowedModels, TimeoutSeconds where applicable).
- Readiness/model-capability blockers exist in `RoutingReadinessService` (allowlist + heuristic fallback).
- Service implementations are wired and functional.
- Endpoint behavior split:
  - Chat uses HF Router (`router.huggingface.co/v1`).
  - Non-chat services currently use hardcoded `https://api-inference.huggingface.co/models/{modelId}`.
- Known implementation limitation:
  - HF image adapter currently does not forward size/count/output-format controls (explicitly documented in UI copy).

### 2.3 Settings UI / operator surfaces

- Connections:
  - `HuggingFace` section exists and can be edited (`Token`, `RouterBaseUrl`).
- Service editors:
  - HF provider help text exists in editor components.
  - Provider selection is still effectively hidden by temporary cloud-provider filters:
    - `HIDDEN_CLOUD_PROVIDER_SECTIONS` includes `HuggingFace`.
    - `useServiceEditorController` filters providers by hidden-cloud sections.
- Chat model catalog:
  - HF provider components exist in add/edit switch cases.
  - Add flow hides HF provider options using `HIDDEN_CHAT_MODEL_PROVIDERS`.
- Add AI Services wizard:
  - No dedicated HF onboarding path (current providers: foundry/google/openai/local-ai).

### 2.4 Tests

- Chat HF coverage exists:
  - provider-native chat client tests
  - routing factory tests
  - validator/readiness mapping tests
  - usage resolver tests
- Non-chat HF service tests exist for embeddings/image/transcription/synthesis.
- Settings/service-editor contract tests include HF provider IDs and section dependencies.
- UI tests include provider-to-section map and HF repository-picker-related behavior.

### 2.5 Hugging Face API Grounding (By Service)

This section maps each GuideAnts HF integration to the Hugging Face API surface it is currently using, and the concrete request/response behavior in code.

#### 2.5.1 Chat (`hf-inference-chat`)

- HF docs/API family:
  - Inference Providers `chat-completion` task (OpenAI-compatible Chat Completions).
  - Base URL pattern: `https://router.huggingface.co/v1` + `/chat/completions`.
  - Supports streaming and tool/function calling.
- Current implementation:
  - Endpoint composed from `HuggingFace:RouterBaseUrl` + `/chat/completions`.
  - Auth uses `Authorization: Bearer <HF token>`.
  - Request maps model/messages/tools/temperature/top_p/reasoning_effort/stream.
  - Streaming consumes SSE and accumulates deltas + tool call fragments.
- Code anchors:
  - `src/server/AntRunner.Chat/AntRunner.Chat.HuggingFace/HuggingFaceChatClient.cs` (request build, endpoint, auth, stream parsing).
  - `src/server/GuideAntsApi/Settings/ProviderConfigurationResolver.cs` (HF chat config resolution).
  - `src/server/GuideAntsApi/Settings/SettingsSectionRegistry.cs` (`HuggingFace:Token`, `HuggingFace:RouterBaseUrl`).
- Status:
  - API alignment is strong for chat path.

#### 2.5.2 Embeddings (`Embeddings.HuggingFace.Inference`)

- HF docs/API family:
  - Inference Providers `feature-extraction` task.
  - Header auth: `Authorization: Bearer hf_...`.
  - Payload core field: `inputs` (string or string[]), optional params include `normalize`, `prompt_name`, `truncate`, `truncation_direction`.
  - Response shape: vector(s), either `number[]` or `number[][]`.
- Current implementation:
  - Calls fixed endpoint `https://api-inference.huggingface.co/models/{modelId}`.
  - Sends JSON `{ "inputs": <string|string[]> }`.
  - Parses both response shapes (`number[]` and `number[][]`).
  - Uses Bearer token from `HuggingFace:Token`.
- Code anchors:
  - `src/server/GuideAntsApi.BackgroundJobs/Services/Embeddings/HuggingFaceEmbeddingService.cs`.
- Status:
  - Semantically aligned with feature-extraction payload/response, but endpoint family is legacy `api-inference` rather than Router-based provider route.

#### 2.5.3 Image Generation (`ImageGeneration.HuggingFace.Inference`)

- HF docs/API family:
  - Inference Providers `text-to-image` and `image-to-image` tasks.
  - Endpoint family: `https://router.huggingface.co/{provider}/{providerId}` (router-based, not legacy `api-inference`).
  - Provider routing: resolved dynamically from HF model metadata API (`/api/models/{modelId}?expand[]=inferenceProviderMapping`).
  - Auth: `Authorization: Bearer <HF token>`.
- Current implementation:
  - Provider resolution:
    - Calls model metadata API to retrieve the `inferenceProviderMapping` object.
    - Iterates providers in declaration order, skipping `fal-ai` (disabled, see note below).
    - Selects first live provider whose task matches (`text-to-image` or `image-to-image`).
  - Text-to-image (replicate):
    - URL: `https://router.huggingface.co/replicate/v1/models/{providerId}/predictions` (no `version` field; only for non-hash `providerId`).
    - URL (hash-pinned): `https://router.huggingface.co/replicate/v1/predictions` with `{ "version": "<hash>", "input": {...} }`.
    - Payload: `{ "input": { "prompt", "width", "height" } }`.
    - Response: synchronous with `Prefer: wait`; result in `output` (string or string[]).
  - Image-to-image (replicate):
    - URL and version logic: same as text-to-image.
    - Payload: `{ "input": { "prompt", "image": "<data-url>", "width", "height" } }`.
    - Response: synchronous with `Prefer: wait`; result in `output`.
  - Text-to-image / image-to-image (wavespeed):
    - URL: `https://router.huggingface.co/wavespeed/api/v3/{providerId}`.
    - Payload: `{ "prompt", "width", "height" }` / `{ "prompt", "image", "width", "height" }`.
  - `fal-ai` disabled: skipped during provider resolution (`fal-ai` entries in the mapping are ignored). Can be re-enabled by removing the skip guard in `ResolveHuggingFaceProviderAsync`.
  - `size`, `n`, and `outputFormat` are currently ignored; `width`/`height` are resolved from `size` string.
- Code anchors:
  - `src/server/GuideAntsApi/Services/NotebookImageService.HuggingFace.cs` (`GenerateImageViaHuggingFace`, `GenerateImageEditViaHuggingFace`, `ResolveHuggingFaceProviderAsync`, `BuildHuggingFaceTextToImageRequest`, `BuildHuggingFaceImageToImageRequest`, `ReadHuggingFaceImageResultAsync`).
- Status:
  - Image generation uses the correct HF Router + Inference Providers path. `fal-ai` is temporarily disabled. replicate and wavespeed are the active providers. Advanced task parameters (`guidance_scale`, `negative_prompt`, `num_inference_steps`, `seed`, etc.) are not yet wired.

#### 2.5.4 Speech Transcription (`SpeechTranscription.HuggingFace.Inference`)

- HF docs/API family:
  - Inference Providers `automatic-speech-recognition` task.
  - Payload may be raw audio bytes or base64 input form with optional parameters (for example timestamps).
- Current implementation:
  - Calls `https://api-inference.huggingface.co/models/{modelId}`.
  - Sends raw audio bytes (`ByteArrayContent`) with content-type from uploaded file.
  - Uses Bearer token from `HuggingFace:Token`.
  - Parses response as JSON object with `text`.
- Code anchors:
  - `src/server/GuideAntsApi/Services/Components/SpeechTranscriptionService.cs` (`TranscribeViaHuggingFaceWithDurationAsync`).
- Status:
  - Input mode is aligned (raw bytes accepted), but optional ASR features like timestamp outputs are not exposed/configurable.

#### 2.5.5 Speech Synthesis (`SpeechSynthesis.HuggingFace.Inference`)

- HF docs/API family:
  - No first-class Inference Providers task doc currently used by this code path for TTS; implementation behaves like legacy Inference API task call.
- Current implementation:
  - Calls `https://api-inference.huggingface.co/models/{modelId}`.
  - Sends JSON `{ "inputs": "<plain text>" }` (SSML stripped before call).
  - Expects binary audio response and writes bytes directly to output file.
  - Uses Bearer token from `HuggingFace:Token`.
- Code anchors:
  - `src/server/GuideAntsApi/Services/Components/SpeechSynthesisService.cs` (`SynthesizeViaHuggingFaceAsync`).
- Status:
  - Functional but under-documented vs the newer Inference Providers task matrix; explicit HF API contract source should be pinned in docs/tests before claiming full support.

#### 2.5.6 Cross-Service API Reality (Important)

- HF integration is currently split across two API families:
  - Chat uses Router/OpenAI-compatible path (`router.huggingface.co/v1/chat/completions`).
  - Image generation uses the HF Router + Inference Providers path (`router.huggingface.co/{provider}/{providerId}`) with dynamic provider resolution from model metadata.
  - Embeddings, speech transcription, and speech synthesis use fixed `api-inference.huggingface.co/models/{modelId}`.
- Practical consequence:
  - `HuggingFace:RouterBaseUrl` affects chat only.
  - Image generation uses the HF Router but with its own hardcoded `https://router.huggingface.co` base.
  - Other non-chat services ignore `RouterBaseUrl` today.
  - Operator docs must explicitly state this to avoid false expectations.

## 3. Key Gaps To Close

1. Exposure gap (primary): HF is implemented but intentionally hidden in key operator selection UIs.
2. Endpoint model split must be explicitly documented: chat uses HF Router base URL; non-chat uses fixed `api-inference` endpoint.
3. HF catalog provider UX is minimal (info-only forms), limiting operator confidence/diagnostics.
4. Temporary hiding remains in place in parts of the UI and must be removed for full availability.
5. HF onboarding path is incomplete (no wizard path, no guided first-run for HF cloud stack).
6. API-contract drift risk: non-chat request options currently expose only a subset of documented task parameters (ASR timestamps, text-to-image controls, etc.).

## 4. Iteration Goals

1. Complete a full HF provider implementation pass across all in-scope services in this iteration:
   - chat
   - embeddings
   - image generation/edit
   - speech transcription
   - speech synthesis
2. Remove temporary hardcoded hiding and expose HF provider paths directly in the product surfaces.
3. Deliver full HF support in both:
   - Add AI Services wizard (first-run/onboarding path)
   - Settings UI surfaces (connections, service editors, chat catalog, runtime profiles)
4. Lock and document the current endpoint architecture as-is:
   - chat on HF Router (`router.huggingface.co/v1/chat/completions`)
   - non-chat on HF Inference API (`api-inference.huggingface.co/models/{modelId}`)
5. Close operator UX and docs gaps so implementation state and support state are unambiguous.

## 5. Non-goals (this iteration base)

- Full redesign of all provider onboarding flows.
- New provider families or unrelated refactors.
- Architecture changes away from the current split endpoint model.

## 6. Proposed Iteration Plan

### Phase A: Exposure Cleanup

1. Remove hardcoded hiding for HF provider paths in:
   - Chat model provider selection
   - Runtime profile provider selection
   - Service provider selection rows
2. Keep logic straightforward: no new feature flags or rollout toggles.
3. Remove wording and logic patterns that imply partial enablement (for example, "with HF enabled") and replace with explicit default-visible behavior in supported environments.

Deliverable:

- HF provider surfaces are fully visible and selectable in supported environments without special flags.

### Phase B: Service Editor Enablement

1. Enable HF provider options in service editors directly.
2. Verify field validation/required markers/connection blocker UX when HF is selected.
3. Ensure save-and-activate flows behave correctly with HF modes.

Deliverable:

- Operators can configure all HF non-chat providers through the existing Services UX without hidden-provider exceptions.

### Phase C: Chat Catalog Enablement

1. Enable `hf-inference-chat` in Add Model wizard provider list directly.
2. Keep/edit minimal provider form, but improve operator copy for required connection + model-id expectations.
3. Ensure catalog edit UX remains coherent for existing HF rows.

Deliverable:

- Operators can add/select HF chat models via Settings with behavior consistent with non-chat HF exposure.

### Phase C.1: Add AI Services Wizard Completion (Required)

1. Add Hugging Face as a first-class provider path in Add AI Services wizard.
2. Implement full wizard coverage for HF onboarding, not just provider visibility:
   - required connection prerequisites and validation UX
   - service capability selection and recommended defaults
   - clear model-id expectations by service type
   - save-and-activate behavior consistent with existing providers
3. Ensure wizard output is fully compatible with downstream Settings surfaces (no follow-up manual repair required).

Deliverable:

- A user can complete HF onboarding entirely in Add AI Services wizard and land in a valid, ready state in Settings.

### Phase D: Endpoint Architecture Confirmation

Locked decision:

- Keep current split strategy as-is for this implementation cycle.
- Clarify in UI/docs exactly which setting affects which path.

Deliverable:

- Explicit, documented endpoint architecture with no ambiguity and no migration work in this plan.

### Phase D.1: API Contract Completion by Service

1. Embeddings:
   - Keep `api-inference` endpoint.
   - Document explicit compatibility contract and supported parameters (`inputs` only vs advanced options).
2. Image Generation:
   - Add request-preset wiring for HF parameters (`guidance_scale`, `negative_prompt`, `num_inference_steps`, `width`, `height`, `seed`, scheduler where available).
   - Map UI controls (`size`, `n`, `outputFormat`) to provider behavior or mark as unsupported in validation.
3. Speech Transcription:
   - Add optional timestamp-capability toggle and response parsing path when requested.
   - Include model capability validation for timestamp support where possible.
4. Speech Synthesis:
   - Pin authoritative HF API contract source for TTS path under the current endpoint architecture.
   - Add response content-type checks and optional duration extraction if headers/metadata are available.
5. Shared:
   - Add per-service contract tests asserting payload and response parsing shape against documented expectations.

### Phase E: Quality and Coverage Closure

1. Add tests ensuring HF options are visible in UI state/controller layers.
2. Add regression tests for service-mode save/activate with HF visible.
3. Add cross-check tests to ensure provider lists remain synchronized:
   - validator known providers
   - routing parse map
   - readiness provider map
   - UI provider-to-section map
4. Add/update docs to reflect exposure status and support tier.
5. Add explicit cross-surface consistency assertions so provider definitions cannot drift:
   - provider IDs match between UI selectors, validators, routing maps, and readiness maps
   - provider-to-section mapping is consistent between wizard and settings controllers
   - any HF provider added to one registry must be represented in all required registries

Deliverable:

- Feature-complete HF provider behavior with explicit test-backed status.

## 7. Acceptance Criteria (base)

1. HF provider implementation is complete across all five service areas (chat, embeddings, image, transcription, synthesis), not a partial release.
2. HF is visible by default in supported environments (no hidden-provider gates), and:
   - service editors expose and persist HF modes correctly.
   - chat catalog can create/select HF chat rows.
   - Add AI Services wizard provides full HF onboarding, not a partial/placeholder path.
3. Readiness/validation/routing remain fail-fast with actionable errors.
4. Docs accurately describe HF status, endpoint behavior, and operator guidance.
5. No regressions in provider selection, routing, readiness, and save flows after removing temporary hiding.
6. Each HF service has a documented API contract section (endpoint/auth/payload/response/error shape) linked to code anchors. Image generation anchor: `NotebookImageService.HuggingFace.cs`.
7. Non-chat HF docs clearly state current endpoint family and any unsupported optional task parameters.
8. Cross-surface consistency assertions pass: provider IDs, section maps, readiness maps, validator sets, and routing maps stay synchronized.

## 8. Suggested Work Breakdown (first pass)

1. Remove hardcoded hiding paths and wire full HF visibility into:
   - `connectionSections.ts`
   - `useServiceEditorController.ts`
   - `AddModelWizard.tsx`
   - `RuntimeProfileDialog.tsx`
2. Enable and validate HF non-chat editor flows.
3. Enable and validate HF chat catalog flow.
4. Implement and validate full HF onboarding in Add AI Services wizard.
5. Apply endpoint architecture documentation updates (split model retained).
6. Add synchronization + visibility regression tests.
7. Add API-contract conformance tests (chat, embeddings, image, ASR, TTS) and update HF implementation docs accordingly.

## 9. Locked Decisions

1. HF enablement scope is simultaneous across chat and non-chat surfaces (not phased).
2. HF is included in Add AI Services wizard work in this iteration (not deferred), with full feature coverage.
3. Full wizard + full settings UI delivery in the same iteration is a hard requirement.
4. Risk mitigation for simultaneous release is achieved via test and validation depth, not phased rollout toggles.

## 10. Required Change Inventory

This section is the implementation inventory. Items are required unless explicitly marked optional.

### 10.1 UI and Controller Surfaces

1. Remove HF hiding and expose provider options consistently in:
   - `src/client/.../connectionSections.ts`
   - `src/client/.../useServiceEditorController.ts`
   - `src/client/.../AddModelWizard.tsx`
   - `src/client/.../RuntimeProfileDialog.tsx`
2. Add full HF path in Add AI Services wizard:
   - provider selection entry and onboarding steps
   - prerequisite validation and blocking messages
   - handoff into persisted settings/service modes without manual repair
3. Align provider copy/labels/help text for HF across wizard + settings so requirements are unambiguous:
   - token requirement
   - router vs non-chat endpoint behavior
   - model-id expectations by service
4. Ensure provider-to-section mapping is identical between wizard and settings controllers.

### 10.2 Backend Service/Contract Work

1. Confirm and lock HF chat routing path:
   - `hf-inference-chat` parse + dispatch + readiness section mapping + validator membership.
2. Complete non-chat contract handling:
   - Embeddings: explicit supported parameter set and parser behavior (endpoint: `api-inference`).
   - Image: router-based provider resolution and request building are implemented (endpoint: HF Router + provider resolution; code: `NotebookImageService.HuggingFace.cs`). Remaining: map advanced controls (`guidance_scale`, `negative_prompt`, `num_inference_steps`, `seed`, scheduler when available); re-enable `fal-ai` once stable.
   - ASR: optional timestamps request/parse path and capability checks (endpoint: `api-inference`).
   - TTS: authoritative contract reference + response content-type validation (endpoint: `api-inference`).
3. Harden error handling and messages for all HF services:
   - auth failures, provider throttling, transient upstream failures, malformed responses, unsupported model/task cases, timeout behavior.

### 10.3 Registry/Map Synchronization

1. Ensure the same HF provider IDs are represented across all required registries:
   - UI provider lists
   - validator known-provider sets
   - routing parse maps/factory mappings
   - readiness provider-to-section mappings
2. Add explicit validation/tests that fail when one registry is updated without corresponding updates in others.

### 10.4 Documentation Updates

1. Update HF docs to reflect:
   - full availability in wizard + settings surfaces
   - split endpoint architecture (chat router vs non-chat api-inference)
   - per-service supported/unsupported options and error expectations
2. Keep code anchors current for each contract statement.

## 11. Required Test Inventory

All items below are required merge gates for this iteration.

### 11.1 UI/Controller Tests

1. Visibility tests:
   - HF appears in Add AI Services wizard provider selection.
   - HF appears in service editors, Add Model wizard, and Runtime profile selection.
2. Flow tests:
   - wizard HF onboarding completes and persists valid configuration.
   - service editor save-and-activate works for HF non-chat modes.
   - chat model add/edit flows work for `hf-inference-chat`.
3. Mapping consistency tests:
   - provider-to-section mapping parity between wizard and settings controllers.

### 11.2 Backend Contract Tests (Per Service)

1. Chat (`hf-inference-chat`):
   - request mapping, stream parsing/tool-call assembly, routing dispatch.
2. Embeddings:
   - payload shape (`inputs`) and response parsing (`number[]`/`number[][]`).
3. Image generation/edit:
   - payload construction with supported parameters, binary response handling.
4. Speech transcription:
   - raw audio request path, text parsing, optional timestamp response path.
5. Speech synthesis:
   - input mapping, binary output handling, content-type checks.

### 11.3 Error and Resilience Tests (Per Service)

1. 401/403: actionable auth/configuration errors.
2. 429: throttling surfaced with actionable retry guidance.
3. 5xx/upstream failures: clear transient failure messaging.
4. Timeout/cancellation: fail-fast behavior with deterministic error shape.
5. Invalid/malformed response content: parser safety and actionable diagnostics.
6. Unsupported model/task parameter combinations: validation or clear rejection path.

### 11.4 Cross-Surface Synchronization Tests

1. Provider ID parity across:
   - UI lists
   - validator known-provider sets
   - routing maps
   - readiness maps
2. Section dependency parity:
   - HF provider selections always resolve to the expected connection section(s) in both wizard and settings.

### 11.5 Documentation and Contract Verification

1. Docs include endpoint/auth/payload/response/error shape per HF service.
2. Docs explicitly identify unsupported optional parameters and current behavior.
3. Contract docs remain linked to current code anchors; CI/test check fails if anchors or referenced symbols drift.
