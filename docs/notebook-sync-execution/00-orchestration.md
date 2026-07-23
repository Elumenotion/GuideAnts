# Notebook File Sync Unification — Execution & Orchestration Guide

Last updated: 2026-07-23

This is the **conductor** document for unifying notebook file sync and fixing the
post-chat visibility gap (sidebar tree, `/files/content`, images, and downloads).

It supersedes the incremental "queue full sync and hope" model introduced when chat
turn completion moved from blocking `SyncNotebookImmediateAsync` to
`QueueNotebookSyncAsync`. The fix is **not** another queue tweak — it is a **single
reconciler** with a **fast register** path that inserts `NotebookFile` rows before SSE
`complete`, while SHA-256 hashing and indexing remain background work.

> **Delivery model: one PR**
>
> All phases land on one feature branch (e.g. `feature/notebook-sync-unification`).
> Phases are **logical milestones** for implementation and gate checks, not separate
> merges. Do not open a PR until **final acceptance** (section 6) is green on the
> complete tree.

> **Audience split**
>
> - **Implementer / reviewer** read this file, [`DECISIONS.md`](./DECISIONS.md),
>   [`STATUS.md`](./STATUS.md), [`serving-gate.md`](./serving-gate.md), and
>   [`acceptance-evidence.md`](./acceptance-evidence.md).
> - **Phase work** is defined in `task-phase-*.md` briefs. Execute phases in order;
>   each phase's Definition of Done must pass before starting the next.

---

## 0. How to use this folder

| File | Purpose |
|------|---------|
| `00-orchestration.md` (this) | Scope, phase order, gates, deviation protocol, final acceptance. |
| `DECISIONS.md` | Locked design decisions (N1–N10) + frozen invariants. Single source of truth. |
| `STATUS.md` | Living ledger: baseline, per-phase state, gate results, deviations. |
| `serving-gate.md` | Proves chat media + sidebar + download work before background reconcile completes. |
| `task-phase-1-reconciler-core.md` | Extract `NotebookFileReconciler` + shared utilities; slim existing sync entry points. |
| `task-phase-2-fast-register-hot-path.md` | `RegisterFilesAsync` + turn-end ordering + tool DB-relative paths. |
| `task-phase-3-call-site-cleanup.md` | Unify all callers; delete duplicated helpers; move enumerator. |
| `task-phase-4-tests-acceptance.md` | Integration tests, regression tests, acceptance evidence. |
| `acceptance-evidence.md` | Captured commands/outputs for the single PR. |

Each task brief: Mission → Read first → Preconditions → Guardrails → Tasks → Files in
scope → Self-verification → Definition of Done → Report-back contract.

---

## 1. Problem statement (why this work exists)

| Symptom | Root cause |
|---------|------------|
| Files missing from sidebar for several seconds after chat | `GetFolderTreeAsync` is **DB-only**; async `SyncNotebook` job has not run yet. |
| Images / downloads 404 in chat markdown | `GetFileContentStreamAsync` requires a `NotebookFiles` row; file may exist on disk only. |
| Client retries (~3s) then gives up | Background job poll (`PollingIntervalSeconds` ≈ 10s) + full-notebook SHA-256 walk exceeds retry window. |
| Duplicated logic drifts | `NotebookFileSyncService` and `SyncNotebookHandler` each hash-then-insert; path helpers split across read vs write. |

**Waterfall (origin):** `SyncNotebookImmediateAsync` on every turn — blocking but consistent.

**GuideAnts (current):** `QueueNotebookSyncAsync` — fast turns, broken serve-time consistency.

**Target:** Fast register inline on the hot path; full reconcile in background; **one**
implementation.

---

## 2. Pre-flight (once, before Phase 1)

- [ ] **`DECISIONS.md` is LOCKED** (N1–N10). No implementation until decisions are filled.
- [ ] **Capture baseline** in `STATUS.md`:
  - `cd src/server && dotnet build GuideAntsApi.sln`
  - `cd src/server && dotnet test GuideAntsApi.sln`
  - `cd src/client && npm run build`
  - `cd src/client && npm test -- --run`
- [ ] **Reproduce the bug** (manual or integration sketch) and record in `STATUS.md`:
  - Chat turn creates a file → SSE `complete` → `GET .../files/content?path=...` returns **404**
    before `SyncNotebook` job completes.
