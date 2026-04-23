# Migration Plan: GUID-Based to Name-Based File Storage

This document details the risks, constraints, and solutions for migrating the file storage layout from GUID-based directory names to human-readable project and notebook names, targeting a single-user, dedicated-device deployment with containerized services.

---

## Progress Update (2026-04-15)

### Reality Check

The migration is now at target state for active data.

- The one-time physical migration has been executed against `docker/volumes/content-files`.
- Active project roots were migrated from GUID to `projectSlug`.
- Active notebook roots were migrated from `{projectSlug}/notebooks/{notebookGuid}` to `{projectSlug}/{notebookSlug}`.
- Active CAS roots were migrated from `projects/{projectGuid}/...` to `projects/{projectSlug}/...`, with legacy notebook markdown GUID folders migrated to slug folders.
- Unmapped/orphan legacy GUID folders were moved out of the active tree into `docker/volumes/content-files/_legacy-unmapped/...` to keep active storage fully name-based.
- Verification now reports:
  - zero GUID-named project roots under active `content-files/`
  - zero GUID-named project roots under active `content-files/projects/`
  - zero active `.../notebooks/{notebookGuid}/...` folders

### Completed

1. **Slug foundation implemented**
   - `Project.Slug` and `Notebook.Slug` added.
   - Uniqueness indexes added for `Project.Slug` and `(Notebook.ProjectId, Notebook.Slug)`.
   - Slug generation utility implemented (`SlugGenerator`).

2. **Centralized path resolution implemented**
   - Added `StoragePathResolver` for slug-based path resolution.
   - Added `LegacyStoragePathResolver` for compatibility where legacy GUID path assumptions still exist.
   - Added notebook association metadata support via `.guideants/notebook.json`.

3. **Path migration and service refactor applied (code paths)**
   - Updated API services, background jobs, endpoints, and helper components to use named paths with compatibility fallback where needed.
   - Updated sandbox/conversation path normalization for new slug path layout.
   - Updated `ScriptExecutionAgent` path validation for named storage paths.

4. **DocumentId migration work completed**
   - `ContentFile.GenerateDocumentId()` now uses stable logical identity (`ProjectId + RelativePath`) instead of absolute physical path.
   - EF migration added to regenerate `ContentFile.DocumentId` and update `DocumentChunk.DocumentId` references.

5. **Deletion behavior aligned with single-user target**
   - Project deletion updated to hard delete + filesystem cleanup.

6. **Testing status now green**
   - `GuideAntsApi.IntegrationTests`: **72 passed, 0 failed**
   - `GuideAntsApi.Tests`: **211 passed, 0 failed**
   - `GuideAntsApi` build: **succeeds (0 errors)**
   - Migration-targeted sync tests pass.

7. **One-time migration runner implemented and exercised**
   - Added CLI entrypoint: `dotnet run --project src/server/GuideAntsApi/GuideAntsApi.csproj -- --run-named-storage-migration --apply`
   - Supports dry-run and apply modes.
   - Performs filesystem moves + lock-step DB path rewrites with transaction rollback handling.
   - Creates/refreshes notebook association metadata in `.guideants/notebook.json`.
   - Archives unmatched legacy GUID directories into `_legacy-unmapped`.

8. **Post-migration compatibility and DB normalization completed**
   - Added/extended compatibility fallback resolution for legacy GUID-era stored paths while serving from the slug-based filesystem.
   - Ran DB normalization to rewrite legacy GUID path formats in:
     - `ContentFiles.Path`
     - `ContentFileVersions.Path` and `ContentFileVersions.StoragePath`
     - `ContentFileMarkdownShadows.StoragePath`
     - `NotebookFileMarkdownShadows.StoragePath`
     - `AssistantFileMarkdownShadows.StoragePath`
   - Legacy GUID path-format counts in active DB rows are now zero.

