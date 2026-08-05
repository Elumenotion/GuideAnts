# Phase 5 — API Hydration Control Plane

**Depends on:** Phase 4 `DONE` (A1–A5 green on a real stack)
**Blocks:** Phase 6

> **This phase builds the control plane itself.** Read `DECISIONS.md` Part A before
> anything else. The API is the sole source of truth for which scopes exist, which are
> worth hydrating next, when the host is idle, and when hydration must pause. Every defect
> this phase can produce is a violation of one of those four.

---

## Mission

Implement spec §8: recover cold SEA runtimes by ranking candidate scopes from **product
data**, gating on **idle**, and driving **scoped** admin applies **one at a time** — without
making SEA a second source of truth and without recreating the startup pip storm.

**Hydration is a job type in the existing background-job architecture.** It is not a new
worker, not a bespoke loop, and not a parallel scheduling mechanism. The API already has a
durable queue with typed handlers, leases, retries, per-type concurrency caps, and a
chat-aware defer gate. §8 is very close to a description of what that system already does.
Build inside it.

---

## The existing architecture is most of §8

Read this before designing anything. Each row is a §8 requirement that the job system
**already satisfies** once hydration is a job type — the work is wiring, not invention.

| §8 requirement | Already provided by | Where |
|---|---|---|
| §8.4.3 at most one proactive apply at a time | `JobTypes:<Type>:MaxConcurrency` semaphore | `BackgroundJobProcessor.cs:36-41` |
| §8.3.1 pause while a conversation lock is held **and** chat + embeddings both use local AI | `ConversationLockJobGate.ShouldDeferJobType` — the condition is an exact match for §8.3.1 | `ConversationLockJobGate.cs:5-15`, `BackgroundJobProcessor.cs:135-149`, `:179-186` |
| §8.4.6 the gate blocks **starting**, and pauses rather than drains | The gate skips *claiming*; deferred rows stay `Pending` and are picked up in a later cycle | `BackgroundJobProcessor.cs:135-149` |
| §8.4.7 finish-current, no cancel contract | The gate is consulted before claim only; an executing handler is never interrupted by it | same |
| §8.4.4 bounded work per window | Scheduler enqueue rate + `MaxConcurrency` + `LeaseSeconds` | `appsettings.json` `BackgroundJobs:JobTypes` |
| Apply timeout | Lease + `CancellationToken` passed to the handler; lease-renewal failure cancels | `BackgroundJobProcessor.cs` renewal loop |
| Failure handling without a tight retry loop | `JobExecutionResult.RetryableTransient` + `JobRetryPolicy` backoff, `MaxAttempts` | `JobExecutionResult.cs`, `JobQueueService.cs` |
| Multi-instance safety | Atomic `ExecuteUpdateAsync` claim + `ClaimToken` + lease | `JobQueueService.cs:62-79` |
| Operator off-switch (B10) | Per-type `Enabled` flag precedent | `BackgroundJobs:JobTypes:RetentionCleanup:Enabled` |
| A system-level recurring producer that ranks and enqueues | `RetentionCleanupScheduler` — enqueues one job per candidate on a timer | `BackgroundJobs/Services/RetentionCleanupScheduler.cs:66-94` |

**What is genuinely new in this phase:** the candidate/ranking query (§8.2), the scoped-apply
handler, extending the defer gate with §8.3.2's local-AI warmup signal, and the shared
`guideScopeId` resolver.

Two structural facts about the queue to design around:

- `JobQueue` has **no cron or recurrence fields**. Recurrence is expressed by a separate
  `BackgroundService` that enqueues on a tick. `RetentionCleanupScheduler` is the
  system-scoped precedent; `ProjectScheduledJobScheduler` is the project-cron one. Follow
  `RetentionCleanupScheduler`.
- Claim order is `Priority` descending, then `Created` ascending
  (`JobQueueService.cs:62-79`). **Rank is `Priority`.** That is how §8.2.2's ordering
  becomes an executable contract rather than a comment, and it is what makes A8 gradeable
  from queue rows rather than from log narration.

