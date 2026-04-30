# Settings Page: Architecture and Extension Guide

Last updated: 2026-04-30

This is the developer-facing architecture source of truth for the Settings experience.
It describes the current implementation shape and where to extend it safely.

## 1. Settings information architecture

Top-level tabs (exact order from `SettingsTabNavigation.tsx`):

1. `Overview`
2. `Personalization`
3. `Connections`
4. `Models & Runtime`
5. `Services`
6. `Infrastructure`
7. `Telemetry`

Responsibilities:

- **Overview**: default chat model controls + readiness summaries + deep links.
- **Personalization**: user name/email profile fields.
- **Connections**: provider credential sections, usage chips, section save/reset flows.
- **Models & Runtime**: model catalog, runtime profiles, local llama runtime inventory/actions.
- **Services**: non-chat service editors and provider-specific service settings.
- **Infrastructure**: runtime-owned dependency keys, source metadata, probe execution.
- **Telemetry**: DB-backed API logging level controls and subsystem presets.

## 2. Routing model and contracts

### Chat routing

Chat uses assistant-selected catalog models plus defaults/overrides:

- `IChatModelResolver` resolves effective model id.
- `IChatTargetResolver` resolves `(catalog model, provider section)`.
- `IChatTargetValidator` validates provider requirements and model/runtime constraints.

Supported chat provider ids (validated as a closed set):

- `openai-chat`
- `openai-responses`
- `azure-openai-chat`
- `azure-openai-responses`
- `anthropic`
- `llama-cpp`
- `google-gemini-chat`
- `hf-inference-chat`
- `openrouter-chat`

### Non-chat routing

Non-chat capabilities are edited from **Services** and resolved through service-provider contracts and service mode state.
No silent fallback is allowed for provider/model/runtime selection.

## 3. Runtime-owned dependencies (Infrastructure)

The runtime dependency catalog currently includes:

- `LlamaCpp:BaseUrl`
- `LocalServiceHosts:SpeechTranscriptionBaseUrl`
- `LocalServiceHosts:SpeechSynthesisBaseUrl`
- `LocalServiceHosts:ImageGenerationBaseUrl`
- `LocalServiceHosts:EmbeddingsBaseUrl`
- `LocalServiceHosts:MediaBaseUrl`
- `LocalServiceHosts:DocumentIntelligenceBaseUrl`

These are runtime-owned and surfaced read-only in Settings with source + probe status.

## 4. Local llama ownership model

- API delegates runtime operations to `guideants-ai` admin routes.
- API does not own host model directories as a primary control path.
- `LlamaModelManagementOptions` currently exposes `AllowOverwrite`.
- Hugging Face token is resolved from `HuggingFace:Token` (Connections section), not from llama options.

## 5. Core endpoints used by Settings

Primary Settings endpoints live in `SettingsEndpoints.cs`:

- `/api/settings/sections*`, `/api/settings/schema`, `/api/settings/readiness`
- `/api/settings/chat-defaults`
- `/api/settings/models*`
- `/api/settings/runtime-profiles*`
- `/api/settings/services/{serviceId}*`
- `/api/settings/routing/chat-targets*`
- `/api/settings/overview`
- `/api/settings/connections/{section}/usage`
- `/api/settings/infrastructure/*`
- `/api/settings/llama/*`

## 6. How to extend Settings safely

### Add a new chat provider

1. Add provider client/factory wiring in chat runtime and routing factory.
2. Add provider id support in validator known providers.
3. Add section mapping/readiness wiring.
4. Add catalog/provider UI support where provider ids are selected.
5. Add routing + validator tests for success and fail-fast cases.

### Add or modify a non-chat service provider path

1. Update server service contract/provider metadata.
2. Update service editor DTO handling and validation.
3. Update Services editor UI for provider-specific fields.
4. Ensure Connections usage chips and readiness reflect the new provider.
5. Add service editor and endpoint tests.

### Extend Add AI Services Wizard behavior

1. Update wizard constants/types under `components/home/addAiServicesWizard`.
2. Keep first-launch predicate aligned with `CONNECTION_SECTION_NAME_SET` and model count logic.
3. Reuse existing Settings APIs; avoid parallel config systems.
4. Add/refresh wizard tests for step persistence and first-launch behavior.

Wizard deep dive: [add-ai-services-wizard.md](add-ai-services-wizard.md)

## 7. Related docs

- Requirements baseline: [settings-and-llama-completion-requirements.md](settings-and-llama-completion-requirements.md)
- Service editor requirements: [settings-service-provider-model-requirements.md](settings-service-provider-model-requirements.md)
- Default chat model behavior: [default-chat-models.md](default-chat-models.md)
- Llama lifecycle and runtime ops: [llama-model-download-and-runtime-management.md](llama-model-download-and-runtime-management.md)
- Operator setup: [setup-guide.md](setup-guide.md)
