# GuideAnts AI Image — Script Execution Requirements

**Status:** Requirements  
**Applies to:** All `guideants-ai` flavors (cpu, cuda13, rocm, slim, vulkan) and any image that embeds ScriptExecutionAgent (SEA); GuideAnts API control plane for scope hydration policy.

---

## 1. Purpose

Define the required behavior of ScriptExecutionAgent as shipped inside GuideAnts AI images, the image/runtime contracts needed for sandbox execution and local inference to operate together, and the API-owned policy for intelligent, idle-gated scope hydration after cold runtime starts.

### 1.1 Source-of-truth rule

The GuideAnts API and its data are the sole source of truth for:

1. Which `(projectId, guideScopeId)` scopes exist as product entities.
2. Which scopes are worth hydrating next (usage / recency ranking).
3. When the host is idle enough to run proactive package work.
4. When proactive hydration must pause because chat or other local-AI work is active.

SEA is an executor. It applies or runs what the API (or an operator through the admin API) asks for a **named** scope. SEA durable directories and runtime trees are not an inventory authority and must not be mixed with API data to decide candidate sets. Mixing those sources produces orphan scopes, phantom scopes, and contradictory “ready” vs empty-runtime signals.

---

## 2. Required system properties

### 2.1 Shared SEA payload

1. Every AI flavor image embeds the same ScriptExecutionAgent publish output at `/app/script-agent/`.
2. A change to SEA source is released by rebuilding every flavor that will be run or published, from that same publish.
3. Compose selects the image via `GA_AI_*_IMAGE`. The running container’s image ID is the system under test.

### 2.2 Image environment

Each AI image final stage defines at least:

| Variable | Value |
|---|---|
| `SCRIPT_EXECUTION_ADMIN_API_ENABLED` | `true` |
| `SCRIPT_EXECUTION_ADMIN_STATE_DIR` | `/var/lib/guideants/script-agent-admin` |
| `SCRIPT_EXECUTION_SCOPE_STATE_ROOT` | `/var/lib/guideants/script-agent-admin/scopes` |
| `SCRIPT_EXECUTION_SCOPE_RUNTIME_ROOT` | `/var/run/guideants/script-agent-runtime` |

Compose may override these; the image must supply defaults so SEA can start with a complete contract.

### 2.3 Runtime mounts (local compose)

| Mount | Path | Role |
|---|---|---|
| Durable admin/scope state | `/var/lib/guideants/script-agent-admin` | Definitions, durable applied-state audit |
| Executable runtime | `/var/run/guideants/script-agent-runtime` | Per-scope venvs, runtime-applied markers, caches |
| Content files | `/app/ContentFiles` | `FILE_STORAGE_ROOT` |

### 2.4 Why durable state and executable runtime are split

Azure Files (CIFS with `nounix`) cannot host Python venvs: `python -m venv` requires Unix symlinks (for example `lib64` → `lib`). Durable definitions therefore live on the durable share/volume; scoped venvs and runtime markers live on a local executable mount (EmptyDir on Azure Container Apps, named volume in compose). A cold or replaced runtime mount is expected; rehydration is policy-driven (§8), never a SEA startup fleet walk.

---

## 3. Required SEA behavior

### 3.1 Startup (listener and package work are decoupled)

These requirements exist because SEA has failed both ways already: (1) package work before bind → `/sandbox/*` connection refused while the process is “up”; (2) package work after bind across every known scope → green `/health` while a pip storm contends with llama/ASR/SD on the same machine.

