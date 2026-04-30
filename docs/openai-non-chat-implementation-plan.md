# OpenAI Non-Chat Implementation Plan (STT, TTS, Images, Embeddings)

## Plan goals

Add OpenAI as a cloud provider to four existing routed services:

1. **SpeechTranscription** — OpenAI `/audio/transcriptions` (Whisper)
2. **SpeechSynthesis** — OpenAI `/audio/speech` (TTS)
3. **ImageGeneration** — OpenAI `/images/generations` and `/images/edits`
4. **Embeddings** — OpenAI `/embeddings`

This plan follows the same integration pattern used by every existing cloud provider (Google Gemini, Hugging Face, OpenRouter). No new routed services, no architectural changes, no new settings sections. OpenAI joins the existing provider matrix harmoniously.

## Target outcomes

- OpenAI SpeechTranscription is fully implemented and configurable end-to-end.
- OpenAI SpeechSynthesis is fully implemented and configurable end-to-end.
- OpenAI ImageGeneration (generate + edit) is fully implemented and configurable end-to-end.
- OpenAI Embeddings is fully implemented and configurable end-to-end.
- The implementation ships with explicit server and client test coverage for all new OpenAI paths.

## Scope

### In scope

- OpenAI provider branches in SpeechTranscription, SpeechSynthesis, ImageGeneration, and Embeddings.
- All seven registration layers per service (provider ID, contract, metadata, readiness, runtime, bootstrap, client).
- Add AI Services Wizard OpenAI path.
- Test coverage for new paths.

### Out of scope

- Chat-provider architecture changes.
- New routed services (no SpeechRecognition or other new services).
- WebSocket/Realtime API integration (fundamentally different architecture; separate plan).
- Unrelated settings/database refactors.

## Baseline (repo state)

- OpenAI connection section already exists (`OpenAI:ApiKey`, optional `OpenAI:Endpoint`) in `SettingsSectionRegistry` and `ProviderSectionRequirements`.
- Non-chat routed services currently have five providers each: Azure, Local, GoogleGemini, HuggingFace, OpenRouter.
- OpenAI non-chat provider IDs, contracts, metadata, readiness rules, and runtime branches are all missing.
- Add AI Services Wizard supports only Foundry and Gemini.

## OpenAI capability mapping

### SpeechTranscription (file/batch)

- Endpoint: `POST /audio/transcriptions`
- Input: multipart audio file + model + optional params (language, response_format, temperature).
- Output: transcript text (or verbose JSON with segments/timestamps).
- Models: `whisper-1`, `gpt-4o-transcribe`, `gpt-4o-mini-transcribe`.

### SpeechSynthesis (TTS)

- Endpoint: `POST /audio/speech`
- Input: JSON body with model, input text, voice, optional response_format and speed.
- Output: audio stream (mp3, opus, aac, flac, wav, pcm).
- Models: `tts-1`, `tts-1-hd`, `gpt-4o-mini-tts`.
- Voices: `alloy`, `ash`, `ballad`, `coral`, `echo`, `fable`, `nova`, `onyx`, `sage`, `shimmer`.

### ImageGeneration

- Endpoint: `POST /images/generations`, `POST /images/edits`
- Input: prompt, model, size, quality, n, optional response_format.
- Output: image URL or base64.
- Models: `dall-e-3`, `gpt-image-1`.
- Note: `gpt-image-1` supports edits; `dall-e-3` does not. Size options differ by model.

### Embeddings

- Endpoint: `POST /embeddings`
- Input: model, input text(s), optional dimensions, optional encoding_format.
- Output: embedding vectors.
- Models: `text-embedding-3-small`, `text-embedding-3-large`, `text-embedding-ada-002`.
- Note: `dimensions` parameter is supported by `text-embedding-3-*` models only, not `ada-002`. Dimension mismatches between index-time and query-time produce incorrect results silently.

## Provider IDs (following existing naming convention)

The variant segment describes the mechanism/protocol, matching existing conventions:

