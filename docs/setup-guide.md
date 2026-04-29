# GuideAnts Setup Guide

Last updated: 2026-04-22

This guide walks an operator through bringing up the GuideAnts stack end to
end: installing prerequisites, starting the compose stack, and configuring
every AI service (chat, embeddings, image generation, speech transcription,
speech synthesis, document intelligence) through the five-tab Settings UI
as it ships today. It also covers cloud-only onboarding, where local AI
containers are not required for initial provider configuration. Deep
architecture details live in the companion docs cross-linked below; this one
is purely operational.

## 1. What you are setting up

GuideAnts runs as a Docker Compose stack on a single host. The stack has
six services:

| Service | Image / source | Role |
|---------|----------------|------|
| `mssql-express` | `${GA_MSSQL_IMAGE:-mssql2025-express-fts}` | SQL Server database. |
| `guideants-ai` | `ghcr.io/elumenotion/guideants-ai-{cpu,cuda13}:latest` | Consolidated local AI service hosting llama.cpp + Script Execution Agent + local ASR / TTS / Stable Diffusion / embeddings behind a single gateway on port `8110`. |
| `docling-serve` | CPU/CUDA compose file selects `quay.io/docling-project/docling-serve*` | Local document intelligence (Markdown extraction). |
| `guideants-webapi-ui` | `${GA_WEBAPI_UI_IMAGE}` | Web API + bundled browser UI. Published on `http://localhost:5107`. |
| `plantuml` | `plantuml-1.2025.2` | Diagram rendering. |
| `searxng` | `docker.io/searxng/searxng:latest` | Meta-search backend used by agents. |

The local llama runtime is split cleanly by ownership: `guideants-ai` owns
model artifacts under `/models-local/llama` and the **router preset file**
`/models-local/router-models.ini` on the same `ai_local_models` Docker
volume (not a host bind). `guideants-webapi-ui` talks to the main API only;
the API delegates download, register, load, and unload to `guideants-ai`
via HTTP (`/llama-admin/*`). The web API process does not mount model
storage.

The repo file `docker/llama/router-models.ini` is a **template** aligned with
the image seed (`docker/build/guideants-ai/router-models.seed.ini`); the
live file is created or updated on the volume (first boot may seed from the
image if the file is missing).

All runtime configuration that is **not** a credential — model store paths,
container base URLs, diagnostic `RouterModelsConfigPath` — lives in
`appsettings.json` / environment variables / compose. Everything that is a
credential or a routing choice lives in the SQL Server database and is
edited from the Settings UI at `/settings`. This split is enforced by
startup validation; see
[`settings-system-ui-and-usage.md`](settings-system-ui-and-usage.md) §6 for
the full list of runtime-owned keys.

## 2. Prerequisites

### Host

- Windows 11 or Linux host with an NVIDIA GPU and recent drivers installed.
  The `guideants-ai` container requires NVIDIA Container Toolkit so
  `--gpus all` works. If you are onboarding cloud-only (no local AI), this
  requirement is optional until you enable local providers.
- Docker Desktop (Windows) or Docker Engine 24+ with the Compose plugin.
- PowerShell 7+ on Windows for the `docker/llama/run/*.ps1` helper scripts.
- ~60 GB of free disk for local model artifacts (Qwen3.5/3.6 quants,
  VibeVoice TTS, FLUX-2 image weights, Harrier embeddings, ASR model).
  Add headroom proportional to how many local llama models you plan to
  keep resident.

### Container images

The compose file references two images you must build or pull ahead of
time (they are not on a public registry):

- `guideants-ai` — selected by `docker/docker-compose.cuda.yml` or
  `docker/docker-compose.cpu.yml`. Build instructions live in
  [`docker/guideants-ai-build.md`](../docker/guideants-ai-build.md) and
  [`docker/build-processes.md`](../docker/build-processes.md).
- `${GA_WEBAPI_UI_IMAGE}` — built from `docker/build/webapi-ui/Dockerfile`;
  see the same build docs.
- `mssql2025-express-fts` and `plantuml-1.2025.2` are also helper images
  under `docker/build/`. Local compose uses the local SQL tag by default;
  the GHCR compose variants default `GA_MSSQL_IMAGE` to
  `ghcr.io/<owner>/mssql2025-express-fts:<tag>`.

