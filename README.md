# GuideAnts Notebooks

Guides + Assistants = GuideAnts (pronounced "guidance")

<table>
  <tr>
    <td><img src="docs/images/Chat.png" alt="Chat"></td>
    <td><img src="docs/images/Services.png" alt="Services"></td>
    <td><img src="docs/images/GuideBuilder.png" alt="Guide Builder"></td>
    <td><img src="docs/images/Telemetry.png" alt="Telemetry"></td>
  </tr>
</table>

GuideAnts is a large, full-stack AI workspace system that combines notebook-style workspaces, reusable guides and assistants, file and lineage management with document intelligence and RAG, provider-routed multimodal AI services, with a modular architecture that works locally and scales to any cloud.

## New: Pluggable DocumentServer Runtime

GuideAnts now supports a pluggable DocumentServer runtime across compose stacks. You can run either Euro-Office DocumentServer or ONLYOFFICE DocumentServer by changing a single env value, without renaming services or changing app config keys.
This powers in-app Office document display and full editing for project and notebook files.

- Compose and app naming stay neutral: `documentserver` service and `DocumentServer:*` settings.
- Switch implementation with one variable: `GA_DOCUMENTSERVER_IMAGE`.
- Example values:
  - `GA_DOCUMENTSERVER_IMAGE=ghcr.io/euro-office/documentserver:latest`
  - `GA_DOCUMENTSERVER_IMAGE=onlyoffice/documentserver:latest`

