# Project and Notebook Files System — Reference Architecture

This document describes the current logical design, entity relationships, storage mechanisms, processing pipelines, and client-side architecture of the dual file system in GuideAnts.

> Current as of June 2026: storage paths are resolved through `IStoragePathResolver` and use project/notebook slugs for the browsable filesystem layout. Legacy GUID-based paths may still exist in historical database rows and are handled through compatibility code where needed.

---

## 1. Overview: Two File Domains

The platform maintains two distinct but interconnected file domains:

- **Project Files** — a centrally managed, versioned file library belonging to a project. Files are organized in explicit folder hierarchies and each file carries a full version history.
- **Notebook Files** — a filesystem-first file store scoped to an individual notebook. The physical filesystem is the source of truth; a database mirror is maintained via a sync service. Notebook files support lightweight, working-copy semantics for AI conversations and tool outputs.

These two domains are connected by **copy** and **publish** operations that preserve lineage metadata, allowing files to flow bidirectionally between the project library and notebook workspaces.

---

## 2. Core Entities

### 2.1 Project

The top-level container. A project owns notebooks, content files, folders, links, semi-structured data, and external auth configurations. A project may designate a home page content file. Each project has a required `Slug` used as the physical project directory name.

- Server model: `src/server/GuideAntsApi.DataModel/Models/Project.cs`
- Client type: `src/client/src/types/project.ts` (`ProjectDetailsDto`)

### 2.2 ContentFile (Project File)

Represents a versioned file in the project library. Each content file has a unique `DocumentId` generated from `{ProjectId}:{RelativePath}` so project or notebook slug renames do not invalidate embeddings. The file may optionally belong to a `ProjectFolder`. The `LatestVersion` counter tracks the current version number. An `IsSnapshot` flag distinguishes snapshot captures from normal files.

Project file bytes are stored through the service layer, not by `RelativePath` directly:

- Versioned legacy-compatible path: `{FileStorage:Path}/{projectSlug}/files/{contentFileId}/v{version}/{fileName}`
- Content-addressable path: `{FileStorage:Path}/projects/{projectSlug}/content/{aa}/{bb}/{contentHash}`

- Server model: `src/server/GuideAntsApi.DataModel/Models/ContentFile.cs`
- Client type: `src/client/src/types/project.ts` (`ContentFileDto`)

### 2.3 ContentFileVersion

An immutable record of a specific version of a content file. Carries content-addressable storage fields (`ContentHash`, `StoragePath`) alongside the legacy path-based fields. Records the original relative path and folder at time of creation for auditability.

Lineage fields connect back to notebook origins when a file was published from a notebook: `FromNotebook`, `OriginNotebookId`, `OriginNotebookFileId`. The `OriginVersionId` field supports version-chain tracking.

- Server model: `src/server/GuideAntsApi.DataModel/Models/ContentFileVersion.cs`

### 2.4 ProjectFolder

A hierarchical folder node for organizing project content files. Self-referencing via `ParentFolderId` to form a tree. Enforces valid filesystem naming and provides circular-reference prevention for move operations.

- Server model: `src/server/GuideAntsApi.DataModel/Models/ProjectFolder.cs`
- Client type: `src/client/src/types/project.ts` (`FolderTreeDto`, `ProjectFolderDto`)

### 2.5 Notebook

A workspace container within a project. Each notebook is optionally backed by a Guide (an `Assistant` with `Kind = Guide`) and can have many conversations, files, links, and semi-structured data records. A notebook may designate either a file or a conversation as its home page. Each notebook has a required `Slug` unique within its project and used as the physical notebook directory name.

Traceability fields (`SourceNotebookId`, `SourceConversationMessageId`) record when a notebook was created by copying another notebook or spawned from a conversation message.

- Server model: `src/server/GuideAntsApi.DataModel/Models/Notebook.cs`
- Client type: `src/client/src/types/notebook.ts` (`NotebookDetailsDto`)

### 2.6 NotebookFile

Mirrors a physical file on disk within a notebook's dedicated filesystem root. The filesystem is the source of truth; this entity is updated by the `NotebookFileSyncService`. Key fields include `RelativePath` (unique per notebook), `FileSize`, `LastModifiedUtc`, and `FileHash` (SHA-256).

