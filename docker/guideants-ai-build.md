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

The `cpu`, `cuda13`, `rocm`, and `vulkan` variants are full local AI images. The `vulkan` variant is vendor-neutral: a single image GPU-accelerates the LLM and image-generation paths on NVIDIA, AMD, and Intel via Vulkan (torch stays on CPU wheels, so ASR/TTS/embeddings run on CPU). See `docker/guideants-ai-vulkan.md` for the full Vulkan design and usage. The `slim` variant is intentionally sandbox-oriented: it starts the Python `ScriptExecutionAgent` and the non-model media service, but it does not start llama, llama-admin, ASR, TTS, SD, or embeddings. Do not confuse `guideants-ai slim` with `guideants-webapi-ui-slim`; the latter is the existing API/UI image for split-stack deployments.

Gateway route prefixes:

- `/sandbox/*` -> ScriptExecutionAgent
- `/llama-cpp/*` -> llama-server
- `/asr/*` -> local ASR service
- `/sd/*` -> local stable-diffusion.cpp wrapper service
- `/tts/*` -> local TTS service
- `/emb/*` -> local embeddings service

The build system is optimized for local iterative development:

- one build script
- backend-specific Dockerfiles (`Dockerfile.cpu`, `Dockerfile.cuda`, `Dockerfile.rocm`, `Dockerfile.slim`, `Dockerfile.vulkan`)
- backend selected interactively (CPU, CUDA 13, ROCm, slim, or Vulkan)
- deterministic dependency-image tags derived from dependency file hashes

## Build Cache Requirements

These requirements are intentional and must be preserved when changing the AI Dockerfiles or build script:

- Heavy dependency work belongs in `deps-*`; application code, service scripts, gateway config, and startup wiring belong in `final-*`.
- `sd-cli` and `sd-server` are runtime dependencies and must be produced before, and copied into, `deps-*`.
- A dependency input change may require a new hash-tagged deps image, but it must not make Docker rebuild every deps layer from scratch solely because the hash tag changed.
- When one deps instruction changes, Docker must still be able to reuse unchanged earlier layers from the previous deps image.
- The changing hash tag must identify the deps contents used by the final image; it must not be the only cache identity available to future deps builds.
- `-RebuildBase` is the explicit escape hatch for intentionally ignoring cache. Normal builds must preserve layer reuse.

The local build script enforces this with two deps tags per backend:

- `guideants-ai-deps:<backend>-<hash12>` is the exact deps image selected by dependency inputs.
- `guideants-ai-deps:<backend>-cache` is the stable moving cache anchor used by later deps builds.

The deps build exports local BuildKit cache metadata with `mode=min` by default to optimize developer throughput. This reduces cache export overhead on local machines while preserving the stable deps cache tag (`guideants-ai-deps:<backend>-cache`) for reuse across dependency hash changes.

If the active Buildx driver does not support cache export (for example `docker`), the build scripts now automatically continue without `--cache-to` and emit a warning. This avoids hard failures on machines with different Docker Buildx defaults.

## GHCR Publishing

GitHub Actions publish the runtime images to GHCR as separate packages:

- `ghcr.io/<owner>/guideants-ai-cpu`
- `ghcr.io/<owner>/guideants-ai-cuda13`
- `ghcr.io/<owner>/guideants-ai-rocm`
- `ghcr.io/<owner>/guideants-ai-slim`
- `ghcr.io/<owner>/guideants-ai-vulkan`

Workflow:

- `.github/workflows/publish-guideants-ai-images.yml`

Manual dispatch options:

- `all` publishes all variants
- `cpu` publishes only the CPU image
- `cuda13` publishes only the CUDA 13 image
- `rocm` publishes only the ROCm image
- `slim` publishes only the sandbox-oriented AI image
- `vulkan` publishes only the vendor-neutral Vulkan image

Workflow implementation details:

- publishes `src/server/ScriptExecutionAgent` with `dotnet publish`
- stages that output into `docker/build/guideants-ai/ScriptExecutionAgent`
- copies backend-specific sandbox requirements into `docker/build/guideants-ai/requirements.txt`
- strips `torch`, `torchaudio`, `torchvision`, and `torchtext` so the Dockerfile remains the single owner of backend torch installation
- builds `final-cpu`, `final-cuda13`, `final-rocm`, `final-slim`, or `final-vulkan`
- runs by manual GitHub Actions dispatch and pushes branch, `sha-*`, and `latest` tags to GHCR
- uses GitHub Actions cache scopes per backend instead of publishing `guideants-ai-deps:*` cache images

