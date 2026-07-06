# Provider, Model, and Service Configuration Report

Generated: 2026-05-25 20:15:00 UTC
Source: running guideants-webapi-ui settings API (DB-backed sections/models) plus runtime dependency overlays from container environment

## Scope
Includes providers marked configured, mapped chat models, and service modes that reference each provider section.
Secret values are excluded. Secret fields are reported only as configured/not set.

## Anthropic

- Readiness: configured
- Updated UTC: 04/28/2026 16:18:19
- Configuration:
  - BaseUrl: https://guideants-ai-images.services.ai.azure.com/anthropic
  - ApiKey: configured
  - AuthToken: not set
- Models:
  - claude-haiku-4-5 (provider=anthropic, active=True), runtimeProfileId=anthropic_standard
  - claude-opus-4-5 (provider=anthropic, active=True), runtimeProfileId=anthropic_standard
  - claude-sonnet-4-5 (provider=anthropic, active=True), runtimeProfileId=anthropic_standard
- Service Configurations:
  - No service modes currently reference this provider section.

## AzureDocumentIntelligence

- Readiness: configured
- Updated UTC: 04/28/2026 16:18:19
- Service Provider IDs: DocumentIntelligence.Azure.DocumentIntelligence
- Configuration:
  - Endpoint: https://waterfall-dev.cognitiveservices.azure.com/
  - ApiKey: configured
- Models:
  - None mapped from current model catalog.
- Service Configurations:
  - Service DocumentIntelligence / Mode cloud
    - ProviderSection: AzureDocumentIntelligence
    - Enabled: True; Default: False; Status: ready
    - RequestPresetJson:
```json
{"ApiVersion":"2024-11-30","MaxRetries":"3"}
```

## AzureOpenAI

- Readiness: configured
- Updated UTC: 04/28/2026 16:18:19
- Configuration:
  - Resource: ai-dougwareai685749536435
  - ApiKey: configured
  - ApiVersion: 2025-04-01-preview
- Models:
  - gpt-4.1 (provider=azure-openai-chat, active=True), runtimeProfileId=openai_chat_standard
  - gpt-4.1-mini (provider=azure-openai-chat, active=True), runtimeProfileId=openai_chat_standard
  - gpt-4o (provider=azure-openai-chat, active=True), runtimeProfileId=openai_chat_standard
  - gpt-4o-mini (provider=azure-openai-chat, active=True), runtimeProfileId=openai_chat_standard
  - gpt-5-chat (provider=azure-openai-chat, active=True), runtimeProfileId=openai_chat_standard
  - gpt-5.1 (provider=azure-openai-chat, active=True), runtimeProfileId=openai_chat_standard
  - gpt-5 (provider=azure-openai-responses, active=True), runtimeProfileId=openai_responses_reasoning
  - gpt-5-mini (provider=azure-openai-responses, active=True), runtimeProfileId=openai_responses_reasoning
  - gpt-5-nano (provider=azure-openai-responses, active=True), runtimeProfileId=openai_responses_reasoning
  - gpt-5.2-codex (provider=azure-openai-responses, active=True), runtimeProfileId=openai_responses_reasoning
  - o3 (provider=azure-openai-responses, active=True), runtimeProfileId=openai_responses_reasoning
  - o4-mini (provider=azure-openai-responses, active=True), runtimeProfileId=openai_responses_reasoning
- Service Configurations:
  - No service modes currently reference this provider section.

## AzureOpenAiEmbedding

- Readiness: configured
- Updated UTC: 04/28/2026 16:18:19
- Service Provider IDs: Embeddings.AzureOpenAI.Embedding
- Configuration:
  - Endpoint: https://ai-dougwareai685749536435.openai.azure.com/
  - ApiKey: configured
- Models:
  - None mapped from current model catalog.
- Service Configurations:
  - Service Embeddings / Mode cloud
    - ProviderSection: AzureOpenAiEmbedding
    - Enabled: True; Default: False; Status: blocked
    - ModelId: text-embedding-3-small
    - Blockers: MODEL_NOT_FOUND: catalog model 'text-embedding-3-small' was not found.

## AzureOpenAiImages

- Readiness: configured
- Updated UTC: 04/28/2026 16:18:19
- Service Provider IDs: ImageGeneration.AzureOpenAI.Images
- Configuration:
  - Endpoint: https://guideants-ai-images.cognitiveservices.azure.com/
  - ApiKey: configured
  - ApiVersion: 2025-04-01-preview
- Models:
  - None mapped from current model catalog.
