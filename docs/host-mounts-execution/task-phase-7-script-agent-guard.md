# Task — Phase 7: Script-agent path-guard rework (registered crossings only)

> Subagent brief. Execute top to bottom; return the Report-back contract verbatim.
> **THE most security-sensitive phase. CodeQL gate required. Treat the registry
> read + additional-root + "only registered links followed" as ONE reviewed unit.**

## Mission

Rework `PathGuard` in the script execution agent so `/execute` and `/files` calls can
operate **under registered mapped folders** — and **only** registered ones — without
weakening the existing protection against arbitrary reparse-point traversal.

## Read first

- `../host-folder-notebook-mounts-plan.md` §13 (current guard behavior + the exact
  required changes), §3.1 (the link is a reparse point; target is outside the storage
  root), §20 (reject unregistered symlinks; never follow arbitrary reparse points).
- `./DECISIONS.md` → Part B registered-links-only invariant.
- `./codeql-gate.md` (**`cs/path-injection` focus** — this is the phase it matters
  most).
- `src/server/ScriptExecutionAgent/Program.cs` —
  `PathGuard.TryResolveAndAuthorizePath` and `HasReparsePointBetween`,
  `FILE_STORAGE_ROOT`.
- The Phase 6 `.guideants/mounts.json` schema + writer.

## Preconditions

- Phase 6 gate green (symlinks + registry exist to test against).

## Guardrails (hard) — read twice

- The guard must change **two** things (plan §13) and **nothing more permissive**:
  1. Add `/app/HostMounts` (or each registered `containerSourcePath`) as an
     **additional authorized root** — so a resolved target under a registered mount
     source is not rejected for being outside `FILE_STORAGE_ROOT`.
  2. Allow a reparse-point crossing **only** when **all** hold:
     - the symlink path matches a **registered** mount link in `mounts.json`;
     - the resolved target is under the registered `containerSourcePath`;
     - the mount is `writable` (for write operations);
     - the request path stays under **either** the notebook root **or** an authorized
       mount source.
- **All unregistered symlinks/reparse points remain rejected.** This is the
  invariant the whole feature's security rests on. A negative test proving an
  unregistered planted link is refused (for both `/execute` and `/files`) is
  **mandatory** and part of DoD.
- **No fallback / fail-open.** If `mounts.json` is missing, unreadable, or does not
  list the link → **reject** (do not assume allowed). A malformed registry is a
  rejection, not a bypass.
- Re-resolve to a canonical absolute path and re-check containment **after** symlink
  resolution — never trust the pre-resolution string (CodeQL `cs/path-injection`).
- Do not broaden writability: a read-only mount must reject writes through the link.

## Tasks

1. Add a registry reader that loads the relevant notebook's `.guideants/mounts.json`
   and exposes the registered links (path, `containerSourcePath`, `writable`).
2. Modify `TryResolveAndAuthorizePath` to accept the additional authorized root(s)
   and the registered-crossing allowance, keeping strict containment for everything
   else.
3. Modify `HasReparsePointBetween` (or its callers) so a reparse point that is a
   **registered** link is permitted, while any **unregistered** reparse point is
   still rejected.
4. Thread the writable check into write operations.
5. Add `ScriptExecutionAgent.Tests`:
   - **positive**: `/execute` + `/files` succeed under a registered, writable mapped
     folder; reads/writes resolve to the source.
   - **negative (mandatory)**: an unregistered symlink/reparse point under a notebook
     root is rejected; a write through a read-only registered mount is rejected; a
     traversal attempt (`..` escaping the source/root) is rejected; a missing/
     malformed `mounts.json` rejects rather than allows.

## Files in scope

- `src/server/ScriptExecutionAgent/Program.cs` (PathGuard + a registry reader; keep
  the reader small and in this project unless an existing shared model fits)
- `src/server/ScriptExecutionAgent.Tests/*`

**Out of scope:** the API service, sync, UI, new-notebook flow.

## Self-verification

```bash
cd src/server && dotnet build GuideAntsApi.sln
cd src/server && dotnet test ScriptExecutionAgent.Tests/ScriptExecutionAgent.Tests.csproj
```

Plus global gate (orchestration §4.1) **and** CodeQL gate (`codeql-gate.md` §5
Phase-7 row — full diff, path focus).

## Definition of Done

- [ ] Additional authorized root(s) + registered-crossing allowance implemented
      exactly per §13; strict containment preserved elsewhere.
- [ ] **Unregistered** symlinks/reparse points still rejected — proven by negative
      tests for both `/execute` and `/files`.
- [ ] Writable check enforced; read-only mount rejects writes.
- [ ] Missing/malformed registry → reject (no fail-open).
- [ ] `ScriptExecutionAgent.Tests` green incl. new positive + negative cases.
- [ ] **CodeQL diff clean** (`cs/path-injection`), fixed in-code, no suppression.

## Report-back contract (return exactly this)

```
PHASE 7 REPORT
- Additional authorized root(s): <how added>
- Registered-crossing conditions enforced (all 4 from §13): <checklist y/n>
- Unregistered reparse rejected (negative test names): <list>
- Read-only-mount write rejected: <test name>
- Missing/malformed mounts.json -> reject (no fail-open): <test name>
- ScriptExecutionAgent.Tests: <counts>
- CODEQL: build-mode=none=<yes> new-vs-baseline=<count -> rules or none> fixed-in-code=<yes/n-a>
- Verification: build=<...>
- Files touched: <list>
- Deviations / surprises: <list or "none">
```
