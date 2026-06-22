# Host Folder Notebook Mounts Plan

## 1. Goal

Add an admin-only feature that maps a local host folder into notebook file trees as a writable child folder of the notebook root.

The user experience should be:

1. Admin opens a notebook folder tree menu.
2. Admin selects a host folder mapping action.
3. Admin chooses scope:
   - this notebook only
   - all notebooks in this project
4. UI displays a host command appropriate to the current start script/runtime configuration.
5. Admin runs the command.
6. The command updates a Docker Compose override and restarts only affected services.
7. The API manages symlinks under notebook roots.

Mapped folders should appear as:

```text
{notebookRoot}/{leafFolderName}
```

There should be no `External/` wrapper folder.

## 2. Key Constraints

- The normal runtime mode is containerized. The API, script execution, PlantUML, and related file-processing concerns run in containers.
- A container cannot access arbitrary host paths unless Docker mounts them into that container.
- A mount source may be a **local host folder** (the first implementation target) or a **Docker-resolvable networked volume** (e.g. SMB/CIFS). The architecture must not preclude networked sources, even though local folders ship first.
- The supported runtime for the first cut is single-machine, localhost Docker Desktop (Linux engine, WSL2 backend). The admin is on the host, so admin-supplied absolute host paths are acceptable.
- Adding a brand-new mount source requires updating Compose configuration and restarting affected services.
- Once a host source is mounted into the containers, the API can create symlinks for additional notebooks without another Compose change.
- Mapped folders are read-write.
- Only admin users can create, remove, or repair host folder mappings.
- Removing a mapping must never delete host folder contents.

## 3. Architecture Summary

Use two layers:

1. **Mount source** (local bind first; networked volumes supported)
   - The source is mounted once into each relevant container under a stable internal path:

   ```text
   /app/HostMounts/{mountKey}
   ```

   - For local folders this is a Docker bind mount. For networked sources it is a `local`-driver volume with CIFS options (see §4). The symlink layer below is identical regardless of source kind, because both surface as the same container path.

2. **API-managed notebook symlink**
   - The API creates symlinks inside notebook roots:

   ```text
   /app/ContentFiles/{projectSlug}/{notebookSlug}/{leafFolderName}
     -> /app/HostMounts/{mountKey}
   ```

This avoids per-notebook Compose entries. A project-wide mapping can be applied to future notebooks by creating another symlink, as long as the source mount already exists.

### 3.1 Verified behavior (symlink spike)

The symlink mechanism was validated on the supported stack (Docker Desktop, Linux engine, WSL2/Ubuntu backend) against the Windows-host content bind mount (`GA_CONTENT_FILES_HOST_PATH` -> `/app/ContentFiles`):

- Creating a symlink inside `/app/ContentFiles/...` that points at `/app/HostMounts/{mountKey}` succeeds.
- The link is readable, traversable, and **writable** through to the target.
- The link **persists to the backing store**: a second container with the same bind mount sees the same symlink.

Two consequences to design around:

- The link materializes on the Windows host as an **NTFS junction whose target is a Linux-absolute path** (`/app/HostMounts/{mountKey}`), which is meaningless host-side. Mapped-folder links are therefore **container-namespace artifacts**, not host-usable paths. Windows Explorer, host backups, and host sync tools will see a dangling/broken junction. Every content-touching service that should resolve the link must have `/app/HostMounts/{mountKey}` mounted; otherwise it sees a dangling link.
- The link is a **reparse point**. The script-execution path guard rejects reparse points today, so the §13 changes are mandatory, not optional.

## 4. Compose Override

Add a generated override file:

```text
docker/docker-compose.host-mounts.generated.yml
```

The `start_*` scripts should include this file if present.

Linux/macOS:

```bash
docker compose -f "$COMPOSE_FILE" -f docker-compose.host-mounts.generated.yml up -d
```

Windows:

```cmd
docker compose -f "%COMPOSE_FILE%" -f "docker-compose.host-mounts.generated.yml" up -d
```

The generated override mounts each configured source into all services that need content-file access.

Local folder example (first implementation target):

```yaml
services:
  guideants-webapi-ui:
    volumes:
      - type: bind
        source: D:/Data/Shared
        target: /app/HostMounts/shared-data
        read_only: false

  guideants-ai:
    volumes:
      - type: bind
        source: D:/Data/Shared
        target: /app/HostMounts/shared-data
        read_only: false

  plantuml:
    volumes:
      - type: bind
        source: D:/Data/Shared
        target: /app/HostMounts/shared-data
        read_only: false
```

Networked source example (SMB/CIFS — not in the first cut, but the generator must not preclude it):