- Service Configurations:
  - Service ImageGeneration / Mode cloud
    - ProviderSection: AzureOpenAiImages
    - Enabled: True; Default: False; Status: blocked
    - ModelId: FLUX.1-Kontext-pro
    - RequestPresetJson:
```json
{"EditModelDeployment":"FLUX.1-Kontext-pro"}
```
    - Blockers: MODEL_NOT_FOUND: catalog model 'FLUX.1-Kontext-pro' was not found.

## AzureSpeechService

- Readiness: configured
- Updated UTC: 04/28/2026 16:18:19
- Service Provider IDs: SpeechTranscription.AzureSpeech.Batch, SpeechSynthesis.AzureSpeech.Ssml
- Configuration:
  - Endpoint: https://waterfall-dev-speech.cognitiveservices.azure.com/
  - ApiKey: configured
  - Region: eastus2
- Models:
  - None mapped from current model catalog.
- Service Configurations:
  - Service SpeechSynthesis / Mode cloud
    - ProviderSection: AzureSpeechService
    - Enabled: True; Default: False; Status: ready
  - Service SpeechTranscription / Mode azure
    - ProviderSection: AzureSpeechService
    - Enabled: True; Default: False; Status: ready

## GoogleGeminiApi

- Readiness: configured
- Updated UTC: 05/01/2026 18:05:33
- Service Provider IDs: SpeechTranscription.Google.SpeechToText, SpeechSynthesis.Google.TextToSpeech, ImageGeneration.Google.Imagen, Embeddings.Google.Embedding
- Configuration:
  - ApiKey: configured
- Models:
  - gemini-2.5-flash (provider=google-gemini-chat, active=True), runtimeProfileId=google_gemini_25_flash
  - gemini-2.5-pro (provider=google-gemini-chat, active=True), runtimeProfileId=google_gemini_25_pro
- Service Configurations:
  - Service Embeddings / Mode Embeddings.Google.Embedding
    - ProviderSection: GoogleGeminiApi
    - Enabled: False; Default: False; Status: blocked
    - ModelId: gemini-embedding-2
    - Blockers: MODEL_NOT_FOUND: catalog model 'gemini-embedding-2' was not found.
  - Service ImageGeneration / Mode google
    - ProviderSection: GoogleGeminiApi
    - Enabled: True; Default: False; Status: blocked
    - ModelId: gemini-2.5-flash-image
    - Blockers: MODEL_NOT_FOUND: catalog model 'gemini-2.5-flash-image' was not found.
  - Service SpeechSynthesis / Mode SpeechSynthesis.Google.TextToSpeech
    - ProviderSection: GoogleGeminiApi
    - Enabled: True; Default: False; Status: blocked
    - ModelId: gemini-3.1-flash-tts-preview
    - RequestPresetJson:
```json
{"VoiceName":"Kore"}
```
    - Blockers: MODEL_NOT_FOUND: catalog model 'gemini-3.1-flash-tts-preview' was not found.
  - Service SpeechTranscription / Mode google
    - ProviderSection: GoogleGeminiApi
    - Enabled: True; Default: False; Status: ready
    - ModelId: gemini-2.5-flash

## HuggingFace

- Readiness: configured
- Updated UTC: 05/25/2026 20:15:00
- Service Provider IDs: SpeechTranscription.HuggingFace.Inference, SpeechSynthesis.HuggingFace.Inference, ImageGeneration.HuggingFace.Inference, Embeddings.HuggingFace.Inference
- Configuration:
  - Token: configured
  - RouterBaseUrl: https://router.huggingface.co/v1
- Models:
  - zai-org/GLM-5.2 (provider=hf-inference-chat, active=True), runtimeProfileId=huggingface_chat_standard
- Service Configurations:
  - Service Embeddings / Mode Embeddings.HuggingFace.Inference
    - ProviderSection: HuggingFace
    - Enabled: True; Default: False; Status: ready
    - ModelId: microsoft/harrier-oss-v1-0.6b
  - Service ImageGeneration / Mode ImageGeneration.HuggingFace.Inference
    - ProviderSection: HuggingFace
    - Enabled: True; Default: False; Status: ready
    - ModelId: Tongyi-MAI/Z-Image-Turbo
    - RequestPresetJson:
```json
{"ImageToImageModelId":"black-forest-labs/FLUX.2-dev"}
```
  - Service SpeechTranscription / Mode SpeechTranscription.HuggingFace.Inference
    - ProviderSection: HuggingFace
    - Enabled: True; Default: False; Status: ready
    - ModelId: openai/whisper-large-v3
  - Service SpeechSynthesis / Mode SpeechSynthesis.HuggingFace.Inference
    - ProviderSection: HuggingFace
    - Enabled: True; Default: False; Status: ready
    - ModelId: ResembleAI/chatterbox

