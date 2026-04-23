# Settings + Llama Completion — Requirements

Last updated: 2026-04-19

## Purpose

This document captures the **requirements** that any completion plan for [`llama-model-download-and-runtime-management.md`](llama-model-download-and-runtime-management.md) and [`settings-page-provider-model-llama-redesign.md`](settings-page-provider-model-llama-redesign.md) must satisfy, **reconciled with corrections made during planning**:

- Chat model selection is driven by the Assistant definition (multi-model concurrent) and does **not** use a mode matrix.
- A global default chat model feature is planned for a separate, subsequent deliverable; the present work must leave a clean seam for it.
- The mode matrix applies **only** to services that route via `ServiceModes` (Embeddings, Image Generation, Speech Transcription, Speech Synthesis, Markdown / Document Intelligence).

Every requirement has a stable ID (`R-X.N`). Use these when reviewing the plan ([`.cursor/plans/finish_both_settings_proposals_*.plan.md`](../.cursor/plans/)) so coverage gaps are unambiguous.

---

## Glossary

- **Service** — a logical feature routed to a provider. Currently: Chat, Embeddings, Image Generation, Speech Transcription, Speech Synthesis, Markdown (Document Intelligence).
- **Provider section** — a settings registry entry holding a provider's credentials/endpoints (e.g. `AzureOpenAI`, `LlamaCpp`, `AzureSpeechService`).
- **Catalog model** — a row in the Models DB table (`Models`) with a `Provider` string and optional `LocalRuntimeJson`.
- **Mode** — a named, persisted routing preset for a **non-chat service** composed of `{ modeId, providerSection, modelId?, requestPresetJson, enabled, isDefault }`.
- **Chat target** — the effective catalog model used for a single chat turn. Derived from the assistant's chat-model config (and, in the future, possibly the global-default setting).
- **Runtime target** — for llama-cpp models, the router alias + GGUF/mmproj artifacts plus current load state.
- **Fail-fast** — if any prerequisite is unmet, return a structured error immediately; never silently substitute another provider/model/runtime.

---

## R-1. Routing shape

| ID | Requirement |
|----|-------------|
| R-1.1 | Chat routing MUST remain assistant-driven. The modelId used for a chat turn MUST come from the assistant/guide definition, not from a service-wide default or mode. |
| R-1.2 | Chat routing MUST support multiple distinct models being active simultaneously (e.g. different assistants in a crew using different models concurrently). |
| R-1.3 | The five non-chat services (Embeddings, Image Generation, Speech Transcription, Speech Synthesis, Markdown) MUST be routable via a mode matrix with an optional per-request `modeId` override and a service-level default. |
| R-1.4 | A per-request `modeId` MUST take precedence over the service default. If `modeId` is absent, the service default MUST be used. |
| R-1.5 | Chat MUST NOT be given a mode matrix, a service default mode, or a chat-scoped `ServiceModes` entry. |
| R-1.6 | Chat resolution MUST be expressed as two isolated steps: (a) resolve the effective `modelId` from assistant config, (b) validate that `modelId`'s executability. Step (a) MUST be the single seam where the future global-default-model feature plugs in. |
| R-1.7 | The system MUST NOT perform silent provider, model, runtime, or mode fallback. Unresolved targets MUST surface as structured errors (R-2). |

## R-2. Error contract (fail-fast)

| ID | Requirement |
|----|-------------|
| R-2.1 | A single error type MUST be used for all routing failures across chat and non-chat services. |
| R-2.2 | The error type MUST carry at least: `code`, `service`, `modeId?`, `modelId?`, `provider?`, `action` (human-readable remediation hint). |
| R-2.3 | Codes MUST include: `ROUTING_MODE_NOT_FOUND`, `ROUTING_PROVIDER_NOT_READY`, `ROUTING_MODEL_NOT_READY`, `ROUTING_RUNTIME_NOT_READY`. Additional codes MAY be added but MUST NOT replace these. |
| R-2.4 | HTTP surface MUST use RFC 7807 `ProblemDetails` with the fields in R-2.2 exposed as extension members. Response status MUST be `400` (caller input), `409` (state), or `503` (unavailable) as appropriate to the code — never `500`. |
| R-2.5 | Client code that catches these errors MUST be able to distinguish them by `code` alone, without parsing `message`. |

## R-3. Per-service resolution rules

