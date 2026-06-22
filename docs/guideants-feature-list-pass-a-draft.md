# GuideAnts Feature List - Pass A Draft (Bottom-up + Top-down)

Status: Draft v1
Date: 2026-06-08
Related: `docs/guideants-feature-list-analysis-approach.md`

## 1. Scope of this pass

This Pass A draft produces:

1. Initial pillar taxonomy.
2. First draft feature matrix with evidence anchors.
3. Candidate headline differentiators for review.

This is intentionally broad-first and evidence-backed. Language is feature-list ready, but not final copy.

## 2. Evidence footprint used

Bottom-up anchors:

- Data model: `src/server/GuideAntsApi.DataModel/ApplicationDbContext.cs`
- Data model ERD/context: `src/server/GuideAntsApi.DataModel/README.md`
- API endpoints: `src/server/GuideAntsApi/Endpoints/`
- Endpoint registration: `src/server/GuideAntsApi/Program.cs`
- Background jobs: `src/server/GuideAntsApi.BackgroundJobs/Jobs/`
- Bootstrap seeding: `src/server/GuideAntsApi/Resources/bootstrap/README.md`

Top-down anchors:

- Route map: `src/client/src/components/AppContent.tsx`
- Core pages: `src/client/src/pages/`
- Settings shell and IA: `src/client/src/pages/Settings.tsx`, `src/client/src/pages/settings/components/SettingsTabNavigation.tsx`
- Service editors: `src/client/src/pages/settings/editors/`

## 3. Initial pillar taxonomy

| Pillar ID | Pillar | What it covers |
|---|---|---|
| P1 | Workspace and Content System | Projects, notebooks, files/folders, versioning, lineage, file movement across project and notebook contexts. |
| P2 | Notebook AI Workbench | In-notebook conversations, assistant-aware runtime behavior, message lifecycle, and runtime readiness controls. |
| P3 | Guide and Assistant Authoring | Creation and maintenance of reusable guide/assistant assets, tool definitions, and packaged exports/imports. |
| P4 | Publishing and External Experiences | Public guide delivery, invocation APIs, auth controls, limit controls, and hosted/embed-style consumption. |
| P5 | AI Service Routing and Model Operations | Per-service provider routing, model catalog/runtime profiles, local model lifecycle, and provider-specific readiness. |
| P6 | Identity, Access, and External Auth | First-party auth, role/admin controls, personalization, and project-scoped third-party OAuth connections. |
| P7 | Usage, Analytics, and Telemetry | Cost/activity analytics (global/project/guide/assistant) and operator telemetry controls. |
| P8 | Reliability and Runtime Operations | Infrastructure dependency checks, operational probes, async jobs, and retention/cleanup flows. |

## 4. First draft feature matrix

