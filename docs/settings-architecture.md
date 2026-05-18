# Settings Architecture and Extension Guide

Last updated: 2026-05-18

This is the developer-facing architecture source of truth for the Settings experience.
It describes the current implementation shape and where to extend it safely.

Source-of-truth set for provider/runtime behavior:
- [setup-guide.md](setup-guide.md)
- [settings-and-llama-completion-requirements.md](settings-and-llama-completion-requirements.md)

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

Supported chat provider ids (validated as a closed set, status-qualified):

Stable (operator-supported):

- `openai-chat`
- `openai-responses`
- `azure-openai-chat`
- `azure-openai-responses`
- `anthropic`
- `llama-cpp`
- `google-gemini-chat`

Experimental/Hidden (implemented, partial/in-flight, not generally operator-facing):

- `hf-inference-chat`
- `openrouter-chat`

Roadmap (not shipped): see roadmap docs only; do not treat as currently available setup providers.

### Default chat model behavior

- `defaultModelId`: instance default chat catalog model.
- `overrideAllChatModels`:
  - `true`: all chat turns route to the default model.
  - `false`: entity `modelId` is used when set; empty/omitted model uses default.
- Sampling overrides from chat defaults apply for default and override paths.

Resolver seams:

- `IChatModelResolver` is the canonical seam for effective chat model resolution.
- `IChatTargetResolver` and `IChatTargetValidator` handle target resolution and execution validation.

Settings/UI surfaces:

- `GET/PUT /api/settings/chat-defaults`
- `Settings -> Overview` default chat model controls
- Guide/assistant editor support for "Use Default Model"
- `Home -> Add AI Services Wizard` can set the first/added model as default chat model.

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

The wizard currently supports four provider paths: `foundry`, `google-gemini`, `openai`, and `local-ai`.

**Cloud provider path (foundry / google-gemini / openai pattern)**:

1. Add types to `addAiServicesWizard/types.ts` and constants to `constants.ts`.
2. Add a connection step, models step, and optional-services step component.
3. Add provider-specific branches in `AddAiServicesWizard.tsx` for each wizard step.
4. Keep first-launch predicate aligned with `CONNECTION_SECTION_NAME_SET` and model count logic.
5. Reuse existing Settings APIs; avoid parallel config systems.

**Local/async provider path (local-ai pattern)**:

The local AI path uses a dedicated hook (`useLocalAiWizardState`) to isolate async state (download polling, inventory, runtime profiles) from the main wizard component. Follow this pattern for any provider that involves long-running async operations or multiple infrastructure dependencies.

1. Extract provider state into a `use{Provider}WizardState` hook.
2. Add a prerequisites step (instead of a generic connection step) to surface infrastructure readiness.
3. Use polling to track async operations and surface progress within the wizard.
4. Persist via existing Settings APIs only; never introduce parallel configuration systems.

General rules:

- Add/refresh wizard tests for step persistence and first-launch behavior.
- Keep step/provider constants in `constants.ts` authoritative.

Wizard deep dive: [add-ai-services-wizard.md](add-ai-services-wizard.md)

## 7. Related docs

- Requirements baseline: [settings-and-llama-completion-requirements.md](settings-and-llama-completion-requirements.md)
- Llama lifecycle and runtime ops: [llama-model-download-and-runtime-management.md](llama-model-download-and-runtime-management.md)
- Operator setup: [setup-guide.md](setup-guide.md)

