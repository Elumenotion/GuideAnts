# Local-AI Regression Recovery — Execution & Orchestration Guide

Last updated: 2026-08-04

This is the **conductor** document for implementing
[`docs/local-ai-regression-recovery-spec.md`](../local-ai-regression-recovery-spec.md)
(hereafter **the spec**) to completion.

The spec defines three things that must all be true at the same time:

1. **ScriptExecutionAgent (SEA) is bind-first and single-scope.** It serves
   `/sandbox/health` and `/sandbox/execute` immediately, and it never walks the fleet of
   durable scopes on its own — not at startup, not after a cold runtime mount.
2. **The image and runtime contract is complete.** Every AI flavor ships the same SEA
   publish with the same env defaults and the same durable/runtime mount split.
3. **Recovery from a cold runtime mount is API-owned.** The GuideAnts API ranks candidate
   scopes from product data, gates on idle, and drives **scoped** admin applies one at a
   time. SEA never ranks, never detects chat activity, and never invents a fleet walk.

> **Audience split**
>
> - **Implementer / reviewer** read this file, [`DECISIONS.md`](./DECISIONS.md),
>   [`STATUS.md`](./STATUS.md), [`sandbox-gate.md`](./sandbox-gate.md), and
>   [`acceptance-evidence.md`](./acceptance-evidence.md).
> - **Phase work** is defined in `task-phase-*.md` briefs. Execute phases in order;
>   each phase's Definition of Done must pass before starting the next.

> **Source control is out of scope for this folder.** Branching, commits, and pull
> requests are the repo owner's, not the implementer's. These documents define phases,
> gates, and evidence — nothing about how or when work is landed.

---

## 0. How to use this folder

| File | Purpose |
|------|---------|
| `00-orchestration.md` (this) | Scope, baseline, phase order, gates, deviation protocol, final acceptance. |
| `DECISIONS.md` | Control model (§1.1/§8.1), spec-derived requirements, implementation choices, invariants. |
| `STATUS.md` | Living ledger: baseline, per-phase state, gate results, deviations. |
| `sandbox-gate.md` | Runtime gate: proves A1–A8 on a real running stack. |
| `task-phase-1-sea-runtime-invariants.md` | SEA §3.1/§3.6/§3.7 invariants + §6.1 test coverage. |
| `task-phase-2-image-payload-contract.md` | §2.1/§2.2/§3.1.6 shared payload, env defaults, entrypoint bootstrap. |
| `task-phase-3-readiness-contract.md` | §4 readiness: health signal, dependent gating, bounded connect retry. |
| `task-phase-4-runtime-acceptance-a1-a5.md` | §6.2/§6.3 build + run active flavor, prove A1–A5. |
| `task-phase-5-api-hydration-control-plane.md` | §8 API-owned ranked, idle-gated, scoped hydration. |
| `task-phase-6-hydration-acceptance-docs.md` | A6–A8, §6.4 control-plane verification, docs, remaining flavors. |
| `acceptance-evidence.md` | Captured commands and outputs for review. |

Each task brief: Mission → Read first → Preconditions → Guardrails → Tasks → Files in
scope → Self-verification → Definition of Done → Report-back contract.

---

## 1. Problem statement (why this work exists)

The spec exists because SEA has already failed in production **in both directions**, and
because a cold runtime mount currently has no safe recovery path.

| Failure mode | Symptom | Spec clause |
|---|---|---|
| Package work **before** bind | `/sandbox/*` connection refused while the process is "up"; tool calls fail as transport errors | §3.1.1–3.1.2 |
| Package work **after** bind across every known scope | Green `/health` while a pip storm contends with llama / ASR / SD on one machine; chat becomes unusable | §3.1.4, §3.6 |
| Durable state used as inventory | Orphan and phantom scopes; contradictory "ready" vs empty-runtime signals | §1.2, §8.1 |
| Cold runtime mount, no policy | Every scope's venv is gone; nothing rehydrates until a user happens to run a tool | §2.4, §8 |