| Service | Provider ID | Convention reference |
|---|---|---|
| SpeechTranscription | `SpeechTranscription.OpenAI.Audio` | matches `SpeechTranscription.OpenRouter.Audio` |
| SpeechSynthesis | `SpeechSynthesis.OpenAI.Tts` | matches `SpeechSynthesis.OpenRouter.Tts` |
| ImageGeneration | `ImageGeneration.OpenAI.Images` | matches `ImageGeneration.AzureOpenAI.Images` |
| Embeddings | `Embeddings.OpenAI.Embedding` | matches `Embeddings.AzureOpenAI.Embedding` |

All four use `ProviderSectionKey: "OpenAI"` — the existing `OpenAI` connection section already requires `ApiKey`.

## Server implementation plan

### 1) Provider ID constants

#### File

- `src/server/GuideAntsApi/Options/AzureDocumentIntelligenceOptions.cs` (specifically the `ServiceProviderIds` static class)

#### Changes

Add four constants to `ServiceProviderIds`:

```csharp
public const string SpeechTranscriptionOpenAiAudio = "SpeechTranscription.OpenAI.Audio";
public const string SpeechSynthesisOpenAiTts = "SpeechSynthesis.OpenAI.Tts";
public const string ImageGenerationOpenAiImages = "ImageGeneration.OpenAI.Images";
public const string EmbeddingsOpenAiEmbedding = "Embeddings.OpenAI.Embedding";
```

### 2) Service contracts (provider registrations)

#### File

- `src/server/GuideAntsApi/Settings/ApplicationSettingsService.Contracts.cs`

#### Changes

Add one `ProviderContract` entry to each of the four existing `ServiceContract` blocks, following the exact pattern of the Google/HF/OpenRouter contracts:

**SpeechTranscription** — add after the OpenRouter entry:

```csharp
new ProviderContract(
    ProviderId: ServiceProviderIds.SpeechTranscriptionOpenAiAudio,
    ProviderKind: "Cloud",
    ProviderSectionKey: "OpenAI",
    ProviderSettingsSection: "OpenAI",
    RequiredSectionFields:
    [
        new SectionFieldRequirement("OpenAI", "ApiKey")
    ],
    RequiredRuntimeKeys: [])
```

**SpeechSynthesis** — same pattern.

**ImageGeneration** — same pattern.

**Embeddings** — same pattern.

Add `"OpenAI:ApiKey"` to the `ErrorKeys` list of each of the four services.

### 3) Service editor metadata

#### File

- `src/server/GuideAntsApi/Settings/ServiceEditorMetadataProvider.cs`

#### Changes

Add metadata entries for each new provider ID following the existing per-service field patterns:

**SpeechTranscription.OpenAI.Audio:**

```csharp
[ServiceProviderIds.SpeechTranscriptionOpenAiAudio] =
[
    Field("ModelId", "text", true, operative: true),
    Field("TimeoutSeconds", "int", true, operative: true),
]
```

**SpeechSynthesis.OpenAI.Tts:**

```csharp
[ServiceProviderIds.SpeechSynthesisOpenAiTts] =
[
    Field("ModelId", "text", true, operative: true),
    Field("VoiceName", "text", true, operative: true),
    Field("TimeoutSeconds", "int", true, operative: true),
]
```

**ImageGeneration.OpenAI.Images:**

```csharp
[ServiceProviderIds.ImageGenerationOpenAiImages] =
[
    Field("ModelId", "text", true, operative: true),
    Field("TimeoutSeconds", "int", true, operative: true),
]
```

**Embeddings.OpenAI.Embedding:**

```csharp
[ServiceProviderIds.EmbeddingsOpenAiEmbedding] =
[
    Field("ModelId", "text", true, operative: true),
    Field("Dimensions", "int", false, operative: true),
    Field("TimeoutSeconds", "int", true, operative: true),
]
```

### 4) Readiness rules

#### File

- `src/server/GuideAntsApi/Services/Routing/RoutingReadinessService.cs`

