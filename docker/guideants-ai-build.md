# GuideAnts AI -- Build System

## Background

GuideAnts AI consolidates two prior containers into one runtime image:

- `llama-server` for model inference (internal port 8080)
- `ScriptExecutionAgent` for script execution (internal port 8081)
- local ASR service (internal port 8082)
- local stable-diffusion.cpp wrapper service (internal port 8083)
- local TTS service (internal port 8084)
- local embeddings service (internal port 8085)
- `nginx` gateway for single ingress (port 80)

Gateway route prefixes:

- `/sandbox/*` -> ScriptExecutionAgent
- `/llama-cpp/*` -> llama-server
- `/asr/*` -> local ASR service
- `/sd/*` -> local stable-diffusion.cpp wrapper service
- `/tts/*` -> local TTS service
- `/emb/*` -> local embeddings service

The build system is optimized for local iterative development:

- one build script
- one Dockerfile
- backend selected interactively (CPU or CUDA 13)
- deterministic dependency-image tags derived from dependency file hashes

## GHCR Publishing

GitHub Actions publish the runtime images to GHCR as separate packages:

- `ghcr.io/<owner>/guideants-ai-cpu`
- `ghcr.io/<owner>/guideants-ai-cuda13`

Workflow:

- `.github/workflows/publish-guideants-ai-images.yml`

Manual dispatch options:

- `all` publishes both variants
- `cpu` publishes only the CPU image
- `cuda13` publishes only the CUDA 13 image

Workflow implementation details:

- publishes `src/server/ScriptExecutionAgent` with `dotnet publish`
- stages that output into `docker/build/guideants-ai/ScriptExecutionAgent`
- copies backend-specific sandbox requirements into `docker/build/guideants-ai/requirements.txt`
- strips `torch`, `torchaudio`, `torchvision`, and `torchtext` so the Dockerfile remains the single owner of backend torch installation
- builds `final-cpu` or `final-cuda13`
- runs by manual GitHub Actions dispatch and pushes branch, `sha-*`, and `latest` tags to GHCR
- uses GitHub Actions cache scopes per backend instead of publishing `guideants-ai-deps:*` cache images

## Current Design

### One Dockerfile, backend-specific dependency stages

`docker/build/guideants-ai/Dockerfile` contains backend-specific builder, dependency, and runtime stages:

- `sd-cli-cpu-builder` -> builds CPU `stable-diffusion.cpp` binaries (`sd-cli` + `sd-server`)
- `sd-cli-cuda-builder` -> builds CUDA `stable-diffusion.cpp` binaries (`sd-cli` + `sd-server`)

- `runtime-cpu-base` -> OS/runtime base on `ghcr.io/ggml-org/llama.cpp:server`
- `pydeps-cpu-builder` -> Python dependency build stage (includes build toolchain)
- `deps-cpu` -> runtime dependency image (no compiler toolchain)
- `final-cpu` -> runtime image on top of `deps-cpu` (or an externally tagged deps image)

- `runtime-cuda13-base` -> OS/runtime base on `ghcr.io/ggml-org/llama.cpp:server-cuda13`
- `pydeps-cuda13-builder` -> Python dependency build stage (includes build toolchain)
- `deps-cuda13` -> runtime dependency image (no compiler toolchain)
- `final-cuda13` -> runtime image on top of `deps-cuda13` (or an externally tagged deps image)

The script builds one target with `--target` based on prompt choice:

- CPU choice -> `--target final-cpu`
- CUDA choice -> `--target final-cuda13`

The backend choice is baked into the image:

- `final-cpu` gets `sd-cli` + `sd-server` from `sd-cli-cpu-builder`
- `final-cuda13` gets CUDA-enabled `sd-cli` + `sd-server` from `sd-cli-cuda-builder`

No startup toggle is used to switch stable-diffusion backend capability.

### Python environment model

GuideAnts AI uses one Python virtual environment per backend image:

- shared env path: `/opt/venv`
- no dedicated `/opt/venv-asr`
- torch + ASR/TTS + embeddings + filtered app requirements install into the same env in `pydeps-*` builder stages
- final dependency stages copy only the finished venv (not compiler toolchains)

This removes duplicate torch/CUDA wheel installation and reduces image size.

### Caching behavior

- BuildKit cache mounts are used for `apt` and `pip` in heavy stages.
- The build script computes a hash from dependency inputs and tags dependency images:
  - `guideants-ai-deps:cpu-<hash12>`
  - `guideants-ai-deps:cuda13-<hash12>`
