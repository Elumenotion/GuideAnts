# Full Provider Test Plan

Last updated: 2026-05-25

## 1. Purpose

Run a deterministic, Playwright-only qualification pass for provider configuration and runtime behavior across:

1. Setup Wizard
2. Chat UI + notebook header toolbar

Coverage uses `docs/provider-model-service-config-2026-05-25.md` as the source of truth for provider/model/service values, with explicit clarifications from this thread.

## 2. Active Scope (Current Phase)

In scope now:

1. Setup Wizard flows
2. Chat model selection/execution via header toolbar
3. Chat-driven image and TTS service exercise
4. ASR feasibility check in current headless environment (record limitation if direct execution is not possible)

Deferred to later phase:

1. Main Settings UI expansion (deep coverage pass)

## 3. Hard Rules (Non-Negotiable)

1. Test execution uses UI automation only via `playwright-cli`.
2. Direct API calls are forbidden for execution.
3. Direct DB reads/writes are forbidden for execution.
4. Local AI providers/services are excluded for this plan.
5. Use baseline DB state prepared for repeated restore.
6. If blocked by a real defect, stop and report with evidence.
7. No model guessing and no substitutions. Use exact model IDs and values defined here.
8. The wizard `Finish` button behavior is two-step by design:
- First click on provider flow advances to summary step.
- Second click on summary dismisses dialog.
- Remaining open after first click is expected, not a defect.

## 4. Baseline Database

- Server: `localhost,1434`
- Database: `guideants-provider-tests`
- Baseline backup: `/var/opt/mssql/data/guideants-provider-tests-baseline.bak`
- Backup: full `COPY_ONLY` with `INIT`

Restore command set before a full rerun:

```sql
ALTER DATABASE [guideants-provider-tests] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
RESTORE DATABASE [guideants-provider-tests]
  FROM DISK = N'/var/opt/mssql/data/guideants-provider-tests-baseline.bak'
  WITH REPLACE, RECOVERY, CHECKSUM;
ALTER DATABASE [guideants-provider-tests] SET MULTI_USER;
```

## 5. Runtime Setup Requirement

Before UI execution:

1. Launch .NET API serving UI at `http://localhost:5106/`.
2. Capture logs:
- `output/logs/guideants-api-stdout-rerun5.log`
- `output/logs/guideants-api-stderr-rerun5.log`
3. Any unexpected exception in API logs during execution is a defect.

### 5.1 Run Reset Checklist (Start-From-Top)

Complete this checklist before each full rerun:

1. Restore baseline DB (Section 4 SQL script).
2. Start API at `http://localhost:5106/`.
3. Reset/rotate log files for current rerun label.
4. Close all Playwright browser sessions (`playwright-cli close-all`, `playwright-cli kill-all`).
5. Open fresh browser session and navigate to Home.
6. Confirm wizard provider options show Foundry, Google Gemini, OpenAI, Hugging Face, OpenRouter.
7. Use the same notebook target for chat/header phase to keep artifacts comparable.

## 6. Provider Scope Clarification (From Thread)

In-scope providers for this phase:

1. Microsoft Foundry
2. Google Gemini
3. OpenAI
4. Hugging Face
5. OpenRouter

Out of scope for this phase:

1. Anthropic (main settings UI phase)
2. Local AI providers/services

## 7. Exact Chat Models to Exercise (No Guessing)

### 7.1 Foundry (Exact mapping required)

1. `gpt-4.1-mini` -> `Completions`
2. `gpt-4o` -> `Completions`
3. `gpt-4o-mini` -> `Completions`
4. `gpt-5.2-codex` -> `Responses`

Clarification: `gpt-5.2-codex` is Responses; the other Foundry models in this pass are Completions.

### 7.2 Google Gemini

1. `gemini-2.5-flash`
2. `gemini-2.5-pro`

### 7.3 OpenAI

1. `gpt-4.1-nano`
2. `gpt-5.1-2025-11-13`

### 7.4 Hugging Face

1. `zai-org/GLM-5.2`

### 7.5 OpenRouter

1. `minimax/minimax-m3`

## 8. Non-Chat Service Targets and Deterministic Values

