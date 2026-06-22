# Task - Phase 3: MCP discovery and descriptor generation

> Subagent brief. Execute top to bottom. Return the Report-back contract verbatim.

## Mission

Add MCP Connection as a first-class Tool Source with connection test, discovery,
selection, descriptor generation, and refresh diff support. Implement the locked MCP
runtime strategy from decisions D1-D3.

## Read first

- `../tool-sources-guide-builder-proposal.md` sections 8.4, 9, 10, 11, 14, 15
  (Phase 3), 16, and 17.
- `./DECISIONS.md` decisions D1, D2, D3.
- `./ui-gate.md` sections 2.3 and 2.8.
- `./runtime-parity-gate.md` and `./codeql-gate.md`.
- Existing runtime touchpoints:
  - `src/server/AntRunner.Chat/AntRunner.ToolCalling/Functions/ToolCaller.cs`
  - `src/server/GuideAntsApi/Services/Conversations/*` (if runtime routing updates are needed)
  - `src/server/GuideAntsApi/Services/Mcp/*`

## Preconditions

- Phase 2 gate green.
- D1, D2, and D3 locked in `DECISIONS.md`.

## Guardrails (hard)

- Discovery output must map into the same operation editor model as other sources.
- Tool mapping stability is mandatory (backing MCP tool id does not drift).
- Do not leak MCP secrets/headers/tokens in non-admin payloads or logs.
- Existing client/sandbox/tool/web runtime behavior must remain unchanged.
- If D1 chooses client-bridge-first, do not partially introduce `mcp://` runtime.
- If D1 chooses dual-mode, runtime branch and tests must be complete in this phase.
- MCP UI must show explicit loading/empty/error/retry states; no silent failures.

## Tasks

1. Add MCP source type in Tool Source picker and source card/type badge.
2. Build MCP connection panel for configured first-release transports (D2), including:
   - connection settings
   - test connection action
   - discover tools action
3. Add backend endpoint/service support for MCP connection test and discovery draft
   generation for operation editor shape.
4. Generate OpenAPI operations from discovered MCP tool schemas and persist metadata
   using the D3 strategy (`x-guideants-tool-source` and/or approved storage fields).
5. Add discovery refresh diff UX with added/changed/removed/disabled states.
6. Implement D1 runtime strategy:
   - `client-bridge-first`: route via `client://` external tool flow.
   - `dual-mode`: add `mcp://` dispatch support (action type, execution branch, tests).
7. Add/extend tests for discovery conversion, stability across refresh, and runtime
   routing behavior for chosen strategy.

## UI behavior details (required)

1. MCP connection panel states
   - `Idle`
   - `Testing connection`
   - `Connected`
   - `Discovery in progress`
   - `Discovery failed` with retry.
2. Discovery result list
   - each tool row shows:
     - discovered name
     - stable backing tool id
     - selected/enabled toggle
     - diff state chip (`Added`, `Changed`, `Removed`, `Disabled`) after refresh.
3. Refresh diff UX
   - user can review changes before applying.
   - previously selected tools remain selected where stable ids match.
4. Validation UX
   - unsupported transport/runtime combinations surface inline and block save/publish.

## Files in scope

Frontend:

- `src/client/src/components/guides/editor/toolSources/*` (MCP panels/helpers)
- `src/client/src/types/guides.ts` (MCP source metadata/editor typings)
- `src/client/src/services/api.ts` (MCP discovery/test endpoints)

Backend:

- `src/server/GuideAntsApi/Endpoints/GuidesEndpoints.cs` (or new endpoints group)
- `src/server/GuideAntsApi/Services/Guides/*` and/or `src/server/GuideAntsApi/Services/Mcp/*`
- `src/server/GuideAntsApi/Models/Guides/*` (DTO additions for discovery/preview)
- `src/server/AntRunner.Chat/AntRunner.ToolCalling/Functions/ToolCaller.cs` (if D1=dual-mode)
- Related tests in `src/server/GuideAntsApi.Tests/*` and client test suites.

Out of scope:

- Full publish-time policy matrix and backend validation completion (Phase 4).
- Storage table/model cleanup migration (Phase 5).

## Self-verification

```bash
cd src/server && dotnet build GuideAntsApi.sln && dotnet test GuideAntsApi.sln
cd src/client && npm run build && npm test -- --run
```

Run required gates:

- `ui-gate.md` phase 3 checks.
- `runtime-parity-gate.md` sections 3.1-3.6 (including MCP strategy behavior).
- `codeql-gate.md` full diff gate.

## Definition of Done

- [ ] MCP source type works end-to-end for connection + discovery + selection.
- [ ] Generated MCP operations integrate with shared operation editor model.
- [ ] Refresh diff identifies added/changed/removed/disabled tools.
- [ ] MCP panel and discovery states meet UI gate contract.
- [ ] D1 runtime strategy is implemented consistently and tested.
- [ ] No secret leakage in responses/logs.
- [ ] Build/tests green, runtime parity pass, and CodeQL diff clean.

## Report-back contract (return exactly this)

```text
PHASE 3 REPORT
- D1 strategy implemented: <client-bridge-first|dual-mode>
- D2 transport scope shipped: <list>
- D3 metadata strategy implemented: <descriptor-extension-only|db-only|both>
- MCP connection/discovery endpoints/services added: <paths>
- MCP discovery refresh diff UX shipped: <yes/no + notes>
- MCP UI state model implemented: <list of states>
- UI GATE: mcp-picker-flow=<pass/fail> mcp-state-flow=<pass/fail> mcp-diff-ux=<pass/fail>
- Runtime routing behavior for MCP verified: <yes/no + test refs>
- RUNTIME PARITY GATE: classification=<pass/fail> generation=<pass/fail> preview=<pass/fail> compatibility=<pass/fail>
- CODEQL: new-vs-baseline=<count -> ids/files or none>
- Verification: server-build=<pass/fail> server-tests=<counts> client-build=<pass/fail> client-tests=<counts>
- Files touched: <exhaustive list>
- Deviations / surprises: <list or "none">
```