```yaml
services:
  guideants-webapi-ui:
    volumes:
      - shared-smb:/app/HostMounts/shared-data
  guideants-ai:
    volumes:
      - shared-smb:/app/HostMounts/shared-data
  plantuml:
    volumes:
      - shared-smb:/app/HostMounts/shared-data

volumes:
  shared-smb:
    driver: local
    driver_opts:
      type: cifs
      device: "//server/share/folder"
      o: "uid=1000,gid=1000,file_mode=0664,dir_mode=0775,vers=3.0,${SMB_CREDENTIALS_REF}"
```

Notes on networked sources:

- CIFS write permissions are set at mount time (`uid`/`gid`/`file_mode`/`dir_mode`), not per-file, so writability is a property of the mount, not the symlink.
- `device` mounts a whole share; to expose a subfolder, either include the subpath in `device` (`//server/share/sub`) or point the leaf symlink deeper. Pick one convention before implementing.
- Credentials must not be inlined in the displayed command or API responses — see §20.

The override generator should branch on source kind (local bind vs networked volume) and emit the appropriate block.

Services to include initially:

- `guideants-webapi-ui`
- `guideants-ai`
- `plantuml`

Review whether other services require the same mount before implementation:

- document server
- document extraction services
- future standalone file-processing workers

## 5. Runtime Configuration

The API needs enough runtime context to display the correct host command.

Add environment variables to compose files:

```text
GuideAntsRuntime__StartCommand
GuideAntsRuntime__ComposeFile
GuideAntsRuntime__HostMountOverrideFile
GuideAntsRuntime__DockerDirectory
GuideAntsRuntime__AffectedMountServices
```

Example values:

```text
GuideAntsRuntime__StartCommand=start_windows.cmd
GuideAntsRuntime__ComposeFile=docker-compose.ghcr-cpu.yml
GuideAntsRuntime__HostMountOverrideFile=docker-compose.host-mounts.generated.yml
GuideAntsRuntime__DockerDirectory=docker
GuideAntsRuntime__AffectedMountServices=guideants-webapi-ui;guideants-ai;plantuml
```

The `start_*` scripts already detect backend and compose mode. They should persist enough state in `.installer_state.env` for helper scripts to reconstruct the compose command.

## 6. Host Helper Commands

Add platform-specific helper scripts:

```text
scripts/guideants-host-mount.ps1
scripts/guideants-host-mount.sh
```

The UI should display one of these commands.

Create/apply example:

```powershell
.\scripts\guideants-host-mount.ps1 apply `
  -MountId "..." `
  -HostPath "D:\Data\Shared"
```

```bash
./scripts/guideants-host-mount.sh apply \
  --mount-id "..." \
  --host-path "/Users/me/Data/Shared"
```

Remove example:

```powershell
.\scripts\guideants-host-mount.ps1 remove -MountId "..."
```

```bash
./scripts/guideants-host-mount.sh remove --mount-id "..."
```

The helper script should:

1. Read `.installer_state.env` and/or arguments.
2. Fetch or receive the mount plan.
3. Rewrite `docker/docker-compose.host-mounts.generated.yml` idempotently.
4. Restart only affected services:

```bash
docker compose \
  -f docker-compose.ghcr-cpu.yml \
  -f docker-compose.host-mounts.generated.yml \
  up -d --no-deps guideants-webapi-ui guideants-ai plantuml
```

5. Optionally call back to the API to request mount reconciliation after restart.

**Self-restart caveat.** `guideants-webapi-ui` is in the affected-services list, so the restart bounces the very container serving the admin UI and any reconcile callback. Expect the admin's session to drop briefly when applying or removing a mount. Because of this, **startup reconciliation is the source of truth** and the post-restart callback (step 5) is best-effort/redundant, not a dependency.

## 7. Data Model

Add a host folder mount table.

```text
HostFolderMount
Id
ProjectId
NotebookId nullable
Scope
SourceKind
DisplayName
LeafName
MountKey
SourceSpec
ContainerSourcePath
NetworkDevice nullable
NetworkOptions nullable
CredentialRef nullable
Status
CreatedByUserId
CreatedUtc
UpdatedUtc
RemovedUtc nullable
ErrorMessage nullable
```

`SourceKind` (local ships first; networked must not be precluded):

```text
LocalPath
Smb
```

Source field semantics by kind:

- `LocalPath`: `SourceSpec` holds the absolute host path (the former `HostPathOriginal`). `NetworkDevice`/`NetworkOptions`/`CredentialRef` are null.
- `Smb`: `SourceSpec` holds the display/UNC form; `NetworkDevice` holds the CIFS `device` (`//server/share/sub`); `NetworkOptions` holds non-secret mount options (`uid`, `gid`, `file_mode`, `dir_mode`, `vers`); `CredentialRef` references the secret store (never the raw credentials — see §20).