9. **Smoke-test validation passed**
   - File preview/content routes now resolve correctly in the migrated environment, including:
     - `/api/projects/{projectId}/files/{fileId}/content`
     - `/api/projects/{projectId}/files/{fileId}/versions/{version}/markdown/content`
     - `/api/projects/{projectId}/notebooks/{notebookId}/files/{fileId}/markdown/content`

10. **ASCII-safe slug normalization completed**
   - Added safe slug encoding so filesystem names remain plain ASCII and shell/script-friendly.
   - Added one-time CLI normalization runner:
     - `dotnet run --project src/server/GuideAntsApi/GuideAntsApi.csproj -- --run-ascii-slug-normalization` (dry-run)
     - `dotnet run --project src/server/GuideAntsApi/GuideAntsApi.csproj -- --run-ascii-slug-normalization --apply` (apply)
   - Applied normalization in this environment and verified follow-up dry-run reports zero remaining slug changes.

### In Place for Test Reliability

1. Integration test host now force-injects the testcontainer SQL connection string so DB-backed settings load from test DB, not local dev defaults.
2. Integration test config includes service-routing/provider defaults needed by startup validation.
3. Integration tests use a fake chat-completion client factory to avoid external LLM dependency during conversation streaming tests.

### Remaining / Follow-up (Critical)

1. Remove legacy fallback code paths after stabilization window (`LegacyStoragePathResolver`, compatibility-only fallbacks where no longer needed).
2. Decide whether to keep or remove published guide surfaces for single-user deployment.
3. Continue next-phase design for robust external filesystem change detection (watchers/polling/reconciliation/manual refresh behavior).

### Known Residual Data Gap (Post-Migration Audit)

Path-format migration is complete; however, a small number of DB rows reference artifacts that are physically missing on disk.

- `ContentFileVersions`: 114 total, 16 missing
- `ContentFileMarkdownShadows`: 15 total, 3 missing
- `NotebookFileMarkdownShadows`: 2426 total, 1 missing
- `AssistantFileMarkdownShadows`: 7 total, 0 missing

A recovery pass restored one file from `_legacy-unmapped`; remaining missing artifacts require either backup restore or stale-row cleanup policy.

---

## 1. Current State

The existing storage layout under `FileStorage:Path` uses GUIDs as directory names:

- Project files: `{storage}/{projectGuid}/files/{fileGuid}/v{n}/{fileName}`
- Content-addressable blobs: `{storage}/projects/{projectGuid}/content/{aa}/{bb}/{hash}`
- Notebook tree: `{storage}/{projectGuid}/notebooks/{notebookGuid}/...`
- Notebook markdown shadows: `{storage}/projects/{projectGuid}/notebooks/{notebookGuid}/markdown/{aa}/{bb}/{hash}.md`
- Container mount point: host `./volumes/content-files` → container `/app/ContentFiles`

This layout is opaque to the user. The goal is for the user to see and interact with their files in a natural folder structure on the host filesystem.

---

## 2. Target State

The proposed layout preserves the existing internal directory structure but replaces GUIDs with human-readable slugs and makes notebooks direct children of their project:

- `{storage}/{projectSlug}/` — project root (was `{storage}/{projectGuid}/`)
- `{storage}/{projectSlug}/{notebookSlug}/` — notebook root (was `{storage}/{projectGuid}/notebooks/{notebookGuid}/`)
- `{storage}/{projectSlug}/files/{fileGuid}/v{n}/{fileName}` — versioned project file storage (unchanged structure, slug replaces GUID)
- `{storage}/projects/{projectSlug}/content/{aa}/{bb}/{hash}` — content-addressable blobs (unchanged structure, slug replaces GUID)
- `{storage}/projects/{projectSlug}/{notebookSlug}/markdown/{aa}/{bb}/{hash}.md` — notebook markdown shadows (slug replaces GUID, no `notebooks/` segment)
- `{storage}/projects/{projectSlug}/content/{aa}/{bb}/{hash}.md` — project markdown shadows (unchanged structure, slug replaces GUID)

