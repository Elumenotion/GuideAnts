# Settings Requirements: Service -> Provider -> Model

Last updated: 2026-04-20
Last implemented: 2026-04-20

Implementation references:
- Service editor endpoints: `/api/settings/services/{serviceId}`, `/api/settings/services/{serviceId}/active-provider`, `/api/settings/services/{serviceId}/providers/{providerId}`, `/api/settings/services/{serviceId}/readiness`
- Local model lifecycle proxy endpoints: `/api/settings/services/{serviceId}/local-models`, `/api/settings/services/{serviceId}/local-models/downloads`, `/api/settings/services/{serviceId}/local-models/operations/{operationId}`, `/api/settings/services/{serviceId}/local-models/{modelRef}/select-active`, `/api/settings/services/{serviceId}/local-models/{modelRef}`, `/api/settings/services/{serviceId}/local-models/load`, `/api/settings/services/{serviceId}/local-models/unload`
- Engine lifecycle behavior:
  - For **Image Generation**, `/local-models/load` starts the `sd-server` subprocess against the current active bundle, `/local-models/unload` stops it and releases GPU/RAM, and `/local-models/{bundleId}/select-active` atomically updates the active marker on disk AND hot-swaps the running engine when one is already loaded (no container restart required). The SD service serializes these operations behind an internal lock so concurrent lifecycle requests return HTTP 409 instead of racing.
  - For **Speech Transcription** and **Speech Synthesis**, `/local-models/load` accepts a full payload (`model_id` or `model_path` plus optional runtime knobs) that the sub-service loads into memory. `/local-models/unload` is forwarded to the sub-service as-is; services that have not yet grown a `/admin/unload` handler surface the upstream 404 rather than being hidden behind a gateway error.
- Client: local model list probe uses a typed outcome (`listOutcome` in `api.ts`) so **404** on the list route is treated as **capability unavailable** without logging spurious API errors to the console; unavailable panels show operator copy only (no raw JSON blobs).

## 1) Purpose

Capture the requirements established in this conversation so implementation can proceed without re-litigating core decisions.

This document is authoritative for this workstream.

## 2) Non-Negotiable Model

1. The hierarchy is `service -> provider -> model`.
2. Provider is always subordinate to a specific service.
3. Model is always subordinate to a provider within a service.
4. Providers are fungible at the service layer: for a given service, declared providers are peer alternatives.
5. Provider selection is never a standalone/global UX concern.
6. Generic routing editors are not acceptable for long-term growth.
7. Bespoke service editors are expected and normal.
8. Chat is the exception: chat model comes from assistant configuration and may involve multiple providers concurrently.

## 3) Cross-Service Requirements

### 3.1 UX Requirements

1. Replace generic non-chat routing editor behavior with service-specific editors.
2. Remove misleading cross-service helper text (for example: "Leave blank for services that do not require a catalog model row.").
3. Show provider choices only in the context of the selected service.
4. Do not expose free-form provider-section selection in service editors.
5. Show model selection only when valid and operative for that service+provider path.
6. Keep labels, helper text, and validation messages service-specific.

### 3.2 Routing/Data Requirements

1. Keep `service -> provider -> model` as the conceptual model for all new work.
2. Do not introduce "mode" as a new user-facing product concept.
3. Legacy `ServiceModes` compatibility is implementation detail only.
4. One active route per non-chat service at a time.
5. Validation fails fast for invalid service/provider/model relationships.
6. Any provider declared by a service contract must be selectable as a peer alternative for that service.
7. Provider-specific controls must not be stored as shared service-level fields unless truly cross-provider.
8. Provider-specific values must persist when switching providers with no cross-provider bleed.

### 3.3 Contract/Schema Requirements

1. Service editors derive allowed providers from service contracts (`service -> providers`), not from arbitrary section names.
2. Provider->model compatibility is explicit and enforced.
3. Models are not fungible across providers.
4. For non-chat services, provider identity is canonical in schema/editor data and not inferred from ad-hoc section strings.
5. Schema metadata is sufficient to render provider-specific field groups per service without raw section-key UX.
6. Persisted UI fields that are not runtime-consumed are disallowed unless explicitly marked non-operative diagnostics.

### 3.4 Shared Editor Interaction Contract

1. Every non-chat service editor must render the same top-level structure in this order:
   - Service header (service name + **persisted** active provider label + readiness summary; see §3.5 when the user is editing another provider before save)
   - Provider selector scoped to that service
   - Provider-specific settings groups
   - Service-level settings group (only true cross-provider controls)
   - Save/Cancel action row
