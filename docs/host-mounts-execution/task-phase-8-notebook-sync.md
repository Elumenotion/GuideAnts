# Task — Phase 8: Notebook file sync — reparse-point handling

> Subagent brief. Execute top to bottom; return the Report-back contract verbatim.

## Mission

Make both notebook-file-sync code paths mount-aware: surface a registered mapped
folder as a first-class tree entry **without** recursively indexing/SHA-256-hashing
the mounted source, and honor the mount delete/move rules — in **both** sync
implementations so they stay consistent.

> **Decision required:** D1 (indexing default). The default lock is "not indexed by
> default"; implement to that unless D1 says otherwise.

## Read first

- `../host-folder-notebook-mounts-plan.md` §14 (two sync paths, reparse-traversal
  caveat, recommended first implementation, delete rules, optional `NotebookFile`
  fields).
- `./DECISIONS.md` → D1, Part B (no recursive index by default, no host-content
  deletion).
- `src/server/GuideAntsApi/Services/Components/NotebookFileSyncService.cs`
  (resolver-aware).
- `src/server/GuideAntsApi.BackgroundJobs/Jobs/SyncNotebookHandler.cs` (builds paths
  manually, no `IStoragePathResolver`).
- Phase 6 `mounts.json` (to identify registered mount roots).

## Preconditions

- Phase 7 gate green. (Can run in parallel with Phase 9 after Phase 7.)

## Guardrails (hard)

- **Both** paths must behave identically w.r.t. mounts:
  `NotebookFileSyncService` **and** `SyncNotebookHandler`.
- **Verify the .NET enumeration default before relying on it** (plan §14):
  `Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)` does **not** add
  `FileAttributes.ReparsePoint` to `AttributesToSkip` by default, so it may descend
  into the junction. Enforce the intended behavior **explicitly** — skip
  `FileAttributes.ReparsePoint` for registered mount roots (or all reparse points).
  Add a test that proves the mounted source is **not** indexed/hashed.
- Treat registered mounted folders as **first-class folder tree entries**; do not
  recursively index their contents by default (D1).
- **Never delete host source content** during sync, mount-root delete, or removal.
- Delete/move rules (plan §14): deleting the mount root = remove mapping (not
  contents); deleting files inside = real host delete; moving/renaming the mount root
  is blocked/metadata-only; moving/renaming files inside is allowed.
- If you add the optional `NotebookFile.IsMounted` / `HostFolderMountId` /
  `ExternalRelativePath` fields, that requires a migration — keep it minimal and note
  it; otherwise prefer surfacing mounts without new columns.
- **No fallback:** if a mount root is registered but its source is absent, reflect it
  as a non-indexed entry / status — do not silently treat it as a normal folder and
  crawl it.

## Tasks

1. In **both** sync paths, detect registered mount roots (via `mounts.json` /
   service) and skip reparse-point descent for them.
2. Surface the mapped folder as a first-class tree entry (not its recursive
   contents).
3. Apply the §14 delete/move semantics at the sync layer (coordinate the actual
   block/allow enforcement with Phases 10/11 for UI/remove, but sync must not crawl
   or delete through the link).
4. Add tests proving: a planted junction under a notebook root is **not** descended
   into / not hashed; both sync paths agree; host content untouched.

## Files in scope

- `src/server/GuideAntsApi/Services/Components/NotebookFileSyncService.cs`
- `src/server/GuideAntsApi.BackgroundJobs/Jobs/SyncNotebookHandler.cs`
- Optional `NotebookFile` fields + migration (only if you choose the row-based
  approach)
- `src/server/GuideAntsApi.Tests/*` (sync reparse tests)

**Out of scope:** UI, remove flow, the guard, endpoints.

## Self-verification

```bash
cd src/server && dotnet build GuideAntsApi.sln
cd src/server && dotnet test GuideAntsApi.Tests/GuideAntsApi.Tests.csproj --filter Sync
```

Plus global gate (orchestration §4.1). If you added `NotebookFile` columns, also run
`DataModel.Tests` + a fresh-DB migration apply.

## Definition of Done

- [ ] Both sync paths skip reparse descent for registered mounts; **proven** by a
      test that the mounted source is not indexed/hashed.
- [ ] Mapped folder surfaces as a first-class entry; no recursive index by default
      (D1).
- [ ] §14 delete/move semantics honored at the sync layer; host content never
      deleted.
- [ ] Both paths consistent; tests green (+ migration if columns added).

## Report-back contract (return exactly this)

```
PHASE 8 REPORT
- .NET enumeration default verified (descends into junction?): <yes/no + how tested>
- Reparse skip applied in: NotebookFileSyncService=<y> SyncNotebookHandler=<y>
- Mounted source NOT indexed/hashed (test name): <name>
- Indexing default implemented (D1): <never/opt-in/always>
- NotebookFile columns added? <no / list + migration name>
- Delete/move semantics enforced at sync layer: <summary>
- Verification: build=<...> sync-tests=<counts> datamodel-tests=<counts or n-a>
- Files touched: <list>
- Deviations / surprises: <list or "none">
```
