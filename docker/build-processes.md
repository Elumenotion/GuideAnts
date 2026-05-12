# GuideAnts Docker Build Processes

This document covers the active Docker build paths under `docker/` and explains how images are produced for local compose runs.

## 1) Active Images And Where They Come From

| Image / Service | Build Source | Built By | Used By |
|---|---|---|---|
| `guideants-ai-deps:<backend>-<hash12>` | `docker/build/guideants-ai/Dockerfile.<backend>` | `docker/build/build_guideants_ai.ps1` | cache/reuse layer for final GuideAnts AI image |
| `guideants-ai-deps:<backend>-cache` | `docker/build/guideants-ai/Dockerfile.<backend>` | `docker/build/build_guideants_ai.ps1` | stable local cache source for future deps rebuilds |
| `guideants-ai:<backend>-<YYDDD>.<HHmm>` | `docker/build/guideants-ai/Dockerfile.<backend>` | `docker/build/build_guideants_ai.ps1` | `guideants-ai` service (`GA_AI_CUDA_IMAGE` / `GA_AI_CPU_IMAGE` / `GA_AI_ROCM_IMAGE`) |
| `guideants-webapi-ui:<YYDDD>.<HHmm>` | `docker/build/webapi-ui/Dockerfile` | `docker/build/build_webapi_ui.ps1` | `guideants-webapi-ui` profile service (`GA_WEBAPI_UI_IMAGE`) |
| `mssql2025-express-fts` | `docker/build/mssql-fts/Dockerfile` | `docker/build/build_guideants_ai.ps1 -All` | `mssql-express` service |
| `plantuml-1.2025.2` | `docker/build/Sandboxes/PlantUml/dockerfile` | `docker/build/build_guideants_ai.ps1 -All` | `plantuml` service |
| `guideants-searxng:latest` | `docker/build/searxng/Dockerfile` | `docker compose build searxng` | `searxng` service (`GA_SEARXNG_IMAGE`) |

Notes:
- `docker/docker-compose.cuda.yml` and `docker/docker-compose.cpu.yml` reference image tags via `docker/.env` (`GA_AI_CUDA_IMAGE`, `GA_AI_CPU_IMAGE`, `GA_WEBAPI_UI_IMAGE`).
- `guideants-webapi-ui` is optional and only starts when compose profile `webapi-ui` is enabled.
- GitHub Actions now publish GHCR copies of the AI, PlantUML, SearXNG, webapi slim, and webapi mssql images without changing the local compose image-selection flow.

## GuideAnts AI Cache Requirements

The AI image is deliberately split into `deps-*` and `final-*` stages. These requirements must hold for local development builds:

- Heavy runtime dependencies belong in `deps-*`; app/service code and runtime wiring belong in `final-*`.
- `sd-cli` and `sd-server` are runtime dependencies, so they belong in `deps-*`, not `final-*`.
- A deps change may create a new `guideants-ai-deps:<backend>-<hash12>` image, but the hash change itself must not force Docker to rebuild every deps layer from scratch.
- When one deps instruction changes, Docker must still have a stable cache source for unchanged earlier deps layers and intermediate builder stages.
- The hash tag is for exact image selection. The stable `guideants-ai-deps:<backend>-cache` tag is for layer reuse across deps hash changes.
- `-RebuildBase` is the only normal path that intentionally disables this cache behavior.

The build script supports those requirements by tagging every deps build with both the hash tag and stable cache tag, importing the stable cache tag with `--cache-from`, and exporting deps cache with `mode=max` plus inline cache metadata.

## GHCR Publish Workflows

The repo publishes the following GHCR packages from GitHub Actions:

| GHCR package | Workflow | Notes |
|---|---|---|
| `ghcr.io/<owner>/guideants-ai-cpu` | `publish-guideants-ai-images.yml` | `final-cpu` target |
| `ghcr.io/<owner>/guideants-ai-cuda13` | `publish-guideants-ai-images.yml` | `final-cuda13` target |
| `ghcr.io/<owner>/mssql2025-express-fts` | `publish-mssql-fts-image.yml` | standalone SQL Server 2025 Express + FTS image used by GHCR compose stacks |
| `ghcr.io/<owner>/guideants-plantuml` | `publish-plantuml-image.yml` | includes staged `ScriptExecutionAgent` publish output |
| `ghcr.io/<owner>/guideants-searxng` | `publish-searxng-image.yml` | repo-root build context; upstream SearXNG base pinned by digest |
| `ghcr.io/<owner>/guideants-webapi-ui-slim` | `publish-slim-image.yml` | standalone API/UI image |
| `ghcr.io/<owner>/guideants-webapi-ui-mssql` | `publish-mssql-image.yml` | API/UI image with bundled SQL Server |