## Current Design

### Backend-Specific Dockerfiles

`docker/build/guideants-ai/Dockerfile.cpu`, `Dockerfile.cuda`, `Dockerfile.rocm`, and `Dockerfile.vulkan` contain backend-specific builder, dependency, and runtime stages:

- `sd-cli-cpu-builder` -> builds CPU `stable-diffusion.cpp` binaries (`sd-cli` + `sd-server`)
- `sd-cli-cuda-builder` -> builds CUDA `stable-diffusion.cpp` binaries (`sd-cli` + `sd-server`)
- `sd-cli-rocm-builder` -> builds ROCm/HIP `stable-diffusion.cpp` binaries (`sd-cli` + `sd-server`)
- `sd-cli-vulkan-builder` -> builds Vulkan `stable-diffusion.cpp` binaries (`sd-cli` + `sd-server`)
- `runtime-cpu-base` -> OS/runtime base on `ghcr.io/ggml-org/llama.cpp:server`
- `pydeps-cpu-builder` -> Python dependency build stage (includes build toolchain)
- `deps-cpu` -> runtime dependency image (no compiler toolchain)
- `final-cpu` -> runtime image on top of `deps-cpu` (or an externally tagged deps image)

- `runtime-cuda13-base` -> OS/runtime base on `ghcr.io/ggml-org/llama.cpp:server-cuda13`
- `pydeps-cuda13-builder` -> Python dependency build stage (includes build toolchain)
- `deps-cuda13` -> runtime dependency image (no compiler toolchain)
- `final-cuda13` -> runtime image on top of `deps-cuda13` (or an externally tagged deps image)

- `runtime-rocm-base` -> OS/runtime base on `ghcr.io/ggml-org/llama.cpp:server-rocm`
- `pydeps-rocm-builder` -> Python dependency build stage (includes build toolchain)
- `deps-rocm` -> runtime dependency image (no compiler toolchain)
- `final-rocm` -> runtime image on top of `deps-rocm` (or an externally tagged deps image)

- `runtime-vulkan-base` -> OS/runtime base on `ghcr.io/ggml-org/llama.cpp:server-vulkan` (Ubuntu 26.04), plus the universal GPU driver layer (`mesa-vulkan-drivers` + libglvnd/EGL libs) that makes one image work on NVIDIA, AMD, and Intel; also installs Node.js 22 (`npx`) for `mcp+sandbox://` MCP servers, matching the other full AI images
- `pydeps-vulkan-builder` -> Python dependency build stage (includes build toolchain)
- `deps-vulkan` -> runtime dependency image (no compiler toolchain)
- `final-vulkan` -> runtime image on top of `deps-vulkan` (or an externally tagged deps image)

The script builds one target with `--target` based on prompt choice:

- CPU choice -> `--target final-cpu`
- CUDA choice -> `--target final-cuda13`
- ROCm choice -> `--target final-rocm`
- Vulkan choice -> `--target final-vulkan`

The backend choice is baked into the dependency image:

- `deps-cpu` gets `sd-cli` + `sd-server` from `sd-cli-cpu-builder`
- `deps-cuda13` gets CUDA-enabled `sd-cli` + `sd-server` from `sd-cli-cuda-builder`
- `deps-rocm` gets HIP-enabled `sd-cli` + `sd-server` from `sd-cli-rocm-builder`
- `deps-vulkan` gets Vulkan-enabled `sd-cli` + `sd-server` from `sd-cli-vulkan-builder`

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
- The deps build exports a `mode=min` local BuildKit cache to keep local iteration fast while still enabling reuse across deps hash changes.
- The build script computes a hash from dependency inputs and tags dependency images:
  - `guideants-ai-deps:cpu-<hash12>`
  - `guideants-ai-deps:cuda13-<hash12>`