2. Provider selectors are rendered as explicit provider choices (segmented control, radio group, or cards), never raw section keys.
3. Switching providers keeps unsaved state per provider during the current edit session and restores the provider's previous draft when toggled back.
4. Save is blocked when current provider-visible required fields are invalid; validation errors are inline and field-specific.
5. Hidden provider fields are never validated against the currently selected provider's save path.
6. Runtime-owned values (docker/env/runtime host keys) are shown as operational dependencies, not editable free-form values, unless editing is explicitly supported.
7. Non-operative persisted fields are either hidden or explicitly labeled as non-operative diagnostics.
8. Each editor must state what controls are currently runtime-consumed vs stored-only.
9. Editor copy is service-specific and must not reuse LLM/chat terminology.
10. Destructive/local-ops actions (download/remove/reset/reload) require explicit confirmation and show operation status.

### 3.5 Service editor UI contract (client)

These rules apply to the shared shell (`ServiceEditorShell` / `ServiceEditorBase`) used by the five service editors on the **Services** tab.

1. **Persisted vs draft provider**  
   - The header line **Active provider** always reflects **`ServiceEditorStateDto.activeProviderId`** from the server (what is persisted).  
   - If the user selects a different provider in the selector before saving, the UI must show a separate, explicit line that they are **editing configuration** for that provider and that it will **not** become active until **Save and activate provider** succeeds. The header must **not** swap the active-provider label to match the draft selection.

2. **Secrets**  
   - Secret fields are represented by `ProviderFieldValueDto` with `isSecret` and `hasValue`; the API does **not** echo secret values in `value`.  
   - The client must show non-revealing affordances (placeholder + short helper text) when `hasValue` is true and the operator has not typed a replacement, so it is obvious a credential is already stored.

3. **Local model operations probe**  
   - For **Image Generation**, **Speech Transcription**, and **Speech Synthesis** when a **local** (non-cloud) provider is selected, the client probes `GET .../local-models`.  
   - **404** or absence of the route on older API builds means **capability unavailable** for that deployment: the UI shows a single operator-facing explanation. It must **not** treat this as an unexpected failure that floods the browser console, and must **not** render raw JSON error payloads in the panel for this state.  
   - When the probe returns **200**, the panel may show structured state (for example a formatted list/JSON of bundles or models) as implementation allows.

4. **Actions when unavailable**  
   - **Download** / select-active / remove (and any prompt-driven flows) must be **disabled or omitted** when the local-model capability is unavailable, so users cannot open dead-end dialogs.

5. **Consistency**  
   - Unavailable copy and behavior for local model operations must be consistent across the three services that expose the panel.

## 4) Service Requirements

### 4.1 Embeddings

Providers under `Embeddings`:

1. Azure OpenAI Embedding
2. Local Embedding HTTP

Provider-scoped controls:

1. Azure OpenAI Embedding: `Endpoint`, `ApiKey`, `Deployment`
2. Local Embedding HTTP: `TimeoutSeconds`, `LocalMinIntervalMs`
3. Local runtime host dependency: `LocalServiceHosts:EmbeddingsBaseUrl`

Runtime requirements:

1. Local controls (`TimeoutSeconds`, `LocalMinIntervalMs`) affect local behavior only.
2. Azure controls (`Endpoint`, `ApiKey`, `Deployment`) affect Azure behavior only.
3. Switching provider (Azure <-> Local) does not change service identity/editor type.
4. Local request metering behavior (single in-flight + configured pacing) remains explicit in UX.
5. Local dimensional adaptation behavior (source 1024 -> stored 1536) is provider behavior, not a generic service knob.

Editor requirements:

1. Layout and flow:
   - Header: "Embeddings" + active provider + readiness summary.
   - Provider selector options: `Azure OpenAI Embedding` and `Local Embedding HTTP` only.
   - Panels: `Provider Configuration`, `Runtime Behavior`, `Operational Dependencies`.
2. Azure provider panel:
   - Required controls: `Endpoint` (URL input), `ApiKey` (secret input), `Deployment` (text/select).
   - Validation: endpoint format required, api key non-empty, deployment non-empty.
3. Local provider panel:
   - Required controls: `TimeoutSeconds` (positive int), `LocalMinIntervalMs` (non-negative int).
   - Operational dependency row: `LocalServiceHosts:EmbeddingsBaseUrl` with readiness state and location hint.
4. Runtime behavior panel:
   - Explicit helper copy for local pacing and single in-flight call behavior.
   - Explicit helper copy for local 1024 -> stored 1536 adaptation behavior.
5. Model/preset controls:
   - If mode-level `modelId` and/or `requestPresetJson` are not runtime-consumed for Embeddings, they are hidden.
   - If later enabled, they must be provider-filtered, typed, validated, and runtime-consumed end-to-end.
