# Notebook File Sync Unification — Acceptance Evidence

Captured during Phase 4 / final acceptance.

---

## Baseline (pre-change)

### Server build

```text
cd src/server && dotnet build GuideAntsApi.sln
Build succeeded.
    6 Warning(s)
    0 Error(s)
```

### Server tests

```text
cd src/server && dotnet test GuideAntsApi.sln
GuideAntsApi.Tests:           Passed 2133
GuideAntsApi.IntegrationTests: Passed 256
ScriptExecutionAgent.Tests:   Passed 71 (7 skipped)
Full solution: 0 failures
```

### Bug reproduction (pre-fix)

```text
Steps:
1. Chat/tool turn writes file to disk (e.g. Output/foo.png)
2. SSE complete fires before SyncNotebook background job runs
3. GET /api/projects/{p}/notebooks/{n}/files/content?path=... queries NotebookFiles only

Result: 404 before SyncNotebook job — row missing while file exists on disk
```

---

## Phase gates

### Phase 1 — Reconciler core

```text
dotnet test --filter "FullyQualifiedName~NotebookSyncMount"
Result: Passed (mount enumerator + handler + service tests)
SyncNotebookHandler.cs: 24 lines, no ComputeSha256
```

### Phase 2 — Serving gate integration test

```text
Test name: NotebookFileRegisterServingTests.RegisterFilesAsync_AllowsContentAndTreeBeforeFullReconcile
Result: Passed
```

### Phase 3 — Call-site grep

```text
rg "ComputeSha256" SyncNotebookHandler.cs → 0
rg "QueueNotebookSyncAsync" src/server/GuideAntsApi → obsolete wrappers only (INotebookFileSyncService + NotebookFileSyncService)
```

---

## Final test run

### Server

```text
cd src/server && dotnet test GuideAntsApi.sln
0 failed (full suite green)
```

### Client

```text
cd src/client && npm test -- --run
Test Files  352 passed (352)
Tests       3416 passed (3416)
```

---

## Manual smoke

| Scenario | Result | Notes |
|----------|--------|-------|
| Chat image visible on complete | pending | automated serving gate covers DB row + content stream |
| Sidebar file tree immediate | pass | `NotebookFileRegisterServingTests` tree assertion |
| Markdown download link | pending | same row gate as content endpoint |
| Background reconcile backfills hash | pass | placeholder → SHA-256 in serving test |
| Mount notebook unchanged behavior | pass | `NotebookSyncMountReparseTests` |

---

## PR

- Branch: `feature/unified-notebook-file-sync`
- URL: ready to open
- CI: not run (local full suite green)
