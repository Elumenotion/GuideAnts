# GuideAnts

## Quickstart

Use the root one-step launcher for your OS:

- Windows:
  - `start_windows.cmd`
- Linux:
  - `bash ./start_linux.sh`
- macOS:
  - `bash ./start_macos.sh`

What these scripts do:

- Validate Docker and Docker Compose availability.
- Detect backend (`cuda13` when NVIDIA is available, otherwise `cpu`).
- Choose the matching compose stack (GHCR by default).
- Start GuideAnts with Docker Compose.
- Wait for `http://localhost:5107/` and open it in your browser.

Useful options:

- `--doctor` checks only (no changes).
- `--fix` attempts limited remediation where possible.
- `--backend cpu|cuda13` forces backend choice.
- `--compose ghcr|local` chooses prebuilt GHCR stack or local-image stack.

After startup, follow the [Local AI Setup Guide](docs/local-ai-setup-guide.md) to configure Hugging Face access, download models, and enable local AI services in the Settings wizard.

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

- `guideants-webapi-ui` for the API plus bundled browser UI
- `mssql-express` for the application database
- `guideants-ai` as a consolidated local AI gateway
- `docling-serve` for local document intelligence / markdown extraction
- `searxng` for search support
- `plantuml` for diagram rendering

The `guideants-ai` container is especially important: it is the local runtime surface behind llama.cpp, embeddings, speech transcription, speech synthesis, image generation, media extraction, and script execution. The Settings UI and API route each AI capability to the correct local or cloud backend rather than treating “the model” as one global switch.

## Repository Tour

- [`docs/`](docs/) contains the most useful product and architecture writeups. This is where to look when you want intent, requirements, rollout notes, or operational behavior.
- [`docker/`](docker/) contains the compose stack, local AI image build instructions, and runtime scripts.
- [`src/client/`](src/client/) contains the user-facing app.
- [`src/server/`](src/server/) contains the .NET solution and supporting server-side projects.
- [`src/python/`](src/python/) contains smaller Python-side utilities that support specific workflows.
- [`scripts/`](scripts/) contains repo-maintenance utilities.

## Where To Start

If you are new to the repo, these are the best first reads:

1. [`docs/setup-guide.md`](docs/setup-guide.md) for the end-to-end local stack and Settings workflow.
2. [`docs/settings-page-provider-model-llama-redesign.md`](docs/settings-page-provider-model-llama-redesign.md) for current Settings architecture and extension seams.
3. [`docs/settings-and-llama-completion-requirements.md`](docs/settings-and-llama-completion-requirements.md) and [`docs/settings-service-provider-model-requirements.md`](docs/settings-service-provider-model-requirements.md) for normative requirements.
4. [`docs/default-chat-models.md`](docs/default-chat-models.md), [`docs/llama-model-download-and-runtime-management.md`](docs/llama-model-download-and-runtime-management.md), and [`docs/add-ai-services-wizard.md`](docs/add-ai-services-wizard.md) for focused deep dives.
5. [`docs/project-and-notebook-files-system.md`](docs/project-and-notebook-files-system.md) for the core project/notebook/file model.
6. [`docker/guideants-ai-build.md`](docker/guideants-ai-build.md) and [`docker/build-processes.md`](docker/build-processes.md) for building the local images this repo expects.

## Development Entry Points

For day-to-day work, the main entry points are:

- [`src/client/package.json`](src/client/package.json) for browser/Electron dev, build, and test commands
- [`src/server/GuideAntsApi.sln`](src/server/GuideAntsApi.sln) for the .NET solution
- [`appsettings.example.json`](appsettings.example.json) and [`appsettings.Development.example.json`](appsettings.Development.example.json) for sanitized config templates
- [`src/server/GuideAntsApi/appsettings.example.json`](src/server/GuideAntsApi/appsettings.example.json) and [`src/server/GuideAntsApi/appsettings.Development.example.json`](src/server/GuideAntsApi/appsettings.Development.example.json) for server-local config structure

Typical work splits into one of three lanes:

- frontend/product work in `src/client`
- API/domain/runtime work in `src/server`
- local infrastructure/runtime work in `docker`

## In One Sentence

GuideAnts is a large, full-stack AI workspace system that combines notebook-style workspaces, reusable guides and assistants, file and lineage management, provider-routed multimodal AI services, and a local-runtime-heavy deployment model in one repo.
