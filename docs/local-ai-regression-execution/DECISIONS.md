# Local-AI Regression Recovery — Derived Requirements

Last updated: 2026-08-05

[`docs/local-ai-regression-recovery-spec.md`](../local-ai-regression-recovery-spec.md) is
the requirement. This file derives the consequences an implementer needs and records which
choices are actually left open. It adds nothing to the spec and negotiates nothing in it.

---

## Part A — The control model

Everything below follows from this. §1.1 and §8.1 are the spec's source-of-truth rule and
ownership table; they are restated here because most implementation errors in this area are
ownership errors, not coding errors.

**The GuideAnts API and its data are the sole source of truth for:**

1. Which `(projectId, guideScopeId)` scopes exist as product entities.
2. Which scopes are worth hydrating next.
3. When the host is idle enough to run proactive package work.
4. When proactive hydration must pause because chat or other local-AI work is active.

**SEA is an executor.** It applies or runs what the API — or an operator through the admin
API — asks for a **named** scope. SEA durable directories and runtime trees are not an
inventory authority.

| Concern | Owner |
|---|---|
| Which scopes exist as product entities | API / database |
| Rank / likelihood of next use | API usage data |
| Idle vs busy host | API |
| Decide what to warm, and when | API |
| Execute one named scoped apply | SEA admin API |
| On-demand hydrate during a tool call | SEA `/execute` |
| Report whether one named scope's runtime is hydrated | SEA admin API |

On the API side, that ownership is exercised through the existing background-job
architecture, not a new one — see B16.

Two failure shapes follow directly, and both have already happened:

- SEA deciding anything (ranking, warming, walking the fleet) → pip storm contending with
  inference on the same machine.
- The API discovering scopes by reading SEA's filesystem → orphan scopes, phantom scopes,
  contradictory ready-vs-empty signals.

---

## Part B — Settled

Settled by the spec, or decided by the product owner. Each row names its source; the
non-obvious ones carry their derivation. B16 is listed first because the rest of the §8
work sits inside it.

### B16 — Hydration is a job type in the existing background-job architecture

**Source:** product owner, 2026-08-05.

Proactive hydration ships as a `JobQueue` type with an `IJobHandler`, plus a scheduler that
ranks candidates and enqueues — following `RetentionCleanupScheduler` /
`RetentionCleanupHandler`. It does not get its own claim, lease, retry, or concurrency
machinery. The API has one background-processing architecture and hydration joins it.

This is not only a consistency preference; the existing system already satisfies most of §8:

| §8 requirement | Provided by |
|---|---|
| §8.4.3 one proactive apply at a time | `JobTypes:<Type>:MaxConcurrency: 1` |
| §8.3.1 pause on conversation lock while chat + embeddings both use local AI | `ConversationLockJobGate.ShouldDeferJobType` — an exact match for the clause |
| §8.4.6 gate blocks starting; pauses rather than drains | The gate skips *claiming*; deferred rows stay `Pending` |
| §8.4.7 finish-current, no cancel | The gate runs before claim only; an executing handler is never interrupted by it |
| §8.4.4 bounded work per window | Scheduler enqueue rate + `MaxConcurrency` + `LeaseSeconds` |
| Failure handling without a tight retry loop | `JobExecutionResult` classes + `JobRetryPolicy` backoff |
| Multi-instance claim safety | Atomic `ExecuteUpdateAsync` claim + `ClaimToken` + lease |
| Operator off-switch (B10) | Per-type `Enabled` flag, as `RetentionCleanup` already does |

What is genuinely new: the §8.2 candidate/ranking query, the scoped-apply handler, the
§8.3.2 local-AI warmup signal added to the existing gate, and the shared `guideScopeId`
resolver. Ranking is carried as `JobQueue.Priority`, since claim order is `Priority`
descending then `Created` ascending — that makes §8.2.2 an executable contract and A8
gradeable from queue rows.

**Consequence to design around:** the gate defers by *not claiming*, so deferred rows
persist. A scheduler that re-enqueues the same scope each tick during a long chat session
builds a backlog of duplicates that grinds for a long time after chat ends. Guard
enqueue against an existing `Pending`/`Processing` row for that scope, as
`ProjectScheduledJobInFlightGuard` does.

### B2 — System under test

Compose selects the image via `GA_AI_*_IMAGE`; the running container's image is the system
under test. A flavor is done when that tag is running and its acceptance rows pass.

**Source:** §2.1.3, §6.2.3, §6.2.4.

