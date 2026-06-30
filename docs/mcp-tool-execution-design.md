# MCP Tool Execution — Design (draft)

Last updated: 2026-06-29  
Status: **DRAFT — iterate here before implementation**

Related:

- [`mcp-tool-execution/00-orchestration.md`](./mcp-tool-execution/00-orchestration.md) — **execution orchestration for this design** (phases, gates, task briefs)
- [`tool-sources-authoring.md`](./tool-sources-authoring.md) — Guide Builder MCP authoring UI
- [`tool-sources-execution/DECISIONS.md`](./tool-sources-execution/DECISIONS.md) — locked D1/D2 (**requires revision** per §8)
- [`tool-sources-guide-builder-proposal.md`](./tool-sources-guide-builder-proposal.md) — original product model
- [`script-execution-agent-admin-api-requirements-plan.md`](./script-execution-agent-admin-api-requirements-plan.md) — scoped venv, per-run env, admin apply
- [`published-openai-wire-continuation-gap-report.md`](./published-openai-wire-continuation-gap-report.md) — wire continuation + tool bridge requirements
- [`published-wire-execution/00-orchestration.md`](./published-wire-execution/00-orchestration.md) — wire refactor orchestration
- [`../src/server/ScriptExecutionAgent/README.md`](../src/server/ScriptExecutionAgent/README.md) — `/execute` and `/admin/*` contracts

---

## 1. Problem statement

MCP tools are authored in Guide Builder and exposed to the model as OpenAPI operations
(`mcp_*`) backed by `client://mcp-bridge-{id}` server URLs. At runtime the server
classifies these as **ClientHandled**, pauses the LLM turn (`pending_client_tool`), and
expects a **client host** to execute the MCP call and post tool results back.

That fails for **in-app notebook chat** (empty assistant bubbles, stuck `streaming` turns)
and for **published wire APIs** that error on `pending_client_tool`. The root cause is
**client-bridge-first** (D1): MCP was routed through `client://` even when the GuideAnts
server could execute the call.

**Locked direction:** **API-only MCP execution** — no client-bridge path for MCP. Two
server modes only:

| `runtimeExecution` | Who calls MCP |
|--------------------|---------------|
| `api` | GuideAnts API (streamable HTTP) |
| `sandbox_subprocess` | `ScriptExecutionAgent` in `guideants-ai` (stdio package) |

Host-local MCP on the user's machine (`127.0.0.1` unreachable from Docker) is **not**
supported unless reachable as a remote URL or run as a sandbox package. That is an
accepted capability trade-off.

