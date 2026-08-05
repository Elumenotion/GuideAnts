# Phase 6 — Hydration Acceptance, Remaining Flavors, Docs

**Depends on:** Phase 5 `DONE`
**Blocks:** Final acceptance

---

## Mission

Prove spec §5 A6–A8 and the §6.4 control-plane matrix on a real stack with the hydration
worker enabled; rebuild every remaining flavor from the same SEA publish (§2.1.2); and
document the three apply paths so an operator can tell them apart.

---

## Read first

- The spec §5 (A6–A8), §6.4, §7 (delivery sequence), §8.5, §8.6
- `docs/local-ai-regression-execution/sandbox-gate.md` §3
- `docs/local-ai-regression-execution/00-orchestration.md` §4.7, §5, §7
- `docs/local-ai-regression-execution/DECISIONS.md` Part A, B2, B10, B12, Part C, D1

---

## Preconditions

- [ ] Phase 5 `DONE`; unit-level §6.4 assertions green
- [ ] Phase 4 evidence still valid for the current image, or Phase 4 re-run after later changes
- [ ] At least 5 durable scopes with non-empty `requirements.txt` available for A6
- [ ] Ability to seed `UsageEvents` recency for A8 (or a known-recent guide to use)

---

## Guardrails

- **Container recreate requires explicit user approval** in the requesting message. A6
  additionally needs the **runtime volume cleared**, which is destructive — describe it
  precisely (durable state is preserved; only the executable runtime is discarded) and wait.
- Do not weaken any earlier invariant to make A6–A8 pass. If hydration cannot hit budget
  because the gate is busy, that is the gate working (§8.4.6), not a bug.
- Do not start a global admin apply during any observation window.
- Do not publish or run a flavor built from a different SEA publish than the active one (§2.1.2).
- Record the hydration job type and scheduler configuration for the run.

---

## Tasks

### 1. Rebuild remaining flavors from the same SEA publish (§2.1.2, §7.4)

For each flavor named in **D1**:

```powershell
pwsh ./docker/build/build_guideants_ai.ps1 -Backend <flavor>
docker inspect --format '{{index .Config.Labels "com.guideants.sea.publish"}}' <tag>
```

- [ ] Every built flavor reports the **same** SEA publish identity as the active flavor
      from Phase 4, using whatever mechanism Phase 2 built (§2.1.2)
- [ ] Each built image passes the §2.2 env verification
- [ ] Flavors outside D1 are explicitly listed as not rebuilt in the report

If a rebuild produces a different publish id, the SEA source changed mid-delivery.
Classify as `payload drift`, rebuild the active flavor too, and re-run Phase 4.

### 2. Run A6–A8 with hydration enabled

Follow [`sandbox-gate.md`](./sandbox-gate.md) §3 exactly.

| # | Requirement | What to capture |
|---|---|---|
| A6 | With many durable scopes and a cold runtime mount, SEA alone does not walk them at startup; any proactive hydrate is API-driven, scoped, and idle-gated | scope count, recreate details, 5-minute process/log samples, attribution of every observed package operation |
| A7 | While a conversation lock is held, no hydration job is claimed; on-demand `/execute` may still hydrate that one scope | `JobQueue` rows staying `Pending`, gate logs with the busy reason, plus a successful on-demand hydrate during the same window |
| A8 | Proactive hydrate candidate order follows API usage/recency ranking, not SEA directory mtime or enumerate order | seeded recency vs deliberately opposite directory mtimes, enqueued `Priority` values, and the actual claim/apply order |

A6 is the anti-regression check for the original incident: after a cold runtime mount,
**nothing** should hydrate except what a tool actually invoked or what a hydration job
deliberately selected. Attribute every single package operation you observe. An
unattributed operation fails the gate even if the stack looks fine.

A8 is easy to fake and easy to pass accidentally. Make the directory order *disagree* with
the usage order on purpose; otherwise the test proves nothing.

### 3. §6.4 control-plane verification on the running stack

| Behavior | Assertion | How to observe |
|---|---|---|
| Source of truth | Candidates from API/DB entities + usage, not filesystem enumeration | logged candidate list contains only API-known scopes; a durable-only orphan folder never appears |
| Ranking | Higher-recency guides hydrated before colder ones within budget | `Priority` values and claim order vs seeded recency |
| Idle gate | No hydration job claimed while busy | rows stay `Pending`; gate log lines with the busy reason |
| Cap | At most one hydration job `Processing` at a time; per-window budget honored | queue row states over time; enqueue count per tick ≤ configured budget |
| No global apply | Hydration never calls global `POST /admin/apply` | every hydration request carries both ids (agent-side log or proxy log) |

Record each row in `STATUS.md`'s control-plane ledger and in `acceptance-evidence.md`.

### 4. Re-confirm §8.6 (the point of the whole exercise)

With the control plane live, verify SEA's own invariants are intact:

- [ ] Bind-first: `/sandbox/health` still succeeds promptly after recreate (A1 re-check)
- [ ] No startup fleet reconcile: A6's observation window is the proof
- [ ] Additive, hash-gated: a scope applied by hydration still keeps its undeclared packages,
      and a second apply with unchanged definitions is a no-op
