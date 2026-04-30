# Add AI Services Wizard

Last updated: 2026-04-30

This document describes the **as-built** Home onboarding wizard behavior.

## 1. Purpose

The wizard helps operators reach a minimally usable AI setup by guiding:

- provider connection setup,
- initial chat model creation,
- optional non-chat service setup.

It complements Settings; it does not replace Settings ownership boundaries.

## 2. Entry points

- Automatic on Home (`/`) during first-launch predicate checks.
- Manual from Home header button (`Setup Wizard`).

## 3. Auto-open predicate (as built)

Home checks:

- `GET /api/settings/sections`
- `GET /api/settings/models`

Wizard opens when:

- configured connection sections count is zero, **or**
- catalog model count is zero.

Configured connections use `CONNECTION_SECTION_NAME_SET` and `readinessStatus === configured`.

Current connection section set is derived from `connectionSections.ts` and includes:

- `AzureOpenAI`, `OpenAI`, `Anthropic`, `GoogleGeminiApi`, `OpenRouter`, `HuggingFace`
- `AzureSpeechService`, `AzureOpenAiImages`, `AzureOpenAiEmbedding`, `AzureDocumentIntelligence`

Auto-open is skipped when dismissal key exists:

- `guideants.firstLaunch.addAiServicesWizard.dismissed.v1`

If probe calls fail, auto-open is skipped (non-blocking behavior).

## 4. Providers and steps

Provider options (current):

- `foundry` (`Microsoft Foundry`)
- `google-gemini` (`Google Gemini`)
- `openai` (`OpenAI`)
- `local-ai` (`Local AI`)

Step sequence (current):

1. `Provider`
2. `Connection details` (cloud providers) / `Prerequisites` (Local AI)
3. `Models`
4. `Optional services`
5. `Finish`

## 5. Footer and dismissal behavior

Footer actions are always visible:

- `Not now`
- `Configure manually`
- `Back`
- `Next`
- `Finish`

Behavior:

- `Finish` on non-final step persists current step and jumps to `Finish` step.
- Wizard closes only when `Finish` is clicked on `Finish` step.
- Overlay click dismissal is disabled.
- Checkbox persists dismissal preference for future auto-open behavior.

## 6. Provider-specific notes

### Microsoft Foundry path

- Core section: `AzureOpenAI`
- Optional service sections: `AzureOpenAiEmbedding`, `AzureOpenAiImages`, `AzureSpeechService`, `AzureDocumentIntelligence`
- Model provider labels map to:
  - `Completions` -> `azure-openai-chat`
  - `Responses` -> `azure-openai-responses`

### Google Gemini path

- Core section: `GoogleGeminiApi`
- Default chat model id prefill: `gemini-2.5-flash`
- Optional service defaults include Gemini model ids/timeouts and TTS voice defaults.

### OpenAI path

- Core section: `OpenAI`
- Default chat model id prefill: `gpt-4.1-nano`
- Model provider labels map to:
  - `Completions` -> `openai-chat`
  - `Responses` -> `openai-responses`
- Optional services: Embeddings, Image Generation, Speech Transcription, Speech Synthesis (no Document Intelligence).

### Local AI path

The local AI path differs structurally from the cloud provider paths. It uses a dedicated hook (`useLocalAiWizardState`) to manage the more complex local-AI-specific state.

**Prerequisites step** (replaces Connection step):

- `HuggingFace:Token` — stored in the `HuggingFace` settings section; used for model downloads.
- Infrastructure status panel — displays live readiness for each runtime dependency key (`LlamaCpp:BaseUrl`, all `LocalServiceHosts:*` keys). Fetched from `GET /api/settings/infrastructure/dependencies`. These keys are set at the container/environment level and cannot be changed in the wizard.

**Models step** (async):

- Reuses `RepositoryFilePicker` and `llamaCppClassifier` from `settings/editors/common/`.
- Supports two install sources: `huggingface` (repo browse + GGUF file pick) and `existingAlias` (attach an unregistered live alias from inventory).
- Models are added to a draft queue and installed individually via `POST /api/settings/models:add`.
- Async installs (`operationId` present in response) are polled via `GET /api/settings/llama/downloads/{operationId}` and show step-by-step progress (Queued → Resolving files → Downloading → Registering alias → Completed).
- Runtime profiles are loaded lazily from `GET /api/settings/runtime-profiles` when the step becomes active.
- Llama inventory is loaded lazily from `GET /api/settings/llama/runtime/inventory` when the step becomes active.

**Optional services step:**

- Toggle-based form for five local services:

| Service | Provider ID | Infrastructure key |
|---|---|---|
| Embeddings | `Embeddings.LocalEmb.Http` | `LocalServiceHosts:EmbeddingsBaseUrl` |
| Image Generation | `ImageGeneration.LocalSd.Http` | `LocalServiceHosts:ImageGenerationBaseUrl` |
| Speech Transcription | `SpeechTranscription.LocalAsr.Http` | `LocalServiceHosts:SpeechTranscriptionBaseUrl` |
| Speech Synthesis | `SpeechSynthesis.LocalTts.Http` | `LocalServiceHosts:SpeechSynthesisBaseUrl` |
| Document Intelligence | `DocumentIntelligence.LocalDocling.Http` | `LocalServiceHosts:DocumentIntelligenceBaseUrl` |

- Each service card shows the required infrastructure key so operators know which container environment variable must be set.
- Persists via `PUT /api/settings/services/{serviceId}/providers/{providerId}/fields` and `PUT /api/settings/services/{serviceId}/active-provider`.

**readyForBasicChat** condition for Local AI: at least one llama-cpp model has been installed (completed draft or pre-existing catalog row).

**Key constants** (in `constants.ts`):

- `HUGGINGFACE_SECTION = 'HuggingFace'`
- `LLAMA_CPP_SECTION = 'LlamaCpp'`
- `LOCAL_AI_SERVICE_PROVIDER_IDS` — map from service key to provider ID string
- `LOCAL_AI_OPTIONAL_SERVICE_DEFAULTS` — timeout/field defaults per service
- `LOCAL_AI_INFRASTRUCTURE_KEYS` — ordered list of dependency keys shown in Prerequisites

## 7. Extension guide

When extending wizard behavior:

1. Keep step/provider constants in `components/home/addAiServicesWizard/constants.ts` authoritative.
2. Keep first-launch predicate aligned with `CONNECTION_SECTION_NAME_SET` and model-count logic in `Home.tsx`.
3. Reuse existing Settings APIs/contracts; do not introduce parallel configuration ownership.
4. For new cloud providers, follow the Gemini/OpenAI pattern: add types, constants, a connection step, a models step, an optional-services step, and per-provider branches in `AddAiServicesWizard.tsx`.
5. For new local/async providers, extract state into a dedicated hook following the `useLocalAiWizardState` pattern.
6. Add tests for:
   - predicate matrix,
   - provider/step persistence,
   - finish/dismiss behavior,
   - dismissal key persistence.

## 8. Related docs

- Operator setup: [setup-guide.md](setup-guide.md)
- Settings architecture: [settings-page-provider-model-llama-redesign.md](settings-page-provider-model-llama-redesign.md)
- Default chat model behavior: [default-chat-models.md](default-chat-models.md)
- Requirements baseline: [settings-and-llama-completion-requirements.md](settings-and-llama-completion-requirements.md)
- Llama lifecycle and runtime ops: [llama-model-download-and-runtime-management.md](llama-model-download-and-runtime-management.md)