The `notebooks/` intermediate path segment is eliminated — notebooks are direct subfolders of projects. Container mount convention: `/app/ContentFiles/{projectSlug}/{notebookSlug}/...`

---

## 3. Risks and Solutions

### Risk 3.1: Name Collisions — Duplicate Project or Notebook Titles

**Current state**: No uniqueness constraint exists on `Project.Title` or `(Notebook.ProjectId, Notebook.Title)` in the database. Two projects can share the same name, and two notebooks within a project can share the same name.

**Impact**: With name-based folders, duplicate names would map to the same directory, causing data corruption or loss.

**Solution**:
1. Add a unique index on `Project.Title` in the database (case-insensitive).
2. Add a unique composite index on `(Notebook.ProjectId, Notebook.Title)`.
3. Add a new `Slug` column to both `Project` and `Notebook` that stores the sanitized, filesystem-safe version of the title. The slug is what gets used for the directory name.
4. Enforce slug uniqueness at the database level.
5. Add API-layer validation to reject creates/renames that would produce duplicate slugs.
6. For existing data, a one-time migration generates slugs and appends numeric suffixes to resolve collisions.

**Key files affected**:
- `src/server/GuideAntsApi.DataModel/Models/Project.cs` — add `Slug` property
- `src/server/GuideAntsApi.DataModel/Models/Notebook.cs` — add `Slug` property
- `src/server/GuideAntsApi.DataModel/ApplicationDbContext.cs` — add unique indexes
- `src/server/GuideAntsApi/Services/Core/ProjectService.cs` — slug generation and validation on create/update
- `src/server/GuideAntsApi/Services/Components/NotebookService.cs` — slug generation and validation on create/update

---

### Risk 3.2: Invalid Filesystem Characters in Names

**Current state**: `Project.Title` and `Notebook.Title` accept any Unicode characters up to 255 characters. No filesystem-safe validation exists (unlike `ProjectFolder.Name` which validates against `Path.GetInvalidFileNameChars()`).

**Impact**: Titles containing `/`, `\`, `:`, `*`, `?`, `"`, `<`, `>`, `|`, or other OS-restricted characters would produce invalid or dangerous paths. Windows has additional reserved names (CON, PRN, AUX, NUL, etc.) and path length limits (260 chars default, ~32K with long path support).

**Solution**:
1. Introduce a `SlugGenerator` utility that sanitizes titles into filesystem-safe slugs: replace invalid characters with hyphens, collapse consecutive hyphens, trim leading/trailing hyphens, lowercase, and cap length.
2. Apply the generator when creating or renaming projects/notebooks.
3. Allow the user-facing title to remain unchanged (display name stays rich); only the slug/folder name is sanitized.
4. Add validation that rejects Windows reserved device names.
5. Enforce a maximum slug length (e.g., 100 characters) to leave headroom for deeply nested paths within the 260-character Windows path limit.

**Key files affected**:
- New utility: `src/server/GuideAntsApi/Services/SlugGenerator.cs`
- `src/server/GuideAntsApi/Services/Core/ProjectService.cs`
- `src/server/GuideAntsApi/Services/Components/NotebookService.cs`

---

### Risk 3.3: Rename Operations Require Physical Directory Moves

**Current state**: Renaming a project or notebook title only updates the database row. The GUID-based directory name never changes, so no filesystem work is needed.

**Impact**: In a name-based system, renaming a project or notebook title must also rename the physical directory. This is a potentially expensive operation (especially for large notebook trees), can fail mid-operation, and must update all paths stored in the database.