A third registry class — [official MCP registry](https://registry.modelcontextprotocol.io/)
**packages-only** (~12% of entries) — uses stdio + env-var auth; **remotes-only** (~84%)
uses streamable HTTP. Both map to the two server modes above.

---

## 2. Browser / localhost (why not client-side MCP)

Browser **origin** blocks direct MCP from the web UI (port/host mismatch, CORS). Secrets
must not live in the client bundle. Server-side execution reuses
`ProjectAssistantEnvironment` and `DeserializeForExecution` (same as sandbox scripts).

Loopback URLs with `runtimeExecution: api` require `host.docker.internal` (or equivalent)
from Docker — warn in Guide Builder; do not infer execution mode from hostname.

---

## 3. Proposed model: explicit runtime execution mode

OpenAPI remains canonical. Extend `x-guideants-tool-source` with **`runtimeExecution`**
distinct from **`discoveryTransport`**.

### 3.1 `runtimeExecution` values (locked)

**Streamable HTTP:**

```json
"x-guideants-tool-source": {
  "kind": "mcp",
  "discoveryTransport": "streamable_http",
  "runtimeExecution": "api",
  "url": "https://mcp.example.com/mcp",
  "bridgeId": "142a96a114a6",
  "headers": { "Authorization": "{{secret:MCP_API_KEY}}" },
  "toolNamePrefix": "mcp_stripe"
}
```

**Stdio package:**

```json
"x-guideants-tool-source": {
  "kind": "mcp",
  "discoveryTransport": "stdio",
  "runtimeExecution": "sandbox_subprocess",
  "bridgeId": "a1b2c3d4e5f6",
  "package": {
    "registryType": "npm",
    "identifier": "@example/mcp-server",
    "command": "npx",
    "args": ["-y", "@example/mcp-server"]
  },
  "environmentVariables": [
    { "name": "EXAMPLE_API_KEY", "secretRef": "{{secret:EXAMPLE_API_KEY}}" }
  ],
  "toolNamePrefix": "mcp_github"
}
```

| `runtimeExecution` | `servers[0].url` (locked) | Dispatch |
|--------------------|---------------------------|----------|
| `api` | `mcp+api://{bridgeId}` | `ActionType.McpApi` |
| `sandbox_subprocess` | `mcp+sandbox://{bridgeId}` | `ActionType.McpSandbox` |

Descriptor-driven defaults: `remotes[]` → `api`; `packages[]` → `sandbox_subprocess`.

### 3.2 Multiple MCP sources per assistant (locked)

**Yes** — one `AssistantOpenApiSchema` row per MCP connection (`customTools[]` in Guide
Builder). Runtime merges all schemas into the model tool list.

Authoring rules:

- **Unique `toolNamePrefix` per MCP source** on the same assistant (default `mcp`
  collides across servers). Operation ids are `{prefix}_{backingToolName}`.
- **Unique schema `name`** per assistant (API already enforces).
- **Shared sandbox scope** — scoped pip/apt under `project + guide` is shared by all
  stdio MCP sources on that guide; install once, run many commands.
- **Shared Guide Environment** — `{{secret:VAR}}` refs are project-scoped; different
  servers reference different variable names.

### 3.3 Discovery transport

| `discoveryTransport` | Behavior |
|----------------------|----------|
| `streamable_http` | API discovers via MCP SDK + resolved headers |
| `stdio` | Spawn package in sandbox; `tools/list` over stdio (same command as runtime) |

### 3.4 Registry import → sandbox (locked)

**Stage on import; apply on explicit author action** (not auto-apply on import):

1. Import / Save writes scoped `requirements.txt`, `apt-packages.txt`,
   `install-scripts.json`, and Environment tab entries.
2. **Test connection** or **Install packages** prompts apply (with confirmation: mutates
   sandbox for all notebooks using this guide in the project).
3. **Publish blocks** if the guide has any `sandbox_subprocess` MCP source and scoped
   admin state is staged but not applied (`setup-status` hashes mismatch). See E16.

---

## 4. Server-side HTTP MCP (`runtimeExecution: api`)

### 4.1 Runtime flow

```text
Model → tool_call mcp_return_policy
  → ToolCaller ActionType.McpApi
  → McpToolExecutor + resolved headers from guide env
  → MCP SDK tools/call
  → tool_result → continue LLM turn (no pending_client_tool)
```

Runs inside `ThreadRun.DoToolCalls` during `SendMessageStreamAsync` — same path for
notebook SSE, published embed SSE, and published wire facades (§6).

### 4.2 Auth

Headers with `{{secret:VAR}}` → `EnvironmentVariableConfigSerializer.DeserializeForExecution`
at tool-call time. Not `AssistantAuthProvider`.

### 4.3 Implementation touchpoints

- [ ] `ToolCaller` — `mcp+api://` → `McpApi`
- [ ] `McpToolExecutor` — per-call v1; pool v2; per-call timeout (no product rate limits)
- [ ] `ThreadRun` — no client partition for MCP
- [ ] Migrate existing `client://mcp-bridge-*` descriptors to `mcp+api://` + `runtimeExecution: api`

---

## 5. Sandbox subprocess MCP (`runtimeExecution: sandbox_subprocess`)

Registry **packages-only** MCP servers run as a **child process** with JSON-RPC on
stdin/stdout. GuideAnts already built the sandbox stack; MCP stdio is the missing layer.

### 5.1 Existing sandbox capabilities

| MCP need | Existing feature |
|----------|------------------|
| Subprocess | `ScriptExecutionAgent` `POST /execute` |
| Env-var auth | `ResolveExecutionEnvironmentAsync` → per-run `environment` |
| pip packages | Scoped `requirements.txt` + admin apply |
| OS / npm deps | `apt-packages.txt`, `install-scripts.json` |
| Operator surface | `SandboxAdmin*` via `guideants-guide-admin` |

**Node.js (E10):** bake into **full `guideants-ai` image and slim variant**.

### 5.2 Runtime flow

```text
Model → tool_call mcp_example_tool
  → ActionType.McpSandbox
  → McpSandboxExecutor (package command + env from metadata)
  → ScriptExecutionAgent stdio child (npx / uvx / …)
  → tools/call → tool_result → continue turn
```

Scope: `projectId + guideId` (shared venv across notebooks using the same guide).

### 5.3 Stdio session model (E7)

Per-call spawn v1 (`initialize` → `tools/call` → teardown); session pool v2 when
latency hurts. Do not route through generic `sandbox://` Python tools for registry
packages.

### 5.4 Published notebook, embed, and wire (locked — E15)

`sandbox_subprocess` uses the **same executor** as notebook chat: `ThreadRun` /
`McpSandboxExecutor` inside `SendMessageStreamAsync`. There is no separate embed or
wire runtime and no publish-time “wait until embed-validated” fork.

| Caller surface | Execution |
|----------------|-----------|
| Private notebook SSE | `SendMessageStreamAsync` → `McpSandbox` |
| Published embed SSE | Same |
| Published wire (Chat / Responses / Messages) | Same (wire is §6 façade only) |

**E3 (API-only everywhere)** already requires this. The only publish gate for stdio MCP
is **E16**: sandbox packages must be applied before publish — not a per-surface decision.

### 5.5 Gaps

- `McpSandbox` action type, stdio discovery in Guide Builder, registry import staging (§3.4)
- Publish-time check for staged ≠ applied (E16)
- Revise D2 to include `stdio` when Phase C starts

---

## 6. Published execution: one engine, wire is a façade

MCP must not add a second published runtime. All surfaces share:

```text
IPublishedConversationService.SendMessageStreamAsync(...)
  → ConversationStreamEngine / ThreadRun
  → StreamingEvent stream (token, assistant_message, usage, error, …)
```

### 6.1 Correct pattern (published embed SSE — reference)

```text
POST …/conversations/{convoId}/messages
  Accept: text/event-stream
  → await foreach (ev in SendMessageStreamAsync)
  → WriteSseEventAsync(ev)   // live, no re-orchestration
```

### 6.2 Defective pattern (shipped wire — must not grow)

`PublishedOpenAiWireEndpoints.cs` today:

- Rejects `stream: true` on Chat Completions / Responses (`unsupported_feature`).
- Private `ExecuteConversationAsync` creates a **new** `wire-{timestamp}` conversation
  every request (no continuation).
- Buffers `SendMessageStreamAsync` into `StringBuilder`; drops token deltas after first
  non-token event.
- Errors on `pending_client_tool` — breaks MCP under old client-bridge routing.

Wire-continuation handlers (`PublishedWire/*`) improve continuation and accept
`stream: true`, but still **buffer-then-emit** pseudo-SSE (`CollectWireConversationResultAsync`
→ `BuildOpenAiChatCompletionsSsePayload` / `BuildAnthropicMessageSsePayload` with full text
in one `content_block_delta`). That is **not** live streaming.

### 6.3 Required wire architecture (locked)

Wire endpoints are **protocol adapters only** — no duplicate conversation/tool orchestration:

```text
POST /api/published/openai/{pubId}/v1/chat/completions
POST /api/published/openai/{pubId}/v1/responses
POST /api/published/anthropic/{pubId}/v1/messages
  │
  ├─ Request adapter (messages/input/tools/stream/conversation ids → SendMessageRequest + resolved convoId)
  │
  ├─ await foreach (ev in SendMessageStreamAsync(...))     ← single execution path
  │
  └─ Response adapter
       stream=true  → flush provider-shaped SSE as events arrive
       stream=false → fold same stream to final JSON (convenience, not a separate executor)
```

**Live streaming mapping (required, not optional):**

| `StreamingEvent` | OpenAI Chat (`stream: true`) | OpenAI Responses | Anthropic Messages |
|------------------|------------------------------|------------------|---------------------|
| `token` | `chat.completion.chunk` delta | output text delta | `content_block_delta` `text_delta` |
| `assistant_message` | final chunk / role | output item complete | block complete |
| `usage` | trailing usage chunk | usage on response | `message_delta` usage |
| `error` | error object | error | error event |

**Non-streaming wire** returns final assistant text after the full turn (including
server-side MCP tool loops). Wire clients do **not** see intermediate `tool_calls` or
`tool` messages in v1 — MCP is opaque inside `ThreadRun`. That is acceptable for
drop-in OpenAI/Anthropic clients; provider-faithful tool visibility is out of scope v1.

**Do not** add MCP-specific logic to wire handlers. Fix `ThreadRun` / `McpToolExecutor`
once; all facades inherit.

### 6.4 Duplication to eliminate

| Location | Problem |
|----------|---------|
| `PublishedOpenAiWireEndpoints.ExecuteConversationAsync` | Inline buffer + new convo per request |
| `PublishedGuidesEndpoints` invoke | Third copy of buffer + `pending_client_tool` handling |
| `WireConversationExecutor.CollectWireConversationResultAsync` | OK for `stream: false` fold only; **not** for streaming paths |

Consolidate on shared `WireStreamAdapter` (names TBD) used by Chat Completions, Responses,
and Messages. Align with [`published-wire-execution/`](./published-wire-execution/) and
[`published-openai-wire-continuation-gap-report.md`](./published-openai-wire-continuation-gap-report.md).

### 6.5 MCP + wire interaction (locked)

Under API-only MCP:

- No `pending_client_tool` for MCP tool calls.
- Wire `stream: true` streams **assistant text tokens** while server executes MCP
  internally between model rounds (same as embed SSE, different wire encoding).
- Shipped OpenAI wire rejection of `stream: true` is a **bug**, not a product constraint.
- Anthropic Messages `stream: true` pseudo-SSE is **insufficient** — must map live tokens.

**Prerequisite:** wire refactor (§6.3) should land **with or before** MCP Phase A so MCP
does not ship on a broken or duplicative wire path.

---

## 7. Guide Builder UI (draft)

| Control | Notes |
|---------|-------|
| **Runtime execution** | `api` or `sandbox_subprocess` only (no client host) |
| **toolNamePrefix** | Required unique per MCP source on same assistant |
| **Package / URL / headers / env** | Per §3 |
| **Sandbox setup** | Staged vs applied status; apply on test/install |
| **Generated server URL** | `mcp+api://` or `mcp+sandbox://` only |

---

## 8. Locked decisions

| ID | Decision | Status |
|----|----------|--------|
| D1 (revise) | MCP scheme: **API-only**; remove `client://` MCP path | **LOCKED** |
| D2 (revise) | Transports: `streamable_http` + `stdio`; drop `client_bridge` | **LOCKED** (stdio at Phase C) |
| E1 | Default runtime | Descriptor-driven: remotes → `api`, packages → `sandbox_subprocess` | **LOCKED** |
| E2 | API URL scheme | `mcp+api://{bridgeId}` | **LOCKED** |
| E3 | Revise D1 | API-only everywhere (notebook, embed, wire) | **LOCKED** |
| E4 | Migrate descriptors | Save rewrites + publish backfill + dev script; no `client://` compat | **LOCKED** |
| E5 | Pooling / timeouts | Per-call client + per-call timeout; no product rate limits | **LOCKED** |
| E6 | Localhost reachability | Warn in builder only | **LOCKED** |
| E7 | Stdio session model | Per-call v1; session pool v2 | **LOCKED** |
| E8 | Sandbox URL scheme | `mcp+sandbox://{bridgeId}` | **LOCKED** |
| E10 | Node.js | Bake into full AI image **and** slim variant | **LOCKED** |
| E11 | Multiple MCP sources | Yes; unique `toolNamePrefix` + schema name | **LOCKED** |
| E12 | Registry import + apply | Stage on import; apply on explicit action (not on import) | **LOCKED** |
| E13 | Wire architecture | Thin adapter over `SendMessageStreamAsync`; live streaming all facades | **LOCKED** |
| E14 | Wire tool visibility | Opaque server-side MCP in v1; no `tool_calls` on wire response | **LOCKED** |
| E15 | Published `sandbox_subprocess` | **Same executor** on notebook, embed, and wire (§5.4); follows from E3 + §6 | **LOCKED** |
| E16 | Publish vs sandbox apply | **Block publish** when any `sandbox_subprocess` MCP source exists and scoped admin staged ≠ applied | **LOCKED** |
| E17 | Egress proxy | Out of scope — assume API outbound access | **LOCKED** |

**Action:** update [`tool-sources-execution/DECISIONS.md`](./tool-sources-execution/DECISIONS.md)
and re-dispatch impacted phases per that file's rules.

---

## 9. Phased delivery

### Phase A — Notebook MCP (HTTP)

- `runtimeExecution: api` in notebook chat via `McpApi` + `ThreadRun`
- Server-side secret resolution; descriptor migration (MCP Test)
- **Wire refactor start:** live `stream: true` on Chat Completions at minimum (§6.3)

**Exit:** return-policy tool call completes with assistant text in notebook.

### Phase B — Hardening

- Publish validation, usage/trace hooks, builder warnings
- Wire: Responses + Anthropic Messages live streaming; continuation parity
- Delete duplicate buffer paths in `PublishedOpenAiWireEndpoints` / invoke

### Phase C — Sandbox stdio packages

- `sandbox_subprocess` end-to-end (notebook, embed, wire — same path)
- Registry import staging; Node in image; publish gate E16
- Revise D2 `stdio` scope when this phase starts

**Exit:** registry PyPI/npm stdio server completes tool call via notebook or published
surface.

---

## 10. Non-goals (v1)

- `client_host` / `client://` MCP execution
- Browser-direct MCP; hostname inference for execution mode
- MCP auth via `AssistantAuthProvider`
- Wire exposure of MCP `tool_calls` / `tool_use` steps to external clients
- OCI MCP without install-script support
- Per-connection product rate limits; egress proxy / allowlist

---

## 11. Revision log

| Date | Change |
|------|--------|
| 2026-06-29 | Initial draft |
| 2026-06-29 | §5 sandbox subprocess |
| 2026-06-29 | Locked API-only; removed client_host; §6 wire façade + live streaming; decisions E1–E17 |
| 2026-06-29 | E15 same executor on all published surfaces; E16 strict publish block for unapplied sandbox |
| 2026-06-29 | Added execution orchestration folder `mcp-tool-execution/` (conductor, DECISIONS, STATUS, 3 gates, 7 phase briefs) |
