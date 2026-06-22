# GuideAnts Setup Guide

Last updated: 2026-06-05

This is the setup-first operator guide for GuideAnts.
Use it to get a working environment from zero to usable chat/services, then use linked docs for deeper architecture details.

Source-of-truth set for provider/runtime setup:
- [settings-architecture.md](settings-architecture.md)
- [settings-and-llama-completion-requirements.md](settings-and-llama-completion-requirements.md)
- [settings-architecture.md#default-chat-model-behavior](settings-architecture.md#default-chat-model-behavior)

## 1. Fast path (recommended)

Use the root launcher script for your OS:

- Windows: `start_windows.cmd`
- Linux: `bash ./start_linux.sh`
- macOS: `bash ./start_macos.sh`

What these scripts do:

- Validate Docker + Docker Compose.
- Auto-detect backend (`cuda13` when NVIDIA is available, `rocm` when AMD/ROCm is available, otherwise `cpu`). The `slim` backend is explicit only.
- Choose compose stack (`ghcr` by default, `local` optional).
- Start the stack and wait for `http://localhost:5107/`.

Useful options:

- `--doctor` (checks only, no startup)
- `--fix` (limited auto-remediation)
- `--backend cpu|cuda13|rocm|slim` (force backend; `slim` is the sandbox-oriented stack)
- `--compose ghcr|local` (prebuilt GHCR vs local images)

If the launcher gets you to `http://localhost:5107/`, skip to section 5 for first-user auth bootstrap and initial wizard flow.

## 2. What you are setting up

GuideAnts runs as a Docker Compose stack on a single host. Pick the stack by deciding two things:

1. Whether model runtimes should run locally (`cpu`, `cuda13`, `rocm`) or elsewhere (`slim`).
2. Whether images should be pulled from GHCR (`--compose ghcr`) or built locally first (`--compose local`).

| Backend | Best for | Compose files | Web/API/SQL shape | AI runtime shape |
|---------|----------|---------------|-------------------|------------------|
| `cuda13` | Local AI on NVIDIA GPUs. | `docker-compose.ghcr-cuda13.yml` or `docker-compose.cuda.yml` | Split stack: API/UI plus separate SQL Server. | Full local AI services. |
| `rocm` | Experimental local AI on AMD/ROCm. | `docker-compose.ghcr-rocm.yml` or `docker-compose.rocm.yml` | Split stack: API/UI plus separate SQL Server. | Full local AI services. |
| `cpu` | Local AI without GPU acceleration. | `docker-compose.ghcr-cpu.yml` or `docker-compose.cpu.yml` | Split stack: API/UI plus separate SQL Server. | Full local AI services. |
| `slim` | Python sandbox users who use cloud/provider AI for model calls. | `docker-compose.ghcr-slim.yml` or `docker-compose.slim.yml` | Combined `guideants-webapi-ui-mssql`; no separate `mssql-express` service. | `guideants-ai slim`: sandbox/media only. |

The services you see depend on that stack:

| Service | Image/source | Role |
|---------|---------------|------|
| `mssql-express` | `mssql2025-express-fts` | SQL Server database for split-stack `cpu`, `cuda13`, and `rocm` deployments. Not present in the slim stack because SQL Server is bundled into `guideants-webapi-ui-mssql`. |
| `guideants-ai` | `ghcr.io/elumenotion/guideants-ai-{cpu,cuda13,rocm}:latest` (or local tag); `guideants-ai-slim` for the slim stack | Full variants are the local AI gateway: llama.cpp, ASR, TTS, image generation, embeddings, media, script execution. The slim AI variant is for Python sandbox/script execution without starting local model runtime services. |
| `docling-serve` | `quay.io/docling-project/docling-serve-cpu:v1.21.0` by default | Local document intelligence / markdown extraction. The `cpu` in this image tag is Docling's CPU image variant, not the GuideAnts backend selection. Healthcheck: `GET /version`. |
| `documentserver` | `${GA_DOCUMENTSERVER_IMAGE}` from `docker/.env` | DocumentServer used for in-app Office document display and full editing in project/notebook file flows. |
| `guideants-webapi-ui` / `guideants-webapi-ui-slim` / `guideants-webapi-ui-mssql` | Stack-specific API/UI image | Main API plus bundled browser UI at `http://localhost:5107`. `guideants-webapi-ui-slim` is API/UI-only for split stacks; it is not the slim AI stack. |
| `plantuml` | `plantuml-1.2025.2` | ScriptExecutionAgent-backed PlantUML sandbox with PlantUML and Graphviz installed. |
| `searxng` | `${GA_SEARXNG_IMAGE:-guideants-searxng:latest}` | Search backend used by agent/web features. |

Llama runtime ownership split:

- `guideants-ai` owns local model artifacts under `/models-local/llama`.
- Router preset lives at `/models-local/router-models.ini` on Docker volume `ai_local_models`.
- API delegates runtime/download/register/load/unload to `guideants-ai` (`/llama-admin/*`).
- Web API does not directly own host llama model folders.

Settings ownership split:

- Runtime/environment config comes from compose/appsettings/env.
- Credentials and routing choices are DB-backed settings edited in UI.
- Script execution package/config state is owned by `guideants-ai` admin state and persisted in Docker volume `script_agent_admin_state`.
- Script execution credentials are not stored by `guideants-ai`; the API must resolve credentials by `project + guide` and pass per-run environment values to the script agent when needed.

Settings top-level tab order (current):

- Admin users see the full administrative settings surface, including the `Users` tab.
- Non-admin users see `Personalization` only.
- Admin tab groups:
  1. Overview
  2. Personalization
  3. Users
  4. Connections
  5. Models & Runtime
  6. Services
  7. Infrastructure
  8. Telemetry

## 3. Prerequisites

### Host

- Docker Desktop (Windows/macOS) or Docker Engine 24+ with Compose plugin.
- Windows PowerShell 7+ for `docker/llama/run/*.ps1` helper scripts.
- For CUDA local AI: NVIDIA drivers + container runtime support.
- Disk budget: ~60 GB minimum for common local model sets.

### Images and compose mode

You can run in either mode:

- `ghcr` mode (default in launcher): pulls prebuilt images via `docker/docker-compose.ghcr-*.yml`.
- `local` mode: uses `docker/docker-compose.{cpu,cuda,rocm,slim}.yml`; build GuideAnts local images first when needed. Third-party images such as Docling or DocumentServer may still be pulled if the exact tag is not already present locally.

The slim stack is selected with `--backend slim` and uses `docker/docker-compose.slim.yml` locally or `docker/docker-compose.ghcr-slim.yml` in GHCR mode. It uses the combined Web/API/SQL image (`guideants-webapi-ui-mssql`) plus the sandbox-oriented AI image (`guideants-ai slim`). It does not use `guideants-webapi-ui-slim`; that image is orthogonal and remains the API/UI image for split-stack deployments.

Script execution state:

- The `guideants-ai` service mounts `script_agent_admin_state` at `/var/lib/guideants/script-agent-admin`.
- That volume stores admin config, apt package requests, global requirements, and per-`project + guide` Python venv state.
- Per-`project + guide` venvs extend the image-provided `/opt/venv` packages; they add or override packages for that scope instead of replacing the baked runtime.
- It survives restart and normal `docker compose down` / `up`.
- It is removed by `docker compose down -v`.

Build references:

- [`docker/guideants-ai-build.md`](../docker/guideants-ai-build.md)
- [`docker/build-processes.md`](../docker/build-processes.md)

### Optional: Hugging Face token

You need an HF token for wizard/download flows that pull models from Hugging Face.
Create one at <https://huggingface.co/settings/tokens> (read scope is enough for public models).

UI token path is intentionally single-source:

1. `Settings -> Connections -> HuggingFace -> Token`

`POST /api/settings/models:add` does not support per-request token overrides.

Details: [`llama-model-download-and-runtime-management.md`](llama-model-download-and-runtime-management.md)

## 4. Start the stack manually (compose)

If you do not use the launcher scripts, start compose directly from repo root.

### Choose compose file

Local images:

- CUDA: `docker/docker-compose.cuda.yml`
- CPU: `docker/docker-compose.cpu.yml`
- ROCm: `docker/docker-compose.rocm.yml`
- Slim: `docker/docker-compose.slim.yml`

GHCR images:

- CUDA: `docker/docker-compose.ghcr-cuda13.yml`
- CPU: `docker/docker-compose.ghcr-cpu.yml`
- ROCm: `docker/docker-compose.ghcr-rocm.yml`
- Slim: `docker/docker-compose.ghcr-slim.yml`

### Example startup commands

```powershell
# local CUDA
 docker compose -f docker/docker-compose.cuda.yml up -d

# local CPU
 docker compose -f docker/docker-compose.cpu.yml up -d

# GHCR CUDA
 docker compose -f docker/docker-compose.ghcr-cuda13.yml up -d

# GHCR CPU
 docker compose -f docker/docker-compose.ghcr-cpu.yml up -d

# local ROCm
 docker compose -f docker/docker-compose.rocm.yml up -d

# GHCR ROCm
 docker compose -f docker/docker-compose.ghcr-rocm.yml up -d

# local slim
 docker compose -f docker/docker-compose.slim.yml up -d

# GHCR slim
 docker compose -f docker/docker-compose.ghcr-slim.yml up -d
```

### Minimal `docker/.env`

```dotenv
GA_WEBAPI_UI_IMAGE=guideants-webapi-ui:latest
DOCLING_SERVE_MAX_SYNC_WAIT=600
DOCLING_SERVE_MAX_FILE_SIZE=524288000
DOCLING_SERVE_ENG_LOC_NUM_WORKERS=2
DOCLING_NUM_THREADS=4
GA_CONTENT_FILES_HOST_PATH=./volumes/content-files
GA_SEARXNG_CONFIG_HOST_PATH=./volumes/searxng/config
GA_SEARXNG_DATA_HOST_PATH=./volumes/searxng/data
GA_DB_NAME=guideants-dev
GA_DOCUMENTSERVER_IMAGE=ghcr.io/euro-office/documentserver:latest
GA_DOCUMENTSERVER_ENABLED=true
GA_DOCUMENTSERVER_JWT_ENABLED=false
# HF_TOKEN=hf_xxxxx
```

### DocumentServer config

Required rules:

1. `GA_DOCUMENTSERVER_IMAGE` selects any compatible DocumentServer image. The checked-in `docker/.env` currently sets this to `ghcr.io/euro-office/documentserver:latest`; override this value in your env file to use another compatible image.
1. Keep naming neutral in compose and config (`documentserver`, `DocumentServer:*`) regardless of which compatible image you select.
1. Example image values:
   - `GA_DOCUMENTSERVER_IMAGE=ghcr.io/euro-office/documentserver:latest`
   - `GA_DOCUMENTSERVER_IMAGE=onlyoffice/documentserver:latest`
1. After changing `GA_DOCUMENTSERVER_IMAGE`, restart the `documentserver` service with your selected compose file so Docker Compose pulls/runs that specific image.
2. `DocumentServer:ApiBaseUrl` is dedicated to DocumentServer callback/download URLs; do not use `ANTRUNNER_SERVICES_HOST_URL` for this.
3. JWT for DocumentServer is optional and disabled by default (`GA_DOCUMENTSERVER_JWT_ENABLED=false`, `DocumentServer:JwtEnabled=false`).

Topology-specific values:

- API containerized in compose:
  - `DocumentServer:ApiBaseUrl = http://guideants-webapi-ui:8080` (already wired in compose)
- API on host (`http://localhost:5106`) with services in Docker:
  - `DocumentServer:ApiBaseUrl = http://host.docker.internal:5106`
  - Optional JWT mode:
    - `GA_DOCUMENTSERVER_JWT_ENABLED=true`
    - `DocumentServer:JwtEnabled=true`
    - configure shared `DOCUMENTSERVER_JWT_SECRET` / `DocumentServer:JwtSecret`

### Enable DocumentServer JWT (explicit recipe)

If you want JWT enabled, set the same secret in both Docker env and API config.

1. Set Docker env values (`docker/.env` or your `--env-file`):

```dotenv
GA_DOCUMENTSERVER_JWT_ENABLED=true
DOCUMENTSERVER_JWT_SECRET=<your-strong-shared-secret>
GA_DOCUMENTSERVER_JWT_HEADER=Authorization
GA_DOCUMENTSERVER_JWT_IN_BODY=false
```

2. Set matching API values:

- API in Docker: compose already maps `DocumentServer__Jwt*` from those env vars.
- API on host (`localhost:5106`): set in `src/server/GuideAntsApi/appsettings.Development.json`:

```json
"DocumentServer": {
  "Enabled": true,
  "PublicUrl": "http://localhost:8082",
  "InternalUrl": "http://localhost:8082",
  "ApiBaseUrl": "http://host.docker.internal:5106",
  "JwtEnabled": true,
  "JwtSecret": "<same-value-as-DOCUMENTSERVER_JWT_SECRET>",
  "JwtHeader": "Authorization",
  "JwtInBody": false
}
```

3. Restart services after changes:

```powershell
docker compose -f docker/docker-compose.cuda.yml up -d --build
```

If the API runs on host, restart the API process after editing `appsettings.Development.json`.

For host-API debugging with compose services, use:

```powershell
docker compose --env-file docker/.env.api-local-debug.example -f docker/docker-compose.cuda.yml up -d --build
```

### Verify startup

```powershell
# choose the same compose file you used for up
 docker compose -f docker/docker-compose.cuda.yml ps
```

All services should report running/healthy.

### Bootstrap seeding on first startup

After migrations and settings bootstrap, required data is seeded from `Resources/bootstrap/`:

- Required guides: Creative Guide, The Guide Guide.
- Required assistants/crew: Conversation Title Generator, Read Web, Search, Media Creator, Diagrams, Code Executor, Conversation User Proxy.
- Runtime profiles: `qwen3_5`, `qwen3_6`, `gemma4`.

Seeding is idempotent and does not overwrite user edits.

Reference: [`../src/server/GuideAntsApi/Resources/bootstrap/README.md`](../src/server/GuideAntsApi/Resources/bootstrap/README.md)

## 5. First load, auth bootstrap, and first-launch wizard

Open `http://localhost:5107`.

### 5.1 Authentication bootstrap (required)

GuideAnts now ships first-party JWT auth with role-based authorization.

On a fresh install:

1. You are routed to `/register`.
2. The first successful registration is auto-assigned `Admin`.
3. Subsequent registrations are created as `Pending`.
4. An admin approves pending users and assigns roles in `Settings -> Users`.

Route behavior:

- Anonymous users are sent to `/login` (or `/register` for first account creation).
- Authenticated `Pending` users are routed to `/pending`.
- Authenticated users with `MustChangePassword` are routed to `/change-password`.
- Approved users (`Reader`, `Contributor`, `Admin`) can access product routes by role.

Reference: [`auth-flow.md`](auth-flow.md)

### 5.2 First-launch wizard behavior

On first-load conditions, Home auto-opens Add AI Services Wizard when either is true:

- No configured connection sections, or
- No catalog models.

Auto-open is skipped if local dismissal key is set:

- `guideants.firstLaunch.addAiServicesWizard.dismissed.v1`

Wizard paths currently supported:

- Microsoft Foundry
- Google Gemini
- OpenAI
- Local AI

Wizard step flow is provider-specific:

`foundry`, `google-gemini`, and `openai` currently use:

1. Provider
2. Connection details
3. Models
4. Optional services
5. Finish

Local AI:

1. Provider
2. Connection details (Prerequisites)
3. Models
4. Speech Transcription
5. Image Generation
6. Speech Synthesis
7. Document Intelligence
8. Embeddings
9. Finish

Local AI path specifics:

- Prerequisites step captures HF token and shows live readiness for `LlamaCpp:BaseUrl` and `LocalServiceHosts:*` keys.
- Models step supports Hugging Face browse + GGUF selection + async install progress for local chat models.
- After chat models, each non-chat local service has its own step with Settings-parity controls.
- Each local service step is skippable; `Next` persists provider fields + activates local provider for that service, while `Skip this service` leaves service config unchanged.
- If a local model/bundle download is in flight on the active step, navigation is blocked until completion or explicit cancel.
- Embeddings now requires explicit model download + load (same lifecycle pattern as ASR/TTS); no silent default-model activation in wizard flow.

Detailed walkthroughs:

- [`add-ai-services-wizard.md`](add-ai-services-wizard.md)
- [`local-ai-setup-guide.md`](local-ai-setup-guide.md)
- [`auth-flow.md`](auth-flow.md)

## 6. Configure AI services (manual Settings path)

Use this if you skip wizard or need fine-grained changes.

> Note: AI/service/runtime configuration tabs are admin-only. Non-admin users only
> have access to `Personalization`.

### Step 1: Connections

Open **Connections** and save credentials you plan to use.

Typical sections include:

- Chat providers: `AzureOpenAI`, `OpenAI`, `Anthropic`, `GoogleGeminiApi`
- Service providers: `AzureSpeechService`, `AzureOpenAiImages`, `AzureOpenAiEmbedding`, `AzureDocumentIntelligence`
- Hugging Face token section for model downloads

Secrets are masked on read and encrypted at rest.

### Step 2: Models & Runtime

Open **Models & Runtime**:

- **Catalog**: add chat models (`llama-cpp`, OpenAI/Azure/Gemini/etc.).
- Provider status for operator setup:
  - Stable (operator-supported): `openai-chat`, `openai-responses`, `azure-openai-chat`, `azure-openai-responses`, `anthropic`, `llama-cpp`, `google-gemini-chat`, `openrouter-chat`
  - Experimental/Hidden: `hf-inference-chat`
- **Runtime Profiles**: manage `qwen3_5`, `qwen3_6`, `gemma4` templates or custom profiles.
- **Local Llama Runtime**: view inventory and run load/unload/delete alias actions.

For local llama onboarding, use `Add Model` with source `Install from Hugging Face` or `Attach existing alias`.

### Step 3: Services

Open **Services** and configure each non-chat capability:

- Embeddings
- Image Generation
- Speech Transcription
- Speech Synthesis
- Document Intelligence

For each service:

1. Choose provider.
2. Fill required provider fields.
3. Save and activate provider.

### Step 4: Overview

Use **Overview** to verify:

- Default chat model state.
- Chat + non-chat readiness chips.
- Direct links back to failing sections.

### Step 5: Infrastructure

Use **Infrastructure** to verify runtime-owned dependencies and probe reachability.

Current dependency keys surfaced in UI:

- `LlamaCpp:BaseUrl`
- `LocalServiceHosts:SpeechTranscriptionBaseUrl`
- `LocalServiceHosts:SpeechSynthesisBaseUrl`
- `LocalServiceHosts:ImageGenerationBaseUrl`
- `LocalServiceHosts:EmbeddingsBaseUrl`
- `LocalServiceHosts:MediaBaseUrl`
- `LocalServiceHosts:DocumentIntelligenceBaseUrl`

Probe notes:

- URL probes use GET with a short timeout (3s).
- `LlamaCpp:BaseUrl` is probed via `/health` path mapping.
- Probe failures are usually runtime/network issues, not DB config corruption.

### Step 6: Telemetry and Personalization

- **Telemetry**: raise API logging levels during troubleshooting.
- **Personalization**: user profile fields only; does not affect routing readiness.

## 7. Worked examples for Add Model

### 7a) Local llama model via Hugging Face

Example flow (`Qwen3.5-9B-Q5_K_M-local`):

1. Settings -> Models & Runtime -> Catalog -> Add Model.
2. Provider: `llama-cpp`.
3. Catalog fields:
   - `modelId`: `Qwen3.5-9B-Q5_K_M-local`
   - `displayName`: `Qwen3.5 9B Q5_K_M (Local)`
4. Provider/runtime fields:
   - Runtime profile: `qwen3_5`
   - Router alias: `Qwen3.5-9B-Q5_K_M`
   - Source: `Install from Hugging Face`
   - Repository: `unsloth/Qwen3.5-9B-GGUF`
   - GGUF: `Qwen3.5-9B-Q5_K_M.gguf`
   - Optional mmproj: `mmproj-F16.gguf`
5. Create model and monitor progress (`queued -> resolvingFiles -> downloading -> registeringAlias -> completed`).
6. In Local Llama Runtime, load the alias and verify test chat.

### 7b) Cloud model add

1. Settings -> Models & Runtime -> Catalog -> Add Model.
2. Pick a stable provider (`openai-chat`, `openai-responses`, `azure-openai-*`, `anthropic`, `google-gemini-chat`, or `llama-cpp`).
3. Fill model/provider config.
4. Save.
5. Verify row is available for chat routing.

### 7c) Attach existing alias (no re-download)

Use when runtime files exist but catalog row is missing:

1. Confirm alias exists in Local Llama Runtime inventory.
2. Add Model -> `llama-cpp` -> source `Attach existing alias`.
3. Select orphaned alias and save.
4. Verify model is usable immediately.

## 8. Worked example: switch markdown extraction to local Docling

1. Infrastructure: verify `LocalServiceHosts:DocumentIntelligenceBaseUrl` resolves and probes healthy.
2. Services -> Document Intelligence:
   - Select `Local Docling HTTP`.
   - Save and activate provider.
3. Validate by extracting a PDF and checking logs for Docling execution path.

## 9. Smoke tests

Run these after setup changes.

### Chat

Open any assistant/notebook and send a simple prompt.

### Embeddings

```powershell
Invoke-RestMethod -Uri "http://localhost:5107/api/settings/embeddings/rebuild" -Method Post
```

Track returned job id until completed.

### Speech transcription / synthesis

- ASR: test microphone upload/voice flow and verify transcription path.
- TTS: request speech output and verify audio response.

### Image generation

Trigger image generation in notebook. First call may be slower due to model warmup.

### Runtime health endpoints

```powershell
curl.exe -s -o NUL -w "HTTP=%{http_code}" http://localhost:8110/llama-cpp/health
curl.exe -s -o NUL -w "HTTP=%{http_code}" http://localhost:8110/llama-admin/health
curl.exe -s -o NUL -w "HTTP=%{http_code}" http://localhost:8110/emb/health
curl.exe -s -o NUL -w "HTTP=%{http_code}" http://localhost:5001/health
curl.exe -s -o NUL -w "HTTP=%{http_code}" http://localhost:8082/web-apps/apps/api/documents/api.js
```

Expected: HTTP 200 for each reachable local runtime.

## 10. Stop, update, reset

### Stop

```powershell
# choose the same compose file used for startup
 docker compose -f docker/docker-compose.cuda.yml down
```

This preserves named volumes by default (including SQL data and `ai_local_models`).

### Update

1. Update image tags/env where needed.
2. Re-run `docker compose -f <file> up -d`.
3. Allow migrations to run on first boot of updated API image.

### Reset local dev state

```powershell
docker compose -f docker/docker-compose.cuda.yml down -v
```

This removes compose-managed volumes for that stack.

## 11. Troubleshooting

### Cannot access Settings admin tabs

- Confirm the user role is `Admin`.
- `Pending`, `Reader`, and `Contributor` users are intentionally limited to `Personalization`.
- Use an admin account to approve and role-assign users in `Settings -> Users`.

### Wizard did not auto-open

- Check local storage key `guideants.firstLaunch.addAiServicesWizard.dismissed.v1`.
- Verify `GET /api/settings/sections` and `GET /api/settings/models` both succeed.

### Local runtime calls fail but cloud setup works

- Validate `LlamaCpp:BaseUrl` and `LocalServiceHosts:*` values.
- Run Infrastructure probes.
- Check `guideants-ai` and `docling-serve` logs.

### Python package changes disappeared

- Packages installed manually inside `guideants-ai` are container-local and disappear when the container is recreated.
- Persist package changes through the script-agent admin state: global/scoped `requirements.txt` for pip packages and `apt-packages.txt` for apt packages.
- Scoped pip packages extend the image's baked `/opt/venv` packages. For example, if `numpy` is baked into the image and a guide adds `humanize`, both are importable in that guide's scripts.
- The persisted state lives in Docker volume `script_agent_admin_state`; keep the volume if you want changes to survive `down/up`.
- Do not use `docker compose down -v` unless you intend to remove that state.

### Model download fails with Hugging Face auth error

- Save token in `Settings -> Connections -> HuggingFace`.
- Retry add/download.

### Service shows Not ready

- Open that service editor.
- Confirm required provider fields and active provider.
- Re-check Overview readiness.

### Local embeddings says Not ready / No model loaded

- Install an embeddings model from the Embeddings service manager (`Add model`).
- Wait for download operation completion (or cancel and retry).
- Load an installed model from the row action (`Load`), then re-check readiness.
- Verify `LocalServiceHosts:EmbeddingsBaseUrl` probe in Infrastructure.

### Add Model structured error codes

- `HUGGINGFACE_TOKEN_MISSING`: missing/invalid HF token.
- `PROVIDER_CREDENTIALS_MISSING`: required connection section is not configured.
- `RUNTIME_PROFILE_NOT_FOUND`: selected runtime profile is missing.
- `ROUTER_ALIAS_TAKEN`: alias already exists in runtime.
- `MODEL_ID_TAKEN`: duplicate catalog model id.

### `ROUTING_RUNTIME_NOT_READY` on local llama actions

A load/unload op is already in flight for that alias.
Wait for current operation to finish, then retry.

## 12. Where to go next

Read in this order:

1. [`add-ai-services-wizard.md`](add-ai-services-wizard.md)
2. [`local-ai-setup-guide.md`](local-ai-setup-guide.md)
3. [`auth-flow.md`](auth-flow.md)
4. [`settings-architecture.md`](settings-architecture.md)
5. [`settings-and-llama-completion-requirements.md`](settings-and-llama-completion-requirements.md)
6. [`settings-and-llama-completion-requirements.md#r-13-non-chat-service-editor-requirements`](settings-and-llama-completion-requirements.md#r-13-non-chat-service-editor-requirements)
7. [`settings-architecture.md#default-chat-model-behavior`](settings-architecture.md#default-chat-model-behavior)
8. [`llama-model-download-and-runtime-management.md`](llama-model-download-and-runtime-management.md)
9. [`telemetry-configuration.md`](telemetry-configuration.md)
10. [`../docker/guideants-ai-build.md`](../docker/guideants-ai-build.md)
11. [`../docker/build-processes.md`](../docker/build-processes.md)