6. Save behavior:
   - Save validates only visible provider controls.
   - Switching provider preserves per-provider values with no cross-provider overwrite.
   - Save summary states which provider will become active for Embeddings.

### 4.2 Image Generation

Providers under `ImageGeneration`:

1. Azure OpenAI Images
2. Local Stable Diffusion HTTP

Provider-scoped controls:

1. Azure OpenAI Images: `Endpoint`, `ApiKey`, `Deployment`, `EditModelDeployment`, `ApiVersion`
2. Local Stable Diffusion HTTP:
   - runtime host dependency: `LocalServiceHosts:ImageGenerationBaseUrl`
   - provider-scoped request timeout (currently sourced from `ImageGeneration:TimeoutSeconds` and used on local path)
   - engine tunables: `GA_SD_STEPS`, `GA_SD_CFG_SCALE`, `GA_SD_STRENGTH`, `GA_SD_SAMPLING_METHOD`, `GA_SD_ENGINE_REQUEST_TIMEOUT_SECONDS`, `GA_SD_POLL_INTERVAL_SECONDS`, `GA_SD_WARMUP_*`
   - model artifact controls: `GA_SD_MODEL_DIR`, `GA_SD_DIFFUSION_MODEL_PATH`, `GA_SD_VAE_PATH`, `GA_SD_LLM_PATH`

Resolution/format/profile requirements:

1. Size/resolution options are provider+model-profile aware, not one global static list.
2. Azure profiles are explicit:
   - flux-family deployments: `1024x1024`, `1024x1792`, `1792x1024`
   - `gpt-image-1.5`: `1024x1024`, `1024x1536`, `1536x1024`, `auto`
3. Local SD currently validates to flux-style sizes (`1024x1024`, `1024x1792`, `1792x1024`) even though wrapper parsing can accept generic `WIDTHxHEIGHT`; this is treated as explicit product policy unless changed.
4. Edit flows use the same provider/profile constraints as generation flows and keep deterministic auto-size behavior from source image aspect ratio.
5. Output format is provider-scoped:
   - Local SD: `png`, `jpeg`, `webp` (`jpg` normalized to `jpeg`)
   - Cloud path currently does not honor `outputFormat` in generation request payload
6. Do not carry forward legacy DALL-E style/quality defaults as generic cross-provider assumptions.

Local model lifecycle requirements:

1. Local Image Generation supports provider-scoped lifecycle operations: download, install, validate, activate bundle (`select-active` API), remove, load engine, unload engine, and hot-swap the active bundle on a running engine.
2. Local model selection is a bundle concept, not a single-file concept.
3. A selectable bundle includes all required artifact roles:
   - diffusion model
   - VAE
   - text encoder / LLM
4. Incomplete bundles are not selectable as active runtime bundles.
5. Download/install state is visible per bundle and per artifact role (queued, downloading, ready, failed).
6. Validation errors identify missing artifact roles explicitly.
7. Local model bundle operations do not affect Azure image provider behavior/settings.
8. Bundle **recipes** (per-role Hugging Face `repo` + `file` and the downloaded weights) are **not** rows in `ApplicationSettings`; they live under the SD model root on disk / the `ai_local_models` volume (`bundles/<id>/bundle-definition.json` plus one file per role). The database holds Image Generation **provider selection**, timeouts, and service routing—not the SD GGUF recipe.
9. Two distinct pieces of state exist and must be modeled separately in the API and the UI:
   - **Active bundle**: the bundle marked on disk (`active_bundle.json`) as the one the engine should load next time it starts. Authoritative for "which bundle will be used".
   - **Loaded bundle / engine state**: the bundle actually resident in the `sd-server` subprocess right now, plus the subprocess liveness (`running` / `unloaded` / `degraded`), last-load timestamp, and last-load error if any.
10. Changing the active bundle while the engine is running must hot-swap the loaded bundle (stop current `sd-server`, start a new one against the newly active bundle) rather than deferring to a container restart. Changing the active bundle while the engine is unloaded only updates the on-disk marker.
11. The SD service must expose explicit "load engine" and "unload engine" operations so operators can release GPU/RAM on demand and re-arm the engine later without restarting `guideants-ai`.
12. Engine lifecycle operations are serialized: at most one of `load` / `unload` / `select-active` hot-swap is in flight at a time. Overlapping requests fail fast with HTTP 409 instead of racing or spawning duplicate subprocesses.
13. If the engine fails to start (bad paths, missing artifacts, subprocess crash during warmup), the service degrades to the `unloaded` state with `config_error` populated. The container must not crash and `/local-models` must keep returning the bundle list so the operator can fix the problem from the UI.
14. An in-flight inference request must not block a later `unload`; unload tears down the subprocess and any in-flight generation fails with a connection error by design, so GPU/RAM is actually released.