`Scope`:

```text
Notebook
Project
```

`Status`:

```text
PendingRestart
Active
PendingRemoval
Removed
Error
```

Add a link table for per-notebook symlink state.

```text
HostFolderMountLink
Id
HostFolderMountId
NotebookId
LinkRelativePath
LinkPhysicalPath
Status
LastLinkedUtc nullable
LastCheckedUtc nullable
ErrorMessage nullable
```

`Status`:

```text
PendingRestart
PendingLink
Linked
Unlinked
LinkError
UnlinkError
```

## 8. Mount Key and Leaf Name

The leaf folder name is derived from the selected host folder by default.

Example:

```text
Host path: D:\Data\Shared Reports
Leaf name: Shared Reports
Notebook path: {notebookRoot}/Shared Reports
```

The user may rename the leaf during setup if needed.

`MountKey` must be filesystem-safe and stable, for example:

```text
{mountId:N}
```

or a slug plus short ID:

```text
shared-reports-8f3a2c
```

The container source path becomes:

```text
/app/HostMounts/{mountKey}
```

## 9. Validation Rules

Reject leaf names that:

- are empty
- contain path separators
- are `.` or `..`
- contain null characters
- collide with reserved notebook folders
- collide with an existing normal file or folder in any target notebook
- collide with another active mapping in any target notebook

Reserved names:

```text
.guideants
Output
Runs
Resources
files
```

For project-scoped mappings, validate every existing notebook in the project before creating the mapping.

## 10. API Services

Add `IHostFolderMountService`.

Responsibilities:

- create mount records
- create link records for notebook/project scope
- produce compose override plans
- produce host command text
- reconcile mounted source availability
- create symlinks
- remove symlinks
- update per-notebook `.guideants/mounts.json`
- repair stale or missing symlinks
- apply active project-scoped mappings to newly created notebooks

Add a reconciliation method:

```text
ReconcileProjectMountsAsync(projectId)
ReconcileNotebookMountsAsync(projectId, notebookId)
ReconcileAllMountsAsync()
```

Run reconciliation:

- on API startup
- after helper command callback
- after notebook creation
- when admin clicks "Check mappings"

## 11. API Endpoints

Admin-only endpoints:

```text
GET    /api/projects/{projectId}/host-folder-mounts
POST   /api/projects/{projectId}/host-folder-mounts
GET    /api/projects/{projectId}/host-folder-mounts/{mountId}
POST   /api/projects/{projectId}/host-folder-mounts/{mountId}/commands/apply
POST   /api/projects/{projectId}/host-folder-mounts/{mountId}/commands/remove
POST   /api/projects/{projectId}/host-folder-mounts/{mountId}/reconcile
DELETE /api/projects/{projectId}/host-folder-mounts/{mountId}
```

Request for create:

```json
{
  "notebookId": "optional-guid",
  "scope": "Project",
  "hostPath": "D:\\Data\\Shared",
  "leafName": "Shared"
}
```

Create response:

```json
{
  "mountId": "...",
  "status": "PendingRestart",
  "leafName": "Shared",
  "containerSourcePath": "/app/HostMounts/shared-8f3a2c",
  "command": ".\\scripts\\guideants-host-mount.ps1 apply -MountId \"...\" -HostPath \"D:\\Data\\Shared\""
}
```

Remove response:

```json
{
  "mountId": "...",
  "status": "PendingRemoval",
  "command": ".\\scripts\\guideants-host-mount.ps1 remove -MountId \"...\""
}
```

## 12. Symlink Materialization

For each target notebook:

1. Resolve notebook root with `IStoragePathResolver`.
2. Ensure `.guideants/notebook.json` exists.
3. Resolve link path:

```text
{notebookRoot}/{leafName}
```

4. Verify the link path is under the notebook root.
5. Verify the source path exists:

```text
/app/HostMounts/{mountKey}
```

6. Create symlink:

Linux/macOS:

```text
Directory symlink
```

Windows containers:

```text
Directory symlink or junction, depending on container privileges and runtime support
```

7. Update link state to `Linked`.
8. Write `.guideants/mounts.json`.

If symlink creation fails, mark the link `LinkError` and surface the error to admins.

## 13. Mount Registry for Script Agent

The API writes a per-notebook registry:

```text
{notebookRoot}/.guideants/mounts.json
```

Example:

```json
{
  "schemaVersion": 1,
  "mounts": [
    {
      "mountId": "9f2a...",
      "leafName": "Shared",
      "linkRelativePath": "Shared",
      "containerSourcePath": "/app/HostMounts/shared-8f3a2c",
      "writable": true
    }
  ]
}
```

**Current guard behavior (must be reworked).** `PathGuard` in `ScriptExecutionAgent/Program.cs` (`TryResolveAndAuthorizePath` / `HasReparsePointBetween`) does two things that block this design today:

1. It rejects **any** reparse point on the path between the storage root and the target. The mapped-folder link is a reparse point (confirmed in §3.1), so every `/execute` and `/files` call under a mapped folder currently fails.
2. It requires the target to be a strict child of `FILE_STORAGE_ROOT` (`/app/ContentFiles`). The resolved link target lives under `/app/HostMounts/...`, i.e. **outside** the storage root, so even after allowing the reparse point, the strict-child check rejects it.

Both must change. The guard needs `/app/HostMounts` (or each registered `containerSourcePath`) as an **additional authorized root**, and a registered-crossing allowance driven by `mounts.json`.

The script execution agent should allow registered symlink/reparse-point crossings only when:

- the symlink path matches a registered mount link in `mounts.json`
- the resolved target is under the registered `containerSourcePath`
- the mount is writable (for write operations)
- the request path remains under either the notebook root or an authorized mount source

All unregistered symlinks/reparse points remain rejected. This is the most security-sensitive change in the plan; treat the registry read, the additional-root logic, and the "only registered links followed" invariant as a single reviewed unit.

## 14. Notebook File Sync

The current sync service walks the notebook root. Host folder mounts introduce symlinked directories and writable external data.

**Two sync code paths must stay consistent.** Mount handling has to be applied to both:

- `NotebookFileSyncService` (`GuideAntsApi/Services/Components/NotebookFileSyncService.cs`) — resolver-aware.
- `SyncNotebookHandler` (`GuideAntsApi.BackgroundJobs/Jobs/SyncNotebookHandler.cs`) — builds paths manually and does not use `IStoragePathResolver`.

**Reparse-point traversal caveat (verify before relying on the default).** Both sync paths enumerate with `Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)`. By default .NET's enumeration does **not** add `FileAttributes.ReparsePoint` to `AttributesToSkip`, so it may descend into the mapped-folder junction and index/SHA-256-hash the entire mounted source — directly contradicting "do not recursively index by default" below. The intended behavior must be enforced explicitly by skipping `FileAttributes.ReparsePoint` for registered mount roots (or all reparse points) in both enumerations. Confirm the actual default with a quick test before implementing.

Recommended first implementation:

- Treat registered mounted folders as first-class folder tree entries.
- Do not recursively index/sync mounted contents by default.
- Serve/browse mounted contents through mount-aware file APIs.
- Never delete host source content when deleting or removing a mount root.

If mounted contents must appear as `NotebookFile` rows, add fields:

```text
NotebookFile.IsMounted
NotebookFile.HostFolderMountId nullable
NotebookFile.ExternalRelativePath nullable
```

Special delete rules:

- deleting the mount root removes the mapping, not host contents
- deleting files inside the mount deletes real host files
- moving or renaming the mount root is blocked or treated as mapping metadata update
- moving or renaming files inside the mount is allowed

## 15. Folder Tree UI

Admin menu additions:

- `Map host folder here`
- `Remove mapped folder`
- `Show apply command`
- `Show remove command`
- `Check mapped folders`

Because the target is always a child of the notebook root, the menu should be available on the notebook root or notebook file section, not arbitrary nested folders.

Display states:

- `Pending restart`: mapping exists, source is not mounted into containers yet
- `Linked`: symlink exists and source is reachable
- `Missing source`: symlink may exist, but `/app/HostMounts/{mountKey}` is absent
- `Link error`: API could not create the symlink
- `Pending removal`: symlink removal has begun; compose removal command still needs to run

Non-admin users:

- can see and use linked mapped folders according to normal notebook/project permissions
- cannot create, remove, repair, or view host commands

## 16. Create Flow

1. Admin selects `Map host folder here`.
2. UI collects host path, scope, and optional leaf name.
3. API validates admin authorization and leaf collisions.
4. API creates `HostFolderMount` with `PendingRestart`.
5. API creates target link rows:
   - one row for notebook scope
   - one row per existing project notebook for project scope
6. API returns apply command.
7. Admin runs command.
8. Command updates compose override and restarts affected services.
9. API startup or explicit callback runs reconciliation.
10. API creates symlinks and writes mount registries.
11. Folder tree shows mapped folder as linked.

## 17. New Notebook Flow

