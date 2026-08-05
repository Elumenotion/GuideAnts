# Local-AI Regression Recovery — Acceptance Evidence

Captured during Phases 1–6 and final acceptance. This is the artifact a reviewer reads
instead of re-running everything.

Rule: **paste raw command output.** A summary sentence is not evidence. If a check could
not be run, say so and say why — do not leave a blank that reads as a pass.

---

## Run identity

```text
Active flavor:     <cpu|cuda13|rocm|slim|vulkan>        (DECISIONS B2, D1)
Image repo:tag:    <e.g. guideants-ai:cpu-26216.1430>
Image ID:          <sha256:...>
SEA publish id:    <com.guideants.sea.publish label>
Compose file:      docker/docker-compose.<flavor>.yml
Date:              <date>
```

---

## Baseline (pre-change)

### Server build

```text
cd src/server && dotnet build GuideAntsApi.sln
Result: pending
```

### Server tests

```text
cd src/server && dotnet test GuideAntsApi.sln
Result: pending
  GuideAntsApi.Tests:            pending
  ScriptExecutionAgent.Tests:    pending
  GuideAntsApi.IntegrationTests: pending
```

### Stack state at baseline

```text
docker compose -f docker/docker-compose.<flavor>.yml ps
Result: pending

docker inspect --format '{{.Image}}' <guideants-ai container>
Result: pending
```

### As-built confirmation (orchestration §1.1)

```text
rg -n "EnumerateExistingScopes" src/server/ScriptExecutionAgent
rg -n "IHostedService|BackgroundService" src/server/ScriptExecutionAgent
rg -n "uninstall|prune" src/server/ScriptExecutionAgent
docker run --rm --entrypoint env <baseline tag> | Select-String "SCRIPT_EXECUTION_"
Result: pending
```

---

## Phase 1 — SEA runtime invariants

### SEA test run

```text
dotnet test "src/server/ScriptExecutionAgent.Tests/ScriptExecutionAgent.Tests.csproj"
Result: pending
```

### Spec §6.1 coverage map

| Spec §6.1 behavior | Test name | Result |
|---|---|---|
| Additive apply — undeclared package survives | pending | pending |
| Startup — process start applies no scope | pending | pending |
| On-demand hydrate — first applies, second skips | pending | pending |
| Runtime root — missing var fails startup | pending | pending |
| Additive under base link — declared only | pending | pending |

### Phase 1 new-behavior coverage

| Behavior | Test name | Result |
|---|---|---|
| Same-scope execute ↔ apply serialize | pending | pending |
| Different-scope independence, no deadlock | pending | pending |
| `/health` not gated by scope mutex | pending | pending |
| Runtime status truthful after marker deleted | pending | pending |
| Unscoped setup-status has no runtime section | pending | pending |

### Enumeration caller audit

```text
rg -n "EnumerateExistingScopes" src/server/ScriptExecutionAgent
Expected callers: global preflight, global apply. Nothing else.
Result: pending
```

---

## Phase 2 — Image + payload contract

### Build

```text
pwsh ./docker/build/build_guideants_ai.ps1 -Backend <flavor>
Result: pending
Dated tag:  pending
Latest tag: pending
```

### §2.2 environment verification (from the built image)

```text
docker run --rm --entrypoint env <tag> | Select-String "^SCRIPT_EXECUTION_"
Result: pending
```

| Variable | Required | Observed |
|---|---|---|
| `SCRIPT_EXECUTION_ADMIN_API_ENABLED` | `true` | pending |
| `SCRIPT_EXECUTION_ADMIN_STATE_DIR` | `/var/lib/guideants/script-agent-admin` | pending |
| `SCRIPT_EXECUTION_SCOPE_STATE_ROOT` | `/var/lib/guideants/script-agent-admin/scopes` | pending |
| `SCRIPT_EXECUTION_SCOPE_RUNTIME_ROOT` | `/var/run/guideants/script-agent-runtime` | pending |

### SEA publish identity (§2.1.2; mechanism per Part C)

```text
docker inspect --format '{{index .Config.Labels "com.guideants.sea.publish"}}' <tag>
Result: pending
```

### Entrypoint ordering (gap G1)

```text
SEA process start line:     pending
Global bootstrap line:      pending
nginx line:                 pending
Bootstrap blocks SEA start: pending (must be: no)
```

### Cold-bootstrap bind measurement

```text
Throwaway container, empty global admin state, time to first /sandbox/health 200:
  before change: pending
  after change:  pending
```

### §2.3 mount inventory

| Compose file | admin state | runtime | ContentFiles |
|---|---|---|---|
| pending | pending | pending | pending |

