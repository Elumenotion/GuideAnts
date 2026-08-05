# Phase 2 — Image + Payload Contract

**Depends on:** Phase 1 `DONE`
**Blocks:** Phases 3–6

> Task 2's stamping mechanism is `DECISIONS.md` Part C — the label and env var shown are a
> suggestion, not a contract.

---

## Mission

Guarantee spec §2.1 (shared SEA payload), §2.2 (image environment), and §3.1.6 (entrypoint
global bootstrap) across every `guideants-ai` flavor, and make the guarantee **verifiable
from a built image** rather than from build-time intent.

The env defaults are already correct in all five flavors. The two real pieces of work are
(a) proving flavors share one SEA publish, and (b) making sure the global bootstrap can no
longer delay SEA's bind.

---

## Read first

- The spec §2.1, §2.2, §2.3, §2.4, §3.1.6, §6.2
- `docs/local-ai-regression-execution/00-orchestration.md` §1.1, §1.2 (G1, G8), §4.3
- `docs/local-ai-regression-execution/DECISIONS.md` Part A, B2, B3, D1, Part C
- Build lane:
  - `docker/build/build_guideants_ai.ps1` — deps slice `:44-68`, backend map `:244-289`,
    tag computation `:291-295`, SEA publish + hash gate `:317-366`, stage into context
    `:368-375`, cleanup `:479-485`, `.env` write `:493-504`
  - `docker/build/build_guideants_ai.sh` (parity mirror, slice at `:260`)
- Image lane:
  - `docker/build/guideants-ai/Dockerfile.cpu` — env `:223-226`, SEA copy `:228`, healthcheck `:260-261`
  - `Dockerfile.cuda:230-233`, `Dockerfile.rocm:314-317`, `Dockerfile.slim:123-126`, `Dockerfile.vulkan:318-321`
  - `docker/build/guideants-ai/entrypoint.sh` — validation `:223-248`, reconcile `:250-251`,
    exports `:254-255`, SEA start `:380-385`, nginx `:400`
  - `docker/build/guideants-ai/entrypoint.slim.sh` — reconcile `:22-33`, SEA `:40-45`, nginx `:50-51`
  - `docker/build/guideants-ai/script-agent-admin/reconcile.sh` — seeding `:26-48`,
    validation `:50-101`, apt hash gate `:103-143`, pip hash gate `:145-152`,
    no-op path `:154-157`, state write `:159-179`

---

## Preconditions

- [ ] Phase 1 `DONE`; SEA tests green
- [ ] Active flavor recorded in `STATUS.md` (DECISIONS B2)

---

## Guardrails

- Do **not** move global bootstrap into the SEA process. It stays in the entrypoint,
  global-only, hash-gated (§3.1.6, DECISIONS non-goals).
- Do **not** let `reconcile.sh` enumerate `scopes/`. It operates on the global admin dir only.
- Do **not** change the durable/runtime mount split (§2.4 is settled).
- Do **not** change env values in §2.2 — they are already correct. Verify, don't edit.
- Do **not** build a flavor "just to check" if it is not going to be run or published in
  this delivery; Phase 6 handles remaining flavors.
- Building an image is fine. **Recreating a running container is not** — that needs
  explicit user approval and belongs to Phase 4.

---

## Tasks

### 1. Entrypoint: bootstrap must not precede bind (gap G1 — B3)

Today `entrypoint.sh:250-251` runs `reconcile.sh` **synchronously** before any service
starts, and SEA starts later at `:380-385`. On a cold global bootstrap (first run, or
changed `apt-packages.txt` / global `requirements.txt`) that is an apt+pip install standing
between container start and `/sandbox/health` — precisely what §3.1.1–3.1.2 forbid.

Reorder so that:

1. Validation of required env (`FILE_STORAGE_ROOT`, tokens) still happens first and still
   fails fast. Validation is cheap and is explicitly allowed pre-bind.
2. Path exports (`SCRIPT_EXECUTION_ADMIN_STATE_DIR`, `SCRIPT_EXECUTION_SCOPE_STATE_ROOT`)
   happen before SEA starts — SEA needs a complete contract.