A stable `DocumentId` is generated from `NotebookId` + `RelativePath` (SHA-256, URL-safe Base64) for use in search indexing. The `OriginContentFileVersionId` field preserves lineage when a file originated from a project content file version.

- Server model: `src/server/GuideAntsApi.DataModel/Models/NotebookFile.cs`
- Client type: `src/client/src/types/notebook.ts` (`NotebookFileDto`, `NotebookFolderTreeDto`)

### 2.7 AssistantFile

Binary file resources attached to an assistant or guide. Categorized by `FolderKind` (CodeInterpreter, VectorStore, HostExtensions). Small files may store content inline via `ContentBytes`; larger binaries are stored on disk.

- Server model: `src/server/GuideAntsApi.DataModel/Models/AssistantFile.cs`

---

## 3. Markdown Shadows

Both file domains use a parallel "markdown shadow" mechanism to extract searchable text content from non-text files (PDFs, images, audio/video via transcription).

### 3.1 ContentFileMarkdownShadow

Linked to a `ContentFileVersion`. Tracks extraction status via `MarkdownExtractionStatus` (Pending, Processing, Completed, Failed, Skipped), content hash, storage path, and an `IsIndexed` flag indicating whether the extracted markdown has been embedded into the vector search index.

- Server model: `src/server/GuideAntsApi.DataModel/Models/ContentFileMarkdownShadow.cs`

### 3.2 NotebookFileMarkdownShadow

Same pattern, linked to a `NotebookFile` via `OriginalNotebookFileId`. Identical status tracking and indexing flag.

- Server model: `src/server/GuideAntsApi.DataModel/Models/NotebookFileMarkdownShadow.cs`

### 3.3 MarkdownExtractionStatus Enum

Defines the pipeline stages: `Pending` → `Processing` → `Completed` | `Failed` | `Skipped`. Declared alongside `ContentFileMarkdownShadow`.

---

## 4. Search and Indexing: DocumentChunk

A unified vector search entity that stores text chunks with their embeddings (float array mapped to SQL Server `vector(1536)`). Each chunk belongs to exactly one of three file types via nullable foreign keys: `ContentFileId`, `NotebookFileId`, or `AssistantFileId`.

Denormalized `ProjectId` and `NotebookId` fields enable efficient filtered queries. The `DocumentId` field groups chunks from the same source document, and `ChunkIndex` preserves ordering.

- Server model: `src/server/GuideAntsApi.DataModel/Models/DocumentChunk.cs`

---

## 5. File Lineage and Auditing

### 5.1 FileLineageEvent

An immutable audit record capturing user-initiated file operations. Records the `UserId`, `Action`, `FileKind` (Project or Notebook), `FileId`, optional `VersionNumber`, `ProjectId`, optional `NotebookId`, optional materialized `StoragePath`, and `Timestamp`.

- Server model: `src/server/GuideAntsApi.DataModel/Models/FileLineageEvent.cs`

### 5.2 FileKind Enum

Distinguishes between `Project` (0) and `Notebook` (1) file domains.

- Server model: `src/server/GuideAntsApi.DataModel/Models/FileKind.cs`

### 5.3 FileLineageAction Enum

Enumerates persisted action types: Uploaded, Versioned, CopiedToNotebook, PublishedToProject, Moved, Renamed, Deleted, ExternalWrite, Created. Values are stable (never reordered) since they are stored in the database.

- Server model: `src/server/GuideAntsApi.DataModel/Models/FileLineageAction.cs`

---

## 6. Message Attachments

The `MessageAttachment` entity links a `NotebookConversationMessage` to a `NotebookFile`, creating a junction table that supports multiple attachments per message with ordering. The `AttachmentType` enum distinguishes between files that were `Referenced` by the user, `Created` by the assistant, or `Modified` by the assistant during a conversation turn.

- Server model: `src/server/GuideAntsApi.DataModel/Models/MessageAttachment.cs`

---

## 7. Physical Storage Layout

