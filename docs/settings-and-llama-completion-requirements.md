# Settings + Llama Completion - Requirements

Last updated: 2026-05-18

This document is the normative requirements baseline for Settings, chat/non-chat routing,
local llama runtime integration, and fail-fast behavior.

Source-of-truth set:
- [setup-guide.md](setup-guide.md)
- [settings-architecture.md](settings-architecture.md)

## Glossary

- **Service**: one capability surface (chat, embeddings, image generation, speech transcription, speech synthesis, document intelligence).
- **Catalog model**: `Models` row used for chat target routing.
- **Provider section**: settings section containing provider credentials/config.
- **Service mode**: persisted non-chat service provider/model/preset state.
- **Fail-fast**: no silent fallback on provider/model/runtime/mode errors.

## R-1. Routing shape

| ID | Requirement |
|----|-------------|
| R-1.1 | Chat routing MUST remain assistant-driven, with `IChatModelResolver` as the effective model seam. |
| R-1.2 | Chat MUST support concurrent use of different models/providers. |
| R-1.3 | Non-chat services MUST resolve through service-provider contracts and persisted service mode state. |
| R-1.4 | Per-request mode override, when supported, MUST take precedence over service defaults. |
| R-1.5 | Chat MUST NOT use non-chat service mode UX semantics. |
| R-1.6 | Chat resolution MUST remain split into resolve target then validate executability. |
| R-1.7 | No silent fallback for provider/model/runtime/mode errors. |

## R-2. Error contract

| ID | Requirement |
|----|-------------|
| R-2.1 | Routing failures MUST use a single structured error family (`RoutingException` -> problem details). |
| R-2.2 | Errors MUST expose stable machine fields (`code`, `service`, optional `modeId`, optional `modelId`, optional `provider`, `action`). |
| R-2.3 | Codes MUST include: `ROUTING_MODE_NOT_FOUND`, `ROUTING_PROVIDER_NOT_READY`, `ROUTING_MODEL_NOT_READY`, `ROUTING_RUNTIME_NOT_READY`. |
| R-2.4 | HTTP mapping MUST use RFC7807 and non-500 fail-fast statuses. |
| R-2.5 | Clients MUST branch on stable `code`, not message text. |

## R-3. Per-service resolution

| ID | Requirement |
|----|-------------|
| R-3.1 | Chat target validation MUST enforce model/provider/runtime readiness (including llama runtime constraints) without bypassing notebook runtime orchestration boundaries. |
| R-3.2 | Non-chat resolution MUST fail fast on missing mode/provider/model/runtime prerequisites. |
| R-3.3 | Service-specific preset behavior MUST remain service-scoped, not flattened into generic cross-service fields. |

## R-4. Persistence and concurrency

| ID | Requirement |
|----|-------------|
| R-4.1 | Settings writes MUST keep row-version optimistic concurrency behavior (`409` on stale writes). |
| R-4.2 | Secret masking semantics (`secretHasValue`, no plaintext echo) MUST be preserved. |
| R-4.3 | Bootstrap MUST NOT synthesize legacy provider mode state from deprecated keys. |
| R-4.4 | Deprecated non-chat `ActiveProviderId` read paths MUST not be used as authoritative runtime selection. |

## R-5. Settings IA and UX

| ID | Requirement |
|----|-------------|
| R-5.1 | Settings MUST present seven top-level tabs in this order: `Overview`, `Personalization`, `Connections`, `Models & Runtime`, `Services`, `Infrastructure`, `Telemetry`. |
| R-5.2 | Overview MUST provide default chat model controls and readiness summaries with deep links to owning tabs. |
| R-5.3 | Services MUST provide dedicated service editors for non-chat capabilities with provider-scoped fields and readiness context. |
| R-5.4 | Service editor interactions MUST preserve provider-scoped draft behavior and scoped validation. |
| R-5.5 | Connections MUST group provider sections and expose usage chips driven by resolver/contract metadata. |
| R-5.6 | Connections MUST retain section-level save/reset/refresh and secret-aware editing behavior. |
| R-5.7 | Infrastructure MUST show runtime-owned dependency keys, source metadata, and probes for URL dependencies. |
| R-5.8 | Telemetry MUST allow DB-backed API log level tuning without requiring container restarts. |

## R-6. Models & Runtime completeness

| ID | Requirement |
|----|-------------|
| R-6.1 | `Models & Runtime` MUST include `Catalog`, `Runtime Profiles`, and `Local Llama Runtime` workflows. |
| R-6.2 | Catalog create flow MUST remain provider-driven wizard UX. |
| R-6.3 | Catalog edit MUST remain provider-scoped and safe for existing rows. |
| R-6.4 | Catalog rows MUST surface readiness/routability signals. |
| R-6.5 | Provider coverage MUST be status-qualified: Stable (operator-supported) includes `openai-chat`, `openai-responses`, `azure-openai-chat`, `azure-openai-responses`, `anthropic`, `llama-cpp`, `google-gemini-chat`; Experimental/Hidden includes `hf-inference-chat`, `openrouter-chat`; roadmap providers are documented separately and are not shipped. |
| R-6.6 | Runtime profiles MUST expose usage-aware lifecycle (including delete guard when referenced). |
| R-6.7 | Runtime profile templates (`qwen3_5`, `qwen3_6`, `gemma4`) MUST remain available. |
| R-6.8 | Local Llama Runtime surface MUST remain runtime-operations focused. |
| R-6.9 | Unified add endpoint behavior for model creation MUST remain consistent with Settings wizard flows. |
| R-6.10 | Runtime inventory MUST expose alias state, artifacts, linkage, and load/unload actions. |
| R-6.11 | Router mapping visibility MUST remain available to operators. |
| R-6.12 | Runtime dependency visibility MUST rely on Infrastructure keys and runtime inventory, not deprecated `ModelStorePath`/`RouterModelsConfigPath` settings requirements. |