Runtime requirements:

1. Switching provider (Azure <-> Local SD) does not change service identity/editor type.
2. Local SD tuning controls affect local behavior only.
3. Azure deployment/edit-deployment controls affect Azure behavior only.
4. If mode-level `modelId`/`requestPresetJson` are not consumed by image runtime dispatch, they are not presented as operative controls.
5. If retained, mode-level `modelId`/`requestPresetJson` are validated and consumed end-to-end.

Editor requirements:

1. Layout and flow:
   - Header: "Image Generation" + active provider + active profile label + readiness summary.
   - Provider selector options: `Azure OpenAI Images` and `Local Stable Diffusion HTTP` only.
   - Panels: `Provider Configuration`, `Generation Profile`, `Runtime Tuning`, `Local Model Bundles` (local only).
2. Azure provider panel:
   - Required controls: `Endpoint`, `ApiKey`, `Deployment`, `EditModelDeployment`, `ApiVersion`.
   - Validation: endpoint/api key/deployment/edit deployment required; API version format validated.
3. Profile and output controls:
   - Profile selector determines allowed resolution list.
   - Resolution control is constrained to active provider+profile valid values.
   - `outputFormat` shown only where runtime consumes it.
   - `quality` and `style` shown only for provider/profile combinations that consume them.
4. Local provider panel:
   - Visible operational dependency: `LocalServiceHosts:ImageGenerationBaseUrl`.
   - `TimeoutSeconds` shown as local-path request timeout control.
   - Advanced tuning controls grouped in collapsible section: `GA_SD_STEPS`, `GA_SD_CFG_SCALE`, `GA_SD_STRENGTH`, sampling and timeout/poll knobs.
5. Local model bundles panel:
   - Bundle list shows artifact-role completeness for each bundle (`diffusion`, `vae`, `text encoder/llm`).
   - Each row shows whether the bundle is the **Active** (on-disk) selection and, separately, whether it is the **Loaded** (in-memory) bundle; a bundle may be active but not loaded (engine stopped) or loaded but not active (transiently during hot-swap).
   - Actions: download/install/activate/remove, each with explicit operation state.
    - Definition portability actions:
      - **Download definition** exports the bundle recipe as JSON (`bundleId`, optional `revision`, and per-role `repo` + `file` values).
      - **Upload definition** imports that JSON and pre-fills the bundle download form so operators can recreate the same bundle in another environment.
    - Bundle row actions are presented as a compact icon action bar with explicit accessible names and tooltips (View details, Download definition, Edit bundle, Activate bundle, Remove bundle).
    - Incomplete bundles cannot be activated; errors name missing artifact roles.
   - A dedicated engine-state strip at the top of the panel shows: process liveness (`running` / `unloaded` / `degraded`), the currently loaded bundle id (or "none"), the last-load timestamp, and any `config_error` surfaced by the service.
   - Load / unload controls:
     - **Load engine** is offered whenever the engine is `unloaded` and an active bundle is present on disk. It proxies `POST /local-models/load` and refreshes the bundle listing on success.
     - **Unload engine** is offered whenever the engine is `running` or `degraded`. It proxies `POST /local-models/unload`, warns inline that any in-flight generation will be aborted, and refreshes the bundle listing on success.
     - Both controls disable themselves while another lifecycle operation is in flight (HTTP 409 from the service is surfaced as operator copy, not a raw error dump).
   - "Activate bundle" on a bundle row:
     - When the engine is unloaded, behaves as "mark this bundle active on disk"; the UI does not claim anything was loaded.
     - When the engine is running, performs a hot-swap (stop current engine, start engine against the newly selected bundle) and reflects the new loaded bundle in the engine-state strip on completion.
     - Under no circumstances does the UI tell the operator that a container restart is required to apply a different active bundle.
6. Model/preset controls:
   - If mode-level `modelId` is not dispatch-relevant, hide it.
   - Request presets are typed controls, not opaque JSON.
7. Save behavior:
   - Save validates only active provider/profile controls.
   - Switching provider preserves provider-specific values and local bundle selection state.
   - Save summary explicitly states active provider and active profile constraints.

### 4.3 Document Intelligence (Markdown Extraction)

Providers under `DocumentIntelligence`:

1. Azure Document Intelligence
2. Local Docling HTTP

Provider-scoped controls:

1. Azure: `Endpoint`, `ApiKey`, `ApiVersion`, `TimeoutSeconds`, `MaxRetries`
2. Local operational: `TimeoutSeconds`, `MaxConcurrentConversions`, `AsyncStatusPollIntervalMs`
3. Local Docling engine controls:
   - `DoclingDoOcr`
   - `DoclingForceOcr`
   - `DoclingOcrPreset`
   - `DoclingPdfBackend`
   - `DoclingTableMode`
   - `DoclingTableCellMatching`
   - `DoclingImageExportMode`