**Target:** A newly built AI image serves sandbox health immediately, hydrates exactly the
one scope a tool actually needs, and recovers the rest of the fleet slowly and invisibly
under API-owned idle policy — never as a startup storm.

### 1.1 What is already true (verified baseline, do not re-implement)

The implementer must **confirm** these before Phase 1 and record them in `STATUS.md`.
They are as-built today and the work must not regress them.

| Spec clause | As-built | Evidence |
|---|---|---|
| §2.2 image env defaults | Present and identical in all 5 flavors | `docker/build/guideants-ai/Dockerfile.cpu:223-226`, `.cuda:230-233`, `.rocm:314-317`, `.slim:123-126`, `.vulkan:318-321` |
| §2.3 mounts | `script_agent_admin_state`, `script_agent_runtime`, ContentFiles bind | `docker/docker-compose.cpu.yml:36-48`, volumes `:313-316` |
| §3.1.3 runtime root required | Startup throws when unset/blank | `src/server/ScriptExecutionAgent/Program.cs:57-60` |
| §3.1.4 no startup fleet walk | No `IHostedService` / `BackgroundService` / warmup in SEA; `EnumerateExistingScopes` is called only from global preflight/apply | `Program.cs:2363-2388`, `:2634-2641`, `:2831-2844` |
| §3.3.3–3.3.5 additive, hash-gated | No pip uninstall path anywhere in SEA; dirtiness is hash-only | `Program.cs:1837-1908`; `ScopeRuntimeAppliedStateRuntime.cs` |
| §3.4 on-demand provisioning | `/execute` → `EnsurePythonVenvAsync` → `EnsureScopeRequirementsForExecutionAsync`, single scope | `Program.cs:1127-1146`, `:1809-1835` |
| §3.5 admin apply | Scoped + global, preflight-then-202, job polling | `AdminApplyJobRuntime.cs:36-85`; routes `Program.cs:665-747` |
| §3.1.6 entrypoint bootstrap is global + hash-gated | `reconcile.sh` seeds/validates global files, hash-gates apt and pip, never enumerates `scopes/` | `docker/build/guideants-ai/script-agent-admin/reconcile.sh:103-179` |

### 1.2 What is missing (this work)

| # | Gap | Spec clause | Owning phase |
|---|---|---|---|
| G1 | Entrypoint runs `reconcile.sh` **synchronously before** SEA starts, so a cold global bootstrap delays bind | §3.1.1–3.1.2, §3.1.6 | Phase 2 |
| G2 | No cross-path mutual exclusion between `/execute` hydrate and `/admin/apply` on the **same** scope | §3.6.1 | Phase 1 |
| G3 | `/admin/setup-status` answers from the **durable audit** only; it cannot say "runtime hydrated" after a cold runtime mount | §3.7.1, §3.7.3 | Phase 1 |
| G4 | Dockerfile `HEALTHCHECK` is an **OR-chain** — the container can report healthy with sandbox down | §4.2–4.3 | Phase 3 |
| G5 | Dependents use `depends_on: service_started`, not health | §4.3 | Phase 3 |
| G6 | No bounded connect-only retry on the API's sandbox client | §4.4 | Phase 3 |
| G7 | Required §6.1 test coverage is only partially present | §6.1 | Phase 1 |
| G8 | No proof that all flavors were built from **one** SEA publish | §2.1.2 | Phase 2 |
| G9 | §8 API control plane absent: no candidate ranking, no hydration job type, and the existing defer gate lacks §8.3.2's local-AI warmup signal | §8.1–8.6 | Phase 5 |
| G10 | Three duplicate `guideScopeId` resolvers; hydration ranking could select a **different** id than `/execute` uses | §8.2.5 | Phase 5 |

---

## 2. Pre-flight (once, before Phase 1)

