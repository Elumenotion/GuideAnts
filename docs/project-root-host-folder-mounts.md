# Project-Root Host Folder Mounts

This document covers the project-root host folder mount feature. It builds on
the notebook-level mount system documented in
[`host-folder-mounts.md`](./host-folder-mounts.md).

## What this feature does

Project-root host folder mounts let an admin mount a folder from the Docker host
so that it appears in two places simultaneously:

1. **At the root of the project file tree** — browsable, expandable into its
   real files and subfolders.
2. **Inside every notebook in the project** — as a symlinked folder at the
   notebook root, with full read-write access (existing notebook mount behavior).

This includes notebooks created or copied *after* the mount was set up.

Before this feature, project-wide mounting was only reachable through a
notebook's "Map host folder" dialog by switching a scope dropdown to "All
notebooks in project." The result never appeared in the project's own tree. This
feature moves project-wide mounting to the **project screen** and makes the
mounted folder a first-class entry in the project file tree.

### Notebook dialog simplification

The notebook "Map host folder" dialog no longer has a scope selector. It always
mounts into the current notebook only. Project-wide mounting is done exclusively
from the project screen.

## How to use it

### Mount a host folder to the project

1. Open a project and **right-click the project root folder** in the sidebar.
2. Select **"Map host folder here"** (visible to admins only).
3. Enter the **absolute host path** of the folder to mount. Optionally set a
   custom leaf name (defaults to the last segment of the host path).
4. Click **Create mapping**. A command dialog appears with the generated
   `guideants-host-mount.sh apply ...` (or `.ps1`) command.
5. Run the displayed command on the Docker host. This adds bind-mount entries to
   `docker-compose.host-mounts.generated.yml` and restarts the affected services.
6. Back in the app, right-click the project root and select **"Check mapped
   folders"** (reconcile). The mounted folder now appears at the project root and
   can be expanded to browse its contents. The same folder also appears inside
   every notebook in the project.

### Context menu actions

**On the project root folder (admin):**

| Action | Description |
|---|---|
| Map host folder here | Opens the mount creation dialog |
| Check mapped folders | Reconciles all project-scope mounts |

**On a mount root node (admin):**

| Action | Description |
|---|---|
| Remove mapped folder | Returns the host remove command |
| Show apply command | Re-displays the apply command |
| Show remove command | Re-displays the remove command |
| Check mapped folders | Reconciles this specific mount |

**On subfolders within a mount:**

Mount subfolders are browsable (expandable/collapsible) but read-only. The
context menu shows "Read-only (host mount)" — no rename, delete, upload, or
create operations.

### Mount status badges

Mount root nodes display a colored status badge:

| Badge | Meaning |
|---|---|
| **Linked** (green) | Mount is active and working |
| **Pending restart** (amber) | Mount created but host command not yet run |
| **Missing source** (orange) | Container mount path is absent |
| **Link error** (red) | Symlink or mount error |
| **Pending removal** (slate) | Marked for removal, host command not yet run |

### Remove a project mount

1. Right-click the mount root in the project tree.
2. Select **Remove mapped folder**.
3. Run the displayed remove command on the host.
4. Reconcile. The mount disappears from both the project tree and all notebooks.

Host folder contents are never deleted — only symlinks and compose volume entries
are removed.

### Installer CLI

The interactive installer (`./installer/guideants.sh`) mount flow offers the
project-wide option as **"Entire project (project root + every notebook)"**. The
API payload is unchanged (`scope: "Project"`).

## How it works

### Architecture overview

The feature reuses the existing project-scope mount machinery. No new mount type,
database table, or API endpoint is introduced.

```
                    HostFolderMount (Scope=Project)
                            |
              +-------------+-------------+
              |                           |
    Project file tree              Per-notebook links
    (virtual overlay)         (HostFolderMountLink per notebook)
              |                           |
    Scans ContainerSourcePath    Symlink at notebook root
    /app/HostMounts/{key}        /app/ContentFiles/{proj}/{nb}/{leaf}
```

### Backend

**Mount creation** uses the existing `HostFolderMountService.CreateMountAsync`
with `Scope=Project` and `NotebookId=null`. This creates a `HostFolderMountLink`
for every notebook in the project. No change to this path.

**New-notebook back-fill** is already implemented. When a notebook is created or
copied after a project mount exists, `ApplyProjectScopedMappingsToNewNotebookAsync`
adds a link for every active project-scope mount. No change to this path.

**Project tree overlay** is new. `ProjectFolderService.GetFolderTreeAsync` now:

1. Builds the normal DB-backed folder tree (unchanged).
2. Queries active project-scope mounts (`Scope == Project`, status not
   `Removed` or `PendingRemoval`).
3. For each mount, scans `ContainerSourcePath` (`/app/HostMounts/{mountKey}`)
   using `HostMountDirectoryScanner` and injects a root-level `FolderTreeDto`
   node with children representing the mount's file/folder structure.

Mount nodes carry metadata:
- `IsHostMount: true` on the mount root
- `MountId` and `MountStatus` on the mount root
- `IsLinked: true` on child folders within the mount
- Unique deterministic IDs (SHA256 of mount ID + path) for stable
  expand/collapse state in the UI

**Shared scanner.** `HostMountDirectoryScanner` is shared by notebook and project
trees. The initial tree load is a **first page**, not a hard ceiling:

