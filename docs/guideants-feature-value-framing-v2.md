# GuideAnts Feature Value Framing (Draft v2)

Status: Draft for discussion
Date: 2026-06-08
Builds on: `docs/guideants-feature-list-analysis-approach.md`, `docs/guideants-feature-list-pass-a-draft.md`

## 1. Positioning adjustment

The Pass A draft was useful as a repo-grounded inventory, but it framed features too literally. The stronger GuideAnts story is not "more things in an AI chat app." It is:

> GuideAnts turns AI work into governed, reusable, observable, and publishable work products.

LibreChat and Open WebUI are strong examples of the "unified AI interface" category: multi-provider chat, agents/tools, files, RAG, code execution, and admin controls. GuideAnts should not try to win by out-listing that surface area.

GuideAnts has a different center of gravity:

- Durable AI workspaces, not disposable conversations.
- Reusable guides and assistants, not one-off prompts.
- Internal workflows that can become external AI experiences.
- Enterprise controls around content, cost, lineage, observability, and runtime isolation.
- Modular local/cloud service architecture rather than a single global model switch.

## 2. Proposed value pillars

| Pillar | Value promise | What to emphasize |
|---|---|---|
| AI Workspaces | Keep AI work organized, contextual, and durable. | Projects, notebooks, files, links, conversations, artifacts, home pages, versioning. |
| Reusable Expertise | Turn repeatable work into reusable guided experiences. | Guides, assistants, tools, instructions, files, crew patterns, imports/exports. |
| Publishable Workflows | Move from internal workflow to controlled external AI product. | Published guides, friendly URLs, embeddable chat, public APIs, auth, limits. |
| Enterprise Content Governance | Preserve provenance, manage content lifecycle, and support audit/compliance needs. | File versioning, markdown shadows, lineage events, notebook sync, retention cleanup. |
| Cost and Usage Control | Understand and govern AI activity before it becomes a runaway expense. | Usage attribution, assistant/message/invocation linkage, charge tracking, published-guide daily limits. |
| Observability and Operations | Give operators the tools to tune, debug, and prove what happened. | Telemetry presets, structured categories, infrastructure probes, readiness checks, runtime status. |
| Modular Secure Runtime | Isolate risky or heavyweight capabilities behind internal services and scoped execution. | `guideants-ai`, sandbox agent, Docling, DocumentServer, SearXNG, PlantUML, token-gated internal calls, notebook-scoped execution. |
| Provider-Routed AI Services | Route each AI capability to the right local or cloud backend. | Chat, embeddings, image generation, ASR, TTS, document intelligence, runtime profiles, local model lifecycle. |

## 3. Enterprise capabilities as value statements

### 3.1 Content governance and lineage

Value statement:

GuideAnts gives AI work a content system of record, so files and generated artifacts can be versioned, traced, synchronized, and recovered instead of disappearing into chat history.

Proof features:

- Project and notebook file systems with copy, sync, upload, move, rename, delete, publish-back.
- File version history via `ContentFileVersion`.
- Notebook file origin tracking via `OriginContentFileVersionId` and related fields.
- File lineage events via `FileLineageEvent` and `FileLineageService`.
- Markdown shadow tables for extracted/indexed document representations.
- Background jobs for extraction, indexing, transcription, embedding rebuilds, and notebook sync.

Evidence anchors:

- `src/server/GuideAntsApi.DataModel/ApplicationDbContext.cs`
- `src/server/GuideAntsApi.DataModel/Models/FileLineageEvent.cs`
- `src/server/GuideAntsApi/Services/Components/FileLineageService.cs`
- `src/server/GuideAntsApi/Endpoints/FileLineageEndpoints.cs`
- `src/server/GuideAntsApi/Endpoints/ProjectContentFileEndpoints.cs`
- `src/server/GuideAntsApi/Endpoints/NotebookEndpoints.cs`
- `src/server/GuideAntsApi.BackgroundJobs/Jobs/`

### 3.2 Cost controls and usage accountability

Value statement:

GuideAnts records AI usage with enough attribution to explain who or what caused cost: project, notebook, conversation, assistant, invocation, message, operation, service, and model.

Proof features:

- Usage events record category, service, operation, model, token/value metrics, metadata, provider cost, markup, and final charge.
- Conversational usage can be associated with assistant IDs, agent invocations, and individual conversation messages.
- Guide/assistant usage pages expose summaries, charts, crew activity, conversations, invocations, and turn messages.
- Published guide access can be blocked when daily charge limits are exceeded.
- Retention cleanup can automatically process published guide conversations based on configured retention windows.

Evidence anchors:

- `src/server/GuideAntsApi.DataModel/Models/UsageEvent.cs`
- `src/server/GuideAnts.Usage/EfUsageRecorder.cs`
- `src/server/GuideAntsApi/Endpoints/UsageEndpoints.cs`
- `src/server/GuideAntsApi/Endpoints/GuideUsageEndpoints.cs`
- `src/server/GuideAntsApi/Services/PublishedGuides/PublishedGuideCostLimitService.cs`
- `src/server/GuideAntsApi.DataModel/Models/PublishedGuide.cs`
- `src/server/GuideAntsApi.BackgroundJobs/Services/RetentionCleanupScheduler.cs`

### 3.3 Observability for tuning and compliance

Value statement:

GuideAnts treats AI workflows as operated systems: admins can tune logs, inspect readiness, probe infrastructure, and investigate routing/runtime issues without changing code.

Proof features:

- Settings-driven telemetry categories and presets.
- Runtime dependency inventory and URL/path probes.
- Service readiness checks for chat targets and non-chat AI services.
- Local runtime status, inventory, load/unload operations, and crash/recovery flows.
- Structured usage records that double as operational evidence.
- Log sanitization for values emitted to logs.