**Solution**:
1. When the slug changes (not every title edit changes the slug), perform an atomic rename of the directory on disk using `Directory.Move`.
2. Wrap the rename in a transaction-like pattern: rename directory first, then update all affected database paths. If the DB update fails, rename the directory back.
3. For project renames, update `ContentFile.Path` for all files in the project, re-resolve `ContentFileVersion.StoragePath` paths, and update `ContentFileMarkdownShadow.StoragePath` and `NotebookFileMarkdownShadow.StoragePath` entries.
4. For notebook renames within a project, update `NotebookFileMarkdownShadow.StoragePath` entries for that notebook.
5. `FileLineageEvent.StoragePath` is an immutable audit field — do not update it. It reflects the path at time of action.
6. `ContentFile.DocumentId` and `NotebookFile.DocumentId` are derived from path/ID hashes. Under the new scheme, `DocumentId` generation should use the entity GUID (stable) rather than the mutable slug/path to avoid re-indexing on rename.

**Key files affected**:
- `src/server/GuideAntsApi/Services/Core/ProjectService.cs` — add directory rename logic
- `src/server/GuideAntsApi/Services/Components/NotebookService.cs` — add directory rename logic
- `src/server/GuideAntsApi/Services/Components/ContentFileService.cs` — path recalculation
- `src/server/GuideAntsApi.DataModel/Models/ContentFile.cs` — `GenerateDocumentId` should use stable GUID
- `src/server/GuideAntsApi.DataModel/Models/NotebookFile.cs` — `GenerateDocumentId` should use stable GUID
- All markdown shadow handlers that build `StoragePath` using project/notebook IDs

---

### Risk 3.4: Cross-Platform Path Separator and Case Sensitivity

**Current state**: The codebase already has some cross-platform path normalization (e.g., `Replace("\\", "/")` in `NotebookPathHelper`, separator normalization in `StoragePathCompatibility`). However, the system implicitly relies on GUID path segments which are case-insensitive and contain no special characters.

**Impact**: Human-readable names introduce case sensitivity risks (Linux containers are case-sensitive; Windows host is case-insensitive). A folder named "My Project" on the Windows host becomes case-sensitive inside the Linux container.

**Solution**:
1. Enforce lowercase slugs to eliminate case mismatches between Windows host and Linux containers.
2. Continue using forward slashes in stored paths and normalize on resolution.
3. Add integration tests that verify path round-tripping between host and container paths.
4. The `StoragePathCompatibility` utility (in `src/server/GuideAntsApi.DataModel/Utilities/StoragePathCompatibility.cs`) will need its anchor-based resolution updated since it currently looks for `"projects"` and `"assistants"` path segments.

---

### Risk 3.5: Container Path Convention Changes

**Current state**: Containers use a fixed convention: `/app/ContentFiles/{projectGuid}/notebooks/{notebookGuid}/...`. This is hardcoded in:
- `NotebookPathHelper.GetWorkingDirectory()` — container path with string interpolation
- `SandboxToolService` — container base path
- `ConversationService` / `PublishedConversationService` — regex patterns normalizing sandbox output paths
- `docker-compose.yml` — bind mount `./volumes/content-files:/app/ContentFiles`

**Impact**: The container path format must change to match the new name-based layout. All services that construct or parse container paths must be updated.

**Solution**:
1. Introduce a `PathResolver` service (or extend `NotebookPathHelper`) that encapsulates all path construction — both host and container — behind a single abstraction. Every path construction site currently using inline `Path.Combine` or string interpolation should call this service instead.
2. The container mount point (`/app/ContentFiles`) can remain the same; only the directory structure underneath changes.
3. Update `NotebookPathHelper.GetWorkingDirectory()` to use `{projectSlug}/{notebookSlug}/...` instead of GUIDs.
4. Update `SandboxToolService.GetModuleContainerPath()` similarly.
5. Update the regex in `ConversationService` and `PublishedConversationService` that normalizes sandbox output paths.
6. Update `ScriptExecutionAgent`'s path validation logic (currently expects `Path.Combine(fileStorageRoot, projectId)` as prefix).