All file bytes are stored on the local filesystem under a configurable root path (`FileStorage:Path` in application configuration). Application code should resolve physical paths through `IStoragePathResolver` rather than composing storage paths inline.

The current browsable layout uses slugs:

```text
{storage}/
├── {projectSlug}/
│   ├── files/{contentFileId}/v{n}/{fileName}
│   └── {notebookSlug}/
│       ├── .guideants/notebook.json
│       ├── Output/
│       ├── Runs/{runId}/
│       └── ...
└── projects/
    └── {projectSlug}/
        ├── content/{aa}/{bb}/{contentHash}
        └── {notebookSlug}/markdown/{aa}/{bb}/{contentHash}.md
```

### 7.1 Project Files

Version records store immutable file bytes in content-addressable storage:

- `{FileStorage:Path}/projects/{projectSlug}/content/{aa}/{bb}/{contentHash}`

`ContentFileVersion.Path` may also contain a legacy/versioned path for compatibility. New path construction should use `ContentFileService` and `IStoragePathResolver`; `ContentFile.GetPhysicalPath()` is legacy and should not be used for new storage behavior.

### 7.2 Notebook Files

Stored at: `{FileStorage:Path}/{projectSlug}/{notebookSlug}/...`

Each notebook root contains `.guideants/notebook.json`, which records `ProjectId` and `NotebookId`. This metadata lets the script execution agent authorize a path by association rather than trusting a human-readable folder name.

The `NotebookPathHelper` class resolves working directories for both private and published contexts:

- **Private notebooks**: tool and script outputs go to an `Output/` subdirectory within the notebook root.
- **Published notebooks**: each invocation gets an isolated `Runs/{RunId}` directory, where `RunId` is a cryptographically random 10-character base-62 identifier.

- Server: `src/server/GuideAntsApi/Services/NotebookPathHelper.cs`

### 7.3 Container Paths

For Docker-based script execution, the host content-file root is mounted into containers at `/app/ContentFiles`. `NotebookPathHelper.GetWorkingDirectory()` returns:

- Private notebook runs: `/app/ContentFiles/{projectSlug}/{notebookSlug}/Output`
- Published notebook runs: `/app/ContentFiles/{projectSlug}/{notebookSlug}/Runs/{runId}`

Before returning the container path, `NotebookPathHelper` resolves the local notebook root so `.guideants/notebook.json` exists for script-agent authorization.

### 7.4 Legacy Path Compatibility

Historical rows may still reference GUID-based paths such as `{storage}/{projectGuid}/notebooks/{notebookGuid}/...` or `{storage}/projects/{projectGuid}/...`. Content retrieval uses `StoragePathCompatibility` where needed to resolve legacy `StoragePath` and `Path` values after migration.

---

## 8. Database Layer

### 8.1 DbContext

`ApplicationDbContext` in `src/server/GuideAntsApi.DataModel/ApplicationDbContext.cs` exposes DbSets for all file-related entities: `Notebooks`, `NotebookFiles`, `NotebookFileMarkdownShadows`, `ContentFiles`, `ContentFileVersions`, `ContentFileMarkdownShadows`, `ProjectFolders`, `DocumentChunks`, `MessageAttachments`, `FileLineageEvents`, `AssistantFiles`, and `AssistantFileMarkdownShadows`.

The `DocumentChunk.Embedding` property is mapped to SQL Server's native `vector(1536)` column type.

### 8.2 Design-Time Factory

`src/server/GuideAntsApi.DataModel/ApplicationDbContextDesignTimeFactory.cs` supports EF Core migrations tooling.

### 8.3 Migrations

Located in `src/server/GuideAntsApi.DataModel/Migrations/`.

---

## 9. Server Services

### 9.1 NotebookFileService

Implements `INotebookFileService`. Handles file listing, content retrieval, upload (with FormData), folder creation, rename, move, and delete operations. File bytes are written under the notebook root resolved by `IStoragePathResolver`; database rows in `NotebookFile` are created or updated to mirror the filesystem state.

- Server: `src/server/GuideAntsApi/Services/Components/NotebookFileService.cs`

### 9.2 NotebookFileSyncService

