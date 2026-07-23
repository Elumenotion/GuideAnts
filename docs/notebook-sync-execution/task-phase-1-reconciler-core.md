# Phase 1 — Reconciler Core

**Branch:** `feature/notebook-sync-unification` (same branch for all phases)  
**Depends on:** Pre-flight complete; `DECISIONS.md` N1–N10 LOCKED  
**Blocks:** Phase 2

---

## Mission

Extract a single `NotebookFileReconciler` and shared sync utilities. Make
`NotebookFileSyncService` and `SyncNotebookHandler` thin delegates. **Behavior-neutral**
for full sync: existing tests should pass without fast-register yet.

---

## Read first

- `docs/notebook-sync-execution/00-orchestration.md` §4.2
- `docs/notebook-sync-execution/DECISIONS.md` N3, N7, N8, N9
- `docs/host-mounts-execution/task-phase-8-notebook-sync.md` (mount enumeration)
- `src/server/GuideAntsApi/Services/Components/NotebookFileSyncService.cs`
- `src/server/GuideAntsApi.BackgroundJobs/Jobs/SyncNotebookHandler.cs`
- `src/server/GuideAntsApi.BackgroundJobs/Sync/NotebookSyncFileEnumerator.cs`

---

## Preconditions

- [ ] Baseline build/test captured in `STATUS.md`
- [ ] Feature branch created from updated `main`

---

## Guardrails

- Do **not** add `RegisterFilesAsync` yet (Phase 2).
- Do **not** change `ConversationStreamEngine` ordering yet.
- Preserve mount skip rules, temp-script exclusion, attachment-preserving deletes.
- One `ComputeSha256` implementation only (`NotebookFileHash`).
- Reconciler uses `IStoragePathResolver` for notebook root — no slug/GUID dual lookup in handler.

---

## Tasks

### 1. Create shared utilities (`GuideAntsApi/Services/Components/Sync/`)

| File | Responsibility |
|------|----------------|
| `NotebookFileHash.cs` | `ComputeSha256(Stream)`, `Placeholder(size, mtime)`, `IsPlaceholder(hash)` |
| `NotebookFileIndexingRules.cs` | `IsDirectIndexable`, `IsTemporaryScriptFile` (move from handler) |
| `NotebookPathResolver.cs` | `ToCwdRelative`, `ToDbRelative`, `GetAlternativePaths` (extract from `NotebookFileService` + `NotebookFileChangeReporter`) |
| `NotebookSyncFileEnumerator.cs` | Move from BackgroundJobs; same behavior |
| `NotebookFileReconciler.cs` | Full reconcile: enumerate, upsert with SHA-256, stale delete, index enqueue |

### 2. `NotebookFileReconciler` API (initial)

```csharp
Task<ReconcileResult> ReconcileNotebookAsync(
    Guid notebookId,
    ReconcileMode mode = ReconcileMode.Full,
    CancellationToken ct = default);

enum ReconcileMode { Full }  // FastRegister added in Phase 2
```

`ReconcileResult`: counts for added/updated/removed/index-enqueued (for logging/tests).

### 3. Slim `NotebookFileSyncService`

- Keep `SemaphoreSlim` per-notebook lock.
- `SyncNotebookAsync` / `SyncNotebookImmediateAsync` → `reconciler.ReconcileNotebookAsync(Full)`.
- `QueueNotebookSyncAsync` → enqueue `SyncNotebook` job (unchanged job type for now).

### 4. Slim `SyncNotebookHandler`

- Resolve notebook id from job payload.
- Call `reconciler.ReconcileNotebookAsync(Full)`.
- Remove local `ComputeSha256`, `IsTemporaryScriptFile`, `IsDirectIndexable`, enumeration loop.

### 5. Wire DI

- Register `NotebookFileReconciler` as scoped in API + BackgroundJobs (handler resolves from scope).

### 6. Update `NotebookFileService.FindNotebookFileByRelativePathAsync`

- Delegate alternative paths to `NotebookPathResolver` (read path only; keep method signature).

---

## Files in scope

**Add:**

- `GuideAntsApi/Services/Components/Sync/NotebookFileHash.cs`
- `GuideAntsApi/Services/Components/Sync/NotebookFileIndexingRules.cs`
- `GuideAntsApi/Services/Components/Sync/NotebookPathResolver.cs`
- `GuideAntsApi/Services/Components/Sync/NotebookSyncFileEnumerator.cs`
- `GuideAntsApi/Services/Components/Sync/NotebookFileReconciler.cs`

**Modify:**

- `NotebookFileSyncService.cs`, `INotebookFileSyncService.cs` (no new methods yet)
- `SyncNotebookHandler.cs`
- `NotebookFileService.cs` (path resolver delegation)
- `GuideAntsApi.BackgroundJobs.csproj` (reference if enumerator moved)
- Existing tests that reference moved types

**Delete (after move):**

- `GuideAntsApi.BackgroundJobs/Sync/NotebookSyncFileEnumerator.cs`

---

## Self-verification

```bash
cd src/server
dotnet build GuideAntsApi.sln
dotnet test GuideAntsApi.sln --filter "FullyQualifiedName~NotebookFileSync|FullyQualifiedName~SyncNotebook|FullyQualifiedName~NotebookSyncMount"
```

- [ ] `SyncNotebookHandler.cs` has no `ComputeSha256` method
- [ ] `NotebookFileSyncService.cs` has no inline hash loop
- [ ] `NotebookSyncMountReparseTests` pass

---

## Definition of Done

- [ ] Single reconciler owns full sync logic
- [ ] Enumerator in API project
- [ ] Path resolver shared (read side)
- [ ] All Phase 1 gates in `00-orchestration.md` §4.2 pass
- [ ] `STATUS.md` Phase 1 → `DONE`

---

## Report-back

```text
PHASE 1 — RECONCILER CORE
Gate: <pass/fail>
Tests: <counts>
Handler line count: <n>
Deviations: <none | list>
Ready for Phase 2: <yes/no>
```
