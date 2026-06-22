# Docker Build / Compose Gate (host folder mounts)

Companion to [`00-orchestration.md`](./00-orchestration.md). This feature changes
**Compose wiring** (a generated override), the **`start_*` launchers**, and the
**host helper scripts** — so a docker validation pass is a **first-class
verification gate**, not an afterthought.

Run this gate after Phases **1, 2, 11, 12** and after **any** change under
`docker/`, the root `start_*` launchers, `scripts/guideants-host-mount.*`, or the
override generator.

> Scope note: full image **rebuilds** are only needed when a `Dockerfile`/build
> context changes (this feature mostly adds Compose volumes + env, not image
> layers). The mandatory part of this gate is **compose resolution + scoped
> restart + override idempotency**. Do a real `up`/`build` smoke only where the
> brief calls for an end-to-end run (Phases 1, 2, 12 / final acceptance).

---

## 1. The compose topology this feature touches

- **Base compose files** live in `docker/` (selected by `start_*`):
  `docker-compose.ghcr-cpu.yml` (default), `…ghcr-cuda13.yml`, `…ghcr-rocm.yml`,
  `…ghcr-slim.yml`, and the local-build variants (`docker-compose.cpu.yml`, etc.).
- **Generated override** (new): `docker/docker-compose.host-mounts.generated.yml`.
  It mounts each configured source into the **affected services**
  (DECISIONS D2; default `guideants-webapi-ui;guideants-ai;plantuml`) at
  `/app/HostMounts/{mountKey}`.
- **Launchers** (repo root): `start_windows.cmd`, `start_linux.sh`,
  `start_macos.sh`. They `cd docker` and run
  `docker compose -f <selectedComposeFile> up -d`. They must additionally include
  the generated override **iff it exists**, and persist state in
  `.installer_state.env`.
- **Helper scripts** (new): `scripts/guideants-host-mount.ps1` / `.sh` rewrite the
  generated override and restart **only** affected services.

---

## 2. Baseline (Pre-flight, once)

With **no** generated override present, confirm the currently selected base compose
file resolves, and record it in `STATUS.md`:

```bash
# from repo root; pick the file start_* would select on this machine
docker compose -f docker/docker-compose.ghcr-cpu.yml config > /dev/null && echo "BASE OK"
```

Also record current `docker compose ps` (running services) so you can confirm later
that an affected-only restart did **not** bounce unrelated services.

---

## 3. Gate checks

### 3.1 Compose resolves WITH a representative override

Create (or have the phase create) a representative
`docker/docker-compose.host-mounts.generated.yml` for a **local bind** source, then:

```bash
docker compose \
  -f docker/docker-compose.ghcr-cpu.yml \
  -f docker/docker-compose.host-mounts.generated.yml \
  config > /dev/null && echo "OVERRIDE OK"
```

PASS = resolves with no error, and the merged config shows each affected service
gaining a bind to `/app/HostMounts/{mountKey}` and **nothing else changed**.

### 3.2 Compose resolves WITHOUT the override (no behavior change)

```bash
# remove/rename the generated override, then:
docker compose -f docker/docker-compose.ghcr-cpu.yml config > /dev/null && echo "NO-OVERRIDE OK"
```

PASS = identical to baseline. The launcher must **not** require the override to
exist.

### 3.3 Launchers include the override only if present

Inspect the three `start_*` scripts:

- With the override file present → the emitted/echoed compose command includes
  `-f docker-compose.host-mounts.generated.yml` (after the base `-f`).
- With it absent → the command is exactly the baseline command.
- `.installer_state.env` carries enough to reconstruct the compose command
  (`COMPOSE_FILE`, override file name, affected services, docker dir) for the helper
  scripts.

A dry-run/echo (no real `up`) is sufficient for this check except in the end-to-end
smoke (Phase 12 / final acceptance).

### 3.4 Affected-services restart is `--no-deps` scoped

The helper scripts (and any documented restart) must restart **only** the affected
set, not the whole stack:

```bash
docker compose \
  -f docker/docker-compose.ghcr-cpu.yml \
  -f docker/docker-compose.host-mounts.generated.yml \
  up -d --no-deps guideants-webapi-ui guideants-ai plantuml
```

PASS = the command targets exactly the `AffectedMountServices` set with `--no-deps`.
A bare `up -d` (recreating unrelated services) is a FAIL.

### 3.5 Override rewrite is idempotent

`apply` the same mount twice → the generated override is **byte-identical** the
second time. `remove` a mount → exactly that source's block disappears and the
remaining blocks are untouched. Diff the file across runs to prove it.

### 3.6 SMB branch (when/if pulled forward) does not inline credentials

If the override generator's CIFS branch is exercised, the generated file must
reference a Docker secret / env var (`CredentialRef`) and must **not** contain
`username=`/`password=` in `driver_opts.o` (plan §20.1). Grep the generated file.

### 3.7 Image build smoke (only when a Dockerfile/build context changed)

If a phase changed a `Dockerfile` or build context (not expected for this feature),
build the affected image(s) and confirm a clean `up -d` + health:

```bash
docker compose -f docker/docker-compose.cpu.yml build <service>
# end-to-end smoke (Phase 12 / final acceptance):
# run start_<os>, hit the health URL (http://localhost:5107/), confirm services up
```

---

## 4. When to run it

| Point | Checks | Why |
|---|---|---|
| Pre-flight | 2 | baseline compose resolves, record running services |
| Phase 1 gate | 3.1, 3.2, 3.3 | override include logic + env vars; no behavior change when absent |
| Phase 2 gate | 3.1, 3.4, 3.5, 3.6 | helper-script rewrite idempotency + scoped restart + no inlined creds |
| Phase 11 gate | 3.1, 3.4, 3.5 | remove rewrites override, restarts affected only |
| Phase 12 / final | 3.1–3.5, 3.7 | full validation + end-to-end `up` smoke + health |

---

## 5. Report-back addition for docker-touching phases

Each subagent on Phases 1/2/11 appends to its report:

```
DOCKER GATE:
- compose config WITH override: <ok/fail>  WITHOUT override: <ok/fail>
- launcher includes override only-if-present: <yes/no>  (.installer_state.env complete: <yes/no>)
- restart scoped --no-deps to AffectedMountServices: <yes/n-a>
- override rewrite idempotent (apply x2 byte-identical): <yes/n-a>
- SMB creds inlined into generated override: <no/n-a>
```