- If the matching deps image exists, the final build reuses it via `--cache-from` and backend-specific build args.
- If the deps image is missing (or `-RebuildBase` is passed), the script rebuilds it first.
- `-RebuildBase` still forces no-cache builds for dependency and final targets.

## Script Behavior

Build script: `docker/build/build_guideants_ai.ps1`

Supported switches:

- none: prompt for backend, build final GuideAnts AI image
- `-RebuildBase`: prompt for backend, force rebuild without cache
- `-All`: build GuideAnts AI, PlantUML, MSSQL, and the compose-used WebAPI+UI image
- `-RebuildBase -All`: full no-cache GuideAnts AI build plus additional images

### Build flow

1. Prompt backend (`CPU` or `CUDA 13`)
2. Build/publish `src/server/ScriptExecutionAgent`
3. Stage `ScriptExecutionAgent` and filtered `requirements.txt` into Docker build context
4. Compute dependency hash from Dockerfile + requirement inputs
5. Build/reuse backend-specific dependency image (`deps-cpu` or `deps-cuda13`)
6. Build final runtime target (`final-cpu` or `final-cuda13`) using the dependency image
7. Clean staged artifacts
8. Write `GA_AI_CUDA_IMAGE=<final-tag>` or `GA_AI_CPU_IMAGE=<final-tag>` to `docker/.env`
9. Optionally build PlantUML/MSSQL and invoke `build_webapi_ui.ps1` if `-All` was passed

## File Layout

```text
docker/
  .env
  docker-compose.cuda.yml
  guideants-ai-build.md
  build/
    build_guideants_ai.ps1
    guideants-ai/
      Dockerfile
      entrypoint.sh
      start-llama.sh
      start-asr.sh
      start-sd.sh
      start-tts.sh
      start-emb.sh
      asr-service/
      sd-service/
      tts-service/
      emb-service/
      .gitattributes
    Sandboxes/
      python311TorchCPU/requirements.txt
      python311TorchCUDA/requirements.txt
```

## Image Tagging

Two image categories are tagged:

- dependency images (cache/reuse targets):
  - `guideants-ai-deps:<backend>-<hash12>`
- final runtime images (compose/runtime target):
  - `guideants-ai:<backend>-<YYDDD>.<HHmm>`

Examples:

- `guideants-ai:cuda13-26096.1715`
- `guideants-ai:cpu-26096.1715`
- `guideants-ai-deps:cuda13-89ab1c2d3e4f`
- `guideants-ai-deps:cpu-1a2b3c4d5e6f`

`GA_AI_CUDA_IMAGE` or `GA_AI_CPU_IMAGE` in `docker/.env` is always updated to the latest built final tag.

## Running

Compose:

```powershell
cd docker
docker compose up guideants-ai
```

Docling profile (document extraction provider) options:

```powershell
# CPU profile
docker compose --profile docling-cpu up

# CUDA 13 profile
docker compose --profile docling-cuda up
```

Recommended image pin variables in `docker/.env`:

```dotenv
DOCLING_SERVE_CPU_IMAGE=quay.io/docling-project/docling-serve-cpu:v1.16.1
DOCLING_SERVE_CUDA_IMAGE=quay.io/docling-project/docling-serve-cu130:v1.16.1
DOCLING_SERVE_MAX_SYNC_WAIT=600
```

`DOCLING_SERVE_MAX_SYNC_WAIT` is in seconds and only affects Docling synchronous endpoints.
GuideAnts markdown extraction now uses Docling async endpoints.

`guideants-webapi-ui` should keep `DocumentIntelligence__LocalDoclingBaseUrl=http://docling-serve:5001` so either profile resolves through the shared `docling-serve` network alias.

### Docling Models Included by `docling-serve` Images

For `quay.io/docling-project/docling-serve-*:v1.16.1`, model artifacts are baked into the image under:

`/opt/app-root/src/.cache/docling/models`

Included model families/artifacts:

- Layout: `docling-project/docling-layout-heron`
- Table structure: `docling-project/docling-models`
  - `model_artifacts/tableformer/accurate/tableformer_accurate.safetensors`
  - `model_artifacts/tableformer/fast/tableformer_fast.safetensors`
- Picture classifier: `docling-project/DocumentFigureClassifier-v2.5`
- OCR assets:
  - RapidOCR PP-OCRv4 artifacts (`onnx` + `torch` bundles)
  - EasyOCR artifacts (`craft_mlt_25k.pth`, `english_g2.pth`, `latin_g2.pth`)