The docling-serve images (`quay.io/docling-project/docling-serve-cpu` and
`docling-serve-cu130`) pull from a public registry on first run.

### Optional: Hugging Face token

You need a Hugging Face token if you plan to download gated models through
the Add Model wizard (`provider=llama-cpp`, source `Install from Hugging Face`).
Create one at <https://huggingface.co/settings/tokens> with **read** scope.
For UI-driven downloads, the token source is intentionally single-path:

1. **Settings → Connections → Hugging Face** (persisted encrypted in the
   application settings store).

The UI and `POST /api/settings/models:add` do not expose a per-request
token override.

Full precedence rules are in
[`llama-model-download-and-runtime-management.md`](llama-model-download-and-runtime-management.md) §`HuggingFaceModelDownloadService`.

## 3. Configure Compose

Use one of the two explicit dev compose files:

- `docker/docker-compose.cuda.yml` starts CUDA `guideants-ai` and CUDA Docling.
- `docker/docker-compose.cpu.yml` starts CPU `guideants-ai` and CPU Docling.

`docker/.env` is still used for shared bind-mount paths, database name,
and the web API image tag. It no longer selects the Docling profile.
Minimal example:

```dotenv
GA_WEBAPI_UI_IMAGE=guideants-webapi-ui:26108.1021
DOCLING_SERVE_MAX_SYNC_WAIT=600

# Host paths that really are bind mounts (content files + searxng
# config/data). The AI model stores are NOT bind mounts any more — see
# the "Local AI model storage" note below.
GA_CONTENT_FILES_HOST_PATH=./volumes/content-files
GA_SEARXNG_CONFIG_HOST_PATH=./volumes/searxng/config
GA_SEARXNG_DATA_HOST_PATH=./volumes/searxng/data

# Single-instance SQL Server database name. Change per environment.
GA_DB_NAME=guideants-dev

# Optional: host-level HF token consumed by docker/llama/run/*.ps1.
# The in-app downloader reads Settings > Connections > Hugging Face
# instead.
# HF_TOKEN=hf_xxxxx
```

Notes:

- Choose CPU or CUDA with the compose file, not with a profile variable.
- **Local AI model storage.** All local model artifacts (llama GGUFs,
  ASR, SD bundles, TTS weights, embeddings) live in a single
  Docker-managed named volume `ai_local_models` with per-service
  subdirs (`llama/`, `asr/`, `sd/`, `tts/`, `emb/`), mounted into
  `guideants-ai` at `/models-local`. The llama **router preset** (alias →
  GGUF/mmproj paths for `llama-server`) is the file
  **`/models-local/router-models.ini`** on that same volume, configured by
  `GA_LLAMA_MODELS_PRESET` in compose. It is **not** bind-mounted from the
  host; the Settings UI updates it through the API and `llama-admin`. You
  do not need to create a host directory for the volume. On a fresh host,
  either import existing artifacts via
  `docker/scripts/migrate-local-models-to-single-volume.ps1` or start the
  stack empty and add models through the Settings UI.
- Previous `GA_TTS_MODELS_HOST_PATH` / `GA_SD_MODELS_HOST_PATH` /
  `GA_EMB_MODELS_HOST_PATH` env vars are **no longer consulted** by the
  compose stack; keeping them set does nothing.
- The web API does **not** need `/models-local` access. Runtime
  inventory, download/register, and router-mapping operations are served
  by `guideants-ai` via `/llama-admin/*` and consumed by
  `guideants-webapi-ui`.
- If you run the web API from the IDE, ensure `LlamaCpp:BaseUrl` points to a
  reachable gateway (for example `http://localhost:8110/llama-cpp`). The
  admin base URL is derived automatically from that value.
- The SQL Server host port is `1434` (not the default `1433`) to avoid
  conflicting with a host-installed SQL Server. The app connects via the
  compose network on `1433`.

## 4. Start the stack

From the repo root:

```powershell
docker compose -f docker/docker-compose.cuda.yml up -d
```

This brings up `mssql-express`, `guideants-ai`, `docling-serve`,
`guideants-webapi-ui`, `plantuml`, and `searxng`.

