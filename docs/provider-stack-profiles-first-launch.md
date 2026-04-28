# Provider Stack Profiles for First Launch

Last updated: 2026-04-28

## Purpose

This document captures source data for a future first-launch experience that
can quickly configure a coherent provider stack from the current Settings
system. It is not an implementation plan for the first-launch UI and it does
not introduce new runtime behavior.

The source of truth for this snapshot was the local test site at
`http://localhost:5107/`, using the current Settings APIs:

- `GET /api/settings/models`
- `GET /api/settings/runtime-profiles`
- `GET /api/settings/llama/runtime/inventory`
- `GET /api/settings/services/{serviceId}`
- `GET /api/settings/sections/ServiceModes`
- `GET /api/settings/overview`
- `GET /api/users/current`

Secrets are intentionally not copied into this document. First launch should
collect secrets from the operator and persist them through the existing
encrypted settings path.

## Naming

**Provider Stack Profile** is the new first-launch concept described here. It
means a provider-family preset that knows which existing Settings sections,
catalog rows, service routes, and readiness checks belong together.

**Runtime Profile** is an existing llama-cpp concept. Runtime Profiles define
sampling parameters, thinking controls, and message-handling behavior for local
llama catalog rows. They are not provider stack profiles, despite the overloaded
word "profile".

## Settings IA Context

The Settings area currently has six top-level tabs:

1. Overview
2. Personalization
3. Connections
4. Models & Runtime
5. Services
6. Infrastructure

Provider stack profiles should configure provider and routing state only. They
should not write Personalization fields.

Personalization is first-launch-adjacent:

- Current user read: `GET /api/users/current`
- Current user update: `PUT /api/users/current/personalization`
- Fields: `name`, `email`

The first-launch flow must ensure a current user row exists before the
Personalization tab can load. Do not assume the removed standalone
`DatabaseSeeder` project is responsible for creating that user.

## Shared Profile Shape

Each provider stack profile should be described with these fields:

| Field | Meaning |
|-------|---------|
| Profile id | Stable id for the future first-launch preset. |
| Display name | User-facing name. |
| Provider family | Cloud or local provider family represented by the preset. |
| First-launch inputs | Values the first-launch flow asks the operator to provide. |
| Settings sections | Existing Settings sections and fields populated by the preset. |
| Chat catalog rows | `Models` rows to seed or make selectable for chat. |
| Service routes | Non-chat service providers and modes the preset can activate. |
| Readiness checks | API checks that should pass after applying the preset. |
| Current gaps | Known missing/incomplete/blocked areas from the current test data. |

First launch should prefer existing Settings concepts over a parallel
configuration model:

- Connections own credentials and provider-level settings.
- Services own active non-chat provider selection.
- `ServiceModes` remains an implementation detail behind service routing.
- Models & Runtime owns chat catalog rows and local llama Runtime Profiles.
- Infrastructure owns runtime-provided local service URLs and diagnostics.

## Profile: Azure OpenAI

| Field | Value |
|-------|-------|
| Profile id | `azure-openai` |
| Display name | Azure OpenAI |
| Provider family | Azure OpenAI and Azure AI services |
| Status | Broadest cloud stack in current Settings data, but some non-chat service modes currently reference catalog model ids that are not present. |

### First-launch inputs

Collect the minimum values needed to populate the existing Azure sections. Some
deployments may share an Azure resource, but the current app stores several
service-specific sections.

| Area | Inputs |
|------|--------|
| Chat | Azure OpenAI resource name, API key, default deployment, optional API version. |
| Embeddings | Azure OpenAI embeddings endpoint, API key, deployment. |
| Images | Azure OpenAI images endpoint, API key, generation deployment, edit deployment, optional API version. |
| Speech | Azure Speech API key, region, optional endpoint, timeouts/retries where exposed. |
| Document Intelligence | Azure Document Intelligence endpoint, API key, optional API version, timeout, retries. |

### Settings sections