- [ ] **Inventory touchpoints** (do not guess scope):
  - `GuideAntsApi/Services/Components/NotebookFileSyncService.cs`
  - `GuideAntsApi/Services/Components/INotebookFileSyncService.cs`
  - `GuideAntsApi.BackgroundJobs/Jobs/SyncNotebookHandler.cs`
  - `GuideAntsApi.BackgroundJobs/Sync/NotebookSyncFileEnumerator.cs`
  - `GuideAntsApi/Services/Components/NotebookFileService.cs` (`FindNotebookFileByRelativePathAsync`, `GetAlternativePaths`)
  - `GuideAntsApi/Services/Components/NotebookFileChangeReporter.cs`
  - `GuideAntsApi/Services/Conversations/Streaming/ConversationStreamEngine.cs`
  - `NotebookImageService.cs`, `NotebookDockerScriptService.cs`, `PodcastTools.cs`
  - `Endpoints/PublishedWire/WireImageGenerationsExecutor.cs`, `WireAudioSpeechExecutor.cs`
  - `client/.../ChatMarkdownViewer.tsx` (retry behavior — document only; change only if gate fails)
- [ ] Feature branch from updated `main` per repo branch-safety rules.
- [ ] Read `docs/host-mounts-execution/task-phase-8-notebook-sync.md` — mount-aware
      enumeration must remain correct in the unified reconciler.

---

## 3. Dependency graph (implementation order — one branch)

```text
Phase 1  Reconciler core
         (NotebookFileReconciler, NotebookPathResolver, NotebookFileHash,
          NotebookFileIndexingRules; both sync paths delegate here)
              │
              ▼
Phase 2  Fast register + hot path
         (RegisterFilesAsync; ConversationStreamEngine before complete;
          DB-relative paths from tools)
              │
              ▼
Phase 3  Call-site cleanup
         (all QueueNotebookSync → Register+QueueReconcile; delete dup helpers;
          move enumerator to API project; upload path uses reconciler where safe)
              │
              ▼
Phase 4  Tests + acceptance
         (serving-gate integration tests; mount reparse; acceptance-evidence.md)
```

**Rules:**

- A phase is not done until its gate (section 4) passes on the current branch.
- **Do not** merge partial phases to `main`. One PR when section 6 is complete.
- Phases are sequential — Phase 2 depends on reconciler APIs from Phase 1.

---

## 4. Verification gates

### 4.1 Global invariants (every phase)

- [ ] `dotnet build GuideAntsApi.sln` — 0 errors; warnings not worse than baseline.
- [ ] `dotnet test GuideAntsApi.sln` — no new failures vs baseline.
- [ ] `npm run build` + `npm test -- --run` in `src/client` — green.
- [ ] **Single reconciler:** `NotebookFileSyncService` and `SyncNotebookHandler` contain
      no independent hash/insert/enumerate loops — they delegate to `NotebookFileReconciler`.
- [ ] **DB gate unchanged for normal files:** `GetFileContentStreamAsync` still requires a
      row (no silent filesystem fallback for standard notebook files — N2).
- [ ] **Mount behavior preserved:** registered mount roots not recursively indexed;
      stale rows under mounts cleaned without deleting host content
      (`NotebookSyncMountReparseTests` still pass).
- [ ] **Deletion rules preserved:** rows referenced by `MessageAttachments` or
      `ContentFileVersions` are not removed during reconcile.
- [ ] **No fallback masking** (user rule): parse/register failures log and skip per-file;
      do not invent rows for missing disk files.
- [ ] **Matches `DECISIONS.md`.**

### 4.2 Phase 1 — Reconciler core

- [ ] `NotebookFileReconciler` exists with `ReconcileNotebookAsync(Full)` used by both
      `NotebookFileSyncService` and `SyncNotebookHandler`.
- [ ] `NotebookPathResolver`, `NotebookFileHash`, `NotebookFileIndexingRules` exist.
- [ ] Behavior-neutral vs pre-change for manual `/files/sync` and existing unit tests
      (full sync still hashes before index enqueue).
- [ ] `SyncNotebookHandler` ≤ ~40 lines of orchestration (no local `ComputeSha256`).

### 4.3 Phase 2 — Fast register + hot path

- [ ] `RegisterFilesAsync(notebookId, dbRelativePaths)` on `INotebookFileSyncService`.
- [ ] Inserts/updates with **placeholder hash** (`NotebookFileHash.Placeholder`) using
      stat only; `SaveChanges` before returning.
