# OpenRouter Provider Completion

Last updated: 2026-06-01

## Scope

This document records the completion state for OpenRouter across:

- Settings UI visibility
- Add AI Services wizard onboarding
- Chat and non-chat runtime routing
- Unit and UI qualification

## Completion Status

OpenRouter is treated as operator-facing for this cycle.

- `openrouter-chat` is visible in model onboarding and runtime profile surfaces.
- OpenRouter connection fields are visible in Settings Connections.
- Service editor routes are visible for:
  - `Embeddings.OpenRouter.Embeddings`
  - `ImageGeneration.OpenRouter.Image`
  - `SpeechTranscription.OpenRouter.Audio`
  - `SpeechSynthesis.OpenRouter.Tts`
- Add AI Services wizard includes a dedicated OpenRouter provider path.

## Locked Operator Defaults

These values are the default onboarding recommendations and qualification targets.

- Chat: `minimax/minimax-m3`
- Image: `recraft/recraft-v4`
- Embeddings: `nvidia/llama-nemotron-embed-vl-1b-v2:free`
- TTS: `hexgrad/kokoro-82m`
- ASR: `nvidia/parakeet-tdt-0.6b-v3`

## Image Model UX Rule

OpenRouter uses a single image model field (`ModelId`) for both text-to-image and image edit operations. This differs from Hugging Face inference, which uses separate text-to-image and image-to-image fields.

## Backend Notes

- OpenRouter chat and non-chat requests always include fixed OpenRouter app attribution headers:
  - `HTTP-Referer`: https://www.guideants.ai
  - `X-OpenRouter-Title` and legacy `X-Title`: GuideAnts
  - `X-OpenRouter-Categories`: programming-app,cloud-agent,personal-agent,writing-assistant,general-chat,image-gen
- OpenRouter embeddings accept `Dimensions` from request preset JSON when provided.
- OpenRouter TTS keeps provider default voice behavior (`alloy`) and allows `VoiceName` override via service mode request preset.

## Qualification

OpenRouter is added to the full-provider Playwright qualification matrix in `docs/full-provider-test-plan.md` with deterministic model targets for chat and all non-chat services.