| Feature ID | Pillar | Feature candidate (user language) | Primary UI entry points | API/DB evidence anchors | Confidence |
|---|---|---|---|---|---|
| F-001 | P1 | Create and manage long-lived projects as durable workspaces. | `/projects`, `/new-project`, `/projects/:projectId` | `ProjectEndpoints.cs`, `Projects.tsx`, `ApplicationDbContext.cs` (`Projects`) | High |
| F-002 | P1 | Organize files and folders with full CRUD and move/rename flows. | `ProjectDetails.tsx`, project sidebar | `ProjectFolderEndpoints.cs`, `ProjectContentFileEndpoints.cs`, `ApplicationDbContext.cs` (`ProjectFolders`, `ContentFiles`) | High |
| F-003 | P1 | Maintain file version history and retrieve prior content revisions. | Project file views in `ProjectDetails.tsx` | `ProjectContentFileEndpoints.cs` (version routes), `ApplicationDbContext.cs` (`ContentFileVersions`) | High |
| F-004 | P1 | Move content between project storage and notebook working context, then publish back. | `NotebookDetails.tsx`, notebook file actions | `NotebookEndpoints.cs` (`copy-from-project`, `publish-to-project`, `sync`), `ApplicationDbContext.cs` (`NotebookFiles`, lineage-related entities) | High |
| F-005 | P1 | Extract markdown views from files with retry for document workflows. | File preview/content flows in project/notebook pages | `ProjectContentFileMarkdownEndpoints.cs`, `NotebookFileMarkdownEndpoints.cs`, background extract/index handlers | High |
| F-006 | P1 | Track file lineage events for provenance and download traceability. | File-related actions in project/notebook UI | `FileLineageEndpoints.cs`, `ApplicationDbContext.cs` (`FileLineageEvents`) | High |
| F-007 | P2 | Run persistent notebook conversations with full thread lifecycle. | `/projects/:projectId/notebooks/:notebookId` | `NotebookConversationsEndpoints.cs`, `NotebookDetails.tsx`, `ApplicationDbContext.cs` (`NotebookConversations`, `NotebookConversationMessages`) | High |
| F-008 | P2 | Edit, delete, and save conversation turns for iterative refinement. | Conversation panel in `NotebookDetails.tsx` | `NotebookConversationsEndpoints.cs` (`PATCH/DELETE/save-as` message routes), `ApplicationDbContext.cs` (`MessageEditHistories`) | High |
| F-009 | P2 | Auto-generate conversation titles and manage conversation lists globally. | `Conversations.tsx`, notebook conversation UI | `NotebookConversationsEndpoints.cs` (`title/generate`), `UserConversationsEndpoints.cs` | High |
| F-010 | P2 | Surface chat readiness and enforce model/runtime gating in notebook UX. | `NotebookServiceToolbar`, `NotebookDetails.tsx` dialogs | `NotebookHeaderToolbarEndpoints.cs` (`chat-readiness`), `NotebookDetails.tsx` (`NoChatModelDialog`, readiness hooks) | High |
| F-011 | P2 | Control local llama runtime from notebook flows (load/unload/restart/status). | `LlamaRuntimeModal` in notebook conversation flow | `NotebookLlamaRuntimeEndpoints.cs` | High |
| F-012 | P3 | Create, edit, duplicate, import, and export guides and assistants. | `GuidesDashboard.tsx`, `GuideEditor.tsx`, `AssistantEditor.tsx` | `GuidesEndpoints.cs`, `AssistantsEndpoints.cs`, `BaseEntityEditor` wrappers | High |
| F-013 | P3 | Author assistant tool surfaces including OpenAPI-backed operations. | Guide/assistant editor flows | `GuidesEndpoints.cs` (`operations` group + preview), `ApplicationDbContext.cs` (`AssistantTools`, `AssistantOpenApiSchemas`, `AssistantOpenApiOperations`) | High |
| F-014 | P3 | Validate runtime compatibility for guide definitions before use. | Guide authoring workflow | `GuidesEndpoints.cs` (`/runtime/validate`) | High |
| F-015 | P3 | Start from seeded first-party guides/assistants and runtime profile templates. | Guide dashboard and model/runtime settings | `Resources/bootstrap/README.md`, bootstrap seeding services wired in `Program.cs` | High |
| F-016 | P4 | Publish guides with friendly names and project-scoped public availability. | Guides dashboard publish actions, public route `/public/:friendlyName` | `GuidesPublishingEndpoints.cs`, `PublishedGuidesEndpoints.cs`, `PublicGuide.tsx` | High |
| F-017 | P4 | Configure public guide experience settings (display/command/starter/attachment controls). | Publish/edit dialogs and public guide page | `PublishedGuidesEndpoints.cs` (response fields include display and interaction flags), `PublicGuide.tsx` | Medium |
| F-018 | P4 | Protect published guides with webhook/API-key auth and enforce invocation limits. | Public guide usage and invocation surfaces | `GuidesPublishingEndpoints.cs` (API key lifecycle), `PublishedGuidesEndpoints.cs` (auth + limit checks), `PublishedGuide` model usage in DB | High |
| F-019 | P4 | Support public conversation/file interactions against published notebooks. | Public guide chat experience | `PublishedNotebookConversationsEndpoints.cs` (messages/files/tool result routes) | High |
| F-020 | P5 | Configure each AI service independently (chat, embeddings, image, ASR, TTS, doc intelligence). | `Settings.tsx` -> Services/Connections/Overview | `SettingsEndpoints.cs` (service editor routes), `ServicesTab.tsx`, service editor components | High |
| F-021 | P5 | Manage model catalog and runtime profiles, including global default chat target behavior. | `Settings.tsx` -> Models and Runtime + Overview | `SettingsEndpoints.cs` (`models`, `runtime-profiles`, `chat-defaults`, routing preflight/readiness), `ModelsRuntimeWorkspace.tsx`, `OverviewTab.tsx` | High |
| F-022 | P5 | Run local model lifecycle operations (download/load/unload/select/delete) by service. | Service editors (`AsrModelManager`, `TtsModelManager`, `EmbRuntimeManager`, image bundle manager) | `SettingsEndpoints.cs` (`/services/{serviceId}/local-models/*`, llama runtime routes), service editor files | High |
| F-023 | P5 | Browse Hugging Face repositories server-side for model/file selection workflows. | Settings model onboarding and repository pickers | `SettingsEndpoints.cs` (`/settings/huggingface/repositories/{owner}/{repo}/files`), `RepositoryFilePicker.tsx` | High |
| F-024 | P6 | Use first-party account auth (register/login/logout/me) with protected route gating. | `/login`, `/register`, protected routes in `AppContent.tsx` | `AuthEndpoints.cs`, `AppContent.tsx`, `Program.cs` auth middleware | High |
| F-025 | P6 | Admins can approve/deactivate/reactivate users and set roles/passwords. | `Settings.tsx` -> Users tab | `AdminUsersEndpoints.cs`, `SettingsTabNavigation.tsx` (`Users` admin-only) | High |
| F-026 | P6 | Configure project-scoped external OAuth providers for tool integrations. | Project auth section (`ProjectDetails.tsx` includes auth content) | `ProjectExternalAuthEndpoints.cs`, `ApplicationDbContext.cs` (`ProjectExternalAuths`, token/auth state entities) | High |
| F-027 | P6 | Persist per-user personalization preferences. | `Settings.tsx` -> Personalization tab | `UserEndpoints.cs` (`/current/personalization`) | High |
| F-028 | P7 | Analyze usage and cost by time, project, and category. | `/usage` | `UsageEndpoints.cs`, `Usage.tsx`, `ApplicationDbContext.cs` (`UsageEvents`, usage categories) | High |
| F-029 | P7 | Drill into guide and assistant analytics (summary/charts/crew/conversations/turns). | Guide usage pages from guides dashboard | `GuideUsageEndpoints.cs`, `GuideUsagePage` route wiring in `AppContent.tsx` | High |
| F-030 | P7 | Tune telemetry verbosity by subsystem with preset-driven controls. | `Settings.tsx` -> Telemetry tab | `TelemetryTab.tsx`, settings section read/write in `SettingsEndpoints.cs` | High |
| F-031 | P8 | Manage runtime dependencies and run infrastructure probes from admin settings. | `Settings.tsx` -> Infrastructure tab | `InfrastructureTab.tsx`, `SettingsEndpoints.cs` (`/infrastructure/dependencies`, `/infrastructure/probes`) | High |
| F-032 | P8 | Execute async background processing for extraction, indexing, transcription, embeddings rebuild, and cleanup. | Indirectly surfaced in settings/file workflows | `GuideAntsApi.BackgroundJobs/Jobs/*`, `SettingsEndpoints.cs` (`/embeddings/rebuild`), retention scheduler services | High |
| F-033 | P8 | Integrate DocumentServer for capabilities, editor config, download, callback, and diagnostics. | File preview/editor surfaces, file preview route | `DocumentServerEndpoints.cs`, `FilePreviewPage` route in `AppContent.tsx` | Medium |

## 5. Candidate headline differentiators (for validation)

These are the strongest candidates for top-level messaging, pending your validation:

1. Dual workspace architecture: project system-of-record plus notebook working context, with copy/sync/publish-back and lineage tracking.
2. Productized AI experiences: guides and assistants are not just prompts; they are authored assets with tool schemas, publishing, auth, limits, and usage analytics.
3. Service-by-service AI routing: chat, embeddings, image, ASR, TTS, and document intelligence can be independently wired across local and cloud providers.
4. Operator-grade local AI controls: in-product model/runtime lifecycle and Hugging Face repository-assisted onboarding rather than manual config only.
5. Built-in operational layer: infrastructure probes, telemetry controls, and cost/usage visibility across both internal and published experiences.

## 6. Gaps to resolve in Pass B

- Reduce overlap among P5/P8 items where operator experience and runtime internals blur.
- Decide whether public guide UX controls (F-017) are headline or supporting.
- Confirm whether DocumentServer (F-033) should stay explicit in core list or be moved to proof-point appendix.
- Normalize language for audience variants (buyer-facing vs technical evaluator).