---

## Read first

- The spec §1.1, §8 (all of it), §6.4, §5 (A6–A8)
- `docs/local-ai-regression-execution/00-orchestration.md` §1.2 (G9, G10), §4.6, §5 (anti-goals)
- `docs/local-ai-regression-execution/DECISIONS.md` Part A, B10, B12, B14, Part C, Part E
- **The job system** (the substrate for this phase):
  - `src/server/GuideAntsApi.DataModel/Models/JobQueue.cs` (entity, `JobStatus`)
  - `src/server/GuideAntsApi.BackgroundJobs/IJobHandler.cs`, `JobHandlerBase.cs`,
    `JobExecutionResult.cs`
  - `src/server/GuideAntsApi.BackgroundJobs/BackgroundJobProcessor.cs`
    (loop `:69-95`, handler init + config validation `:97-120`, gate `:135-149`, `:179-212`,
    claim/execute `:214-323`)
  - `src/server/GuideAntsApi.BackgroundJobs/JobQueueService.cs` (`EnqueueAsync:31-43`,
    `TryClaimAsync:62-79`)
  - `src/server/GuideAntsApi.BackgroundJobs/ConversationLockJobGate.cs`,
    `ConversationLockGateOptions.cs`
  - `src/server/GuideAntsApi/Services/ConversationLockGate/ConversationLockGateEligibility.cs:23-31`
  - `src/server/GuideAntsApi.BackgroundJobs/Services/RetentionCleanupScheduler.cs`
    (**the model for the producer in task 4**)
  - `src/server/GuideAntsApi.BackgroundJobs/Jobs/JobPayloads.cs`,
    `TestJobHandler.cs`, `RetentionCleanupHandler.cs`
  - `src/server/GuideAntsApi/Services/Scheduling/ProjectScheduledJobExecutionHandler.cs:28-30`
    (the scoped-services-in-a-cached-handler note — read it, it is a real trap)
  - `src/server/GuideAntsApi/Services/Scheduling/ProjectScheduledJobInFlightGuard.cs`
    (duplicate-enqueue precedent)
- Existing API surfaces:
  - `src/server/GuideAntsApi/Services/NotebookDockerScriptService.cs` —
    `ResolveGuideScopeIdAsync:355-396`, base URL `:521-555`, auth `:486-501`
  - `src/server/GuideAntsApi/Services/Mcp/McpSandboxExecutor.cs:224-252` (duplicate resolver)
  - `src/server/GuideAntsApi/Services/Mcp/McpToolExecutor.cs:176-202` (duplicate resolver)
  - `src/server/GuideAntsApi/Services/Mcp/McpSandboxAdminApiClient.cs` — scoped query
    builder `:74-79`, `PostNoContentAsync:65-72`, admin auth `:96-117`
  - `src/server/GuideAntsApi/Endpoints/SystemGuideEndpoints.cs` — apply proxy `:205-230`
- Other busy signals:
  - `src/server/GuideAntsApi/Services/Bootstrap/LocalAiStartupWarmupService.cs:10-12`, `:57-63`
  - `src/server/GuideAntsApi/Services/Routing/ILlamaRuntimeCoordinator.cs:37-79`
- Data:
  - `src/server/GuideAntsApi.DataModel/Models/UsageEvent.cs` (`ProjectId`, `AssistantId`, `Created`)
  - `src/server/GuideAntsApi.DataModel/ApplicationDbContext.cs:56`, indexes `:370-414`

---

## Preconditions

- [ ] Phase 4 `DONE`; A1–A5 evidence captured
- [ ] Phase 1's scoped runtime status (§3.7) is available — the scheduler needs a truthful
      "is this scope's runtime hydrated?" answer, not the durable audit
- [ ] Scoped-apply duration observed in Phase 4, as the basis for `LeaseSeconds`, tick
      interval, and per-window budget

---

## Guardrails

