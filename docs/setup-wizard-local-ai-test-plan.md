# Setup Wizard-Only Local AI Configuration Test Plan

Last updated: 2026-05-05

## 1. Purpose

Validate that GuideAnts can be fully configured for **local AI usage** using the **Setup Wizard only**.

This plan already incorporates the reference database (`guideants-dev-major-refactor-20260415`) during planning.
The reference DB is **not** part of execution.

## 2. Hard Rules (non-negotiable)

1. Use **Setup Wizard only** for all configuration changes.
2. Do not use Settings pages to configure anything.
3. Do not use API calls or SQL writes as part of execution.
4. Hugging Face token is already saved in the test DB. Do not edit token.
5. Do not substitute alternate model choices. Use the exact choices in this runbook.
6. Do not skip local service steps.

## 3. Tools

Execution tool:

1. `playwright-cli`

Evidence tool:

1. `playwright-cli screenshot --filename=...`

No other execution tooling is required.

## 4. Pre-Execution Inputs

1. App URL: `http://localhost:5107`
2. Tester starts from Home page.
3. Local runtime containers are running.
4. HF token precondition is already satisfied.

## 5. Target Configuration (frozen)

After wizard completion, this exact configuration must be true:

1. Active provider IDs:
- `SpeechTranscription.LocalAsr.Http`
- `ImageGeneration.LocalSd.Http`
- `SpeechSynthesis.LocalTts.Http`
- `DocumentIntelligence.LocalDocling.Http`
- `Embeddings.LocalEmb.Http`

2. Field values:
- Speech Transcription: `TimeoutSeconds=300`
- Speech Synthesis: `TimeoutSeconds=300`
- Image Generation: `TimeoutSeconds=900`
- Image Generation: `LocalOutputFormat=png`
- Embeddings: `TimeoutSeconds=300`
- Embeddings: `LocalMinIntervalMs=5000`
- Document Intelligence: `TimeoutSeconds=600`
- Document Intelligence: `MaxConcurrentConversions=1`
- Document Intelligence: `AsyncStatusPollIntervalMs=2000`

3. Chat model readiness:
- At least one local `llama-cpp` chat model is present and usable.

4. Service runtime readiness:
- ASR ready
- Image runtime ready
- TTS ready
- Embeddings ready
- Document Intelligence step saved with local provider + values above

## 6. Deterministic Test Data

Use the following exact choices in wizard:

1. ASR model:
- `Qwen/Qwen3-ASR-0.6B`

2. TTS model:
- `chatterbox` (catalog)
- Reference voice: `en_us_cv_001`

3. Embeddings model:
- `qwen3_embedding_0_6b` (Qwen3-Embedding-0.6B GGUF)

4. Image bundle:
- Diffusion repo: `unsloth/FLUX.2-klein-4B-GGUF`
- Diffusion file: `flux-2-klein-4b-Q4_K_S.gguf`
- VAE repo: `black-forest-labs/FLUX.2-small-decoder`
- VAE file: `full_encoder_small_decoder.safetensors`
- Text encoder repo: `unsloth/Qwen3-4B-GGUF`
- Text encoder file: `Qwen3-4B-Q4_K_M.gguf`

5. Chat model step:
- Use existing local chat model if already present in wizard list.
- If wizard requires installation to proceed, install exactly one local GGUF model and proceed when status is completed.

## 7. Execution Procedure (Wizard-only)

### Step 0: Open and capture start state

1. Run:
```bash
playwright-cli open http://localhost:5107
playwright-cli snapshot --filename=wizard-00-home.yaml
playwright-cli screenshot --filename=output/playwright/wizard-00-home.png
```

2. Click `Setup Wizard`.
3. Capture:
```bash
playwright-cli snapshot --filename=wizard-01-open.yaml
playwright-cli screenshot --filename=output/playwright/wizard-01-open.png
```

Expected:

1. Wizard modal opens.
2. Footer actions visible: `Not now`, `Configure manually`, `Back`, `Next`, `Finish`.

### Step 1: Provider

Actions:

1. Select provider `Local AI`.
2. Click `Next`.
3. Capture:
```bash
playwright-cli screenshot --filename=output/playwright/wizard-02-provider-local-ai.png
```

Expected:

1. Step sequence switches to local flow:
- Provider
- Connection details
- Models
- Speech Transcription
- Image Generation
- Speech Synthesis
- Document Intelligence
- Embeddings
- Finish

### Step 2: Connection details (Prerequisites)

Actions:

1. Verify HF token shows as already stored.
2. Verify infrastructure checks show ready/reachable.
3. Do not edit HF token.
4. Click `Next`.
5. Capture:
```bash
playwright-cli screenshot --filename=output/playwright/wizard-03-prereqs-pass.png
```

Expected:

1. No validation error.
2. Next transitions to `Models`.

### Step 3: Models (chat)

Actions:

1. Ensure at least one local chat model is usable in this step.
2. If required by wizard to proceed, perform one chat model install and wait until completed.
3. Click `Next`.
4. Capture:
```bash
playwright-cli screenshot --filename=output/playwright/wizard-04-models-chat-ready.png
```

Expected:

1. Model requirement is satisfied.
2. Step advances to `Speech Transcription`.