Any in-scope required service showing `blocked` (including `MODEL_NOT_FOUND`) is a defect for this baseline.

### 8.1 Foundry

1. Embeddings
- Provider: `Embeddings.AzureOpenAI.Embedding`
- ModelId: `text-embedding-3-small`
- Expected: `ready`

2. Image Generation
- Provider: `ImageGeneration.AzureOpenAI.Images`
- ModelId: `FLUX.1-Kontext-pro`
- RequestPresetJson must include `EditModelDeployment=FLUX.1-Kontext-pro`
- Expected: `ready`

3. Speech Transcription
- Provider: `SpeechTranscription.AzureSpeech.Batch`
- Mode: `azure`
- Expected: `ready`

4. Speech Synthesis
- Provider: `SpeechSynthesis.AzureSpeech.Ssml`
- Expected: `ready`

5. Document Intelligence
- Provider: `DocumentIntelligence.Azure.DocumentIntelligence`
- ApiVersion: `2024-11-30`
- MaxRetries: `3`
- Expected: `ready`

### 8.2 Google Gemini

1. Embeddings
- Provider: `Embeddings.Google.Embedding`
- ModelId: `gemini-embedding-2`
- Expected: `ready`

2. Image Generation
- Provider: `ImageGeneration.Google.Imagen`
- ModelId: `gemini-2.5-flash-image`
- Expected: `ready`

3. Speech Transcription
- Provider: `SpeechTranscription.Google.SpeechToText`
- ModelId: `gemini-2.5-flash`
- Expected: `ready`

4. Speech Synthesis
- Provider: `SpeechSynthesis.Google.TextToSpeech`
- ModelId: `gemini-3.1-flash-tts-preview`
- VoiceName: `Kore`
- Expected: `ready`

### 8.3 OpenAI

1. Embeddings
- Provider: `Embeddings.OpenAI.Embedding`
- ModelId: `text-embedding-3-small`
- Expected: `ready`

2. Image Generation
- Provider: `ImageGeneration.OpenAI.Images`
- ModelId: `gpt-image-1`
- Expected: `ready`

3. Speech Transcription
- Provider: `SpeechTranscription.OpenAI.Audio`
- ModelId: `whisper-1`
- Expected: `ready`

4. Speech Synthesis
- Provider: `SpeechSynthesis.OpenAI.Tts`
- ModelId: `tts-1`
- VoiceName: `alloy`
- Expected: `ready`

### 8.4 Hugging Face

1. Embeddings
- Provider: `Embeddings.HuggingFace.Inference`
- ModelId: `microsoft/harrier-oss-v1-0.6b`
- Expected: `ready`

2. Image Generation
- Provider: `ImageGeneration.HuggingFace.Inference`
- TextToImageModelId: `Tongyi-MAI/Z-Image-Turbo`
- ImageToImageModelId: `black-forest-labs/FLUX.2-dev`
- Expected: `ready`

3. Speech Transcription
- Provider: `SpeechTranscription.HuggingFace.Inference`
- ModelId: `openai/whisper-large-v3`
- Expected: `ready`

4. Speech Synthesis
- Provider: `SpeechSynthesis.HuggingFace.Inference`
- ModelId: `ResembleAI/chatterbox`
- Expected: `ready`

### 8.5 OpenRouter

1. Embeddings
- Provider: `Embeddings.OpenRouter.Embeddings`
- ModelId: `nvidia/llama-nemotron-embed-vl-1b-v2:free`
- Expected: `ready`

2. Image Generation
- Provider: `ImageGeneration.OpenRouter.Image`
- ModelId: `recraft/recraft-v4`
- Expected: `ready`
- Note: OpenRouter uses a single image `ModelId` for both text-to-image and image edit. Unlike HF, no TextToImageModelId/ImageToImageModelId split.

3. Speech Transcription
- Provider: `SpeechTranscription.OpenRouter.Audio`
- ModelId: `nvidia/parakeet-tdt-0.6b-v3`
- Expected: `ready`

4. Speech Synthesis
- Provider: `SpeechSynthesis.OpenRouter.Tts`
- ModelId: `hexgrad/kokoro-82m`
- Expected: `ready`

