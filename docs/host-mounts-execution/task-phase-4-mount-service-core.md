# Task — Phase 4: Mount service core (validation, keys, override plan, command text)

> Subagent brief. Execute top to bottom; return the Report-back contract verbatim.

## Mission

Add `IHostFolderMountService` and its core (non-filesystem) logic: leaf/mount-key
derivation, **validation rules**, **compose override plan** generation, and the
**displayed host command text**. Symlink materialization and the script-agent guard
are **out of scope** (Phases 6–7) — stub their seams cleanly.

> **Decisions required:** D2 (affected-services set drives the override plan), D3
> (command-source model drives what the command text contains).

## Read first

- `../host-folder-notebook-mounts-plan.md` §8 (mount key/leaf), §9 (validation), §10
  (API services — method list + reconcile signatures), §4 (override block shapes),
  §6/§11 (command text & response shapes), §20 (security — sanitize command, no host
  path leak).
- `./DECISIONS.md` → D2, D3, Part B invariants (no `External/`, no fallback).
- `./codeql-gate.md` (command-text construction + path handling are scanned in
  Phase 5; build them clean now).
- `src/server/GuideAntsApi/Services/StoragePathResolver.cs` (for later seams; do not
  create symlinks here).
- Service registration conventions in `GuideAntsApi` (how existing services are DI'd).

## Preconditions

- Phase 3 gate green. D2, D3 resolved.

## Guardrails (hard)

- **Validation (plan §9) is mandatory and total.** Reject leaf names that are empty,
  contain path separators, are `.`/`..`, contain null chars, collide with reserved
  names (`.guideants`, `Output`, `Runs`, `Resources`, `files`), or collide with an
  existing file/folder or active mapping **in any target notebook**. For
  project-scope, validate **every** existing notebook before creating the mapping.
- **Mount key** must be filesystem-safe and stable (plan §8) — e.g. `{mountId:N}` or
  `slug-shortid`. Leaf name defaults from the host folder's leaf, renamable.
- **Command text + override plan must be injection-safe** (plan §20): quote/escape so
  no host path can break the command; never inline SMB credentials; never include the
  raw host path in anything a non-admin can read.
- **No fallback:** validation failure returns a precise error; never "auto-fix" a bad
  leaf name into something that silently differs. Do not default a missing notebook or
  permission into a permissive path.
- Symlink creation, `mounts.json` writing, and guard changes are **stubbed** (define
  the interface methods; throw `NotImplementedException` or leave clearly-marked
  seams) — Phases 6–7 implement them. Do not half-implement them here.
- The override plan must branch on `SourceKind` (local bind now; the SMB/CIFS branch
  shape defined but inert) — must **not preclude** networked sources (plan §4).

## Tasks

1. Define `IHostFolderMountService` with the responsibilities + reconcile signatures
   in plan §10 (`ReconcileProjectMountsAsync`, `ReconcileNotebookMountsAsync`,
   `ReconcileAllMountsAsync`). Implement the **core** methods now:
   - create mount + link records (notebook/project scope) — fan-out link rows.
   - validate (plan §9) — a dedicated, unit-tested validator.
   - derive mount key + leaf (plan §8).
   - produce the compose override **plan** (data the helper script/override needs,
     branching on `SourceKind`).
   - produce the **command text** (plan §6/§11 shapes), sanitized.
2. Leave reconcile/symlink/registry/repair methods as **declared but stubbed** seams
   for Phases 6/9/11 with clear TODO markers referencing the phase.
3. Register the service in DI. Add a sanitizer/quoting helper for command text (or
   reuse an existing one) so Phase 5's CodeQL gate is clean.
4. Unit-test the validator and key/leaf derivation thoroughly.

## Files in scope

- `src/server/GuideAntsApi/Services/Components/HostFolderMountService.cs` (+
  `IHostFolderMountService.cs`) — follow existing service folder conventions.
- A validator + command-text helper (new files in scope).
- DI registration (the existing service-registration file).
- `src/server/GuideAntsApi.Tests/*` (validator + key/leaf + command-text tests).

**Out of scope:** endpoints (Phase 5), actual symlink/registry/guard work
(Phases 6–7), UI, migrations.

## Self-verification

```bash
cd src/server && dotnet build GuideAntsApi.sln
cd src/server && dotnet test GuideAntsApi.Tests/GuideAntsApi.Tests.csproj --filter HostFolderMount
```

Plus global gate (orchestration §4.1).

## Definition of Done

- [ ] `IHostFolderMountService` declared per §10; core methods implemented; symlink/
      registry/guard seams stubbed and clearly marked.
- [ ] Validator enforces **all** §9 rules incl. project-scope all-notebook check;
      unit-tested (positive + negative).
- [ ] Mount-key + leaf derivation correct (filesystem-safe, stable); unit-tested.
- [ ] Override plan branches on `SourceKind` (local now, SMB not precluded); command
      text matches §6/§11 shapes and is **sanitized** (no injection, no host-path/
      credential leak).
- [ ] No fallback/auto-fix; build + targeted tests green.

## Report-back contract (return exactly this)

```
PHASE 4 REPORT
- IHostFolderMountService methods implemented: <list>
- Methods stubbed for later phases (with TODO markers): <list>
- Validation rules covered (§9): <checklist y/n each>
- Mount key scheme: <scheme>; leaf derivation: <how>
- Command-text sanitization approach: <how>; SourceKind branch: local=<y> smb-shape=<y/inert>
- Tests added: <names/counts>
- Verification: build=<pass/fail> tests=<counts>
- Files touched: <list>
- Deviations / surprises: <list or "none">
```