- [ ] **Read `DECISIONS.md` Part A (the control model) first.** The API is the sole source
      of truth for which scopes exist, which are worth hydrating, when the host is idle,
      and when hydration must pause. SEA executes named work. Most defects in this area are
      ownership errors, not coding errors.
- [ ] **Confirm the flavor set** (`DECISIONS.md` D1). Absent an answer, use the flavor the
      active `GA_AI_*_IMAGE` in `docker/.env` points at.
- [ ] **Read the spec end to end.** It is normative; this folder is only the execution plan.
- [ ] **Read SEA source:**
  - `src/server/ScriptExecutionAgent/Program.cs` (startup `:11-108`, bind `:751-753`, execute `:116-217`, admin `:468-749`)
  - `AdminApplyJobRuntime.cs`, `AdminSetupStatusRuntime.cs`,
    `ScopeRuntimeAppliedStateRuntime.cs`, `AdminScopeAppliedStateRuntime.cs`
- [ ] **Read API sandbox surfaces:**
  - `src/server/GuideAntsApi/Services/NotebookDockerScriptService.cs` (`ResolveGuideScopeIdAsync:355-396`)
  - `src/server/GuideAntsApi/Services/Mcp/McpSandboxAdminApiClient.cs`
  - `src/server/GuideAntsApi/Endpoints/SystemGuideEndpoints.cs`
- [ ] **Read the existing idle-gate precedent:**
  - `src/server/GuideAntsApi.BackgroundJobs/ConversationLockJobGate.cs`
  - `src/server/GuideAntsApi/Services/ConversationLockGate/ConversationLockGateEligibility.cs`
  - `src/server/GuideAntsApi.BackgroundJobs/BackgroundJobProcessor.cs:179-186`
  - `src/server/GuideAntsApi/Services/Bootstrap/LocalAiStartupWarmupService.cs`
- [ ] **Read the image lane:**
  - `docker/build/build_guideants_ai.ps1` (slice `:44-68`, SEA publish `:317-375`, tags `:291-295`, `.env` write `:493-504`)
  - `docker/build/guideants-ai/entrypoint.sh` (`:250-251`, `:380-385`, `:400`) and `entrypoint.slim.sh`
  - `docker/build/guideants-ai/nginx.conf:58-68`
- [ ] **Capture baseline** in `STATUS.md`:
  - `cd src/server && dotnet build GuideAntsApi.sln`
  - `cd src/server && dotnet test GuideAntsApi.sln`
  - `docker compose -f docker/docker-compose.<active>.yml ps` and the current
    `guideants-ai` **image ID** (this is the system under test, per §2.1.3)
  - Current value of the active `GA_AI_*_IMAGE` in `docker/.env`
- [ ] **Confirm §1.1 as-built table** by inspection; note any row that no longer holds.
---

## 3. Dependency graph (implementation order)

```text
Phase 1  SEA runtime invariants
         (§3.1 bind-first proof, §3.6.1 per-scope mutation mutex,
          §3.7 runtime-aware scoped status, §6.1 test matrix)
              │
              ▼
Phase 2  Image + payload contract
         (§2.1 one SEA publish across flavors, §2.2 env defaults verified,
          §3.1.6 entrypoint bootstrap must not delay bind)
              │
              ▼
Phase 3  Readiness contract
         (§4.2 health means SEA accepting, §4.3 dependents gate on it,
          §4.4 bounded connect-only retry — one option, not both)
              │
              ▼
Phase 4  Runtime acceptance A1–A5
         (§6.2 build active flavor, §6.3 recreate on new tag,
          prove A1–A5 with captured evidence)
              │
              ▼
Phase 5  API hydration control plane
         (§8 shared guideScopeId resolver, ranked candidates from UsageEvents,
          hydration as a JobQueue type + scheduler, §8.3.2 signal on the
          existing defer gate)
              │
              ▼
Phase 6  Hydration acceptance + docs + remaining flavors
         (A6–A8, §6.4 matrix, §2.1.2 rebuild remaining flavors,
          operator/consumer docs)
```