## 9. Wizard Procedure (Detailed)

Run order:

1. Foundry
2. Google Gemini
3. OpenAI
4. Hugging Face
5. OpenRouter

For each provider:

1. Open Home (`/`) and launch `Setup Wizard`.
2. Select provider.
3. Connection step:
- Keep stored secrets (`********`) unless correction is required.
- Do not rotate credentials during this test unless explicitly part of defect recovery.
4. Models step:
- Confirm required models exist (already configured or newly added).
- For Foundry, enforce exact provider mapping from Section 7.1.
5. Optional services step:
- Keep `Configure now` enabled for relevant services.
- Validate required fields populated with deterministic values from Section 8.
6. Finish behavior:
- Click `Finish` to reach summary page.
- Click `Finish` again to close wizard.
7. Re-open wizard after each provider pass to verify persisted state where necessary.
8. Capture provider evidence screenshot at summary step.

### 9.1 Clean Retry Definition

When the plan says "retry once," a clean retry means:

1. Do not change expected model IDs, provider mappings, or service values.
2. Close and reopen the wizard (or reload page once) and repeat the same step exactly.
3. Keep stored secrets unchanged (`********`) unless field is actually empty.
4. If the same failure repeats, classify as defect and stop.

## 10. Chat + Header Toolbar Procedure (Detailed)

### 10.0 Provider Switching in Header Panels (Exact UI Pattern)

For each toolbar panel (`Chat`, `Image generation`, `Speech synthesis (TTS)`, `Speech transcription (ASR)`):

1. Click panel button in notebook header.
2. If collapsed, expand panel.
3. Use provider/model selector within the panel to switch to target provider/model.
4. Run the deterministic prompt/action for that provider.
5. Capture evidence before switching to next provider.

### 10.1 Chat model loop

1. Open notebook conversation.
2. Open header `Chat` toolbar panel.
3. Enable `Override all chat models`.
4. For each model in Section 7:
- Select model.
- Send deterministic prompt:
  - `Provider/model smoke test: reply with model id and a one-line acknowledgement.`
- Record success/failure and capture screenshot:
  - `output/playwright/full-provider-chat-<modelId>.png`

Per-model PASS criteria:

1. Selected model remains selected at send time.
2. Assistant response completes without UI runtime error banner.
3. Response includes a model-identifying acknowledgement (exact model ID string preferred).

### 10.2 Image service loop (chat-driven)

For each in-scope provider image path:

1. Use Creative Guide prompt to generate:
- `Using the current image service provider, generate an image of a red bird perched on a branch.`
2. Follow-up edit prompt:
- `Now edit that image so the bird is blue, preserving composition and branch.`
3. Record pass if both generation and edit succeed.

### 10.3 TTS loop (chat-driven)

For each in-scope provider TTS path:

1. Use deterministic prompt:
- `Using the current TTS service provider, synthesize this sentence and attach a WAV file: The quick brown fox jumps over the lazy dog.`
2. Record pass if audio artifact is produced.

### 10.4 ASR handling

1. Attempt direct ASR exercise through available UI controls.
2. Use deterministic utterance text when ASR supports typed/source prompt configuration:
- `Pack my box with five dozen liquor jugs.`
3. If ASR requires microphone/audio upload and the environment cannot provide it, mark `LIMITATION` with evidence.
4. If ASR UI is available and action runs but returns provider/runtime failure, mark `DEFECT`.

ASR `LIMITATION` vs `DEFECT` rule:

1. `LIMITATION`: environmental inability only (no mic device, no upload control in headless run, permission model blocks capture).
2. `DEFECT`: product behavior failure after ASR action is actually invoked (validation/runtime/provider failure, unexpected error, blocked readiness).

### 10.5 Runtime-Profile Auto-Assignment Verification (Current Phase Acceptance)

Deep profile-column validation is deferred with main Settings UI phase. For this phase, accept runtime-profile auto-assignment as verified if all are true:

1. Wizard model save succeeds for each required model/provider mapping.
2. Saved models appear selectable in Chat toolbar override list.
3. Model execution in chat succeeds without add/save/runtime profile errors.
4. No `MODEL_NOT_FOUND` blockers appear for in-scope wizard-created model use.