#### Changes

**a) `RequiresExplicitModelId`** — Add an `"OpenAI"` branch (all four services require a model ID):

```csharp
if (string.Equals(providerSection, "OpenAI", StringComparison.OrdinalIgnoreCase))
{
    return string.Equals(service, RoutedServiceNames.Embeddings, StringComparison.OrdinalIgnoreCase)
        || string.Equals(service, RoutedServiceNames.ImageGeneration, StringComparison.OrdinalIgnoreCase)
        || string.Equals(service, RoutedServiceNames.SpeechTranscription, StringComparison.OrdinalIgnoreCase)
        || string.Equals(service, RoutedServiceNames.SpeechSynthesis, StringComparison.OrdinalIgnoreCase);
}
```

**b) `ModelCapabilityBlockers`** — Add an `"OpenAI"` dispatch:

```csharp
if (string.Equals(providerSection, "OpenAI", StringComparison.OrdinalIgnoreCase))
{
    return OpenAiCapabilityBlockers(service, modelId, mode.RequestPresetJson);
}
```

**c) New method `OpenAiCapabilityBlockers`** — Validate model IDs per service using heuristic or AllowedModels preset, following the same `IsAllowedByConfigOrHeuristic` pattern as HuggingFace/OpenRouter:

- **Embeddings**: heuristic matches `embed` prefix.
- **ImageGeneration**: heuristic matches `dall-e`, `gpt-image`.
- **SpeechTranscription**: heuristic matches `whisper`, `transcribe`.
- **SpeechSynthesis**: heuristic matches `tts`.

**d) `AdditionalModeFieldBlockers`** — Add a check for `SpeechSynthesis` + `OpenAI` requiring `VoiceName` in `RequestPresetJson` (parallel to the existing Google Gemini VoiceName requirement).

### 5) SpeechTranscription runtime

#### File

- `src/server/GuideAntsApi/Services/Components/SpeechTranscriptionService.cs`

#### Changes

- Add constant: `private const string OpenAiProviderSection = "OpenAI";`
- Add provider branch in the `TranscribeDirectAudioWithDurationAsync` switch:
  `OpenAiProviderSection => await TranscribeViaOpenAiAsync(...)`
- Implement `TranscribeViaOpenAiAsync`:
  - Read `OpenAI:ApiKey` and optional `OpenAI:Endpoint` (defaults to `https://api.openai.com/v1`).
  - Require `mode.ModelId` (e.g. `whisper-1`).
  - Build multipart POST to `{endpoint}/audio/transcriptions` with `Authorization: Bearer {apiKey}`.
  - Form fields: `file` (audio stream), `model`, optional `language` from `RequestPresetJson`.
  - Parse JSON response for transcript text.
  - Follow existing logging pattern (`asr_api_request_start` / `asr_api_request_success`).
- Update the `_ => throw RoutingException` default arm error message to include `OpenAiProviderSection`.

#### Adapter check

- Review `SpeechTranscriptionAdapter.cs` for any adapter-level changes needed. If the adapter simply delegates to `ISpeechTranscriptionService`, no changes are required.

### 6) SpeechSynthesis runtime

#### File

- `src/server/GuideAntsApi/Services/Components/SpeechSynthesisService.cs`

#### Changes

- Add constant: `private const string OpenAiProviderSection = "OpenAI";`
- Add provider branch in the `SynthesizeToWavAsync` switch:
  `OpenAiProviderSection => await SynthesizeViaOpenAiAsync(...)`
- Implement `SynthesizeViaOpenAiAsync`:
  - Read `OpenAI:ApiKey` and optional `OpenAI:Endpoint`.
  - Require `mode.ModelId` (e.g. `tts-1`, `tts-1-hd`, `gpt-4o-mini-tts`).
  - Read `VoiceName` from `mode.RequestPresetJson` (required).
  - POST JSON to `{endpoint}/audio/speech` with `Authorization: Bearer {apiKey}`.
  - Request body: `{ model, input: <plain text from SSML>, voice, response_format: "wav" }`.
  - Stream response bytes to output file.
  - Follow existing logging pattern (`tts_api_request_start` / `tts_api_request_success`).