Publish workflow behavior:

- `publish-guideants-ai-images.yml` and `publish-plantuml-image.yml` trigger by manual dispatch
- `publish-mssql-fts-image.yml`, `publish-slim-image.yml`, and `publish-mssql-image.yml` also trigger on `main`/tag pushes when relevant files change
- all emit branch, tag, `sha-*`, and `latest` (for `main`) tags
- target `linux/amd64`
- push to GHCR with the repository `GITHUB_TOKEN`

## 2) GuideAnts AI Build (`build_guideants_ai.ps1`)

Run from repo root:

```powershell
pwsh .\docker\build\build_guideants_ai.ps1
```

Optional switches:

```powershell
pwsh .\docker\build\build_guideants_ai.ps1 -RebuildBase
pwsh .\docker\build\build_guideants_ai.ps1 -All
pwsh .\docker\build\build_guideants_ai.ps1 -RebuildBase -All
```

Script flow:
1. Prompts for backend (`CPU`, `CUDA 13`, or `ROCm`) and maps to Docker target (`final-cpu`, `final-cuda13`, or `final-rocm`).
2. Builds `src/server/ScriptExecutionAgent` with `dotnet publish`.
3. Stages publish output into `docker/build/guideants-ai/ScriptExecutionAgent`.
4. Copies backend-specific `requirements.txt` from sandbox folder, then strips `torch*` entries so torch stays backend-controlled in Dockerfile.
5. Computes a deterministic dependency hash from Dockerfile + dependency input files.
6. Builds or reuses `guideants-ai-deps:<backend>-<hash12>` from `deps-cpu` / `deps-cuda13` / `deps-rocm`.
7. Tags the same deps image as `guideants-ai-deps:<backend>-cache`, so future deps rebuilds can reuse unchanged layers from the previous deps image even when the hash changes.
8. Runs final build with `--target <final-target>`, `--cache-from <deps-image>`, and backend-specific deps image build args.
9. Cleans staged artifacts (`ScriptExecutionAgent`, staged `requirements.txt`).
10. Writes `GA_AI_CUDA_IMAGE=<new tag>`, `GA_AI_CPU_IMAGE=<new tag>`, or `GA_AI_ROCM_IMAGE=<new tag>` into `docker/.env`.
11. If `-All` is set, also builds PlantUML and MSSQL FTS images, then invokes `build_webapi_ui.ps1` to build the compose-used WebAPI+UI image.

## 3) AI Multi-Stage Build (Why It Matters)

The AI build uses backend-specific Dockerfiles, each split into runtime base, Python dependency build, dependency runtime image, and final app layer:

- CPU lane: `runtime-cpu-base` -> `pydeps-cpu-builder` -> `deps-cpu` -> `final-cpu`
- CUDA lane: `runtime-cuda13-base` -> `pydeps-cuda13-builder` -> `deps-cuda13` -> `final-cuda13`
- ROCm lane: `runtime-rocm-base` -> `pydeps-rocm-builder` -> `deps-rocm` -> `final-rocm`

What is in `pydeps-*` (heavy Python build stage):
- Python 3.11 + a single shared venv (`/opt/venv`)
- build toolchain (`build-essential`, `cmake`, headers)
- backend torch install (CPU index or CUDA 13 index) once per image variant
- pip install of ASR/TTS/Emb + filtered app requirements in the same venv

What is in `deps-*` (heavy runtime dependency layer, tagged for reuse):
- runtime OS deps (`ffmpeg`, `nginx`, `graphviz`, etc.)
- copied `/opt/venv` from `pydeps-*`
- backend-specific `sd-cli` and `sd-server` binaries copied from `sd-cli-*-builder`
- Playwright package + Chromium install

What is in `final-*` (light app/service layer):
- `ScriptExecutionAgent` publish artifacts copy
- service/runtime scripts and config (`nginx.conf`, `entrypoint.sh`, `start-*.sh`, router seed)
- service app folders (`llama-admin-service/`, `asr-service/`, `sd-service/`, `tts-service/`, `emb-service/`, `media-service/`)
- health check and entrypoint wiring