### Docling Defaults Used by Current GuideAnts Integration

`GuideAntsApi` now submits file content + `to_formats=md` to `/v1/convert/file/async`,
polls `/v1/status/poll/{task_id}`, then fetches `/v1/result/{task_id}`.
No explicit OCR/layout/table preset override is sent.

With Docling defaults in `v1.16.1`, this means:

- OCR preset: `auto` (engine selected by Docling at runtime)
- Layout preset/kind: default (`docling_layout_default`, which uses Heron)
- Table structure preset: default (`tableformer_v1_accurate`)
- `do_picture_classification`: `false` unless explicitly enabled
- `do_picture_description`: `false` unless explicitly enabled

### Model Hosting Notes

- Hugging Face-backed in Docling:
  - `docling-project/docling-layout-heron`
  - `docling-project/docling-models`
  - `docling-project/DocumentFigureClassifier-v2.5`
- Not Hugging Face-backed:
  - RapidOCR model files (ModelScope-hosted in Docling downloader)
  - EasyOCR model files (EasyOCR model sources/config)

Standalone launcher:

```powershell
cd docker/llama/run
.\start-llama-server.ps1
```

## Local Model Storage Layout

Every local AI model (llama GGUFs, ASR, SD bundles, TTS weights, embeddings)
now lives in a single Docker named volume `ai_local_models` with
per-service subdirectories:

- `/models-local/llama`
- `/models-local/asr`
- `/models-local/sd` (bundles under `bundles/<bundleId>/{diffusion,vae,text-encoder}/`)
- `/models-local/tts`
- `/models-local/emb`

Populate the volume on a fresh host via
`docker/scripts/migrate-local-models-to-single-volume.ps1` (copies from
pre-existing host binds / named volumes and restructures legacy flat SD
files into a bundle). New bundles and models are added through the
Settings UI, which drives `huggingface_hub.snapshot_download` server-side.

Legacy `GA_TTS_MODELS_HOST_PATH`, `GA_SD_MODELS_HOST_PATH`, and
`GA_EMB_MODELS_HOST_PATH` overrides are no longer consulted by
`docker-compose.cuda.yml` and have been removed from `docker/.env`.

## Local SD Model Bootstrap (Legacy Pre-refactor Path)

The pre-refactor flow downloaded flat files directly to a host bind
directory (`docker/volumes/sd/models`) that was then bind-mounted at
`/models-sd`. That path is gone. On a fresh host:

1. Run the migration script above if you have an old-shape SD directory
   to import, OR
2. Start the stack empty and add bundles through Settings → Image
   generation → Add bundle (drives `huggingface_hub.snapshot_download`
   under the covers with the centralized `HuggingFace:Token`).

The SD service looks for bundles at `/models-local/sd/bundles/<id>/`
with `diffusion/`, `vae/`, and `text-encoder/` role subdirs containing
exactly one file each. The active bundle is recorded in
`/models-local/sd/active_bundle.json`.

### Active vs loaded bundle

"Active bundle" and "loaded bundle" are two different pieces of state:

- **Active bundle** (`active_bundle.json` on disk) is the bundle the
  engine will pick up when it next starts. Modified by
  `POST /sd/admin/bundles/{id}/select-active`.
- **Loaded bundle** is the bundle the `sd-server` child process has
  actually mapped into GPU/RAM right now. Surfaced on
  `GET /sd/admin/bundles` as `loadedBundleId` + engine state
  (`running` / `unloaded` / `degraded`).

Runtime lifecycle endpoints (all serialized by an internal lock; a
second caller gets HTTP 409 rather than racing):

- `POST /sd/admin/load` — start `sd-server` against the current active
  bundle. No-op when already running.
- `POST /sd/admin/unload` — stop `sd-server` and release GPU/RAM. Any
  in-flight generation will fail with a connection error; this is by
  design so unload is never blocked by a long job.
- `POST /sd/admin/bundles/{id}/select-active` — update the on-disk
  active marker AND, if an engine is already running, hot-swap it to
  the newly active bundle. Changing the active bundle does **not**
  require a `guideants-ai` restart.