For CPU-only local services, use:

```powershell
docker compose -f docker/docker-compose.cpu.yml up -d
```

Verify everything came up:

```powershell
docker compose -f docker/docker-compose.cuda.yml ps
```

All services should report `running` / `healthy`. The first boot takes a
few minutes because `mssql-express` applies EF migrations and the
application seeds required guides and assistants (see below).

### Required guides and assistants (bootstrap seeding)

On first startup, after EF migrations and application settings bootstrap,
the system imports a set of required guides and assistants from
`Resources/bootstrap/` in the web API project. These definitions power
core features such as the home page quick start button and conversation
title generation.

Seeded guides:

- **Creative Guide** — general-purpose creative assistant with crew
  members for search, media creation, diagrams, and code execution.
- **The Guide Guide** — helps users create and refine their own guides.

Seeded assistants (including crew members):

- **Conversation Title Generator** — generates titles for chat threads.
- **Read Web** — reads and summarises web content.
- **Search** — meta-search crew member.
- **Media Creator** — image generation crew member.
- **Diagrams** — diagram rendering crew member.
- **Code Executor** — code execution crew member.

Seeding is idempotent: if a guide or assistant with the same name already
exists in the database, the corresponding seed is skipped. User
modifications are never overwritten. Seeds do not specify an explicit
model; they inherit the operator's configured default chat model via
`ChatDefaults` (see [default-chat-models.md](default-chat-models.md)).

The seed definitions use the same folder-based export format as the
guide/assistant import API. To add or update a seed, export the entity
from a running system, extract it into a named subfolder under
`Resources/bootstrap/guides/` or `Resources/bootstrap/assistants/`, and
remove any model-specific fields from `manifest.json`. See
[`Resources/bootstrap/README.md`](../src/server/GuideAntsApi/Resources/bootstrap/README.md)
for details.

## 5. First load at `http://localhost:5107`

