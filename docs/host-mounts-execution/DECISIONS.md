# Host Folder Notebook Mounts — Locked Decisions (single source of truth)

Last updated: 2026-06-17 · Status: **LOCKED for phases 1–12** (D4/D5 deferred to SMB follow-on).

Every subagent reads this file. If a value here is `UNDECIDED`, the orchestrator
**must** resolve it with the user (see [`00-orchestration.md`](./00-orchestration.md)
§1) before dispatching the phase that depends on it. Changing a value after a phase
ships requires a revert + re-dispatch of that phase — so get these right first.

This file resolves the plan's
[§21 Open Questions](../host-folder-notebook-mounts-plan.md) and pins the
cross-cutting invariants that the whole feature must hold.

---

## Part A — Open questions to resolve (from plan §21)

### D1. Indexing of mounted contents — **LOCKED: `never-by-default`**

Plan §21: *"Should mounted folder contents be searchable/indexed by default,
opt-in, or never indexed?"*

- Plan §14 "Recommended first implementation" already says **do not recursively
  index/sync mounted contents by default**. The proposed lock is **"not indexed by
  default; per-mount opt-in deferred."**
- **Blocks:** Phase 8 (notebook sync), Phase 6 (registry `writable`/index hints).
- **Resolve to:** `never-by-default` (recommended) | `opt-in` | `always`.

### D2. Affected-services set — **LOCKED: `guideants-webapi-ui;guideants-ai;plantuml`**

Plan §4/§21: *"Which services beyond `guideants-webapi-ui`, `guideants-ai`, and
`plantuml` need host mounts?"*

- The plan lists those three initially and flags **document server**, **document
  extraction services**, and **future file-processing workers** for review.
- **Action before lock:** audit which running services resolve content-file paths
  / follow notebook symlinks (anything that opens files under `/app/ContentFiles`
  and could traverse a mapped folder needs the mount, or it sees a dangling link —
  plan §3.1).
- This value is written into `GuideAntsRuntime__AffectedMountServices` (Phase 1),
  the helper-script restart list (Phase 2), and the override generator (Phase 4).
- **Blocks:** Phase 1, Phase 2, Phase 4.
- **Resolve to:** the exact `;`-separated service list.

### D3. Command source model — **LOCKED: `(a) api-plan`**

Plan §21: *"Should the helper scripts call the API to fetch mount plans, or should
the UI generate fully self-contained commands?"*

- Two coherent options:
  - **(a) API is the source of truth:** API produces the mount plan + command text;
    the helper script reads `.installer_state.env` + the `--mount-id` and (per plan
    §6.2) fetches/receives the plan, rewrites the override, restarts. (Recommended —
    keeps validation/authorization server-side.)
  - **(b) Self-contained command:** UI emits a fully self-contained command with all
    args; the script needs no API call to apply.
- Either way, **startup reconciliation is the source of truth** for symlinks and the
  post-restart callback is best-effort (plan §6 self-restart caveat).
- **Blocks:** Phase 2 (script behavior), Phase 4/5 (command-text + plan endpoint).
- **Resolve to:** `(a) api-plan` (recommended) | `(b) self-contained`.

### D4. SMB credential mechanism — **DEFERRED (SMB is a follow-on)** — record intended direction

Plan §21/§20.1: credential storage for networked sources — Docker secret, env var
reference, or admin-managed secret store (drives `CredentialRef`).

- SMB/CIFS is **out of the first cut** (plan §22 follow-on). The first-cut code must
  **not preclude** it: `SourceKind`, `CredentialRef`, and the override generator
  branch are designed in, but no credential storage ships in phases 1–12 unless the
  orchestrator explicitly pulls SMB forward.
- **Record the intended mechanism** here so the schema/field names are right the
  first time. Proposed: **Docker secret referenced by `CredentialRef`** (never
  inlined into `driver_opts.o`, API responses, or `mounts.json` — plan §20.1).
- **Blocks:** only an SMB follow-on phase; **does not block** phases 1–12 for local
  folders, provided the `CredentialRef` column + generator branch exist and are
  unused.
- **Resolve to (direction only):** `docker-secret` (recommended) | `env-ref` |
  `secret-store`.