**Key files affected**:
- `src/server/GuideAntsApi/Services/NotebookPathHelper.cs`
- `src/server/GuideAntsApi/Services/SandboxToolService.cs`
- `src/server/GuideAntsApi/Services/Conversations/ConversationService.cs`
- `src/server/GuideAntsApi/Services/Conversations/PublishedConversationService.cs`
- `src/server/ScriptExecutionAgent/Program.cs`
- `docker/docker-compose.yml`

---

### Risk 3.6: Content-Addressable and Versioned Storage Migration

**Current state**: Content-addressable blobs live at `{storage}/projects/{projectGuid}/content/...` and legacy versioned files at `{storage}/{projectGuid}/files/{fileGuid}/v{n}/...`. The `StoragePath` column in `ContentFileVersion` and markdown shadows stores absolute paths containing GUIDs.

**Impact**: Changing the project directory from GUID to name means all existing stored paths become stale. Content-addressable paths use the `projects/{projectGuid}` prefix.

**Solution**:
1. The internal directory structure (`files/`, `projects/.../content/`, `projects/.../markdown/`) is preserved as-is. Only the GUID path segments change to slugs, and the `notebooks/` intermediate segment is removed.
2. Write a one-time data migration that:
   a. Renames `{storage}/{projectGuid}/` to `{storage}/{projectSlug}/`.
   b. Moves notebook directories from `{storage}/{projectSlug}/notebooks/{notebookGuid}/` to `{storage}/{projectSlug}/{notebookSlug}/` (eliminates `notebooks/` segment).
   c. Renames GUID segments within `{storage}/projects/{projectGuid}/` to `{storage}/projects/{projectSlug}/` and within nested notebook markdown paths similarly.
   d. Updates all `StoragePath` columns in `ContentFileVersion`, `ContentFileMarkdownShadow`, `NotebookFileMarkdownShadow`, and `AssistantFileMarkdownShadow` to reflect the new slug-based paths.
   e. Updates `ContentFile.Path` values.
3. `StoragePathCompatibility` provides a safety net during migration — it can resolve both old and new path formats by adding project slug as a recognized root anchor alongside the existing `"projects"` and `"assistants"` anchors.
4. `FileLineageEvent.StoragePath` is left unchanged (historical audit).

---

### Risk 3.7: DocumentId Stability During Migration

**Current state**: `ContentFile.DocumentId` is a SHA-256 hash of `ContentFile.Path` (the physical base path containing the project GUID). `NotebookFile.DocumentId` is a SHA-256 hash of `{notebookGuid}:{relativePath}`. These IDs are stored in `DocumentChunk` rows for vector search.

**Impact**: Changing the path or ID inputs would change DocumentIds, orphaning all existing `DocumentChunk` rows and requiring a full re-index.

