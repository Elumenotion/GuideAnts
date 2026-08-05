# Phase 1 — SEA Runtime Invariants

**Depends on:** Pre-flight complete
**Blocks:** Phases 2–6

> **Mechanism is not pre-decided.** Tasks 1 and 2 below describe *one* way to satisfy
> §3.6.1 and §3.7 (lock key shape, status field names). Those specifics are `DECISIONS.md`
> Part C — the implementer's call. The **requirement** is binding; the shape is a starting
> suggestion, and whatever is built gets named in the report-back.

---

## Mission

Make the ScriptExecutionAgent process provably conform to spec §3.1 (startup), §3.6
(isolation from inference), and §3.7 (scoped status), and close the §6.1 automated
coverage matrix. No Docker, no API, no compose changes in this phase.

Two of the three properties are **already true** as-built and must be **locked down by
tests** so a future change cannot silently regress them. The third (§3.6.1 cross-path
mutual exclusion, and §3.7 runtime-aware status) requires new code.

---

## Read first

- The spec §3.1, §3.3, §3.4, §3.6, §3.7, §6.1
- `docs/local-ai-regression-execution/00-orchestration.md` §1.1, §1.2, §4.2, §5
- `docs/local-ai-regression-execution/DECISIONS.md` Part C (former B5, B6 — mechanism), Part D
- SEA source:
  - `src/server/ScriptExecutionAgent/Program.cs` — startup `:11-108`, bind `:751-753`,
    `/execute` `:116-217`, admin routes `:468-749`, `ResolveScope :1514-1536`,
    `EnsurePythonVenvAsync :1569-1672`,
    `EnsureScopeRequirementsForExecutionAsync :1809-1835`,
    `SyncScopeRuntimeFromDefinitionsAsync :1837-1908`,
    `EnumerateExistingScopes :2363-2388`
  - `src/server/ScriptExecutionAgent/AdminApplyJobRuntime.cs` — `StartApplyAsync :36-85`,
    background `Task.Run :82`
  - `src/server/ScriptExecutionAgent/AdminSetupStatusRuntime.cs` — `BuildAsync :55-183`
  - `src/server/ScriptExecutionAgent/ScopeRuntimeAppliedStateRuntime.cs` (runtime marker)
  - `src/server/ScriptExecutionAgent/AdminScopeAppliedStateRuntime.cs` (durable audit)
- Tests:
  - `src/server/ScriptExecutionAgent.Tests/Infrastructure/ScriptExecutionAgentWebApplicationFactory.cs`
  - `src/server/ScriptExecutionAgent.Tests/InProcess/ScriptExecutionAgentAdminApiTests.cs`
  - `src/server/ScriptExecutionAgent.Tests/InProcess/ScriptExecutionAgentInProcessTests.cs`

---

## Preconditions

- [ ] Baseline build/test recorded in `STATUS.md`
- [ ] As-built confirmation table in `STATUS.md` filled in

---

## Guardrails

- Do **not** add an `IHostedService`, `BackgroundService`, timer, or `Task.Run` that is
  reachable from process start. The only permitted `Task.Run` remains the one that runs an
  **already-accepted** admin apply job (`AdminApplyJobRuntime.cs:82`).
- Do **not** add a caller of `EnumerateExistingScopes` outside explicit global admin apply
  and its preflight.
- Do **not** add pip uninstall, prune, or any "packages not in requirements" reconciliation.
- Do **not** add a listing/inventory endpoint. §3.7 answers only for IDs the caller supplied.
- Do **not** put `/health` behind any lock, semaphore, or scope work.
- Do **not** change the durable audit (`applied-state.json`) format — the new runtime
  reporting is additive.
- No Docker or compose edits in this phase.

---

## Tasks

### 1. Per-scope mutation mutex (§3.6.1 — gap G2)

Today the `/execute` hydrate path and the scoped `/admin/apply` job path can both mutate
the same scope's venv concurrently. `EnsurePythonVenvAsync` has a per-venv semaphore for
**venv creation** only, and `AdminApplyJobRuntime` dedupes **admin jobs** by scope key —
neither excludes the other.

Introduce one shared, process-wide, per-scope async mutex:

| Aspect | Requirement |
|---|---|
| Key | `project:{projectId:D}:guide:{guideScopeId:D}` (same shape `AdminApplyJobRuntime` already uses) |
| Held by | `/execute` requirements/install-script application; scoped admin apply job execution |
| Not held by | `/health`, `/execute` script **run** after provisioning completes, any read-only status |
| Different scopes | Never block each other |
| Global apply | Acquires each scope's mutex as it reaches that scope; does not hold all of them at once |
| Timeout | Bounded wait with a clear error, not an indefinite block |

Reuse the existing scope-key helper rather than formatting the key in two places.

> This is mutual exclusion, not a retry or fallback. If the lock cannot be acquired within
> the bound, fail the request with an explicit "scope busy" error. Do not silently skip
> package work and pretend the scope is ready.

### 2. Runtime-aware scoped status (§3.7 — gap G3)

`AdminSetupStatusRuntime.BuildAsync` currently answers from the durable audit
(`AdminScopeAppliedStateRuntime`). After a cold runtime mount the durable audit still says
"applied" while the venv is gone — spec §3.7.3 forbids treating that as readiness.

One way to do this is a **runtime** section on the scoped response. The field names below
are a suggestion, not a contract (`DECISIONS.md` Part C):

| Field | Meaning |
|---|---|
| `runtime.venvPresent` | Does the scoped venv interpreter exist under the runtime root? |
| `runtime.requirementsHashMatches` | Does `runtime-applied-state.json` match the staged durable `requirements.txt` hash? |
| `runtime.installScriptsHashMatches` | Same for install-scripts |
| `runtime.state` | `hydrated` when venv present and both hashes match; otherwise `needs-apply` |

Rules:

- Scoped requests only. A request without both `projectId` and `guideId` returns the
  existing global shape with **no** runtime section (§3.7.2).
- The existing durable audit fields are unchanged (back-compat for the admin UI and
  `McpSandboxPublishGateService`).
- Computing this section performs **no** package work and creates **no** directories
  beyond what already exists. It is a read.
- No enumeration: the answer is derived only from the supplied IDs.

### 3. Lock down bind-first (§3.1.1–3.1.2, §3.1.4–3.1.5)

No production change expected here — this is about making the invariant testable.

- Extract the pre-bind sequence (`Program.cs:30-108`) into a named, callable startup
  routine if that is what a test needs to assert on; keep behavior identical.
- Ensure the routine's only side effects remain: read config, validate admin definition
  files, create state and runtime root directories.

If extraction would meaningfully restructure `Program.cs`, prefer asserting behavior
through the in-process factory instead. Do not refactor for its own sake.

### 4. Close the §6.1 coverage matrix (gap G7)

Every row below must map to a **named test**. Some exist; confirm and reference them
rather than duplicating.

| Spec §6.1 behavior | Assertion | Existing? |
|---|---|---|
| Additive apply | A package not listed in `requirements.txt` remains installed after scoped admin apply when the requirements hash is unchanged | Yes — `ScriptExecutionAgentAdminApiTests` scoped-apply-preserves-undeclared test; verify it still asserts this |
| Startup | With ≥2 durable scopes staged and an empty runtime root, process start applies **no** scope: no venv created, no `runtime-applied-state.json` written, no pip invoked | **New** |
| On-demand hydrate | Empty runtime + durable requirements → first `/execute` applies and writes the runtime marker; second `/execute` with unchanged definitions performs no pip install | **New** (partially covered by the existing "runtime rehydration after cache clear" test — extend or add) |
| Runtime root | Missing/blank `SCRIPT_EXECUTION_SCOPE_RUNTIME_ROOT` fails startup | **New** (behavior exists at `Program.cs:57-60`, untested) |
| Additive under base link | Scope linked to a fat base runtime with a small `requirements.txt` → apply installs/updates only the declared packages; base-visible packages are untouched and are not a dirtiness signal | **New** |

Add these tests for this phase's own code:

| Behavior | Assertion |
|---|---|
| Same-scope exclusion | Concurrent `/execute` hydrate and scoped `/admin/apply` on one scope serialize; neither corrupts the runtime marker |
| Different-scope independence | Concurrent mutations on two scopes both complete; no deadlock, no cross-blocking |
| Health not gated | `/health` returns 200 while a single-scope provision holds the scope mutex (§3.6.2) |
| Runtime status truthfulness | Durable audit says applied + runtime marker deleted → scoped status reports `needs-apply` |
| Runtime status scoping | Unscoped `setup-status` returns no runtime section and does not enumerate |
| No new enumeration callers | A guard test (or a documented review check) that `EnumerateExistingScopes` callers are exactly: global preflight and global apply |

Tests that shell out to real `pip` are slow and environment-sensitive. Where the existing
suite already stubs or skips on non-Linux, follow that precedent rather than inventing a
new harness. `[assembly: DoNotParallelize]` is in force — do not add parallelism.

---

## Files in scope

| Action | Path |
|--------|------|
| Modify | `src/server/ScriptExecutionAgent/Program.cs` (scope mutex wiring; startup extraction if needed) |
| Modify | `src/server/ScriptExecutionAgent/AdminApplyJobRuntime.cs` (acquire scope mutex around apply) |
| Modify | `src/server/ScriptExecutionAgent/AdminSetupStatusRuntime.cs` (runtime section) |
| Modify/Add | `src/server/ScriptExecutionAgent/ScopeRuntimeAppliedStateRuntime.cs` (read helper for status) |
| Add | Scope mutex type (new file under `src/server/ScriptExecutionAgent/`) |
| Modify | `src/server/ScriptExecutionAgent.Tests/InProcess/ScriptExecutionAgentAdminApiTests.cs` |
| Modify | `src/server/ScriptExecutionAgent.Tests/InProcess/ScriptExecutionScopeRuntimeTests.cs` |
| Add | `src/server/ScriptExecutionAgent.Tests/InProcess/ScriptExecutionAgentStartupInvariantTests.cs` |
| Add | `src/server/ScriptExecutionAgent.Tests/InProcess/ScopeMutationConcurrencyTests.cs` |
| Modify | `src/server/ScriptExecutionAgent/README.md` (document the runtime status section) |

Out of scope: Dockerfiles, entrypoints, nginx, compose, anything under `GuideAntsApi`.

---

## Self-verification

```powershell
dotnet test "src/server/ScriptExecutionAgent.Tests/ScriptExecutionAgent.Tests.csproj"
cd src/server; dotnet build GuideAntsApi.sln; dotnet test GuideAntsApi.sln
```

Then audit by inspection:

```powershell
# no new fleet-walk callers
rg -n "EnumerateExistingScopes" src/server/ScriptExecutionAgent
# no hosted services or startup background work
rg -n "IHostedService|BackgroundService|Task\.Run" src/server/ScriptExecutionAgent
# no uninstall/prune path
rg -n "uninstall|prune" src/server/ScriptExecutionAgent
```

- [ ] `EnumerateExistingScopes` callers are exactly global preflight + global apply
- [ ] The only `Task.Run` is the accepted-apply-job launcher
- [ ] No pip uninstall in scoped paths (apt removal on **global** apply is pre-existing and stays)
- [ ] `/health` unaffected by scope locking

---

## Definition of Done

- [ ] Phase 1 gate (orchestration §4.2) passes
- [ ] Every §6.1 row has a named test; the mapping is recorded in `acceptance-evidence.md`
- [ ] SEA test project green; API solution build/test no worse than baseline
- [ ] `STATUS.md` updated: Phase 1 → `DONE`, gaps G2/G3/G7 → closed
- [ ] No anti-goal from orchestration §5 present in the diff

---

## Report-back

```text
PHASE 1 COMPLETE
- Scope mutex: <type name, key shape, call sites>
- Runtime status section: <fields, endpoint, scoped-only proof>
- Startup invariant test: <name> (N durable scopes, empty runtime, zero applies)
- 6.1 coverage map:
    additive apply           -> <test name>
    startup no-apply         -> <test name>
    on-demand hydrate/skip   -> <test name>
    runtime root required    -> <test name>
    additive under base link -> <test name>
- Concurrency tests: <same-scope, different-scope, health-not-gated>
- SEA tests: <passed/failed/skipped>
- Solution build/test vs baseline: <delta>
- Enumeration callers audit: <list>
- Deviations: <none | list>
```
