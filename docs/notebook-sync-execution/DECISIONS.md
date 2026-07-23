# Notebook File Sync Unification — Locked Decisions

Last updated: 2026-07-23  
Status: **LOCK before Phase 1**

This file freezes design decisions for the unified notebook sync work. The skills-execution
and host-mounts-execution folders are structural templates; **this** document is the
contract for this feature.

Rules:

- If a decision is `UNDECIDED`, the blocked phases do not start.
- Changing a locked decision mid-implementation requires updating all affected code and
  re-running gates from Phase 1.
- Do not reinterpret locked values in task briefs.

---

## Part A — Locked decisions (N1–N10)

| ID | Decision | Resolved value | Blocks |
|----|----------|----------------|--------|
| N1 | **Delivery** | **One PR** on a single feature branch after all phases and gates pass. Phases are milestones, not separate merges. | all |
| N2 | **Serve gate** | `GetFileContentStreamAsync` and `GetFolderTreeAsync` remain **DB-backed**. No filesystem fallback for normal notebook files when the row is missing. Visibility is fixed by **earlier row insert**, not by bypassing the gate. | 2, 4 |
| N3 | **Two-tier sync** | **Fast register:** stat + placeholder hash + immediate `SaveChanges`. **Full reconcile:** enumerate notebook, SHA-256 backfill, stale delete, index/extract enqueue. | 1, 2 |
| N4 | **Placeholder hash** | Use `NotebookFileHash.Placeholder(size, mtime)` → `pending:{size:x}:{ticks:x}`. Same pattern family as linked-mount virtual files. Full reconcile replaces with SHA-256. | 1, 2 |
| N5 | **Hot-path ordering** | On file-changing turns: `RegisterFilesAsync` runs **inline** in the conversation background run **before** the stream channel completes and **before** SSE `complete`. Then `QueueReconcileAsync(Full)`. | 2 |
| N6 | **Path contract** | **Register** accepts **notebook-root-relative** paths (`Output/foo.png`, `Runs/{id}/foo.png`). Tools populate DB-relative paths at creation time. CWD-relative paths in `ChatRunOutput` remain for the model; resolver converts when DB paths not supplied. | 2, 3 |
| N7 | **Single reconciler** | `NotebookFileReconciler` is the only module that upserts from disk, removes stale rows, and decides index enqueue. `NotebookFileSyncService` = lock + enqueue + delegate. `SyncNotebookHandler` = job wrapper + delegate. | 1, 3 |
| N8 | **Index enqueue timing** | **Never** on Fast register. **Only** on Full reconcile when SHA-256 confirms new/changed content (or placeholder → real hash transition). | 1, 2 |
| N9 | **Enumerator location** | `NotebookSyncFileEnumerator` moves to `GuideAntsApi` (`Services/Components/Sync/`). BackgroundJobs references API project; no duplicate enumeration logic. | 1, 3 |
| N10 | **API naming** | Public surface: `RegisterFilesAsync`, `ReconcileNotebookAsync`, `ReconcileNotebookImmediateAsync`, `QueueReconcileAsync`. Keep obsolete aliases (`SyncNotebookAsync`, etc.) as thin wrappers **one release** if needed; update all in-repo call sites in the same PR. | 1, 3 |

---

## Part B — Frozen invariants

- **Filesystem is source of truth** for notebook file bytes; `NotebookFile` mirrors disk.
- **Host mounts (plan §14):** registered mount roots are first-class entries; do not
  recursively index/hash mounted host content; never delete host content during sync.
- **Temp script files** (`{guid}_script.py`) excluded from sync enumeration (existing
  handler filter preserved in shared rules).
- **Referenced rows:** never delete `NotebookFile` rows still referenced by
  `MessageAttachments` or `ContentFileVersions`.
- **No fallback masking:** if disk file missing at register time, skip that path with log;
  do not insert a row.
- **Conversation lock / job gate:** `SyncNotebook` is **not** deferred by
  `ConversationLockJobGate` (existing behavior preserved). Index jobs may still defer.
- **`IStoragePathResolver`:** all notebook root resolution goes through the resolver in the
  reconciler (eliminate slug/GUID dual path in handler only).

---

## Part C — Explicit non-goals (this PR)

- Lowering `PollingIntervalSeconds` as the primary fix.
- Client-only retry loops as the primary fix (optional hardening only if serving-gate fails).
- Schema migration for sync state columns (`SyncStatus`, etc.) — placeholder hash is enough.
- Rewriting `NotebookFileService` upload/create to reconciler in every path (only where
  touched for deduplication; full upload refactor is optional stretch).

---

## Part D — Decision ledger

| ID | Status | Date | Notes |
|----|--------|------|-------|
| N1 | LOCKED | 2026-07-23 | One PR |
| N2 | LOCKED | 2026-07-23 | DB gate; fix via register |
| N3 | LOCKED | 2026-07-23 | Fast + Full tiers |
| N4 | LOCKED | 2026-07-23 | Placeholder hash |
| N5 | LOCKED | 2026-07-23 | Register before complete |
| N6 | LOCKED | 2026-07-23 | DB-relative register paths |
| N7 | LOCKED | 2026-07-23 | Single reconciler |
| N8 | LOCKED | 2026-07-23 | Index after full hash |
| N9 | LOCKED | 2026-07-23 | Enumerator in API project |
| N10 | LOCKED | 2026-07-23 | API rename + aliases |
