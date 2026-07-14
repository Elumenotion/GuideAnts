# Host Agent — Locked Decisions

Last updated: 2026-07-12

Resolve `UNDECIDED` items before dispatching implementation phases. Proposal detail
lives in [`../host-agent-and-desktop-runtime-proposal.md`](../host-agent-and-desktop-runtime-proposal.md).

---

## D1 — Agent listen port

**Decision:** `UNDECIDED` (proposal: dynamic free port, persisted in `.installer_state.env`)

**Blocks:** Phase 1

**Notes:** Fixed port (e.g. `17421`) simplifies firewall docs but risks conflicts.
Dynamic + state file matches Electron static-server pattern.

---

## D2 — Agent script location

**Decision:** `UNDECIDED` (proposal: `installer/scripts/guideants-host-agent.mjs`)

**Blocks:** Phase 1

**Notes:** Must ship inside the portable installer bundle, not only repo `docker/`.

---

## D3 — Container → agent call path

**Decision:** `UNDECIDED` (proposal: API server proxies; browser never holds agent token)

**Blocks:** Phase 1

**Options:**

- **(a) API proxy (recommended):** webapi-ui calls agent with server-side token;
  browser calls existing admin API.
- **(b) Direct container → agent:** simpler but token in compose env visible inside
  container (acceptable if agent allowlist is strict).

Electron desktop may call agent directly from main process via IPC (token read from
state file, not renderer).

---

## D4 — Node runtime for agent

**Decision:** `UNDECIDED` (proposal: use `node` on PATH for dev; bundle with Electron
installer for production)

**Blocks:** Phase 3

---

## D5 — macOS / Windows login autostart

**Decision:** `UNDECIDED` (proposal: defer to Phase 3; manual launcher only in Phase 1–2)

**Blocks:** Phase 3

---

## Invariants (non-negotiable)

1. **No `docker.sock`** in `guideants-webapi-ui` or general app containers for this
   feature.
2. **Agent binds `127.0.0.1` only.**
3. **Mutating operations** exec existing helper scripts — no duplicate compose logic
   in the agent.
4. **Scoped restart** with `--no-deps` on affected services only (same set as host
   mounts: `guideants-webapi-ui`, `guideants-ai`, `plantuml`).
5. **WSL co-location:** agent and `docker compose` run in the same OS context.
6. **Fallback:** manual `guideants-host-mount.*` commands remain when agent is down.
