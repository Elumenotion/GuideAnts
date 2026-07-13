# Host Agent and Desktop Runtime — Execution Guide

Last updated: 2026-07-12

Orchestration document for implementing
[`../host-agent-and-desktop-runtime-proposal.md`](../host-agent-and-desktop-runtime-proposal.md).

> **Audience split**
>
> - **Orchestrator** reads this file + [`DECISIONS.md`](./DECISIONS.md) +
>   [`STATUS.md`](./STATUS.md).
> - **Subagents** read only their phase brief (when added) and cited proposal sections.

**Prerequisite:** Host folder mounts feature complete — see
[`../host-mounts-execution/STATUS.md`](../host-mounts-execution/STATUS.md).

---

## Dependency graph

```
 Phase 1  Host agent script + launcher start/stop + compose env + API proxy
    │
    ▼
 Phase 2  Electron --desktop + preload IPC + in-app apply
    │
    ▼
 Phase 3  Installer bundle Electron + operator docs + cross-platform E2E
```

Phase 1 does **not** require Electron changes. Browser admin UI can call apply via
API → agent on day one.

---

## Phase 1 gate (host agent core)

- [ ] `installer/scripts/guideants-host-agent.mjs` listens on `127.0.0.1` only
- [ ] Token auth on mutating routes; health unauthenticated or minimal
- [ ] Apply/remove delegate to existing `guideants-host-mount.*` scripts
- [ ] Launcher starts agent after health check; stop script stops agent
- [ ] `.installer_state.env` extended with agent port/token/pid
- [ ] `GuideAntsRuntime__HostAgentUrl` + `__HostAgentToken` on webapi-ui service
- [ ] Linux compose files include `extra_hosts: host.docker.internal:host-gateway`
- [ ] API endpoint or internal client proxies mount apply to agent (token not exposed to browser)
- [ ] Fallback to manual shell command when agent unreachable
- [ ] WSL: agent runs in same environment as `docker compose`

---

## Phase 2 gate (Electron)

- [ ] `--desktop` / `--no-desktop` flags on launcher
- [ ] Electron loads `http://localhost:5107` when stack healthy
- [ ] Preload IPC: `hostAgent.applyMount` / `removeMount` / `health`
- [ ] Direct Electron open detects missing stack and guides user to launcher

---

## Phase 3 gate (release)

- [ ] Electron binary bundled under `installer/GuideAnts/`
- [ ] Operator docs updated in [`../host-folder-mounts.md`](../host-folder-mounts.md)
- [ ] Cross-platform smoke: Win native, WSL, Linux, macOS
- [ ] Security review: no docker.sock in app containers; token handling

---

## Deviation protocol

Same as [`../host-mounts-execution/00-orchestration.md`](../host-mounts-execution/00-orchestration.md):
record gate failures in [`STATUS.md`](./STATUS.md), resolve open questions in
[`DECISIONS.md`](./DECISIONS.md) before dispatch.
