# Host Folder Notebook Mounts

This document is the operator and admin guide for mapping host folders into
notebook project trees. It complements the design plan in
[`host-folder-notebook-mounts-plan.md`](./host-folder-notebook-mounts-plan.md).

## Overview

A **host folder mount** surfaces a directory from the Docker host inside
containers at `/app/HostMounts/{mountKey}`, then links it into a notebook as
`{notebookRoot}/{leafName}` — a direct child of the notebook root. There is **no
`External/` wrapper folder**.

Two layers:

1. **Compose bind mount** — host path → `/app/HostMounts/{mountKey}` on affected
   services (`guideants-webapi-ui`, `guideants-ai`, `plantuml`).
2. **API-managed symlink** — `/app/ContentFiles/{project}/{notebook}/{leafName}`
   → `/app/HostMounts/{mountKey}` plus a per-notebook `.guideants/mounts.json`
   registry for the script-execution agent.

Mapped folders are **read-write**. Removing a mapping **never deletes host folder
contents**; it only removes symlinks and the compose volume entry.

## Admin runbook

### Map a host folder

1. Sign in as **Admin** and open the target notebook (or project root for
   project-scoped mappings).
2. Use **Map host folder here** from the folder tree context menu.
3. Enter the absolute host path, scope (`Notebook` or `Project`), and leaf name
   (folder name under the notebook root).
4. The API creates the mount in `PendingRestart` and returns an apply command.
5. Copy and run the displayed command on the **host** (PowerShell on Windows,
   shell on Linux/macOS). Example:

   ```powershell
   .\scripts\guideants-host-mount.ps1 apply `
     -MountId "<mount-guid>" `
     -HostPath "D:\Data\Shared"
   ```

   ```bash
   ./scripts/guideants-host-mount.sh apply \
     --mount-id "<mount-guid>" \
     --host-path "/home/me/Data/Shared"
   ```

6. The helper script rewrites `docker/docker-compose.host-mounts.generated.yml`
   and restarts **only** the affected services with `--no-deps`.
7. On startup, the API **reconciles** symlinks and writes `.guideants/mounts.json`.
   The folder tree should show the mount as **Linked**.

### Self-restart / session-drop caveat

`guideants-webapi-ui` is in the affected-services list. Applying or removing a
mount restarts that container, which **briefly drops the admin browser session**.
Plan for this when running apply/remove commands.

**Startup reconciliation is the source of truth.** The helper script may call
back to the API after restart, but that callback is best-effort — reconciliation
on container start always runs.

### Check mapped folders

Use **Check mapped folders** in the folder tree (admin only). The API re-runs
reconciliation and returns current link status per notebook.

### Remove a mapped folder

1. Select **Remove mapped folder** on the mount root in the folder tree.
2. The API marks the mount `PendingRemoval`, removes symlinks from all affected
   notebooks, updates each `mounts.json`, and returns a remove command.
3. Run the remove command on the host:

   ```powershell
   .\scripts\guideants-host-mount.ps1 remove -MountId "<mount-guid>"
   ```

   ```bash
   ./scripts/guideants-host-mount.sh remove --mount-id "<mount-guid>"
   ```

4. The helper script removes the compose volume block and restarts affected
   services. Reconciliation marks the mount `Removed` once
   `/app/HostMounts/{mountKey}` is gone.

Symlinks are removed **before** the compose restart. Host folder contents are
never deleted.

### Show apply / remove command

Admins can re-display the last apply or remove command from the folder tree menu
without changing mount state.

## Runtime configuration

Host folder mount apply/remove uses **compose context** from `.installer_state.env`, written by the `start_*` launchers:

| Key | Purpose |
|---|---|
| `COMPOSE_FILE` | Base compose file (e.g. `docker-compose.ghcr-cpu.yml`) |
| `HOST_MOUNT_OVERRIDE_FILE` | Generated override filename |
| `DOCKER_DIRECTORY` | Directory containing compose files (`docker`) |
| `START_COMMAND` | Which launcher was used (`start_windows.cmd`, etc.) |

The helper scripts also bind-mount host folders into **`guideants-webapi-ui`**, **`guideants-ai`**, and **`plantuml`**, and restart only those services after apply/remove. That service list is defined in the scripts themselves (not in compose env or installer state).

Helper scripts read `.installer_state.env` to reconstruct the compose command without hard-coding paths.

The generated override is included **only when the file exists**; baseline
compose behavior is unchanged when no mounts are configured.

## Helper scripts

| Script | Platform |
|---|---|
| `scripts/guideants-host-mount.ps1` | Windows (PowerShell) |
| `scripts/guideants-host-mount.sh` | Linux / macOS |

Both scripts:

1. Read `.installer_state.env` (requires a prior `start_*` run).
2. Fetch the mount plan from `GET /api/internal/host-folder-mounts/{mountId}/compose-override-plan` (api-plan model; `-HostPath` can be supplied when the API is unreachable during early setup).
3. Rewrite `docker/docker-compose.host-mounts.generated.yml` idempotently.
4. Restart affected services:

   ```bash
   docker compose \
     -f <compose-file> \
     -f docker-compose.host-mounts.generated.yml \
     up -d --no-deps guideants-webapi-ui guideants-ai plantuml
   ```

