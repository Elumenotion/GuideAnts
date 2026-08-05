# Sandbox Gate — Local-AI Regression Recovery

Companion to [`00-orchestration.md`](./00-orchestration.md). This gate proves the spec's
acceptance criteria (§5, A1–A8) on a **real running stack**, not in unit tests.

Run §2 (A1–A5) at the end of **Phase 4**. Run §3 (A6–A8) at the end of **Phase 6**.
Re-run both at final acceptance.

> **Container discipline.** Every run of this gate requires recreating `guideants-ai`
> onto the newly built tag. That is a container recreate. Say so, state why, and **wait
> for explicit user approval** before running it. Read-only inspection (`docker ps`,
> `docker logs`, `docker exec` for status, `docker inspect`) needs no approval.

---

## 0. Conventions

Set these once per run and record them in `acceptance-evidence.md`.

```powershell
$Compose = "docker/docker-compose.<flavor>.yml"   # flavor per DECISIONS B2 / D1
$Svc     = "guideants-ai"
$Tag     = "<repo:tag from build>"                 # e.g. guideants-ai:cpu-26216.1430
$ImageId = docker inspect --format '{{.Id}}' $Tag
```

All curl commands run **inside** the AI container against its own nginx, so they exercise
the same `/sandbox/*` proxy path the API uses (`docker/build/guideants-ai/nginx.conf:58-68`):

```powershell
docker compose -f $Compose exec -T $Svc curl -fsS http://localhost/sandbox/health
```

The agent token is whatever the stack was started with
(`GA_SCRIPT_AGENT_TOKEN` in `docker/.env`, surfaced as `SCRIPT_EXECUTION_AGENT_TOKEN`).
Read it from the running container rather than assuming:

```powershell
$Token = docker compose -f $Compose exec -T $Svc printenv SCRIPT_EXECUTION_AGENT_TOKEN
```

---

## 1. Gate intent

Pass when all are true.

**Phase 4 scope (A1–A5):**

- SEA serves sandbox health and a trivial execute on the newly built image.
- No multi-scope package reconcile storm appears in the five minutes after health.
- Chat still works on flavors that include llama.
- On-demand hydration is single-scope, and repeats are hash-skipped.

**Phase 6 scope (A6–A8):**

- A cold runtime mount with many durable scopes produces **no** SEA-initiated fleet walk.
- Proactive hydration pauses for chat; on-demand hydration does not.
- Proactive candidate order follows API usage ranking, not directory order.

---

## 2. Phase 4 checks — A1 through A5

### 2.0 Preconditions

```powershell
docker compose -f $Compose ps
docker inspect --format '{{.Id}} {{index .Config.Labels "com.guideants.sea.publish"}}' $Tag
docker run --rm --entrypoint env $Tag | Select-String "SCRIPT_EXECUTION_"
```

- [ ] Image was built from current SEA source in this branch (Phase 2).
- [ ] All four §2.2 env variables present with spec values.
- [ ] SEA publish identity present, by whatever mechanism Phase 2 built (§2.1.2).
- [ ] `docker/.env` `GA_AI_*_IMAGE` points at this tag (§6.2.3).
- [ ] **Recreate approved by user**, then recreate `guideants-ai` onto the tag.
- [ ] Record health timestamp `T0` = first moment `/sandbox/health` succeeds.

### 2.1 A1 — sandbox health

```powershell
docker compose -f $Compose exec -T $Svc curl -fsS -o NUL -w "%{http_code}`n" http://localhost/sandbox/health
```

- [ ] Returns `200`.
- [ ] Succeeds on the **first** poll after container start within the flavor's expected
      window; a long delay before first success is a **bind regression**, even if it
      eventually passes (§3.1.1–3.1.2).

Record `T0` and the elapsed time from container start.

### 2.2 A2 — trivial execute

```powershell
$body = @{
  script    = "print('sandbox-gate-ok')"
  scriptType = "Python"
  projectId = "<known project guid>"
  notebookId = "<known notebook guid>"
  guideId   = "<known guide scope guid>"
} | ConvertTo-Json -Compress