Navigate to <http://localhost:5107>. You should land on the GuideAnts
home page. The pre-seeded guides appear on the home page immediately;
no manual configuration is needed for them. Open **Settings** (gear icon
or <http://localhost:5107/settings>). You will see five tabs in this
fixed order (R-5.1):

1. **Overview** — **Default Chat Model** (global catalog default + optional
   “override all chat models”), chat providers in use (assistant-referenced),
   and status for all five non-chat services (`Ready`, `Not ready`, or
   `Not configured` when no active provider is saved yet), with jumps to
   **Connections**, **Services**, and **Models & Runtime** (see
   [default-chat-models.md](default-chat-models.md)).
2. **Models & Runtime** — catalog, runtime profiles, local llama runtime.
3. **Services** — editors for Embeddings, Image Generation, Document
   Intelligence, Speech Transcription, and Speech Synthesis.
4. **Connections** — provider credentials.
5. **Infrastructure** — runtime-owned dependency keys with source +
   reachability/path probes.

The architecture and per-tab responsibilities are documented in
[`settings-page-provider-model-llama-redesign.md`](settings-page-provider-model-llama-redesign.md);
this guide walks the practical configuration order.

### Cloud-only first run (local services not installed/running)

If `guideants-ai` is unreachable, some Settings calls that proxy to the local
runtime (for example llama inventory or local-model endpoints) may fail with
DNS or connection errors mentioning `guideants-ai`.

This does not block cloud onboarding. Switch to **Connections** (or use any
**Open in Connections** link from **Overview**) and continue configuring
cloud providers (`OpenAI`, `AzureOpenAI`, `Anthropic`, and cloud service
sections). You can complete cloud credentials and service/model setup without local
runtime services.

When you later want local AI features (local llama, local ASR/TTS, local SD,
local embeddings), start the services container, then use **Refresh** on
**Overview** or reload Settings to re-check status.

## 6. Configure AI services (recommended order)

For a first-time install, fill things in in this order. Each step maps to
one tab.

### Step 1 — Connections: fill in credentials you plan to use

Tab: **Connections**. Sections are grouped by ownership:

- **Chat / LLM providers** — `OpenAI`, `Anthropic`, `AzureOpenAI`,
  `LlamaCpp`.
- **Service providers** — `AzureOpenAiImages`, `AzureOpenAiEmbedding`,
  `AzureSpeechService`, `AzureDocumentIntelligence`.
- **Local runtime connectors** — `LlamaCpp` credentials that drive local
  inference (plus the Hugging Face token for in-app downloads).

Open each section you plan to use, fill in required fields, and click
**Save Section**. Each save is row-version concurrent — if somebody else
updated the section between load and save you will see a 409 and get an
explicit "reload / reapply draft" flow.

Guidance:

- Required fields are listed first; optional fields follow; secret fields
  show masked values (`********`) and a `secretHasValue` dot.
  Re-sending `********` on save preserves the existing encrypted value.
- Credentials are encrypted at rest with `encv2::` (AES-GCM, key id
  `SettingsSecrets:ActiveKeyId`). The shipped `appsettings.json` uses a
  local-dev key material — rotate it for any non-dev deployment.
- The **Used by services** chip list on each section tells you which
  service configurations (or chat-target catalog rows) depend on this
  section. If you later try to delete a section with non-zero usage the
  API rejects the delete; fix the dependent services first.

You do **not** have to fill in every section. Only the ones you intend to
reference from Services configuration and chat assistants need to be configured.

### Step 2 — Services: pick the active provider for each non-chat service

Tab: **Services**.

Each service has its own editor (`Embeddings`, `Image Generation`,
`Speech Transcription`, `Speech Synthesis`, `Document Intelligence`) with:

- **Active provider** (persisted state) + readiness summary.
- **Provider selector** scoped to that service only.
- **Provider-specific fields** with required validation.
- **Operational dependencies** (for local paths, sourced from
  `LocalServiceHosts:*` and related runtime keys).
- **Local model/runtime operations** for services that support them.

Typical flow for each service:

1. Open the service in **Services**.
2. Select the provider you want active for that service.
3. Fill provider-specific required fields (or verify local dependency keys).
4. Click **Save and activate provider**.

Chat is not configured on this tab. Chat model selection is assistant-driven
(R-1.1), and chat-target readiness is surfaced from **Models & Runtime** →
**Catalog**.

### Step 3 — Models & Runtime: catalog + local llama runtime

Tab: **Models & Runtime**. Three sub-tabs:

#### Catalog

One row per registered model. Columns include provider, display name,
active flag, and (for llama-cpp rows) a runtime-readiness badge
(`Registered` / `Missing Artifact` / `Loaded` / `Unloaded`) derived from
live inventory, not config.

The editor changes shape by provider (R-6.5):

- `llama-cpp` rows: guided local-runtime builder first (router alias +
  runtime profile + optional mmproj), with the raw `LocalRuntimeJson` in
  an advanced collapsible.
- Other providers: generic fields only.

Use **Add Model** to register a new catalog row. The `Provider` field is
a select populated from the provider registry — free-text is not
accepted (R-6.2).

#### Runtime Profiles

Sampling / thinking-budget presets for local llama models. Each profile
shows a "Used by N models" usage count; delete is blocked if any catalog
row references the profile.

The tab is list-first. Use **Add Profile** to open the create dialog,
use **Edit** on a row to open the same dialog for updates, and use
**Import** / **Export** to move profile JSON through the existing
runtime-profile contract without leaving Settings.

Three templates are built in (R-6.7):

- `qwen3_5` — Qwen 3.5 preset.
- `qwen3_6` — Qwen 3.6 preset (the current default recommendation).
- `gemma4` — Gemma 4 preset.

In the dialog, click `Insert <name> template` to stamp the preset into a
new profile with the canonical id (`qwen3_5`, `qwen3_6`, `gemma4`).
Idempotent: if the profile already exists, the UI surfaces it rather
than overwriting.

#### Local Llama Runtime

This tab is runtime operations only. Model onboarding moved to
**Catalog → Add Model**.

1. **Runtime Inventory.** One row per router alias, sourced from
   `guideants-ai` admin data plus the live `llama-server /v1/models`
   response. Each row shows runtime state, GGUF / mmproj presence,
   bound catalog model ids, and `Load` / `Unload` / `Delete alias + files`
   actions.
   Load / unload goes through `ILlamaRuntimeCoordinator` — a per-alias
   semaphore — so a second click on an in-flight alias returns a
   deterministic `409 ROUTING_RUNTIME_NOT_READY` instead of stalling.
   Unloading an alias with a non-zero notebook reference count prompts
   for confirmation.

2. **Router Mapping.** Preview of the effective alias-to-path
   registration in the runtime-owned router preset file, with duplicate-alias and
   missing-artifact flags. Read-only on this tab; mutations happen via
   Catalog Add Model / router delete actions (or the admin API). Do not rely on editing a host file:
   the live path is on the Docker volume, not in the repo.

The tab header surfaces the effective `ModelStorePath` and
`RouterModelsConfigPath` so you can verify path resolution without
opening `appsettings.json` (R-6.12). The same keys also appear on the
Infrastructure tab with source and existence probes.

### Step 4 — Infrastructure: verify runtime-owned keys resolve

Tab: **Infrastructure**. Read-only. Lists every runtime-owned key the
system depends on:

- `LocalServiceHosts:*` base URLs for each non-cloud service.
- `ServiceRouting:Containers:*:BaseUrl` entries used by the sandbox /
  script-execution routing.
- `LlamaModelManagement:ModelStorePath` and
  `LlamaModelManagement:RouterModelsConfigPath`.
- `LlamaCpp:BaseUrl`.

Each row shows:

- **Value** (redacted for secrets).
- **Source** — `appsettings` / `env` / `compose` / `user-secrets` /
  `unknown`. This is resolved by `RuntimeDependencySourceResolver` and
  tells you *where* the value came from so you know where to change it.
- **HasValue** — whether the key resolved to a non-empty string.
- **ReadOnly** — always `true`; these keys cannot be edited from the UI.

Click **Run Probes** to execute the diagnostic batch via
`POST /api/settings/infrastructure/probes`. Baked-in checks:

- `LlamaCpp:BaseUrl` prefix validation.
- HTTP reachability for every declared base URL.
- Path-existence probe for `ModelStorePath` and
  `RouterModelsConfigPath`.

Failures here are almost always an environment misconfiguration
(container not running, bind-mount path missing on the host, wrong port
exposed) rather than a DB issue.

For local llama management specifically, the authoritative health signal is
`/llama-admin/health` on the `guideants-ai` gateway.

## 7. Worked examples for Add Model

### 7a. Local llama (Qwen3.5) via Install from Hugging Face

End-to-end flow for `Qwen3.5-9B-Q5_K_M-local` through the wizard:

1. **Open wizard.** Models & Runtime → Catalog → `Add Model`.
2. **Step 1 (Provider).** Select `llama-cpp`.
3. **Step 2 (Catalog).** Enter:
   - `modelId`: `Qwen3.5-9B-Q5_K_M-local`
   - `displayName`: `Qwen3.5 9B Q5_K_M (Local)`
   - `description`: blank
   - `displayOrder`: blank
   - `isActive`: on
4. **Step 3 (Provider config).**
   - Runtime profile: select `qwen3_5` (or use inline `Insert qwen3_5`)
   - Router alias: `Qwen3.5-9B-Q5_K_M`
   - Optional `Context size (tokens)`: blank to use container default, or an
     integer `1024..1048576` for per-alias router key `c`
   - Optional `Prompt cache RAM (MiB)`: blank to use container default, or an
     integer `0..262144` for per-alias `LLAMA_ARG_CACHE_RAM`
   - Source: `Install from Hugging Face`
   - Repository: `unsloth/Qwen3.5-9B-GGUF`
   - Click **Browse repository files**. The wizard calls
     `GET /api/settings/llama/huggingface/repositories/{owner}/{repo}/files`
     (server-side; the configured HF token is injected automatically) and
     populates two dropdowns:
     - **Model file (GGUF)** — pick `Qwen3.5-9B-Q5_K_M.gguf` (size/quant
       label shown inline; multi-file sharded quants are listed but
       disabled since `llama-server` cannot load a single shard).
     - **Vision projector (mmproj)** — pick `mmproj-F16.gguf`. Hidden if
       the repo has no mmproj file (text-only model).
   - If HF is unreachable, the repo is gated without a token, or the
     filename heuristic misses the file you want, click
     **Enter filename manually** to fall back to free-text entry. The
     manual field accepts either an exact filename or a glob with `*`;
     the largest matching file wins.
   - Target directory: `Qwen3.5-9B-Q5_K_M`
5. **Step 4 (Review).** Click `Create model`.
6. **Step 5 (Progress).** Watch `queued → resolvingFiles → downloading → registeringAlias → completed`.
7. **Load + verify.**
   - Runtime Inventory shows `hasModelFile=Yes`, `hasMmprojFile=Yes`, `runtimeState=unloaded`.
   - Click `Load`; state transitions `unloaded → loading → loaded`.
   - Point an assistant to `Qwen3.5-9B-Q5_K_M-local` and run a chat turn.

### 7b. Cloud model add (provider-agnostic path)

1. Models & Runtime → Catalog → `Add Model`.
2. Step 1 provider: `openai-chat`.
3. Step 2 catalog: choose a unique id/display name.
4. Step 3 provider config: set model/deployment fields for your connection.
5. Step 4 review: `Create model` (sync add, no download progress).
6. Verify Catalog row exists and appears as a chat target in Overview/chat routing.

### 7c. Attach existing alias recovery (no re-download)

Use when alias/files exist but catalog row was removed:

1. Confirm Runtime Inventory still has alias with `hasModelFile=Yes` and `hasMmprojFile=Yes`.
2. Catalog → Add Model → provider `llama-cpp`.
3. Step 3 source: `Attach existing alias`.
4. Pick orphaned alias (`catalogModelIds=[]`) and complete `Create model`.
5. Verify add is synchronous and chat works immediately without another download.

## 8. Worked example — switch Markdown extraction to Docling (local)

To use local Docling for `DocumentIntelligence`, switch the service to the
local provider and verify `LocalServiceHosts:DocumentIntelligenceBaseUrl`:

1. **Connections** → confirm no cloud credentials are required (Docling
   runs locally via the `docling-serve` container). No section
   needs editing.
2. **Infrastructure** → verify
   `LocalServiceHosts:DocumentIntelligenceBaseUrl` resolves to
   `http://docling-serve:5001` (inside compose) or
   `http://localhost:5001` (local dev), and that the reachability probe
   returns healthy.
3. **Services** → **Document Intelligence**:
   - Select **Local Docling HTTP** as the provider.
   - Verify the operational dependency row points to
     `LocalServiceHosts:DocumentIntelligenceBaseUrl`.
   - Click **Save and activate provider**.
4. Verify by extracting a PDF — the API logs should read
   `via docling-serve` instead of `via Azure Document Intelligence`.

Advanced users can switch via the API directly; see
[`settings-system-ui-and-usage.md`](settings-system-ui-and-usage.md) §12
for PowerShell examples using the `/api/settings/sections/...` and
`/api/settings/services/{serviceId}*` endpoints.

## 9. Smoke tests

After configuration, run these quick checks:

### Chat

Navigate to any assistant, start a new chat, send "hello". A successful
response confirms resolver + validator + the selected chat provider are
wired correctly.

### Embeddings

```powershell
Invoke-RestMethod -Uri "http://localhost:5107/api/settings/embeddings/rebuild" -Method Post
```

Returns a job id. Poll `GET /api/background-jobs/{id}` (or watch the
**Embeddings** row on Settings → **Overview**, or open **Services** →
**Embeddings** for detailed status) until `completed`.

### Speech transcription / synthesis

- ASR: POST an audio file to a notebook that uses `SpeechTranscription`.
  The logs should show `GuideAntsApi.Services.Components.SpeechTranscriptionService`
  at `Information` level (enabled by default in both `appsettings.json`
  and the compose env).
- TTS: analogous path through `SpeechSynthesisService`. On the
  `guideants-ai` container, TTS auto-load is **off** by default
  (`GA_TTS_AUTO_LOAD_ON_STARTUP=0` unless overridden); the first request
  triggers load and warmup.

### Image generation

Trigger an image-generation flow from a notebook. First request after
boot may take longer while the SD pipeline warms up (warmup is enabled
by default via `GA_SD_WARMUP_*`).

#### Local SD bundle definitions: download + upload

When using the local Image Generation provider, the bundle manager supports
recipe portability:

- **Download definition** (row action) exports that bundle's recipe as
  JSON (`<bundle-id>.bundle-definition.json`).
- **Upload definition** imports a recipe JSON and pre-fills the
  **Download bundle** form so you can re-create the same bundle in another
  environment.

Current bundle-row actions are a compact icon action bar with accessible labels
(`aria-label` + tooltip): **View details**, **Download definition**,
**Edit bundle**, **Activate bundle**, and **Remove bundle**.

Required recipe fields:

- `bundleId`
- `roles.diffusion.repo` + `roles.diffusion.file`
- `roles.vae.repo` + `roles.vae.file`
- `roles.textEncoder.repo` + `roles.textEncoder.file`
- optional `revision`

**Where the recipe lives:** The downloadable bundle recipe and weights are stored on the **`ai_local_models` named volume** (`/models-local/sd/bundles/<bundle-id>/`), not in SQL Server `ApplicationSettings`. The database holds provider routing, timeouts, and related settings (`ImageGeneration`, `ServiceModes`, etc.), not the per-role Hugging Face `(repo, file)` pairs—those are volume files unless you manually pasted JSON into some other store.

If **FLUX.2-klein-9B** diffusion is paired with a **Qwen3-4B** text-encoder GGUF, `sd-server` can crash during sampling with `GGML_ASSERT(ggml_can_mul_mat)` (upstream expects **Qwen3-8B** for klein-9B). Fix the **`text-encoder/`** file and `bundle-definition.json` on the volume (or **Edit bundle** in Settings and re-download).

### Runtime health gateway

```powershell
curl.exe -s -o NUL -w "HTTP=%{http_code}" http://localhost:8110/llama-cpp/health
curl.exe -s -o NUL -w "HTTP=%{http_code}" http://localhost:8110/llama-admin/health
curl.exe -s -o NUL -w "HTTP=%{http_code}" http://localhost:8110/emb/health
curl.exe -s -o NUL -w "HTTP=%{http_code}" http://localhost:5001/health
```

All four should return `200`.

## 10. Stopping and updating

### Stop

```powershell
Push-Location docker
docker compose -f docker-compose.cuda.yml down
Pop-Location
```

`down` preserves the `mssql_data` and `ai_local_models` named volumes
(the latter holds every local AI model under per-service subdirs). The
bind-mounted content-files and SearXNG directories on the host are
untouched.

### Update images

1. Edit `GA_WEBAPI_UI_IMAGE` in `docker/.env` if you need a different web API tag.
2. `docker compose -f docker/docker-compose.cuda.yml up -d` — compose pulls the selected tags and
   recreates changed containers.
3. On first boot of the updated web API, EF migrations run
   automatically. Existing DB state is preserved by migration idempotency
   patterns where applicable.

### Reset local dev state

```powershell
Push-Location docker
docker compose -f docker-compose.cuda.yml down -v
Pop-Location
```

This wipes SQL Server data and llama / ASR model volumes. Bind-mounted
host directories still need manual cleanup if you want a full reset.

## 11. Troubleshooting

Short list of the common first-day issues. Each pairs the symptom with
the tab / endpoint where you diagnose it.

### All non-chat services show "Not ready" on Overview

Cause: one or more service editors are missing required provider fields,
pointing at unavailable local dependencies, or have not been saved after a
provider switch.

Fix:

- Open **Services** and review each affected service editor.
- Ensure required provider fields are configured in **Connections** and
  runtime dependencies are healthy in **Infrastructure**.
- Save each service with **Save and activate provider** after changes.

### Chat model on Models & Runtime shows `PROVIDER_MISSING_FIELDS` but chat works fine

You are running a build from before 2026-04-18 where the readiness
mapper did not recognize `openai-chat` / `openai-responses`. Update the
`guideants-webapi-ui` image.

### Local SD: `GGML_ASSERT(ggml_can_mul_mat)` or connection reset during image generation

Usually a **bad bundle recipe** on the **`ai_local_models`** volume: **FLUX.2-klein-9B** requires a **Qwen3-8B** text-encoder GGUF, not Qwen3-4B. Update `sd/bundles/<id>/bundle-definition.json` and the single file under `text-encoder/`, then activate the bundle and **Load engine** (see the note under [Local SD bundle definitions](#local-sd-bundle-definitions-download--upload) above).

### Llama runtime inventory/download calls fail with llama-admin errors

Symptoms include:

- `GET /api/settings/llama/runtime/inventory` returns 5xx with an error
  mentioning `llama admin`.
- `POST /api/settings/models:add` fails for `llama-cpp` install operations.

Cause: the web API cannot reach `guideants-ai` at the derived
`/llama-admin/*` base URL.

Fix:

1. Check runtime health:
   `curl http://localhost:8110/llama-admin/health`
2. If running the API outside compose, ensure `LlamaCpp:BaseUrl` targets
   the gateway you can reach from that process (for local host runs:
   `http://localhost:8110/llama-cpp`).
3. If running in compose, ensure `LlamaCpp__BaseUrl` is
   `http://guideants-ai:80/llama-cpp` and recreate `guideants-webapi-ui`.

### Add Model wizard error codes

When Step 4 returns a structured error, use the `code` directly:

- `HUGGINGFACE_TOKEN_MISSING` — configure token in **Connections → Hugging Face**.
- `PROVIDER_CREDENTIALS_MISSING` — open **Connections** for the provider section named in the message.
- `RUNTIME_PROFILE_NOT_FOUND` — go back to Step 3 and select/create a valid profile.
- `ROUTER_ALIAS_TAKEN` — choose a different alias or use `Attach existing alias`.
- `MODEL_ID_TAKEN` — return to Step 2 and pick a unique `modelId`.

### Settings shows `Local services container is unreachable` or guideants-ai DNS errors

Symptoms include:

- Error text title or body: `Local services container is unreachable`
- Transport errors that mention `guideants-ai`, `connection refused`, or
  `Name or service not known (guideants-ai:80)`

Cause: the web API cannot reach the local runtime gateway. This is expected
when local services are intentionally not installed/running for a cloud-only
deployment.

Fix options:

1. If you want local AI now, start the services container (`guideants-ai`)
   and retry the failing action (for example **Refresh** on **Overview**, or
   reload the tab).
2. If you are cloud-only, continue in **Connections** and configure cloud
   providers. The missing local container does not prevent cloud provider
   setup.

### 409 on section save

Stale `rowVersion`. The UI will show a banner; click **Reload** to pull
the latest, re-apply your draft, and save again. This is by design —
settings sections are optimistic-concurrent so two operators cannot
silently stomp each other (R-4.1).

### `ROUTING_RUNTIME_NOT_READY` with `action: "Wait for the in-flight operation..."`

The alias you targeted is locked by another load / unload. Inspect
`GET /api/settings/llama/runtime/status` or the Runtime Inventory table
to see which alias is busy, wait for the operation to complete, then
retry.

### Docling probe fails / "connection refused" on port 5001

The wrong explicit stack is running, or an old profile-based Docling
container is still present. Stop the old containers and start the matching
compose file:

```powershell
docker rm -f docling-serve-cpu docling-serve-cuda
docker compose -f docker/docker-compose.cuda.yml up -d
```

## 12. Where to go next

Deeper docs, in reading order:

- [`settings-system-ui-and-usage.md`](settings-system-ui-and-usage.md) —
  operational overview of the settings system, API contract, secrets
  lifecycle.
- [`settings-page-provider-model-llama-redesign.md`](settings-page-provider-model-llama-redesign.md) —
  canonical description of the Settings UI and the two-resolver
  routing model (chat vs non-chat services).
- [`settings-and-llama-completion-requirements.md`](settings-and-llama-completion-requirements.md) —
  binding requirements (`R-X.N`). When docs disagree, this one wins.
- [`llama-model-download-and-runtime-management.md`](llama-model-download-and-runtime-management.md) —
  llama runtime lifecycle, per-alias coordinator semantics, download
  service internals.
- [`docker/llama/README.md`](../docker/llama/README.md) — script-driven
  model download helpers and gateway port map.
- [`docker/guideants-ai-build.md`](../docker/guideants-ai-build.md) /
  [`docker/build-processes.md`](../docker/build-processes.md) — how to
  build the `guideants-ai` and `guideants-webapi-ui` images locally.