- **No second architecture.** Hydration is a `JobQueue` type with an `IJobHandler` and a
  scheduler that enqueues. If you are writing your own claim, lease, retry, or concurrency
  logic, stop — it exists.
- The handler calls **scoped** apply only. It must be structurally impossible for it to
  issue an unscoped/global apply — not merely avoided by convention (§8.4.5).
- Candidates come from **API entities + usage**. No code path in this phase lists, stats,
  or globs SEA durable or runtime directories (§8.2.3, §1.2).
- SEA gains **no** ranking, idle detection, or chat awareness (§8.1). If you find yourself
  editing SEA in this phase, stop and re-read §8.1.
- **At most one** proactive scoped apply at a time — via `MaxConcurrency: 1`, not a
  hand-rolled semaphore (§8.4.3).
- The gate blocks **claiming** a hydration job; it never blocks `/execute` hydrate (§8.3.4).
- **Pause, do not drain** (§8.4.6) and **finish-current** (§8.4.7, B12) are properties the
  processor already has. This phase **verifies** them for the new type; it does not
  reimplement them.
- Worker ships **active** with an operator off-switch (B10). Safety is in the gating — one
  at a time, budgeted, scoped, paused on busy — not in shipping the control plane off.
- Do not change staging behavior: MCP / guide save still stages without applying (§8.5.1).
- Do not subject operator-triggered apply to the idle gate (§8.5.2).
- Do not regress existing gated job types. The gate change in task 3 is additive.

---

## Tasks

### 1. Extract one shared `guideScopeId` resolver (gap G10 — §8.2.5, B14)

Three copies of the same resolution logic exist today:
`NotebookDockerScriptService:355-396`, `McpSandboxExecutor:224-252`,
`McpToolExecutor:176-202`. §8.2.5 requires hydration to use **the same** scope identity as
`/execute`; three copies is how that quietly stops being true.

- Extract to a single service (for example `IGuideScopeResolver`) registered in DI.
- Resolution order stays exactly as today: `Notebooks.GuideId ?? Notebooks.NotebookTemplateId`
  for `(ProjectId, NotebookId)`, then the invocation's assistant id, else throw.
- Replace all three call sites. No behavior change — this is a pure extraction, and tests
  should prove the resolved id is unchanged for each existing path.
- The candidate builder uses this resolver, or the same entity query it is built from, when
  it maps a ranked usage row to a scope id.

> Do not "improve" the resolution order here. Changing which id a scope maps to would
> orphan every existing runtime tree.

### 2. Candidate set and ranking (§8.2; query and tiebreak per Part C)

A service that produces a ranked list of `(projectId, guideScopeId)`. It is consumed by the
scheduler in task 4 and is independently testable.

| Rule | Implementation |
|---|---|
| §8.2.1 candidates are API-known entities | Query product tables (projects, notebooks/guides/assistants), never the filesystem |
| §8.2.2 rank by usage/recency | API usage data. A recency-descending grouping over `UsageEvents` is the obvious starting point; `IX_UsageEvents_GuideUsageReport` on `(AssistantId, ProjectId, AgentInvocationId, Created)` may support it — verify the plan rather than assuming. Exact query is yours |
| §8.2.3 orphan folders are not candidates | Nothing reads SEA directories; satisfied by construction and asserted by a test |
| §8.2.4 API-known guide with no runtime is valid | Absence of a runtime tree must not exclude a candidate — it is the whole point |
| §8.2.5 identity matches `/execute` | Use the shared resolver from task 1 |
| Rank is carried, not just implied | Emit an ordinal the scheduler maps to `JobQueue.Priority`, so claim order reproduces rank |

Important subtlety: `UsageEvents.AssistantId` and the `/execute` scope id
(`Notebooks.GuideId ?? NotebookTemplateId`) are **not guaranteed to be the same value**.
Resolve deliberately and document the mapping you chose; a ranking that names an id
`/execute` would never use will hydrate the wrong tree and look like a no-op forever.

