# Local llama.cpp Docker (Qwen + Gemma)

This folder contains scripts and Docker assets for running local `llama.cpp` router mode with multiple local model families (currently Qwen and Gemma).

## Layout

- `run/download-model.ps1` - Download Q6 or Q8 GGUF model from Hugging Face (Qwen helper workflow).
- `run/download-sd-models.ps1` - Download FLUX2 + VAE + Qwen3-4B assets for local stable-diffusion.cpp testing.
- `run/download-tts-models.ps1` - Download Chatterbox catalog assets for local TTS testing.
- `run/download-emb-models.ps1` - Download Harrier embedding model assets for local embeddings testing.
- `run/start-llama-server.ps1` - Start persistent GPU server container.
- `run/stop-llama-server.ps1` - Stop or remove container.
- `run/test-openai-chat.ps1` - Host-side PowerShell API smoke test.
- `run/test-openai-chat.sh` - Host-side bash/curl API smoke test.
- `Dockerfile.model-fetcher` - Minimal helper image for scripted model download workflows.
- `../volumes/llama/models/` - Local GGUF + mmproj mount point (persisted on host).

## Quick start

```powershell
pwsh .\run\download-model.ps1 -Quantization q6
pwsh .\run\download-sd-models.ps1
pwsh .\run\download-tts-models.ps1
pwsh .\run\download-emb-models.ps1
pwsh .\run\start-llama-server.ps1
pwsh .\run\test-openai-chat.ps1
```

## Notes

- **Hugging Face token for UI add-model downloads:** token is configured in
  **Settings → Connections → Hugging Face** and injected server-side during
  `POST /api/settings/models:add` when source is `Install from Hugging Face`.
  No per-request token override is accepted on the UI endpoint.

  For gated Hugging Face models, prefer option 2 so credentials live with the application. `HF_TOKEN` is intentionally **not** bound into the `appsettings.json` schema — it is a pure host-level env var consumed by shell scripts and by option 3 above; `ComposeEnvironmentContractTests` whitelists it explicitly.
- **Router alias registration via the UI.** New aliases are registered atomically through the Add Model wizard:
  - **Settings → Models & Runtime → Catalog → Add Model** calls `POST /api/settings/models:add` on the web API, which delegates runtime transfer work to `guideants-ai` (`/llama-admin/downloads`) for the Hugging Face source.
  - The `llama-admin` service in `guideants-ai` performs non-destructive file writes in `/models-local/llama`, serializes same-alias downloads, and atomically upserts the live router preset at **`/models-local/router-models.ini`** on the **`ai_local_models`** volume (same filesystem as GGUF trees — not a host bind). On first boot of an empty volume, `entrypoint.sh` seeds that file from `/opt/seed/router-models.ini` in the image.
  - The **GuideAnts API** delegates downloads and router changes to llama-admin over HTTP; the web API does not read a repo-side `router-models.ini` at runtime.
  - Both paths are visible in the Local Llama Runtime tab header and in **Settings → Infrastructure → Runtime Dependencies** with a source indicator (`appsettings` / `env` / `compose`) and an existence probe.
  - The `router-models.ini` file in this folder documents the default seed format (kept in sync with `docker/build/guideants-ai/router-models.seed.ini`); operational truth is the file on the volume, maintained via **Settings → Models & Runtime** (UI).
- Runtime uses the upstream `ghcr.io/ggml-org/llama.cpp:server-cuda13` image directly.
- Container uses `--restart unless-stopped` so it survives host restarts.
- In the full compose stack, every local model (llama GGUFs, ASR, SD
  bundles, TTS, embeddings) persists in a single Docker named volume
  `ai_local_models` mounted at `/models-local` in `guideants-ai`. The
  llama tree is at `/models-local/llama`.
- In standalone script flows from this folder, the same named volume is
  reused — the scripts no longer accept ad-hoc host bind paths.
