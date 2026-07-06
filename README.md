# GuideAnts Notebooks

**Guides + Assistants = Guidance**

## AI work that sticks around. Shared expertise that scales.

GuideAnts is a structured workspace for AI work–where projects, notebooks, files, conversations, and generated artifacts live together instead of evaporating in chat windows. Teams who want to share and productize their work can package it into reusable guides, publish them, and embed them in other applications.

<table>
  <tr>
    <td><img src="docs/images/ProjectOffice.png" alt="Project Office"></td>
    <td><img src="docs/images/Chat.png" alt="Chat"></td>
    <td><img src="docs/images/Services.png" alt="Services"></td>
    <td><img src="docs/images/GuideBuilder.png" alt="Guide Builder"></td>
    <td><img src="docs/images/Telemetry.png" alt="Telemetry"></td>
  </tr>
</table>

### From prompt to product

Most AI work happens in chats that evaporate. The conversation scrolls away. The prompt that worked is gone. The file you uploaded has no link to the output it produced. The decision that shaped the workflow lives in someone's memory.

GuideAnts gives AI work a real home. Projects, notebooks, documents and source files, conversations, generated artifacts, context, versions, and decisions live together. You don't have to build a product to benefit–your daily AI work is already better when it's grounded in your files, organized by project, and actually findable later.

