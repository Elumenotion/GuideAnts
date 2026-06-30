# MCP Tool Execution — Locked Decisions (single source of truth)

Last updated: 2026-06-29
Status: LOCKED

This file freezes the design decisions from
[`../mcp-tool-execution-design.md`](../mcp-tool-execution-design.md) §3, §8, and §10
before implementation starts. It also records the **revisions** this design forces on
the previously-locked decisions in
[`../tool-sources-execution/DECISIONS.md`](../tool-sources-execution/DECISIONS.md)
(D1, D2).

Rules:

- If a decision below is `UNDECIDED`, any phase listed under "Blocks" is blocked.
- Changing a locked decision after a phase ships requires reverting and re-dispatching
  the impacted phases (see `00-orchestration.md` §5).
- Subagents must not reinterpret values in this file. The design doc is context; this
  file is the contract.

---

## Part A — Revised cross-folder decisions (supersede `tool-sources-execution`)

These two were locked `client-bridge-first` in the earlier feature. This design
**revises** them. The earlier folder's `DECISIONS.md` must be annotated to point here.

### D1 (revised). MCP scheme strategy → **API-only**

- Status: `LOCKED`
- Previous value: `client-bridge-first` (route MCP through `client://`).
- New value: **API-only.** Remove the `client://` MCP execution path entirely. MCP is
  executed server-side. No client host runs MCP. (design §1, §10; E3)
- Blocks: Phase 1, Phase 2, Phase 5.

### D2 (revised). MCP transport scope

- Status: `LOCKED`
- Previous value: `streamable_http;client_bridge`.
- New value: **`streamable_http` + `stdio`**; drop `client_bridge`. `stdio` lands at
  Phase 5 (design "Phase C"). (design §3.3, §8 D2)
- Blocks: Phase 1 (`discoveryTransport` model), Phase 5 (`stdio` scope).

---

## Part B — Locked design decisions (design §8, E1–E17)

| ID | Decision | Resolved value |
|----|----------|----------------|
| E1 | Default runtime mode | Descriptor-driven: `remotes[]` → `api`, `packages[]` → `sandbox_subprocess`. |
| E2 | API URL scheme | `servers[0].url` = `mcp+api://{bridgeId}`. |
| E3 | API-only everywhere | `api`/`sandbox` MCP execute server-side on notebook, embed, **and** wire — one executor. |
| E4 | Descriptor migration | Save rewrites + publish backfill + dev script. **No `client://` MCP compat path.** |
| E5 | Pooling / timeouts | Per-call MCP client + per-call timeout. No product rate limits. (Pool is v2.) |
| E6 | Localhost reachability | Warn in Guide Builder only; never infer execution mode from hostname. |
| E7 | Stdio session model | Per-call spawn v1 (`initialize` → `tools/call` → teardown). Session pool is v2. |
| E8 | Sandbox URL scheme | `servers[0].url` = `mcp+sandbox://{bridgeId}`. |
| E10 | Node.js | Baked into **full `guideants-ai` image AND slim variant**. |
| E11 | Multiple MCP sources | Yes; **unique `toolNamePrefix`** + unique schema `name` per assistant. |
| E12 | Registry import + apply | **Stage on import; apply on explicit author action** (test/install), never on import. |
| E13 | Wire architecture | Thin protocol adapter over `SendMessageStreamAsync`; **live streaming** all facades. |
| E14 | Wire tool visibility | Server-side MCP is **opaque** in v1; no `tool_calls`/`tool_use` on wire responses. |
| E15 | Published `sandbox_subprocess` | **Same executor** on notebook, embed, and wire (design §5.4). Follows E3 + §6. |
| E16 | Publish vs sandbox apply | **Block publish** when any `sandbox_subprocess` MCP source exists and scoped admin state is staged ≠ applied (`setup-status` hash mismatch). |
| E17 | Egress proxy | Out of scope; assume API outbound network access. |

---