## LlamaCpp

- Readiness: configured
- Configuration endpoint: unavailable in current runtime
- Models:
  - gemma-4-26B-A4B-it-UD-Q5_K_XL (provider=llama-cpp, active=True), runtimeProfileId=gemma4
  - gemma-4-31B-it-Q5_K_M (provider=llama-cpp, active=True), runtimeProfileId=gemma4
  - qwen3.5-27b (provider=llama-cpp, active=True), runtimeProfileId=qwen3_5
  - qwen3.5-35b-a3b (provider=llama-cpp, active=True), runtimeProfileId=qwen3_5
  - Qwen3.5-9B-Q5_K_M (provider=llama-cpp, active=True), runtimeProfileId=qwen3_5
  - Qwen3.6-27B-UD-Q5_K_XL (provider=llama-cpp, active=True), runtimeProfileId=qwen3_5
  - Qwen3.6-35B-A3B-UD-Q5_K_M (provider=llama-cpp, active=True), runtimeProfileId=qwen3_5
- Service Configurations:
  - No service modes currently reference this provider section.

## LocalServiceHosts

- Readiness: not-applicable
- Configuration endpoint: unavailable in current runtime
- Models:
  - None mapped from current model catalog.
- Service Configurations:
  - Service DocumentIntelligence / Mode local
    - ProviderSection: LocalServiceHosts:DocumentIntelligenceBaseUrl
    - Enabled: True; Default: True; Status: ready
  - Service Embeddings / Mode local
    - ProviderSection: LocalServiceHosts:EmbeddingsBaseUrl
    - Enabled: True; Default: True; Status: ready
  - Service ImageGeneration / Mode local
    - ProviderSection: LocalServiceHosts:ImageGenerationBaseUrl
    - Enabled: True; Default: True; Status: ready
  - Service SpeechSynthesis / Mode local
    - ProviderSection: LocalServiceHosts:SpeechSynthesisBaseUrl
    - Enabled: True; Default: True; Status: ready
  - Service SpeechTranscription / Mode local
    - ProviderSection: LocalServiceHosts:SpeechTranscriptionBaseUrl
    - Enabled: True; Default: True; Status: ready
- Runtime Dependency Source (live):
  - LocalServiceHosts:SpeechTranscriptionBaseUrl = http://guideants-ai:80 (source=environment)
  - LocalServiceHosts:SpeechSynthesisBaseUrl = http://guideants-ai:80 (source=environment)
  - LocalServiceHosts:ImageGenerationBaseUrl = http://guideants-ai:80 (source=environment)
  - LocalServiceHosts:EmbeddingsBaseUrl = http://guideants-ai:80 (source=environment)
  - LocalServiceHosts:MediaBaseUrl = http://guideants-ai:80 (source=environment)
  - LocalServiceHosts:DocumentIntelligenceBaseUrl = http://docling-serve:5001 (source=environment)

## OpenAI

- Readiness: configured
- Updated UTC: 04/28/2026 16:18:19
- Service Provider IDs: SpeechTranscription.OpenAI.Audio, SpeechSynthesis.OpenAI.Tts, ImageGeneration.OpenAI.Images, Embeddings.OpenAI.Embedding
- Configuration:
  - ApiKey: configured
  - Endpoint: (empty)
- Models:
  - gpt-4.1-nano (provider=openai-chat, active=True), runtimeProfileId=openai_chat_standard
  - gpt-5.1-2025-11-13 (provider=openai-chat, active=True), runtimeProfileId=openai_chat_standard
- Service Configurations:
  - Service ImageGeneration / Mode ImageGeneration.OpenAI.Images
    - ProviderSection: OpenAI
    - Enabled: True; Default: False; Status: blocked
    - ModelId: gpt-image-1
    - Blockers: MODEL_NOT_FOUND: catalog model 'gpt-image-1' was not found.

## Not Configured (Excluded From Main Sections)

- OpenRouter: readiness=unconfigured, missingFields=ApiKey

## Section Endpoints Unavailable

- LlamaCpp
- LocalServiceHosts

## Chat Defaults Context

- DefaultModelId: claude-sonnet-4-5
- OverrideAllChatModels: True
- Temperature: 0.7
- TopP: 0.8
- ReasoningEffort: minimal