For teams who want to go further, GuideAnts lets you encode repeatable ways of working into **guides and assistants**–reusable assets that package instructions, tools, files, model choices, and context options. And when a workflow is ready, you can **share** it with a friendly URL, **embed** it in another application with the [`guideants` web component](https://www.npmjs.com/package/guideants), or **integrate** it into your app's data and workflow.

[**Try GuideAnts SaaS →**](https://www.guideants.ai) · [**Self-host GuideAnts →**](#getting-started)

---

## What you can do

### Your AI workspace

| Capability | What it does |
|----------|----------|
| **Projects** | Organize all AI work in durable workspaces that own files, notebooks, guides, assistants, and usage records. |
| **Notebooks** | Active working environments with conversations, file context, generated artifacts, and version history. |
| **Chat** | Multi-turn conversations grounded in files, guides, and project context–not isolated chat threads. |
| **Files and documents** | View, edit, and collaborate on Office docs (DOCX, PPTX, XLSX), ODF formats, and Markdown directly in notebooks. Track versions, lineage, and markdown shadows for efficient indexing and RAG. |
| **Context and grounding** | Ground conversations in your actual files, past work, and project knowledge–not just what you remember to paste in. |
| **Guides and assistants** | Reusable AI workflows that package instructions, tools, files, model choices, and context options into assets anyone on the team can use–even just for yourself. |
| **Skills** | Import or author portable `SKILL.md` packages (the agentskills.io / Claude / Codex dialect) on a guide or assistant. Bodies and references load on demand; `scripts/` and `assets/` materialize into the notebook sandbox at creation (like crew CodeInterpreter files). |
| **Telemetry** | Usage events, cost tracking, invocation traces, model attribution, and runtime observability. |

### Shared and published work

| Capability | What it does |
|----------|----------|
| **Published guides** | Controlled public entry points with friendly URLs, auth, limits, and usage tracking. |
| **Embedding** | Drop guides into other applications with the `guideants` web component. |
| **Integration** | Connect guides to domain apps and workflows via public APIs, client context, and published conversation flows. |
| **Governance** | Apply access controls, charge limits, retention policies, and observability to published experiences. |
| **Tuning** | Use traceability and cost attribution to optimize model choices, routing, and behavior from prototype to sustainable production. |

---

## Ground in trusted content

GuideAnts treats documents as first-class workspace citizens, not just uploads:

| Capability | Description |
|----------|----------|
| **In-place viewing** | Open many file formats directly in the notebook–no download required. |
| **Real-time collaborative editing** | Co-edit Office documents (DOCX, PPTX, XLSX) and ODF formats (ODT, ODP, ODS) with your team, with changes versioned and linked to the conversation. |
| **Markdown editor** | Full-featured Markdown editing with live preview, syntax highlighting, and version history. |
| **Content lineage & markdown shadows** | Track file origins, versions, and **markdown shadows**–lightweight Markdown representations extracted via [Docling](https://github.com/docling-project/docling) for efficient indexing and RAG–as files move between project and notebook contexts. |
| **AI-grounded editing** | Guides and assistants can read, reference, and transform file content as context–turning a spreadsheet into a report, a spec into code, or a deck into a summary without copy-pasting. |

---

## Reusable guides and assistants

Most teams have a few people who know the right prompt, the right model, and the right way to get the AI to do something useful. When those people are unavailable, the workflow breaks.

GuideAnts packages instructions, tools, files, context options, conversation starters, model choices, and validation rules into **guides and assistants**–reusable assets that encode how work gets done. Anyone on the team can use them without needing to understand the underlying models or prompts.

You don't have to publish a guide to benefit from one. Guides work inside notebooks, conversations, and internal workflows. Publishing is optional–and only makes sense when the workflow is ready to be shared.

Guides and assistants can also carry **skills**–portable `SKILL.md` packages in the same dialect used by Claude and the OpenAI Codex CLI. Import an existing skill, author a new one in the Guide Builder, or spin up a new assistant directly from one or more skills. Skill bodies and references load on demand rather than being stuffed into every prompt; `scripts/` and `assets/` copy into the notebook sandbox when a notebook is created so the model can run them with the same tools as crew CodeInterpreter files. Published guide skills are also available to external agents as resources over the wire (`/api/published/mcp`).

---

## Traceable tuning: From expensive prototypes to sustainable products

This is where most AI initiatives stall. Your team builds something impressive with a frontier model. It works. Then someone asks what it costs to run every day, and the answer is terrifying.

**AI product failures are increasingly cost failures, not just quality failures.**

GuideAnts helps teams tune AI workflows from impressive prototypes into economically sustainable products. Because usage is attributed to projects, notebooks, conversations, assistants, invocations, messages, services, operations, models, and charges, teams can identify where spend is coming from and optimize without losing the product workflow.

Every interaction in GuideAnts is traced: you can see exactly which model handled which message, which assistant drove which cost, which tool invocation mattered, and which step could run on a smaller model without anyone noticing the difference.

### The EveryEventEver proof

[EveryEventEver](https://everyeventever.com/) is maintained using published guides. The first version ran on a state-of-the-art frontier model and cost **hundreds of dollars per day**. Using GuideAnts traceability, the team identified which parts of the workflow needed the expensive model and which didn't. The same inference now costs **about ten dollars per day**.

### What makes tuning possible

- **Usage attribution**: Track costs down to individual messages, assistants, invocations, and models
- **Model routing**: Mix strong models where needed and smaller/local models where sufficient
- **Runtime profiles**: Tune model behavior and provider choices for cost/quality tradeoffs
- **Charge tracking**: See exactly what each component costs in real-time
- **Published guide limits**: Set daily and billing-period charge limits as guardrails

---

## The product creation lifecycle

For teams who want to turn internal workflows into shareable AI products, GuideAnts supports an iterative lifecycle–not a one-shot prompt:

| Stage    | What you're doing | GuideAnts value |
|----------|----------|----------|
| **Build** | Create a guide or assistant from instructions, files, tools, model choices, and workflow design. | Expertise becomes a reusable asset instead of a one-off prompt. |
| **Validate** | Test the guide in notebooks, conversations, and internal workflows. | Teams can check behavior before exposing the experience to others. |
| **Tune** | Use traceability, usage attribution, cost data, model routing, and observed outputs to improve quality/cost. | Teams can move from expensive prototype behavior to sustainable production behavior. |
| **Ground** | Attach the tuned experience to durable project/notebook content, files, markdown extraction, context options, and generated artifacts. | The AI product has reliable source material and persistent work context. |
| **Share** | Publish with a friendly URL or controlled access pattern. | The workflow becomes available beyond the authoring environment. |
| **Embed** | Use the `guideants` web component to place a published guide inside another application. | The AI experience becomes part of a domain-specific product surface. |
| **Integrate** | Use public guide APIs, client context, attachments, and published conversation flows. | Existing apps and reports can call into the guide rather than redirect users elsewhere. |
| **Govern** | Apply auth, limits, retention, usage analytics, observability, and runtime controls. | The published AI product remains operable and accountable. |

This loop is not strictly linear. Mature teams will cycle through **Validate → Tune → Ground** many times as they improve prompts, assistants, models, routing, tools, and context.

---

## Publish where people already work

When a workflow is ready, a guide isn't just something your team uses internally. Publish it with a friendly URL, drop it into another app with the `guideants` web component, or connect it to your application's data and workflow via public APIs.

### Sharing and embedding

- **Share**: Expose a guide by friendly URL so someone can use a packaged workflow directly
- **Embed**: Drop the guide into another site/app with the `guideants` web component
- **Integrate**: Connect it to a domain experience, like a reporting/Power BI-style site, where the guide becomes contextual help, analysis, workflow automation, or guided interpretation
- **Govern**: Apply auth, limits, usage tracking, retention, and observability around that external experience

[See a Power BI integration demo →](https://www.elumenotion.com/demos/powerbi)

---

## Enterprise governance

GuideAnts provides controls for both internal work and published experiences:

| Enterprise concern | GuideAnts value |
|----------|----------|
| **Content provenance** | AI-assisted work can be traced back to source files, notebook artifacts, and generated outputs. |
| **Tuning traceability** | Teams can see which model, assistant, message, operation, or tool invocation drove cost or behavior. |
| **Runtime safety** | Tool execution runs through scoped services without exposing broad filesystem access. |
| **Cost risk** | Usage can be measured, optimized, and constrained–even for internal workflows. |
| **Operational tuning** | Teams can investigate model routing, service readiness, and runtime failures. |
| **Access control** | Published experiences can be anonymous, key-protected, or webhook-validated depending on use case. |
| **Lifecycle management** | External experiences can be retired, limited, or cleaned up over time. |

---

## Core concepts

| Concept  | What it is |
|----------|----------|
| **Project** | The durable workspace boundary–owns folders, content files, notebooks, guides, assistants, and usage records. |
| **Notebook** | The active working environment inside a project. Conversations, files, artifacts, and context live here. |
| **Guide** | A reusable AI experience built from instructions, tools, files, model choices, and context options. Works internally; can be published when ready. |
| **Assistant** | A reusable assistant definition that applies guide knowledge consistently across conversations. |
| **Published Guide** | A controlled public entry point for a guide, with auth, limits, usage tracking, and embedding support. |

---

## Architecture

GuideAnts is a full-stack platform:

- **Backend:** .NET solution with modular API, usage recording, sandbox execution, and provider-routed AI services.
- **Frontend:** React 19 + Vite application with the GuideAnts UI.
- **Runtime:** [`guideants-ai`](https://github.com/Elumenotion/GuideAnts/tree/main/docker) Docker service for scoped tool execution, document intelligence, and local AI workloads. Local inference uses native runtimes–[llama.cpp](https://github.com/ggml-org/llama.cpp) for chat and embeddings, [audio.cpp](https://github.com/0xShug0/audio.cpp) for speech transcription and synthesis, and [stable-diffusion.cpp](https://github.com/leejet/stable-diffusion.cpp) for image generation–with Python facades and an nginx gateway.
- **Local model catalogs:** Curated per-service manifests drive which models appear in settings, what files get downloaded, and how voice or speaker controls render (for example, TTS voice-pack presets vs. built-in speaker ids). See [`docs/native-ai-migration/`](docs/native-ai-migration/README.md) for the contributor architecture.
- **Document workspace:** Built-in viewer and collaborative editor–open many formats in the notebook. Office (DOCX, PPTX, XLSX) and ODF (ODT, ODP, ODS) support real-time co-editing; Markdown has a full-featured editor; many more open for viewing, annotation, and versioning. Changes are tracked as part of your project's content lineage.
- **Embedding:** [`guideants`](https://www.npmjs.com/package/guideants) npm package for embedding published guides into any web application.

Each AI capability–chat, embeddings, document intelligence, image generation, speech transcription, speech synthesis–can be routed to a different local or cloud provider. Your workflow doesn't have to choose one model for everything.

---

## Getting started

GuideAnts runs locally with Docker Compose. OS-specific quickstart scripts are included:

```bash
# Windows
.\quickstart.ps1
# or: start_windows.cmd

# Linux / macOS
./quickstart.sh
# or: start_linux.sh / start_macos.sh
```

Backends: `cuda13` (NVIDIA), `rocm` (AMD), `vulkan` (NVIDIA/AMD/Intel via Vulkan), `cpu`, and `slim` (sandbox-only, no local models). The root launchers auto-detect GPU where possible; on Windows, NVIDIA drivers below the CUDA 13 minimum (R580) fall back to `vulkan` instead of CPU.

See the [setup guide](https://github.com/Elumenotion/GuideAnts/blob/main/docs/setup-guide.md) for full instructions and the [developer config guide](https://github.com/Elumenotion/GuideAnts/blob/main/docs/developer-config-guide.md) for configuration options.

### Documentation

All documentation lives in the repository:

- [Setup guide](https://github.com/Elumenotion/GuideAnts/blob/main/docs/setup-guide.md) – installation and configuration
- [Local AI setup guide](https://github.com/Elumenotion/GuideAnts/blob/main/docs/local-ai-setup-guide.md) – wizard-driven fully local configuration (ASR, TTS, embeddings, image gen)
- [Developer config guide](https://github.com/Elumenotion/GuideAnts/blob/main/docs/developer-config-guide.md) – configuration reference
- [Auth flow](https://github.com/Elumenotion/GuideAnts/blob/main/docs/auth-flow.md) – authentication architecture
- [Project and notebook files system](https://github.com/Elumenotion/GuideAnts/blob/main/docs/project-and-notebook-files-system.md) – file and content management
- [LLaMA model management](https://github.com/Elumenotion/GuideAnts/blob/main/docs/llama-model-download-and-runtime-management.md) – local model lifecycle
- [Native local AI migration](https://github.com/Elumenotion/GuideAnts/blob/main/docs/native-ai-migration/README.md) – curated catalog manifests, voice packs, and contributor workflow
- [Docker build guide](https://github.com/Elumenotion/GuideAnts/blob/main/docker/guideants-ai-build.md) – building the runtime service
- [Vulkan backend guide](https://github.com/Elumenotion/GuideAnts/blob/main/docker/guideants-ai-vulkan.md) – vendor-neutral GPU (llama + image gen) on Docker Desktop and native Linux
- [Full docs directory](https://github.com/Elumenotion/GuideAnts/tree/main/docs) – architecture, features, test plans, and more

## Development Entry Points

> **New to the codebase?** Read [`docs/developer-config-guide.md`](docs/developer-config-guide.md) first–it is the single source of truth for what to install and how the client, server, and docker lanes hang together.

For day-to-day work, the main entry points are:

- [`docs/developer-config-guide.md`](docs/developer-config-guide.md) for the install checklist and per-lane pre-requisites (client, server, docker)
- [`src/client/package.json`](src/client/package.json) for browser/Electron dev, build, and test commands
- [`src/server/GuideAntsApi.sln`](src/server/GuideAntsApi.sln) for the .NET solution
- [`appsettings.example.json`](appsettings.example.json) and [`appsettings.Development.example.json`](appsettings.Development.example.json) for sanitized config templates
- [`src/server/GuideAntsApi/appsettings.example.json`](src/server/GuideAntsApi/appsettings.example.json) and [`src/server/GuideAntsApi/appsettings.Development.example.json`](src/server/GuideAntsApi/appsettings.Development.example.json) for server-local config structure

Typical work splits into one of three lanes:

- frontend/product work in `src/client`
- API/domain/runtime work in `src/server`
- local infrastructure/runtime work in `docker` (see [`docs/native-ai-migration/`](docs/native-ai-migration/README.md) when changing ASR/TTS/emb catalogs or local model settings UI)

## Big Thanks To Upstream Projects

GuideAnts is built on top of excellent open source work. Huge thanks to the teams and contributors behind these projects:

- [llama.cpp](https://github.com/ggml-org/llama.cpp) for local chat inference and GGUF embeddings in `guideants-ai`.
- [audio.cpp](https://github.com/0xShug0/audio.cpp) for local speech transcription and synthesis in `guideants-ai`.
- [stable-diffusion.cpp](https://github.com/leejet/stable-diffusion.cpp) for the local image-generation engine used in `guideants-ai`.
- [Hugging Face Hub](https://github.com/huggingface/huggingface_hub) for curated model download and management workflows.
- [FastAPI](https://github.com/fastapi/fastapi) and [Uvicorn](https://github.com/encode/uvicorn) for the local Python service APIs.
- [FFmpeg](https://github.com/FFmpeg/FFmpeg) for media extraction/transcoding.
- [Playwright](https://github.com/microsoft/playwright-python) for browser automation used in local service workflows.
- [Docling](https://github.com/docling-project/docling) for document intelligence and markdown extraction (`docling-serve`).
- [SearXNG](https://github.com/searxng/searxng) for metasearch and web retrieval.
- [PlantUML](https://github.com/plantuml/plantuml) and [Graphviz](https://gitlab.com/graphviz/graphviz) for diagram rendering.
- [Euro-Office DocumentServer](https://github.com/Euro-Office/DocumentServer) and [ONLYOFFICE DocumentServer](https://github.com/ONLYOFFICE/DocumentServer) as compatible `GA_DOCUMENTSERVER_IMAGE` targets for full in-app Office document display and editing capabilities.