- Add to `ResolveSpeechSynthesisProviderId` switch.
- Update default arm error message to include `OpenAiProviderSection`.

### 7) ImageGeneration runtime

#### File

- `src/server/GuideAntsApi/Services/NotebookImageService.cs`

#### Changes

- Add constants:
  ```csharp
  private const string ImageProviderOpenAi = ServiceProviderIds.ImageGenerationOpenAiImages;
  private const string OpenAiProviderSection = "OpenAI";
  ```
- Add provider branch in the generation and edit routing switches.
- Implement OpenAI generation:
  - POST JSON to `{endpoint}/images/generations` with Bearer auth.
  - Request body: `{ model, prompt, size, quality, n, response_format }`.
  - Handle model-specific size constraints (`dall-e-3` vs `gpt-image-1`).
- Implement OpenAI edit (for models that support it):
  - POST multipart to `{endpoint}/images/edits` with Bearer auth.
  - Include image, optional mask, prompt, model.
- Add to `ResolveImageProviderId` switch.
- Update default arm error message.
- Follow existing usage tracking and notebook output patterns.

### 8) Embeddings runtime

#### Files

- Add `src/server/GuideAntsApi.BackgroundJobs/Services/Embeddings/OpenAiEmbeddingService.cs`
- Update `src/server/GuideAntsApi.BackgroundJobs/Services/Embeddings/ProviderRoutedEmbeddingService.cs`
- Update `src/server/GuideAntsApi.BackgroundJobs/ServiceCollectionExtensions.cs`

#### Changes

**New `OpenAiEmbeddingService`** (following the pattern of `AzureOpenAiEmbeddingService`, `GoogleGeminiEmbeddingService`, etc.):

- Constructor: `HttpClient`, `IConfiguration`, `ILogger`.
- Method: `GetEmbeddingsAsync(texts, modelId, requestPresetJson, cancellationToken)`.
- Read `OpenAI:ApiKey` and optional `OpenAI:Endpoint` from configuration.
- POST JSON to `{endpoint}/embeddings` with Bearer auth.
- Request body: `{ model, input: [...texts] }` plus optional `dimensions` from `requestPresetJson`.
- Parse response and return `float[][]`.

**`ProviderRoutedEmbeddingService`:**

- Add constant: `private const string OpenAiProviderSection = "OpenAI";`
- Inject `OpenAiEmbeddingService`.
- Add switch arm:
  ```csharp
  OpenAiProviderSection => await _openAiEmbeddingService
      .GetEmbeddingsAsync(texts, RequireModelId(mode), mode.RequestPresetJson, cancellationToken)
      .ConfigureAwait(false),
  ```
- Update default arm error message.

**`ServiceCollectionExtensions`:**

- Register `OpenAiEmbeddingService` in DI.

### 9) Bootstrap profile

#### File

- `src/server/GuideAntsApi/Resources/bootstrap/provider-stack-profiles/openai.json`

#### Changes

Populate the currently-empty `serviceDefaults` array with entries for all four services, following the pattern used by other profiles:

```json
"serviceDefaults": [
  {
    "serviceId": "SpeechTranscription",
    "providerId": "SpeechTranscription.OpenAI.Audio",
    "modelId": "whisper-1",
    "timeoutSeconds": 300
  },
  {
    "serviceId": "SpeechSynthesis",
    "providerId": "SpeechSynthesis.OpenAI.Tts",
    "modelId": "tts-1",
    "voiceName": "alloy",
    "timeoutSeconds": 300
  },
  {
    "serviceId": "ImageGeneration",
    "providerId": "ImageGeneration.OpenAI.Images",
    "modelId": "gpt-image-1",
    "timeoutSeconds": 600
  },
  {
    "serviceId": "Embeddings",
    "providerId": "Embeddings.OpenAI.Embedding",
    "modelId": "text-embedding-3-small",
    "timeoutSeconds": 300
  }
]
```

