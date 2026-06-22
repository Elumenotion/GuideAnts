# Host Folder Notebook Mounts — Execution & Orchestration Guide

Last updated: 2026-06-17

This is the **conductor** document for executing
[`../host-folder-notebook-mounts-plan.md`](../host-folder-notebook-mounts-plan.md).
It is written for the **top-level (orchestrating) agent**. It defines how the plan
is split into **subagent task briefs**, the **dependency order**, the
**verification gates** the orchestrator runs after each phase (including the
**docker build/compose**, **client/server test**, **CodeQL**, and
**documentation** gates the feature requires), and the **deviation/failure
protocol** that keeps the work on-rails so it lands correctly the first time.

> **Audience split**
>
> - **You (orchestrator)** read this file + [`DECISIONS.md`](./DECISIONS.md) +
>   [`STATUS.md`](./STATUS.md) + the two gate references
>   ([`docker-gate.md`](./docker-gate.md), [`codeql-gate.md`](./codeql-gate.md)).
>   You dispatch subagents, run gates, and update `STATUS.md`.
> - **Subagents** read only their own `task-phase-N-*.md` brief, the plan sections
>   it cites, `DECISIONS.md`, and — when the brief says so — the relevant gate doc.
>   A subagent should **not** need any other context.

---

## 0. How to use this folder

| File | Owner | Purpose |
|------|-------|---------|
| `00-orchestration.md` (this) | Orchestrator | Dispatch order, gates, deviation protocol. |
| `DECISIONS.md` | Orchestrator (fill **before** any dispatch) | Resolves the plan §21 open questions + locks cross-cutting invariants. Single source of truth. |
| `STATUS.md` | Orchestrator (update after every gate) | Living ledger: phase state, docker/test/CodeQL gate results, deviations, re-dispatches. |
| `docker-gate.md` | Orchestrator + docker-touching subagents | Compose-override validation, image build, affected-services restart gate. |
| `codeql-gate.md` | Orchestrator + security-sensitive subagents | Local (no-GitHub) CodeQL gate: baseline, commands, diff, rules to watch for this feature. |
| `task-phase-1-runtime-config-compose.md` | Subagent | Phase 1 brief (runtime config + compose-override include in `start_*`). |
| `task-phase-2-host-helper-scripts.md` | Subagent | Phase 2 brief (host helper scripts). |
| `task-phase-3-data-model.md` | Subagent | Phase 3 brief (data model + migrations). |
| `task-phase-4-mount-service-core.md` | Subagent | Phase 4 brief (validation, mount key/leaf, override plan, command text). |
| `task-phase-5-api-endpoints.md` | Subagent | Phase 5 brief (admin endpoints). |
| `task-phase-6-symlink-registry.md` | Subagent | Phase 6 brief (symlink materialization + `mounts.json`). |
| `task-phase-7-script-agent-guard.md` | Subagent | Phase 7 brief (script-agent path-guard rework — security-critical). |
| `task-phase-8-notebook-sync.md` | Subagent | Phase 8 brief (notebook file sync reparse handling). |
| `task-phase-9-new-notebook-reconcile.md` | Subagent | Phase 9 brief (project-scope new-notebook flow + reconciliation triggers). |
| `task-phase-10-folder-tree-ui.md` | Subagent | Phase 10 brief (folder tree UI + deletion semantics). |
| `task-phase-11-remove-flow.md` | Subagent | Phase 11 brief (remove flow + stale-symlink reconciliation). |
| `task-phase-12-tests-openapi-docs.md` | Subagent | Phase 12 brief (integration tests, OpenAPI, documentation, final acceptance). |

Each task brief follows the **same template** (Mission → Read first →
Preconditions → Guardrails → Tasks → Files in/out of scope → Self-verification →
Definition of Done → Report-back contract). The Report-back contract is what you
diff against the brief to **detect deviations**.

---

## 1. Pre-flight (do this once, before any subagent is dispatched)

Landing "the first time" depends on locking cross-cutting choices up front. **Do
not dispatch Phase 1 until all of the following are true.**