Reconciles the on-disk state of a notebook's file tree with the `NotebookFile` database table. During a sync pass, the service walks the filesystem, compares file hashes and timestamps against existing database records, creates new rows for discovered files, updates modified rows, and removes rows for files no longer on disk. Sync also triggers downstream markdown extraction and indexing jobs for changed files.

- Server: `src/server/GuideAntsApi/Services/Components/NotebookFileSyncService.cs`

### 9.3 ContentFileService

Manages CRUD operations for project content files. Handles version creation, content-addressable storage, folder assignment, rename/move metadata updates, and delete with cascade considerations.

- Server: `src/server/GuideAntsApi/Services/Components/ContentFileService.cs`

### 9.4 NotebookService

High-level notebook lifecycle management including creation, update, delete, copy, and template-based initialization.

- Server: `src/server/GuideAntsApi/Services/Core/ProjectService.cs` (project-level operations)
- Server: `src/server/GuideAntsApi/Services/NotebookTemplateService.cs` (template and assistant list resolution)

### 9.5 NotebookCopyService

Handles deep-copy operations when creating a notebook from an existing one, including file duplication.

### 9.6 StoragePathResolver

Central path service for storage roots, project roots, notebook roots, container notebook roots, content-addressable paths, and markdown shadow paths. It resolves GUID inputs to current slugs, caches those mappings, creates notebook roots as needed, writes notebook association metadata, and can discover externally renamed notebook folders by reading `.guideants/notebook.json`.

- Server: `src/server/GuideAntsApi/Services/StoragePathResolver.cs`

### 9.7 ScriptExecutionAgent Path Guard

The script execution agent authorizes working directories by:

1. Resolving the candidate path under `FILE_STORAGE_ROOT`.
2. Walking upward until it finds `.guideants/notebook.json`.
3. Verifying the metadata matches the requested `ProjectId` and `NotebookId`.
4. Rejecting paths that escape the notebook root or cross reparse points.

- Server: `src/server/ScriptExecutionAgent/Program.cs`

Execution runtime scope is separate from path authorization:

- File access remains bounded by `ProjectId + NotebookId` and notebook metadata.
- Python venv/package state is bounded by `ProjectId + GuideId`, so notebooks in the same project that use the same guide share one venv.
- Scoped Python venvs extend the image-provided base venv (`/opt/venv` by default on Linux), so guide-scoped packages add to the baked runtime instead of hiding it.
- Environment variables and secret values, when provided, are per-run values resolved by the API from the project-bounded notebook guide scope. That scope includes the guide configuration plus the guide crew members' configurations for the same project. Secret values are encrypted at rest in API storage, and the script agent does not persist credential files.

---

## 10. API Endpoints

### 10.1 Notebook Files

Base: `/api/projects/{projectId}/notebooks/{notebookId}/files`

Operations include: list files, get folder tree, get file content by path, upload files (multipart FormData), create folder, rename, move, delete, sync (triggers `SyncNotebookJob`), copy from project, publish to project, get origin info, and home page management.

- Server: `src/server/GuideAntsApi/Endpoints/NotebookEndpoints.cs`

### 10.2 Notebook File Markdown

Base: `/api/projects/{projectId}/notebooks/{notebookId}/files/{fileId}/markdown`

Endpoints for retrieving markdown shadow metadata, downloading extracted markdown content, and retrying failed extractions.

- Server: `src/server/GuideAntsApi/Endpoints/NotebookFileMarkdownEndpoints.cs`

### 10.3 Project Content Files

Base: `/api/projects/{projectId}/files`

CRUD for project files and versions: list, get details, get content, create, patch metadata, delete, move, rename, version history, and markdown shadow endpoints.

- Server: `src/server/GuideAntsApi/Endpoints/ProjectContentFileEndpoints.cs`
- Server: `src/server/GuideAntsApi/Endpoints/ProjectContentFileMarkdownEndpoints.cs`

### 10.4 Project Folders

Base: `/api/projects/{projectId}/folders`

CRUD for the folder tree: get tree, create, rename, move, delete.

- Server: `src/server/GuideAntsApi/Endpoints/ProjectFolderEndpoints.cs`

