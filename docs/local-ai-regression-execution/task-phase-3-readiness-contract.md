# Phase 3 — Readiness Contract

**Depends on:** Phase 2 `DONE`
**Blocks:** Phases 4–6

---

## Mission

Implement spec §4 so that "the AI container is healthy" means "SEA is accepting
connections", and so that anything depending on the sandbox waits for that.

Readiness is enforced by the health gate, not by a retry (B7). The API's sandbox client
gets no retry: a compensating retry around a race that the readiness gate eliminates is
failure-hiding, and §4.4's second option additionally requires distinguishing "before SEA
accepted the request" from "after SEA began executing" — an error there runs a user's
script twice.

---

## Read first

- The spec §4 (all four clauses)
- `docs/local-ai-regression-execution/00-orchestration.md` §1.2 (G4, G5, G6), §4.4, §5
- `docs/local-ai-regression-execution/DECISIONS.md` B7, B8, Part C
- Image + compose:
  - `docker/build/guideants-ai/nginx.conf:58-68` (`/sandbox/` → `127.0.0.1:8081`, trailing-slash prefix strip)
  - `docker/build/guideants-ai/nginx.slim.conf:21-39`
  - `docker/build/guideants-ai/Dockerfile.cpu:260-261` (HEALTHCHECK OR-chain)
  - `docker/build/guideants-ai/Dockerfile.slim:140-141`
  - `docker/docker-compose.cpu.yml:209-213` (`depends_on: service_started`)
  - `docker/docker-compose.ghcr-cpu.yml:190-194`, `docker/docker-compose.slim.yml:85-87`
- API client (read-only in this phase):
  - `src/server/GuideAntsApi/Services/NotebookDockerScriptService.cs` — base URL `:521-555`,
    execute `:318-322`, auth `:486-501`

---

## Preconditions

- [ ] Phase 2 `DONE`; active flavor image built and env-verified
- [ ] Phase 2's bind-time measurement captured, so the health gate's start window is sized
      from data rather than guessed

---

## Guardrails

- Do not add a retry "as well, just in case" (B7).
- Do **not** add an unbounded retry, a sleep-and-hope loop, or a silent fallback that
  masks an unready sandbox. If sandbox is down, callers must see a clear failure.
- Do **not** retry an execute request at all (B7), and never after SEA has begun
  executing the script (§4.4).
- `/health` and `/sandbox/health` must stay unauthenticated and cheap — no scope work, no
  package work, no directory walk behind them.
- Do **not** change the nginx proxy path shape. `/sandbox/` → `http://127.0.0.1:8081/`
  with the trailing slash is load-bearing (it strips the prefix so SEA sees `/health`).
- Compose edits do not restart anything by themselves; **recreating** to test them does and
  needs explicit user approval (Phase 4).

---

## Tasks

### 1. Health signal requires sandbox (gap G4 — §4.2–4.3, B8)

Today every flavor's `HEALTHCHECK` is an **OR-chain**: sandbox is tried first, but if it
fails the check falls through to llama / media / other endpoints, so the container can
report healthy while SEA is dead. §4.3 requires the opposite.

Change to: **sandbox health is required** when sandbox is enabled in that image. Other
endpoint checks may be added with AND, never OR.

| Flavor | File | Current |
|---|---|---|
| cpu | `Dockerfile.cpu` | `:260-261` OR-chain |
| cuda13 | `Dockerfile.cuda` | OR-chain |
| rocm | `Dockerfile.rocm` | OR-chain |
| vulkan | `Dockerfile.vulkan` | OR-chain |
| slim | `Dockerfile.slim` | `:140-141` OR-chain |

Notes:

- Keep `--interval` / `--timeout` / `--retries` sane for the flavor's start cost, and use
  `--start-period` so a slow first start is not counted as unhealthy. A start period is a
  grace window, not a fallback — it does not mask a permanently dead SEA.
- Every AI image in this repo embeds SEA, so in practice sandbox is required everywhere;
  keep the check honest rather than conditional on a variable nothing sets.

### 2. Dependents wait for health (gap G5 — §4.3; mechanism per Part C)

Every compose file that starts `guideants-ai` and has a service that calls the sandbox must
gate on health rather than start order:

```yaml
depends_on:
  guideants-ai:
    condition: service_healthy
```

Inventory to cover (confirm the full list at implementation time; do not assume this is
exhaustive):