| Section | Fields used by this profile |
|---------|-----------------------------|
| `AzureOpenAI` | `Resource`, `ApiKey`, `Deployment`, `ApiVersion` |
| `AzureOpenAiEmbedding` | `Endpoint`, `ApiKey`, `Deployment` |
| `AzureOpenAiImages` | `Endpoint`, `ApiKey`, `Deployment`, `EditModelDeployment`, `ApiVersion` |
| `AzureSpeechService` | `ApiKey`, `Region`, `Endpoint`, `TimeoutSeconds`, `MaxRetries` |
| `AzureDocumentIntelligence` | `Endpoint`, `ApiKey`, `ApiVersion`, `TimeoutSeconds`, `MaxRetries` |

### Chat catalog rows

Seed or expose these current Azure chat rows:

| Model id | Provider | Notes |
|----------|----------|-------|
| `gpt-4.1` | `azure-openai-chat` | Ready in current overview. |
| `gpt-4.1-mini` | `azure-openai-chat` | Ready. |
| `gpt-4o` | `azure-openai-chat` | Ready. |
| `gpt-4o-mini` | `azure-openai-chat` | Ready. |
| `gpt-5-chat` | `azure-openai-chat` | Ready. |
| `gpt-5.1` | `azure-openai-chat` | Ready. |
| `gpt-5` | `azure-openai-responses` | Ready, reasoning choices configured. |
| `gpt-5-mini` | `azure-openai-responses` | Ready, reasoning choices configured. |
| `gpt-5-nano` | `azure-openai-responses` | Ready, reasoning choices configured. |
| `gpt-5.2-codex` | `azure-openai-responses` | Ready, reasoning choices configured. |
| `o3` | `azure-openai-responses` | Ready, reasoning choices configured. |
| `o4-mini` | `azure-openai-responses` | Ready, reasoning choices configured. |

### Service routes

| Service | Provider id | Provider section | Current mode id | Current state |
|---------|-------------|------------------|-----------------|---------------|
| Embeddings | `Embeddings.AzureOpenAI.Embedding` | `AzureOpenAiEmbedding` | `cloud` | Blocked in overview: `text-embedding-3-small` is referenced as a model id but is not in the catalog. |
| Image Generation | `ImageGeneration.AzureOpenAI.Images` | `AzureOpenAiImages` | `cloud` | Active default mode, but blocked in overview: `FLUX.1-Kontext-pro` is referenced as a model id but is not in the catalog. |
| Speech Transcription | `SpeechTranscription.AzureSpeech.Batch` | `AzureSpeechService` | `azure` | Ready. |
| Speech Synthesis | `SpeechSynthesis.AzureSpeech.Ssml` | `AzureSpeechService` | `cloud` | Ready. |
| Document Intelligence | `DocumentIntelligence.Azure.DocumentIntelligence` | `AzureDocumentIntelligence` | `cloud` | Ready. |

### Readiness and gaps

- Validate chat with `/api/settings/overview` chat targets.
- Validate non-chat service modes through `/api/settings/sections/ServiceModes`
  and `/api/settings/overview`.
- Current first-launch design must decide whether service `ModelId` values like
  `text-embedding-3-small` and `FLUX.1-Kontext-pro` should create catalog rows
  or stop being treated as catalog-model references for those service providers.

## Profile: Google Gemini

| Field | Value |
|-------|-------|
| Profile id | `google-gemini` |
| Display name | Google Gemini |
| Provider family | Google Gemini API |
| Status | Chat is configured; several service providers are exposed, with some model-id readiness blockers. |

### First-launch inputs

| Area | Inputs |
|------|--------|
| Shared Google config | Gemini API key. |
| Chat | Default chat model choice. |
| Embeddings | Embedding model id. |
| Images | Image model id. |
| Speech Transcription | Audio/transcription model id. |
| Speech Synthesis | TTS model id and voice name. |

### Settings sections

| Section | Fields used by this profile |
|---------|-----------------------------|
| `GoogleGeminiApi` | `ApiKey` |
| `ServiceModes` | Per-service `ModelId` and `RequestPresetJson` where the service provider needs them. |

### Chat catalog rows

| Model id | Provider | Notes |
|----------|----------|-------|
| `gemini-2.5-flash` | `google-gemini-chat` | Ready as a chat target and used by current Speech Transcription mode. |
| `gemini-2.5-pro` | `google-gemini-chat` | Ready as a chat target and currently selected in `ChatDefaults.DefaultModelId`. |

### Service routes