## R-7. Llama runtime backend behavior

| ID | Requirement |
|----|-------------|
| R-7.1 | Inventory endpoint MUST provide merged alias/artifact/runtime/catalog linkage state. |
| R-7.2 | Load/unload/download/status routes MUST remain compatible with current UI flows. |
| R-7.3 | `LlamaModelManagement` options requirement is `AllowOverwrite` (no required host-path settings). |
| R-7.4 | Hugging Face token for download paths MUST resolve from the `HuggingFace` settings section token (`HuggingFace:Token`) via resolver logic. |
| R-7.5 | Downloads MUST be non-destructive and serialized for conflicting targets. |
| R-7.6 | Router alias registration MUST remain atomic and preserve existing entries. |
| R-7.7 | Unload with active notebook references MUST require explicit operator confirmation semantics. |

## R-8. Onboarding and operator flows

| ID | Requirement |
|----|-------------|
| R-8.1 | Runtime profile onboarding templates must remain operator-accessible and idempotent. |
| R-8.2 | Catalog onboarding helpers for local llama models must remain idempotent. |
| R-8.3 | HF-driven onboarding must produce registered inventory-ready aliases on success. |
| R-8.4 | Alias load transitions must remain observable in UI and API status. |
| R-8.5 | Post-load notebook readiness must reflect executable runtime state. |
| R-8.6 | Unloaded or unavailable runtime state must fail with stable routing/runtime-not-ready semantics. |

## R-9. Global default chat model

| ID | Requirement |
|----|-------------|
| R-9.1 | Effective model resolution seam for defaults/override MUST remain isolated in chat model resolver flow. |
| R-9.2 | Chat target readiness responses MUST include reference-kind semantics for direct/default/override paths. |
| R-9.3 | Current scope remains instance-level defaults unless explicitly expanded in a separate project. |

## R-10. Non-regression

| ID | Requirement |
|----|-------------|
| R-10.1 | Notebook runtime orchestration APIs must remain behaviorally compatible for callers. |
| R-10.2 | Existing optimistic concurrency and draft behavior must not regress. |
| R-10.3 | Existing secret handling behavior must not regress. |
| R-10.4 | Catalog delete and alias/runtime delete semantics must stay distinct. |
| R-10.5 | Provider enum/validation must remain a constrained closed set including current cloud and local chat providers. |

## R-11. Documentation

| ID | Requirement |
|----|-------------|
| R-11.1 | Canonical setup + architecture docs MUST reflect shipped behavior and current tab/runtime/provider contracts. |
| R-11.2 | Llama docs MUST reflect current token and runtime ownership model. |
| R-11.3 | Historical rollout or plan docs must not be treated as current install guidance. |

## R-12. Preserved chat load orchestration behavior

| ID | Requirement |
|----|-------------|
| R-12.1 | Notebook-scoped readiness orchestration remains authoritative for required llama alias state before chat dispatch. |
| R-12.2 | Existing load/unload/verify orchestration mechanics remain intact. |
| R-12.3 | Load state machine behavior remains preserved. |
| R-12.4 | Per-notebook serialization guarantees remain preserved. |
| R-12.5 | Chat dispatch validator integration must run at the correct orchestration boundary; transient in-flight load states must not be treated as terminal runtime failure. |
| R-12.6 | `ROUTING_RUNTIME_NOT_READY` for chat should represent unresolved terminal readiness, not normal in-flight resolution. |
| R-12.7 | Existing caller surfaces that rely on preflight-and-load must remain compatible. |
| R-12.8 | Existing notebook runtime endpoint contract shape remains stable (additive changes only). |
| R-12.9 | Automated coverage must include validator-orchestration interaction cases. |
| R-12.10 | Settings-initiated unload behavior must not create undefined contention outcomes with in-flight notebook load orchestration. |

## R-13. Non-chat service editor requirements

| ID | Requirement |
|----|-------------|
| R-13.1 | Non-chat editor model remains `service -> provider -> model/runtime options`; provider fields are service-scoped and must not bleed across providers. |
| R-13.2 | Services tab remains the owner for non-chat active provider/runtime behavior; Connections remains credential ownership; Infrastructure remains runtime dependency ownership. |
| R-13.3 | Service editor renders in consistent order: header/readiness, provider selector, provider fields, service-level fields (if any), and action row. |
| R-13.4 | Active provider label reflects persisted state; draft provider changes remain draft until save. |
| R-13.5 | Provider switching preserves in-session drafts per provider; save must not mutate unrelated provider drafts. |
| R-13.6 | Validation is scoped to operative fields for selected provider; hidden/non-operative fields must not block save. |
| R-13.7 | Service provider options must come from server contracts; provider/model compatibility remains explicit and validated. |
| R-13.8 | Save operations preserve row-version optimistic concurrency semantics (`409` on stale writes). |
| R-13.9 | For local capability operations, actions remain explicit/stateful; destructive operations require confirmation; unavailable capability states must present operator-friendly copy. |
| R-13.10 | Non-chat editor copy remains service-specific and must avoid chat/LLM-generic wording. |

## Non-goals

- Redesigning notebook runtime orchestration.
- Introducing silent fallback behavior.
- Expanding default model scope beyond current product boundary without explicit follow-up requirements.