5. Optionally POST reconcile after restart (best-effort).

## Security model

### Admin-only surface

Create, remove, repair, host-command display, and host-path visibility are
**Admin-only** (`RequireAdmin` policy). Non-admins may use linked folders per
normal notebook/project permissions but cannot view host paths or commands.

### Registered-links-only (script execution)

The script-execution agent follows a reparse point (symlink/junction) **only**
when:

- the link matches an entry in `.guideants/mounts.json` for that notebook,
- the resolved target is under the registered `containerSourcePath`,
- writability is satisfied for write operations, and
- the path stays within authorized scope.

**All unregistered symlinks and reparse points are rejected.** This is the
primary security invariant — never bypass it with fallback logic.

### Sensitive data handling

- Host paths are admin-only configuration; they are not exposed to non-admin API
  consumers or client storage (`localStorage`).
- Apply/remove commands are built with structured quoting to prevent shell
  injection.
- Logs sanitize user-supplied host paths and leaf names
  (`LogValueSanitizer.Sanitize`).
- Mounted contents are **not recursively indexed** by default (both sync paths
  skip reparse-point descent).

### Layout invariant

Mapped folders appear as `{notebookRoot}/{leafName}`. Any `External/` (or
similar) wrapper is incorrect and must not be introduced.

## Troubleshooting

| UI / API state | Meaning | Remediation |
|---|---|---|
| **Pending restart** | Mapping exists; host path not yet mounted into containers | Run the apply command; wait for affected-service restart and startup reconciliation |
| **Linked** | Symlink exists and `/app/HostMounts/{mountKey}` is reachable | Normal operation |
| **Missing source** | Symlink may exist, but the container mount path is absent | Run apply (or fix compose override / host path); run **Check mapped folders** |
| **Link error** | API could not create the symlink (permissions, collision, invalid path) | Inspect admin error message; fix leaf name or notebook root permissions; reconcile |
| **Pending removal** | Symlinks removed; compose volume entry still present | Run the remove command; expect brief session drop on webapi restart |
| **Error** | Reconciliation or removal could not complete cleanly | Use admin detail view; reconcile after fixing underlying issue |

A missing source or unregistered link is always surfaced as an explicit failure
state — never coerced into success.

### Script agent rejects a mapped path

Confirm `.guideants/mounts.json` exists under the notebook `.guideants` folder,
lists the leaf with the correct `containerSourcePath`, and that `writable` matches
the intended access. Unregistered symlinks under the notebook root are always
rejected with `unregistered reparse point`.

## API endpoints (admin)

```
GET    /api/projects/{projectId}/host-folder-mounts
POST   /api/projects/{projectId}/host-folder-mounts
GET    /api/projects/{projectId}/host-folder-mounts/{mountId}
POST   /api/projects/{projectId}/host-folder-mounts/{mountId}/commands/apply
POST   /api/projects/{projectId}/host-folder-mounts/{mountId}/commands/remove
POST   /api/projects/{projectId}/host-folder-mounts/{mountId}/reconcile
DELETE /api/projects/{projectId}/host-folder-mounts/{mountId}
```

OpenAPI: see `guideants-swagger.json` at the repo root (regenerated from
`/swagger/v1/swagger.json`). All mount endpoints require Bearer JWT with Admin
role.

## Deferred: SMB/CIFS networked sources

SMB/CIFS is **not implemented** in the first cut (local bind mounts only). The
schema and override generator are designed so a follow-on can add networked
sources without changing the symlink, guard, or reconciliation layers.

### Planned follow-on scope

1. **Override generator CIFS branch** — mount `//server/share/sub` via Docker
   volume driver `local` + `type=cifs` into `/app/HostMounts/{mountKey}` on the
   same affected services.
2. **Credential handling (§20.1)** — `CredentialRef` references a Docker secret
   (intended direction: `docker-secret` per `DECISIONS.md` D4). Credentials are
   **never** inlined in API responses, `mounts.json`, displayed commands, or
   `driver_opts.o` on disk.
3. **Subfolder convention (D5)** — encode subpaths in the CIFS `device`
   (`//server/share/sub`; `device-subpath` direction).
4. **Reuse unchanged layers** — symlink materialization, `.guideants/mounts.json`,
   script-agent registered-links-only guard, notebook sync skip rules, folder
   tree UI, and remove/reconcile flows stay identical to local mounts.

### What already exists for SMB

- `SourceKind.Smb` enum value and nullable `NetworkDevice`, `NetworkOptions`,
  `CredentialRef` columns on `HostFolderMount`.
- Compose override plan structure with a CIFS branch stub (unused until follow-on).

No SMB credentials, secret storage, or UI ship until the dedicated follow-on
phase.

## Related documents

- Design plan: [`host-folder-notebook-mounts-plan.md`](./host-folder-notebook-mounts-plan.md)
- Locked decisions: [`host-mounts-execution/DECISIONS.md`](./host-mounts-execution/DECISIONS.md)
- Execution status: [`host-mounts-execution/STATUS.md`](./host-mounts-execution/STATUS.md)
