# Task — Phase 6: Symlink materialization + `.guideants/mounts.json` registry

> Subagent brief. Execute top to bottom; return the Report-back contract verbatim.
> **Security-sensitive phase — CodeQL gate required.**

## Mission

Implement the filesystem half of the service: create/remove the notebook symlinks
that surface `/app/HostMounts/{mountKey}` as `{notebookRoot}/{leafName}`, and write
the per-notebook `.guideants/mounts.json` registry that Phase 7's guard will read.

## Read first

- `../host-folder-notebook-mounts-plan.md` §12 (symlink materialization steps), §13
  (registry file + schema), §3.1 (verified symlink behavior + the NTFS-junction
  caveat), §20 (security).
- `./DECISIONS.md` → D1 (indexing default — affects whether you add index hints to
  the registry), Part B invariants (no `External/`, no host-content deletion, no
  fallback).
- `./codeql-gate.md` (`cs/path-injection` focus).
- `src/server/GuideAntsApi/Services/StoragePathResolver.cs` (resolve notebook root).
- Phase 4 service seams you are now filling.

## Preconditions

- Phase 5 gate green. §3.1 spike still valid (re-confirm if env changed).

## Guardrails (hard)

- Follow plan §12 **exactly**: resolve notebook root via `IStoragePathResolver`;
  ensure `.guideants/notebook.json` exists; resolve link path `{notebookRoot}/
  {leafName}`; **verify the link path is under the notebook root**; **verify the
  source exists** at `/app/HostMounts/{mountKey}`; create a **directory symlink**;
  set link `Linked`; write `mounts.json`.
- On any failure → set link `LinkError` and **surface** the error. **Never** swallow
  it or mark `Linked` optimistically (no fallback).
- **Never delete host source content.** Removing a link removes only the symlink.
- `mounts.json` schema = plan §13 (`schemaVersion`, `mounts[]` with `mountId`,
  `leafName`, `linkRelativePath`, `containerSourcePath`, `writable`). **Never** write
  the host path or any credential into it.
- Path construction must be injection-safe (CodeQL `cs/path-injection`): build the
  link path from the resolved root + validated leaf; re-verify canonical containment
  after construction.
- Honor the NTFS-junction caveat (plan §3.1): the link is a container-namespace
  artifact; do not assume host-side resolvability.

## Tasks

1. Implement symlink creation in `HostFolderMountService` per §12, including the
   under-root and source-exists checks.
2. Implement symlink removal (used by Phases 9/11) that never touches host content.
3. Implement the `mounts.json` writer/updater per §13 (atomic write; one entry per
   active link in the notebook).
4. Update link `Status` transitions correctly (`PendingLink` → `Linked` /
   `LinkError`; removal → `Unlinked` / `UnlinkError`).
5. Add unit/integration tests: successful create (link under root, source present),
   failure when source absent (→ `LinkError`, surfaced), removal leaves host content
   intact, registry content matches schema and omits host path/creds.

## Files in scope

- `src/server/GuideAntsApi/Services/Components/HostFolderMountService.cs` (fill the
  Phase-4 symlink/registry seams)
- A `mounts.json` model/serializer (new)
- `src/server/GuideAntsApi.Tests/*` and/or `GuideAntsApi.IntegrationTests/*`

**Out of scope:** the script-agent guard (Phase 7), sync changes (Phase 8),
new-notebook flow (Phase 9), UI.

## Self-verification

```bash
cd src/server && dotnet build GuideAntsApi.sln
cd src/server && dotnet test GuideAntsApi.Tests/GuideAntsApi.Tests.csproj --filter Symlink
# integration (if used):
cd src/server && dotnet test GuideAntsApi.IntegrationTests/GuideAntsApi.IntegrationTests.csproj --filter Mount
```

Plus global gate (orchestration §4.1) **and** CodeQL gate (`codeql-gate.md` §5
Phase-6 row).

## Definition of Done

- [ ] Symlink create follows §12 (under-root + source-exists verified); failure →
      `LinkError` **surfaced**, never masked.
- [ ] Symlink removal never deletes host content.
- [ ] `mounts.json` matches §13 schema; **no** host path/credential in it.
- [ ] Link status transitions correct; tests cover success + absent-source +
      removal-preserves-content.
- [ ] **CodeQL diff clean** (`cs/path-injection`).

## Report-back contract (return exactly this)

```
PHASE 6 REPORT
- Symlink create checks (under-root, source-exists): <both yes>
- Failure handling: absent source -> <LinkError surfaced? yes>
- Removal preserves host content (test): <yes>
- mounts.json schema matches §13: <yes>; host-path/creds excluded: <yes>
- Status transitions implemented: <list>
- CODEQL: build-mode=none=<yes> new-vs-baseline=<count -> rules or none> fixed-in-code=<yes/n-a>
- Verification: build=<...> tests=<counts>
- Files touched: <list>
- Deviations / surprises: <list or "none">
```
