# Task — Phase 2: HTTP MCP runtime execution (`runtimeExecution: api`)

> Subagent brief. Execute top to bottom. Return the Report-back contract verbatim.

## Mission

Execute `mcp+api://` tool calls **server-side** inside the live turn. Add
`ActionType.McpApi`, route the `mcp+api` scheme to it in `ToolCaller`, and implement
`McpToolExecutor` to call MCP `tools/call` over streamable HTTP with headers resolved at
call time. The call runs inside `ThreadRun.DoToolCalls` during `SendMessageStreamAsync`,
returns a `tool_result`, and **continues the turn** — there is no `pending_client_tool`
for MCP. After this phase, a notebook return-policy tool call completes with assistant
text (design Phase A exit).

## Read first

- `../mcp-tool-execution-design.md` §4 (4.1–4.3), §1, §2, §8 (E2, E3, E5), §10.
- `./DECISIONS.md` — D1 (API-only), E2, E3, E5, Part C invariants
  (no `pending_client_tool` for MCP; secrets at call time; shared execution path).
- `./runtime-parity-gate.md` §3.1, §3.3, §3.4.
- `./codeql-gate.md` §6 (SSRF + secret-leakage rules).
- Runtime touchpoints:
  - `src/server/AntRunner.Chat/AntRunner.ToolCalling/Functions/ToolCaller.cs`
    (`ActionType` enum + scheme dispatch; `ExecuteWebApiAsync` as a structural reference)
  - `src/server/AntRunner.Chat/AntRunner.Chat/ThreadRun.cs` (`DoToolCalls`)
  - `src/server/GuideAntsApi/Services/Mcp/*`
  - `src/server/GuideAntsApi/Services/EnvironmentVariables/EnvironmentVariableConfigSerializer.cs`
    (`DeserializeForExecution`)

## Preconditions

- Phase 1 gate green (`mcp+api://` descriptors exist; classification/validator updated;
  `client://` MCP path removed).
- D1/E2/E3/E5 locked.

## Guardrails (hard)

- **No `pending_client_tool` for MCP.** The executor resolves the call in-process and
  returns a `tool_result` that continues the turn. No client partition for MCP
  (design §4.3).
- **No client-bridge re-entry.** Do not route `api` back through any `client://` path
  (D1). No `api` → client downgrade on error — failures are explicit tool errors.
- **Secrets resolve at call time only**, via
  `EnvironmentVariableConfigSerializer.DeserializeForExecution`, not
  `AssistantAuthProvider` (design §4.2). Never log resolved headers; never echo them into
  results or traces.
- **Per-call client + per-call timeout (E5).** No connection pooling (that is v2), no
  product rate limits.
- Same execution path serves notebook, embed, and wire (E3) — do not add an MCP-specific
  branch outside `ThreadRun`/`ToolCaller`.
- No new silent `catch {}`; MCP transport/SDK errors surface explicitly.

## Tasks

1. Add `ActionType.McpApi` to the `ToolCaller` enum and map the `mcp+api` scheme to it in
   the scheme dispatch (alongside `client`/`tool`/`sandbox`).
2. Implement `McpToolExecutor` (under `Services/Mcp/`): given the descriptor metadata
   (`bridgeId`, `url`, `headers`, `toolNamePrefix`) and the model's tool-call arguments,
   open a per-call MCP client over streamable HTTP, resolve headers via
   `DeserializeForExecution`, invoke `tools/call`, and return the structured result.
   Enforce a per-call timeout.
3. Wire `McpApi` dispatch into `ThreadRun.DoToolCalls` so the result is appended as a
   `tool_result` and the LLM turn continues (design §4.1). Confirm the path is the same
   for notebook SSE today (embed/wire inherit via Phases 3–4).
4. Map the prefixed operation id back to the backing MCP tool name
   (`{toolNamePrefix}_{backingToolName}`) for the `tools/call` request.
5. Add tests: scheme→`McpApi` dispatch; executor `tools/call` happy path (mock MCP server);
   header secret resolution at call time without leakage; per-call timeout; turn continues
   with assistant text and **no** `pending_client_tool`.

## Files in scope

- `src/server/AntRunner.Chat/AntRunner.ToolCalling/Functions/ToolCaller.cs`
- `src/server/AntRunner.Chat/AntRunner.Chat/ThreadRun.cs`
- `src/server/GuideAntsApi/Services/Mcp/*` (new `McpToolExecutor`)
- DI registration where MCP services are registered (`Program.cs` MCP section)
- Tests: `src/server/GuideAntsApi.Tests/ToolCalling/ToolCallerTests.cs`,
  `src/server/GuideAntsApi.Tests/Services/Mcp/*`

Out of scope:

- `sandbox_subprocess` execution (Phase 5).
- Wire façade streaming (Phases 3, 4) — Phase 2 proves it on the notebook path.
- Builder UI + publish gate (Phase 6).

## Self-verification

```bash
cd src/server && dotnet build GuideAntsApi.sln && dotnet test GuideAntsApi.sln
cd src/client && npm run build && npm test -- --run
```

Run required gates:

- `runtime-parity-gate.md` §3.1, §3.3 (`api`), §3.4.
- `codeql-gate.md` full diff gate.

## Definition of Done

- [ ] `ActionType.McpApi` exists; `mcp+api` scheme dispatches to it.
- [ ] `McpToolExecutor` calls `tools/call` over streamable HTTP, per-call client + timeout.
- [ ] Headers resolved at call time via `DeserializeForExecution`; no secret leak in logs/
      results/traces.
- [ ] Runs in `ThreadRun.DoToolCalls`; turn continues; **no `pending_client_tool`** for MCP.
- [ ] Notebook return-policy tool call completes with assistant text (Phase A exit).
- [ ] Build/tests green; runtime-parity §3.1/§3.3/§3.4 pass; CodeQL diff clean.

## Report-back contract (return exactly this)

```text
PHASE 2 REPORT
- ActionType.McpApi added + mcp+api dispatch: <pass/fail>
- McpToolExecutor (per-call client + timeout): <paths>
- Header secret resolution at call time (no leak): <pass/fail>
- Runs in ThreadRun.DoToolCalls; turn continues (no pending_client_tool): <pass/fail>
- Notebook return-policy tool call completes with assistant text: <yes/no + test refs>
- RUNTIME PARITY GATE: classification+dispatch=<p/f> api-execution=<p/f> secret-parity=<p/f>
- CODEQL: new-vs-baseline=<count → ids/files or none>
- Verification: server-build=<p/f> server-tests=<counts> client-build=<p/f> client-tests=<counts>
- Files touched: <exhaustive list>
- Deviations / surprises: <list or "none">
```