| ID | Requirement |
|----|-------------|
| R-3.1 | **Chat**: target MUST be validated against (a) catalog row exists and is active, (b) provider section is configured, (c) for llama-cpp models: router alias exists, GGUF present, and the model is loaded. Any (a–c) failure MUST emit the appropriate `ROUTING_*` error, **subject to R-12** — validation runs only after the existing notebook load orchestration has completed (or was explicitly skipped by the caller), so a momentarily-unloaded model under active load orchestration MUST NOT fail (c). |
| R-3.2 | **Embeddings / Images / Speech T / Speech S / Markdown**: mode resolution MUST verify the mode exists (else `ROUTING_MODE_NOT_FOUND`), the mode is `enabled`, the provider section is configured (else `ROUTING_PROVIDER_NOT_READY`), any bound `modelId` exists and is active (else `ROUTING_MODEL_NOT_READY`), and any llama runtime target is loaded (else `ROUTING_RUNTIME_NOT_READY`). |
| R-3.3 | Each of the five non-chat services MUST retain the existing service-specific preset knobs somewhere in the mode's `requestPresetJson` (images size/quality/style, speech voice/language, embeddings batch/dims, markdown engine options). No preset capability present today may regress. |

## R-4. Persistence + concurrency

| ID | Requirement |
|----|-------------|
| R-4.1 | Modes MUST persist through the same `ApplicationSettings` row-versioned concurrency mechanism used for other settings sections. Updates MUST return `409` on row-version mismatch, consistent with current behavior. |
| R-4.2 | Secrets referenced by modes MUST continue to flow through the existing `SettingsSecretsOptions` encryption path; no secret values may be returned in plaintext, and `secretHasValue` semantics MUST be preserved. |
| R-4.3 | Boot behavior MUST NOT assume any predefined mode set and MUST NOT synthesize `ServiceModes` from legacy `{Service}:ActiveProviderId` values. Service modes are explicit configuration. |
| R-4.4 | Reading `ActiveProviderId` from any of the five non-chat sections MUST be removed from production code paths. Chat sections have no `ActiveProviderId` and are not affected. |

## R-5. Settings UI information architecture

| ID | Requirement |
|----|-------------|
| R-5.1 | The Settings page MUST present five top-level tabs: `Overview`, `Models & Runtime`, `Services`, `Connections`, `Infrastructure`. Order MUST match that sequence (see `SettingsTabNavigation.tsx`). |
| R-5.2 | `Overview` MUST present exactly two sections: (a) **Chat providers in use** — one row per chat connection section (`AzureOpenAI`, `OpenAI`, `Anthropic`, `LlamaCpp`) that is referenced by any catalog model used by an active assistant; each row shows Ready / Not ready based on whether that section appears in `SettingsOverviewDto.providerIssues`, and MUST deep-link to **Connections** with that section focused. (b) **Non-chat services** — one row for each of the five routed services (`SpeechTranscription`, `SpeechSynthesis`, `ImageGeneration`, `Embeddings`, `DocumentIntelligence`); each row shows one of `Ready`, `Not ready`, or `Not configured` (when there is no saved active provider yet) and MUST deep-link to **Services** with that service selected. The Overview MUST NOT duplicate full service-editor controls, connection editors, or llama runtime operations; those remain on **Services**, **Connections**, and **Models & Runtime** as applicable. |
| R-5.3 | `Services` MUST provide one dedicated editor for each non-chat service (`Embeddings`, `ImageGeneration`, `DocumentIntelligence`, `SpeechTranscription`, `SpeechSynthesis`) with: active-provider state, provider selector scoped to that service, provider-specific fields, readiness summary, and service-scoped save/apply actions. |
| R-5.4 | `Services` provider updates MUST use the existing optimistic concurrency pattern (`409` on stale row-version), preserve provider-scoped drafts when switching providers during editing, and expose validation blockers in-context for the currently selected provider. |
| R-5.5 | `Connections` MUST group provider sections by ownership: `Chat/LLM Providers`, `Service Providers`, `Local Runtime Connectors`. For each section it MUST show a configured/unconfigured status badge and a "Used by services" chip list derived from the resolver/mode metadata. |
| R-5.6 | `Connections` MUST present credential editing in a details-panel layout: required fields first, optional fields second, secret fields masked with `secretHasValue` indication, footer actions `Reset` / `Save Section` / `Refresh`. |
| R-5.7 | `Infrastructure` MUST list runtime-owned dependency keys (`LocalServiceHosts:*` and `ServiceRouting:Containers:*:BaseUrl`) with source indicator (`appsettings` / `env` / `compose`), services-chips, and at minimum these diagnostics: `LlamaCpp:BaseUrl` prefix check and a reachability probe for each declared base URL. |

## R-6. Models & Runtime completeness