1. Before the HTTP listener binds, SEA may only: read config, validate admin definition files (schema / line rules), and create the state and runtime root directories.
2. SEA binds `/health` and `/execute` immediately after that validation. Package install, uninstall, venv creation for scopes, and install-scripts are not prerequisites of bind.
3. Startup fails fast if `SCRIPT_EXECUTION_SCOPE_RUNTIME_ROOT` is unset or empty.
4. After bind, SEA does not start any synchronous or background job that enumerates durable scopes (`project-*/guide-*`) or applies requirements/install-scripts to them. There is no startup “warmup reconcile” of known scopes — not awaited, not `Task.Run`.
5. An empty or fresh `SCRIPT_EXECUTION_SCOPE_RUNTIME_ROOT` volume does not cause multi-scope work at process start. Missing runtime state is repaired only when a specific scope is needed (§3.4), when an operator or the API calls scoped admin apply (§3.5 / §8), or never automatically by SEA itself.
6. Entrypoint global admin bootstrap (image-level apt / global requirements into `/opt/venv`), if present, is hash-gated against durable global applied-state, runs at most that global set, and never walks per-project scopes.

### 3.2 Scope layout

For each `(projectId, guideScopeId)`:

| Kind | Location |
|---|---|
| Durable scope root | `{SCOPE_STATE_ROOT}/project-{projectId}/guide-{guideScopeId}/` |
| Durable requirements | `{durable}/requirements.txt` |
| Durable applied audit | `{durable}/applied-state.json` |
| Runtime scope root | `{SCOPE_RUNTIME_ROOT}/project-{projectId}/guide-{guideScopeId}/` |
| Python venv | `{runtime}/python-venv/` (or configured relative venv dir) |
| Runtime applied marker | `{runtime}/runtime-applied-state.json` |

### 3.3 Python environment model

1. Scoped venvs extend the image base Python runtime (`/opt/venv` on Linux AI images by default) via a `.pth` (or equivalent) so image packages remain importable.
2. Packages installed into the scoped venv take precedence on `sys.path`.
3. Requirements application is additive: declared packages are installed or upgraded to satisfy the definition.
4. Application of a requirements definition leaves undeclared packages in place, including packages visible only through the base runtime link.
5. Dirtiness for whether to run pip is determined solely by comparing definition hashes to the runtime applied marker (and durable audit where applicable). Presence of top-level packages that are not named in requirements is not a dirtiness signal and does not trigger uninstall.

### 3.4 On-demand provisioning (`POST /execute`)

This is the only path that creates or mutates a scope runtime as a **side effect of a user/tool script run**.

When executing Python for a scope:

1. SEA ensures the durable and runtime directories exist.
2. SEA ensures the scoped venv exists under the runtime root.
3. If `runtime-applied-state` hashes match the current durable definitions for requirements and install-scripts, SEA skips package work for that scope.
4. If hashes differ, SEA applies **only that scope’s** durable definitions to **that scope’s** runtime (additive install / install-scripts as configured), then updates the runtime marker (and durable audit as designed).
5. SEA then runs the requested script in that scoped interpreter.
6. Execute never enumerates or mutates other projects’ or guides’ scopes.
7. On-demand execute hydrate is allowed while chat is active: it is correctness for the tool that was invoked, bounded to that one scope.

### 3.5 Explicit admin apply (`POST /admin/apply`)

1. Admin apply runs only when invoked through the admin API (operator UI/proxy or API-owned proactive hydrate in §8).
2. Preflight validates definitions; successful preflight accepts a job and applies in the background.
3. Apply uses the same additive package rules and hash-only dirtiness as §3.3–3.4.
4. Scoped apply mutates one scope. Global apply enumerates known durable scopes only because an operator explicitly requested global apply.
5. Process start, container healthy, and inference warmup do not invoke admin apply.
6. Proactive recovery (§8) must use **scoped** apply only. Automated callers must not invoke global apply.

### 3.6 Isolation from inference (why AI stays usable)

1. At most one pip/install-script mutation runs against a given scoped venv at a time (execute vs admin apply on the same scope).
2. `/health` remains servable while a single-scope provision runs.
3. SEA initiates multi-scope package work only via explicit global admin apply (§3.5). SEA never invents a fleet walk after recreate.
4. Single-scope on-demand provision (§3.4) is allowed when a tool actually executes for that scope; its cost is bounded to that scope’s definition delta, not to the set of all historical guides on the volume.
5. SEA does not implement usage ranking, idle detection, or chat-awareness. Those belong to the API (§8).