**Rules:**

- A phase is not done until its gate (section 4) passes on the current tree.
- Phases are sequential. Phase 5 depends on Phase 1's scoped runtime status (§3.7)
  because candidate selection needs a truthful "runtime hydrated?" answer, and on Phase 3's
  readiness contract because it must not fire applies at an unready agent.
- Phase 4 is a **hard stop**: no §8 work starts until A1–A5 are green on a real stack.

---

## 4. Verification gates

### 4.1 Global invariants (every phase)

- [ ] `cd src/server && dotnet build GuideAntsApi.sln` — 0 errors; warnings not worse than baseline.
- [ ] `cd src/server && dotnet test GuideAntsApi.sln` — no new failures vs baseline.
- [ ] **No fleet walk added.** `EnumerateExistingScopes` (and any successor) has no new
      caller reachable from process start, health, warmup, or a background timer inside SEA.
      Verify by call-site inspection, not by intent.
- [ ] **No pip uninstall / prune path added** to scoped apply (§3.3.4).
- [ ] **No new source of truth.** No API code path discovers scopes by listing SEA
      durable or runtime directories (§1.2, §8.1).
- [ ] **Matches `DECISIONS.md`.**
- [ ] **No container recreate performed without explicit user approval** in the message
      that requested it (repo rule). Ask, then wait.

### 4.2 Phase 1 — SEA runtime invariants

- [ ] `dotnet test src/server/ScriptExecutionAgent.Tests/ScriptExecutionAgent.Tests.csproj` green.
- [ ] Every §6.1 required-coverage row has a named test (table in the Phase 1 brief).
- [ ] A test asserts process start applies **no** scope, with ≥2 durable scopes staged and
      an empty runtime root.
- [ ] A test asserts concurrent `/execute` and scoped `/admin/apply` on the **same** scope
      serialize; on **different** scopes they do not deadlock.
- [ ] `/admin/setup-status?projectId=&guideId=` reports a **runtime** hydration field
      distinct from the durable audit, and reports `needs apply` when the runtime marker
      is missing but the durable audit says applied.
- [ ] `/health` still returns before any package work in an in-process factory test.

### 4.3 Phase 2 — Image + payload contract

- [ ] `docker/build/build_guideants_ai.ps1 -Backend <active>` succeeds.
- [ ] `docker run --rm --entrypoint env <tag> | Select-String SCRIPT_EXECUTION_` shows all
      four §2.2 variables with the spec values.
- [ ] The image records the **SEA publish identity** it was built from, and the same value
      is reproduced for a second flavor built from that publish (§2.1.2).
- [ ] SEA process start is **not** blocked by a cold global bootstrap (B3), and
      `reconcile.sh` still never enumerates `scopes/`.
- [ ] `docker/.env` `GA_AI_*_IMAGE` points at the dated tag for the flavor that will be run (§6.2.3).

### 4.4 Phase 3 — Readiness contract

- [ ] The AI container health signal used by dependents **requires** sandbox health when
      sandbox is enabled in that image (no OR-chain that can mask a dead SEA).
- [ ] Exactly **one** of §4.4's two options is implemented (decision B7) — not both, and
      not an unbounded retry.
- [ ] If the retry option is chosen: retries are limited to connection-refused /
      bad-gateway **before** the execute request is accepted, are bounded by a deadline,
      and **never** re-issue a script that SEA already began executing.
- [ ] Compose dependents that require sandbox gate on health, and the change is applied to
      every compose file that starts `guideants-ai`.

### 4.5 Phase 4 — Runtime acceptance A1–A5

Defined in [`sandbox-gate.md`](./sandbox-gate.md) §2. Pass when A1–A5 are green on a
stack running the newly built image, with the image ID recorded.

### 4.6 Phase 5 — API hydration control plane

- [ ] Hydration is a `JobQueue` type with an `IJobHandler` plus a scheduler that enqueues
      (B16). **No** bespoke claim, lease, retry, or concurrency code was written.