- [ ] `ConversationStreamEngine`: register reported paths **before** SSE `complete`;
      then `QueueReconcileAsync` (full background reconcile).
- [ ] Tool outputs carry **DB-relative** paths internally (`ScriptExecutionResult` or
      parallel fields) for image/script/podcast/wire paths.
- [ ] **serving-gate** passes (section 4.5).

### 4.4 Phase 3 — Call-site cleanup

- [ ] All prior `QueueNotebookSyncAsync`-only tool paths call `RegisterFilesAsync` when
      paths are known, then queue reconcile.
- [ ] `NotebookSyncFileEnumerator` lives under `GuideAntsApi` (not BackgroundJobs-only).
- [ ] `NotebookFileChangeReporter` uses `NotebookPathResolver.ToCwdRelative`.
- [ ] `FindNotebookFileByRelativePathAsync` uses `NotebookPathResolver` (no duplicate
      `GetAlternativePaths`).
- [ ] Dead private `ComputeSha256` / `IsTemporaryScriptFile` / `IsDirectIndexable` removed
      from sync handler and sync service (shared utilities only).

### 4.5 Serving gate (summary)

Defined in [`serving-gate.md`](./serving-gate.md). Run after Phase 2 and at final
acceptance. Pass when:

- `/files/content` returns **200** for a tool-created file **after** turn `complete` and
  **before** `SyncNotebook` job runs.
- Folder tree API lists the file in the same window.
- Chat markdown image fetch succeeds on first post-stream attempt (no permanent 404).

### 4.6 Phase 4 — Tests + acceptance

- [ ] New integration tests documented in `acceptance-evidence.md`.
- [ ] Existing `NotebookFileSyncEndpointsTests`, `NotebookSyncMountReparseTests`,
      `SyncNotebookHandlerTests` updated and green.
- [ ] `acceptance-evidence.md` complete for the single PR.

---

## 5. Deviation & failure protocol

When a gate fails, **stop the line** — do not start the next phase.

1. **Classify** in `STATUS.md`:
   - `build/test red` → fix mechanically.
   - `serving-gate fail` → register not before complete, wrong path, or row missing.
   - `mount regression` → enumerator/reconciler broke host-mount skip rules.
   - `duplicate logic` → handler/service still own hash loops.
   - `filesystem fallback added` → violates N2; revert.
   - `scope creep` → out-of-scope file; revert or update brief + DECISIONS.
2. Fix in the **owning phase**; re-run the **full** gate for that phase.
3. Record attempt + fix in `STATUS.md` deviation log.
4. Do not land partial work on `main`.

---

## 6. Final acceptance (single PR ready)

The job is complete only when **all** hold:

- [ ] Phases 1–4 marked `DONE` in `STATUS.md`.
- [ ] **serving-gate** green on final tree.
- [ ] **One reconciler** — grep confirms no `ComputeSha256` in `SyncNotebookHandler.cs`.
- [ ] Chat turn with file creation: sidebar + markdown image + download link work without
      waiting for background job poll interval.
- [ ] Full background reconcile still: backfills SHA-256, enqueues index/extract, removes
      stale rows, respects attachments.
- [ ] Mount reparse tests pass.
- [ ] `acceptance-evidence.md` captured.
- [ ] One PR opened with full diff; user reviews and merges after CI green.

---

## 7. Report-back contract (final handoff to user)

```text
NOTEBOOK SYNC UNIFICATION — FINAL REPORT
Branch: <branch>
PR: <url or "ready to open">

BASELINE:
- Server build/test: <pass + counts>
- Client build/test: <pass + counts>

PHASES:
- Phase 1 reconciler core: <DONE + notes>
- Phase 2 fast register: <DONE + notes>
- Phase 3 call-site cleanup: <DONE + notes>
- Phase 4 tests: <DONE + notes>

SERVING GATE:
- Content 200 before SyncNotebook job: <pass/fail + test name>
- Folder tree before job: <pass/fail>
- Chat image first fetch: <pass/fail>

INVARIANTS:
- Single reconciler: <pass/fail>
- Mount reparse: <pass/fail>
- Attachment-preserving delete: <pass/fail>

DEVIATIONS: <none | list from STATUS.md>

FILES ADDED/MOVED (high level):
- <list>

RECOMMENDED POST-MERGE SMOKE:
- <manual steps>
```
