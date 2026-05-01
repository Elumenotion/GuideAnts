# GuideAnts Setup Guide

Last updated: 2026-05-01

This is the setup-first operator guide for GuideAnts.
Use it to get a working environment from zero to usable chat/services, then use linked docs for deeper architecture details.

## 1. Fast path (recommended)

Use the root launcher script for your OS:

- Windows: `start_windows.cmd`
- Linux: `bash ./start_linux.sh`
- macOS: `bash ./start_macos.sh`

What these scripts do:

- Validate Docker + Docker Compose.
- Auto-detect backend (`cuda13` when NVIDIA is available, otherwise `cpu`).
- Choose compose stack (`ghcr` by default, `local` optional).
- Start the stack and wait for `http://localhost:5107/`.

Useful options:

- `--doctor` (checks only, no startup)
- `--fix` (limited auto-remediation)
- `--backend cpu|cuda13` (force backend)
- `--compose ghcr|local` (prebuilt GHCR vs local images)

If the launcher gets you to `http://localhost:5107/`, skip to section 5.

## 2. What you are setting up

GuideAnts runs as a Docker Compose stack on a single host. The runtime stack includes six services:

| Service | Image/source | Role |
|---------|---------------|------|
| `mssql-express` | `mssql2025-express-fts` | SQL Server database. |
| `guideants-ai` | `ghcr.io/elumenotion/guideants-ai-{cpu,cuda13}:latest` (or local tag) | Consolidated local AI gateway: llama.cpp, ASR, TTS, image generation, embeddings, media, script execution. |
| `docling-serve` | `quay.io/docling-project/docling-serve-{cpu,cu130}` | Local document intelligence / markdown extraction. |
| `guideants-webapi-ui` | `${GA_WEBAPI_UI_IMAGE}` | Main API plus bundled browser UI at `http://localhost:5107`. |
| `plantuml` | `plantuml-1.2025.2` | Diagram rendering. |
| `searxng` | `${GA_SEARXNG_IMAGE:-guideants-searxng:latest}` | Search backend used by agent/web features. |

Llama runtime ownership split:

- `guideants-ai` owns local model artifacts under `/models-local/llama`.
- Router preset lives at `/models-local/router-models.ini` on Docker volume `ai_local_models`.
- API delegates runtime/download/register/load/unload to `guideants-ai` (`/llama-admin/*`).
- Web API does not directly own host llama model folders.

Settings ownership split:

- Runtime/environment config comes from compose/appsettings/env.
- Credentials and routing choices are DB-backed settings edited in UI.

Settings top-level tab order (current):

1. Overview
2. Personalization
3. Connections
4. Models & Runtime
5. Services
6. Infrastructure
7. Telemetry

## 3. Prerequisites

### Host

- Docker Desktop (Windows/macOS) or Docker Engine 24+ with Compose plugin.
- Windows PowerShell 7+ for `docker/llama/run/*.ps1` helper scripts.
- For CUDA local AI: NVIDIA drivers + container runtime support.
- Disk budget: ~60 GB minimum for common local model sets.

### Images and compose mode

You can run in either mode:

- `ghcr` mode (default in launcher): pulls prebuilt images via `docker/docker-compose.ghcr-*.yml`.
- `local` mode: uses `docker/docker-compose.{cpu,cuda}.yml`; build local images first when needed.

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

GHCR images:

- CUDA: `docker/docker-compose.ghcr-cuda13.yml`
- CPU: `docker/docker-compose.ghcr-cpu.yml`

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
```

### Minimal `docker/.env`

```dotenv
GA_WEBAPI_UI_IMAGE=guideants-webapi-ui:latest
DOCLING_SERVE_MAX_SYNC_WAIT=600
GA_CONTENT_FILES_HOST_PATH=./volumes/content-files
GA_SEARXNG_CONFIG_HOST_PATH=./volumes/searxng/config
GA_SEARXNG_DATA_HOST_PATH=./volumes/searxng/data
GA_DB_NAME=guideants-dev
# HF_TOKEN=hf_xxxxx
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

## 5. First load and first-launch wizard

Open `http://localhost:5107`.

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

Wizard step flow:

1. Provider
2. Connection details (cloud) or Prerequisites (Local AI)
3. Models
4. Optional services
5. Finish

Local AI path specifics:

- Prerequisites step captures HF token and shows live readiness for `LlamaCpp:BaseUrl` and `LocalServiceHosts:*` keys.
- Models step supports Hugging Face browse + GGUF selection + async install progress.
- Optional services step configures local providers for embeddings, images, STT, TTS, and document intelligence.

Detailed walkthroughs:

- [`add-ai-services-wizard.md`](add-ai-services-wizard.md)
- [`local-ai-setup-guide.md`](local-ai-setup-guide.md)

## 6. Configure AI services (manual Settings path)

Use this if you skip wizard or need fine-grained changes.

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
2. Pick provider (`openai-chat`, `openai-responses`, `azure-openai-*`, `google-gemini-chat`, etc.).
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

### Wizard did not auto-open

- Check local storage key `guideants.firstLaunch.addAiServicesWizard.dismissed.v1`.
- Verify `GET /api/settings/sections` and `GET /api/settings/models` both succeed.

### Local runtime calls fail but cloud setup works

- Validate `LlamaCpp:BaseUrl` and `LocalServiceHosts:*` values.
- Run Infrastructure probes.
- Check `guideants-ai` and `docling-serve` logs.

### Model download fails with Hugging Face auth error

- Save token in `Settings -> Connections -> HuggingFace`.
- Retry add/download.

### Service shows Not ready

- Open that service editor.
- Confirm required provider fields and active provider.
- Re-check Overview readiness.

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
3. [`settings-page-provider-model-llama-redesign.md`](settings-page-provider-model-llama-redesign.md)
4. [`settings-and-llama-completion-requirements.md`](settings-and-llama-completion-requirements.md)
5. [`settings-service-provider-model-requirements.md`](settings-service-provider-model-requirements.md)
6. [`default-chat-models.md`](default-chat-models.md)
7. [`llama-model-download-and-runtime-management.md`](llama-model-download-and-runtime-management.md)
8. [`telemetry-configuration.md`](telemetry-configuration.md)
9. [`../docker/guideants-ai-build.md`](../docker/guideants-ai-build.md)
10. [`../docker/build-processes.md`](../docker/build-processes.md)