Evidence anchors:

- `docs/telemetry-configuration.md`
- `src/client/src/pages/settings/components/TelemetryTab.tsx`
- `src/client/src/pages/settings/components/InfrastructureTab.tsx`
- `src/server/GuideAntsApi/Endpoints/SettingsEndpoints.cs`
- `src/server/GuideAnts.Logging/LogValueSanitizer.cs`
- `src/server/GuideAntsApi/Endpoints/NotebookHeaderToolbarEndpoints.cs`
- `src/server/GuideAntsApi/Endpoints/NotebookLlamaRuntimeEndpoints.cs`

### 3.4 Modular secure runtime architecture

Value statement:

GuideAnts separates the web/API control plane from specialized execution services, keeping risky or heavyweight operations behind scoped, token-gated, internal service boundaries.

Proof features:

- `guideants-ai` acts as a consolidated local AI gateway for sandbox execution, media, llama, ASR, TTS, image generation, embeddings, and admin runtime services.
- Slim mode can run sandbox/media while routing model calls to cloud providers.
- The script execution agent requires API-to-agent shared token authentication.
- Every script/listing request requires project and notebook IDs.
- Script paths are canonicalized, bounded by `FILE_STORAGE_ROOT`, validated against notebook metadata, and rejected if they escape notebook scope.
- Reparse-point pivots are rejected.
- Linux execution/listing can run under notebook-scoped low-privilege identities via `setpriv`.
- Nginx paths separate internal services: `/sandbox`, `/llama-cpp`, `/asr`, `/sd`, `/tts`, `/emb`, `/media`, `/llama-admin`.
- Deployment guidance keeps SQL, AI runtime, Docling, DocumentServer, PlantUML, and SearXNG internal, with browser traffic mediated by the API.

Evidence anchors:

- `README.md`
- `src/server/ScriptExecutionAgent/README.md`
- `src/server/ScriptExecutionAgent/Program.cs`
- `src/server/GuideAntsApi/Services/NotebookDockerScriptService.cs`
- `src/server/GuideAntsApi/Services/SandboxToolService.cs`
- `docker/build/guideants-ai/nginx.conf`
- `docker/build/guideants-ai/nginx.slim.conf`
- `docker/docker-compose.slim.yml`
- `docker/docker-compose.cuda.yml`
- `docker/docker-compose.cpu.yml`
- `docker/docker-compose.rocm.yml`

### 3.5 Secrets and access posture

Value statement:

GuideAnts has the beginnings of an enterprise-grade security posture: first-party roles, server-side tool OAuth, masked/encrypted settings secrets, public guide auth, and API-mediated service access.

Proof features:

- App-issued JWT auth and protected route gating.
- Admin approval, role assignment, deactivate/reactivate, and set-password flows.
- DB-backed settings secrets are encrypted and masked in UI/API presentation.
- Published guides support API-key auth and webhook auth.
- Tool OAuth tokens are stored server-side instead of client local storage.

Evidence anchors:

- `src/server/GuideAntsApi/Endpoints/AuthEndpoints.cs`
- `src/server/GuideAntsApi/Endpoints/AdminUsersEndpoints.cs`
- `src/server/GuideAntsApi/Settings/ApplicationSettingsJson.cs`
- `src/server/GuideAntsApi.DataModel/Models/PublishedGuide.cs`
- `src/server/GuideAntsApi/Endpoints/GuidesPublishingEndpoints.cs`
- `src/server/GuideAntsApi/Endpoints/ProjectExternalAuthEndpoints.cs`

## 4. Competitive framing

GuideAnts should avoid generic claims like:

- "Supports many models."
- "Has agents and tools."
- "Includes file upload and RAG."
- "Self-hostable AI chat."

Those are table stakes in the category and are already central to LibreChat/Open WebUI positioning.

Stronger GuideAnts framing:

- "A governed workspace for AI work products."
- "Turn repeatable expertise into reusable, publishable AI experiences."
- "Track the content, cost, and lineage of AI-assisted work."
- "Run local and cloud AI capabilities as modular services with operational controls."
- "Move from internal notebook workflow to controlled public guide without rebuilding the system."

## 5. Candidate final feature list shape

Instead of a long list of implementation features, use a short value-led list:

| Value-led feature | Supporting capability cluster |
|---|---|
| Governed AI workspaces | Projects, notebooks, files, links, versions, lineage, markdown extraction, publish-back. |
| Reusable guides and assistants | Instructions, files, context options, tools, OpenAPI operations, crew, imports/exports. |
| Publishable AI experiences | Public guides, friendly URLs, embeddable chat, API/webhook auth, usage limits, retention. |
| Enterprise cost and usage visibility | Usage events, attribution, model/service metrics, guide/assistant analytics, charge limits. |
| Operable AI infrastructure | Readiness checks, telemetry tuning, runtime probes, model lifecycle, background jobs. |
| Secure modular execution | Internal service topology, token-gated sandbox, notebook-scoped execution, path hardening, pluggable runtimes. |
| Provider-routed multimodal AI | Independent routing for chat, embeddings, document intelligence, image, speech-to-text, text-to-speech, local/cloud backends. |

## 6. Advice for the next revision

Make the enterprise layer explicit in the primary story, not an appendix. It is one of the places GuideAnts can feel more serious than a chat UI:

- Content governance answers: "Can we trust and trace the work?"
- Cost controls answer: "Can we let people use this without surprise bills?"
- Observability answers: "Can we operate and investigate this?"
- Modular sandbox architecture answers: "Can we safely run useful tools and local services?"

The next feature document should probably collapse the 33 Pass A candidates into 7-9 value-led features, with the implementation inventory retained as proof.