Skip candidates that scoped `/admin/setup-status` already reports as `hydrated` (Phase 1's
runtime section) so budget is not spent re-applying. That status call is a read for a
**named** scope, which §3.7 permits. It is not an inventory query.

### 3. Extend the defer gate with §8.3.2 (do not build a second gate)

`ConversationLockJobGate.ShouldDeferJobType` already implements §8.3.1 exactly:

```csharp
return options.Enabled
       && bothChatAndEmbeddingsUseLocalAi
       && hasActiveConversationLock
       && options.GatedJobTypes.Contains(jobType);
```

Adding the hydration job type to `ConversationLockGate:GatedJobTypes` satisfies §8.3.1 with
no new mechanism. What is missing is §8.3.2.

| Signal | Source | Required? | Status |
|---|---|---|---|
| Active conversation lock while chat and embeddings both use local AI | `ConversationLocks` unexpired rows + `ConversationLockGateEligibility.BothUseLocalAiAsync()` | Yes — §8.3.1 | **Exists** |
| Local-AI warmup or apply in progress | `LocalAiStartupWarmupService.IsWarmupInProgress` / `IsApplyInProgress` | Yes — §8.3.2 | **Missing** — not referenced from `GuideAntsApi.BackgroundJobs` today |
| Llama alias load/unload lock held | `ILlamaRuntimeCoordinator` alias locks | Optional — §8.3.3; see Part C | Missing |

Rules:

- Extend the **existing** gate so the new signals compose into `ShouldDeferJobType`'s
  answer. Do not add a second gate that the processor consults separately, and do not check
  busy-ness inside the handler.
- The warmup signal lives in `GuideAntsApi`, not `GuideAntsApi.BackgroundJobs`. Follow the
  `IConversationLockGateEligibility` precedent: declare a small interface in the
  BackgroundJobs project, implement it in the API project, register it in
  `StartupConfiguration.cs:432`.
- Whether §8.3.2 applies to **all** gated types or only hydration is yours to decide and
  report. Adding a new busy condition to existing extraction/indexing types changes their
  behavior; be deliberate rather than incidental.
- **Note the `options.Enabled` coupling.** Disabling `ConversationLockGate` today disables
  gating for every type. Once hydration rides on it, that switch also un-gates hydration.
  State this in the operator docs (Phase 6) — it is a real operational edge.
- Keep existing gated job types' behavior unchanged; regression-test them.

### 4. Hydration as a job type (§8.4)

Two pieces, mirroring `RetentionCleanupScheduler` + `RetentionCleanupHandler`.

#### 4a. The job type

| Step | Detail |
|---|---|
| Payload | A record carrying **both** ids, e.g. `HydrateScopeRuntimeJob(Guid ProjectId, Guid GuideScopeId)`. Two required GUIDs is the type-level guarantee that §8.4.5 asks for. |
| Handler | `JobHandlerBase<HydrateScopeRuntimeJob>`; `JobType` string must match the `appsettings` key **exactly** |
| Scoped services | Handlers are resolved once at startup and cached for process lifetime (`BackgroundJobProcessor.cs:97-120`). Take `IServiceScopeFactory`, not a `DbContext` or scoped client, as a constructor field. See the comment at `ProjectScheduledJobExecutionHandler.cs:28-30`. |
| DI | `services.AddJobHandler<HydrateScopeRuntimeHandler>()` in `StartupConfiguration.cs` alongside the others |
| Config | `BackgroundJobs:JobTypes:<JobType>` with `MaxConcurrency: 1` (§8.4.3) and `LeaseSeconds` ≥ the Phase 4 apply duration with headroom. **Startup throws** if a registered handler has no matching config entry — add it to `appsettings.json`, `appsettings.example.json`, and the Development variants. |
| Gate | Add the job type to `BackgroundJobs:ConversationLockGate:GatedJobTypes` and to `ConversationLockGateOptions`' defaults |

Handler body:

- Resolve the SEA admin client in a fresh scope; `POST /admin/apply` with **both** ids
  (`McpSandboxAdminApiClient` scoped query builder `:74-79`), then poll
  `GET /admin/apply/jobs/{jobId}` to a terminal state, bounded by the handler's
  `CancellationToken` (the processor cancels it on lease-renewal failure).
- Return `JobExecutionResult.Success()` on a succeeded apply.
- Return `PermanentMissingInput` when the scope no longer exists or has no staged
  definitions — a deleted guide must not retry five times.
- Return `RetryableTransient` only for genuine transport/5xx faults, and set a **low**
  `MaxAttempts`. This is opportunistic warming; a scope that fails is retried when the
  scheduler next ranks it, not by grinding through the retry ladder.
- If sandbox health is unavailable, do not spin: treat it as a skip and let the next window
  handle it.
- Log the two ids, the outcome, and the duration. A6–A8 are graded from these plus the
  queue rows.

#### 4b. The scheduler (producer)

A `BackgroundService` modeled on `RetentionCleanupScheduler` — it **only enqueues**, it does
not execute:

```text
if (!options.Enabled) return;                    // operator off-switch; default on (B10)
while (!stopping)
{
    wait TickInterval
    candidates = ranked list from task 2         // §8.2
    skip any scope that already has a Pending/Processing hydration row   // see hazard below
    take up to MaxScopesPerWindow                // §8.4.4
    enqueue one job per candidate, Priority = rank ordinal
}
```

- **Do not check the idle gate here.** The gate belongs at claim time, in the processor,
  where it already is. A scheduler-side check would be a second gate whose answer goes stale
  between enqueue and claim.
- **Duplicate-enqueue hazard — read this one.** The gate defers by *not claiming*; deferred
  rows stay `Pending`. A scheduler that re-enqueues the same scope every tick during a long
  chat session builds an unbounded backlog of identical jobs. `MaxConcurrency: 1` keeps it
  from becoming a simultaneous storm, but it will still grind through stale duplicates for
  a long time after the user stops chatting — the pip storm, rediscovered by a different
  route. Guard against it the way `ProjectScheduledJobInFlightGuard` does: query for an
  existing `Pending`/`Processing` row for that scope before enqueueing. A test must cover
  the "busy for many ticks" case explicitly.
- Consider whether a stale `Pending` hydration job should expire. A job enqueued because a
  scope ranked highly an hour ago may no longer be worth running. `AvailableAt` and a
  freshness check in the handler are both available; pick one and report it.

### 5. Configuration (§8.4.4; values per Part C)

Job-type settings go in the existing `BackgroundJobs:JobTypes` section. Scheduler settings
follow the `BackgroundJobs:ProjectScheduledJobs` shape.

| Setting | Where | Default | Meaning |
|---|---|---|---|
| `MaxConcurrency` | `JobTypes:<Type>` | `1` | §8.4.3 — one proactive apply at a time |
| `LeaseSeconds` | `JobTypes:<Type>` | implementer's, reported | Apply timeout; derive from Phase 4 |
| `Enabled` | `JobTypes:<Type>` | `true` (B10) | Operator off-switch, following the `RetentionCleanup` precedent |
| `TickIntervalSeconds` | scheduler section | implementer's, reported | How often to rank and enqueue |
| `MaxScopesPerWindow` | scheduler section | implementer's, reported | §8.4.4 budget — a budget is required, its value is not specified |
| `MinCandidateRecency` (optional) | scheduler section | unset | Ignore guides untouched for longer than this |

Defaults carry real cost on a shared host. Choose them from the observed duration of a
scoped apply in Phase 4, not from intuition, and state the basis in the report-back.

Behavioral settings live in configuration, not in new secret env vars. The admin token
continues to come from `ScriptExecution:AdminToken`.

If `ComposeEnvironmentContractTests` covers this surface, extend it consistently rather
than letting compose and `appsettings` drift.

### 6. Separation from staging and operators (§8.5)

- [ ] MCP / guide save still stages requirements and install-scripts **without** applying
      (`McpSandboxSetupStagingService`) — verify unchanged.