This makes it straightforward to choose the implementation that best fits your environment while keeping the same GuideAnts runtime wiring. See [DocumentServer image switching](#documentserver-image-switching) for operational details.

## Quickstart

Use the root one-step launcher for your OS:

- Windows:
  - `start_windows.cmd`
- Linux:
  - `bash ./start_linux.sh`
- macOS:
  - `bash ./start_macos.sh`

If your `.sh` launchers are not executable, run:

- `chmod +x ./start_linux.sh ./start_macos.sh`

What these scripts do:

- Validate Docker and Docker Compose availability.
- Detect backend (`cuda13` when NVIDIA is available, `rocm` when AMD/ROCm is available, otherwise `cpu`; `slim` is explicit only).
- Choose the matching compose stack (GHCR by default).
- Start GuideAnts with Docker Compose.
- Wait for `http://localhost:5107/` and open it in your browser.

Useful options:

- `--doctor` checks only (no changes).
- `--fix` attempts limited remediation where possible.
- `--backend cpu|cuda13|rocm|slim` forces the AI/runtime stack. Use `slim` when you need the Python sandbox but plan to use cloud/provider AI instead of local model runtimes.
- `--compose ghcr|local` chooses prebuilt GHCR stack or local-image stack.

## New: First-Party Authentication And Authorization (June 2026)

GuideAnts now ships first-party app authentication and role-based API authorization.

- App-issued JWT bearer auth (`/api/auth/register`, `/api/auth/login`, `/api/auth/me`).
- Bootstrap-admin flow on fresh installs: first registrant becomes `Admin`; later users are `Pending` until approved.
- Admin user management endpoints and UI (`Settings -> Users`) for approve/role/deactivate/set-password actions.
- Role-gated app/API access (`Pending`, `Reader`, `Contributor`, `Admin`) with server-enforced policies.
- Tool OAuth tokens moved server-side and encrypted at rest (no client `localStorage` token persistence).

For the full flow and bootstrap procedure, see [`docs/auth-flow.md`](docs/auth-flow.md).

## Security And Exposure Status (June 2026)

GuideAnts now includes first-party auth and user/role management, and hardening work continues for cross-network exposure.

Current guidance:

- Treat current stacks as trusted-network/dev deployments unless explicitly hardened for external exposure.
- Avoid exposing internal service containers directly to public networks.
- Prefer API-mediated browser traffic (including proxied integrations) over direct container host ports.

## Run Modes: Fast Local Dev Vs Deployment-intent

Use the mode that matches your goal.

### A) Fast local API/UI debugging (host-run API + UI)

Use this for rapid iteration when you do not want to rebuild the API image after each change:

- Run API/UI directly on host (`http://localhost:5106` API, Vite/browser dev as needed).
- Keep supporting dependencies in Docker.
- Use `docker/.env.api-local-debug.example` as your baseline env.

Example (CUDA dependency services only):

```powershell
docker compose --env-file docker/.env.api-local-debug.example -f docker/docker-compose.cuda.yml up -d mssql-express guideants-ai docling-serve documentserver plantuml searxng
```

Then run API/UI from source on host (see [`docs/developer-config-guide.md`](docs/developer-config-guide.md)).

### B) Deployment-intent compose runs (private internals)

Use this for full-stack validation with private internals:

- Non-GHCR compose stacks and `docker/docker-compose.cuda.api-only-local-build.yml` are deployment-intent variants.
- Internal services are intended to remain private on `guideants-network`.
- The API/UI entrypoint is the only service that should require host-port exposure.

For end-to-end startup examples, see [`docs/setup-guide.md`](docs/setup-guide.md).

## Choose A Runtime Stack

The launcher makes two independent choices:

- **Backend** chooses the AI/runtime shape: `cpu`, `cuda13`, `rocm`, or `slim`.
- **Compose mode** chooses where GuideAnts images come from: `ghcr` pulls prebuilt images; `local` uses GuideAnts images you built on this machine. Third-party services such as Docling and DocumentServer may still pull their exact image tags the first time you start a local stack.

Default startup is `--compose ghcr` with auto-detected `cuda13`, `rocm`, or `cpu`. The `slim` backend is never auto-detected; choose it explicitly.

| Backend | Use this when | Auto-selected? | GHCR compose | Local compose | Web/API/SQL shape | AI image shape |
|---------|---------------|----------------|--------------|---------------|-------------------|----------------|
| `cuda13` | You want local model runtimes on NVIDIA GPUs. | Yes, when supported NVIDIA runtime is detected. | `docker/docker-compose.ghcr-cuda13.yml` | `docker/docker-compose.cuda.yml` | Split stack: API/UI plus separate SQL Server. | Full `guideants-ai` with llama.cpp, ASR, TTS, image generation, embeddings, media, and sandbox. |
| `rocm` | You are testing local model runtimes on AMD/ROCm. | Yes, when ROCm is detected. | `docker/docker-compose.ghcr-rocm.yml` | `docker/docker-compose.rocm.yml` | Split stack: API/UI plus separate SQL Server. | Full `guideants-ai` ROCm variant. |
| `cpu` | You want local model runtimes without GPU acceleration, or no GPU backend is available. | Yes, fallback when GPU backends are not selected. | `docker/docker-compose.ghcr-cpu.yml` | `docker/docker-compose.cpu.yml` | Split stack: API/UI plus separate SQL Server. | Full `guideants-ai` CPU variant. |
| `slim` | You need Python sandbox/script execution and supporting services, but model calls will go to cloud/provider AI. | No. Must pass `--backend slim`. | `docker/docker-compose.ghcr-slim.yml` | `docker/docker-compose.slim.yml` | Combined `guideants-webapi-ui-mssql` image. | `guideants-ai slim`: sandbox and non-model media service only. |

Terminology that matters:

| Name | Meaning | Do not confuse it with |
|------|---------|------------------------|
| `guideants-ai slim` | The sandbox-oriented AI image used by the `slim` backend. It starts `/sandbox` and `/media`; it does not start llama, llama-admin, ASR, TTS, SD, or embeddings. | `guideants-webapi-ui-slim`. |
| `docker-compose.slim.yml` | The local full slim stack: combined Web/API/SQL, slim AI, Docling, DocumentServer, PlantUML, and SearXNG. | A standalone web/API slim compose file. |
| `guideants-webapi-ui-slim` | The API/UI-only image used by split-stack deployments, especially GHCR CPU/CUDA/ROCm stacks. It does not bundle SQL Server. | The slim backend or slim AI stack. |
| `guideants-webapi-ui-mssql` | The combined Web/API/SQL image used by the slim stack and the MSSQL all-in-one path. | Split-stack API/UI images. |

Experimental support note:

- GuideAnts now includes experimental ROCm support for AMD GPUs (`rocm` backend). Behavior, compatibility, and image size/performance are still being validated.

After startup:

- For `cpu`, `cuda13`, or `rocm`, follow the [Local AI Setup Guide](docs/local-ai-setup-guide.md) to configure Hugging Face access, download models, and enable local AI services in the Settings wizard.
- For `slim`, skip local model setup. Configure cloud/provider AI in Settings and use the slim stack for Python sandbox/script execution plus supporting services.

GuideAnts is an AI notebook and workflow platform built around projects, notebooks, reusable guides, and provider-routed AI services. It is designed to give people a place to collect source material, work with assistants in context, run multimodal AI tasks, and turn rough working sessions into reusable or publishable outputs.

At a high level, a GuideAnts project is the durable home for files, folders, links, guides, assistants, usage data, and published experiences. Notebooks sit inside projects as working spaces where users chat with models, upload or copy files, generate artifacts, run speech and image workflows, and publish results back into the project when they are ready.

## What This Project Does

GuideAnts is not just a chat UI. The codebase supports a fairly broad product surface:

- **Projects and notebooks** for organizing long-lived work, source files, notebook snapshots, and conversation history.
- **Notebook conversations** with model-backed assistants, rich editing, attachments, and model/runtime selection.
- **Guides and assistants** that package prompts, tools, OpenAPI-backed operations, auth settings, avatars, conversation starters, and runtime compatibility rules.
- **Published guides** that can be exposed publicly with friendly URLs, auth hooks, usage limits, and embeddable chat experiences.
- **Project and notebook file systems** with copy, sync, publish-back, versioning, and lineage tracking.
- **Background processing** for markdown extraction, transcription, indexing, embeddings rebuilds, retention cleanup, and related async work.
- **Provider-routed AI services** so chat, embeddings, image generation, speech transcription, speech synthesis, and document intelligence can each be pointed at local or cloud backends independently.
- **Local AI runtime management** for llama.cpp and other local services, including model cataloging, runtime profiles, router alias management, load/unload flows, and Hugging Face-based model onboarding.
- **Usage and cost visibility** for both internal activity and published guide execution.

## Core Product Model

The easiest way to understand GuideAnts is to think in terms of its main objects:

- **Project**: the durable workspace boundary. A project owns folders, content files, notebooks, guides, assistants, and usage records.
- **Notebook**: the active working environment inside a project. A notebook can hold copied/uploaded files, conversations, generated artifacts, and a chosen template or guide.
- **Guide**: a reusable, shareable AI experience that can be attached to a notebook or published for outside use.
- **Assistant**: a reusable assistant definition with instructions, tools, context options, files, and model settings.
- **Published Guide**: a controlled public entry point for a guide, with auth and cost-limit enforcement.

That shape shows up consistently across the API, the data model, the React UI, and the background job system.

## How The System Is Put Together

This repo contains the full application stack, not just one app.

- **Client app**: `src/client` is a React 19 + Vite application that can run in the browser or inside Electron. It includes the main product UI for home, projects, notebooks, guides, assistants, usage, and settings.
- **Main API**: `src/server/GuideAntsApi` is an ASP.NET Core 8 application that exposes the product API and serves the built browser UI.
- **Data model**: `src/server/GuideAntsApi.DataModel` contains the EF Core models, `DbContext`, and migrations for projects, notebooks, files, guides, assistants, published guides, settings, and usage data.
- **Background jobs**: `src/server/GuideAntsApi.BackgroundJobs` handles async work such as extraction, transcription, indexing, embeddings rebuilds, and retention cleanup.
- **Chat and tool-calling libraries**: `src/server/AntRunner.Chat` contains the shared multi-provider chat runtime and tool-calling infrastructure used by the app.
- **Local execution/runtime helpers**: `src/server/ScriptExecutionAgent` and the `docker/build/guideants-ai` assets support local script execution and the consolidated AI gateway.
- **Python utilities**: `src/python/pptx` contains presentation-generation tooling and related helpers.
- **Docker deployment/runtime assets**: `docker` contains compose definitions, image build recipes, startup scripts, runtime volume conventions, and local AI infrastructure docs.

## Local Runtime Shape

The current operator/developer setup is centered on Docker Compose. The stack described in the repo currently includes:

- `guideants-webapi-ui`, `guideants-webapi-ui-slim`, or `guideants-webapi-ui-mssql` for the API plus bundled browser UI, depending on stack.
- `mssql-express` for the application database in split stacks, or bundled SQL Server inside `guideants-webapi-ui-mssql` in combined stacks.
- `guideants-ai` as a consolidated local AI gateway, or as the sandbox-oriented AI runtime in the slim stack.
- `docling-serve` for local document intelligence / markdown extraction
- `documentserver` for in-app Office document display and full editing in project and notebook file flows
- `searxng` for search support
- `plantuml` as a ScriptExecutionAgent-backed diagram sandbox with PlantUML and Graphviz installed

### Network Exposure Policy

- In deployment-intent stacks, only the API/UI entrypoint should have a host `ports` mapping.
- SQL, AI runtime, Docling, DocumentServer, PlantUML, and SearXNG should remain internal to `guideants-network`.
- Client/browser traffic should route through API endpoints and proxy routes instead of direct host access to supporting containers.

### Auth And User Management Status

Auth and user management are implemented with app-issued JWTs and role-based authorization. On a fresh install, the first registered account is automatically `Admin`; additional accounts are created as `Pending` until approved by an admin user.

See [`docs/auth-flow.md`](docs/auth-flow.md) for the bootstrap-admin and role/route behavior details.

Set `GA_DOCUMENTSERVER_IMAGE` to whichever compatible DocumentServer image you want the compose stacks to run. The checked-in `docker/.env` sets `GA_DOCUMENTSERVER_IMAGE=ghcr.io/euro-office/documentserver:latest`; override that value with any compatible image when needed.

### DocumentServer Image Switching

- Keep the compose service/config naming neutral (`documentserver`, `DocumentServer:*`) and switch implementations only by changing `GA_DOCUMENTSERVER_IMAGE`.
- Example values:
  - `GA_DOCUMENTSERVER_IMAGE=ghcr.io/euro-office/documentserver:latest`
  - `GA_DOCUMENTSERVER_IMAGE=onlyoffice/documentserver:latest`
- After changing the image value, restart the container with your selected compose file so Docker Compose pulls/runs the requested image for `documentserver`.

For local host-API debugging (API at `http://localhost:5106`, services in Docker), use `docker/.env.api-local-debug.example` as the reference env and set `DocumentServer:ApiBaseUrl` in `src/server/GuideAntsApi/appsettings.Development.json` to `http://host.docker.internal:5106`.

To enable DocumentServer JWT, set:

- Docker env: `GA_DOCUMENTSERVER_JWT_ENABLED=true` and `DOCUMENTSERVER_JWT_SECRET=<secret>`
- API config: `DocumentServer:JwtEnabled=true` and `DocumentServer:JwtSecret=<same secret>`

The `guideants-ai` container is especially important. Full local AI variants are the runtime surface behind llama.cpp, embeddings, speech transcription, speech synthesis, image generation, media extraction, and script execution. The `guideants-ai slim` variant is different: it is the sandbox-oriented AI image for Python script execution when model calls are routed to cloud/provider services. This is separate from `guideants-webapi-ui-slim`, which remains the API/UI image used by split-stack deployments. The Settings UI and API route each AI capability to the correct local or cloud backend rather than treating “the model” as one global switch.

For sandbox hardening, API-to-agent calls now require a shared token (`ScriptExecution__AgentToken` in API, `SCRIPT_EXECUTION_AGENT_TOKEN` in `guideants-ai`) and every script request is notebook-scoped (`ProjectId` + `NotebookId`) with canonical path and reparse-point checks inside the agent.

## Big Thanks To Upstream Projects

GuideAnts is built on top of excellent open source work. Huge thanks to the teams and contributors behind these projects:

- [llama.cpp](https://github.com/ggml-org/llama.cpp) for local LLM inference/runtime foundations.
- [stable-diffusion.cpp](https://github.com/leejet/stable-diffusion.cpp) for the local image-generation engine used in `guideants-ai`.
- [Qwen3-ASR](https://github.com/QwenLM/Qwen3-ASR) for local speech transcription models/runtime (`qwen-asr`).
- [VibeVoice](https://github.com/microsoft/VibeVoice) for local speech synthesis models/runtime.
- [Transformers](https://github.com/huggingface/transformers) for model loading and inference integration across local services.
- [sentence-transformers](https://github.com/UKPLab/sentence-transformers) for local embeddings support.
- [Hugging Face Hub](https://github.com/huggingface/huggingface_hub) for model download and management workflows.
- [PyTorch](https://github.com/pytorch/pytorch) for tensor/runtime acceleration across ASR, TTS, and embeddings.
- [FastAPI](https://github.com/fastapi/fastapi) and [Uvicorn](https://github.com/encode/uvicorn) for the local Python service APIs.
- [FFmpeg](https://github.com/FFmpeg/FFmpeg) for media extraction/transcoding.
- [Playwright](https://github.com/microsoft/playwright-python) for browser automation used in local service workflows.
- [Docling](https://github.com/docling-project/docling) for document intelligence and markdown extraction (`docling-serve`).
- [SearXNG](https://github.com/searxng/searxng) for metasearch and web retrieval.
- [PlantUML](https://github.com/plantuml/plantuml) and [Graphviz](https://gitlab.com/graphviz/graphviz) for diagram rendering.
- [Euro-Office DocumentServer](https://github.com/Euro-Office/DocumentServer) and [ONLYOFFICE DocumentServer](https://github.com/ONLYOFFICE/DocumentServer) as compatible `GA_DOCUMENTSERVER_IMAGE` targets for full in-app Office document display and editing capabilities.

## Repository Tour

- [`docs/`](docs/) contains the most useful product and architecture writeups. This is where to look when you want intent, requirements, rollout notes, or operational behavior.
- [`docker/`](docker/) contains the compose stack, local AI image build instructions, and runtime scripts.
- [`src/client/`](src/client/) contains the user-facing app.
- [`src/server/`](src/server/) contains the .NET solution and supporting server-side projects.
- [`src/python/`](src/python/) contains smaller Python-side utilities that support specific workflows.
- [`scripts/`](scripts/) contains repo-maintenance utilities.

## Where To Start

If you are new to the repo, these are the best first reads:

1. **[`docs/developer-config-guide.md`](docs/developer-config-guide.md) — start here if you are setting up a dev machine.** It has the full install checklist (Docker, PowerShell Core, Node, .NET, optional GPU drivers) with cross-platform install links, plus per-lane pre-requisites for client, server, and docker work.
2. [`docs/setup-guide.md`](docs/setup-guide.md) for the end-to-end local stack, first-user bootstrap, and Settings workflow.
3. [`docs/auth-flow.md`](docs/auth-flow.md) for auth lifecycle, role model, and admin approval flow.
4. [`docs/settings-page-provider-model-llama-redesign.md`](docs/settings-page-provider-model-llama-redesign.md) for current Settings architecture and extension seams.
5. [`docs/settings-and-llama-completion-requirements.md`](docs/settings-and-llama-completion-requirements.md) and [`docs/settings-service-provider-model-requirements.md`](docs/settings-service-provider-model-requirements.md) for normative requirements.
6. [`docs/default-chat-models.md`](docs/default-chat-models.md), [`docs/llama-model-download-and-runtime-management.md`](docs/llama-model-download-and-runtime-management.md), and [`docs/add-ai-services-wizard.md`](docs/add-ai-services-wizard.md) for focused deep dives.
7. [`docs/project-and-notebook-files-system.md`](docs/project-and-notebook-files-system.md) for the core project/notebook/file model.
8. [`docker/guideants-ai-build.md`](docker/guideants-ai-build.md) and [`docker/build-processes.md`](docker/build-processes.md) for building the local images this repo expects.

## Development Entry Points

> **New to the codebase?** Read [`docs/developer-config-guide.md`](docs/developer-config-guide.md) first — it is the single source of truth for what to install and how the client, server, and docker lanes hang together.

For day-to-day work, the main entry points are:

- [`docs/developer-config-guide.md`](docs/developer-config-guide.md) for the install checklist and per-lane pre-requisites (client, server, docker)
- [`src/client/package.json`](src/client/package.json) for browser/Electron dev, build, and test commands
- [`src/server/GuideAntsApi.sln`](src/server/GuideAntsApi.sln) for the .NET solution
- [`appsettings.example.json`](appsettings.example.json) and [`appsettings.Development.example.json`](appsettings.Development.example.json) for sanitized config templates
- [`src/server/GuideAntsApi/appsettings.example.json`](src/server/GuideAntsApi/appsettings.example.json) and [`src/server/GuideAntsApi/appsettings.Development.example.json`](src/server/GuideAntsApi/appsettings.Development.example.json) for server-local config structure

Typical work splits into one of three lanes:

- frontend/product work in `src/client`
- API/domain/runtime work in `src/server`
- local infrastructure/runtime work in `docker`