If startup warmup times out, the SD wrapper stays up (fail-open) and
supports manual retry via `POST /sd/admin/warmup`. If `sd-server`
itself fails to launch (bad paths, missing artifacts, subprocess
crash during warmup), the service degrades to `unloaded` with
`config_error` populated on `/sd/health` and `/sd/admin/bundles`;
the container stays up so the operator can re-select or re-download
a bundle and call `POST /sd/admin/load` from the UI.

## Local TTS Model Bootstrap (Pre-test, External Artifacts)

TTS model files are not baked into the image. Download them to the mounted host directory before testing local podcast generation:

```powershell
cd docker/llama/run
.\download-tts-models.ps1
```

Default location inside the `ai_local_models` volume:

`/models-local/tts`

Default expected subdirectories:

- `VibeVoice-1.5B` (from `microsoft/VibeVoice-1.5B`)
- `Qwen2.5-1.5B-tokenizer` (from `Qwen/Qwen2.5-1.5B`)

If these files are missing, the `/tts/admin/load` and `/tts/synthesize`
endpoints fail until artifacts are present. On a fresh host, either run
the migration script or register the models through Settings → Speech.

## Local Embeddings Model Bootstrap (Pre-test, External Artifacts)

Embedding model files are not baked into the image. Download them to the mounted host directory before testing local embeddings:

```powershell
cd docker/llama/run
.\download-emb-models.ps1
```

Default location inside the `ai_local_models` volume:

`/models-local/emb`

Default expected subdirectory:

- `harrier-oss-v1-0.6b` (from `microsoft/harrier-oss-v1-0.6b`)

If these files are missing, `/emb/admin/load`, `/emb/ready`, and
`/emb/embed` fail until artifacts are present.

Required pre-test sequence for embeddings:

1. Stage the model into the `ai_local_models` volume (migration script
   or Settings UI — the pre-refactor `.\download-emb-models.ps1` host
   download path is no longer wired into the compose stack).
2. `docker compose up -d` — the volume mounts at `/models-local` and the
   emb service reads from `/models-local/emb`.
3. Verify `http://localhost:8110/emb/health`.
4. Verify `http://localhost:8110/emb/ready` after autoload warmup finishes.
5. Run `/emb/embed` smoke calls with `purpose=document` and `purpose=query`.

## Startup Load Controls (ASR + SD + TTS + Embeddings)

Startup loading behavior is configurable per service through environment variables.

- `GA_ASR_AUTO_LOAD_ON_STARTUP` (`1`/`0`)
  - `1`: autoload ASR model on ASR service startup
  - `0`: do not autoload ASR model
- `GA_ASR_WAIT_FOR_READY_ON_STARTUP` (`1`/`0`, default `0`)
  - `1`: run an ASR readiness monitor (`/asr/ready`) in background when autoload is enabled
  - `0`: skip ASR readiness monitoring on startup
- `GA_ASR_READY_TIMEOUT_SECONDS` (default `1800`)
- `GA_ASR_DEVICE_MAP` (default `auto`)
- `GA_ASR_WARMUP_ON_LOAD` (`1`/`0`, default `1`)
  - `1`: runs a representative warmup transcription using `GA_ASR_WARMUP_AUDIO_PATH`
  - `0`: skips warmup (first real ASR call may be slower)
- `GA_ASR_WARMUP_AUDIO_PATH` (default `/app/asr-service/warmup.webm`)
- `GA_ASR_WARMUP_LANGUAGE` (optional; blank by default)
- `GA_ASR_WARMUP_LOG_TEXT_MAX_CHARS` (default `320`; caps logged warmup transcript length in startup logs)
- `GA_TTS_AUTO_LOAD_ON_STARTUP` (`1`/`0`)
  - `1`: autoload TTS model on TTS service startup
  - `0`: do not autoload TTS model
- `GA_TTS_WAIT_FOR_READY_ON_STARTUP` (`1`/`0`, default `0`)
  - `1`: run a TTS readiness monitor (`/tts/ready`) in background when autoload is enabled
  - `0`: skip TTS readiness monitoring on startup
- `GA_TTS_READY_TIMEOUT_SECONDS` (default `1800`)
- `GA_TTS_DEFAULT_MODEL_PATH` (default `VibeVoice-1.5B`)
- `GA_TTS_DEFAULT_MODEL_ID` (default `microsoft/VibeVoice-1.5B`)
- `GA_TTS_TOKENIZER_PATH` (default `Qwen2.5-1.5B-tokenizer`)
- `GA_TTS_TOKENIZER_ID` (default `Qwen/Qwen2.5-1.5B`)
- `GA_TTS_DEVICE_MAP` (default `auto`)
- `GA_TTS_DTYPE` (default `bfloat16`)
- `GA_EMB_AUTO_LOAD_ON_STARTUP` (`1`/`0`)
  - `1`: autoload embeddings model on startup
  - `0`: do not autoload embeddings model