### 3.7 Scoped status for a named scope

When the API asks about a scope it already knows (`projectId` + `guideId`):

1. SEA may report whether that scope’s runtime marker matches the staged durable definitions (runtime hydrated vs needs apply).
2. That answer is scoped to the IDs the API supplied. It is not a license for SEA to become the inventory source.
3. Durable audit alone (`applied-state.json`) is not sufficient to claim runtime readiness after a cold runtime mount.

---

## 4. Required readiness contract

1. Nginx proxies `/sandbox/*` to SEA on `127.0.0.1:8081`.
2. `GET /sandbox/health` succeeds only when SEA is accepting connections and reports healthy.
3. The AI container’s health signal used by dependents requires sandbox health when sandbox is enabled in that image.
4. Clients that call `/sandbox/execute` before SEA is listening receive a transport/gateway failure; the product either:
   - keeps the AI service non-ready until sandbox health succeeds, or
   - retries only connection-refused / bad-gateway failures that occur before SEA accepts the execute request, within a bounded deadline, and never retries after SEA has begun executing the script.

---

## 5. Required service outcomes (acceptance)

On a stack running a newly built AI image that embeds the SEA meeting §3:

| # | Requirement |
|---|---|
| A1 | `GET /sandbox/health` returns success. |
| A2 | `POST /sandbox/execute` with a trivial Python script returns stdout and exit code `0`. |
| A3 | For five minutes after SEA becomes healthy, process list shows no multi-scope package reconcile storm (no sustained `pip uninstall` / fleet apply) unless an operator explicitly started global apply. |
| A4 | Where the flavor includes llama: a short non-tool chat completion finishes successfully. |
| A5 | First Python execute against a scope with durable requirements and empty runtime hydrates that scope only; a second execute with unchanged definitions skips pip install. |
| A6 | With many durable scopes and a cold runtime mount, SEA alone does not walk them at startup; any proactive hydrate is API-driven, scoped, and idle-gated (§8). |
| A7 | While a conversation lock is held on a local-AI stack, proactive hydrate does not start a new scoped apply; on-demand `/execute` for an in-use tool may still hydrate that one scope. |
| A8 | Proactive hydrate candidate order follows API usage/recency ranking, not SEA directory mtime or enumerate order. |

---

## 6. Verification

### 6.1 Automated (gate before image build)

```powershell
dotnet test "src/server/ScriptExecutionAgent.Tests/ScriptExecutionAgent.Tests.csproj"
```

Required coverage:

| Behavior | Assertion |
|---|---|
| Additive apply | Package not listed in requirements remains after admin apply when requirements hash is unchanged |
| Startup | Process start does not apply all known scopes |
| On-demand hydrate | Empty runtime + durable requirements → first execute applies; second skips |
| Runtime root | Missing `SCRIPT_EXECUTION_SCOPE_RUNTIME_ROOT` fails startup |
| Additive under base link | Scope linked to fat base + small requirements → apply performs install/update of declared packages only |

### 6.2 Image build

```powershell
pwsh ./docker/build/build_guideants_ai.ps1 -Backend <flavor>
```

1. Build publishes current SEA source into the image.
2. Built image contains §2.2 environment defaults.
3. Active `GA_AI_*_IMAGE` is set to the new dated tag for flavors that will be run.
4. Definition of done for a flavor: that tag is running and §5 acceptance A1–A5 pass (A6–A8 when §8 is implemented).

### 6.3 Runtime acceptance procedure

1. Build the active flavor (§6.2).
2. Recreate `guideants-ai` onto that tag (operator-approved).
3. Execute A1–A5 (§5); when §8 is present, also A6–A8.
4. Record image ID, health timestamp, execute result, and chat result (if applicable).

### 6.4 API control-plane verification (when §8 is implemented)

