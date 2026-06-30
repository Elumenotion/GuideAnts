# Mounted File Context Menus

## Overview

When a host folder is mounted into a project or notebook, the files inside it are read-only for most mutating operations (rename, delete, move). Previously the context menus were inconsistent:

- **Notebook tree**: showed "Linked files are read-only here." but hid the Edit button even for editable markdown files.
- **Project tree**: showed the full menu (Edit, Create Notebook, Set Home Page, Rename, Download, Delete) even though most options fail against a host mount.

Both trees now present a **unified mounted-file context menu**: **Edit** (markdown only), **Preview**, and **Download**.

## Mounted-File Menu Options

### Edit (markdown only)

Available when the file is a markdown file (`.md`, `.markdown`, or content type `text/markdown` / `text/x-markdown`).

Opens `FullScreenEditor`, a Lexical-based rich text markdown editor. On save:
- **Notebook**: content is written back through `notebookFilesApi.uploadFiles`, which persists changes to the host-mounted folder on disk.
- **Project**: content is saved via `PUT /api/projects/{projectId}/files/mounted/content?path={relativePath}`, which writes directly to the host file on disk using the mount's `ContainerSourcePath`.

### Preview

- **Notebook tree**: triggers `onPreviewFile`, which opens `FilePreviewOverlay` — the same overlay used when double-clicking a file.
- **Project tree**: triggers `onFileSelect`, which opens the file in the project's main content viewer — the same view opened by double-clicking a file in the sidebar.

### Download

Fetches the file content via the API and triggers a browser download. This is a read-only operation and always safe for mounted files.

## Hidden Options (and why)

The following options are hidden when a file is inside a host mount because they would fail or produce errors:

| Option | Where hidden | Why hidden |
|---|---|---|
| Create Notebook from File | Context menu | Requires project-managed file, not a host mount reference |
| Set Project Home Page | Context menu | Requires a stable project content file ID |
| Rename | Context menu | Cannot rename files on the host through the project API |
| Delete | Context menu + content viewer header | Cannot delete host-mounted files through the project API |
| History | Content viewer header | No DB versioning for host-mounted files |
| Publish to Project (notebook) | Context menu | Not applicable for linked files |
| Set as Notebook Home Page (notebook) | Context menu | Not applicable for linked files |

For **multi-select** on mounted files, only **Download N Items** is available.

## How Mount Detection Works

### Notebook tree (`NotebookFolderTree.tsx`)

Files are detected as mounted via three checks (combined in the `isLinkedFile` helper):

1. `file.isLinked` — boolean flag set by the API on the `NotebookFileDto`.
2. `context.getMountForPath(relativePath)` — returns a mount if the path exactly matches a mount's relative path.
3. `context.getEnclosingMount(relativePath)` — returns a mount if the path is inside a mounted folder.

These are provided by the `NotebookFolderTreeContext`, which sources mount data from `useNotebookHostMounts()`.

### Project tree (`FolderTree.tsx`)

Mount status is tracked at the **folder level** via `FolderTreeDto` fields:

- `isHostMount` — the folder itself is a direct host mount.
- `isLinked` — the folder is inside a host mount.

Since individual files don't carry these flags, mount awareness is propagated down the recursive `FolderNode` tree via a `parentInsideMount` prop:

```
insideMount = parentInsideMount || folder.isHostMount || folder.isLinked
```

Each child `FolderNode` receives `parentInsideMount={insideMount}`, so files at any depth inside a mount are correctly detected.

## Path-Based API for Project Mounted Files

Standard project file APIs are GUID-based and resolve from the database. Host-mounted files have no database row, so they use path-based endpoints instead:

| Endpoint | Method | Purpose |
|---|---|---|
| `/files/mounted/details?path=` | GET | Returns file metadata (synthetic id, content type, size, mtime) |
| `/files/mounted/content?path=` | GET | Returns file content (stream) for preview and download |
| `/files/mounted/content?path=` | PUT | Saves content back to the host file (write-back) |

The `relativePath` is prefixed with the mount's `LeafName` (e.g. `GuideAnts/README.md`). The backend resolves this to a physical path under the mount's `ContainerSourcePath` with traversal safety checks (`Path.GetFullPath` + `StartsWith` guard).

Each mounted file receives a **deterministic synthetic GUID** (SHA256-based, keyed on `mountfile:{mountId}:{relativePath}`) so files are uniquely selectable and React keys remain stable across folder tree refreshes.

## Affected Files

- `src/client/src/components/notebook/sidebar/NotebookFolderTree.tsx` — Removed `!selectedFileIsLinked` guard on Edit button, removed "Linked files are read-only here." text from both single-file and multi-select menus.
- `src/client/src/components/project/sidebar/FolderTree.tsx` — Added `parentInsideMount` prop to `FolderNode`, gated single-file and multi-select menus based on `insideMount`. Updated `openMarkdownEditor`, `saveMarkdownEditor`, and `handleDownloadSelectedFile` to use path-based APIs when `insideMount`.
- `src/client/src/components/project/content/ContentFileContent.tsx` — Added `mountedRelativePath` prop; routes fetch/edit/save/download through path-based APIs for mounted files. Passes `mountedRelativePath` to all three `<FileContents>` rendering paths. Added `mountedRelativePath` to `React.memo` comparator. Hides Delete and History buttons in content viewer header for mounted files.
- `src/client/src/components/project/content/FileContents.tsx` — Added `mountedRelativePath` prop; fetches preview content via path-based API when set; added to `useEffect` dependency array.
- `src/client/src/services/api.ts` — Added `getMountedDetailsByPath`, `getMountedContentByPath`, and `saveMountedByPath` methods.
- `src/client/src/pages/ProjectDetails.tsx` — Added `findMountedFileInTree` helper; passes `mountedRelativePath` to `ContentFileContent` for mounted files.
- `src/server/GuideAntsApi/Services/Components/ProjectFolderService.cs` — Added `CreateMountVirtualFileId` for deterministic synthetic ids, `FileExtensionContentTypeProvider` for content types, `ResolveMountedFilePhysicalPathAsync` for path resolution with traversal safety, and `GetMountedFileContentAsync`/`GetMountedFileDetailsAsync`/`SaveMountedFileContentAsync` service methods.
- `src/server/GuideAntsApi/Services/Components/IProjectFolderService.cs` — Added mounted file interface methods.
- `src/server/GuideAntsApi/Endpoints/ProjectContentFileEndpoints.cs` — Added three mounted-file endpoints.

## Testing

1. **Notebook mounted markdown**: right-click a linked `.md` file — confirm Edit, Preview, Download are shown; no "read-only" text. Edit and save — confirm changes persist to host.
2. **Notebook mounted non-markdown**: right-click a linked `.txt` or other file — confirm only Preview and Download.
3. **Notebook multi-select**: select multiple linked files — confirm no "read-only" text.
4. **Project mounted markdown**: right-click a `.md` inside a host-mounted folder — confirm Edit, Preview, Download only. Select it — content viewer shows Edit and Download buttons only (no Delete or History).
5. **Project mounted non-markdown**: right-click a non-markdown file — confirm Preview and Download only. Content viewer shows Download only (no Edit, Delete, or History).
6. **Project nested mount**: files in subfolders of a mounted root also get the restricted menu.
7. **Project multi-select mount**: only Download N Items shown.
8. **Regression (unmounted)**: in projects/notebooks without mounts, the full context menu is still available.