4. Local runtime host dependency: `LocalServiceHosts:DocumentIntelligenceBaseUrl`

Runtime requirements:

1. Settings must be actually consumed by extraction runtime behavior.
2. "Stored but not applied" patterns are not acceptable.
3. For image-heavy documents, settings must expose practical speed/quality tradeoffs without code changes.
4. `MaxConcurrentConversions=1` remains a valid/tested local setting; single-doc slowness is diagnosable independently of queue concurrency.
5. Azure and Local settings remain independent.
6. Switching provider (Azure <-> Local Docling) does not change service identity/editor type.
7. Editor shows Azure-only vs Docling-only controls conditionally.
8. Common Docling performance knobs are typed controls, not JSON-only UX.

Editor requirements:

1. Layout and flow:
   - Header: "Document Intelligence" + active provider + readiness summary.
   - Provider selector options: `Azure Document Intelligence` and `Local Docling HTTP` only.
   - Panels: `Provider Configuration`, `Conversion Throughput`, `Docling Engine Controls` (local only), `Operational Dependencies`.
2. Azure provider panel:
   - Required controls: `Endpoint`, `ApiKey`, `ApiVersion`, `TimeoutSeconds`, `MaxRetries`.
   - Validation: endpoint/api key required; timeout and retries must be positive integers.
3. Local provider panel:
   - Required controls: `TimeoutSeconds`, `MaxConcurrentConversions`, `AsyncStatusPollIntervalMs`.
   - Validation: timeout > 0, concurrency >= 1, poll interval > 0.
   - Operational dependency row: `LocalServiceHosts:DocumentIntelligenceBaseUrl`.
4. Docling engine controls panel:
   - Typed controls for OCR, PDF backend, table mode, table cell matching, image export mode.
   - Controls are labeled by speed/quality impact and include safe defaults.
   - Common knobs are first-class controls; advanced free-form JSON is optional and clearly marked advanced-only.
5. Performance and troubleshooting UX:
   - Inline explanation that `MaxConcurrentConversions=1` affects queue concurrency, not single-document parse cost.
   - Surface current queue/backlog or equivalent readiness signals where available.
6. Save behavior:
   - Save validates only currently selected provider controls.
   - Switching providers preserves provider-specific settings with no leakage.
   - Save summary states active provider and the operative throughput settings.

### 4.4 Speech Transcription

Providers under `SpeechTranscription`:

1. Azure Speech Batch
2. Local ASR HTTP

Provider-scoped controls:

1. Azure Speech Batch:
   - `AzureSpeechService:Endpoint`
   - `AzureSpeechService:ApiKey`
   - `AzureSpeechService:TimeoutSeconds` (runtime currently uses Azure section timeout for cloud transcription path)
2. Local ASR HTTP:
   - runtime host dependency: `LocalServiceHosts:SpeechTranscriptionBaseUrl`
   - `SpeechTranscription:TimeoutSeconds` (runtime currently applies this timeout on local ASR path)
3. Local ASR runtime/model controls (runtime-owned today in container env):
   - `GA_ASR_MODEL_DIR`
   - `GA_ASR_DEFAULT_MODEL_PATH`
   - `GA_ASR_DEFAULT_MODEL_ID`
   - `GA_ASR_DEVICE_MAP`
   - `GA_ASR_DTYPE`
   - `GA_ASR_MAX_INFERENCE_BATCH_SIZE`
   - `GA_ASR_MAX_NEW_TOKENS`
   - `GA_ASR_AUTO_LOAD_ON_STARTUP`
   - `GA_ASR_WARMUP_ON_LOAD`
   - `GA_ASR_WARMUP_AUDIO_PATH`
   - `GA_ASR_WARMUP_LANGUAGE`

Runtime behavior requirements:

1. Provider choices are constrained by Speech Transcription contracts only.
2. Azure and Local provider behavior remains independent under one service identity.
3. Diarization behavior is provider-specific:
   - Azure path supports diarization payload settings when enabled.
   - Local ASR path does not currently apply diarization formatting.
4. Timeout behavior is provider-specific and explicit:
   - cloud path uses Azure Speech section timeout
   - local path uses Speech Transcription section timeout
5. Transcription format behavior (speaker-labeled vs plain text) must be explicit by workflow and provider.
6. If language selection is surfaced in settings or endpoint contracts, it must be runtime-consumed by provider dispatch for the selected provider; non-operative language fields are not allowed.
7. If mode-level `requestPresetJson` (`language`, `modelHint`) is not consumed by runtime dispatch, it must not be presented as an operative control.

