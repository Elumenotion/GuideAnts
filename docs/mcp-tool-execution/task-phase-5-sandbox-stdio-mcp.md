# Task — Phase 5: Sandbox stdio MCP (`runtimeExecution: sandbox_subprocess`)

> Subagent brief. Execute top to bottom. Return the Report-back contract verbatim.

## Mission

Execute registry **packages-only** MCP servers as a `ScriptExecutionAgent` stdio child.
Add `ActionType.McpSandbox`, route the `mcp+sandbox` scheme to it, and implement
`McpSandboxExecutor` to spawn the package command (`npx`/`uvx`/…) with resolved env and
run JSON-RPC `initialize` → `tools/call` → teardown per call (E7). The same executor serves
notebook, embed, and wire (E15). Bake Node.js into the full and slim `guideants-ai` images
(E10). After this phase, a registry PyPI/npm stdio server completes a tool call
(design Phase C exit, runtime side).

## Read first

- `../mcp-tool-execution-design.md` §5 (5.1–5.5), §8 (E7, E8, E10, E15), §1.
- `../script-execution-agent-admin-api-requirements-plan.md` (scoped venv, per-run env).
- `../../src/server/ScriptExecutionAgent/README.md` (`/execute` + `/admin/*` contracts).
- `./DECISIONS.md` — D1, D2 (stdio scope), E7, E8, E10, E15, Part C (scope, no host-local).
- `./runtime-parity-gate.md` §3.1, §3.3 (`sandbox_subprocess`), §3.4.
- `./sandbox-apply-gate.md` §3.1–§3.4.
- `./codeql-gate.md` §6 (command-injection + untrusted-output rules).
- Touchpoints:
  - `src/server/AntRunner.Chat/AntRunner.ToolCalling/Functions/ToolCaller.cs` (`ActionType`)
  - `src/server/AntRunner.Chat/AntRunner.Chat/ThreadRun.cs` (`DoToolCalls`)
  - `src/server/GuideAntsApi/Services/Mcp/*`
  - `src/server/ScriptExecutionAgent/*` (`Program.cs`, `ResolveExecutionEnvironmentAsync`,
    `NotebookMountsRegistry.cs`, `PathGuard.cs`)
  - Docker image definitions for `guideants-ai` (full + slim)

## Preconditions

- Phase 2 gate green (`McpApi` + `ThreadRun` MCP dispatch exists to extend).
- Phase 4 gate green (wire streams live, so `sandbox_subprocess` inherits a correct wire).
- D2 `stdio` scope, E7/E8/E10/E15 locked.

## Guardrails (hard)

- **Same executor on all surfaces (E15).** Route through `ThreadRun`/`ToolCaller`; no
  separate embed/wire sandbox runtime; no publish-time "wait until embed-validated" fork.
- **Per-call spawn v1 (E7):** `initialize` → `tools/call` → teardown. No long-lived session
  pool (that is v2).
- **Do not route registry packages through generic `sandbox://` Python tools** (design
  §5.3). `mcp+sandbox` is its own dispatch.
- **No silent fallback** (user rule): spawn failure / non-zero exit / malformed JSON-RPC
  surface as explicit tool errors — never a quiet downgrade to another transport or a
  generic Python path.
- **Command injection guard (codeql §6):** pass `package.command` + `args` as an argv
  vector and env as a map; never compose a shell string from descriptor/user input.
- **Secrets at call time only**, via `DeserializeForExecution`; never log resolved env.
- **Scope = `projectId + guideId`** (design §5.2): shared venv/packages across notebooks on
  the guide.
- **No host-local MCP** (design §1): only package commands inside the sandbox.

## Tasks

1. Add `ActionType.McpSandbox` to `ToolCaller` and map the `mcp+sandbox` scheme to it.
2. Implement `McpSandboxExecutor` (under `Services/Mcp/`): build the spawn request
   (`package.command`, `args`, resolved `environmentVariables`) and drive the
   `ScriptExecutionAgent` stdio child via JSON-RPC for one tool call, then tear down.
   Enforce a per-call timeout.