## 11. Stop Conditions and Defect Classification

Immediate stop and defect if:

1. Required wizard provider flow cannot be completed.
2. Required model cannot be added/selected via UI.
3. Required in-scope readiness is `blocked` (including `MODEL_NOT_FOUND`).
4. Save/activation fails after one clean retry.
5. Unexpected server exception appears in API logs during test actions.

Not defects:

1. Wizard dialog remains open after first `Finish` click.
2. Anthropic model absence in this phase's wizard/chat flows.

## 12. Evidence Package

Required minimum artifacts:

1. `output/playwright/full-provider-01-wizard-provider-options.png`
2. `output/playwright/full-provider-02-foundry-finish.png`
3. `output/playwright/full-provider-03-gemini-finish.png`
4. `output/playwright/full-provider-04-openai-finish.png`
5. `output/playwright/full-provider-05-huggingface-finish.png`
6. `output/playwright/full-provider-06-openrouter-finish.png`
7. `output/playwright/full-provider-07-chat-toolbar-chat.png`
8. `output/playwright/full-provider-08-chat-toolbar-image.png`
9. `output/playwright/full-provider-09-chat-toolbar-tts.png`
10. `output/playwright/full-provider-10-chat-toolbar-asr.png`
10. `output/playwright/full-provider-chat-<modelId>.png` per model
11. Defect evidence screenshots + relevant API log references

### 12.1 Artifact Naming Matrix (Provider/Service Specific)

Use these concrete filenames in addition to minimum set:

1. Chat models:
- `output/playwright/full-provider-chat-gpt-4.1-mini.png`
- `output/playwright/full-provider-chat-gpt-4o.png`
- `output/playwright/full-provider-chat-gpt-4o-mini.png`
- `output/playwright/full-provider-chat-gpt-5.2-codex.png`
- `output/playwright/full-provider-chat-gemini-2.5-flash.png`
- `output/playwright/full-provider-chat-gemini-2.5-pro.png`
- `output/playwright/full-provider-chat-gpt-4.1-nano.png`
- `output/playwright/full-provider-chat-gpt-5.1-2025-11-13.png`
- `output/playwright/full-provider-chat-deepseek-ai_DeepSeek-V4-Pro.png`

2. Image (per provider):
- `output/playwright/full-provider-image-foundry-generate.png`
- `output/playwright/full-provider-image-foundry-edit.png`
- `output/playwright/full-provider-image-gemini-generate.png`
- `output/playwright/full-provider-image-gemini-edit.png`
- `output/playwright/full-provider-image-openai-generate.png`
- `output/playwright/full-provider-image-openai-edit.png`
- `output/playwright/full-provider-image-hf-generate.png`
- `output/playwright/full-provider-image-hf-edit.png`

3. TTS (per provider):
- `output/playwright/full-provider-tts-foundry.png`
- `output/playwright/full-provider-tts-gemini.png`
- `output/playwright/full-provider-tts-openai.png`
- `output/playwright/full-provider-tts-hf.png`

4. ASR:
- `output/playwright/full-provider-asr-attempt.png`
- `output/playwright/full-provider-asr-limitation.png` (if limitation classification is used)

## 13. Result/Defect Reporting Template

For each significant step or blocker report:

1. Date/time (ET)
2. Phase (`Wizard` or `Chat/Toolbar`)
3. Provider/model/service impacted
4. Exact step performed
5. Observed behavior
6. Expected behavior
7. Outcome (`PASS` / `DEFECT` / `LIMITATION`)
8. Evidence file paths
9. API log references (if any)

## 14. Pass/Fail Criteria

Pass requires all:

1. Wizard flows completed for all in-scope wizard providers.
2. All required models in Section 7 exercised in chat loop.
3. Image and TTS loops exercised across in-scope providers.
4. ASR either directly exercised or documented as environment limitation with evidence.
5. No unresolved in-scope defects without evidence and classification.

Fail if any:

1. Required in-scope model/service coverage skipped.
2. API/DB direct intervention was used to execute tests.
3. Real blocker occurred and was not documented.