## Client-side required changes

### 1) Display labels

#### Files

- `src/client/src/pages/settings/constants/displayLabels.ts`
- `src/client/src/pages/settings/constants/displayLabels.test.ts`

#### Changes

**`SERVICE_PROVIDER_LABELS`** — Add four entries:

```typescript
'SpeechTranscription.OpenAI.Audio': 'OpenAI Whisper',
'SpeechSynthesis.OpenAI.Tts': 'OpenAI TTS',
'ImageGeneration.OpenAI.Images': 'OpenAI Images',
'Embeddings.OpenAI.Embedding': 'OpenAI Embeddings',
```

**`PROVIDER_FIELD_LABEL_OVERRIDES`** — Add four entries:

```typescript
'SpeechTranscription.OpenAI.Audio': { ModelId: 'Transcription Model ID' },
'SpeechSynthesis.OpenAI.Tts': { ModelId: 'TTS Model ID' },
'ImageGeneration.OpenAI.Images': { ModelId: 'Image Model ID' },
'Embeddings.OpenAI.Embedding': { ModelId: 'Embedding Model ID' },
```

**`COMMON_FIELD_LABELS`** — Add if not already present:

```typescript
Dimensions: 'Dimensions',
```

**`COMMON_FIELD_HELP_TEXT`** — Add:

```typescript
Dimensions: 'Output embedding dimensions. Supported by text-embedding-3-* models only.',
```

### 2) Service editors

Each editor already has a switch/branch per provider. Add the OpenAI case to each:

#### SpeechTranscription editor

- File: `src/client/src/pages/settings/editors/speech-transcription/SpeechTranscriptionEditor.tsx`
- Add OpenAI behavior case (fields: ModelId, TimeoutSeconds).

#### SpeechSynthesis editor

- File: `src/client/src/pages/settings/editors/speech-synthesis/SpeechSynthesisEditor.tsx`
- Add OpenAI behavior case (fields: ModelId, VoiceName, TimeoutSeconds).

#### ImageGeneration editor

- File: `src/client/src/pages/settings/editors/image-generation/ImageGenerationEditor.tsx`
- Add OpenAI behavior case (fields: ModelId, TimeoutSeconds).

#### Embeddings editor

- File: `src/client/src/pages/settings/editors/embeddings/EmbeddingsEditor.tsx`
- Add OpenAI behavior case (fields: ModelId, Dimensions, TimeoutSeconds).

### 3) Shared service editor plumbing

#### Files

- `src/client/src/pages/settings/editors/common/ServiceEditorBase.tsx`
- `src/client/src/pages/settings/state/useServiceEditorController.ts`
- `src/client/src/pages/settings/state/serviceEditorValidation.ts`
- `src/client/src/pages/settings/state/__tests__/serviceEditorValidation.test.ts`

#### Changes

