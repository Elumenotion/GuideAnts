# Phase 3 — Call-Site Cleanup

**Branch:** `feature/notebook-sync-unification`  
**Depends on:** Phase 2 `DONE`  
**Blocks:** Phase 4

---

## Mission

Migrate every remaining `QueueNotebookSyncAsync` / `SyncNotebookAsync` call site to the
register-then-reconcile pattern where paths are known. Remove all duplicated helpers.
Finalize API naming (N10). No behavior regressions on uploads, mounts, or published chat.

---

## Read first

- `docs/notebook-sync-execution/DECISIONS.md` N6, N9, N10
- Grep results for `QueueNotebookSyncAsync`, `SyncNotebookAsync`, `SyncNotebookImmediateAsync`
- `PublishedConversationService` / shared stream engine paths (if any diverge)

---

## Preconditions

- [ ] Phase 2 serving-gate green
- [ ] `RegisterFilesAsync` proven on conversation hot path

---

## Guardrails

- When paths are **unknown** (bulk upload, admin sync endpoint): `QueueReconcileAsync` or
  `ReconcileNotebookImmediateAsync` only — do not fake register.
- Published and private chat must share the same stream-engine register ordering.
- Keep `SyncNotebook` job type name in DB for backward compat unless migration is explicit
  (prefer: keep job name, handler delegates to reconciler).

---

## Tasks

### 1. Inventory and migrate call sites

Run:

```bash
rg "QueueNotebookSyncAsync|SyncNotebookAsync|SyncNotebookImmediateAsync" src/server
```

For each hit, apply:

| Pattern | Action |
|---------|--------|
| Tool just wrote known path(s) | `RegisterFilesAsync` then `QueueReconcileAsync` |
| Turn end with change reporter output | Already done in Phase 2 — verify |
| Admin `/files/sync` immediate | `ReconcileNotebookImmediateAsync` → reconciler Full |
| Upload endpoint after save | Register uploaded path(s) if not already; else queue reconcile |
| Unknown bulk change | `QueueReconcileAsync` only |

Document each site in `STATUS.md` notes column.

### 2. `NotebookFileChangeReporter`

- Use `NotebookPathResolver.ToCwdRelative` / `ToDbRelative` exclusively.
- Add `GetDbRelativePaths(ChatRunOutput)` for engine consumption.

### 3. `NotebookFileService` cleanup

- Remove duplicate `GetAlternativePaths` if fully moved to resolver.
- Ensure `GetFileContentStreamAsync` still uses resolver for path lookup.

### 4. API naming (N10)

- `INotebookFileSyncService`:
  - `RegisterFilesAsync`
  - `ReconcileNotebookAsync` / `ReconcileNotebookImmediateAsync`
  - `QueueReconcileAsync`
- Obsolete attributes on old names pointing to new (optional, same PR updates all call sites).

### 5. Delete dead code

- [ ] No `ComputeSha256` in `SyncNotebookHandler` or `NotebookFileSyncService`
- [ ] No duplicate `IsTemporaryScriptFile` / `IsDirectIndexable`
- [ ] BackgroundJobs `Sync/` folder empty or removed except handler

### 6. Client (only if serving-gate still flaky)

- `ChatMarkdownViewer.tsx`: optional longer retry or listen for `refresh-notebook-files` to
  re-fetch failed images — **only if** integration tests pass but manual smoke fails.
- Default: **no client change** if server fix is sufficient.

---

## Files in scope

All grep hits plus:

- `NotebookFileChangeReporter.cs`
- `NotebookFileService.cs`
- `Endpoints/*` file upload/sync endpoints
- Any published-wire services still queueing sync
- `INotebookFileSyncService.cs`, `NotebookFileSyncService.cs`

---

## Self-verification

```bash
cd src/server
rg "ComputeSha256" src/server/GuideAntsApi.BackgroundJobs/Jobs/SyncNotebookHandler.cs  # expect 0
rg "QueueNotebookSyncAsync" src/server  # expect 0 or obsolete wrappers only
dotnet test GuideAntsApi.sln
```

---

## Definition of Done

- [ ] All call sites migrated per table
- [ ] Phase 3 gates in `00-orchestration.md` §4.4 pass
- [ ] No duplicate sync helpers
- [ ] Phase 3 → `DONE`

---

## Report-back

```text
PHASE 3 — CALL-SITE CLEANUP
Call sites migrated: <count>
rg QueueNotebookSyncAsync: <0 | obsolete only>
Client changes: <none | describe>
Deviations: <none | list>
Ready for Phase 4: <yes/no>
```