docker compose -f $Compose exec -T $Svc `
  curl -fsS -X POST http://localhost/sandbox/execute `
    -H "X-Script-Agent-Token: $Token" `
    -H "Content-Type: application/json" `
    -d $body
```

- [ ] HTTP success.
- [ ] Response stdout contains `sandbox-gate-ok`.
- [ ] Exit code `0`.

### 2.3 A3 — no reconcile storm for five minutes

Start observing at `T0`. Read-only; no recreate needed.

```powershell
# process sampling
docker compose -f $Compose exec -T $Svc ps -eo pid,etimes,args
# repeat every 30s for 5 minutes, or:
docker compose -f $Compose logs --since 5m $Svc
```

- [ ] No sustained `pip install` / `pip uninstall` activity across **multiple** scope
      venv paths.
- [ ] No process whose command line references more than one
      `.../script-agent-runtime/project-*/guide-*/python-venv/...` path during the window.
- [ ] No `apt-get` activity after `T0` beyond a single hash-gated global bootstrap that
      was already in flight (§3.1.6).
- [ ] If an operator started a global apply during the window, the run is **void** —
      restart the observation (§5 A3 carve-out).

Capture the raw samples; "looked fine" is not evidence.

### 2.4 A4 — short chat completion (flavors with llama)

- [ ] A short, non-tool chat turn completes successfully against the local model.
- [ ] Mark `n/a` for flavors without llama (for example `slim`), and say which flavor.

### 2.5 A5 — first execute hydrates, second skips

Pick a scope that has durable requirements staged and an **empty** runtime tree.

```powershell
# confirm durable definition exists and runtime is absent
docker compose -f $Compose exec -T $Svc ls /var/lib/guideants/script-agent-admin/scopes/project-<p>/guide-<g>/
docker compose -f $Compose exec -T $Svc ls /var/run/guideants/script-agent-runtime/project-<p>/guide-<g>/ 2>&1
```

Run the same trivial execute as A2 against that scope, twice.

- [ ] First execute: creates `python-venv/`, installs declared packages, writes
      `runtime-applied-state.json`. Observed in logs and on disk.
- [ ] Second execute (definitions unchanged): **no** pip install. Runtime marker unchanged.
- [ ] Neither execute touched any other `project-*/guide-*` runtime directory (§3.4.6).
- [ ] A package present in the venv but **not** named in `requirements.txt` is still
      present after both runs (§3.3.4).

---

## 3. Phase 6 checks — A6 through A8

### 3.1 A6 — cold runtime mount, many durable scopes, no startup walk

Setup: ensure ≥5 durable scopes exist under `SCRIPT_EXECUTION_SCOPE_STATE_ROOT` with
non-empty `requirements.txt`, then clear the **runtime** volume only.

```powershell
docker compose -f $Compose exec -T $Svc sh -c "ls -d /var/lib/guideants/script-agent-admin/scopes/project-*/guide-* | wc -l"
```

Removing the runtime volume and recreating is a **destructive recreate** — describe it,
get explicit approval, then perform it.

- [ ] After recreate on the cold runtime mount, `/sandbox/health` succeeds promptly.
- [ ] For five minutes: **zero** venv creation or pip activity for scopes nobody executed
      against and no hydration job selected (§3.1.5).
- [ ] Any package work observed is attributable to either an `/execute` call or a
      **scoped** `POST /admin/apply` from a hydration job (§8.4.2). Attribute each one;
      unattributed work fails the gate.
- [ ] Hydration requests visible in API logs name exactly one `projectId`+`guideId` per call.

### 3.2 A7 — conversation lock blocks proactive, not on-demand

With hydration enabled and candidate rows in `JobQueue`:

1. Hold a conversation lock (start a chat turn on a local-AI stack).
2. Observe for at least one full scheduler tick plus one processor poll interval.
3. While the lock is held, invoke a tool that triggers `/sandbox/execute` for a scope with
   an empty runtime.

- [ ] No **new** hydration job is claimed while the lock is held; rows stay `Pending`
      rather than being failed or dropped (§8.3.1, §8.4.6).
- [ ] If a hydration job was already `Processing` when the lock appeared, it finishes and
      nothing new is claimed (§8.4.7, B12).
- [ ] The on-demand `/execute` hydrate **does** run and hydrates that one scope (§8.3.4).
- [ ] After the lock expires or is released, claiming resumes on a later poll.
- [ ] Across the busy window, the scheduler did not accumulate duplicate `Pending` rows for
      the same scope (B16 hazard). Query `JobQueue` grouped by payload scope to confirm.

### 3.3 A8 — candidate order follows API ranking

Arrange usage so a known-cold guide has the most recent `UsageEvents` activity and a
different guide is oldest, with directory mtimes deliberately in the **opposite** order.

- [ ] The first proactive scoped apply targets the highest-ranked candidate by API
      recency, not the directory that happens to sort or stat first (§8.2.2, §8.8 A8).
- [ ] Subsequent applies follow descending rank.
- [ ] Enqueued `JobQueue.Priority` values reproduce the ranking, and claim order follows
      them — this is the durable evidence; logs are corroboration.
- [ ] Log lines record the candidate list with rank and the reason each was chosen.

### 3.4 §6.4 control-plane matrix

| Behavior | Assertion | Result |
|---|---|---|
| Source of truth | Candidates produced from API/DB entities + usage; not SEA filesystem enumeration | pending |
| Ranking | Higher-recency / higher-usage guides hydrated before colder ones within budget | pending |
| Idle gate | No hydration job is claimed while the gate reports busy | pending |
| Cap | At most one hydration job `Processing` at a time; per-window budget honored | pending |
| No global apply | Hydration never calls global `POST /admin/apply` | pending |

For "no global apply", prove it two ways: a unit/integration test that the hydration payload
and client surface cannot construct an unscoped apply request, **and** an observation that
every apply request in the run carried both `projectId` and `guideId`.

---

## 4. Gate failure triage

| Symptom | Likely cause | Owning phase |
|---|---|---|
| `/sandbox/health` connection refused for many seconds after start | Package work before bind; entrypoint ordering (G1) | 2 |
| Health green but execute returns 502 | nginx up, SEA not accepting; health signal masking a dead SEA (G4) | 3 |
| Dependent service errors on first tool call after recreate | Dependent not gated on health (G5) | 3 |
| pip activity across several scopes right after recreate | A fleet walk was re-introduced, or worker called global apply | 1 / 5 |
| Second execute reinstalls packages | Hash comparison reading the wrong marker (durable audit vs runtime) | 1 |
| Package disappears after apply | An uninstall/prune path was introduced — additive violation | 1 |
| Hydration touches a scope with no API entity | Candidate set built from filesystem — source-of-truth violation | 5 |
| Hydration jobs keep getting claimed during chat | Job type missing from `GatedJobTypes`, or `ConversationLockGate:Enabled` is false | 5 |
| Applies continue long after chat ends | Duplicate `Pending` rows accumulated during the busy window (B16 hazard) | 5 |
| Hydration picks the wrong scope id | Duplicate resolver still in use instead of the shared one (B14) | 5 |
| Flavors disagree on SEA behavior | Built from different publishes — payload drift (§2.1.2) | 2 / 6 |

---

## 5. Evidence capture

Record every command and its raw output in
[`acceptance-evidence.md`](./acceptance-evidence.md), plus for each run:

- Image repo:tag and image **ID**.
- SEA publish identity label value.
- `T0` health timestamp and elapsed-from-start.
- Execute stdout and exit code.
- Chat result (or `n/a` + flavor).
- Whether an operator-initiated global apply occurred during any observation window.