| Behavior | Assertion |
|---|---|
| Source of truth | Candidate scopes are produced from API/DB entities + usage; not from SEA filesystem enumeration |
| Ranking | Higher-recency / higher-usage guides are hydrated before colder ones within the configured budget |
| Idle gate | New scoped apply is not started while the idle gate reports busy |
| Cap | At most one proactive scoped apply runs at a time; per-window scope budget is honored |
| No global apply | Background worker never calls global `POST /admin/apply` |

---

## 7. Delivery sequence

1. SEA source implements §3; §6.1 green.
2. Dockerfiles implement §2.2 for all flavors.
3. Build active flavor; run §6.3 for A1–A5.
4. Build remaining flavors from the same SEA publish before they are used or published.
5. Implement §4 readiness; re-check A1–A2 across a fresh recreate.
6. Implement §8 API control plane; verify A6–A8 and §6.4.

---

## 8. API-owned intelligent hydration (control plane)

This section is required so cold runtime mounts recover without recreating the startup pip storm, and without making SEA a second source of truth.

### 8.1 Ownership

| Concern | Owner |
|---|---|
| Product scopes that exist | GuideAnts API / database (notebooks, guides, assistants) |
| Rank / likelihood of next use | GuideAnts API usage data (for example `UsageEvents` recency by `ProjectId` + `AssistantId` as `guideScopeId`) |
| Idle vs busy host | GuideAnts API (conversation locks, local-AI warmup apply in progress, and related existing gates) |
| Execute one named scoped apply | SEA admin API |
| On-demand hydrate during a tool call | SEA `/execute` (§3.4) |

SEA must not rank scopes, detect chat activity, or choose what to warm. The API must not discover candidates by walking SEA durable or runtime directories.

### 8.2 Candidate set

1. Candidates are `(projectId, guideScopeId)` rows the API already knows as product entities.
2. Ranking uses API usage/recency (and related API signals). Prefer guides with recent activity.
3. A durable folder on the SEA volume with no API entity is **not** a hydration candidate.
4. An API-known guide with no SEA runtime tree yet **is** a valid candidate when policy selects it.
5. Scope identity for hydration must match the same `guideScopeId` resolution used for `/execute` (guide / template id — not ad hoc filesystem names).

### 8.3 Idle gate

Proactive hydration reuses the product’s existing “defer local heavy work while chat is active” posture:

1. When chat and embeddings use local AI and any conversation lock is active, do not **start** a new proactive scoped apply.
2. When local-AI warmup/apply is in progress, do not start a new proactive scoped apply.
3. Optionally treat llama alias load/unload locks as busy for starting proactive apply.
4. On-demand `/execute` hydrate (§3.4) is **not** blocked by this gate.
5. The gate should be a shared API capability (same idea as conversation-lock gating for extraction/indexing), not a one-off SEA flag.

### 8.4 Worker behavior

1. An API-owned background worker (or equivalent job) builds a ranked candidate list from §8.2.
2. For each candidate, while the idle gate allows work, the API calls **scoped** `POST /admin/apply?projectId=&guideId=` (or the existing system-guide proxy), then polls the apply job.
3. Concurrency: at most one proactive scoped apply at a time.
4. Budget: a configured maximum number of scopes per idle window / period; stop when budget or idle ends.
5. Never call global admin apply from this worker.
6. If the idle gate becomes busy between scopes, pause; do not drain the rest of the list.
7. If a proactive apply is already running when chat becomes active, do not start another; document whether in-flight pip is left to finish (default) or cancelled only if an explicit cancel contract exists later. Prefer finish-current over inventing cancel behavior.

### 8.5 Separation from staging and operators

1. MCP / guide save may stage requirements and install-scripts into durable SEA state without applying them.
2. Operator-triggered apply via the admin UI remains allowed and is not required to wait for the idle gate (operator intent), but must still be scoped unless the operator chooses global.
3. Proactive hydrate (§8.4) is recovery/optimization under idle policy; it does not replace staging or operator apply.

### 8.6 Acceptance relative to SEA

§8 must preserve §3: SEA stays bind-first, no startup fleet reconcile, additive hash-gated applies, single-scope mutations unless an operator requests global apply. Intelligent ordering and pause/resume live only in the API.
