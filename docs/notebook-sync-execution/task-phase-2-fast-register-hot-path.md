# Phase 2 — Fast Register + Hot Path

**Branch:** `feature/notebook-sync-unification`  
**Depends on:** Phase 1 `DONE`  
**Blocks:** Phase 3

---

## Mission

Add `RegisterFilesAsync` (stat + placeholder hash + immediate save). Wire turn-end and
tool paths so `NotebookFile` rows exist **before** SSE `complete`. Queue full reconcile
after register. Pass **serving-gate**.

---

## Read first

- `docs/notebook-sync-execution/serving-gate.md`
- `docs/notebook-sync-execution/DECISIONS.md` N2–N6, N8
- `ConversationStreamEngine.cs` — `QueueNotebookSyncIfNeededAsync`, stream completion order
- `NotebookFileChangeReporter.cs` — `NewFiles` / `ModifiedFiles` paths
- `NotebookImageService.cs`, `NotebookDockerScriptService.cs`, `PodcastTools.cs`

---

## Preconditions

- [ ] Phase 1 gate passed
- [ ] `NotebookFileReconciler` exists and full reconcile works

---

## Guardrails

- Register is **synchronous** on the hot path (no background job for register).
- Do **not** enqueue index jobs on register (N8).
- Do **not** add filesystem fallback in `GetFileContentStreamAsync` (N2).
- Register only paths that exist on disk at call time; log and skip missing paths.
- `SaveChanges` once per `RegisterFilesAsync` batch (not per file if batch is small).

---

## Tasks

### 1. Extend reconciler + service API

```csharp
// INotebookFileSyncService
Task RegisterFilesAsync(Guid notebookId, IReadOnlyList<string> dbRelativePaths, CancellationToken ct = default);
Task QueueReconcileAsync(Guid notebookId, CancellationToken ct = default);  // replaces QueueNotebookSyncAsync naming (N10)

// NotebookFileReconciler
Task RegisterFilesAsync(...);  // stat, placeholder hash, upsert, SaveChanges
```

Obsolete thin wrappers: `QueueNotebookSyncAsync` → `QueueReconcileAsync` if renaming in same PR.

### 2. Register implementation

For each normalized DB-relative path:

1. Resolve absolute path via notebook root + `NotebookPathResolver`.
2. If `!File.Exists` → log warning, skip.
3. Stat size + `LastWriteTimeUtc`.
4. Upsert `NotebookFile` with `NotebookFileHash.Placeholder(size, mtime)`.
5. `SaveChangesAsync` before return.

Do **not** SHA-256 on this path.

### 3. `ConversationStreamEngine` ordering

In the background run completion path (where `QueueNotebookSyncIfNeededAsync` runs today):

```text
1. Collect DB-relative paths from ChatRunOutput (NewFiles + ModifiedFiles via resolver)
2. await RegisterFilesAsync(notebookId, paths)     // INLINE — before complete
3. await QueueReconcileAsync(notebookId)             // fire-and-forget job
4. yield / emit StreamingEventTypes.Complete
```

If `NewFiles` and `ModifiedFiles` are empty, skip register; queue reconcile only if
policy requires (document: optional full reconcile on every turn vs conditional — **default:
keep conditional queue** to avoid unnecessary full walks; register only when changes reported).

### 4. Tool DB-relative paths

| Caller | Change |
|--------|--------|
| `NotebookImageService` | After write, call `RegisterFilesAsync` with `Output/...` path; include DB path in tool result metadata |
| `NotebookDockerScriptService` | Register script output paths; populate DB-relative in `ScriptExecutionResult` |
| `PodcastTools` | Register audio output path |
| Wire executors (`WireImageGenerationsExecutor`, `WireAudioSpeechExecutor`) | Register on generation complete |

Extend `ChatRunOutput` or tool result types with `DbRelativePaths` (or map `NewFiles` through
resolver at register time if CWD-only today).

### 5. Integration test (serving-gate)

Implement test described in `serving-gate.md` §2.1.

---

## Files in scope

**Modify:**

- `INotebookFileSyncService.cs`, `NotebookFileSyncService.cs`
- `NotebookFileReconciler.cs`
- `ConversationStreamEngine.cs`
- `NotebookFileChangeReporter.cs` (expose DB-relative collection helper)
- `NotebookImageService.cs`, `NotebookDockerScriptService.cs`, `PodcastTools.cs`
- Wire executors under `Endpoints/PublishedWire/`
- Tool result DTOs if needed (`ScriptExecutionResult`, etc.)

**Add:**

- `GuideAntsApi.IntegrationTests/.../NotebookFileRegisterServingTests.cs` (or equivalent)

---

## Self-verification

```bash
cd src/server
dotnet test GuideAntsApi.sln --filter "FullyQualifiedName~RegisterServing|FullyQualifiedName~NotebookFileSync"
```

Manual (document in acceptance-evidence):

- Chat turn creates image → visible in markdown before ~3s
- Sidebar updates on `complete` event

---

## Definition of Done

- [ ] `RegisterFilesAsync` inserts placeholder rows
- [ ] Turn-end register runs before `complete`
- [ ] **serving-gate** §2.1–2.3 pass
- [ ] `STATUS.md` serving gate row updated
- [ ] Phase 2 → `DONE`

---

## Report-back

```text
PHASE 2 — FAST REGISTER
Serving gate integration test: <pass/fail>
Register before complete (review): <pass/fail>
Tool paths updated: <list>
Deviations: <none | list>
Ready for Phase 3: <yes/no>
```