- [ ] Single-scope mutations unless an operator requests global: no hydration-initiated global apply
- [ ] Ordering and pause/resume live only in the API: no ranking or idle logic in the SEA diff

### 5. Documentation

Write for an operator who has never read the spec.

**Operator / product docs** (place alongside existing developer docs, e.g.
`docs/developer-config-guide.md` and/or a new focused page):

- What proactive hydration is and what problem it solves (cold runtime mount after recreate).
- That it runs by default, and the off-switch for incident response (B10), with config keys.
- That it is a background job type like the others, so the existing job configuration,
  logs, and queue table apply to it — including how to see pending hydration work.
- What "idle" means concretely — the gate signals as built — and why chat wins.
- That `BackgroundJobs:ConversationLockGate:Enabled` is a **global** switch: turning it off
  to unblock some other job type also un-gates hydration.
- The three distinct apply paths, side by side (§8.5):

| Path | Trigger | Idle-gated? | Scope |
|---|---|---|---|
| Staging (MCP / guide save) | Author saves | n/a — no apply happens | writes durable definitions only |
| Operator apply (admin UI) | Operator clicks | No (operator intent) | scoped unless operator chooses global |
| Proactive hydrate (background job) | Idle window | Yes | scoped, one at a time, budgeted |
| On-demand hydrate | A tool actually runs | No (§3.4.7) | that one scope |

- What an operator should expect to see in logs, and what would be alarming
  (multi-scope pip activity nobody asked for).
- How to disable hydration if it interferes.

**Update existing docs that describe the old behavior:**

- `src/server/ScriptExecutionAgent/README.md` — startup contract, runtime status section
- `docs/developer-config-guide.md` — entrypoint ordering, health gating, volume semantics
- `docs/script-execution-agent-admin-api-requirements-plan.md` — note where this delivery
  supersedes or extends it, rather than leaving two documents disagreeing

**Do not** rewrite `docs/local-ai-regression-recovery-spec.md`. It is the requirement. If
implementation revealed the spec is wrong somewhere, say so in the report and let the user
decide; do not silently edit the contract to match the code.

### 6. Final evidence pass

- [ ] `acceptance-evidence.md` complete for both A1–A5 and A6–A8 runs
- [ ] `STATUS.md` acceptance ledger row "After Phase 6" filled for A1–A8
- [ ] Control-plane ledger row "After Phase 6 (runtime)" filled
- [ ] Container recreate log complete, with approvals
- [ ] Deviation log complete

---

## Files in scope

| Action | Path |
|--------|------|
| Modify | `docker/.env` (image pins for rebuilt flavors) |
| Add/Modify | Operator documentation for proactive hydration |
| Modify | `src/server/ScriptExecutionAgent/README.md` |
| Modify | `docs/developer-config-guide.md` |
| Modify | `docs/script-execution-agent-admin-api-requirements-plan.md` (supersession note) |
| Modify | `docs/local-ai-regression-execution/STATUS.md` |
| Modify | `docs/local-ai-regression-execution/acceptance-evidence.md` |

Source changes are not expected. Any required source change belongs to its owning phase and
re-triggers that phase's gate plus Phase 4.

---

## Self-verification

- [ ] All rebuilt flavors report the same SEA publish id
- [ ] A6 five-minute window: every package operation attributed to an `/execute` or a
      named scoped apply from a hydration job
- [ ] A7: rows stayed `Pending` with a concrete busy reason logged; on-demand hydrate
      succeeded in the same window; no duplicate `Pending` rows per scope
- [ ] A8: seeded recency order ≠ directory order, and apply order followed recency
- [ ] §6.4 matrix: 5/5
- [ ] Docs reviewed by reading them as an operator: could someone disable this feature
      using only the doc?

---

## Definition of Done

- [ ] Phase 6 gate (orchestration §4.7) passes
- [ ] `sandbox-gate.md` §3 green
- [ ] §6.4 matrix green
- [ ] All run/published flavors built from one SEA publish
- [ ] §8.6 re-confirmed
- [ ] `STATUS.md` updated: Phase 6 → `DONE`; final acceptance checklist complete
- [ ] Final acceptance checklist (orchestration §7) satisfied

---

## Report-back

```text
PHASE 6 COMPLETE
- Flavors rebuilt: <list> ; all share SEA publish <hash>: <yes>
- Flavors intentionally not rebuilt: <list + reason>
- Hydration config for the run: tick <x>, budget <n>, MaxConcurrency 1, LeaseSeconds <n>,
  off-switch verified: <yes>
- A6: <pass/fail> - <N> durable scopes, cold runtime, unattributed package ops: <0>
- A7: <pass/fail> - rows stayed Pending (<reason>), on-demand hydrate succeeded: <yes>,
  duplicate Pending rows per scope: <0>
- A8: <pass/fail> - seeded recency order <list>, Priority values <list>, apply order <list>
- 6.4 matrix: source-of-truth <p/f>, ranking <p/f>, idle gate <p/f>, cap <p/f>, no-global <p/f>
- 8.6 re-check: bind-first <p/f>, no fleet reconcile <p/f>, additive <p/f>, single-scope <p/f>
- Hydration left enabled after the gate: <yes/no>
- Docs updated: <list>
- Spec discrepancies found (not edited): <none | list>
- Deviations: <none | list>
```
