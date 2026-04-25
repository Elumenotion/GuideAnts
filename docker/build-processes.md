# Waterfall Docker Build Processes

This document covers the active Docker build paths under `docker/` and explains how images are produced for local compose runs.

## 1) Active Images And Where They Come From

| Image / Service | Build Source | Built By | Used By |
|---|---|---|---|
| `guideants-ai-deps:<backend>-<hash12>` | `docker/build/guideants-ai/Dockerfile` (`deps-cpu` / `deps-cuda13`) | `docker/build/build_guideants_ai.ps1` | cache/reuse layer for final GuideAnts AI image |
| `guideants-ai:<backend>-<YYDDD>.<HHmm>` | `docker/build/guideants-ai/Dockerfile` | `docker/build/build_guideants_ai.ps1` | `guideants-ai` service (`GA_AI_IMAGE`) |
| `guideants-webapi-ui:<YYDDD>.<HHmm>` | `docker/build/webapi-ui/Dockerfile` | `docker/build/build_webapi_ui.ps1` | `guideants-webapi-ui` profile service (`GA_WEBAPI_UI_IMAGE`) |
| `mssql2025-express-fts` | `docker/build/mssql-fts/Dockerfile` | `docker/build/build_guideants_ai.ps1 -All` | `mssql-express` service |
| `plantuml-1.2025.2` | `docker/build/Sandboxes/PlantUml/dockerfile` | `docker/build/build_guideants_ai.ps1 -All` | `plantuml` service |
| `guideants-searxng:latest` | `docker/build/searxng/Dockerfile` | `docker compose build searxng` | `searxng` service (`GA_SEARXNG_IMAGE`) |

Notes:
- `docker/docker-compose.yml` references image tags via `docker/.env` (`GA_AI_IMAGE`, `GA_WEBAPI_UI_IMAGE`).
- `guideants-webapi-ui` is optional and only starts when compose profile `webapi-ui` is enabled.
- GitHub Actions now publish GHCR copies of the AI, PlantUML, SearXNG, webapi slim, and webapi mssql images without changing the local compose image-selection flow.

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
1. Prompts for backend (`CPU` or `CUDA 13`) and maps to Docker target (`final-cpu` or `final-cuda13`).
2. Builds `src/server/ScriptExecutionAgent` with `dotnet publish`.
3. Stages publish output into `docker/build/guideants-ai/ScriptExecutionAgent`.
4. Copies backend-specific `requirements.txt` from sandbox folder, then strips `torch*` entries so torch stays backend-controlled in Dockerfile.
5. Computes a deterministic dependency hash from Dockerfile + dependency input files.
6. Builds or reuses `guideants-ai-deps:<backend>-<hash12>` from `deps-cpu` / `deps-cuda13`.
7. Runs final build with `--target <final-target>`, `--cache-from <deps-image>`, and backend-specific deps image build args.
8. Cleans staged artifacts (`ScriptExecutionAgent`, staged `requirements.txt`).
9. Writes `GA_AI_IMAGE=<new tag>` into `docker/.env`.
10. If `-All` is set, also builds PlantUML and MSSQL FTS images, then invokes `build_webapi_ui.ps1` to build the compose-used WebAPI+UI image.

## 3) AI Multi-Stage Build (Why It Matters)

The AI Dockerfile has two backend lanes, each split into runtime base, Python dependency build, dependency runtime image, and final app layer:

- CPU lane: `runtime-cpu-base` -> `pydeps-cpu-builder` -> `deps-cpu` -> `final-cpu`
- CUDA lane: `runtime-cuda13-base` -> `pydeps-cuda13-builder` -> `deps-cuda13` -> `final-cuda13`

What is in `pydeps-*` (heavy Python build stage):
- Python 3.11 + a single shared venv (`/opt/venv`)
- build toolchain (`build-essential`, `cmake`, headers)
- backend torch install (CPU index or CUDA 13 index) once per image variant
- pip install of ASR/TTS/Emb + filtered app requirements in the same venv

What is in `deps-*` (heavy runtime dependency layer, tagged for reuse):
- runtime OS deps (`ffmpeg`, `nginx`, `graphviz`, etc.)
- copied `/opt/venv` from `pydeps-*`
- Playwright package + Chromium install

What is in `final-*` (light app/service layer):
- `ScriptExecutionAgent` publish artifacts copy
- `nginx.conf`, `entrypoint.sh`, `start-llama.sh`, `start-asr.sh`
- ASR app copy (`asr-service/`) and process wiring
- health check and entrypoint wiring

Why this is the clean extension path:
- Most service-extension changes (gateway routes, startup logic, agent publish output, runtime scripts) are in `final-*`.
- Heavy dependency rebuilds are avoided by reusing `guideants-ai-deps:*` tags keyed off dependency file hashes.
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
  - This matches the image used by `docker-compose.yml` for the `guideants-webapi-ui` service

## 6) Compose Usage After Builds

From `docker/`:

```powershell
docker compose up -d guideants-ai mssql-express plantuml
```

Optional UI profile:

```powershell
docker compose --profile webapi-ui up -d guideants-webapi-ui
```

Because the build scripts update `.env`, compose picks up the newest `GA_AI_IMAGE` and `GA_WEBAPI_UI_IMAGE` automatically.

## 7) Sandbox/Experimental Dockerfiles

The following folders contain sandbox/reference builds and are not first-class compose entrypoints by default:

- `docker/build/Sandboxes/python311TorchCPU`
- `docker/build/Sandboxes/python311TorchCUDA`
- `docker/build/Sandboxes/python311TorchMARM64`
- `docker/build/Sandboxes/whiper-large`
- `docker/build/Sandboxes/Net9AndPython`

These are useful for experimentation and dependency prototyping, while production/local-stack builds should follow Sections 2-6 above.