### Step 4: Speech Transcription

Actions:

1. Confirm provider is fixed to `SpeechTranscription.LocalAsr.Http`.
2. Set `TimeoutSeconds=300`.
3. Download/install model `Qwen/Qwen3-ASR-0.6B`.
4. Load/select active model when available.
5. Wait until readiness is ready.
6. Click `Next`.
7. Capture:
```bash
playwright-cli screenshot --filename=output/playwright/wizard-05-asr-ready.png
```

Expected:

1. No blocking error.
2. Step advances to `Image Generation`.

### Step 5: Image Generation

Actions:

1. Confirm provider is fixed to `ImageGeneration.LocalSd.Http`.
2. Set `TimeoutSeconds=900`.
3. Set `LocalOutputFormat=png`.
4. Download/install bundle with exact repo/file pairs from Section 6.
5. Select bundle active.
6. Load image runtime.
7. Wait until readiness is ready.
8. Click `Next`.
9. Capture:
```bash
playwright-cli screenshot --filename=output/playwright/wizard-06-image-ready.png
```

Expected:

1. Bundle install completes.
2. Active bundle is set.
3. Step advances to `Speech Synthesis`.

### Step 6: Speech Synthesis

Actions:

1. Confirm provider is fixed to `SpeechSynthesis.LocalTts.Http`.
2. Set `TimeoutSeconds=300`.
3. Download/install catalog model `chatterbox`.
4. Keep reference voice default `en_us_cv_001`; no language or speed fields should be shown.
5. Load/select active model.
6. Wait until readiness is ready.
7. Click `Next`.
8. Capture:
```bash
playwright-cli screenshot --filename=output/playwright/wizard-07-tts-ready.png
```

Expected:

1. No blocking error.
2. Step advances to `Document Intelligence`.

### Step 7: Document Intelligence

Actions:

1. Confirm provider is fixed to `DocumentIntelligence.LocalDocling.Http`.
2. Set `TimeoutSeconds=600`.
3. Set `MaxConcurrentConversions=1`.
4. Set `AsyncStatusPollIntervalMs=2000`.
5. Click `Next`.
6. Capture:
```bash
playwright-cli screenshot --filename=output/playwright/wizard-08-docint-values.png
```

Expected:

1. Values save without validation errors.
2. Step advances to `Embeddings`.

### Step 8: Embeddings

Actions:

1. Confirm provider is fixed to `Embeddings.LocalEmb.Http`.
2. Set `TimeoutSeconds=300`.
3. Set `LocalMinIntervalMs=5000`.
4. Download/install model `microsoft/harrier-oss-v1-0.6b`.
5. Load/select active model.
6. Wait until readiness is ready.
7. Click `Next`.
8. Capture:
```bash
playwright-cli screenshot --filename=output/playwright/wizard-09-embeddings-ready.png
```

Expected:

1. Embeddings is configured to local provider and ready.
2. Step advances to `Finish`.

### Step 9: Finish

Actions:

1. Click `Finish`.
2. Capture:
```bash
playwright-cli screenshot --filename=output/playwright/wizard-10-finish.png
```

Expected:

1. Wizard closes.
2. No global error banner.

### Step 10: Persistence check (still wizard-only)

Actions:

1. Reopen `Setup Wizard`.
2. Select `Local AI`.
3. Navigate through local service steps.
4. Confirm each required value/provider from Section 5 remains exactly persisted.
5. Capture:
```bash
playwright-cli screenshot --filename=output/playwright/wizard-11-persistence.png
```

Expected:

1. Values remain unchanged.
2. Providers remain local IDs listed in Section 5.

## 8. Pass/Fail Criteria

Pass only if all are true:

1. Entire flow completed using Setup Wizard only.
2. All provider IDs and field values match Section 5 exactly.
3. Required runtime readiness reached on ASR/Image/TTS/Embeddings.
4. Persistence check passes on wizard reopen.

Fail if any are true:

1. Tester must leave wizard to finish setup.
2. Any required value/provider differs from Section 5.
3. Any required service never reaches ready state.
4. Any step is skipped.

Blocked-Infra if:

1. A required model download repeatedly fails due runtime/network/container issues after one in-wizard cancel+retry.

## 9. Evidence Package

Required output files:

1. `output/playwright/wizard-00-home.png`
2. `output/playwright/wizard-01-open.png`
3. `output/playwright/wizard-02-provider-local-ai.png`
4. `output/playwright/wizard-03-prereqs-pass.png`
5. `output/playwright/wizard-04-models-chat-ready.png`
6. `output/playwright/wizard-05-asr-ready.png`
7. `output/playwright/wizard-06-image-ready.png`
8. `output/playwright/wizard-07-tts-ready.png`
9. `output/playwright/wizard-08-docint-values.png`
10. `output/playwright/wizard-09-embeddings-ready.png`
11. `output/playwright/wizard-10-finish.png`
12. `output/playwright/wizard-11-persistence.png`

## 10. Notes for Tester

1. Do not interpret this as a broad exploratory test.
2. This is a deterministic qualification runbook.
3. If a required UI field is missing or renamed, mark `Fail` and capture screenshot at failure point.