3. **SEA starts.**
4. The global `reconcile.sh` bootstrap runs concurrently, with its output clearly labelled
   in the container log so an operator can tell global bootstrap apart from scope work.
5. nginx starts as it does today.

Constraints on the reordering:

- `reconcile.sh` writes into the global admin state dir and installs into `/opt/venv`.
  Confirm nothing SEA does at bind time reads a file that reconcile creates. `reconcile.sh`
  seeds `config.json` / `requirements.txt` / `apt-packages.txt` (`:26-48`), and SEA's
  `AdminStateRuntime.InitializeAsync` also creates the global files it needs. If both can
  create the same file, make the ordering explicit and idempotent — do not leave it to a race.
- A failure in the global bootstrap must be **loud** (logged, non-zero recorded) but must
  not take down an already-serving SEA. It also must not be swallowed silently.
- Apply the same reordering to `entrypoint.slim.sh` (`:22-33` vs `:40-45`).
- `reconcile.sh` itself needs no logic change beyond logging clarity — it is already
  global-only and hash-gated.

Also fix the contradiction noted during discovery: the entrypoint's shell default for
`SCRIPT_EXECUTION_ADMIN_API_ENABLED` is `false` (`entrypoint.sh:241`) while every AI image
sets `true`. Harmless today because image ENV wins, but it is a trap. Make the entrypoint
default match the image contract or remove the shell default entirely.

### 2. Stamp and verify SEA publish identity (gap G8 — §2.1.2; mechanism per Part C)

`build_guideants_ai.ps1` already computes a SEA source hash and caches the publish
(`:317-366`, state at `docker/.build-state/scriptexecutionagent-guideants-ai.hash`). Surface
it into the image so §2.1.2 is checkable after the fact:

One workable mechanism (Part C — substitute freely, then name what you built):

| Mechanism | Value |
|---|---|
| Image label `com.guideants.sea.publish` | SEA publish source hash |
| Image env `GA_SEA_PUBLISH_ID` | same value |

Implementation notes:

- Pass it as a build arg from the build script into each Dockerfile's final stage; do not
  hardcode it in the Dockerfiles.
- Mirror the change in `build_guideants_ai.sh` so the two build entry points stay in parity.
- Add it to the build script's completion output so the operator sees which publish went in.

Then verifying §2.1.2 is a one-liner per flavor:

```powershell
docker inspect --format '{{index .Config.Labels "com.guideants.sea.publish"}}' <tag>
```

### 3. Verify §2.2 env defaults from the built image (not from source)

Add a repeatable check (script or documented command) that the built image exposes all four
variables with the spec values:

```powershell
docker run --rm --entrypoint env <tag> | Select-String "^SCRIPT_EXECUTION_(ADMIN_API_ENABLED|ADMIN_STATE_DIR|SCOPE_STATE_ROOT|SCOPE_RUNTIME_ROOT)="
```

| Variable | Required value |
|---|---|
| `SCRIPT_EXECUTION_ADMIN_API_ENABLED` | `true` |
| `SCRIPT_EXECUTION_ADMIN_STATE_DIR` | `/var/lib/guideants/script-agent-admin` |
| `SCRIPT_EXECUTION_SCOPE_STATE_ROOT` | `/var/lib/guideants/script-agent-admin/scopes` |
| `SCRIPT_EXECUTION_SCOPE_RUNTIME_ROOT` | `/var/run/guideants/script-agent-runtime` |

Compose may override these (§2.2), but the image must stand alone.

### 4. Confirm §2.3 mount contract in compose

Verify, per compose file that starts `guideants-ai`, that all three mounts are present
(`docker/docker-compose.cpu.yml:36-48`, volumes `:313-316` is the reference shape):

| Mount | Path |
|---|---|
| Durable admin/scope state | `/var/lib/guideants/script-agent-admin` |
| Executable runtime | `/var/run/guideants/script-agent-runtime` |
| Content files | `/app/ContentFiles` |

Record any compose file that is missing one. Fixing compose ordering/health is Phase 3;
this task is inventory plus mount correctness only.

### 5. Build the active flavor (§6.2)