- Each successful deps build also updates a stable backend cache tag:
  - `guideants-ai-deps:cpu-cache`
  - `guideants-ai-deps:cuda13-cache`
  - `guideants-ai-deps:rocm-cache`
  - `guideants-ai-deps:vulkan-cache`
- If the matching hash-tagged deps image exists, the final build reuses it via backend-specific build args.
- If the deps image is missing, the script rebuilds it with the stable cache tag as `--cache-from` so unchanged deps layers can be reused across hash changes.
- `-RebuildBase` still forces no-cache builds for dependency and final targets.

#### Troubleshooting: `Cache export is not supported for the docker driver`

If the build fails with:

`Cache export is not supported for the docker driver.`

the `--cache-to` export path is unsupported in the current Buildx/engine configuration.
Use this recovery sequence on Windows Docker Desktop:

1. `docker context use desktop-linux`
2. `docker buildx use desktop-linux`
3. In Docker Desktop settings, enable **containerd image store** ("Use containerd for pulling and storing images")
4. Restart Docker Desktop
5. Verify with `docker buildx inspect --bootstrap`
6. Re-run `build_guideants_ai.ps1`

## Script Behavior

Build script: `docker/build/build_guideants_ai.ps1`

Supported switches:

- none: prompt for backend, build final GuideAnts AI image
- `-RebuildBase`: prompt for backend, force rebuild without cache
- `-All`: build GuideAnts AI, PlantUML, MSSQL, and the compose-used WebAPI+UI image
- `-RebuildBase -All`: full no-cache GuideAnts AI build plus additional images

### Troubleshooting: `/usr/bin/env: 'bash\r': No such file or directory`

If `guideants-webapi-ui` or `guideants-searxng` containers restart with:

`/usr/bin/env: 'bash\r': No such file or directory`

the image contains entrypoint scripts with CRLF line endings.

Machine-specific cause on Windows:
- Git may be configured with `core.autocrlf=true` on one machine and not others.
- With only generic `text=auto`, `.sh` files can be checked out as CRLF and copied into Linux images as `bash\r`.

Repository safeguard:
- Root `.gitattributes` enforces LF for shell scripts: `*.sh text eol=lf`.

Recommended rebuild commands:
1. WebAPI+UI:
   - `pwsh .\docker\build\build_webapi_ui.ps1 -NoCache`
2. SearXNG:
   - `docker compose -f docker/docker-compose.cuda.yml build searxng`
   - `docker compose -f docker/docker-compose.cuda.yml up -d --no-deps --force-recreate searxng`

Cache note:
- `-RebuildBase` forces no-cache for GuideAnts AI dependency/final targets.
- In `-All`, SearXNG currently builds via a normal `docker build` path.
- If SearXNG appears stale, run:
  - `docker rmi guideants-searxng:latest`
  - rerun the build command/script.

### Build flow

1. Prompt backend (`CPU`, `CUDA 13`, `ROCm`, `slim`, or `Vulkan`)
2. Build/publish `src/server/ScriptExecutionAgent`
3. Stage `ScriptExecutionAgent` and filtered `requirements.txt` into Docker build context
4. Compute dependency hash from backend Dockerfile + requirement inputs
5. Build/reuse backend-specific dependency image (`deps-cpu`, `deps-cuda13`, `deps-rocm`, `deps-slim`, or `deps-vulkan`)
6. Tag the deps image with both the hash tag and stable backend cache tag
7. Build final runtime target (`final-cpu`, `final-cuda13`, `final-rocm`, `final-slim`, or `final-vulkan`) using the dependency image
8. Clean staged artifacts
9. Write `GA_AI_CUDA_IMAGE=<final-tag>`, `GA_AI_CPU_IMAGE=<final-tag>`, `GA_AI_ROCM_IMAGE=<final-tag>`, `GA_AI_SLIM_IMAGE=<final-tag>`, or `GA_AI_VULKAN_IMAGE=<final-tag>` to `docker/.env`
10. Optionally build PlantUML/MSSQL and invoke `build_webapi_ui.ps1` if `-All` was passed

### Buildx Driver Recommendation

For reliable local cache export and best build times, use a `docker-container` Buildx builder:

```powershell
docker buildx create --name guideants-builder --driver docker-container --use
docker buildx inspect --bootstrap
```

