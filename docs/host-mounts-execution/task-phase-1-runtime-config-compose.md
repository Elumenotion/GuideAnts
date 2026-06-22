# Task — Phase 1: Runtime config + compose-override include in `start_*`

> Subagent brief. Execute top to bottom; return the Report-back contract verbatim.

## Mission

Lay the **docker/runtime foundation** so the API knows how it was launched and the
launchers automatically pick up a generated host-mounts override **if it exists**.
No data model, no API, no symlinks here — only Compose env vars and launcher wiring.

> **Decisions required (read `DECISIONS.md`):** D2 (affected-services set) and D3
> (command-source model). Do not invent values — use the locked ones.

## Read first

- `../host-folder-notebook-mounts-plan.md` §4 (Compose override), §5 (Runtime
  Configuration), §22.1.
- `./DECISIONS.md` → D2, D3, and Part B invariants (no `External/`, startup
  reconciliation is source of truth).
- `./docker-gate.md` (this phase runs the docker gate).
- Launchers (repo root): `start_windows.cmd`, `start_linux.sh`, `start_macos.sh`.
- Compose files in `docker/` (the `ghcr-*` set is the default path; local-build
  variants too).

## Preconditions

- Pre-flight baseline captured; D2 and D3 resolved in `DECISIONS.md`.

## Guardrails (hard)

- The override file name is **exactly** `docker-compose.host-mounts.generated.yml`
  in `docker/`. The launchers must include it **only if it exists** — never fail or
  change behavior when it is absent.
- Do **not** generate the override here (Phase 2/4 own generation). You may add a
  small **sample/example** override (clearly named, e.g. `*.example.yml`, or a
  fenced example in docs) only for gate validation — do **not** commit a real
  generated override.
- Env var names are **exactly** the plan §5 `GuideAntsRuntime__*` set. Values come
  from D2 for `AffectedMountServices`.
- The launchers already select the compose file and save `.installer_state.env`
  (`BACKEND`, `COMPOSE_MODE`, `LAST_RUN_EPOCH`). **Extend, do not rewrite** that
  logic; preserve existing behavior exactly.
- No "fallback": if the override is malformed, surface a clear error — do not
  silently drop it.

## Tasks

1. Add the `GuideAntsRuntime__*` env vars (plan §5) to the compose files the
   launchers use (at minimum the `ghcr-*` set; mirror to local-build variants for
   consistency). Set on the API service (`guideants-webapi-ui`):
   - `GuideAntsRuntime__StartCommand` (per-OS; see step 3 for how launchers persist
     the actual one)
   - `GuideAntsRuntime__ComposeFile`
   - `GuideAntsRuntime__HostMountOverrideFile=docker-compose.host-mounts.generated.yml`
   - `GuideAntsRuntime__DockerDirectory=docker`
   - `GuideAntsRuntime__AffectedMountServices=<D2 value>`
2. In all three launchers, after the compose file is selected and before
   `docker compose ... up -d`:
   - If `docker/docker-compose.host-mounts.generated.yml` exists, add
     `-f docker-compose.host-mounts.generated.yml` (after the base `-f`).
   - Otherwise run the existing command unchanged.
3. Persist enough state in `.installer_state.env` for the Phase 2 helper scripts to
   reconstruct the compose command: at least `COMPOSE_FILE`, the override file name,
   the affected-services list, the docker directory, and the per-OS start command.
4. Confirm the API can read the env vars (a thin options/config binding is
   acceptable here, or leave consumption to Phase 4 — but the vars must be present
   and readable). If you add a config POCO, keep it minimal and in scope.

## Files in scope

- `docker/docker-compose.ghcr-*.yml` and local-build variants (env vars only)
- `start_windows.cmd`, `start_linux.sh`, `start_macos.sh`
- Optional minimal runtime-config options binding in `GuideAntsApi` (only if needed)
- An optional `docker/docker-compose.host-mounts.generated.example.yml` for gate use

**Out of scope:** data model, endpoints, services, helper scripts (Phase 2),
override generation logic, UI.

## Self-verification

```bash
# from repo root
docker compose -f docker/docker-compose.ghcr-cpu.yml config > /dev/null && echo BASE_OK
# create a representative sample override, then:
docker compose -f docker/docker-compose.ghcr-cpu.yml -f docker/docker-compose.host-mounts.generated.yml config > /dev/null && echo OVERRIDE_OK
```

Plus global gate (orchestration §4.1): server build/tests, client build/tests, and
the docker gate (`docker-gate.md` §3.1–3.3).

## Definition of Done

- [ ] `GuideAntsRuntime__*` env vars present (correct names + D2 value) on the API
      service in the launcher-selected compose files.
- [ ] All three launchers include the generated override **iff present**, unchanged
      behavior when absent; `.installer_state.env` carries the reconstruction state.
- [ ] Docker gate green (compose resolves with and without override; restart command
      unchanged for the no-override baseline).
- [ ] Server + client build/tests unchanged vs baseline.

## Report-back contract (return exactly this)

```
PHASE 1 REPORT
- Env vars added (names + AffectedMountServices value): <list>
- Compose files edited: <list>
- Launchers updated (include-if-present): windows=<y> linux=<y> macos=<y>
- .installer_state.env keys persisted: <list>
- DOCKER GATE: compose WITH override=<ok> WITHOUT override=<ok> launcher include-if-present=<yes>
- Verification: server-build=<pass/fail> server-tests=<counts> client-build=<pass/fail> client-tests=<counts>
- Files touched: <exhaustive list>
- Deviations / surprises: <list or "none">
```
