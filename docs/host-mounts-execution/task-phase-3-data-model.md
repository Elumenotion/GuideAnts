# Task — Phase 3: Data model & migrations

> Subagent brief. Execute top to bottom; return the Report-back contract verbatim.

## Mission

Add the persistence for host folder mounts: the **`HostFolderMount`** table and the
per-notebook **`HostFolderMountLink`** table, their enums, and the EF Core migration.
No services, endpoints, symlinks, or UI here.

## Read first

- `../host-folder-notebook-mounts-plan.md` §7 (Data Model — exact fields), §3
  (architecture), §8 (mount key / leaf semantics for column intent).
- `./DECISIONS.md` → D4/D5 (SMB deferred; the `SourceKind=Smb` enum value,
  `NetworkDevice`/`NetworkOptions`/`CredentialRef` columns must exist but stay
  unused for the first cut), Part B invariants.
- `src/server/GuideAntsApi.DataModel/Models/*.cs` (entity conventions; see
  `ExternalOAuthToken.cs`, `Notebook.cs`, `Project.cs`).
- `src/server/GuideAntsApi.DataModel/ApplicationDbContext.cs`
- `src/server/GuideAntsApi.DataModel/EF_COMMANDS.md` (exact migration commands)

## Preconditions

- Phase 2 gate green. `dotnet ef --version` works.

## Guardrails (hard)

- Implement **all** columns from plan §7 for both tables, with the documented enums:
  `SourceKind { LocalPath, Smb }`, `Scope { Notebook, Project }`,
  `HostFolderMount.Status { PendingRestart, Active, PendingRemoval, Removed, Error }`,
  `HostFolderMountLink.Status { PendingRestart, PendingLink, Linked, Unlinked,
  LinkError, UnlinkError }`.
- **Source-field semantics by kind (plan §7):**
  - `LocalPath`: `SourceSpec` = absolute host path; `NetworkDevice`/`NetworkOptions`/
    `CredentialRef` null.
  - `Smb`: `SourceSpec` = display/UNC; `NetworkDevice` = CIFS device; `NetworkOptions`
    = non-secret options; `CredentialRef` = secret-store reference (**never** raw
    creds).
- `SourceSpec` (host path) and any credential reference are **sensitive** — mark them
  so Phase 5 knows not to project them to non-admins. Do not log them at the model
  layer.
- FKs: `HostFolderMount.ProjectId` → `Projects`; `NotebookId` nullable → `Notebooks`;
  `CreatedByUserId` → `Users`; `HostFolderMountLink.HostFolderMountId` → cascade,
  `NotebookId` → `Notebooks`. Choose `OnDelete` so deleting a project/notebook
  cleans up mount/link rows but never implies host-content deletion (that is a
  runtime concern, not a DB cascade).
- **No host content is ever referenced for deletion** by any cascade.
- Migration must be safe with the repo's migration workflow (see `EF_COMMANDS.md`).

## Tasks

1. Add `HostFolderMount` entity (plan §7) + `SourceKind`, `Scope`, `Status` enums.
2. Add `HostFolderMountLink` entity (plan §7) + its `Status` enum.
3. Wire `ApplicationDbContext`: DbSets, FKs/indexes. Add indexes that Phase 4/5/9
   queries need (e.g. `(ProjectId, Status)`, `(HostFolderMountId, NotebookId)`,
   and a uniqueness constraint preventing two **active** links to the same leaf in
   the same notebook — coordinate the exact unique key with plan §9 collision rules).
4. Add the migration via `EF_COMMANDS.md` exact command (e.g.
   `AddHostFolderMounts`):
   ```powershell
   # from src/server
   dotnet ef migrations add AddHostFolderMounts --project GuideAntsApi.DataModel/GuideAntsApi.DataModel.csproj --startup-project GuideAntsApi/GuideAntsApi.csproj
   ```
5. Verify the auto-migrate path and that `DataModel.Tests` compiles against the new
   model.

## Files in scope

- `src/server/GuideAntsApi.DataModel/Models/HostFolderMount.cs` (+ enum files)
- `src/server/GuideAntsApi.DataModel/Models/HostFolderMountLink.cs`
- `src/server/GuideAntsApi.DataModel/ApplicationDbContext.cs`
- `src/server/GuideAntsApi.DataModel/Migrations/*` (generated)
- `src/server/GuideAntsApi.DataModel.Tests/*` (only if model change breaks them)

**Out of scope:** services, endpoints, symlinks, UI, override generation.

## Self-verification

```bash
cd src/server && dotnet build GuideAntsApi.sln
cd src/server && dotnet ef migrations list --project GuideAntsApi.DataModel/GuideAntsApi.DataModel.csproj --startup-project GuideAntsApi/GuideAntsApi.csproj
# fresh-DB apply on a scratch DB:
cd src/server && dotnet ef database update --project GuideAntsApi.DataModel/GuideAntsApi.DataModel.csproj --startup-project GuideAntsApi/GuideAntsApi.csproj
cd src/server && dotnet test GuideAntsApi.DataModel.Tests/GuideAntsApi.DataModel.Tests.csproj
```

Plus global gate (orchestration §4.1).

## Definition of Done

- [ ] Both entities + all three enum sets match plan §7; source-field semantics by
      kind honored; sensitive columns flagged.
- [ ] Migration at head; fresh-DB apply succeeds; designer snapshot updated.
- [ ] FKs/indexes/uniqueness support the §9 collision rules; no cascade implies
      host-content deletion.
- [ ] `DataModel.Tests` green; solution builds.

## Report-back contract (return exactly this)

```
PHASE 3 REPORT
- HostFolderMount columns: <list w/ types/nullability>
- HostFolderMountLink columns: <list w/ types/nullability>
- Enums: SourceKind/Scope/Mount.Status/Link.Status present: <yes>
- Sensitive columns flagged (SourceSpec/CredentialRef): <how>
- Indexes/unique constraints added: <list>
- Migration name(s): <names>; fresh-DB update: <pass/fail>
- Verification: build=<pass/fail> datamodel-tests=<counts>
- Files touched: <list>
- Deviations / surprises: <list or "none">
```