Without this setup, builds still succeed, but cache export is disabled for that run and a warning is shown.

## File Layout

```text
docker/
  .env
  docker-compose.cuda.yml
  docker-compose.vulkan.yml
  guideants-ai-build.md
  guideants-ai-vulkan.md
  build/
    build_guideants_ai.ps1
    guideants-ai/
      Dockerfile.cpu
      Dockerfile.cuda
      Dockerfile.rocm
      Dockerfile.vulkan
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
      python311TorchROCM/requirements.txt
      python311TorchVulkan/requirements.txt
```

## Image Tagging

Two image categories are tagged:

- dependency images (cache/reuse targets):
  - `guideants-ai-deps:<backend>-<hash12>`
  - `guideants-ai-deps:<backend>-cache`
- final runtime images (compose/runtime target):
  - `guideants-ai:<backend>-<YYDDD>.<HHmm>`

Examples:

- `guideants-ai:cuda13-26096.1715`
- `guideants-ai:cpu-26096.1715`
- `guideants-ai:vulkan-26096.1715`
- `guideants-ai-deps:cuda13-89ab1c2d3e4f`
- `guideants-ai-deps:cuda13-cache`
- `guideants-ai-deps:cpu-1a2b3c4d5e6f`
- `guideants-ai-deps:vulkan-cache`

`GA_AI_CUDA_IMAGE`, `GA_AI_CPU_IMAGE`, `GA_AI_ROCM_IMAGE`, or `GA_AI_VULKAN_IMAGE` in `docker/.env` is always updated to the latest built final tag.

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
DOCLING_SERVE_CPU_IMAGE=quay.io/docling-project/docling-serve-cpu:v1.21.0
DOCLING_SERVE_CUDA_IMAGE=quay.io/docling-project/docling-serve-cu130:v1.21.0
DOCLING_SERVE_MAX_SYNC_WAIT=600
DOCLING_SERVE_MAX_FILE_SIZE=524288000
DOCLING_SERVE_ENG_LOC_NUM_WORKERS=2
DOCLING_NUM_THREADS=4
# Optional shared secret for docling-serve (see docker/docling-serve.env.example — do not set empty)
# DOCLING_SERVE_API_KEY=your-secret-here
# GA_DOCLING_CUDA_VISIBLE_DEVICES=
```

See `docker/docling-serve.env.example` for the full server-side variable list (logging, OTEL, GPU device, optional model artifacts path).

`DOCLING_SERVE_MAX_SYNC_WAIT` is in seconds and only affects Docling synchronous endpoints.
GuideAnts markdown extraction uses Docling async endpoints.

`guideants-webapi-ui` resolves Docling through `LocalServiceHosts__DocumentIntelligenceBaseUrl=http://docling-serve:5001`.
When `DOCLING_SERVE_API_KEY` is configured with a non-empty value, add it to the `docling-serve` service environment and set `DocumentIntelligence__DoclingApiKey` on `guideants-webapi-ui` to the same value so the API sends `X-Api-Key` on submit/poll/result calls. Do not pass an empty `DOCLING_SERVE_API_KEY` — docling-serve v1.21+ fails startup validation.

Infrastructure probes for `LocalServiceHosts:DocumentIntelligenceBaseUrl` hit `{baseUrl}/version`.

### Docling Models Included by `docling-serve` Images

For `quay.io/docling-project/docling-serve-*:v1.21.0`, model artifacts are baked into the image under:

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

### Docling conversion options (GuideAnts Settings → Document Intelligence)

`GuideAntsApi` submits `/v1/convert/file/async` multipart requests with `to_formats=md`
plus any configured `DocumentIntelligence:Docling*` fields (OCR, table mode, PDF backend,
image export mode, pipeline, enrichment flags, picture-description preset). It polls
`/v1/status/poll/{task_id}` and fetches `/v1/result/{task_id}`.

When unset, Docling server defaults apply. With v1.21.0 that typically means:

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

Default expected subdirectory:

- `chatterbox` (catalog entry; downloaded from `ResembleAI/chatterbox` via Settings → Speech)