---

## Phase 3 — Readiness contract

### §4.4 option

```text
Implemented: health gate — AI service non-ready until sandbox health succeeds  [B7]
Bounded connect-only retry: NOT implemented.
API sandbox client retry wrapper audit:
  rg -n "Retry|Polly|backoff" src/server/GuideAntsApi/Services/NotebookDockerScriptService.cs
  Result: pending (expected: none)
```

### HEALTHCHECK

```text
docker inspect --format '{{json .Config.Healthcheck}}' <tag>
Result: pending
Requires /sandbox/health (AND, not OR): pending
```

### Unhealthy-when-SEA-down proof

```text
docker run -d --rm --name sea-healthcheck <tag>
docker exec sea-healthcheck sh -c "pkill -f ScriptExecutionAgent.dll"
docker inspect --format '{{.State.Health.Status}}' sea-healthcheck
Result: pending (expected: unhealthy)
Time to unhealthy: pending
```

### Dependent gating

| Compose file | sandbox caller present | `service_healthy` applied |
|---|---|---|
| pending | pending | pending |

---

## Phase 4 — Runtime acceptance A1–A5

### Recreate

```text
Approved by user: pending (quote/when)
Command: docker compose -f <compose> up -d --force-recreate guideants-ai
T_start (recreate complete): pending
T0 (first /sandbox/health 200): pending
Elapsed: pending
```

### A1 — sandbox health

```text
docker compose -f <compose> exec -T guideants-ai curl -fsS -o NUL -w "%{http_code}\n" http://localhost/sandbox/health
Result: pending
```

### A2 — trivial execute

```text
POST /sandbox/execute (trivial Python)
Result: pending
stdout: pending
exit code: pending
```

### A3 — five-minute observation

```text
Samples (>=30s apart) from T0:
  t+00:00 pending
  t+00:30 pending
  t+01:00 pending
  ...
  t+05:00 pending

Multi-scope pip activity observed: pending (expected: none)
Operator-initiated global apply during window: pending (expected: no)
```

### A4 — chat completion

```text
Flavor includes llama: pending
Result: pending  (or: n/a — <flavor> has no llama)
```

### A5 — hydrate then skip

```text
Scope: project-<p>/guide-<g>
Durable requirements present: pending
Runtime tree before: pending (expected: absent)

First execute:
  venv created: pending
  packages installed: pending
  runtime-applied-state.json written: pending

Second execute (definitions unchanged):
  pip invocations: pending (expected: 0)
  runtime marker changed: pending (expected: no)

Other scopes touched: pending (expected: none)
Undeclared package <name> still present: pending (expected: yes)
```

---

## Phase 5 — API hydration control plane (unit level)

### Build + test

```text
cd src/server && dotnet build GuideAntsApi.sln
cd src/server && dotnet test GuideAntsApi.sln
Result: pending (delta vs baseline: pending)
```

### Shared resolver (§8.2.5, B14)

```text
Resolver type: pending
Call sites replaced: NotebookDockerScriptService, McpSandboxExecutor, McpToolExecutor, candidate builder
Resolved-id-unchanged tests: pending
```

### AssistantId → guideScopeId mapping decision

```text
Chosen mapping: pending
Rationale: pending
```

### Job-system integration (B16)

```text
JobType string:        pending
Payload record:        pending
Handler type:          pending
Scheduler type:        pending
JobTypes config:       MaxConcurrency 1, LeaseSeconds pending, Enabled true
GatedJobTypes entry:   pending
Priority = rank via:   pending
Duplicate-enqueue guard: pending
Stale-job handling:    pending
Bespoke claim/lease/retry/concurrency code written: pending (expected: none)
```

### §6.4 assertions (unit form)

| Behavior | Test name | Result |
|---|---|---|
| Source of truth (no filesystem inventory) | pending | pending |
| Ranking by UsageEvents recency | pending | pending |
| Rank reaches queue as `Priority` | pending | pending |
| Identity matches `/execute` resolver | pending | pending |
| Gate — conversation lock leaves rows `Pending` | pending | pending |
| Gate — warmup/apply (§8.3.2, new signal) | pending | pending |
| Gate — llama alias lock | pending | pending |
| Gate does not block `/execute` | pending | pending |
| Gate — existing gated types unaffected | pending | pending |
| Concurrency cap = 1 via `MaxConcurrency` | pending | pending |
| Budget per tick honored | pending | pending |
| Pause, do not drain | pending | pending |
| Finish-current on busy | pending | pending |
| No duplicate backlog across busy ticks | pending | pending |
| No global apply constructible | pending | pending |
| Startup throws without `JobTypes` config | pending | pending |
| Default posture matches B10 | pending | pending |

