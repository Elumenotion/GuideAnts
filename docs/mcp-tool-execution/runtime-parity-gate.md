# Runtime Parity Gate (MCP Tool Execution)

Companion to `00-orchestration.md`.

This feature changes how MCP tool calls are **classified** and **dispatched** at runtime.
Correctness depends on parity: the descriptor (`servers[0].url` scheme +
`x-guideants-tool-source`), the authoring classification, and the runtime
`ActionType` dispatch must agree. This gate prevents a split-brain model where the
descriptor says one thing and `ToolCaller` executes another — and proves the
`client://` MCP path is **gone**, not merely hidden.

---

## 1. Gate intent

Pass this gate when all are true:

- Scheme classification is deterministic and shared:
  `http(s)`, `client`, `sandbox`, `tool`, **`mcp+api`**, **`mcp+sandbox`**.
- Descriptor-driven defaults hold (E1): remotes → `api` (`mcp+api://`), packages →
  `sandbox_subprocess` (`mcp+sandbox://`).
- Runtime dispatch maps `mcp+api` → `ActionType.McpApi` and `mcp+sandbox` →
  `ActionType.McpSandbox`.
- **No `client://` MCP route exists** and **no MCP tool call yields
  `pending_client_tool`.**
- Existing non-MCP descriptors (`http(s)`, `client`, `sandbox`, `tool`) keep their exact
  current behavior.

---

## 2. Baseline checks (pre-flight)

Capture the **current** (pre-migration) MCP behavior so the migration is provably a
behavior change in the intended direction only.

### 2.1 Current MCP descriptor smoke

- Identify existing MCP descriptors authored under the prior `client-bridge-first` model
  (`servers[0].url` = `client://mcp-bridge-{id}`) in bootstrap/test fixtures.
- Record that today they classify as ClientHandled and pause with `pending_client_tool`.
  This is the behavior being **removed**.

### 2.2 Scheme dispatch reference

Current dispatch lives in
`src/server/AntRunner.Chat/AntRunner.ToolCalling/Functions/ToolCaller.cs`
(`ActionType` enum + scheme switch mapping `client`/`tool`/`sandbox`). Record the current
mapping table as the baseline.

### 2.3 Baseline command set

```bash
cd src/server && dotnet build GuideAntsApi.sln && dotnet test GuideAntsApi.sln
cd src/client && npm run build && npm test -- --run
```

Record baseline results in `STATUS.md`.

---

## 3. Gate checks

### 3.1 Scheme classification + dispatch parity

Given representative descriptors, frontend helpers and backend `ToolCaller` classify to
the same source kind and runtime action type.

| `servers[0].url` | Source kind | Runtime action type |
|---|---|---|
| `https://api.example.com` | Web API | `WebApi` |
| `client://worm-commander-client` | Client Actions | `ClientHandled` |
| `sandbox://__init__.py` | Sandbox Module | `SandboxHandled` |
| `tool://localhost` | Local Function | `LocalFunction` |
| `mcp+api://{bridgeId}` | MCP (HTTP) | `McpApi` |
| `mcp+sandbox://{bridgeId}` | MCP (stdio package) | `McpSandbox` |

**No `mcp://`-only, `mcp+client://`, or `client://mcp-bridge-*` mapping may exist.** Grep
the dispatch switch and the migration output to prove removal.

### 3.2 Descriptor generation + migration parity

- `runtimeExecution` (`api`|`sandbox_subprocess`) and `discoveryTransport`
  (`streamable_http`|`stdio`) are **distinct** fields in `x-guideants-tool-source`
  (design §3.1).
- Defaults are descriptor-driven (E1): remotes → `api`, packages → `sandbox_subprocess`.
- **Migration:** every pre-existing `client://mcp-bridge-*` descriptor is rewritten on
  save and backfilled on publish to `mcp+api://{bridgeId}` (HTTP) or
  `mcp+sandbox://{bridgeId}` (package), with `runtimeExecution` set accordingly (E4). The
  dev script migrates fixtures/bootstrap. Round-trip is lossless for `bridgeId`,
  `toolNamePrefix`, headers, env, and package metadata.

### 3.3 Runtime execution parity (load-bearing)

For each MCP mode, a real tool call inside `ThreadRun.DoToolCalls` during
`SendMessageStreamAsync` must:

- `api`: call MCP `tools/call` over streamable HTTP with headers resolved via
  `DeserializeForExecution`; return `tool_result`; **continue the turn** (no
  `pending_client_tool`).
- `sandbox_subprocess`: spawn the package command as a `ScriptExecutionAgent` stdio child
  (`initialize` → `tools/call` → teardown), return `tool_result`, continue the turn.

The same executor path is used on notebook, embed, and wire (E3, E15) — verify the wire
path does not re-enter a separate orchestration.

### 3.4 Secret-resolution parity

Header/env `{{secret:VAR}}` templates resolve **only** at tool-call time via
`EnvironmentVariableConfigSerializer.DeserializeForExecution`, not via
`AssistantAuthProvider`. Preview/classification output never contains resolved values.

### 3.5 Compatibility parity (non-MCP untouched)

- `http(s)`, `client` (non-MCP), `sandbox`, `tool` descriptors keep identical behavior.
- Existing operation IDs remain stable across migration.
- No tool loss in edit-save round-trip.

---

## 4. When to run this gate

| Point | Required checks |
|---|---|
| Pre-flight baseline | 2.1, 2.2, 2.3 |
| After Phase 1 | 3.1, 3.2, 3.5 |
| After Phase 2 | 3.1, 3.3 (`api`), 3.4 |
| After Phase 5 | 3.1, 3.3 (`sandbox_subprocess`), 3.4 |
| Final acceptance (Phase 7) | 3.1–3.5 full pass |

---

## 5. Report-back addition (Phases 1, 2, 5, 7)

```text
RUNTIME PARITY GATE:
- Scheme classification + dispatch matrix: <pass/fail + notes>
- Descriptor generation + migration parity: <pass/fail>
- No client:// MCP residue / no pending_client_tool for MCP: <pass/fail>
- Runtime execution parity (modes touched): <pass/fail + test refs>
- Secret-resolution parity (call-time only, no leak): <pass/fail>
- Non-MCP scheme compatibility: <pass/fail>
```