- [ ] One shared `guideScopeId` resolver; the three former duplicates call it, and
      candidate selection uses the **same** resolver (§8.2.5).
- [ ] Candidate list is built from API entities + `UsageEvents` recency. A unit test proves
      a durable folder with no API entity is **not** a candidate (§8.2.3), and an
      API-known guide with no runtime tree **is** one (§8.2.4).
- [ ] Rank reaches the queue as `Priority`, so claim order reproduces §8.2.2 ranking.
- [ ] Idle gating rides `ConversationLockJobGate` (§8.3.1 already matches the clause), with
      the §8.3.2 local-AI warmup signal added to it — not a second gate, and not a check
      inside the handler (§8.3.5). A unit test proves a busy gate leaves hydration rows
      `Pending` and does **not** block `/execute` hydrate (§8.3.4).
- [ ] Existing gated job types are unaffected by the gate change.
- [ ] Hydration calls **scoped** apply only; the payload requires both ids so an unscoped
      apply is unconstructible (§8.4.5).
- [ ] Concurrency cap = 1 via `MaxConcurrency`, not a hand-rolled semaphore; per-window
      budget honored by the scheduler (§8.4.3–8.4.4).
- [ ] Pause-not-drain (§8.4.6) and finish-current (§8.4.7, B12) **verified** for the new job
      type — these are existing processor properties, not new code.
- [ ] Deferred rows do not accumulate as duplicates across many busy ticks (B16 hazard).
- [ ] Job type is **enabled by default** with an operator off-switch (B10). Safety comes from
      the gating — one at a time, budgeted, scoped, paused on busy — not from shipping it off.

### 4.7 Phase 6 — Hydration acceptance + docs

- [ ] A6–A8 green per `sandbox-gate.md` §3.
- [ ] §6.4 control-plane matrix green (all five rows).
- [ ] Remaining flavors rebuilt from the same SEA publish before they are run or published (§2.1.2).
- [ ] Operator documentation covers: what proactive hydration is, how to disable it, how
      it differs from operator apply and from MCP staging (§8.5).
- [ ] `acceptance-evidence.md` complete.

---

## 5. Anti-goals (things that will fail review even if they work)

These are the specific wrong turns this spec was written to prevent. Do not implement
them, and reject them in review.

| Anti-goal | Why it fails |
|---|---|
| A SEA `BackgroundService` that "repairs" scopes after start | §3.1.4 — this is the exact regression the spec exists to stop. Recreate must not trigger fleet work. |
| Making SEA rank scopes, read usage, or detect chat | §3.6.5, §8.1 — SEA is an executor, not a control plane. |
| Hydration calling **global** `POST /admin/apply` | §8.4.5 — one global call re-creates the storm. |
| A standalone hydration `BackgroundService` with its own claim / lease / retry / concurrency logic | B16 — the API has one background-processing architecture; a second one is a maintenance and correctness liability, and duplicates behavior the queue already gets right. |
| Discovering candidates by listing `SCOPE_STATE_ROOT` on disk | §1.2, §8.2.3 — produces orphan and phantom scopes. |
| Treating `applied-state.json` (durable audit) as proof of runtime readiness | §3.7.3 — it survives a cold runtime mount and lies. |
| Uninstalling packages not named in `requirements.txt` | §3.3.4 — additive is a hard contract. |
| A "fallback" path that silently retries after SEA began executing a script | §4.4 — duplicate side effects; also violates repo rule against fallback logic. |
| Skipping bind-first because "reconcile is fast when warm" | §3.1.1 — cold is the case that breaks. |
| Widening the retry in §4.4 to cover post-accept failures | §4.4 — never retry after execution started. |

---

## 6. Deviation & failure protocol

When a gate fails, **stop the line** — do not start the next phase.