| Service | Provider id | Provider section | Current mode id | Current state |
|---------|-------------|------------------|-----------------|---------------|
| Embeddings | `Embeddings.Google.Embedding` | `GoogleGeminiApi` | `Embeddings.Google.Embedding` | Disabled mode; blocked if probed because `gemini-embedding-2` is not in the catalog. |
| Image Generation | `ImageGeneration.Google.Imagen` | `GoogleGeminiApi` | `google` | Enabled but blocked: `gemini-2.5-flash-image` is not in the catalog. |
| Speech Transcription | `SpeechTranscription.Google.SpeechToText` | `GoogleGeminiApi` | `google` | Ready with `gemini-2.5-flash`. |
| Speech Synthesis | `SpeechSynthesis.Google.TextToSpeech` | `GoogleGeminiApi` | `SpeechSynthesis.Google.TextToSpeech` | Active default mode, but blocked: `gemini-3.1-flash-tts-preview` is not in the catalog. Current preset includes `VoiceName=Kore`. |
| Document Intelligence | None | None | None | No Gemini-backed Document Intelligence provider is currently exposed. |

### Readiness and gaps

- Current `GoogleGeminiApi` credential readiness is fine in the test data.
- Image generation, embeddings, and speech synthesis have model-id catalog
  blockers in the current overview.
- Document Intelligence has no Google provider in the current service contract.
- First launch should either seed the missing service model ids into the catalog
  or avoid configuring those service modes as ready until the service routing
  semantics are settled.

## Profile: OpenAI

| Field | Value |
|-------|-------|
| Profile id | `openai` |
| Display name | OpenAI |
| Provider family | OpenAI platform |
| Status | Chat-only/incomplete for routed non-chat services in the current app. |

### First-launch inputs

| Area | Inputs |
|------|--------|
| Chat | OpenAI API key, optional endpoint override, optional deployment/default model. |

### Settings sections

| Section | Fields used by this profile |
|---------|-----------------------------|
| `OpenAI` | `ApiKey`, `Endpoint`, `Deployment` |

### Chat catalog rows

| Model id | Provider | Notes |
|----------|----------|-------|
| `gpt-4.1-nano` | `openai-chat` | Ready. |
| `gpt-5.1-2025-11-13` | `openai-chat` | Ready. |

### Service routes

No first-party OpenAI providers are currently exposed for the five routed
non-chat services:

- Embeddings
- Image Generation
- Speech Transcription
- Speech Synthesis
- Document Intelligence

The Settings system does expose OpenRouter and Hugging Face service providers,
but those are separate provider families and should not be silently folded into
the OpenAI provider stack profile.

### Readiness and gaps

- This profile should be marked chat-only/incomplete for first launch.
- It can configure chat models and `ChatDefaults`, but it should not claim a
  full multimodal/service stack.
- Adding first-party OpenAI non-chat service providers would be a separate
  service-contract expansion.

## Profile: Anthropic

| Field | Value |
|-------|-------|
| Profile id | `anthropic` |
| Display name | Anthropic |
| Provider family | Anthropic |
| Status | LLM-only special case. |

### First-launch inputs

| Area | Inputs |
|------|--------|
| Chat | Anthropic API key or auth token, optional base URL, optional default model, optional default max tokens/thinking budgets. |

### Settings sections

| Section | Fields used by this profile |
|---------|-----------------------------|
| `Anthropic` | `BaseUrl`, `ApiKey`, `AuthToken`, `DefaultModel`, `DefaultMaxTokens`, `ThinkingBudgetMinimal`, `ThinkingBudgetLow`, `ThinkingBudgetMedium`, `ThinkingBudgetHigh` |

### Chat catalog rows

| Model id | Provider | Notes |
|----------|----------|-------|
| `claude-opus-4-5` | `anthropic` | Ready; reasoning choices `minimal`, `low`, `medium`, `high`. |
| `claude-sonnet-4-5` | `anthropic` | Ready; reasoning choices `minimal`, `low`, `medium`, `high`. |
| `claude-haiku-4-5` | `anthropic` | Ready; reasoning choices `minimal`, `low`, `medium`, `high`. |

### Service routes

Anthropic is LLM-only in the current app. There are no Anthropic providers for:

- Embeddings
- Image Generation
- Speech Transcription
- Speech Synthesis
- Document Intelligence

### Readiness and gaps