| Parameter | Default | Config key |
|---|---|---|
| Max files (per scan/list) | 5,000 | `FileStorage:LinkedMountTreeMaxFiles` |
| Initial max depth | 3 | `FileStorage:LinkedMountTreeMaxDepth` |
| Scan timeout | 2,500 ms | `FileStorage:LinkedMountTreeScanBudgetMs` |
| Shallow + listing cache TTL | 120 s | `FileStorage:LinkedMountTreeCacheSeconds` |
| Project overlay cache TTL | 120 s | `FileStorage:ProjectMountTreeCacheSeconds` |

**Browse model (R1–R4):**

1. **Initial window** — eager scan to depth 3 from the mount root, including
   **directory stubs** (folders appear even when they have no files in-window).
2. **Lazy load** — expanding a folder under a mount fetches **one directory
   level** via `GET .../host-mounts/listing?path=...` (immediate files + child
   folder stubs). Deeper levels require further expands.
3. **Cache** — shallow root pages and per-path listings are cached. Polling
   `.../tree` must not re-walk the host when the cache is warm. Invalidate on
   mount reconcile/remove, API mutations under the mount, and explicit refresh.
4. **Sync** — mount interiors are still not recursively indexed into
   `NotebookFile`; browse APIs only.

The scanner filters temporary script files (`{hash}_script.sh/ps1/py`) and
`__pycache__/` directories. Notebook-specific filters (`Resources/`,
`.guideants/`) are applied in `NotebookFileService` after scanning, not in the
shared scanner.

**Acceptance:** Playwright scenario
`walkthroughs/scenarios/notebook/host-mount-lazy-tree.spec.ts` proves expand
below depth 3 against the notebook’s **existing** linked host mount (default path
`samples/skills/audiocpp skills`). Unit tests are supporting only.

**DTO additions.** `FolderTreeDto` gained four optional fields:

```csharp
public record FolderTreeDto(
    ...
    bool IsHostMount = false,
    Guid? MountId = null,
    HostFolderMountStatus? MountStatus = null,
    bool IsLinked = false
);
```

These mirror the notebook tree DTO's mount flags so the frontend badge/display
utilities work identically.

### Frontend

**`MapHostFolderDialog`** is now scope-agnostic. The scope `<select>` is removed.
The scope is passed as a prop: notebook callers pass `scope="Notebook"`, the
project sidebar passes `scope="Project"`. Heading and help text adapt to the
scope.

**`useProjectHostMounts`** is a new hook mirroring `useNotebookHostMounts`. It
calls `hostFolderMountsApi.list` filtered to `scope === 'Project'` and builds
`ProjectHostMountEntry` objects with derived display state.

**Project `FolderTree`** accepts mount handler props (`onMapHostFolder`,
`onRemoveMappedFolder`, `onShowApplyCommand`, `onShowRemoveCommand`,
`onCheckMappedFolders`, `isAdmin`) and uses them to:

- Add "Map host folder here" and "Check mapped folders" to the root folder's
  context menu (admin only).
- Render mount root nodes with `HostMountStateBadge`.
- Show mount-specific context menu on mount root nodes (remove, show commands,
  check).
- Show read-only indicator on mount subfolder context menus.
- Allow full expand/collapse browsing of mount contents.

**`ProjectSidebar`** wires the mount dialogs and handlers:

- Opens `MapHostFolderDialog` with `scope="Project"` for mount creation.
- Opens `HostMountCommandDialog` for apply/remove command display.
- Manages reconcile calls and toast notifications.

Reused components (no changes): `HostMountCommandDialog`,
`HostMountStateBadge`, `hostMountDisplayState` utilities,
`hostFolderMountsApi` service.

### Installer CLI

`installer/guideants.sh` relabels the project-wide mount choice from
"All notebooks" to "Entire project (project root + every notebook)". Log
messages update similarly. The API payload is unchanged (`scope: "Project"`).
No changes to `remove_host_mount` or the PowerShell helper.

## What did not change

- `HostFolderMountLink` entity (stays per-notebook).
- `HostFolderMountService` create/reconcile/remove logic.
- Host apply/remove scripts (`guideants-host-mount.sh`, `.ps1`).
- Notebook file tree rendering of mounts.
- New-notebook back-fill path.
- API endpoints — no new endpoints added.
- Script-execution agent registered-links-only security model.

## Key files

### Backend

| File | Role |
|---|---|
| `Services/Components/HostMountDirectoryScanner.cs` | Shared budgeted directory scanner (new) |
| `Services/Components/ProjectFolderService.cs` | Project tree overlay with mount nodes |
| `Services/Components/NotebookFileService.cs` | Refactored to use shared scanner |
| `Models/ProjectFolderDtos.cs` | `FolderTreeDto` mount metadata fields |

### Frontend

| File | Role |
|---|---|
| `components/notebook/hostMounts/MapHostFolderDialog.tsx` | Scope-agnostic dialog |
| `hooks/useProjectHostMounts.ts` | Project-scope mount hook (new) |
| `components/project/sidebar/FolderTree.tsx` | Mount badges, context menus, browsing |
| `components/project/sidebar/ProjectSidebar.tsx` | Dialog wiring and handlers |
| `types/hostFolderMount.ts` | `ProjectHostMountEntry` type |
| `types/project.ts` | `FolderTreeDto` mount fields |
| `utils/hostMountDisplayState.ts` | `buildProjectHostMountEntry` utility |

### Installer

| File | Role |
|---|---|
| `installer/guideants.sh` | Relabeled project-wide mount UX text |

## Related documents

- Notebook-level mount guide: [`host-folder-mounts.md`](./host-folder-mounts.md)
- Implementation plan: [`../plans/project-root-host-folder-mounts.md`](../plans/project-root-host-folder-mounts.md)