- [ ] Operator apply through the admin UI / proxy is **not** idle-gated, and remains scoped
      unless the operator explicitly chooses global (`SystemGuideEndpoints:205-230`).
      Operator apply does not go through the queue.
- [ ] Proactive hydrate does not replace either. Document the three paths side by side.

### 7. Tests (§6.4 in unit form)

| Behavior | Assertion |
|---|---|
| Source of truth | Candidate builder produces nothing for a durable folder with no API entity; a test with a fake/absent filesystem still yields candidates from the DB |
| Ranking | Given seeded `UsageEvents`, candidates come back in descending recency with deterministic tiebreak |
| Rank reaches the queue | Enqueued rows' `Priority` reproduces candidate rank, so claim order follows §8.2.2 |
| Identity | Ranked candidate's scope id equals the shared resolver's id for the same notebook/guide |
| Gate — lock | Unexpired conversation lock + local-AI eligibility → hydration job is **not claimed** and stays `Pending` |
| Gate — warmup | Warmup/apply in progress → not claimed (§8.3.2, the new signal) |
| Gate — alias lock | Alias lock held → not claimed (only if included per Part C) |
| Gate — execute unaffected | Gate busy does not change `/execute` hydrate behavior (§8.3.4) |
| Gate — no regression | Existing gated types defer exactly as before; ungated types (`Test`, `SyncNotebook`, `RebuildEmbeddings`, `RetentionCleanup`, `ProjectScheduledJobExecution`) still are not deferred |
| Cap | Two `Pending` hydration rows → only one is `Processing` at a time |
| Budget | Scheduler enqueues at most `MaxScopesPerWindow` per tick |
| Pause not drain | Gate turns busy → remaining rows stay `Pending`, none are claimed, none are failed |
| Finish-current | Gate turns busy while a hydration job is `Processing` → it runs to completion and nothing new is claimed (B12) |
| No duplicate backlog | Gate busy across many scheduler ticks → at most one `Pending` row per scope |
| No global apply | Payload requires both ids; every recorded admin request carries both |
| Startup config | Handler registered without a `JobTypes` entry throws (`BackgroundJobProcessorStartupValidationTests` idiom) |
| Default posture | With stock config the job type is enabled; setting its `Enabled: false` stops it |

Test idioms to follow: `BackgroundJobProcessorLockGateTests` (reflection-invoke
`ProcessAvailableJobsAsync` against an in-memory DB with a stubbed
`IConversationLockGateEligibility` and a `RecordingJobHandler`),
`ConversationLockJobGateTests` (pure gate unit tests), `RetentionCleanupHandlerTests` +
`BackgroundJobTestHelpers` (handler with in-memory DB and config),
`ProjectScheduledJobSchedulerTests` with `CapturingJobQueueService` (scheduler enqueue
assertions). Mock the SEA admin HTTP surface — no live agent in unit tests.

---

## Files in scope

| Action | Path |
|--------|------|
| Add | `src/server/GuideAntsApi/Services/.../GuideScopeResolver.cs` (shared resolver) |
| Modify | `src/server/GuideAntsApi/Services/NotebookDockerScriptService.cs` (use resolver) |
| Modify | `src/server/GuideAntsApi/Services/Mcp/McpSandboxExecutor.cs` (use resolver) |
| Modify | `src/server/GuideAntsApi/Services/Mcp/McpToolExecutor.cs` (use resolver) |
| Add | Hydration candidate/ranking service |
| Add | `src/server/GuideAntsApi.BackgroundJobs/Jobs/` — payload record + handler |
| Add | Scheduler (`RetentionCleanupScheduler` shape) |
| Modify | `src/server/GuideAntsApi.BackgroundJobs/ConversationLockJobGate.cs` + `ConversationLockGateOptions.cs` (§8.3.2 signal, gated-type default) |
| Add | Busy-signal interface in `GuideAntsApi.BackgroundJobs` + implementation in `GuideAntsApi` (`IConversationLockGateEligibility` precedent) |
| Modify | `src/server/GuideAntsApi/Configuration/StartupConfiguration.cs` (`AddJobHandler`, scheduler, options, busy-signal impl) |
| Modify | `src/server/GuideAntsApi/appsettings.json`, `appsettings.example.json`, Development variants (`JobTypes` entry, `GatedJobTypes`, scheduler section) |
| Modify | `src/server/GuideAntsApi/Services/Mcp/McpSandboxAdminApiClient.cs` (scoped-only worker surface) |
| Add | `src/server/GuideAntsApi.Tests/BackgroundJobs/...` (the table in task 7) |
| Modify | `src/server/GuideAntsApi.Tests/BackgroundJobs/ConversationLockJobGateTests.cs` and the processor gate tests (regressions) |