- `GA_EMB_WARMUP_ON_LOAD` (`1`/`0`, default `1`)
  - `1`: run embedding warmup on model load
  - `0`: skip warmup for manual loads (autoload still forces warmup)
- `GA_EMB_WAIT_FOR_READY_ON_STARTUP` (`1`/`0`, default `0`)
  - `1`: run an embeddings readiness monitor (`/emb/ready`) in background when autoload is enabled
  - `0`: skip embeddings readiness monitoring on startup
- `GA_EMB_READY_TIMEOUT_SECONDS` (default `1800`)
- `GA_EMB_MODEL_DIR` (default `/models-local/emb`)
- `GA_EMB_DEFAULT_MODEL_PATH` (default `harrier-oss-v1-0.6b`)
- `GA_EMB_DEFAULT_MODEL_ID` (default `microsoft/harrier-oss-v1-0.6b`)
- `GA_SD_AUTO_LOAD_ON_STARTUP` (`1`/`0`)
  - `1`: run SD warmup generation on SD service startup (primes generation path)
  - `0`: skip SD warmup generation
- `GA_SD_WARMUP_PROMPT` (default `startup-warmup`)
- `GA_SD_WARMUP_SIZE` (default `512x512`)
- `GA_SD_WARMUP_STEPS` (default `1`)
- `GA_SD_WARMUP_OUTPUT_FORMAT` (default `png`)
- `GA_SD_SERVER_PATH` (default `/usr/local/bin/sd-server`)
- `GA_SD_ENGINE_HOST` (default `127.0.0.1`)
- `GA_SD_ENGINE_PORT` (default `18083`)
- `GA_SD_ENGINE_READY_TIMEOUT_SECONDS` (default `1800`)
- `GA_SD_ENGINE_REQUEST_TIMEOUT_SECONDS` (default `120`)
  - per-request HTTP timeout used for sd-server submit/poll calls
- `GA_SD_POLL_INTERVAL_SECONDS` (default `0.25`)
- `GA_SD_WARMUP_REQUEST_TIMEOUT_SECONDS` (default `180`)
  - request timeout override used specifically for startup/manual warmup calls
- `GA_SD_WARMUP_FAIL_OPEN_ON_STARTUP` (`1`/`0`, default `1`)
  - `1`: keep SD wrapper alive when startup warmup fails; retry with `POST /sd/admin/warmup`
  - `0`: fail startup if warmup fails
- `GA_SD_WAIT_FOR_READY_ON_STARTUP` (`1`/`0`, default `0`)
  - `1`: run an SD readiness monitor (`/sd/health`) in background during startup
  - `0`: skip SD readiness monitoring on startup
- `GA_SD_READY_TIMEOUT_SECONDS` (default `1800`)
- `GA_SD_CUDA_VISIBLE_DEVICES` (optional explicit SD GPU pinning; current deployment uses `1`)

Default compose behavior starts gateway-backed services in parallel. Optional readiness checks are non-blocking monitors so one service startup does not block others.

## Extending the Image

### Add Python packages

Add package install lines in both backend Python dependency builder stages (`pydeps-cpu-builder` and `pydeps-cuda13-builder`) for Python dependencies. Add OS-level runtime-only packages in both dependency runtime stages (`deps-cpu` and `deps-cuda13`).

### Add runtime services

Update both final stages plus `entrypoint.sh`:

1. Add binaries/install steps
2. Start/monitor process in `entrypoint.sh`
3. Update gateway route prefix mapping in `nginx.conf`
4. Update `EXPOSE` / health checks
5. Update compose port mappings as needed

## Key Constraints and Decisions

- Use upstream `llama.cpp:server` / `llama.cpp:server-cuda13` (not `full`) to avoid unnecessary image bloat.
- Use one Python 3.11 venv (`/opt/venv`) for project and ASR dependencies to stay compliant with Ubuntu 24.04 PEP 668 behavior and avoid duplicate torch installation.
- Keep stable-diffusion model weights external to image layers and load them through mounted volumes.
- Keep shell scripts LF-only (`.gitattributes`) for Linux container compatibility.
- Keep `docker/.env` as the single source for compose runtime image selection.