- `docker/docker-compose.cpu.yml`, `.cuda.yml`, `.rocm.yml`, `.vulkan.yml`, `.slim.yml`
- `docker/docker-compose.cpu.api-only-local-build.yml`, `.cuda.api-only-local-build.yml`
- `docker/docker-compose.ghcr-cpu.yml`, `.ghcr-cuda13.yml`, `.ghcr-rocm.yml`,
  `.ghcr-vulkan.yml`, `.ghcr-slim.yml`
- Installer modular compose under `installer/docker/compose/`

Because `guideants-ai` has no compose-level `healthcheck` today, `service_healthy` will use
the image `HEALTHCHECK` from task 1. Verify that is actually what resolves — a
`service_healthy` dependency on a service with no health definition is a silent no-op in
some compose versions. If a compose-level healthcheck is needed for the behavior to be
real, add it and keep it identical in meaning to the image one.

Trade-off to document in the report: gating on health means dependents start later. That is
the consequence §4.3 accepts when the product keeps the service non-ready rather than
retrying.

### 3. Confirm the proxy and health semantics (§4.1–4.2)

No change expected; verify and record.

- [ ] `/sandbox/*` proxies to SEA on `127.0.0.1:8081` in both `nginx.conf` and `nginx.slim.conf`.
- [ ] `GET /sandbox/health` succeeds **only** when SEA is accepting connections and reports
      healthy. Specifically: with SEA stopped, nginx must return a failure (502/504), not a
      cached or static 200. Test this by stopping only the SEA process inside a throwaway
      container and re-checking.

### 4. Record that §4.4 option 2 is not implemented

- [ ] Note in `acceptance-evidence.md` that the API sandbox client has **no** retry, citing
      B7. A reviewer looking for a retry should find the decision, not silence.
- [ ] Confirm by inspection that no retry/backoff wrapper exists around the execute call in
      `NotebookDockerScriptService`. If one is found, it is pre-existing scope for a
      decision update, not something to quietly keep.

---

## Files in scope

| Action | Path |
|--------|------|
| Modify | `docker/build/guideants-ai/Dockerfile.{cpu,cuda,rocm,slim,vulkan}` (HEALTHCHECK) |
| Modify | `docker/docker-compose.*.yml` (depends_on health gating for sandbox callers) |
| Modify | `installer/docker/compose/*.yml` (same gating, if the modular stack starts `guideants-ai`) |
| Modify | `docs/developer-config-guide.md` (startup ordering expectation) |

Out of scope: SEA C# source, API C# source, hydration worker.

---

## Self-verification

Build the active flavor again so the new HEALTHCHECK is in the image, then use a throwaway
container (not a recreate of the running stack):

```powershell
pwsh ./docker/build/build_guideants_ai.ps1 -Backend <active>

docker run -d --rm --name sea-healthcheck <tag>
docker inspect --format '{{json .State.Health}}' sea-healthcheck
# expect: starting -> healthy, and the check command references /sandbox/health

# prove health fails when SEA is down
docker exec sea-healthcheck sh -c "pkill -f ScriptExecutionAgent.dll"
Start-Sleep -Seconds 60
docker inspect --format '{{.State.Health.Status}}' sea-healthcheck   # expect: unhealthy
docker rm -f sea-healthcheck
```

Compose validation without starting anything:

```powershell
docker compose -f docker/docker-compose.<flavor>.yml config | Select-String -Context 2 "depends_on|service_healthy"
```

- [ ] Health command requires `/sandbox/health`
- [ ] Killing SEA turns the container **unhealthy** (this is the check that G4 is really closed)
- [ ] Every compose file that starts `guideants-ai` gates sandbox callers on `service_healthy`
- [ ] `docker compose config` parses cleanly for every edited file

---

## Definition of Done

- [ ] Phase 3 gate (orchestration §4.4) passes
- [ ] Exactly one §4.4 option implemented (option 1); the absence of option 2 is documented
- [ ] Unhealthy-when-SEA-down demonstrated and captured
- [ ] `STATUS.md` updated: Phase 3 → `DONE`, gaps G4/G5/G6 → closed
- [ ] No unbounded retry or silent fallback introduced anywhere

---

## Report-back

```text
PHASE 3 COMPLETE
- Readiness enforced by: health gate (B7); no client retry
- HEALTHCHECK updated in: <flavors>
- Health command: <exact command>
- Start period / interval / retries: <values + rationale>
- Kill-SEA test: container went unhealthy after <N>s
- depends_on service_healthy applied in: <compose files>
- Compose files with no sandbox caller (unchanged): <list>
- Startup latency impact for dependents: <observed>
- Deviations: <none | list>
```