Out of scope: **all** SEA source (§8.1), Dockerfiles, compose health (Phase 3).

---

## Self-verification

```powershell
cd src/server; dotnet build GuideAntsApi.sln; dotnet test GuideAntsApi.sln
```

Audit by inspection:

```powershell
# no filesystem inventory of SEA state from the API
rg -n "script-agent-admin|script-agent-runtime|SCOPE_STATE_ROOT|SCOPE_RUNTIME_ROOT" src/server/GuideAntsApi
# hydration cannot call unscoped apply
rg -n "admin/apply|PostNoContentAsync|ForwardAsync" src/server/GuideAntsApi
# no parallel scheduling/claim machinery was invented
rg -n "BackgroundService|SemaphoreSlim|Timer" src/server/GuideAntsApi --glob "*Hydrat*"
# SEA untouched this phase
git diff --stat -- src/server/ScriptExecutionAgent
```

- [ ] Hydration is a `JobQueue` type with an `IJobHandler`; no bespoke claim, lease, retry,
      or concurrency code was written
- [ ] No API code enumerates SEA directories
- [ ] Every hydration-originated apply request carries `projectId` **and** `guideId`
- [ ] `git diff` shows **no** SEA changes in this phase
- [ ] Job type enabled by default; its `Enabled: false` stops it (B10)
- [ ] Existing gated background job types behave as before

---

## Definition of Done

- [ ] Phase 5 gate (orchestration §4.6) passes
- [ ] All task 7 tests green
- [ ] Solution build/test no worse than baseline
- [ ] `STATUS.md` updated: Phase 5 → `DONE`, gaps G9/G10 → closed; control-plane ledger
      "After Phase 5 (unit)" filled
- [ ] §8.6 re-checked: SEA is still bind-first, no startup fleet reconcile, additive and
      hash-gated, single-scope unless an operator requests global

---

## Report-back

```text
PHASE 5 COMPLETE
- Shared resolver: <type> ; call sites replaced: <3 + candidate builder>
- Job type: <JobType string>, payload <record>, handler <type>
- Job config: MaxConcurrency 1, LeaseSeconds <n>, Enabled default true
- Scheduler: <type>, tick <interval>, budget <n>/window, Priority = <rank mapping>
- Duplicate-enqueue guard: <mechanism>; stale-job handling: <mechanism>
- Candidate source: <tables/query>, ranking: <rule>, tiebreak <rule>
- AssistantId -> guideScopeId mapping decision: <what you chose and why>
- Gate: rides ConversationLockJobGate; added signals <warmup/apply | alias lock?>;
  applied to <hydration only | all gated types>
- Default basis: <observed scoped-apply duration from Phase 4 these were derived from>
- Apply path: scoped only via <method>; unscoped overload absent: <yes>
- Job polling: 202 -> GET /admin/apply/jobs/{id}, bounded by lease
- Retry posture: MaxAttempts <n>; Permanent vs Transient classification: <rule>
- Tests added: <list, mapped to the task 7 table>
- Bespoke scheduling/claim machinery written: <none>
- SEA diff in this phase: <empty>
- Deviations: <none | list>
```
