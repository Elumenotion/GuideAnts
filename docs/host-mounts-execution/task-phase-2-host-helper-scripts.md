# Task — Phase 2: Host helper scripts (`apply` / `remove`)

> Subagent brief. Execute top to bottom; return the Report-back contract verbatim.

## Mission

Add the platform-specific helper scripts the UI displays to admins. They rewrite the
generated Compose override **idempotently** and restart **only** the affected
services. This is the host-side actuator for adding/removing a mount source.

> **Decisions required:** D2 (affected-services set), D3 (command-source model —
> determines whether the script fetches the plan from the API or receives it fully
> in args).

## Read first

- `../host-folder-notebook-mounts-plan.md` §4 (override shape), §6 (Host Helper
  Commands, incl. the **self-restart caveat**), §22.2.
- `./DECISIONS.md` → D2, D3, D4/D5 (SMB deferred but the generator branch must not be
  precluded), Part B invariants.
- `./docker-gate.md` §3.4–3.6 (scoped restart, idempotency, no inlined creds).
- Phase 1 output: `.installer_state.env` keys, override file name, env vars.

## Preconditions

- Phase 1 gate green. D2, D3 resolved.

## Guardrails (hard)

- Override rewrite must be **idempotent**: applying the same mount twice → a
  byte-identical override; removing a mount removes **only** its block.
- Restart **only** the affected services with `--no-deps` (the D2 set). Never a bare
  full-stack `up`.
- **Self-restart caveat (plan §6):** because `guideants-webapi-ui` is in the
  affected set, the restart bounces the API serving the admin UI and any callback.
  Treat the post-restart API callback (plan §6 step 5) as **best-effort/redundant** —
  startup reconciliation is the source of truth. Do **not** make the script fail if
  the callback fails.
- **Never inline SMB credentials** into the generated override (plan §20.1). For the
  first cut you implement the **local bind** branch; structure the script so the
  CIFS/credential branch can be added later without inlining secrets.
- Idempotent rewrite must not corrupt a hand-edited base compose file — the script
  only ever writes `docker-compose.host-mounts.generated.yml`.

## Tasks

1. Create `scripts/guideants-host-mount.ps1` and `scripts/guideants-host-mount.sh`
   with `apply` and `remove` verbs and the argument shapes in plan §6
   (`-MountId`/`--mount-id`, `-HostPath`/`--host-path`).
2. Read `.installer_state.env` (+ args) to reconstruct the compose command
   (`COMPOSE_FILE`, override file, affected services, docker dir).
3. Per D3:
   - `(a) api-plan`: fetch/receive the mount plan from the API and apply it.
   - `(b) self-contained`: build the override block entirely from args.
4. Rewrite `docker/docker-compose.host-mounts.generated.yml` idempotently (local
   bind block per plan §4). Preserve other mounts' blocks.
5. Restart affected services only:
   ```bash
   docker compose -f <COMPOSE_FILE> -f docker-compose.host-mounts.generated.yml \
     up -d --no-deps <affected services>
   ```
6. Optionally call back to the API to request reconciliation (best-effort per the
   caveat). Failure of this step must not fail the script.
7. `remove` removes the source's block and restarts affected services (so the stack
   no longer mounts it).

## Files in scope

- `scripts/guideants-host-mount.ps1`
- `scripts/guideants-host-mount.sh`

**Out of scope:** API services/endpoints, override *generation logic inside the API*
(Phase 4 may share format), data model, UI.

## Self-verification

```bash
# apply twice → identical override
./scripts/guideants-host-mount.sh apply --mount-id test --host-path /tmp/shared
cp docker/docker-compose.host-mounts.generated.yml /tmp/a.yml
./scripts/guideants-host-mount.sh apply --mount-id test --host-path /tmp/shared
diff /tmp/a.yml docker/docker-compose.host-mounts.generated.yml && echo IDEMPOTENT_OK
# validate + scoped restart command
docker compose -f docker/docker-compose.ghcr-cpu.yml -f docker/docker-compose.host-mounts.generated.yml config > /dev/null && echo CONFIG_OK
./scripts/guideants-host-mount.sh remove --mount-id test
```

Plus global gate (orchestration §4.1) + docker gate (`docker-gate.md`
§3.1, 3.4, 3.5, 3.6).

## Definition of Done

- [ ] Both scripts exist with `apply`/`remove`; read `.installer_state.env` + args.
- [ ] Override rewrite idempotent (apply x2 byte-identical; remove deletes only the
      target block).
- [ ] Restart is `--no-deps`-scoped to the D2 affected set.
- [ ] Post-restart API callback is best-effort; script does not fail if it fails.
- [ ] Local-bind branch implemented; SMB branch not precluded; **no** inlined creds.
- [ ] Docker gate green.

## Report-back contract (return exactly this)

```
PHASE 2 REPORT
- Scripts created: ps1=<y> sh=<y>; verbs: <apply/remove>
- Command-source model implemented (D3): <a-api-plan / b-self-contained>
- Idempotent apply (x2 byte-identical): <yes>
- Remove deletes only target block: <yes>
- Restart scoped --no-deps to: <service list>
- Callback best-effort (no fail on callback error): <yes>
- SMB creds inlined: <no>
- DOCKER GATE: config WITH override=<ok> idempotent=<yes> scoped-restart=<yes> creds-inlined=<no>
- Verification: server-build/tests=<...> client-build/tests=<...>
- Files touched: <list>
- Deviations / surprises: <list or "none">
```