### B3 — Package work never precedes bind, including in the entrypoint

The entrypoint starts SEA before, or concurrently with, the global admin bootstrap.
`reconcile.sh` does not stand between container start and SEA accepting connections.

**Derivation:** §3.1's preamble names the failure this rule exists to prevent — "package
work before bind → `/sandbox/*` connection refused while the process is 'up'". That defect
is observable at the container boundary; it does not matter whether the install runs inside
the SEA process or in the shell that launches it. §3.1.6 permits an entrypoint global
bootstrap and constrains what it may do — hash-gated against durable global applied-state,
at most that global set, never per-project scopes — but says nothing that exempts it from
§3.1.1–3.1.2's ordering. §4.2 then requires `/sandbox/health` to succeed only when SEA is
accepting, so a pre-bind bootstrap converts directly into an unhealthy window.

**Current state:** `entrypoint.sh:250-251` runs `reconcile.sh` synchronously; SEA starts at
`:380-385`. Warm cache is a no-op; cold bootstrap is an apt + pip install in front of bind.
This is gap G1.

**Consequence for the implementer:** `reconcile.sh` and `AdminStateRuntime.InitializeAsync`
both create global files. Reordering must make that seeding explicitly idempotent rather
than relying on the current ordering to avoid a race.

### B7 — Readiness is enforced by the health gate, not by a retry

The AI service is not ready until sandbox health succeeds (§4.4, first option). The API's
sandbox client gets no retry.

**Derivation:** §4.4 offers two ways to keep clients from hitting a listener that is not up.
The second requires distinguishing "before SEA accepted the request" from "after SEA began
executing the script," and getting that wrong runs a user's script twice. The first removes
the race outright rather than compensating for it. A compensating retry around a race that
a readiness gate eliminates is the failure-hiding pattern this repo prohibits outright.

### B8 — Health means sandbox is up

`GET /sandbox/health` succeeds only when SEA is accepting connections and reports healthy.
The health signal dependents consume requires sandbox health when sandbox is enabled in that
image. The current `HEALTHCHECK` OR-chain, which lets another endpoint report healthy while
SEA is dead, contradicts this.

**Source:** §4.2, §4.3.

### B10 — The hydration control plane ships on

The §8 worker is active by default. An operator off-switch exists for incident response;
the default is on.

**Derivation:** §8 opens "This section is **required** so cold runtime mounts recover
without recreating the startup pip storm." Part A assigns items 3 and 4 — when the host is
idle enough for proactive package work, and when hydration must pause — to the API. A
worker that is off by default leaves nothing owning them, which is the control plane not
existing. Shipping §8 disabled would deliver the constraint (SEA never walks the fleet)
without the capability that makes the constraint survivable (something recovers cold
runtimes).

The safety the spec asks for is in the **gating**, not in the master switch: one apply at a
time, budgeted per window, scoped only, paused whenever chat or warmup is active.

### B12 — In-flight apply when the host turns busy

Finish-current. No cancel contract.

**Source:** §8.4.7.

### B14 — Scope identity for hydration

Hydration uses the same `guideScopeId` resolution as `/execute` — guide / template id, not
ad hoc filesystem names.

**Source:** §8.2.5.

---

## Part C — Implementation choices

The requirement is binding; the mechanism is the implementer's and is named in the phase
report-back.

| Requirement (binding) | Source | Left to the implementer |
|---|---|---|
| Every flavor that will be run or published is built from the same SEA publish, verifiable after the fact rather than asserted. | §2.1.2 | Image label, env var, build manifest, or another mechanism. |
| At most one pip / install-script mutation runs against a given scoped venv at a time, across `/execute` and admin apply. `/health` stays servable during it. | §3.6.1, §3.6.2 | Lock type, key format, timeout policy. |
| SEA can report, for a scope the caller names, whether the runtime marker matches staged durable definitions. Durable audit alone is not sufficient after a cold runtime mount. No listing endpoint. | §3.7.1–3.7.3 | Whether this extends `/admin/setup-status` or adds a scoped endpoint; field names. |
| Dependents that require sandbox are gated on the health signal, not start order. | §4.3 | `depends_on: service_healthy`, an app-level readiness check, or both; which compose files actually have sandbox callers. |
| Candidates come from API entities; ranking uses API usage/recency; the filesystem is never consulted. | §8.2.1–8.2.3 | The query, recency window, and tie-breaking. Includes resolving how `UsageEvents.AssistantId` maps to the `/execute` scope id (`Notebooks.GuideId ?? NotebookTemplateId`) — they are not guaranteed equal, and B14 requires they agree. |
| The idle gate treats active conversation locks (with local-AI eligibility) and local-AI warmup/apply as busy. | §8.3.1–8.3.2 | Whether llama alias load/unload locks also count as busy — §8.3.3 marks this **optional**. Including them is the more conservative reading, since alias load is when the host can least absorb a pip install. |
| Hydration honors a per-window scope budget and a concurrency cap of one. | §8.4.3–8.4.4, B16 | Tick interval, budget value, `LeaseSeconds`. Derive these from the scoped-apply duration observed in Phase 4 rather than from intuition, and state the basis. |
| Deferred hydration work does not accumulate as a duplicate backlog. | B16 | Whether the guard is an in-flight query at enqueue, an `AvailableAt` expiry, a freshness re-check in the handler, or a combination. |
| Whether the §8.3.2 warmup signal gates all currently-gated job types or only hydration. | §8.3.2 | Either is defensible; adding a busy condition to existing extraction/indexing types changes their behavior, so decide deliberately and report. |