When a notebook is created:

1. Notebook is created normally.
2. API checks for active project-scoped host folder mounts.
3. For each active project mount:
   - create `HostFolderMountLink`
   - create symlink immediately if source exists
   - write `.guideants/mounts.json`
4. No Compose update is required because the host source is already mounted at `/app/HostMounts/{mountKey}`.

If the source is not present, link status becomes `PendingRestart` or `LinkError`.

## 18. Remove Flow

Removing a mapping is also admin-only.

1. Admin selects `Remove mapped folder`.
2. API marks the mount `PendingRemoval`.
3. API removes symlinks from all affected notebooks.
4. API updates each `.guideants/mounts.json`.
5. API marks links `Unlinked`.
6. API returns remove command.
7. Admin runs command.
8. Command rewrites compose override without the source mount.
9. Command restarts affected services only.
10. API startup or callback confirms `/app/HostMounts/{mountKey}` is gone.
11. API marks mount `Removed`.

Symlink behavior:

- symlinks should be removed before the compose restart
- removing symlinks never deletes host contents
- if the host command is never run, `/app/HostMounts/{mountKey}` remains mounted but unreachable from notebooks
- reconciliation removes stale symlinks for removed mappings
- if API cannot remove a symlink, keep the mapping in `Error` or `PendingRemoval` and show remediation to admins

## 19. Deletion Semantics

Mapped folder root:

- `Delete` should not recursively delete the host folder.
- UI should offer `Remove mapped folder` instead.
- API should block normal folder delete for a mount root.

Inside mapped folder:

- file create/update/delete operations are real operations against the host folder
- tool-generated files may be written there
- conversation outputs may modify host data

This distinction must be visible in the UI.

## 20. Security Considerations

- Admin-only creation/removal.
- Do not expose original host paths to non-admin users.
- Sanitize command display to avoid shell injection.
- Store host path as sensitive/admin configuration.
- Validate symlink targets during every reconciliation.
- Reject unregistered symlinks in script execution.
- Never follow arbitrary reparse points in notebook roots.
- Record lineage/audit events for mapping creation, linking, unlinking, and removal.

### 20.1 Networked source credentials (SMB/CIFS)

Networked sources usually require credentials, which raises the bar beyond "store host path as sensitive config":

- Never inline credentials in the displayed apply command, in API responses, or in `mounts.json`.
- Keep credentials out of the generated override on disk: reference a Docker secret or an environment variable (the `CredentialRef` field) rather than writing `username=...,password=...` into `driver_opts.o`.
- Lock down file permissions on `docker-compose.host-mounts.generated.yml` if any sensitive material lands there.
- Credentials are admin-only and never surfaced to non-admin users, the same as host paths.

## 21. Open Questions

- Should mounted folder contents be searchable/indexed by default, opt-in, or never indexed?
- Which services beyond `guideants-webapi-ui`, `guideants-ai`, and `plantuml` need host mounts?
- Should the helper scripts call the API to fetch mount plans, or should the UI generate fully self-contained commands?
- For networked (SMB) sources: what is the credential storage mechanism — Docker secret, env var reference, or admin-managed secret store? (Drives the `CredentialRef` design.)
- For networked (SMB) sources: subfolder convention — encode the subpath in the CIFS `device` or point the leaf symlink deeper?

Resolved:

- **Windows symlink behavior in Docker Desktop** — confirmed working via the §3.1 spike (create/read/write/persist all succeed; link surfaces host-side as an NTFS junction with a container-only target).
- **Host folder path collection** — first cut is localhost single-machine with the admin on the host, so admin-typed absolute paths are acceptable; a host folder picker is unnecessary for now. Revisit if/when remote or browser-only deployments are in scope.

## 22. Suggested Implementation Order

1. Add runtime config env vars and include generated compose override in `start_*`.
2. Add helper scripts that can rewrite the generated override and restart affected services.
3. Add data model and migrations for host folder mounts and links.
4. Add admin API endpoints for create/apply/remove/reconcile command generation.
5. Add `HostFolderMountService` and symlink materialization.
6. Add `.guideants/mounts.json` writer.
7. Update script execution path guard to allow registered mount symlinks only.
8. Update notebook creation flow to apply active project-scoped mappings.
9. Update notebook folder tree UI and context menus.
10. Add remove flow and stale symlink reconciliation.
11. Add tests for path validation, symlink creation/removal, project-scope notebook creation, and script-agent authorization.

Networked (SMB/CIFS) sources are a follow-on after local folders land: extend the override generator with the CIFS branch, add credential handling (§20.1), and reuse the existing symlink/guard/reconciliation layers unchanged.