### D5. SMB subfolder convention — **DEFERRED (SMB follow-on)** — record intended direction

Plan §4/§21: encode the subpath in the CIFS `device` (`//server/share/sub`) **or**
point the leaf symlink deeper.

- Same deferral as D4. Pick **one** convention before any SMB code so the symlink
  layer and `NetworkDevice` semantics agree.
- **Resolve to (direction only):** `device-subpath` | `deeper-symlink`.

---

## Part B — Frozen invariants (NOT open for subagent reinterpretation)

Decided by the plan; must hold in every phase:

- **No `External/` wrapper.** A mapped folder appears exactly as
  `{notebookRoot}/{leafFolderName}` (plan §1, §3). Any `External/` (or similar)
  wrapper folder is an automatic FAIL.
- **Two-layer architecture (plan §3).** Layer 1 = a **mount source** surfaced at the
  stable container path `/app/HostMounts/{mountKey}` (local bind first; networked
  volume not precluded). Layer 2 = an **API-managed symlink**
  `/app/ContentFiles/{projectSlug}/{notebookSlug}/{leafName}` →
  `/app/HostMounts/{mountKey}`. The symlink layer is identical regardless of source
  kind.
- **Mapped folders are read-write** (plan §2).
- **Admin-only** create/remove/repair/host-command-view (plan §2, §15, §20).
  Non-admins may *use* linked folders per normal notebook/project permissions.
- **Removing a mapping never deletes host folder contents** (plan §2, §14, §18,
  §19). Deleting *files inside* a mount are real host operations; deleting/moving the
  **mount root** is blocked/treated as metadata, never a recursive host delete.
- **Registered-links-only security model (plan §13, §20).** The script-execution
  guard follows a reparse point **only** when it matches a registered link in
  `mounts.json`, the resolved target is under the registered `containerSourcePath`,
  writability is satisfied for writes, and the path stays under the notebook root or
  an authorized mount source. **All unregistered symlinks/reparse points stay
  rejected.** This is the most security-sensitive invariant in the feature.
- **No recursive indexing by default (plan §14).** Both sync paths
  (`NotebookFileSyncService` and `SyncNotebookHandler`) must explicitly skip
  reparse-point descent for registered mounts; verify the .NET enumeration default
  rather than assuming it.
- **Compose change required only to add/remove a source; symlinks need no Compose
  change** (plan §2, §3, §17). A project-scoped mapping applies to future notebooks
  by creating another symlink against the already-mounted source.
- **Startup reconciliation is the source of truth (plan §6).** The
  `guideants-webapi-ui` self-restart drops the admin session briefly; the
  post-restart callback is best-effort/redundant, never a dependency.
- **No secrets on disk in clear text (plan §20.1).** SMB credentials are referenced
  via `CredentialRef`, never inlined into the generated override, API responses, or
  `mounts.json`; host paths are admin-only sensitive config.
- **No "fallback" logic (user rule).** A missing source, a missing/!matching
  registry entry, or a failed symlink surfaces as an explicit status
  (`PendingRestart`/`Missing source`/`LinkError`/`Error`) — never silently coerced
  into `Linked`/success, and a rejected guard check is never bypassed.
- **First cut = single-machine localhost Docker Desktop (Linux engine, WSL2).**
  Admin-typed absolute host paths are acceptable; no host folder picker (plan §21
  resolved). SMB/CIFS is a follow-on (plan §22).

---

## Part C — Decision ledger

| ID | Decision | Status | Resolved value | Date |
|----|----------|--------|----------------|------|
| D1 | Indexing default | LOCKED | `never-by-default` | 2026-06-17 |
| D2 | Affected-services set | LOCKED | `guideants-webapi-ui;guideants-ai;plantuml` | 2026-06-17 |
| D3 | Command source model | LOCKED | `api-plan` | 2026-06-17 |
| D4 | SMB credential mechanism (deferred) | DEFERRED | `docker-secret` (direction) | 2026-06-17 |
| D5 | SMB subfolder convention (deferred) | DEFERRED | `device-subpath` (direction) | 2026-06-17 |

Update this table as the orchestrator locks each value (and note any mid-flight
change in `STATUS.md`'s deviation log).