Local model lifecycle requirements:

1. Local Speech Transcription must support provider-scoped model lifecycle operations: load/select active model, validate readiness, and expose load failures.
2. Local model controls must support explicit model target selection (`model_id` or `model_path`) and runtime parameters (`dtype`, `device_map`, inference caps) where supported by the local ASR service.
3. Local ASR model lifecycle operations must not affect Azure Speech provider settings or behavior.
4. Readiness state (`loaded`, warmup status, load error) is first-class operational state for local provider and must be visible in provider-specific UX.

Editor requirements:

1. Layout and flow:
   - Header: "Speech Transcription" + active provider + readiness summary.
   - Provider selector options: `Azure Speech Batch` and `Local ASR HTTP` only.
   - Panels: `Provider Configuration`, `Transcription Behavior`, `Local Model Operations` (local only), `Operational Dependencies`.
2. Azure provider panel:
   - Required controls: `Endpoint`, `ApiKey`, and effective cloud timeout source.
   - Validation: endpoint/api key required.
3. Local provider panel:
   - Required controls: `SpeechTranscription:TimeoutSeconds`.
   - Operational dependency row: `LocalServiceHosts:SpeechTranscriptionBaseUrl`.
   - Local model runtime controls grouped under `Local Model Operations`.
4. Transcription behavior panel:
   - Provider-specific behavior notes for diarization and output format expectations.
   - Language control appears only if runtime-consumed end-to-end for selected provider.
5. Local model operations panel:
   - Readiness state (`loaded`, warmup status, errors) is visible.
   - Load controls expose `model_id`/`model_path`, dtype/device options, and inference caps where supported.
   - Operation feedback includes in-progress/success/failure states.
6. Model/preset controls:
   - If mode-level `modelId` is not used by transcription dispatch, hide it.
   - If mode-level request preset fields are not runtime-consumed, hide them.
7. Save behavior:
   - Save validates visible provider controls only.
   - Switching providers preserves provider-specific values and local ops state.
   - Save summary states active provider and effective timeout source.

### 4.5 Speech Synthesis

Providers under `SpeechSynthesis`:

1. Azure Speech SSML
2. Local TTS HTTP

Provider-scoped controls:

1. Azure Speech SSML:
   - `AzureSpeechService:ApiKey`
   - `AzureSpeechService:Region`
   - `AzureSpeechService:Endpoint` (optional endpoint override)
   - `AzureSpeechService:TimeoutSeconds` exists in shared settings but is not currently enforced by the synthesis runtime path.
2. Local TTS HTTP:
   - runtime host dependency: `LocalServiceHosts:SpeechSynthesisBaseUrl`
   - `SpeechSynthesis:TimeoutSeconds` (runtime currently applies this timeout on local TTS HTTP path)
3. Local TTS runtime/model controls (runtime-owned in container env):
   - model/tokenizer target controls: `GA_TTS_MODEL_DIR`, `GA_TTS_DEFAULT_MODEL_PATH`, `GA_TTS_DEFAULT_MODEL_ID`, `GA_TTS_TOKENIZER_PATH`, `GA_TTS_TOKENIZER_ID`
   - inference/runtime controls: `GA_TTS_DTYPE`, `GA_TTS_DEVICE_MAP`, `GA_TTS_MAX_NEW_TOKENS`, `GA_TTS_SAMPLE_RATE`, `GA_TTS_DEFAULT_VOICE_SECONDS`
   - startup/readiness controls: `GA_TTS_AUTO_LOAD_ON_STARTUP`, `GA_TTS_WAIT_FOR_READY_ON_STARTUP`, `GA_TTS_READY_TIMEOUT_SECONDS`

Runtime behavior requirements:

1. Provider choices are constrained by Speech Synthesis contracts only.
2. Azure and Local provider behavior remains independent under one service identity.
3. Payload behavior is provider-specific:
   - Azure synthesis path sends SSML to Azure Speech SDK (`SpeakSsmlAsync`).
   - Local TTS path strips SSML markup to plain text and calls `POST /tts/synthesize`.
4. Timeout behavior is provider-specific and explicit:
   - local path uses `SpeechSynthesis:TimeoutSeconds`
   - Azure synthesis path currently does not enforce `AzureSpeechService:TimeoutSeconds` in the `SpeechSynthesisService` dispatch path
5. Local duration behavior is header-derived (`x-audio-duration-seconds`) and rounds to seconds; missing/invalid header resolves to `0`.
6. Local TTS runtime currently accepts text-only synthesis payloads and does not consume mode-level `voice`/`language`/`rate` fields.
7. If mode-level `requestPresetJson` (`voice`, `language`, `rate`) or mode-level `modelId` are not consumed by synthesis provider dispatch, they must not be shown as operative controls.