3. Add the stdio child capability to `ScriptExecutionAgent` (the "missing layer" per
   design §5): launch a child process with JSON-RPC on stdin/stdout under the scoped
   environment (`ResolveExecutionEnvironmentAsync`), respecting `PathGuard`/mounts.
4. Wire `McpSandbox` dispatch into `ThreadRun.DoToolCalls` so the result continues the turn
   (no `pending_client_tool`), identically to `McpApi`.
5. **Node.js in images (E10):** bake Node into the full `guideants-ai` image **and** the
   slim variant; verify `npx` resolves in both. Keep `uvx`/PyPI runner working for Python
   packages.
6. Add tests: scheme→`McpSandbox` dispatch; executor stdio happy path (mock package);
   explicit error on spawn/exit/JSON-RPC failure (no fallback); env resolution without
   leak; argv-vector (no shell) construction; scope keyed by `projectId+guideId`; same
   executor reached from notebook/embed/wire (seam/trace assertion).

## Files in scope

- `src/server/AntRunner.Chat/AntRunner.ToolCalling/Functions/ToolCaller.cs`
- `src/server/AntRunner.Chat/AntRunner.Chat/ThreadRun.cs`
- `src/server/GuideAntsApi/Services/Mcp/*` (new `McpSandboxExecutor`)
- `src/server/ScriptExecutionAgent/*` (stdio child runtime)
- Docker image definitions for `guideants-ai` (full + slim) — Node install
- Tests: `src/server/GuideAntsApi.Tests/ToolCalling/*`,
  `src/server/GuideAntsApi.Tests/Services/Mcp/*`, ScriptExecutionAgent tests.

Out of scope:

- Registry import staging, apply-on-action UX, and E16 publish gate (Phase 6).
- Wire mapping changes (done in Phases 3–4).

## Self-verification

```bash
cd src/server && dotnet build GuideAntsApi.sln && dotnet test GuideAntsApi.sln
cd src/client && npm run build && npm test -- --run
```

Run required gates:

- `runtime-parity-gate.md` §3.1, §3.3 (`sandbox_subprocess`), §3.4.
- `sandbox-apply-gate.md` §3.1, §3.2, §3.3, §3.4.
- `codeql-gate.md` full diff gate.

## Definition of Done

- [ ] `ActionType.McpSandbox` exists; `mcp+sandbox` scheme dispatches to it.
- [ ] `McpSandboxExecutor` spawns package command via `ScriptExecutionAgent` stdio child;
      per-call `initialize`→`tools/call`→teardown; per-call timeout.
- [ ] Explicit errors on spawn/exit/JSON-RPC failure — no silent fallback.
- [ ] argv-vector + env-map (no shell string); env resolved at call time, not leaked.
- [ ] Scope `projectId+guideId`; same executor on notebook/embed/wire.
- [ ] Node.js in full + slim images (`npx` works in both).
- [ ] Build/tests green; runtime-parity + sandbox-apply (§3.1–3.4) pass; CodeQL clean.

## Report-back contract (return exactly this)

```text
PHASE 5 REPORT
- ActionType.McpSandbox added + mcp+sandbox dispatch: <pass/fail>
- McpSandboxExecutor (per-call spawn, initialize→tools/call→teardown): <paths>
- ScriptExecutionAgent stdio child runtime: <paths>
- Explicit error on spawn/exit/JSON-RPC failure (no fallback): <pass/fail>
- argv-vector + env-map (no shell); env resolved at call time, no leak: <pass/fail>
- Scope projectId+guideId; same executor notebook/embed/wire (E15): <pass/fail>
- Node in images (E10): full=<p/f> slim=<p/f>
- RUNTIME PARITY GATE: classification+dispatch=<p/f> sandbox-execution=<p/f> secret-parity=<p/f>
- SANDBOX APPLY GATE: shared-executor=<p/f> stdio-lifecycle=<p/f> scope=<p/f> node=<p/f>
- CODEQL: new-vs-baseline=<count → ids/files or none>
- Verification: server-build=<p/f> server-tests=<counts> client-build=<p/f> client-tests=<counts>
- Files touched: <exhaustive list>
- Deviations / surprises: <list or "none">
```