1. **Classify** in `STATUS.md`:
   - `bind regression` → something re-introduced work before `app.StartAsync()`.
   - `fleet walk` → a new caller reaches scope enumeration from a non-operator path.
   - `additive violation` → an uninstall/prune appeared in scoped apply.
   - `source-of-truth violation` → filesystem used as inventory, or SEA ranking.
   - `readiness gap` → health green while `/sandbox/health` fails, or dependent not gated.
   - `payload drift` → a flavor built from a different SEA publish than the active one.
   - `idle-gate leak` → proactive apply started while chat/warmup busy.
   - `scope creep` → revert or update brief + DECISIONS.
2. Fix in the **owning phase**; re-run the **full** gate for that phase.
3. Record attempt + fix in the `STATUS.md` deviation log, including the image ID under test.
4. Do not land partial work on `main`.

**Container discipline:** every runtime gate needs a recreate. Prepare the change, state
that a recreate is required and why, and **wait for explicit approval**. Do not bounce a
container "to verify" mid-task.

---

## 7. Final acceptance

The job is complete only when **all** hold:

- [ ] Phases 1–6 marked `DONE` in `STATUS.md`.
- [ ] §6.1 automated coverage green; every required row has a named test.
- [ ] A1–A5 green on the active flavor, with image ID and timestamps captured.
- [ ] A6–A8 green with the control plane enabled.
- [ ] §6.4 control-plane matrix green.
- [ ] All flavors that will be run or published are built from the **same** SEA publish (§2.1.2).
- [ ] §8.6 preserved: SEA is still bind-first, still has no startup fleet reconcile, still
      additive and hash-gated, still single-scope unless an operator asks for global.
- [ ] Anti-goals (section 5) audited — none present in the diff.
- [ ] `acceptance-evidence.md` captured.
- [ ] Work handed to the repo owner for review.

---

## 8. Report-back contract (final handoff to user)

```text
LOCAL-AI REGRESSION RECOVERY — FINAL REPORT
Image under test: <repo:tag> (<image id>)

BASELINE:
- Server build/test: <pass + counts>
- SEA tests: <pass + counts>

PHASES:
- Phase 1 SEA runtime invariants: <DONE + notes>
- Phase 2 Image + payload contract: <DONE + notes>
- Phase 3 Readiness contract: <DONE + option chosen>
- Phase 4 Runtime acceptance A1-A5: <DONE + notes>
- Phase 5 API hydration control plane: <DONE + notes>
- Phase 6 Hydration acceptance + docs: <DONE + notes>

ACCEPTANCE (spec section 5):
- A1 sandbox health: <pass/fail>
- A2 trivial execute: <pass/fail>
- A3 no reconcile storm for 5 min: <pass/fail + how observed>
- A4 short chat completion: <pass/fail | n/a for flavor>
- A5 first execute hydrates, second skips: <pass/fail>
- A6 cold mount, no startup walk: <pass/fail>
- A7 conversation lock blocks proactive, not on-demand: <pass/fail>
- A8 candidate order follows API ranking: <pass/fail>

CONTROL PLANE (spec section 6.4):
- Source of truth / Ranking / Idle gate / Cap / No global apply: <pass/fail each>

INVARIANTS:
- Bind-first preserved: <pass/fail>
- No startup fleet reconcile: <pass/fail>
- Additive, hash-gated only: <pass/fail>
- Single-scope unless operator global: <pass/fail>
- All run/published flavors from one SEA publish: <pass/fail>

DEVIATIONS: <none | list from STATUS.md>

FILES CHANGED (high level):
- src/server/ScriptExecutionAgent/...
- src/server/GuideAntsApi/...
- docker/build/guideants-ai/...
- docs/local-ai-regression-execution/...

RECOMMENDED OPERATOR SMOKE:
1. Recreate guideants-ai on the new tag (approved)
2. curl /sandbox/health, POST /sandbox/execute trivial script
3. Watch 5 min: no multi-scope pip activity
4. With hydration enabled, confirm jobs stay `Pending` during chat and drain after
```