### API-side audits

```text
rg -n "script-agent-admin|script-agent-runtime|SCOPE_STATE_ROOT|SCOPE_RUNTIME_ROOT" src/server/GuideAntsApi
Result: pending (expected: no inventory reads)

git diff --stat -- src/server/ScriptExecutionAgent
Result: pending (expected: empty for this phase)
```

---

## Phase 6 — A6–A8, flavors, docs

### Flavor payload parity (§2.1.2)

| Flavor | Tag | SEA publish id | Matches active? |
|---|---|---|---|
| pending | pending | pending | pending |

```text
Flavors intentionally not rebuilt (not run or published): pending
```

### Hydration configuration for the run

```text
JobTypes.<Type>.Enabled:         true
JobTypes.<Type>.MaxConcurrency:  1
JobTypes.<Type>.LeaseSeconds:    pending
Scheduler TickIntervalSeconds:   pending
Scheduler MaxScopesPerWindow:    pending
Basis (Phase 4 apply duration):  pending
Off-switch verified to stop it:  pending
```

### A6 — cold runtime mount, no startup walk

```text
Durable scopes present: pending
Runtime volume cleared: pending (destructive recreate approved: pending)
/sandbox/health after recreate: pending

Five-minute observation:
  package operations observed: pending
  each attributed to /execute or a named scoped apply from a hydration job: pending
  unattributed operations: pending (expected: 0)
```

### A7 — lock blocks proactive, not on-demand

```text
Conversation lock held from: pending
Gate defer log line: pending
Busy reason reported: pending
Hydration rows claimed during lock: pending (expected: none; rows stay Pending)
Job Processing at lock time: pending -> ran to completion (B12): pending
On-demand /execute hydrate during lock: pending (expected: succeeded)
Claiming resumed after lock released: pending
Duplicate Pending rows per scope across the busy window: pending (expected: <=1)
```

### A8 — ranking, not directory order

```text
Seeded usage recency order:   pending
Directory mtime order:        pending (deliberately opposite)
Enqueued Priority values:     pending
Actual claim/apply order:     pending
Followed recency: pending
```

### §6.4 matrix (runtime)

| Behavior | Assertion | Result |
|---|---|---|
| Source of truth | Candidates from API/DB entities + usage, not SEA filesystem | pending |
| Ranking | Higher-recency guides hydrated first within budget | pending |
| Idle gate | No hydration job claimed while busy | pending |
| Cap | One `Processing` at a time; per-window budget honored | pending |
| No global apply | Every hydration request carried both ids | pending |

### §8.6 re-confirmation

```text
Bind-first preserved:                 pending
No startup fleet reconcile:           pending
Additive, hash-gated:                 pending
Single-scope unless operator global:  pending
Ranking/idle logic absent from SEA:   pending
```

---

## Anti-goal audit (orchestration §5)

| Anti-goal | Present in diff? |
|---|---|
| SEA background service repairing scopes | pending |
| SEA ranking / idle detection / chat awareness | pending |
| Hydration calling global apply | pending |
| A standalone hydration BackgroundService with its own claim/lease/retry/concurrency | pending |
| Candidates discovered by listing directories | pending |
| Durable audit treated as runtime readiness | pending |
| Uninstall / prune of undeclared packages | pending |
| Silent retry after execution started | pending |
| Bootstrap blocking bind | pending |

---

## Security / hygiene

```text
git diff --stat
Secrets scan over changed files (tokens, keys, passwords): pending
No new secret-bearing env vars added: pending
```

---

## Files changed

```text
src/server/ScriptExecutionAgent/
  <list>
src/server/ScriptExecutionAgent.Tests/
  <list>
src/server/GuideAntsApi/
  <list>
src/server/GuideAntsApi.Tests/
  <list>
docker/build/guideants-ai/
  <list>
docker/docker-compose.*.yml
  <list>
docs/local-ai-regression-execution/
  00-orchestration.md
  DECISIONS.md
  STATUS.md
  sandbox-gate.md
  acceptance-evidence.md
  task-phase-1-sea-runtime-invariants.md
  task-phase-2-image-payload-contract.md
  task-phase-3-readiness-contract.md
  task-phase-4-runtime-acceptance-a1-a5.md
  task-phase-5-api-hydration-control-plane.md
  task-phase-6-hydration-acceptance-docs.md
```

---

## Spec discrepancies found (not edited)

```text
<none | list: clause, what the code/runtime showed, recommended spec change>
```

The spec is the requirement. If implementation proved a clause wrong, record it here and
let the user decide — do not edit the contract to match the code.