## Part C — Frozen invariants (not open for reinterpretation)

From design §1, §2, §4, §5, §6, §10 and the user's standing rules:

- **OpenAPI descriptor remains canonical.** `servers[0].url` scheme stays the runtime
  dispatch selector. MCP metadata lives in `x-guideants-tool-source` inside
  `SpecificationJson` (no new DB columns for MCP runtime mode).
- **No `pending_client_tool` for MCP.** Under API-only MCP, a tool call never pauses the
  turn waiting for a client host. A turn that emits `pending_client_tool` for an MCP tool
  is a **defect**, not a state.
- **No fallback masking** (user rule: *fallback is a bug generator*). No silent
  `catch {}`, no "assume web API when scheme parse fails", no hostname-based execution-mode
  inference, no quiet downgrade of `api` → client bridge.
- **Secrets never leak.** `{{secret:VAR}}` header/env templates resolve only at
  tool-call time via `EnvironmentVariableConfigSerializer.DeserializeForExecution`; raw
  values never appear in preview payloads, logs, exported JSON, or non-admin responses.
- **Server-side resolution path is shared with sandbox scripts** —
  `ProjectAssistantEnvironment` + `DeserializeForExecution`, **not** `AssistantAuthProvider`.
- **One published runtime.** Wire endpoints are protocol adapters over
  `SendMessageStreamAsync`; no second conversation/tool orchestration engine. Do not add
  MCP-specific logic to wire handlers.
- **Wire refactor is a prerequisite.** The live-streaming wire façade (E13) lands **with
  or before** MCP Phase A so MCP never ships on a broken/duplicative wire path
  (design §6.3, §6.5).
- **Sandbox scope is `projectId + guideId`** — shared venv/packages across notebooks using
  the same guide; install once, run many.
- **No host-local MCP** on the user's machine unless reachable as a remote URL or run as a
  sandbox package (design §1). Accepted capability trade-off.

---

## Part D — Decision ledger

| ID | Decision | Status | Resolved value | Date |
|----|----------|--------|----------------|------|
| D1 (revise) | MCP scheme strategy | LOCKED | API-only; remove `client://` MCP | 2026-06-29 |
| D2 (revise) | MCP transport scope | LOCKED | `streamable_http` + `stdio` (stdio at Phase 5) | 2026-06-29 |
| E1 | Default runtime mode | LOCKED | descriptor-driven | 2026-06-29 |
| E2 | API URL scheme | LOCKED | `mcp+api://{bridgeId}` | 2026-06-29 |
| E3 | API-only everywhere | LOCKED | notebook + embed + wire, one executor | 2026-06-29 |
| E4 | Descriptor migration | LOCKED | save rewrite + publish backfill + dev script | 2026-06-29 |
| E5 | Pooling / timeouts | LOCKED | per-call client + per-call timeout | 2026-06-29 |
| E6 | Localhost reachability | LOCKED | warn in builder only | 2026-06-29 |
| E7 | Stdio session model | LOCKED | per-call v1; pool v2 | 2026-06-29 |
| E8 | Sandbox URL scheme | LOCKED | `mcp+sandbox://{bridgeId}` | 2026-06-29 |
| E10 | Node.js in image | LOCKED | full + slim | 2026-06-29 |
| E11 | Multiple MCP sources | LOCKED | unique prefix + schema name | 2026-06-29 |
| E12 | Registry import + apply | LOCKED | stage on import; apply on action | 2026-06-29 |
| E13 | Wire architecture | LOCKED | thin adapter; live streaming | 2026-06-29 |
| E14 | Wire tool visibility | LOCKED | opaque MCP in v1 | 2026-06-29 |
| E15 | Published sandbox executor | LOCKED | same executor all surfaces | 2026-06-29 |
| E16 | Publish vs sandbox apply | LOCKED | block publish on staged ≠ applied | 2026-06-29 |
| E17 | Egress proxy | LOCKED | out of scope | 2026-06-29 |