---

## Part D — Input needed from the product owner

| # | Input | Why it cannot be derived |
|---|---|---|
| D1 | Which flavors (`cpu`, `cuda13`, `rocm`, `slim`, `vulkan`) will be run or published from this work. | §2.1.2 states the rule — every such flavor is rebuilt from the same SEA publish — but the set is a release-plan fact. Absent an answer, pre-flight uses the flavor the active `GA_AI_*_IMAGE` in `docker/.env` points at, and Phase 6 reports which flavors were not rebuilt. |

---

## Part E — Invariants

Checked on every diff.

- **Bind-first.** Before the listener binds, SEA may only read config, validate admin
  definition files, and create state and runtime root directories (§3.1.1–3.1.2). Per B3
  this constrains the container, not merely the process.
- **No startup fleet reconcile.** Nothing inside SEA enumerates durable scopes as a
  consequence of process start, health, or a fresh runtime volume — not awaited, not
  `Task.Run` (§3.1.4–3.1.5).
- **Runtime root is required.** Missing or blank `SCRIPT_EXECUTION_SCOPE_RUNTIME_ROOT`
  fails startup (§3.1.3).
- **Additive only.** Applying requirements installs or upgrades declared packages and
  leaves everything else in place, including packages visible only through the base
  runtime link (§3.3.3–3.3.4).
- **Hash-only dirtiness.** Undeclared top-level packages are not a dirtiness signal and do
  not trigger uninstall (§3.3.5).
- **Single-scope mutation.** `/execute` and scoped apply mutate exactly one scope.
  Multi-scope work happens only on explicit operator global apply (§3.4.6, §3.5.4).
- **Automated callers never call global apply** (§3.5.6, §8.4.5).
- **SEA is not a control plane.** No ranking, idle detection, or chat awareness in SEA
  (§3.6.5, §8.1).
- **The API is not an inventory crawler.** The API never discovers scopes by walking SEA
  durable or runtime directories (§1.2, §8.1, §8.2.3).
- **Durable audit is not runtime readiness** after a cold runtime mount (§3.7.3).
- **On-demand hydrate is never idle-gated** (§3.4.7, §8.3.4).
- **The idle gate blocks starting, and pauses rather than drains** (§8.3.1, §8.4.6).
- **Staging is not applying** (§8.5.1). **Operator apply is not idle-gated**, but is still
  scoped unless the operator chooses global (§8.5.2).
- **One background-processing architecture.** Hydration is a `JobQueue` type with an
  `IJobHandler` and a scheduler that enqueues. No parallel claim, lease, retry, or
  concurrency machinery (B16).

---

## Part F — Non-goals

From the spec:

- Cancelling an in-flight scoped apply (§8.4.7).
- A SEA endpoint that lists or enumerates scopes for the API (§3.7.2).
- Moving venvs onto the durable share (§2.4 — Azure Files cannot host them).

Proposed for this work, subject to rejection:

- Coordination beyond what `JobQueue`'s atomic claim and lease already provide. The queue
  makes multiple API instances safe to run; cross-host scheduling policy is not in scope.
- Replacing `reconcile.sh` with in-process global bootstrap.
- Migrating the existing `applied-state.json` audit format.
- Any Azure Container Apps change — that lane is
  [`docs/azure-deploy-execution`](../azure-deploy-execution/00-orchestration.md).
