# Local AI Setup Guide (Wizard-Only)

Last validated: 2026-05-05

This guide configures GuideAnts for fully local AI using the Setup Wizard only. If you only need Python sandbox/script execution and plan to use cloud/provider AI for model calls, use the explicit `--backend slim` stack instead of this local model setup.

For GPU acceleration without CUDA 13 or ROCm, start the stack with `--backend vulkan` (see [`docker/guideants-ai-vulkan.md`](../docker/guideants-ai-vulkan.md)). Vulkan GPU-accelerates llama and image generation; ASR, TTS, and embeddings still run on CPU inside the image.

## Prerequisites

1. GuideAnts is running at `http://localhost:5107`.
2. Local runtime containers are running.
3. A Hugging Face token is already stored.

## Target Local Configuration

After completion, these local providers and values should be active:

- Speech Transcription: `SpeechTranscription.LocalAsr.Http`, `TimeoutSeconds=300`
- Image Generation: `ImageGeneration.LocalSd.Http`, `TimeoutSeconds=900`, `LocalOutputFormat=png`
- Speech Synthesis: `SpeechSynthesis.LocalTts.Http`, `TimeoutSeconds=300`
- Document Intelligence: `DocumentIntelligence.LocalDocling.Http`, `TimeoutSeconds=600`, `MaxConcurrentConversions=1`, `AsyncStatusPollIntervalMs=2000`
- Embeddings: `Embeddings.LocalEmb.Http`, `TimeoutSeconds=300`, `LocalMinIntervalMs=5000`

Deterministic model choices used in this flow:

- ASR: `Qwen/Qwen3-ASR-0.6B`
- TTS: catalog model `chatterbox` with reference voice `en_us_cv_001`
- Embeddings: catalog model `qwen3_embedding_0_6b` (Qwen3-Embedding-0.6B GGUF)
- Image bundle:
  - Diffusion: `unsloth/FLUX.2-klein-4B-GGUF` + `flux-2-klein-4b-Q4_K_S.gguf`
  - VAE: `black-forest-labs/FLUX.2-small-decoder` + `full_encoder_small_decoder.safetensors`
  - Text encoder: `unsloth/Qwen3-4B-GGUF` + `Qwen3-4B-Q4_K_M.gguf`

## Step-by-Step Wizard Flow

### 1. Open Setup Wizard from Home

![Wizard start on home page](images/local-ai-wizard/wizard-00-home.png)

Open Setup Wizard:

![Wizard opened](images/local-ai-wizard/wizard-01-open.png)

### 2. Select Local AI Provider

Pick `Local AI` and continue.

![Local AI selected](images/local-ai-wizard/wizard-02-provider-local-ai.png)

### 3. Connection Details (Prerequisites)

Confirm the HF token is already stored and infrastructure statuses are configured, then continue.

![Prerequisites passed](images/local-ai-wizard/wizard-03-prereqs-pass.png)

### 4. Models (Chat)

Ensure at least one local llama-cpp chat model is available, then continue.

![Chat model ready](images/local-ai-wizard/wizard-04-models-chat-ready.png)

### 5. Speech Transcription

Confirm provider is fixed to Local ASR HTTP, keep `TimeoutSeconds=300`, and ensure readiness is `Ready`.

![ASR ready](images/local-ai-wizard/wizard-05-asr-ready.png)

### 6. Image Generation

Set `TimeoutSeconds=900`, keep output format `png`, install/activate the exact bundle, load engine, and wait for `Ready`.

![Image generation ready](images/local-ai-wizard/wizard-06-image-ready.png)

### 7. Speech Synthesis

Confirm provider is Local TTS HTTP, `TimeoutSeconds=300`, catalog model `chatterbox`, reference voice `en_us_cv_001`, and readiness `Ready`. Local TTS infers language from the selected voice-pack voice.

![TTS ready](images/local-ai-wizard/wizard-07-tts-ready.png)

### 8. Document Intelligence

Set:

- `TimeoutSeconds=600`
- `MaxConcurrentConversions=1`
- `AsyncStatusPollIntervalMs=2000`

![Document Intelligence values](images/local-ai-wizard/wizard-08-docint-values.png)

### 9. Embeddings

Set `TimeoutSeconds=300`, `LocalMinIntervalMs=5000`, install/load `microsoft/harrier-oss-v1-0.6b`, and wait for `Ready`.

![Embeddings ready](images/local-ai-wizard/wizard-09-embeddings-ready.png)

### 10. Finish

Click `Finish`.

![Wizard finish](images/local-ai-wizard/wizard-10-finish.png)

## Persistence Check

Reopen Setup Wizard, select `Local AI`, and walk through local service steps. Confirm all provider IDs and values remain unchanged.

![Persistence check](images/local-ai-wizard/wizard-11-persistence.png)

## Result from Latest Validation

- Wizard-only flow completed end-to-end.
- All required local provider IDs and values persisted exactly.
- ASR, Image, TTS, and Embeddings reached `Ready`.
