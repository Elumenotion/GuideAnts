# Phase 4 — Runtime Acceptance A1–A5

**Depends on:** Phases 1–3 `DONE`
**Blocks:** Phases 5–6 (hard stop — no §8 work starts until this is green)

---

## Mission

Prove spec §5 acceptance A1–A5 on a **real stack running the newly built image**, per the
§6.3 runtime acceptance procedure. This phase writes almost no code. It produces evidence.

This is the hard stop in the plan. The §8 control plane exists to recover cold runtimes
safely; building it on top of an unproven agent would mean debugging two layers at once.

---

## Read first

- The spec §5 (A1–A5), §6.2, §6.3
- `docs/local-ai-regression-execution/sandbox-gate.md` §2 (the actual procedure)
- `docs/local-ai-regression-execution/00-orchestration.md` §4.5, §6 (deviation protocol)
- `docs/local-ai-regression-execution/DECISIONS.md` B2 (active flavor is the SUT)

---

## Preconditions

- [ ] Phase 1 `DONE` — SEA invariants + §6.1 coverage green
- [ ] Phase 2 `DONE` — image built from current SEA source, env + publish identity verified
- [ ] Phase 3 `DONE` — health requires sandbox; dependents gated
- [ ] Active flavor and target tag recorded in `STATUS.md`
- [ ] A known `(projectId, notebookId, guideScopeId)` triple available for execute tests
- [ ] A scope with **durable requirements staged** and an **empty runtime tree**, for A5

---

## Guardrails

- **Container recreate requires explicit user approval in the requesting message.** Prepare
  everything, state plainly that a recreate of `guideants-ai` onto `<tag>` is required and
  why, then **wait**. Do not bounce the container to "just check".
- Read-only inspection is always fine: `docker ps`, `docker logs`, `docker inspect`,
  `docker compose exec` for status/env/files.
- Do **not** start a global admin apply during the A3 observation window. If one is started
  by anyone, the run is void — restart the window (§5 A3).
- Do **not** "fix" a failing check inside this phase. Classify it, route it to the owning
  phase, fix there, re-run that phase's gate, then re-run this one (orchestration §6).
- Do **not** paraphrase results. Capture raw command output.

---

## Tasks

### 1. Build and stage the active flavor (§6.2)

```powershell
pwsh ./docker/build/build_guideants_ai.ps1 -Backend <active flavor>
```

- [ ] Build publishes **current** SEA source from this branch (§6.2.1)
- [ ] Built image contains the §2.2 env defaults (§6.2.2)
- [ ] `docker/.env` active `GA_AI_*_IMAGE` set to the new dated tag (§6.2.3)
- [ ] Record repo:tag, image ID, and the `com.guideants.sea.publish` label

### 2. Request and perform the recreate (§6.3.2)

State to the user, in the message that asks:

- which service (`guideants-ai`), which compose file, which tag;
- that this is a recreate and will interrupt local AI briefly;
- that A1–A5 cannot be evaluated without it.

After approval:

```powershell
docker compose -f docker/docker-compose.<flavor>.yml up -d --force-recreate guideants-ai
```

Record the recreate in the `STATUS.md` container recreate log, including approval.

Immediately begin timing: `T_start` = recreate completion, `T0` = first successful
`/sandbox/health`.

### 3. Execute A1–A5

Follow [`sandbox-gate.md`](./sandbox-gate.md) §2 exactly. Summary of what must be true:

| # | Requirement | Evidence to capture |
|---|---|---|
| A1 | `GET /sandbox/health` returns success | HTTP code, `T0`, elapsed from `T_start` |
| A2 | `POST /sandbox/execute` with a trivial Python script returns stdout and exit code `0` | full response body |
| A3 | For five minutes after health, no multi-scope package reconcile storm | process samples at ≥30s intervals + container logs for the window |
| A4 | Where the flavor includes llama: a short non-tool chat completion finishes | request/response summary, or `n/a` + flavor |
| A5 | First execute against a durable-requirements scope with empty runtime hydrates that scope only; a second execute with unchanged definitions skips pip | before/after directory listings, both responses, log excerpts |

A3 deserves care: it is the check that catches a re-introduced fleet walk, and it is the
one most easily faked by not looking. Sample the process list on a schedule and keep the
raw output. A single scoped hydrate that you triggered is expected and fine; sustained
multi-scope pip activity is a failure.

A5 also carries the additive check from §3.3.4: confirm a package present in the venv but
absent from `requirements.txt` survives both runs.

### 4. Record the result (§6.3.4)

Fill in, for this run:

- `STATUS.md` → acceptance ledger row "After Phase 4" (A1–A5; A6–A8 stay blank)
- `STATUS.md` → container recreate log
- `acceptance-evidence.md` → the raw commands and outputs

### 5. On failure

Classify per orchestration §6 and route:

| Symptom | Classification | Owning phase |
|---|---|---|
| Health slow or refused after start | `bind regression` | 2 |
| Health green but execute 502 | `readiness gap` | 3 |
| Multi-scope pip after start | `fleet walk` | 1 |
| Second execute reinstalls | wrong marker read | 1 |
| Undeclared package removed | `additive violation` | 1 |
| Image env missing / wrong publish | `payload drift` | 2 |

Fix in the owning phase, re-run that phase's gate, then re-run Phase 4 **in full** — not
just the check that failed.

---

## Files in scope

| Action | Path |
|--------|------|
| Modify | `docs/local-ai-regression-execution/STATUS.md` |
| Modify | `docs/local-ai-regression-execution/acceptance-evidence.md` |
| Modify | `docker/.env` (active `GA_AI_*_IMAGE`, written by the build script) |

No source changes are expected in this phase. If a source change becomes necessary, it
belongs to the owning phase and this phase restarts.

---

## Self-verification

- [ ] Every A1–A5 row has raw captured output, not a summary sentence
- [ ] Image ID in the evidence matches the image the container is actually running:
      `docker inspect --format '{{.Image}}' <container>`
- [ ] A3 window was a full five minutes from `T0`, with timestamps
- [ ] No global admin apply occurred during the A3 window
- [ ] A4 is either a real chat result or an explicit `n/a` naming the flavor

---

## Definition of Done

- [ ] Phase 4 gate (orchestration §4.5 / `sandbox-gate.md` §2) passes
- [ ] Definition of done for the flavor is met (§6.2.4): that tag is running and A1–A5 pass
- [ ] `STATUS.md` acceptance ledger + recreate log updated
- [ ] `STATUS.md` updated: Phase 4 → `DONE`
- [ ] Evidence captured in `acceptance-evidence.md`

---

## Report-back

```text
PHASE 4 COMPLETE
- Flavor / tag: <backend> / <repo:tag>
- Image ID: <id>   SEA publish: <hash>
- Recreate approved by user: <yes + when>
- T_start -> T0 (first /sandbox/health 200): <N>s
- A1 health: <pass/fail>
- A2 execute: <pass/fail, stdout excerpt, exit code>
- A3 five-minute observation: <pass/fail>, samples at <times>, global apply during window: <no>
- A4 chat: <pass/fail | n/a (<flavor> has no llama)>
- A5 hydrate-then-skip: <pass/fail>, first run installed <packages>, second run pip invocations: <0>
- A5 additive check: undeclared package <name> present after both runs: <yes>
- Deviations: <none | classification + owning phase + re-gate result>
```