### 10.5 File Lineage

Base: `/api/lineage`

Query lineage events and download file versions from lineage records.

- Server: `src/server/GuideAntsApi/Endpoints/FileLineageEndpoints.cs`

### 10.6 Endpoint Registration

All endpoint groups are registered via extension method calls in `src/server/GuideAntsApi/Program.cs`.

---

## 11. Background Job Pipeline

A SQL-backed job queue (`JobQueue` entity) drives asynchronous processing. The `BackgroundJobProcessor` polls and dispatches jobs to registered handlers. Job payloads are defined as records in `src/server/GuideAntsApi.BackgroundJobs/Jobs/JobPayloads.cs`.

### 11.1 File Processing Pipeline

The pipeline processes files through three stages:

1. **Extraction** — Convert non-text files to markdown text. Handlers: `ExtractContentVersionMarkdownHandler`, `ExtractNotebookFileMarkdownHandler`. Uses Azure Document Intelligence for PDFs and images.

2. **Transcription** — Convert audio/video files to text. Handlers: `TranscribeContentVersionMarkdownHandler`, `TranscribeNotebookFileMarkdownHandler`.

3. **Indexing** — Chunk the extracted markdown and compute vector embeddings, storing results as `DocumentChunk` rows. Handlers: `IndexContentMarkdownShadowHandler`, `IndexNotebookMarkdownShadowHandler`, `IndexDirectTextFileHandler`.

### 11.2 Notebook Sync Job

`SyncNotebookJob` / `SyncNotebookHandler` — triggered manually via the API or after operations that may modify disk state. Delegates to `NotebookFileSyncService`.

### 11.3 Other File Jobs

- `ExtractAssistantFileMarkdownHandler` and `IndexAssistantFileMarkdownShadowHandler` — same pipeline for assistant-attached files.
- `RebuildEmbeddingsHandler` — global re-index of all document chunks.
- `RetentionCleanupHandler` — cleans up published guide run directories and expired data.

### 11.4 Job Infrastructure

- Job queue service: `src/server/GuideAntsApi.BackgroundJobs/JobQueueService.cs`
- Processor: `src/server/GuideAntsApi.BackgroundJobs/BackgroundJobProcessor.cs`
- Handler registration: `src/server/GuideAntsApi/Configuration/StartupConfiguration.cs`
- Supporting services (embeddings, Document Intelligence, hybrid search): `src/server/GuideAntsApi.BackgroundJobs/Services/`

---

## 12. Cross-Domain Operations: Copy and Publish

### 12.1 Copy from Project to Notebook

A project `ContentFileVersion` is copied into the notebook's filesystem. The resulting `NotebookFile` record stores the `OriginContentFileVersionId` to preserve provenance. A `FileLineageEvent` with action `CopiedToNotebook` is recorded.

Client API: `notebookFilesApi.copyFromProject()` in `src/client/src/services/notebookFiles.ts`

### 12.2 Publish from Notebook to Project

A `NotebookFile` is published as a new `ContentFile` (or new version of an existing one) in the project library. The resulting `ContentFileVersion` records `FromNotebook = true`, `OriginNotebookId`, and `OriginNotebookFileId`. A `FileLineageEvent` with action `PublishedToProject` is recorded.

Client API: `notebookFilesApi.publishToProject()` in `src/client/src/services/notebookFiles.ts`
Client types: `PublishNotebookFileDto`, `PublishNotebookFileResultDto` in `src/client/src/types/notebook.ts`

### 12.3 Origin Info Resolution

The client can query origin file information for notebook files that came from the project library, enabling the UI to display provenance (original file name, folder, version number).

Client API: `notebookFilesApi.getOriginFileInfo()` in `src/client/src/services/notebookFiles.ts`
Client type: `OriginFileInfoDto` in `src/client/src/types/notebook.ts`

---

## 13. Client Architecture

### 13.1 Type System

The client-side type system mirrors the server DTOs:

- **Project types**: `src/client/src/types/project.ts` — `ProjectDetailsDto`, `ContentFileDto`, `ContentFileDetailsDto`, `FolderTreeDto`, `ProjectFolderDto`
- **Notebook types**: `src/client/src/types/notebook.ts` — `NotebookDetailsDto`, `NotebookFileDto`, `NotebookFolderTreeDto`, `NotebookConversationDto`, publish/origin DTOs
- **API types**: `src/client/src/types/api.ts` — supplementary DTOs for markdown shadows and file-in-use checks
- **Conversation types**: `src/client/src/types/conversation.ts` — `MessageDto`, streaming types, `PendingAttachment`

### 13.2 API Services

Two service modules handle file operations:

- **General API**: `src/client/src/services/api.ts` — the `api` object exposes `api.projects.files.*` for project content files, `api.projects.folders.*` for folder operations, and `api.projects.notebooks.*` for notebook-level operations including file upload, conversations, and streaming.
- **Notebook Files API**: `src/client/src/services/notebookFiles.ts` — the `notebookFilesApi` object provides dedicated notebook file operations: list, tree, upload, rename, move, delete, sync, copy from project, publish to project, origin info, and markdown shadow retrieval.

### 13.3 Client-Side File Caching

Notebook file content is cached in IndexedDB via the `fileCache` utility module. The cache uses a composite key of `{projectId}:{notebookId}:{relativePath}` and stores the file blob alongside its content type, file name, and SHA-256 hash. Cache freshness is validated by comparing the stored hash against the current hash reported by the folder tree API.

Cache entries are proactively invalidated on upload, rename, move, delete, copy, and sync operations.

- Client: `src/client/src/utils/fileCache.ts`

### 13.4 React Contexts

Three domain contexts manage file-related state:

- **ProjectContext** (`src/client/src/contexts/ProjectContext.tsx`) — loads project details, folder tree, manages file selection, rename operations, and permission checks.
- **NotebookContext** (`src/client/src/contexts/NotebookContext.tsx`) — loads notebook details, manages file CRUD operations (upload, create folder, delete, rename, move, copy from project), conversations list, and assistant metadata. Exposes home page management actions.
- **ConversationContext** (`src/client/src/contexts/ConversationContext.tsx` and `src/client/src/contexts/conversation/`) — manages active conversation state including message streaming, pending file attachments, and assistant selection. Depends on `NotebookContext` for file refresh after tool outputs.

### 13.5 Polling Hooks

The UI keeps file trees fresh via polling:

- `src/client/src/hooks/useNotebookFilesPolling.ts` — periodically fetches the notebook folder tree
- `src/client/src/hooks/useProjectFilesPolling.ts` — periodically fetches the project content file list
- `src/client/src/hooks/useProjectNotebooksPolling.ts` — periodically fetches the notebook list for a project

### 13.6 Conversation Store

The `conversationStore` (`src/client/src/store/conversationStore.ts`) normalizes messages and handles `attachedNotebookFileId` references, linking conversation messages to notebook files.

### 13.7 Routing

Route definitions in `src/client/src/components/AppContent.tsx`:

- `/projects/:projectId` — project detail view with sidebar file tree
- `/projects/:projectId/notebooks/:notebookId` — notebook view with file sidebar and conversation panel
- `/projects/:projectId/notebooks/:notebookId/edit` — notebook edit page
- `/projects/:projectId/notebooks/:notebookId/files/preview` — dedicated file preview page

Project-scoped routes wrap children in a `ProjectProvider`. The notebook view wraps content in a `NotebookProvider`, and the active conversation panel uses a `ConversationProvider`.

### 13.8 Key UI Components

**Notebook file UI** (`src/client/src/components/notebook/`):

- `sidebar/NotebookSidebar.tsx` — notebook sidebar with sections for files, conversations, and links
- `sidebar/NotebookFolderTree.tsx` — recursive folder tree component for notebook files with drag-and-drop, context menus, rename, move, and delete
- `content/NotebookContent.tsx` — main content area
- `content/FilePreviewOverlay.tsx` — modal file preview
- `content/InlineFileViewer.tsx` — inline file rendering within conversations
- `dialogs/` — upload, publish-to-project, and save-assistant-content dialogs
- `conversations/` — Lexical-based editors, conversation cells, markdown rendering, file attachment UI