Local model lifecycle requirements:

1. Local Speech Synthesis must support provider-scoped model lifecycle operations: load model, probe health/readiness, and expose load failures.
2. Local model controls must support explicit load targets for both model and tokenizer (`model_id`/`model_path` and `tokenizer_id`/`tokenizer_path`).
3. Readiness state (`loaded`, active `modelRef`/`tokenizerRef`, dtype/device metadata, load errors) is first-class local provider operational state.
4. Local model lifecycle operations must not affect Azure Speech provider settings or behavior.

Editor requirements:

1. Layout and flow:
   - Header: "Speech Synthesis" + active provider + readiness summary.
   - Provider selector options: `Azure Speech SSML` and `Local TTS HTTP` only.
   - Panels: `Provider Configuration`, `Synthesis Behavior`, `Local Model Operations` (local only), `Operational Dependencies`.
2. Azure provider panel:
   - Required controls: `ApiKey`, `Region`; optional `Endpoint`.
   - Validation: api key non-empty, region non-empty.
   - UI copy explicitly states Azure path consumes SSML directly.
3. Local provider panel:
   - Required controls: `SpeechSynthesis:TimeoutSeconds`.
   - Operational dependency row: `LocalServiceHosts:SpeechSynthesisBaseUrl`.
   - UI copy explicitly states local path strips SSML to plain text before synthesis.
4. Local model operations panel:
   - Readiness state includes `loaded`, active `modelRef`, `tokenizerRef`, dtype/device metadata, and last load failure.
   - Load controls expose both model and tokenizer targets (`model_id`/`model_path`, `tokenizer_id`/`tokenizer_path`).
   - Operation actions show in-progress/success/failure and timestamped last result.
5. Voice/language/rate and model binding rules:
   - If `voice`, `language`, `rate`, or mode-level `modelId` are not runtime-consumed for selected provider, they are not shown as operative controls.
   - If any are retained for diagnostics, they are explicitly labeled non-operative.
6. Save behavior:
   - Save validates only visible provider controls.
   - Switching providers preserves provider-specific values and local model-operation draft inputs.
   - Save summary states active provider and whether SSML is passthrough or text-normalized on runtime path.

## 5) Acceptance Criteria

### 5.1 Global

1. No helper copy that is false for the current service editor.
2. Each non-chat service editor is tailored, not generic.
3. Provider picker is service-constrained.
4. Invalid service/provider/model combinations cannot be saved.
5. Switching providers preserves provider-specific values without leakage.
6. Editors do not expose raw provider section names as a user-editable primitive.
7. Validation and error display are scoped to currently visible provider controls.
8. Hidden controls do not block save for the active provider path.
9. Non-operative fields are hidden or explicitly labeled as non-operative diagnostics.
10. The service header **Active provider** label always matches persisted server state until save completes; draft selection is labeled separately (§3.5).
11. Secret/API key rows use `hasValue` (and never echoed secrets) so operators can see that a credential is stored without revealing it.
12. Local model operations: expected unavailable endpoints do not produce repeated console error noise; unavailable panels show curated copy only, not raw JSON blobs; destructive/local-ops controls are not offered when the capability probe fails (§3.5).

### 5.2 Embeddings

1. Embeddings switches between Azure and Local providers as peer alternatives under one service.
2. Local timeout/pacing settings do not affect Azure behavior.
3. Embeddings editor does not show unused mode-level model/preset controls unless implemented and runtime-consumed.
4. Embeddings editor shows only service-scoped provider options and never raw section-name inputs.
5. Azure panel requires `Endpoint`, `ApiKey`, and `Deployment` before save.
6. Local panel requires `TimeoutSeconds` and `LocalMinIntervalMs` and surfaces `EmbeddingsBaseUrl` readiness.
7. Switching provider preserves unsaved provider-specific values during the edit session.
8. Save validates only currently visible provider controls.

### 5.3 Image Generation

