# Llama Model Download + Runtime Management

Last updated: 2026-04-30

This document describes the shipped local llama lifecycle in GuideAnts:
model onboarding, alias inventory, load/unload orchestration, and fail-fast behavior.

## 1. Ownership model

- Runtime model artifacts and alias registration are owned by the runtime (`guideants-ai`).
- The web API delegates download/register/status operations to runtime admin endpoints.
- The API does not use deprecated `ModelStorePath`/`RouterModelsConfigPath` options as required settings contracts.

## 2. Token and options behavior

- Hugging Face token source of truth: `HuggingFace:Token` (Connections section), resolved by `IHuggingFaceTokenResolver`.
- `LlamaModelManagementOptions` currently provides `AllowOverwrite`.
- No per-request token override is part of the normal Settings add/download flow.

## 3. Core backend components

- `ILlamaRuntimeCoordinator`: per-alias operation serialization.
- `NotebookModelRuntimeService`: notebook-scoped runtime orchestration.
- `ILlamaRuntimeAdminClient`: API client for runtime admin routes.
- `ILlamaRuntimeInventoryService`: merged alias/artifact/runtime/catalog view.
- `IRouterModelsConfigService`: router mapping management via runtime admin.
- `IHuggingFaceModelDownloadService`: delegated download + optional catalog registration integration.

## 4. Settings endpoints (llama-focused)

- `GET /api/settings/llama/runtime/inventory`
- `POST /api/settings/llama/runtime/load`
- `POST /api/settings/llama/runtime/unload`
- `GET /api/settings/llama/runtime/status`
- `POST /api/settings/llama/downloads`
- `GET /api/settings/llama/downloads/{operationId}`
- `DELETE /api/settings/llama/router/entries/{routerModelId}`
- `GET /api/settings/llama/huggingface/repositories/{owner}/{repo}/files`

## 5. UX surfaces

`Settings -> Models & Runtime` provides:

- Catalog model onboarding/editing
- Runtime profiles
- Local llama runtime inventory/actions

`Settings -> Infrastructure` provides runtime dependency visibility/probes,
including `LlamaCpp:BaseUrl` and local service host keys.

## 6. Runtime and error guarantees

1. No silent fallback on unavailable aliases or provider/runtime mismatch.
2. Alias operations are serialized to avoid conflicting load/unload actions.
3. Runtime-not-ready states surface through stable routing/runtime error codes.
4. Notebook chat orchestration and validator boundaries preserve load-on-demand semantics.

## 7. Operator troubleshooting quick checks

1. Verify `LlamaCpp:BaseUrl` in Infrastructure and run probes.
2. Verify runtime health endpoints from API/container network perspective.
3. Verify HuggingFace token exists in Connections when download flows are used.
4. Use runtime inventory to inspect alias artifact/readiness state.

## 8. Related docs

- Default chat model resolution: [default-chat-models.md](default-chat-models.md)
- Settings architecture: [settings-page-provider-model-llama-redesign.md](settings-page-provider-model-llama-redesign.md)
- Requirements baseline: [settings-and-llama-completion-requirements.md](settings-and-llama-completion-requirements.md)
- Operator setup: [setup-guide.md](setup-guide.md)