If these files are missing, the `/tts/admin/load` and `/tts/synthesize`
endpoints fail until artifacts are present. On a fresh host, either run
the migration script or register the models through Settings → Speech.
Local TTS uses the curated Chatterbox catalog; reference voices come from
the baked voice pack (`VoiceName` enum in settings).

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

- `CUDA_VISIBLE_DEVICES` (optional; sourced from active env)
  - global GPU ordering for processes in the container when set (example `1,0` maps `host GPU 1 -> cuda:0`, `host GPU 0 -> cuda:1`)
- Optional per-service CUDA pinning (comma-separated physical GPU ids; empty value means inherit global ordering):
  - `GA_LLAMA_CUDA_VISIBLE_DEVICES`
  - `GA_ASR_CUDA_VISIBLE_DEVICES`
  - `GA_TTS_CUDA_VISIBLE_DEVICES`
  - `GA_EMB_CUDA_VISIBLE_DEVICES`
- `GA_ASR_AUTO_LOAD_ON_STARTUP` (`1`/`0`)
  - `1`: autoload ASR model on ASR service startup
  - `0`: do not autoload ASR model
- `GA_ASR_WAIT_FOR_READY_ON_STARTUP` (`1`/`0`, default `0`)
  - `1`: run an ASR readiness monitor (`/asr/ready`) in background when autoload is enabled
  - `0`: skip ASR readiness monitoring on startup