- [ ] **Resolve the plan §21 open questions in [`DECISIONS.md`](./DECISIONS.md)**
      (indexing default, affected-services set, command-source model, SMB
      credential mechanism, SMB subfolder convention). Any value left `UNDECIDED`
      that a phase depends on **blocks** that phase — see the per-decision
      "blocks phase" note in `DECISIONS.md`.
- [ ] **Confirm the §3.1 symlink spike result still holds** on the target stack
      (Docker Desktop, Linux engine, WSL2/Ubuntu backend): symlink under
      `/app/ContentFiles/...` → `/app/HostMounts/{mountKey}` creates, reads,
      writes, and persists across a second container with the same bind mount.
      If the environment changed, re-run the spike before Phase 6/7.
- [ ] **Capture a clean baseline** and record it in `STATUS.md` as the "before"
      line. Every later gate compares against this:
  - Server: from `src/server` run `dotnet build GuideAntsApi.sln` and
    `dotnet test GuideAntsApi.sln` (unit + ScriptExecutionAgent + DataModel;
    integration tests per CI matrix).
  - Client: from `src/client` run `npm run build` and `npm test -- --run`.
  - Docker: per [`docker-gate.md`](./docker-gate.md) §"Baseline" — confirm the
    current selected compose file resolves (`docker compose -f <file> config`)
    with **no** generated override present yet.
- [ ] **Capture the CodeQL baseline** per [`codeql-gate.md`](./codeql-gate.md)
      §"Baseline" (local, **no GitHub fetch/parity** — this branch is not on
      GitHub). Save SARIFs to `.codeql/baseline/` and record per-language/per-rule
      counts in `STATUS.md`. Security-sensitive gates diff against this.
- [ ] Confirm `dotnet ef` is installed (`dotnet ef --version`) — Phase 3 needs it.
- [ ] Confirm a clean working tree (`git status`) and that you are on the feature
      branch.

If a §21 decision is unresolved and the user has not decided it, **stop and ask**
(use a structured question). Do not pick for them where the plan flags a real
trade-off (credential mechanism, indexing policy, subfolder convention).

---

## 2. Dependency graph (dispatch order)

```
 Phase 1  Runtime config + compose-override include in start_*   [docker]
    │
    ▼
 Phase 2  Host helper scripts (ps1/sh) — rewrite override, restart  [docker]
    │
    ▼
 Phase 3  Data model + migrations (HostFolderMount, *Link)
    │
    ▼
 Phase 4  Mount service core (validation, mount key/leaf, override plan, command text)
    │
    ▼
 Phase 5  Admin API endpoints (create/apply/remove/reconcile/list/delete)   [CodeQL]
    │
    ▼
 Phase 6  Symlink materialization + .guideants/mounts.json registry   [CodeQL]
    │
    ▼
 Phase 7  Script-agent path-guard rework (registered crossings only)  [CodeQL — CRITICAL]
    │
    ├───────────────┐
    ▼               ▼
 Phase 8         Phase 9
 Notebook        New-notebook project-scope flow
 file sync       + reconciliation triggers
 reparse         (needs service + symlink + guard)
 handling
    └───────┬───────┘
            ▼
 Phase 10  Folder tree UI + deletion semantics   [client]
            │
            ▼
 Phase 11  Remove flow + stale-symlink reconciliation
            │
            ▼
 Phase 12  Integration tests + OpenAPI + documentation + final acceptance   [docs]
```

**Rules:**

- Phases run **strictly in order**. The only allowed parallelism is **Phase 8 and
  Phase 9** *after* Phase 7's guard lands — and even then prefer sequential unless
  schedule pressure demands it, because both depend on the registered-mount
  contract from Phases 6–7.
- **A phase is not "done" until its gate (section 4) passes.** A downstream phase
  must **never** start on top of a failed gate. This is the core mechanism that
  prevents compounding failures.
- One subagent per phase. Do **not** hand a subagent more than its brief.
- **Phase 7 is the single most security-sensitive change** (it widens the
  script-execution authorization surface). Treat the registry read, the additional
  authorized root, and the "only registered links followed" invariant as **one
  reviewed unit**, and run the full CodeQL gate on it.

---

## 3. Dispatch protocol (per phase)

For each phase, in order:

1. **Confirm preconditions** in the brief (prior gate green; required DECISIONS
   resolved). Update `STATUS.md` → phase `IN_PROGRESS`.