Why this is the clean extension path:
- Most service-extension changes (gateway routes, startup logic, agent publish output, runtime scripts) are in `final-*`.
- Heavy dependency rebuilds are avoided by reusing hash tags for exact deps identity and stable `guideants-ai-deps:<backend>-cache` tags for layer reuse across deps hash changes.
- The deps build exports `mode=max` BuildKit cache plus inline cache metadata, so intermediate builder stages can be reused when a later deps instruction changes.
- `requirements.txt` is only reinstalled when dependency inputs change (or when `-RebuildBase` is used).

This is the main optimization that keeps AI image iteration fast while still allowing new services/processes to be added cleanly.

## 4) GuideAnts WebAPI + UI Build (`build_webapi_ui.ps1`)

Run:

```powershell
pwsh .\docker\build\build_webapi_ui.ps1
```

Useful switches:

```powershell
pwsh .\docker\build\build_webapi_ui.ps1 -NoCache
pwsh .\docker\build\build_webapi_ui.ps1 -UseAppBuildCache
pwsh .\docker\build\build_webapi_ui.ps1 -NoRecreate
```

Dockerfile stages:
- `ui-build` (Node 20): installs npm deps and builds browser UI.
- `api-build` (.NET SDK 8): restores and publishes `GuideAntsApi`.
- `runtime` (Playwright .NET image): installs runtime deps, copies API publish + UI static bundle.

Script behavior:
1. Builds timestamped tag `guideants-webapi-ui:<YYDDD>.<HHmm>`.
2. By default, disables cache for `ui-build` and `api-build` stages (`--no-cache-filter`) for deterministic app rebuilds.
3. Writes/repairs `GA_WEBAPI_UI_IMAGE` in `docker/.env`.
4. Recreates `guideants-webapi-ui` container unless `-NoRecreate` is passed.

## 5) Additional Image Builds Triggered By `-All`

When `build_guideants_ai.ps1 -All` is used:

- PlantUML image:
  - Dockerfile: `docker/build/Sandboxes/PlantUml/dockerfile`
  - Tag: `plantuml-1.2025.2`
  - Includes Java + PlantUML jar + ASP.NET Core runtime + ScriptExecutionAgent payload

- MSSQL FTS image:
  - Dockerfile: `docker/build/mssql-fts/Dockerfile`
  - Tag: `mssql2025-express-fts`
  - Adds `mssql-server-fts` package to SQL Server 2025 base image

- WebAPI+UI image:
  - Built by invoking: `docker/build/build_webapi_ui.ps1 -NoRecreate` (or `-NoCache -NoRecreate` when `-RebuildBase` is used)
  - Dockerfile: `docker/build/webapi-ui/Dockerfile`
  - Tag: `guideants-webapi-ui:<YYDDD>.<HHmm>` (written to `GA_WEBAPI_UI_IMAGE`)
  - This matches the image used by `docker-compose.cuda.yml` for the `guideants-webapi-ui` service

## 6) Compose Usage After Builds

From `docker/`:

```powershell
docker compose -f docker-compose.cuda.yml up -d guideants-ai mssql-express plantuml
```

Recreate the WebAPI/UI service after a build:

```powershell
docker compose -f docker-compose.cuda.yml up -d --no-deps --force-recreate guideants-webapi-ui
```

Because the build scripts update `.env`, compose picks up the newest `GA_AI_CUDA_IMAGE`, `GA_AI_CPU_IMAGE`, and `GA_WEBAPI_UI_IMAGE` automatically.

## 7) SQL Recovery Model On New Installs

- On first app startup, `GuideAntsApi` creates the configured SQL catalog when missing (`SqlServerDatabaseInitializer`).
- New catalogs are immediately set to `RECOVERY SIMPLE` so transaction logs auto-truncate and local installs do not require log-backup maintenance.
- Existing catalogs are not modified automatically.

Verify after first boot:

```powershell
docker exec guideants-mssql-express-1 /opt/mssql-tools18/bin/sqlcmd `
  -S localhost -U sa -P "YourStrong!Passw0rd" -C `
  -Q "SELECT name, recovery_model_desc FROM sys.databases WHERE name = 'guideants-dev';"
```

## 8) Sandbox/Experimental Dockerfiles

The following folders contain sandbox/reference builds and are not first-class compose entrypoints by default:

- `docker/build/Sandboxes/python311TorchCPU`
- `docker/build/Sandboxes/python311TorchCUDA`
- `docker/build/Sandboxes/python311TorchMARM64`
- `docker/build/Sandboxes/whiper-large`
- `docker/build/Sandboxes/Net8AndPython`

These are useful for experimentation and dependency prototyping, while production/local-stack builds should follow Sections 2-6 above.