| ID | Requirement |
|----|-------------|
| R-6.1 | The `Models & Runtime` workspace MUST contain three sub-tabs: `Catalog`, `Runtime Profiles`, `Local Llama Runtime`. |
| R-6.2 | Catalog MUST expose `Add Model` as a provider-driven wizard with steps: provider selection, catalog block, provider-specific configuration, review, and async progress (when applicable). |
| R-6.3 | Catalog row edit MUST remain available and provider-scoped (`provider` + `modelId` immutable; install/source controls hidden in edit mode). |
| R-6.4 | Catalog MUST display a runtime-readiness badge on every row and a local-runtime state badge on llama-cpp rows, derived from live runtime inventory + per-model readiness probes. |
| R-6.5 | Wizard provider form MUST support at least: `openai-chat`, `openai-responses`, `azure-openai-chat`, `azure-openai-responses`, `anthropic`, `llama-cpp`; llama-cpp MUST support `Install from Hugging Face` and `Attach existing alias`. |
| R-6.6 | Runtime Profiles MUST show a "Used by N models" usage count per profile. Delete of a profile with `N > 0` MUST be prevented with a clear user-facing explanation. |
| R-6.7 | Runtime Profiles MUST offer at least the templates `qwen3_5`, `qwen3_6`, `gemma4`, each populating the form with the corresponding preset JSON. |
| R-6.8 | `Local Llama Runtime` MUST be runtime-ops only (`Runtime Inventory`, `Router Mapping`). Download/register onboarding is removed from this tab. |
| R-6.9 | `POST /api/settings/models:add` MUST be the unified add endpoint for all providers and both llama-cpp source flows (`huggingface`, `existingAlias`). |
| R-6.10 | `Runtime Inventory` MUST show, per router alias: runtime state, GGUF presence, mmproj presence, bound catalog model ids, optional router per-alias knobs (`contextSize`, `cacheRamMib`) when present, and actions `Load` / `Unload` / `Refresh`. |
| R-6.11 | `Router Mapping` MUST preview the effective alias-to-path registration and MUST flag duplicate and missing mappings. |
| R-6.12 | Models & Runtime MUST surface the effective `ModelStorePath` and `RouterModelsConfigPath` so operators can verify path resolution from the UI. |

## R-7. Llama runtime backend behavior

| ID | Requirement |
|----|-------------|
| R-7.1 | Backend MUST expose an inventory endpoint returning a merged view: router entries (aliases + paths) + disk artifact existence + runtime state from the llama server + catalog-model linkage. DTO shape MUST include at minimum: `routerModelId`, `runtimeState`, `statusSource`, `modelPath`, `mmprojPath`, `hasModelFile`, `hasMmprojFile`, `catalogModelIds`, `notebookReferenceCount`. |
| R-7.2 | Backend MUST expose load, unload, start-download, and download-status endpoints compatible with the UI in R-6.8. |
| R-7.3 | `LlamaModelManagement` options MUST include: `ModelStorePath`, `RouterModelsConfigPath`, `HfToken`, `AllowOverwrite`. |
| R-7.4 | The `HfToken` value stored as a secret on the `LlamaCpp` provider section (UI / DB) MUST take precedence over environment and appsettings sources in the download service's token resolution. |
| R-7.5 | Downloads MUST be non-destructive: partial files MUST NOT overwrite existing files on failure, and completion MUST be atomic. Concurrent downloads targeting the same alias MUST be serialized. |
| R-7.6 | Registering a router alias MUST update the runtime router preset file (in `guideants-ai`, default `/models-local/router-models.ini` on the `ai_local_models` volume) atomically and preserve existing entries. |
| R-7.7 | Unload MUST require operator confirmation when `notebookReferenceCount > 0`, and the confirmation MUST include that count. |

## R-8. Qwen3.6 onboarding end-to-end