2. **Dispatch one subagent** with a prompt that is exactly: *"Read and execute
   `docs/host-mounts-execution/task-phase-N-*.md` end to end. Obey its guardrails
   and Definition of Done. Return the Report-back contract verbatim."* Give it no
   other instructions — the brief is the contract.
3. **Receive the Report-back.** Do not trust it blind — it is a claim.
4. **Run the gate** (section 4 + the phase's own gate). The gate is **your**
   independent verification with your own tools, not the subagent's word.
5. **Decide**: PASS → mark phase `DONE` in `STATUS.md`, proceed. FAIL/DEVIATION →
   follow section 5.

> You verify; the subagent implements. Never let "the subagent said it's done"
> substitute for a green gate.

---

## 4. Verification gates

### 4.1 Global invariants — checked at **every** gate

Run/inspect these after every phase. Any failure blocks the next phase.

- [ ] **Server build green**: `cd src/server && dotnet build GuideAntsApi.sln`
      (0 errors; warnings not worse than baseline).
- [ ] **Server tests green**: `cd src/server && dotnet test GuideAntsApi.sln` — no
      new failures vs the Pre-flight baseline. (DataModel + API unit +
      ScriptExecutionAgent always; integration tests when the phase touches
      endpoints/services/migrations.)
- [ ] **Client build green**: `cd src/client && npm run build` (tsc + vite, 0
      errors).
- [ ] **Client tests green**: `cd src/client && npm test -- --run`.
- [ ] **Docker gate green** when the phase touched anything under `docker/`,
      `start_*`, `scripts/guideants-host-mount.*`, or the override generator:
      run [`docker-gate.md`](./docker-gate.md). At minimum
      `docker compose -f <selectedComposeFile> -f docker-compose.host-mounts.generated.yml config`
      must resolve with a representative generated override, and an affected-only
      restart must be `--no-deps`-scoped.
- [ ] **No "fallback" anti-patterns** (per the user's hard rule — *fallback is a
      bug generator*). Grep the diff for newly added `fallback`, silent
      `catch {}`/swallowed errors, default-on-missing-mount logic, "assume
      writable", or "skip the guard if registry missing" shortcuts. A missing
      source, a missing registry entry, or a failed symlink must surface as an
      explicit status/error, never be masked into success.
- [ ] **No host content destruction path**: nothing in the diff can delete host
      source contents when a mapping/symlink is removed (plan §2, §14, §18, §19).
      Removing a mapping removes links only.
- [ ] **Security invariants intact** (plan §13, §20): unregistered
      symlinks/reparse points remain rejected by the script agent; host paths and
      SMB credentials are never exposed to non-admins or written into
      `mounts.json`/API responses/displayed commands.
- [ ] **Scope discipline**: the subagent only touched files its brief authorized.
      Diff the file list against the brief's "Files in scope". Unexpected files =
      deviation.
- [ ] **CodeQL diff clean** vs the pre-flight baseline — run the local gate
      ([`codeql-gate.md`](./codeql-gate.md)) at minimum after every
      **security-sensitive** phase (**5, 6, 7**, and Phase 12 final acceptance;
      plus any phase that adds SMB/credential handling). C# **must** use
      `--build-mode=none`; **no GitHub parity**; **no alert suppression** — fix the
      code.
- [ ] **Matches `DECISIONS.md`** (affected-services set, command-source model,
      indexing default, no-`External/`-wrapper layout). A subagent that built an
      `External/` wrapper or recursively indexed mounts by default is an automatic
      FAIL.

### 4.2 Per-phase gate criteria

Each is **in addition** to 4.1. Commands assume `src/server`, `src/client`, or
repo root cwd as noted.

**Phase 1 — Runtime config + compose-override include**

- [ ] `GuideAntsRuntime__*` env vars (plan §5) are present in the active compose
      file(s) with correct values; the API can read them.
- [ ] All three `start_*` scripts (`start_windows.cmd`, `start_linux.sh`,
      `start_macos.sh`) include `docker-compose.host-mounts.generated.yml`
      **only if the file exists**, on both the up path and any restart path, and
      persist enough state in `.installer_state.env` to reconstruct the compose
      command. No behavior change when the override is absent.
- [ ] **Docker gate**: with a hand-written sample
      `docker/docker-compose.host-mounts.generated.yml`,
      `docker compose -f <file> -f docker-compose.host-mounts.generated.yml config`
      resolves; with the file absent, plain `up` is unchanged. Start with no
      override present → identical behavior to baseline.

**Phase 2 — Host helper scripts**

- [ ] `scripts/guideants-host-mount.ps1` and `.sh` exist with `apply`/`remove`
      verbs (plan §6) and read `.installer_state.env` + arguments.
- [ ] Override rewrite is **idempotent**: applying the same mount twice yields a
      byte-identical override; removing a mount removes exactly its block.
- [ ] Restart is **affected-services only** (`up -d --no-deps <services>` per the
      `AffectedMountServices` set), not a full `up`.
- [ ] **Self-restart caveat handled** (plan §6): the script does not depend on a
      successful post-restart API callback; startup reconciliation is the source of
      truth and the callback is best-effort.
- [ ] **Docker gate**: generated override validates via `compose config` after both
      `apply` and `remove`; no credentials inlined for the SMB branch (per
      DECISIONS / plan §20.1).

**Phase 3 — Data model & migrations**

- [ ] `HostFolderMount` and `HostFolderMountLink` entities + enums
      (`SourceKind`, `Scope`, both `Status` sets) match plan §7; source-field
      semantics by kind are honored (LocalPath vs Smb columns).
- [ ] EF migration present at head; fresh-DB apply succeeds
      (`dotnet ef database update` on a scratch DB); `DataModel.Tests` green.
- [ ] No host path / credential stored in a way that leaks to non-admin queries
      (sensitive columns identified for Phase 5 projection rules).

**Phase 4 — Mount service core**

- [ ] `IHostFolderMountService` exists with create/plan/command-text/validation/
      reconcile method signatures (plan §10); symlink + guard work is **stubbed/out
      of scope** here (Phases 6–7).
- [ ] **Validation rules (plan §9) unit-tested**: empty/separator/`.`/`..`/null/
      reserved-name/collision rejections; project-scope validates **every** existing
      notebook before creating the mapping.
- [ ] Mount key + leaf derivation (plan §8) tested (filesystem-safe, stable).
- [ ] Override plan + displayed command text (plan §6, §11) generated correctly and
      **sanitized** (no shell injection; no credential inlining).

**Phase 5 — Admin API endpoints**

- [ ] All endpoints from plan §11 exist, are **admin-only**
      (`RequireAdmin`; non-admin → 403), and return the documented create/remove
      response shapes.
- [ ] Host paths and any `CredentialRef`/credentials are **never** returned to
      non-admins; displayed command is sanitized.
- [ ] Integration tests cover create (notebook + project scope), apply/remove
      command generation, reconcile, and authorization (admin vs non-admin).
- [ ] **CodeQL diff clean**: no new `cs/log-forging` (host path/leaf logging),
      `cs/path-injection`, or command-injection findings from the new surface.

**Phase 6 — Symlink materialization + registry**

- [ ] Symlink creation follows plan §12 (resolve root via `IStoragePathResolver`,
      verify under notebook root, verify source exists, create dir symlink, set
      `Linked`); failure → `LinkError` surfaced, **never** silently skipped.
- [ ] `.guideants/mounts.json` written per plan §13 schema; `writable` reflects the
      mount; **no host path or credential** written into it.
- [ ] Removing a link removes the symlink and updates the registry **without**
      touching host content.
- [ ] **CodeQL diff clean**: `cs/path-injection` on link/registry file work.

**Phase 7 — Script-agent path guard (security-critical)**

- [ ] `PathGuard` (`ScriptExecutionAgent/Program.cs`,
      `TryResolveAndAuthorizePath`/`HasReparsePointBetween`) now (a) treats
      `/app/HostMounts` (or each registered `containerSourcePath`) as an
      **additional authorized root**, and (b) allows a reparse-point crossing
      **only** when it matches a registered link in `mounts.json`, resolves under
      the registered source, satisfies writability for writes, and stays under the
      notebook root or an authorized mount source (plan §13).
- [ ] **Every unregistered symlink/reparse point remains rejected** — proven by a
      negative test (a hand-planted unregistered link under a notebook root is
      refused for both `/execute` and `/files`).
- [ ] `ScriptExecutionAgent.Tests` green incl. new positive/negative cases.
- [ ] **CodeQL diff clean** (focus `cs/path-injection`): the widened root must not
      introduce a traversal escape; fix in-code, no suppression.

**Phase 8 — Notebook file sync**

- [ ] **Both** sync paths handled (plan §14): `NotebookFileSyncService`
      (resolver-aware) and `SyncNotebookHandler` (manual paths). They behave
      consistently.
- [ ] Reparse-point traversal verified: enumeration **does not** descend into
      registered mount junctions by default (skip `FileAttributes.ReparsePoint`
      for registered roots, or all reparse points); confirmed by a test against a
      planted junction so the mounted source is **not** SHA-256-indexed.
- [ ] Mounted folder appears as a first-class tree entry without recursive index;
      delete/move rules from plan §14 honored (no host-content deletion).

**Phase 9 — New-notebook project-scope flow + reconciliation**

- [ ] Notebook creation applies active **project-scoped** mounts (plan §17): creates
      `HostFolderMountLink`, creates symlink when source present, writes
      `mounts.json`; no Compose change needed.
- [ ] Reconciliation runs on **API startup**, after **helper callback**, after
      **notebook creation**, and on **"Check mappings"** (plan §10).
- [ ] If source absent, link → `PendingRestart`/`LinkError` (explicit, not masked).
- [ ] Integration test proves a new notebook in a project with an active mount gets
      a link/symlink.

**Phase 10 — Folder tree UI + deletion semantics**

- [ ] Admin context-menu actions (plan §15): map / remove / show apply / show
      remove / check; available on the notebook root/file section only.
- [ ] Display states render (`Pending restart`, `Linked`, `Missing source`,
      `Link error`, `Pending removal`).
- [ ] Non-admins can use linked mapped folders per normal permissions but cannot
      create/remove/repair or view host commands.
- [ ] Deletion semantics visible (plan §19): mount-root `Delete` is blocked/replaced
      by `Remove mapped folder`; files inside the mount are real host operations.
- [ ] **UI-convention gate**: no new icon library / bespoke modal markup; reuse
      existing dialog/button/toast components. `npm run build` + `npm test -- --run`
      green; `npm run find-orphans` not worse than baseline.

**Phase 11 — Remove flow + stale reconciliation**

- [ ] Remove flow (plan §18) is admin-only: mark `PendingRemoval` → remove symlinks
      from all affected notebooks → update each `mounts.json` → mark `Unlinked` →
      return remove command; symlinks removed **before** the compose restart.
- [ ] Reconciliation removes **stale** symlinks for removed mappings and, after the
      host command confirms `/app/HostMounts/{mountKey}` is gone, marks the mount
      `Removed`.
- [ ] Failure to remove a symlink keeps the mapping in `Error`/`PendingRemoval` with
      admin-facing remediation — **never** a silent success.
- [ ] Normal folder delete on a mount root is blocked server-side (plan §19), not
      just hidden in the UI.

**Phase 12 — Integration tests, OpenAPI, documentation, final acceptance**

- [ ] Full test matrix green: path validation, symlink create/remove,
      project-scope new-notebook, script-agent authorization (positive + negative),
      remove + stale reconciliation, sync reparse handling.
- [ ] Swagger regenerated; the new admin endpoints carry the correct security
      scheme; `node scripts/find-unused-api-endpoints.mjs` shows no surprises.
- [ ] **Documentation** updated under `docs/`: admin runbook (map/remove/check host
      folders, the self-restart caveat), the runtime-config env vars, the helper
      scripts, the security model (registered-links-only + credential handling), and
      a "networked SMB is a follow-on" note. The source plan §22 SMB follow-on is
      explicitly carried into a "Deferred" section.
- [ ] **Docker gate** full pass (compose validates with and without override;
      affected-services restart scoped) and **final CodeQL diff clean**.

### 4.3 Docker build/compose gate (summary)

Defined in [`docker-gate.md`](./docker-gate.md). Run it after Phases **1, 2, 11,
12** and any phase that edits `docker/`, `start_*`, or the override generator.
Summary: the base compose file plus a representative generated override must
`docker compose ... config` cleanly; affected-services restart must be
`--no-deps`-scoped to the `AffectedMountServices` set; with no override present,
behavior is byte-for-byte the baseline; the SMB branch must not inline credentials.

### 4.4 CodeQL security gate (summary)

Defined in [`codeql-gate.md`](./codeql-gate.md). Local **baseline-vs-current**, not
GitHub parity. Run after security-sensitive phases (**5, 6, 7**, SMB work, final
acceptance). **Pass = zero NEW findings** vs `.codeql/baseline/`. Watch
`cs/path-injection` (symlink/registry/guard/file APIs), command injection in the
displayed apply/remove command, `cs/log-forging` (host path/leaf logging), and
clear-text storage of SMB credentials. C# `--build-mode=none` only;
code-scanning suites only; **no suppression — fix the code**.

---

## 5. Deviation & failure protocol

When a gate fails, **stop the line**. Do not start the next phase.

1. **Classify** the failure in `STATUS.md`:
   - **Build/test red** → mechanical; re-dispatch the same subagent with the exact
     error output and the failing command.
   - **Docker gate red** → compose does not resolve, restart not scoped, or override
     not idempotent; re-dispatch with the `compose config` output.
   - **Missing DoD item** → the subagent under-delivered; re-dispatch with the
     specific unchecked items quoted.
   - **Scope creep** (touched out-of-scope files) → review those edits; revert the
     unauthorized ones (`git checkout -- <file>`) unless genuinely required, in which
     case update the brief + `DECISIONS.md` first so the change is recorded.
   - **Decision drift** (built against the wrong DECISIONS value, e.g. wrong
     affected-services set, an `External/` wrapper, recursive indexing on by
     default) → revert the phase's changes and re-dispatch with DECISIONS re-quoted.
   - **Fallback/masking introduced** → hard reject; require removal. Per the user
     rule, fallback logic that hides a missing source / failed link / unregistered
     symlink behind a success path is never acceptable.
   - **Security regression** → any new CodeQL finding, any unregistered link that the
     guard now follows, or any host-path/credential leak → hard reject.
2. **Re-dispatch** the *same* phase brief with a focused correction note appended
   ("Gate failed on X; fix only X; do not touch anything else"). Re-run the **full**
   gate afterward (not just the failed check) to catch regressions.
3. **Cap retries at 2.** If a third attempt is needed, escalate to the user with the
   gate output and your hypothesis — the brief or a DECISIONS value may be wrong.
4. **Record everything** in `STATUS.md`: attempt #, what failed, what changed, gate
   re-run result.

**Never** advance a phase to fix a problem a later phase owns ("I'll harden the
guard in Phase 12") — that is how deviations compound and how a security hole ships.
Fix it in the phase that owns it.

---

## 6. Final acceptance (after Phase 12 gate)

The plan is "executed fully" only when **all** hold:

- [ ] Every section of `../host-folder-notebook-mounts-plan.md` (§4–§19) is
      satisfiable by pointing at a commit/file/test.
- [ ] **End-to-end local-folder flow** proven on the target stack: admin maps a host
      folder (notebook + project scope) → runs the displayed command → affected
      services restart → startup reconciliation creates symlinks → folder appears as
      `{notebookRoot}/{leafName}` (no `External/` wrapper) → reads/writes work
      through the link → `Remove mapped folder` unlinks without deleting host
      content.
- [ ] **Script execution works under a mapped folder** and **every unregistered
      symlink is still rejected** (positive + negative test).
- [ ] No recursive indexing of mounted contents by default; both sync paths
      consistent.
- [ ] Global invariants (4.1) green on the final tree; **docker gate** green;
      **final CodeQL diff clean** (zero new vs baseline; any touched pre-existing
      finding fixed in-code).
- [ ] Documentation merged; SMB/CIFS carried into an explicit **Deferred follow-on**
      section (override CIFS branch + credential handling + reuse of the
      symlink/guard/reconciliation layers).
- [ ] `STATUS.md` shows every phase `DONE` with a passing gate and no open
      deviations.

When all are checked, summarize the run (phases, retries, any DECISIONS that changed
mid-flight, deferred SMB scope) for the user.