- This profile should configure chat only.
- First launch must not imply Anthropic can satisfy the routed service stack.
- Any non-chat services used alongside Anthropic would be an explicit separate
  user choice, not part of the Anthropic provider stack profile.

## Profile: Local llama/local AI

| Field | Value |
|-------|-------|
| Profile id | `local-ai` |
| Display name | Local llama/local AI |
| Provider family | Local runtime services |
| Status | Most complete local stack, dependent on runtime-owned infrastructure and local artifacts. |

### First-launch inputs

Local first launch is not primarily "API key plus endpoint". It should validate
runtime-owned infrastructure and either adopt existing local artifacts or guide
the user through model downloads.

| Area | Inputs or checks |
|------|------------------|
| llama chat | Runtime availability, router alias, Runtime Profile, optional Hugging Face token for downloads, model GGUF, optional mmproj. |
| Embeddings | Local embeddings base URL readiness, timeout, pacing interval. |
| Image Generation | Local SD base URL readiness, timeout, output format, bundle/engine readiness. |
| Speech Transcription | Local ASR base URL readiness, media base URL readiness, timeout. |
| Speech Synthesis | Local TTS base URL readiness, timeout. |
| Document Intelligence | Local Docling base URL readiness, timeout/concurrency/poll interval and Docling controls. |

### Runtime-owned infrastructure

These values are not normal provider-profile writes. They come from appsettings,
environment variables, compose, or runtime container configuration and should be
reported/validated by first launch.

| Key | Current role |
|-----|--------------|
| `LlamaCpp:BaseUrl` | llama.cpp chat runtime endpoint. |
| `LocalServiceHosts:EmbeddingsBaseUrl` | Local embeddings service. |
| `LocalServiceHosts:ImageGenerationBaseUrl` | Local Stable Diffusion service. |
| `LocalServiceHosts:SpeechTranscriptionBaseUrl` | Local ASR service. |
| `LocalServiceHosts:SpeechSynthesisBaseUrl` | Local TTS service. |
| `LocalServiceHosts:MediaBaseUrl` | Media extraction dependency used by transcription. |
| `LocalServiceHosts:DocumentIntelligenceBaseUrl` | Local Docling service. |

### Chat catalog rows and router aliases

| Catalog model id | Router alias | Runtime profile id | Runtime state | Artifacts | Notes |
|------------------|--------------|--------------------|---------------|-----------|-------|
| `qwen3.5-27b` | `Qwen3.5-27B-Q6_K` | `qwen3_5` | `unloaded` | GGUF and mmproj present | `parallelToolCalls=true`. |
| `qwen3.5-35b-a3b` | `Qwen3.5-35B-A3B-Q5_K_XL` | `qwen3_5` | `unloaded` | GGUF and mmproj present | Notebook reference count is currently 99. |
| `Qwen3.5-9B-Q5_K_M` | `Qwen3.5-9B-Q5_K_M` | `qwen3_5` | `unloaded` | GGUF and mmproj present | Direct catalog id matches alias. |
| `Qwen3.6-27B-UD-Q5_K_XL` | `Qwen3.6-27B-UD-Q5_K_XL` | `qwen3_5` | `unloaded` | GGUF and mmproj present | Direct catalog id matches alias. |
| `Qwen3.6-35B-A3B-UD-Q5_K_M` | `Qwen3.6-35B-A3B-UD-Q5_K_M` | `qwen3_5` | `unloaded` | GGUF and mmproj present | `parallelToolCalls=true`. |
| `gemma-4-26B-A4B-it-UD-Q5_K_XL` | `gemma-4-26B-A4B-it-UD-Q5_K_XL` | `gemma4` | `loaded` | GGUF and mmproj present | Only loaded local chat target in the current snapshot. |
| `gemma-4-31B-it-Q5_K_M` | `gemma-4-31B-it-Q5_K_M` | `gemma4` | `unloaded` | GGUF and mmproj present | Router context size is `131072`; `parallelToolCalls=true`. |

All current local llama catalog rows expose reasoning choices
`["none","enabled"]`.

### Existing llama Runtime Profiles

These are existing llama Runtime Profiles and should be referenced by local
catalog rows. They are not first-launch provider stack profiles.