**Project file UI** (`src/client/src/components/project/`):

- `sidebar/ProjectSidebar.tsx` — project sidebar with folder tree and file list
- `content/ContentFileContent.tsx` — content file detail view
- `content/FileContents.tsx` — file content rendering
- `dialogs/CreateNotebookDialog.tsx` — create notebook from project context

---

## 14. Data Flow Diagrams (Logical)

### 14.1 Notebook File Lifecycle

1. Files arrive on disk via upload (API multipart), tool output (script execution container writes to `Output/` or `Runs/{runId}`), or copy-from-project.
2. `NotebookFileSyncService` reconciles disk state with `NotebookFile` database records.
3. Changed or new files trigger `ExtractNotebookFileMarkdownJob` or `TranscribeNotebookFileMarkdownJob`.
4. Successful extraction creates a `NotebookFileMarkdownShadow` record and triggers `IndexNotebookMarkdownShadowJob`.
5. The indexing job chunks the markdown text, computes embeddings, and stores `DocumentChunk` rows linked to the `NotebookFile`.
6. The UI polls the folder tree for updates and uses IndexedDB-cached content with hash-based freshness checks.

### 14.2 Project File Lifecycle

1. Files are uploaded via the project content file API.
2. A `ContentFile` and initial `ContentFileVersion` are created; bytes are written to content-addressable storage.
3. The version triggers `ExtractContentVersionMarkdownJob` or `TranscribeContentVersionMarkdownJob`.
4. Successful extraction creates a `ContentFileMarkdownShadow` and triggers `IndexContentMarkdownShadowJob`.
5. Indexing produces `DocumentChunk` rows linked to the `ContentFile`.
6. Subsequent uploads create new `ContentFileVersion` records, incrementing `LatestVersion`.

### 14.3 Cross-Domain Flow

1. **Copy to Notebook**: Project `ContentFileVersion` bytes are copied to the notebook's disk root. A `NotebookFile` is created with `OriginContentFileVersionId` set. Standard notebook sync and indexing follow.
2. **Publish to Project**: A `NotebookFile`'s bytes are written as a new `ContentFileVersion` (or new `ContentFile`). The version records `FromNotebook`, `OriginNotebookId`, and `OriginNotebookFileId`. Standard project indexing follows.

### 14.4 Conversation File Attachments

1. User attaches a notebook file to a message via the conversation UI.
2. The `MessageAttachment` record links the `NotebookConversationMessage` to the `NotebookFile` with `AttachmentType.Referenced`.
3. During a conversation turn, if the assistant creates or modifies files (via tools), `MessageAttachment` records with `AttachmentType.Created` or `AttachmentType.Modified` link those files to the assistant's response message.
4. The conversation service's `AttachmentMessageBuilder` constructs LLM message payloads from attached file content.

---

## 15. Entity Relationship Summary

- **Project** → many `ContentFile`, `ProjectFolder`, `Notebook`; optional `HomePageContentFile`
- **ProjectFolder** → self-referencing parent/child tree; many `ContentFile`
- **ContentFile** → many `ContentFileVersion`; optional `ProjectFolder`; many `DocumentChunk`
- **ContentFileVersion** → optional `ContentFileMarkdownShadow`; optional `OriginVersion` (version chain); optional `OriginNotebookFile` (publish lineage)
- **Notebook** → `Project`; optional `Guide` (`Assistant`); many `NotebookConversation`, `NotebookFile`; optional `HomePageFile`, `HomePageConversation`; optional `SourceNotebook`
- **NotebookFile** → `Notebook`; optional `OriginContentFileVersion` (copy lineage); many `DocumentChunk`, `MessageAttachment`
- **NotebookFileMarkdownShadow** → `NotebookFile`
- **DocumentChunk** → exactly one of `ContentFile` | `NotebookFile` | `AssistantFile`
- **MessageAttachment** → `NotebookConversationMessage` + `NotebookFile`
- **FileLineageEvent** — standalone audit record referencing `FileId`, `FileKind`, `ProjectId`, optional `NotebookId`
- **AssistantFile** → `Assistant`; many `DocumentChunk`