- `GA_ASR_READY_TIMEOUT_SECONDS` (default `1800`)
- `GA_ASR_DEVICE_MAP` (default `auto`)
- `GA_ASR_BACKEND` (default `cuda`; must be `cpu`/`cuda`/`vulkan` and must match the `ENGINE_ENABLE_*` flags the image's `audiocpp_server` was built with — `cpu` for the CPU flavor, `vulkan` for the Vulkan/ROCm flavors, `cuda` for the CUDA flavor)
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
- `GA_TTS_DEFAULT_MODEL_PATH` (default `chatterbox`)
- `GA_TTS_DEFAULT_MODEL_ID` (default `chatterbox` catalog id; download resolves to `ResembleAI/chatterbox`)
- `GA_TTS_DEVICE_MAP` (legacy; native engine ignores)
- `GA_TTS_BACKEND` (default `cuda`; must be `cpu`/`cuda`/`vulkan` and must match the `ENGINE_ENABLE_*` flags the image's `audiocpp_server` was built with — `cpu` for the CPU flavor, `vulkan` for the Vulkan/ROCm flavors, `cuda` for the CUDA flavor)
- `GA_TTS_DTYPE` (legacy; native engine ignores)
- `GA_TTS_VOICE` (default reference voice from voice pack, e.g. `en_us_cv_001`)
- `GA_TTS_LANG_CODE` (inferred from voice pack selection)
- `GA_TTS_SPEED` (default `1.0`)
  - Local TTS runs Chatterbox via `audiocpp_server`. Voice selection uses the
    curated reference-voice pack exposed in the settings UI.
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
- `GA_SD_CUDA_VISIBLE_DEVICES` (optional explicit SD physical GPU pinning; empty value means inherit global ordering)
- `GA_SD_VK_VISIBLE_DEVICES` (optional SD-only Vulkan device selector; empty value means inherit `GGML_VK_VISIBLE_DEVICES`)
- `GA_EMB_TARGET_DEVICES` (default `cuda:0,cuda:1`; logical indices interpreted after CUDA remapping)

Default compose behavior starts gateway-backed services in parallel. Optional readiness checks are non-blocking monitors so one service startup does not block others.

## CUDA Visibility Verification Matrix

Use this checklist after changing GPU routing vars. The expected behavior is:

- global `CUDA_VISIBLE_DEVICES` controls container-wide logical GPU order
- `GA_*_CUDA_VISIBLE_DEVICES` (when set) overrides only that service process
- empty `GA_*_CUDA_VISIBLE_DEVICES` inherits global ordering

| Path | Setup | Verify commands | Expected pass criteria |
| --- | --- | --- | --- |
| Compose (local CUDA) | `docker compose -f docker/docker-compose.cuda.yml up -d` | `docker logs guideants-ai` and `docker logs docling-serve` | `guideants-ai` sees expected global ordering; service-specific pins only apply where configured |
| Compose (GHCR CUDA) | `docker compose -f docker/docker-compose.ghcr-cuda13.yml up -d` | `docker logs guideants-ai` and runtime API smoke calls | Same behavior as local CUDA stack |
| Standalone PS1 | `pwsh .\\docker\\llama\\run\\start-llama-server.ps1` | `docker logs guideants-ai` | No hardcoded SD pin; values come from params/env only |

### Runtime Checks

1. **llama**
   - look for `device_info` lines and confirm `CUDA0/CUDA1` mapping matches expected logical order.
2. **SD**
   - look for `ggml_cuda_init` and confirm visible devices match either global order (inherit) or `GA_SD_CUDA_VISIBLE_DEVICES` (override).
3. **ASR/TTS**
   - confirm startup logs show CUDA availability and successful model load on intended device ordering.
4. **Embeddings**
   - if `GA_EMB_DEVICE=cuda-multi`, verify `GA_EMB_TARGET_DEVICES` is interpreted as logical indices after remap.

### Suggested Smoke Matrix

| Case | `CUDA_VISIBLE_DEVICES` | Service override | Expected result |
| --- | --- | --- | --- |
| A (inherit all) | `1,0` | all `GA_*_CUDA_VISIBLE_DEVICES` empty | all services follow remapped order |
| B (SD pin only) | `1,0` | `GA_SD_CUDA_VISIBLE_DEVICES=0` | SD pinned to physical GPU 0, others inherit |
| C (llama pin only) | `1,0` | `GA_LLAMA_CUDA_VISIBLE_DEVICES=1` | llama pinned to physical GPU 1, others inherit |
| D (no global remap) | empty | targeted `GA_*_CUDA_VISIBLE_DEVICES` set | only explicitly pinned services are constrained |

## Extending the Image

### Add Python packages

Add package install lines in every backend Python dependency builder stage (`pydeps-cpu-builder`, `pydeps-cuda13-builder`, `pydeps-rocm-builder`, and `pydeps-vulkan-builder`) for Python dependencies. Add OS-level runtime-only packages in every dependency runtime stage (`deps-cpu`, `deps-cuda13`, `deps-rocm`, and `deps-vulkan`).

### Add runtime services

Update both final stages plus `entrypoint.sh`:

1. Add binaries/install steps
2. Start/monitor process in `entrypoint.sh`
3. Update gateway route prefix mapping in `nginx.conf`
4. Update `EXPOSE` / health checks
5. Update compose port mappings as needed

## Key Constraints and Decisions

- Use upstream `llama.cpp:server` / `llama.cpp:server-cuda13` / `llama.cpp:server-rocm` / `llama.cpp:server-vulkan` (not `full`) to avoid unnecessary image bloat.
- The Vulkan image is vendor-neutral and runs on **both Windows (Docker Desktop) and native Linux** from one image. The image bakes all three drivers — Mesa **dzn** (Vulkan-on-D3D12, built from source), Mesa **RADV/ANV** (`mesa-vulkan-drivers`), and libglvnd/EGL for the NVIDIA ICD injection — and a single env-driven `docker-compose.vulkan.yml` selects the path per host: Windows → dzn over `/dev/dxg` (runs from git bash, no WSL distro; the nvidia runtime is *not* used there because on WSL2 it gives CUDA but no Vulkan ICD → CPU `llvmpipe`); native Linux AMD/Intel → RADV/ANV over `/dev/dri`; native Linux NVIDIA → nvidia-container-toolkit (`runtime: nvidia` + `graphics` cap). The installer's `select_vulkan_runtime()` sets the `GA_VULKAN_*` env automatically. The `server-vulkan` base is Ubuntu 26.04 and needs a few build workarounds (`pkg-config`, a GCC-14 `CFLAGS` relaxation, and a Playwright `os-release` spoof) — see `docker/guideants-ai-vulkan.md`.
- Use one Python 3.11 venv (`/opt/venv`) for project and ASR dependencies to stay compliant with Ubuntu 24.04 PEP 668 behavior and avoid duplicate torch installation.
- Keep stable-diffusion model weights external to image layers and load them through mounted volumes.
- Keep shell scripts LF-only (`.gitattributes`) for Linux container compatibility.
- Keep `docker/.env` as the single source for compose runtime image selection.