1. Image Generation switches between Azure and Local providers as peer alternatives under one service.
2. Resolution choices change by selected provider/profile (including `gpt-image-1.5` vs flux-profile differences) and reject invalid combinations.
3. UI does not expose cloud output-format controls unless cloud runtime consumes them.
4. Local output-format controls reflect supported runtime values (`png`, `jpeg`, `webp`) and are validated.
5. Editor does not expose non-operative style/quality controls unless runtime consumption is implemented for selected provider/profile.
6. Local SD tuning knobs can be changed without code edits and affect local generation/edit runtime behavior only.
7. Local provider supports downloading/selecting model bundles containing diffusion + VAE + text encoder/LLM artifacts.
8. Incomplete local bundles cannot be activated and produce role-specific missing-artifact errors.
9. Local model bundle selection remains provider-scoped and does not alter Azure provider settings.
10. Image editor renders provider-specific panels and hides irrelevant controls for the active provider/profile.
11. Azure panel requires `Endpoint`, `ApiKey`, `Deployment`, and `EditModelDeployment` before save.
12. Local panel surfaces `ImageGenerationBaseUrl` readiness and validates local timeout/tuning inputs.
13. Provider switching preserves per-provider draft state and bundle selection context.
14. The bundle manager exposes explicit **Load engine** and **Unload engine** controls backed by `/local-models/load` and `/local-models/unload`, and displays the live engine state (liveness, loaded bundle id, last-load timestamp, last-load error) alongside the bundle list.
15. Selecting a different active bundle while the engine is running hot-swaps the loaded model (no `guideants-ai` container restart); selecting one while the engine is unloaded only updates the on-disk marker. The UI never shows a "restart required" message for bundle changes.
16. Concurrent lifecycle requests (load / unload / set active while loaded) cannot race: the second request is rejected (HTTP 409) and surfaced as operator copy, not a raw error dump.
17. An `sd-server` failure (bad artifact paths, subprocess crash during warmup) degrades the service to `unloaded` + `config_error`; the container stays up and the bundle manager remains usable so the operator can fix and re-load from the UI.
18. Bundle definitions are portable: operators can download a bundle definition JSON and upload it later to pre-fill the download recipe (`bundleId`, optional `revision`, and all role repo/file pairs).

### 5.4 Document Intelligence

1. Document Intelligence switches between Azure and Local Docling as peer alternatives under one service.
2. Docling tuning fields are visible for local provider and affect runtime behavior.
3. Large image-heavy documents can be tuned via settings without code changes.
4. Local Docling concurrency/polling settings are not reused as Azure settings.
5. Document Intelligence editor shows typed controls for common Docling knobs and does not force JSON-only editing.
6. Azure panel requires endpoint/key and validates timeout/retry values.
7. Local panel requires timeout/concurrency/poll interval and surfaces `DocumentIntelligenceBaseUrl` readiness.
8. Editor explicitly separates queue-throughput controls from single-document parse behavior guidance.
9. Switching provider preserves provider-specific settings with no cross-provider overwrite.

### 5.5 Speech Transcription

1. Speech Transcription switches between Azure Speech Batch and Local ASR HTTP as peer providers under one service identity.
2. Azure transcription controls (`Endpoint`, `ApiKey`, Azure timeout) do not alter local ASR behavior.
3. Local transcription controls (`SpeechTranscription` timeout, local ASR runtime/model controls) do not alter Azure behavior.
4. Service editor shows transcription-relevant controls only and hides non-operative preset fields unless implemented and runtime-consumed.
5. If language is exposed in API/editor UX, it is actually consumed end-to-end by provider runtime; otherwise it is removed from operative UX.
6. Local ASR provider supports operational model lifecycle controls (load/select/readiness/failure visibility) without requiring code changes.
7. Transcription editor surfaces only service-scoped provider options and never raw provider section text inputs.
8. Local provider panel includes explicit readiness state and load operation feedback.
9. Save validates only visible provider controls and preserves unsaved state across provider toggles.

### 5.6 Speech Synthesis

1. Speech Synthesis switches between Azure Speech SSML and Local TTS HTTP as peer providers under one service identity.
2. Azure synthesis controls (`ApiKey`, `Region`, optional `Endpoint`) do not alter local TTS behavior.
3. Local synthesis controls (`SpeechSynthesis` timeout and local model/runtime controls) do not alter Azure synthesis behavior.
4. Local synthesis path enforces `SpeechSynthesis:TimeoutSeconds`, strips SSML to plain text for `/tts/synthesize`, and reads duration from `x-audio-duration-seconds` when provided.
5. Service editor does not expose non-operative mode-level fields (`modelId`, `voice`/`language`/`rate` presets) unless runtime consumption is implemented end-to-end.
6. Local TTS provider supports operational model lifecycle controls (model+tokenizer load target selection, readiness, and failure visibility) without code changes.
7. Synthesis editor surfaces only service-scoped provider options and never raw provider section text inputs.
8. Azure panel copy explicitly states SSML passthrough behavior; local panel copy explicitly states SSML-to-text normalization behavior.
9. Save validates only visible provider controls and preserves unsaved state across provider toggles.

## 6) Non-Goals

1. Re-designing assistant chat-model assignment in this workstream.
2. Treating provider selection as a global independent object.
3. Expanding free-form JSON usage where typed controls are known and required.