- Verify `ServiceEditorBase` and controller render the new OpenAI provider IDs without modification (they should, since they're data-driven from metadata).
- Add validation rules for the `Dimensions` field (optional positive integer) if not already covered by generic numeric validation.

### 4) Add AI Services Wizard (OpenAI path)

#### Files

- `src/client/src/components/home/addAiServicesWizard/types.ts`
- `src/client/src/components/home/addAiServicesWizard/constants.ts`
- `src/client/src/components/home/addAiServicesWizard/utils.ts`
- `src/client/src/components/home/addAiServicesWizard/steps/ProviderStep.tsx`
- `src/client/src/components/home/AddAiServicesWizard.tsx`
- Add OpenAI-specific step components as needed.

#### Changes

**`types.ts`:**

- Add `'openai'` to `AddAiServicesWizardProvider` union type.
- Add `OpenAiCoreConnectionFormState` interface (fields: `apiKey`, `endpoint`, `apiKeyHasStoredValue`).
- Add `OpenAiOptionalServicesFormState` interface (following the Gemini pattern):
  - `enableSpeechTranscription`, `speechTranscriptionModelId`, `speechTranscriptionTimeoutSeconds`
  - `enableSpeechSynthesis`, `speechSynthesisModelId`, `speechSynthesisVoiceName`, `speechSynthesisTimeoutSeconds`
  - `enableImages`, `imagesModelId`, `imagesTimeoutSeconds`
  - `enableEmbeddings`, `embeddingsModelId`, `embeddingsDimensions`, `embeddingsTimeoutSeconds`
- Add `OpenAiOptionalServiceKey` type: `'Embeddings' | 'ImageGeneration' | 'SpeechTranscription' | 'SpeechSynthesis'`.

**`constants.ts`:**

- Add `OPENAI_CORE_SECTION = 'OpenAI'`.
- Add provider option to `WIZARD_PROVIDER_OPTIONS`:
  ```typescript
  {
    id: 'openai',
    label: 'OpenAI',
    description: 'Configure OpenAI API key, chat models, and optional services (STT, TTS, images, embeddings).',
  }
  ```
- Add `OPENAI_SERVICE_PROVIDER_IDS`:
  ```typescript
  export const OPENAI_SERVICE_PROVIDER_IDS: Readonly<Record<
    'Embeddings' | 'ImageGeneration' | 'SpeechTranscription' | 'SpeechSynthesis',
    string
  >> = {
    Embeddings: 'Embeddings.OpenAI.Embedding',
    ImageGeneration: 'ImageGeneration.OpenAI.Images',
    SpeechTranscription: 'SpeechTranscription.OpenAI.Audio',
    SpeechSynthesis: 'SpeechSynthesis.OpenAI.Tts',
  } as const;
  ```
- Add `OPENAI_OPTIONAL_SERVICE_DEFAULTS`:
  ```typescript
  export const OPENAI_OPTIONAL_SERVICE_DEFAULTS = {
    speechTranscriptionModelId: 'whisper-1',
    speechTranscriptionTimeoutSeconds: '300',
    speechSynthesisModelId: 'tts-1',
    speechSynthesisVoiceName: 'alloy',
    speechSynthesisTimeoutSeconds: '300',
    imagesModelId: 'gpt-image-1',
    imagesTimeoutSeconds: '600',
    embeddingsModelId: 'text-embedding-3-small',
    embeddingsTimeoutSeconds: '300',
  } as const;
  ```
- Add OpenAI model provider mapping constants (chat models use `openai-chat` / `openai-responses`).

**Wizard components:**

- Add OpenAI connection step (ApiKey + optional Endpoint).
- Add OpenAI models step (chat model registration with provider selection: Completions vs Responses).
- Add OpenAI optional services step (four toggles with model/config fields per service).
- Add finish step summary for OpenAI path.
- Follow the Gemini wizard flow as the structural template.

### 5) Connection overview checks

#### Files

- `src/client/src/pages/settings/components/ConnectionsTab.tsx`
- `src/client/src/pages/settings/constants/connectionSections.ts`
- `src/client/src/pages/settings/utils.ts`

#### Changes

- Verify `OpenAI` connection section already appears in the connections taxonomy (it should, since `SettingsSectionRegistry` already defines it and `CONNECTION_SECTION_LABELS` will use `humanizePresentationKey` for unlisted sections).
- Optionally add explicit `CONNECTION_SECTION_LABELS` entry: `OpenAI: 'OpenAI'`.
- Verify readiness chips render correctly for new OpenAI provider modes.

## Server tests required

### Settings/routing contract tests

- `src/server/GuideAntsApi.Tests/Settings/ApplicationSettingsServiceSchemaAndReadinessTests.cs`
  - Verify schema includes all four new provider IDs.
  - Verify readiness for each new provider contract.
- `src/server/GuideAntsApi.Tests/Settings/ServiceEditorMetadataProviderTests.cs`
  - Verify metadata returns correct fields for each new provider ID.
- `src/server/GuideAntsApi.Tests/Settings/ServiceEditorUpdateValidationTests.cs`
  - Verify validation accepts/rejects mode updates for new providers.
- `src/server/GuideAntsApi.Tests/Services/Routing/RoutingReadinessServiceTests.cs`
  - Verify `RequiresExplicitModelId` returns `true` for OpenAI on all four services.
  - Verify `OpenAiCapabilityBlockers` accepts valid models and rejects invalid models.
  - Verify VoiceName requirement for SpeechSynthesis + OpenAI.

### Runtime tests

- **SpeechTranscription:**
  - `src/server/GuideAntsApi.Tests/Services/SpeechTranscriptionServiceTests.cs`
  - Add tests for OpenAI provider branch (successful transcription, missing API key, missing model ID, HTTP errors).
- **SpeechSynthesis:**
  - Add tests to `src/server/GuideAntsApi.Tests/Services/` (parallel to SpeechTranscription tests).
  - Cover OpenAI provider branch (successful synthesis, missing VoiceName, missing API key, HTTP errors).
- **ImageGeneration:**
  - `src/server/GuideAntsApi.Tests/Services/NotebookImageServiceTests.cs`
  - Add tests for OpenAI generation and edit branches.
  - Cover model-specific behavior (dall-e-3 no edit, gpt-image-1 with edit).
- **Embeddings:**
  - Add `src/server/GuideAntsApi.Tests/BackgroundJobs/OpenAiEmbeddingServiceTests.cs`
  - Cover successful embedding, dimensions parameter handling, missing API key.

## Client tests required

- `src/client/src/pages/settings/constants/displayLabels.test.ts`
  - Verify all four new provider labels resolve correctly.
- `src/client/src/pages/settings/state/__tests__/serviceEditorValidation.test.ts`
  - Verify validation for Dimensions field.
- `src/client/src/components/home/addAiServicesWizard/__tests__/utils.test.ts`
  - Verify OpenAI wizard utility functions.
- Add wizard integration tests for the OpenAI flow if project patterns require it.

## Delivery sequence

1. Add provider ID constants to `ServiceProviderIds`.
2. Add provider contracts to `ApplicationSettingsService.Contracts.cs` for all four services.
3. Add metadata to `ServiceEditorMetadataProvider` for all four providers.
4. Add readiness rules (`RequiresExplicitModelId`, `OpenAiCapabilityBlockers`, VoiceName blocker).
5. Implement OpenAI Embeddings runtime (lowest risk, easiest to validate).
6. Implement OpenAI SpeechTranscription runtime.
7. Implement OpenAI SpeechSynthesis runtime.
8. Implement OpenAI ImageGeneration runtime.
9. Add `openai.json` bootstrap profile service defaults.
10. Update client display labels and service editor behavior cases.
11. Extend Add AI Services Wizard with OpenAI provider path.
12. Run full test suite and regression.

## Acceptance criteria

1. All four OpenAI provider IDs are registered in `ServiceProviderIds`, `ApplicationSettingsService.Contracts`, `ServiceEditorMetadataProvider`, and `RoutingReadinessService`.
2. OpenAI SpeechTranscription route works end-to-end (audio file in, transcript out via `/audio/transcriptions`).
3. OpenAI SpeechSynthesis route works end-to-end (text in, audio file out via `/audio/speech`).
4. OpenAI ImageGeneration route works end-to-end (prompt in, image out via `/images/generations`; edit via `/images/edits` for supporting models).
5. OpenAI Embeddings route works end-to-end (text in, vectors out via `/embeddings`).
6. Wizard can configure OpenAI core connection + optional services for all four capabilities.
7. Readiness correctly blocks modes with missing `ModelId`, missing `VoiceName` (TTS), and unrecognized model IDs.
8. Tests pass for contracts, routing, runtime, and client behavior updates.
9. No regressions in existing Azure/Local/Google/HuggingFace/OpenRouter paths.