| Runtime profile id | Display name | Purpose |
|--------------------|--------------|---------|
| `qwen3_5` | Qwen 3.5 | Qwen-family local models. Thinking uses `chat_template_kwargs.enable_thinking`; default choice is `enabled`. |
| `gemma4` | Gemma 4 | Gemma 4 local models. Thinking uses a system-message prefix; default choice is `enabled`. |

### Service routes

| Service | Provider id | Provider section | Current mode id | Current state |
|---------|-------------|------------------|-----------------|---------------|
| Embeddings | `Embeddings.LocalEmb.Http` | `LocalServiceHosts:EmbeddingsBaseUrl` | `local` | Active default and ready. |
| Image Generation | `ImageGeneration.LocalSd.Http` | `LocalServiceHosts:ImageGenerationBaseUrl` | `local` | Ready, not current default. |
| Speech Transcription | `SpeechTranscription.LocalAsr.Http` | `LocalServiceHosts:SpeechTranscriptionBaseUrl` | `local` | Active default and ready. |
| Speech Synthesis | `SpeechSynthesis.LocalTts.Http` | `LocalServiceHosts:SpeechSynthesisBaseUrl` | `local` | Ready, not current default. |
| Document Intelligence | `DocumentIntelligence.LocalDocling.Http` | `LocalServiceHosts:DocumentIntelligenceBaseUrl` | `local` | Active default and ready. |

### Readiness and gaps

- Current llama runtime summary: 1 loaded alias, 7 total aliases, no missing
  artifact aliases.
- Most llama chat targets are blocked in Overview only because their aliases are
  unloaded. This is a runtime state, not a missing configuration/artifact state.
- First launch should distinguish "configured but unloaded" from "not
  configured".
- First launch should not mutate runtime-owned base URLs. It should validate
  and explain where to change them.

## Current ServiceModes Snapshot

This snapshot matters because a future first-launch feature will probably write
or replace this data through service-editor APIs or equivalent backend
orchestration.

| Service | Current default mode | Modes |
|---------|----------------------|-------|
| `Embeddings` | `local` | `local` ready; `cloud` references `AzureOpenAiEmbedding` and `text-embedding-3-small`; `Embeddings.Google.Embedding` references `GoogleGeminiApi` and `gemini-embedding-2` but is disabled. |
| `ImageGeneration` | `cloud` | `local` ready; `cloud` references `AzureOpenAiImages` and `FLUX.1-Kontext-pro`; `google` references `GoogleGeminiApi` and `gemini-2.5-flash-image`. |
| `SpeechTranscription` | `local` | `google` ready with `gemini-2.5-flash`; `azure` ready; `local` ready. |
| `SpeechSynthesis` | `SpeechSynthesis.Google.TextToSpeech` | `local` ready; `cloud` ready; Google TTS references `gemini-3.1-flash-tts-preview` with `VoiceName=Kore` but is blocked by missing catalog model. |
| `DocumentIntelligence` | `local` | `local` ready; `cloud` ready with Azure Document Intelligence preset values. |

## First-launch Acceptance Notes

A first-launch provider stack flow based on this document should:

- Configure only the selected provider profile and the explicit service routes
  that belong to it.
- Preserve the current separation between Personalization, Connections,
  Services, Models & Runtime, and Infrastructure.
- Never echo or document secret values.
- Surface incomplete stacks honestly: OpenAI and Anthropic are chat-only in the
  current app; Google is missing Document Intelligence and has several service
  model catalog blockers.
- Treat local runtime infrastructure as externally owned and validate it rather
  than overwriting it.
- Treat llama Runtime Profiles as local model runtime data, not as provider
  stack profiles.

## Verification Checklist

Before this document is used to drive implementation, re-check:

- Every chat model row in this document exists in `GET /api/settings/models`,
  or is intentionally removed from the profile.
- Every llama Runtime Profile id exists in `GET /api/settings/runtime-profiles`.
- Every llama router alias exists in
  `GET /api/settings/llama/runtime/inventory`, or the profile marks it as a
  download/adoption target.
- Every service provider id appears in
  `GET /api/settings/services/{serviceId}`.
- Every expected service mode appears in
  `GET /api/settings/sections/ServiceModes`.
- `/api/users/current` returns a user before the Settings Personalization tab is
  shown.