**Solution**:
1. Change `ContentFile.GenerateDocumentId()` to hash `{ProjectId}:{RelativePath}` instead of `Path`. This uses the stable GUID (which doesn't change) combined with the logical path (which only changes on move/rename of the file itself, not the project).
2. Change `NotebookFile.GenerateDocumentId()` to continue using `{NotebookId}:{RelativePath}` — this already uses the stable GUID and logical path, so no change is needed for notebook files.
3. Run a one-time migration to regenerate `ContentFile.DocumentId` values and update matching `DocumentChunk.DocumentId` references.
4. Alternatively, trigger a `RebuildEmbeddingsJob` after migration to re-index everything.

**Key files affected**:
- `src/server/GuideAntsApi.DataModel/Models/ContentFile.cs` — `GenerateDocumentId()`
- Migration script to update existing `DocumentId` values

---

### Risk 3.8: Project Deletion Leaves Named Directories

**Current state**: `ProjectService.DeleteProjectAsync` performs a soft delete (sets `Deleted = true`) when the project has content. No filesystem cleanup occurs. `NotebookService.DeleteAsync` does a best-effort `Directory.Delete` of the notebook's GUID directory.

**Impact**: With name-based directories, a soft-deleted project's folder remains visible to the user with its real name. This is confusing and blocks reuse of the name.

**Solution**:
1. For the single-user deployment, convert project deletion from soft-delete to hard-delete with filesystem cleanup (delete the project's directory tree). The soft-delete pattern exists to protect other users' data in a multi-tenant system — that concern doesn't apply here.
2. On notebook deletion, the existing `Directory.Delete` logic works but the path changes from `{storage}/{projectGuid}/notebooks/{notebookGuid}` to `{storage}/{projectSlug}/{notebookSlug}`.
3. Ensure the slug is freed for reuse after deletion (the unique index constraint handles this automatically when the row is removed).

---

### Risk 3.9: User-Initiated External File Modifications

**Current state**: Users don't directly interact with the storage directory. The `NotebookFileSyncService` reconciles disk-to-DB but assumes the system is the only writer.

**Impact**: With visible, named directories, users will be tempted to add, rename, move, or delete files directly on the filesystem. The sync service must handle all of these cases gracefully, including:
- New files appearing that have no DB record
- Files disappearing that do have DB records
- Files being renamed or moved (detected as delete + create, not rename)
- Files being modified (content hash changes)
- Users creating folders that don't match any notebook
- Users renaming the project or notebook folder itself

**Solution**:
1. `NotebookFileSyncService` already handles new/changed/deleted files during sync — it is designed for "filesystem is source of truth" and handles all these cases. No changes to its sync logic are needed beyond the path resolution refactor.
2. The sync service is already triggered via API call and polling. The existing mechanisms are sufficient.

**Key files affected**:
- `src/server/GuideAntsApi/Services/Components/NotebookFileSyncService.cs` — path resolution only

---

### Risk 3.10: Published Guide Path References

**Current state**: Published guides reference notebooks by GUID in API routes (`/api/published/projects/{projectId:guid}/notebooks/{notebookId:guid}/conversations`). Published conversation endpoints construct notebook root paths using GUIDs. Retention cleanup uses these paths.

**Impact**: In the single-user model, published guides may not be needed (they are an external access mechanism). If retained, their path construction must update.

**Solution**:
1. Evaluate whether published guides are needed in the single-user deployment. If not, disable or remove the feature to reduce migration scope.
2. If retained, published guide paths should use the same `PathResolver` abstraction, resolving `projectSlug/notebookSlug` from the `PublishedGuide.NotebookId` → `Notebook` → `Notebook.Slug` chain.
3. Published API routes can remain GUID-based (they're internal API identifiers, not filesystem paths).

**Key files affected**:
- `src/server/GuideAntsApi/Endpoints/PublishedNotebookConversationsEndpoints.cs`
- `src/server/GuideAntsApi/Endpoints/PublishedGuidesEndpoints.cs`
- `src/server/GuideAntsApi.BackgroundJobs/Jobs/RetentionCleanupHandler.cs`

---

### Risk 3.11: Background Job Path Resolution

**Current state**: Background job handlers construct paths inline using `projectId.ToString()` and `notebookId.ToString()`. They resolve notebook roots and markdown shadow paths independently.

**Impact**: Every background job handler that builds a filesystem path must be updated.

**Solution**:
1. All path construction must go through the centralized `PathResolver` service.
2. Inject `PathResolver` into all background job handlers that currently construct paths.
3. The `PathResolver` must be able to look up the project and notebook slugs from their GUIDs (requires a DB query or cache).
4. Add a lightweight in-memory cache of GUID-to-slug mappings in `PathResolver`, invalidated on rename.

**Key files affected** (all handlers that construct filesystem paths):
- `src/server/GuideAntsApi.BackgroundJobs/Jobs/ExtractNotebookFileMarkdownHandler.cs`
- `src/server/GuideAntsApi.BackgroundJobs/Jobs/TranscribeNotebookFileMarkdownHandler.cs`
- `src/server/GuideAntsApi.BackgroundJobs/Jobs/ExtractContentVersionMarkdownHandler.cs`
- `src/server/GuideAntsApi.BackgroundJobs/Jobs/TranscribeContentVersionMarkdownHandler.cs`
- `src/server/GuideAntsApi.BackgroundJobs/Jobs/IndexNotebookMarkdownShadowHandler.cs`
- `src/server/GuideAntsApi.BackgroundJobs/Jobs/IndexContentMarkdownShadowHandler.cs`
- `src/server/GuideAntsApi.BackgroundJobs/Jobs/IndexDirectTextFileHandler.cs`
- `src/server/GuideAntsApi.BackgroundJobs/Jobs/SyncNotebookHandler.cs`
- `src/server/GuideAntsApi.BackgroundJobs/Jobs/RetentionCleanupHandler.cs`

---

### Risk 3.12: Test Suite Breakage

**Current state**: Unit and integration tests create temp directories using GUID-based path patterns. `StoragePathCompatibilityTests` explicitly tests the current path layout.

**Impact**: All tests that construct or assert filesystem paths will break.

**Solution**:
1. Update test fixtures to use the new slug-based path layout.
2. Update `StoragePathCompatibilityTests` to cover both legacy and new path formats.
3. Add new tests for slug generation, collision resolution, and rename-with-directory-move.

**Key files affected**:
- `src/server/GuideAntsApi.Tests/Services/NotebookFileServiceTests.cs`
- `src/server/GuideAntsApi.Tests/BackgroundJobs/StoragePathCompatibilityTests.cs`
- `src/server/GuideAntsApi.IntegrationTests/Endpoints/NotebookFileSyncEndpointsTests.cs`

---

## 4. Migration Strategy

### Phase 1: Foundation (No Breaking Changes)

1. Add `Slug` column to `Project` and `Notebook` entities (nullable initially).
2. Create `SlugGenerator` utility.
3. Create `PathResolver` service that supports both GUID and slug-based path resolution.
4. Populate `Slug` for all existing records via a data migration.
5. Add uniqueness constraints on `Project.Slug` and `(Notebook.ProjectId, Notebook.Slug)`.
6. Add slug validation to create/update endpoints.

**Status**: Completed.

### Phase 2: Dual-Mode Path Resolution

1. Refactor all inline path construction across services, handlers, and helpers to use `PathResolver`.
2. `PathResolver` can operate in "legacy" (GUID) or "named" (slug) mode via configuration toggle.
3. Update `StoragePathCompatibility` to recognize both layouts.
4. Update all tests.

**Status**: Completed (with legacy compatibility support still intentionally present).

### Phase 3: Physical Migration

1. Write a migration tool that:
   a. Renames project root directories from GUID to slug.
   b. Under each project, moves notebook directories from `notebooks/{notebookGuid}/` to `{notebookSlug}/` (removes intermediate `notebooks/` segment).
   c. Renames GUID segments within `projects/{projectGuid}/` to `projects/{projectSlug}/` for content-addressable and markdown shadow paths, and similarly for notebook markdown paths.
   d. Updates all `StoragePath` and `Path` columns in the database to reflect the new paths.
2. Update `DocumentId` generation for `ContentFile` to use GUID-based hashing.
3. Trigger a full embeddings rebuild.

**Status**: Completed in this environment. One-time filesystem + DB path migration has been applied, active storage is slug-based, and legacy GUID directories were moved to `_legacy-unmapped`.

### Phase 4: Enable Name-Based Mode

1. Set `PathResolver` to "named" mode.
2. Remove GUID-based path construction code.
3. Enable filesystem watcher for notebook files.
4. Update container path conventions in `docker-compose.yml` and related scripts.

**Status**: Completed in this environment with compatibility fallback retained intentionally during stabilization.

### Phase 5: Cleanup

1. Remove the "legacy" mode from `PathResolver`.
2. Drop deprecated `ContentFile.Path` column (or repurpose it).
3. Remove `StoragePathCompatibility` anchor-based resolution for `"projects"` segment.
4. Update documentation.

**Status**: Not started (deferred intentionally until after stabilization window).

---

## 5. Scope Reduction Opportunities

For the single-user deployment, several features may be simplified or removed:

1. **Published guides** — if external access is not needed, removing this feature eliminates an entire path-construction surface area.
2. **Multi-user isolation** — API-layer project/user isolation checks can be simplified or removed.
3. **Soft delete** — with a single user, hard-delete with filesystem cleanup is more appropriate than soft-delete (see Risk 3.8).
4. **Content-addressable storage** — with a single user and lower file volumes, the CAS deduplication benefit is reduced. Consider whether simpler versioned storage (copying files) is sufficient.

---

## 6. Estimated Impact Summary

| Area | Files Affected | Complexity |
|------|---------------|------------|
| Entity models (Slug, DocumentId) | 4 | Medium |
| Database migration | 1-2 | Medium |
| PathResolver service | 1 (new) | High |
| SlugGenerator utility | 1 (new) | Low |
| Server services (path refactor) | ~12 | High |
| Background job handlers | ~10 | Medium |
| Endpoint path construction | ~4 | Medium |
| Container path helpers | ~3 | Medium |
| StoragePathCompatibility | 1 | Medium |
| Tests | ~5 | Medium |
| Docker/compose config | ~2 | Low |
| Client (no filesystem changes) | 0 | None |
| Data migration tool | 1 (new) | High |
| **Total** | **~45 files** | |

The client-side code is unaffected because it communicates via API endpoints using GUIDs for project and notebook identifiers. The path change is entirely server-side and infrastructure.

---

## 7. Resolved Follow-Up Decisions

The following design decisions were made after review of the initial proposal:

1. **Notebook association metadata is notebook-scoped, not file-scoped.**
   Each notebook root should contain a reserved system folder at `.guideants/` with a notebook association file (for example `.guideants/notebook.json`).
   When `.guideants/` is created, the system should set the filesystem hidden attribute where the host OS supports it.
   This is a usability improvement, not a security boundary, and the implementation must not rely on the hidden flag alone.
   This metadata should contain the stable identifiers needed to associate the folder to the notebook, at minimum:
   - `SchemaVersion`
   - `ProjectId`
   - `NotebookId`
   
   The `.guideants/` folder is a system namespace. It must be ignored and hidden by:
   - notebook file/folder listings
   - sync/import logic that treats notebook contents as user files
   - assistant/tool output reporting
   - any UI that displays notebook content

2. **Notebook folder names are user-owned once created.**
   If the notebook cannot be found at its expected named location, the system should scan candidate notebook folders for matching `.guideants/notebook.json` metadata.
   If a matching notebook folder is found under a different name, that discovered folder name becomes the notebook's current runtime location.
   The system must not automatically rename the folder back to the previously expected name.

3. **Missing notebook folders should not destroy notebook history.**
   If no associated notebook folder can be found via the metadata scan:
   - the UI should surface a clear notebook storage warning
   - the notebook record and its conversations should be preserved
   - the system should create a fresh empty notebook folder as if the notebook were new

This means notebook storage identity is determined by notebook association metadata, not by per-file identifiers and not solely by the current folder name.

### Next Phase Requirements

- **Detect external notebook content changes and refresh state.**
  We now also need a way to detect notebook content changes made outside the application and refresh the notebook's contents accordingly.

- **Defer comprehensive external-change detection to the next phase.**
  This is explicitly recognized as a harder follow-on requirement and should not be conflated with the current migration.
  The current migration should handle notebook folder association and recovery via `.guideants` metadata.
  A later phase should define and implement how to detect external file adds, deletes, renames, moves, and edits, and how to refresh both database state and the UI.

- **Future design work should choose the detection mechanism.**
  Candidate approaches include filesystem watchers, polling plus reconciliation, hash/mtime-based refresh, and manual refresh fallback behavior.
