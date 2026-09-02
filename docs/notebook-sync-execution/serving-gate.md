# Serving Gate — Notebook File Sync Unification

Companion to `00-orchestration.md`. Run after **Phase 2** and at **final acceptance**.

This gate proves the user-visible fix: files are **servable and listable** as soon as the
chat turn completes, without waiting for the background `SyncNotebook` job (poll interval +
metadata walk; SHA-256 only for new/changed/placeholder rows).

---

## 1. Gate intent

Pass when all are true:

- A `NotebookFile` row exists with placeholder (or final) hash **before** SSE `complete`.
- `GET /api/projects/{p}/notebooks/{n}/files/content?path=...` returns **200** in that
  window (file bytes match disk).
- `GET .../files/tree` (or folder-tree endpoint used by the client) includes the new file.
- Chat markdown authenticated image fetch does **not** permanently 404 when the assistant
  references a just-created file.
- Background `SyncNotebook` still runs and eventually backfills SHA-256 and enqueues indexing.

---

## 2. Checks

### 2.1 Integration test (required)

Add `NotebookFileRegisterServingTests` (or extend
`NotebookFileSyncEndpointsTests`) that:

1. Creates project + notebook on disk under test storage.
2. Writes a file to disk under the notebook root (simulating a tool write) **without**
   inserting `NotebookFile`.
3. Calls `RegisterFilesAsync` with the DB-relative path (or simulates turn-end register via
   service under test).
4. **Asserts row exists** with `NotebookFileHash.IsPlaceholder(hash) == true`.
5. Calls `GetFileContentStreamAsync` → success **before** any `SyncNotebook` job handler runs.
6. Calls `GetFolderTreeAsync` → file appears in tree.
7. Optionally: enqueue `SyncNotebook` job, assert hash becomes non-placeholder after handler.

### 2.2 Turn-end ordering (code review)

- [ ] `ConversationStreamEngine` calls `RegisterFilesAsync` **before** background channel
      writer completes / before `StreamingEventTypes.Complete` is yielded to the client.
- [ ] `QueueReconcileAsync` is **after** register (non-blocking).

### 2.3 Tool path contract

- [ ] `NotebookImageService` / `NotebookDockerScriptService` / `PodcastTools` supply
      DB-relative paths to register (not only CWD-relative `NewFiles`).

### 2.4 Client behavior (manual smoke — document in acceptance-evidence)

- [ ] Run a chat turn that generates an image or writes a file.
- [ ] Image renders in the assistant cell without refresh.
- [ ] Sidebar file tree shows the file within the same turn (no 10s wait).
- [ ] Download link on a relative markdown path works.

### 2.5 Negative cases

- [ ] Register with path that does not exist on disk → no row inserted; no throw.
- [ ] Full reconcile later still discovers files missed by register (if `NewFiles` empty but
      file on disk) — eventual consistency within job latency.

---

## 3. Report-back addition (Phase 2 + final)

```text
SERVING GATE:
- Register before complete (code review): <pass/fail>
- Integration test content 200 before job: <pass/fail + test name>
- Integration test folder tree before job: <pass/fail>
- Manual chat image smoke: <pass/fail + notes>
- Placeholder hash backfilled after full reconcile: <pass/fail>
```