| ID | Requirement |
|----|-------------|
| R-8.1 | The UI MUST offer a one-click "Insert Qwen3.6 template" action on Runtime Profiles that writes the profile exactly as specified in [the Qwen3.6 worked example](llama-model-download-and-runtime-management.md#qwen36-onboarding-worked-example). Idempotent — no overwrite. |
| R-8.2 | The UI MUST offer a one-click "Insert Qwen3.6 model" action on Catalog that writes the model exactly as specified in [the Qwen3.6 worked example](llama-model-download-and-runtime-management.md#qwen36-onboarding-worked-example). Idempotent — no overwrite. |
| R-8.3 | A Hugging Face download initiated from the UI with the proposal §7 defaults MUST complete and register the router alias, resulting in a Runtime Inventory row with `hasModelFile=true`, `hasMmprojFile=true`, `runtimeState=unloaded`. |
| R-8.4 | Loading the Qwen3.6 alias from `Runtime Inventory` MUST transition through `loading → loaded` observable from the UI. |
| R-8.5 | After load, notebook runtime preflight for an assistant bound to `qwen3.6-35b-a3b-local` MUST report `ready`. |
| R-8.6 | Unloading the Qwen3.6 alias and then invoking a chat flow pinned to that assistant MUST fail with `ROUTING_RUNTIME_NOT_READY` and MUST NOT substitute another provider or model. |

## R-9. Global default chat model (**delivered**)

| ID | Requirement |
|----|-------------|
| R-9.1 | The chat resolution flow MUST isolate "which modelId is used for this turn" into a single named seam that can later evaluate a global-default indirection without modifying the validator, dispatch sites, or error contract. **Delivered:** `IChatModelResolver` (`GuideAntsApi.Services.Routing`). |
| R-9.2 | The chat-targets readiness endpoint response MUST include a per-entry `referenceKind` field. **Delivered:** populated as `direct`, `defaultedTo`, or `overriddenToDefault` (see [default-chat-models.md](default-chat-models.md)). |
| R-9.3 | No requirement in this document, and no work derived from the plan, may presuppose where the global-default value lives, how assistants opt in, or whether it is global / per-workspace / per-user. Those are the next project's scope. **Delivered scope:** one **instance-wide** default in `ChatDefaults` application settings; per-workspace / per-user remains out of scope. |

**What changed during implementation:** Silent `gpt-4.1` fallbacks in some assistant/template materialization paths were removed; missing configuration now fails via the routing contract instead of a hidden default. See [default-chat-models.md](default-chat-models.md).

## R-10. Non-regression

| ID | Requirement |
|----|-------------|
| R-10.1 | Existing notebook-scoped runtime orchestration (`GET /api/notebooks/{id}/llama-runtime`, `POST /api/notebooks/{id}/llama-runtime/load`, operation polling) MUST continue to work unchanged. Full behavioral preservation is specified in R-12. |
| R-10.2 | Existing optimistic concurrency + preserve-draft behavior on settings sections MUST be preserved. |
| R-10.3 | Existing secret-masking semantics on provider sections MUST be preserved. |
| R-10.4 | Catalog row delete (`DELETE /api/settings/models/{id}`) MUST remain catalog-only (must not cascade to router alias/files). Alias delete cascade lives on `DELETE /api/settings/llama/router/entries/{routerModelId}` and is a separate, explicit operation. |
| R-10.5 | The Provider enum for catalog models (used by chat routing) is a closed set: `openai-chat`, `openai-responses`, `azure-openai-chat`, `azure-openai-responses`, `anthropic`, `llama-cpp`. The OpenAI platform (api.openai.com) and Azure OpenAI variants MUST remain distinct so readiness and dispatch read from the same credentials section. The pre-split aliases `openai` / `azure-openai` were canonicalized to `azure-openai-chat` by the `RenameOpenAiChatProvidersToAzure` migration and are no longer valid input — the server fails fast on them rather than silently aliasing. The UI (Models & Runtime → Catalog) MUST render this set as a constrained select, not a free-text input. |

## R-12. Preserved chat load-orchestration behavior

The existing chat system guarantees that required llama models are loaded before a chat turn runs. This orchestration lives in [NotebookModelRuntimeService.cs](../src/server/GuideAntsApi/Services/LlamaCpp/NotebookModelRuntimeService.cs) / [NotebookLlamaRuntimeEndpoints.cs](../src/server/GuideAntsApi/Endpoints/NotebookLlamaRuntimeEndpoints.cs) and it MUST survive the refactor functionally intact.

| ID | Requirement |
|----|-------------|
| R-12.1 | The notebook-scoped readiness computation (Guide + crew + optional assistant → required router aliases → state ∈ `ready | requires_load | loading | failed | invalid`) MUST continue to be the authority on "what models does this chat need, and are they loaded?". The new chat resolver / validator MUST NOT re-derive required-model sets from catalog alone; it validates a single `modelId` per turn and defers set-level readiness to this orchestration. |
| R-12.2 | The existing unload / load / verify sequence executed via the llama `/models/load`, `/models/unload`, and `/models` endpoints MUST be preserved behaviorally — same routes, same verification polling, same error mapping, same timeouts. |
| R-12.3 | The load-operation state machine (`queued → unloading → loading → verifying → ready | failed`) MUST be preserved. New routing / validator code MUST NOT bypass, duplicate, or replace it. |
| R-12.4 | Per-notebook serialization of load operations MUST be preserved so concurrent chat starts within one notebook cannot produce conflicting loads. |
| R-12.5 | The chat dispatch path MUST integrate with this orchestration such that an assistant whose required llama model is currently unloaded is loaded on demand by the established flow. `IChatTargetValidator` MUST run **after** that orchestration has settled, not before. A transient "unloaded" state that is actively being resolved by an in-flight operation MUST NOT cause the validator to fail. |
| R-12.6 | `ROUTING_RUNTIME_NOT_READY` for chat MUST be emitted only when the orchestration cannot resolve the state: artifacts missing on disk, the load operation reached `failed`, the readiness is `invalid`, or the caller explicitly opted out of awaiting a load. It MUST NOT be emitted merely because the model happened to be unloaded at the instant the validator ran. |
| R-12.7 | Client surfaces that today trigger preflight-and-load before a chat stream starts (notebook chat UI, published chat, scripted runs, etc.) MUST continue to work without code changes on their side. |
| R-12.8 | The HTTP contract of the existing endpoints — `GET /api/notebooks/{id}/llama-runtime`, `POST /api/notebooks/{id}/llama-runtime/load`, operation polling — including response DTO shapes (`state`, operation id, `errorDetails`) MUST remain stable. If additive fields are needed they MUST be optional and backwards-compatible. |
| R-12.9 | All existing automated and manual tests covering load-on-demand behavior MUST continue to pass without modification of their assertions. New tests added for the validator (R-3.1) MUST explicitly cover the interaction with orchestration: (a) validator passes when orchestration already loaded the model, (b) validator passes when orchestration completes during the turn, (c) validator emits `ROUTING_RUNTIME_NOT_READY` only when orchestration terminated in `failed` or artifacts are missing. |
| R-12.10 | Unload actions initiated from the new Settings UI (Local Llama Runtime tab) MUST NOT silently tear down a model that is the target of an in-flight notebook load operation. Contention between the two MUST be resolved deterministically — either by blocking the unload or by failing it with a clear reason — never by leaving the system in an indeterminate state. |

## R-11. Documentation

| ID | Requirement |
|----|-------------|
| R-11.1 | On completion, both proposal documents MUST be updated to reflect delivered reality, including: the chat-vs-non-chat routing split, the `ROUTING_*` error contract, and the scope of `ActiveProviderId` retirement. Discrepancies between proposal text and delivered behavior MUST be called out in a "What changed during implementation" section. |
| R-11.2 | [`docker/llama/README.md`](../docker/llama/README.md) MUST be updated to describe where `HfToken` is resolved from and how router aliases are registered via the UI. |
| R-11.3 | A release note MUST be produced naming every breaking change for operators with `appsettings` overrides (the five `{Service}:ActiveProviderId` keys). |

---

## Acceptance matrix (traceability)

When validating the plan, check each requirement against the plan's phases. Expected mapping:

- R-1.\*, R-2.\*, R-3.\*, R-4.\*, R-9.1, R-9.2 → Phase A
- R-7.\* (readiness / preflight / overview data) partial → Phase A/B split; Phase B for the composite endpoints
- R-5.1, R-5.2 → Phase B (API) + Overview UI in Phase C or a dedicated step
- R-5.3, R-5.4 → Phase C
- R-5.5, R-5.6 → Phase D
- R-5.7 → Phase E
- R-6.\* → Phase F plus Add-Model wizard/cascade amendments (`models:add`, catalog edit split, runtime-ops-only tab)
- R-7.\* → already in place; add-model endpoint integration extends runtime download + registration behavior
- R-8.\* → Phase G
- R-4.3, R-4.4 (ActiveProviderId retirement / no boot synthesis) → Phase H
- R-10.\* → verified continuously across all phases (non-regression gate)
- R-11.\* → Phase H
- R-12.\* → Phase A (resolver/validator integration point with orchestration) + Phase F/G (unload-contention + test coverage); acts as a non-regression gate alongside R-10

A requirement is **satisfied** only when (a) a plan step cites the files it touches, (b) it ships behind a test or manual-test step, and (c) it is not contradicted by another delivered step.

## Non-goals (explicit)

- Replacing notebook-level runtime compatibility orchestration.
- Changing the `Models` table schema.
- Cloud model download workflows.
- Automatic provider/model/runtime fallback of any kind.
- Extending the global default beyond **instance-wide** defaults (R-9.3 follow-on: per-workspace / per-user).