```powershell
pwsh ./docker/build/build_guideants_ai.ps1 -Backend <active flavor>
```

- Confirm the build published **current** SEA source from this branch (the hash gate must
  have re-published after Phase 1's changes; if it reused a cached publish, that is a bug
  in the gate — investigate rather than deleting state blindly).
- Record the dated tag (`guideants-ai:<backend>-<jjjjj>.<hhmm>`) and the `-latest` alias.
- Confirm the script wrote the active `GA_AI_*_IMAGE` in `docker/.env` (§6.2.3).

Do **not** recreate the running container here. Phase 4 does that, with approval.

---

## Files in scope

| Action | Path |
|--------|------|
| Modify | `docker/build/guideants-ai/entrypoint.sh` |
| Modify | `docker/build/guideants-ai/entrypoint.slim.sh` |
| Modify | `docker/build/guideants-ai/script-agent-admin/reconcile.sh` (logging clarity only) |
| Modify | `docker/build/guideants-ai/Dockerfile.{cpu,cuda,rocm,slim,vulkan}` (publish-id build arg → label + env) |
| Modify | `docker/build/build_guideants_ai.ps1` |
| Modify | `docker/build/build_guideants_ai.sh` |
| Modify | `docker/.env` (active `GA_AI_*_IMAGE`, written by the build script) |
| Add | Image contract verification script or documented commands |
| Modify | `docs/developer-config-guide.md` (bootstrap ordering + publish identity, if it documents the old order) |

Out of scope: SEA C# source (Phase 1), compose healthcheck/depends_on (Phase 3), API (Phase 5).

---

## Self-verification

```powershell
pwsh ./docker/build/build_guideants_ai.ps1 -Backend <active>
docker run --rm --entrypoint env <tag> | Select-String "SCRIPT_EXECUTION_|GA_SEA_PUBLISH_ID"
docker inspect --format '{{index .Config.Labels "com.guideants.sea.publish"}}' <tag>
docker run --rm --entrypoint sh <tag> -c "ls -l /app/script-agent/ScriptExecutionAgent.dll"
rg -n "scopes" docker/build/guideants-ai/script-agent-admin/reconcile.sh
```

- [ ] Four §2.2 variables present with spec values
- [ ] Publish identity present as both label and env
- [ ] SEA payload present at `/app/script-agent/`
- [ ] `reconcile.sh` has no `project-*` / `guide-*` enumeration
- [ ] Entrypoint starts SEA before the global bootstrap, and bootstrap failure is logged loudly
- [ ] `docker/.env` active image updated

Bind-first sanity without recreating the stack: run the image standalone with a throwaway
runtime dir and time first success on `/sandbox/health`.

```powershell
docker run -d --rm --name sea-bindcheck -e FILE_STORAGE_ROOT=/app/ContentFiles `
  -e SCRIPT_EXECUTION_REQUIRE_TOKEN=false <tag>
# poll /sandbox/health from inside; record elapsed to first 200
docker rm -f sea-bindcheck
```

This is a fresh throwaway container, not a recreate of a running service — no approval needed.

---

## Definition of Done

- [ ] Phase 2 gate (orchestration §4.3) passes
- [ ] Cold-bootstrap bind delay eliminated (measured, recorded in `acceptance-evidence.md`)
- [ ] Publish identity reproducible across a second flavor built from the same publish
- [ ] `STATUS.md` updated: Phase 2 → `DONE`, gaps G1/G8 → closed
- [ ] No secrets added to Dockerfiles, entrypoints, or build scripts

---

## Report-back

```text
PHASE 2 COMPLETE
- Active flavor / tag: <backend> / <repo:tag> (<image id>)
- SEA publish identity: <hash> (label + env verified)
- Entrypoint ordering: SEA start at <line>, global bootstrap at <line>, nginx at <line>
- Cold-bootstrap bind time: before <Xs> / after <Ys> (throwaway container measurement)
- 2.2 env verification: <4/4 pass>
- 2.3 mount inventory: <compose files checked, any missing mounts>
- docker/.env GA_AI_*_IMAGE: <key=value>
- Deviations: <none | list>
```