- Single ingress port is exposed at `8110` and routed by prefix:
  - `/llama-cpp/*` -> llama-server
  - `/sandbox/*` -> ScriptExecutionAgent
  - `/asr/*` -> local ASR service
- `/sd/*` -> stable-diffusion.cpp wrapper service (txt2img/img2img)
- `/tts/*` -> local TTS service (synthesize WAV)
- `/emb/*` -> local embeddings service (query/document vectors)
- `--no-mmap` is the default in scripts/compose for stability on this host.
- To explicitly enable mmap, pass `-UseMmap` to `run/start-llama-server.ps1`.
- Default context window is `262144` (Qwen 3.5 default context length).
- Vision input requires `mmproj`; the download script fetches it by default and places it in a model-specific subdirectory so the router can load it automatically.
- Keep each model + its `mmproj` in a dedicated subdirectory (for example `/models/Qwen3.5-27B-Q6_K/*` and `/models/Gemma-4-31B-Q6_K_XL/*`) because `mmproj` filenames often collide across model families.
- Server startup always includes `--jinja` (required for current local model families).
- Runtime model loading is explicit through `/models/load`; no autoload fallback is assumed.
- Startup load controls (ASR + SD + TTS + Embeddings) are environment-driven:
  - `CUDA_VISIBLE_DEVICES=<gpu-id-list>` (optional; sourced from active env)
    - Global ordering for all services in the container when set (example `1,0` maps `host GPU 1 -> cuda:0`, `host GPU 0 -> cuda:1`)
  - Optional per-service overrides (comma-separated physical GPU ids; empty means inherit global ordering):
    - `GA_LLAMA_CUDA_VISIBLE_DEVICES`
    - `GA_ASR_CUDA_VISIBLE_DEVICES`
    - `GA_TTS_CUDA_VISIBLE_DEVICES`
    - `GA_EMB_CUDA_VISIBLE_DEVICES`
  - `GA_ASR_AUTO_LOAD_ON_STARTUP=1|0`
  - `GA_ASR_DEVICE_MAP=auto` (default)
  - `GA_TTS_AUTO_LOAD_ON_STARTUP=1|0`
  - `GA_TTS_DEVICE_MAP=auto` (default)
  - `GA_EMB_AUTO_LOAD_ON_STARTUP=1|0`
  - `GA_EMB_WARMUP_ON_LOAD=1|0` (autoload forces warmup)
  - `GA_EMB_WAIT_FOR_READY_ON_STARTUP=1|0`
  - `GA_EMB_READY_TIMEOUT_SECONDS`
  - `GA_SD_AUTO_LOAD_ON_STARTUP=1|0`
  - `GA_SD_CUDA_VISIBLE_DEVICES=<gpu-id-list>` (optional SD-only physical GPU pinning; empty inherits global ordering)
  - `GA_SD_ENGINE_REQUEST_TIMEOUT_SECONDS`
  - `GA_SD_WARMUP_PROMPT`, `GA_SD_WARMUP_SIZE`, `GA_SD_WARMUP_STEPS`, `GA_SD_WARMUP_OUTPUT_FORMAT`
  - `GA_SD_WARMUP_REQUEST_TIMEOUT_SECONDS`
  - `GA_SD_WARMUP_FAIL_OPEN_ON_STARTUP=1|0` (when `1`, startup warmup failure is non-fatal; use `/sd/admin/warmup` to retry)
  - `GA_SD_WAIT_FOR_READY_ON_STARTUP=1|0`, `GA_SD_READY_TIMEOUT_SECONDS`
  - Default behavior: the web API orchestrates startup warmup (LLM first, then ASR, embeddings, TTS, and SD). Container-side autoload flags default to off; do not enable `GA_SD_AUTO_LOAD_ON_STARTUP` unless you intentionally bypass web API orchestration.
- llama health endpoint via gateway: `http://localhost:8110/llama-cpp/health`
- embeddings health endpoint via gateway: `http://localhost:8110/emb/health`
- SD warmup retry endpoint via gateway: `http://localhost:8110/sd/admin/warmup` (`POST`)
