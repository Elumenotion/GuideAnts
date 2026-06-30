# Task — Phase 1: Descriptor model + migration

> Subagent brief. Execute top to bottom. Return the Report-back contract verbatim.

## Mission

Introduce the explicit **runtime execution mode** model for MCP tool sources and migrate
every existing `client://mcp-bridge-*` descriptor off the removed client-bridge path.
After this phase, MCP descriptors carry `runtimeExecution` + `discoveryTransport` and use
`mcp+api://{bridgeId}` or `mcp+sandbox://{bridgeId}` as `servers[0].url`. No runtime
execution wiring yet — that is Phases 2 (`api`) and 5 (`sandbox_subprocess`). This phase
is the data/model foundation both depend on.

## Read first

- `../mcp-tool-execution-design.md` §3 (3.1–3.4), §8 (E1, E2, E4, E8), §10.
- `./DECISIONS.md` — D1 (revised, API-only), D2 (revised, transports), E1, E2, E4, E8,
  and Part C invariants.
- `./runtime-parity-gate.md` §2, §3.1, §3.2, §3.5.
- `./codeql-gate.md` §6 (secret/JSON-handling rules).
- Existing MCP authoring/runtime touchpoints (inventory before editing):
  - `src/server/GuideAntsApi/Services/Mcp/*` (discovery, descriptor generator, middleware)
  - `src/server/GuideAntsApi/Services/Guides/ToolSourceValidator.cs`
  - `src/server/AntRunner.Chat/AntRunner.ToolCalling/Functions/ToolCaller.cs`
    (`ActionType` enum + scheme dispatch ~lines 16–37, 228–231)
  - `src/client/src/components/guides/editor/toolSources/*`
    (`McpConnectionPanel.tsx`, `openApiDescriptorBuilder.ts`, classification helpers)

## Preconditions

- `DECISIONS.md` D1/D2 revised and E1/E2/E4/E8 locked.
- Pre-flight baselines captured in `STATUS.md`, including the current
  `client://mcp-bridge-*` classification (runtime-parity §2).

## Guardrails (hard)

- `runtimeExecution` (`api` | `sandbox_subprocess`) and `discoveryTransport`
  (`streamable_http` | `stdio`) are **distinct** fields in `x-guideants-tool-source`.
  Do not conflate them.
- **No `client://` MCP path may survive.** Remove client-bridge MCP routing/authoring;
  do not leave a compatibility shim (E4, D1). Non-MCP `client://` (real client tools)
  stays untouched.
- **No hostname-based mode inference** (E6, user rule): mode is descriptor-driven (E1),
  never guessed from `localhost`/`127.0.0.1`.
- Metadata lives only in `x-guideants-tool-source` inside `SpecificationJson` — no new DB
  columns for runtime mode (Part C). A migration is allowed **only** if it rewrites stored
  descriptor JSON, not schema.
- Secrets: headers/env keep `{{secret:VAR}}` templates verbatim in the descriptor; never
  resolve or store raw values here.
- No new silent `catch {}`; descriptor parse/validation failures are explicit errors.

## Tasks

1. Extend the `x-guideants-tool-source` model (backend DTO + client typings) with
   `runtimeExecution`, `discoveryTransport`, `bridgeId`, `toolNamePrefix`, `url`/`headers`
   (HTTP), and `package` + `environmentVariables` (stdio) per design §3.1.
2. Set `servers[0].url` generation: `mcp+api://{bridgeId}` for `api`,
   `mcp+sandbox://{bridgeId}` for `sandbox_subprocess` (E2, E8). Apply descriptor-driven
   defaults (E1): remotes → `api`, packages → `sandbox_subprocess`.
3. Update classification helpers (frontend) and `ToolSourceValidator` (backend) to
   recognize `mcp+api`/`mcp+sandbox` schemes as MCP sources and validate scheme ↔
   `runtimeExecution` consistency. Reject mismatches explicitly.
4. **Remove the client-bridge MCP path:** delete/retire `client://mcp-bridge-*` generation
   and any MCP→`ClientHandled` routing assumptions in authoring. Keep generic `client://`
   (non-MCP client tools) intact.
5. **Migrate existing descriptors (E4):**
   - Save-time rewrite: on guide save, rewrite any `client://mcp-bridge-*` MCP descriptor
     to the new scheme + fields.
   - Publish-time backfill: ensure published snapshots are rewritten.
   - Dev script: a one-shot script (under `scripts/`) that rewrites bootstrap/fixture
     descriptors. Round-trip must preserve `bridgeId`, `toolNamePrefix`, headers, env, and
     package metadata.
6. Add/extend tests: model serialization, scheme generation, classification + validator
   parity, mismatch rejection, and migration round-trip (`client://mcp-bridge-*` →
   `mcp+api://` / `mcp+sandbox://`).

## Files in scope

Backend:

- `src/server/GuideAntsApi/Services/Mcp/*` (descriptor generator, model)
- `src/server/GuideAntsApi/Services/Guides/ToolSourceValidator.cs`
- `src/server/GuideAntsApi/Services/Guides/*` (save/publish descriptor rewrite hooks)
- `src/server/GuideAntsApi/Models/Guides/*` (DTO additions)
- `scripts/` (dev migration script)
- Tests in `src/server/GuideAntsApi.Tests/Services/Mcp/*`,
  `.../Services/Guides/ToolSourceValidatorTests.cs`.

Frontend:

- `src/client/src/components/guides/editor/toolSources/openApiDescriptorBuilder.ts`
- `src/client/src/components/guides/editor/toolSources/*` classification helpers + MCP panel typings
- `src/client/src/components/guides/editor/toolSources/__tests__/*`

Out of scope:

- Runtime `ActionType.McpApi`/`McpSandbox` dispatch + executors (Phases 2, 5).
- Wire changes (Phases 3, 4).
- Guide Builder runtime-mode UX controls and publish gate (Phase 6).

## Self-verification

```bash
cd src/server && dotnet build GuideAntsApi.sln && dotnet test GuideAntsApi.sln
cd src/client && npm run build && npm test -- --run
```

Run required gates:

- `runtime-parity-gate.md` §3.1, §3.2, §3.5.
- `codeql-gate.md` full diff gate.

## Definition of Done

- [ ] `runtimeExecution` + `discoveryTransport` model exists end-to-end (DTO + typings).
- [ ] Scheme generation: `mcp+api://`/`mcp+sandbox://`; defaults descriptor-driven (E1).
- [ ] Classification + validator recognize the new schemes and reject scheme/mode mismatch.
- [ ] No `client://` MCP path remains (grep-proven); non-MCP `client://` untouched.
- [ ] Migration (save rewrite + publish backfill + dev script) round-trips losslessly.
- [ ] Build/tests green; runtime-parity §3.1/§3.2/§3.5 pass; CodeQL diff clean.

## Report-back contract (return exactly this)

```text
PHASE 1 REPORT
- runtimeExecution/discoveryTransport model added: <paths>
- URL scheme generation (mcp+api / mcp+sandbox): <pass/fail>
- Descriptor-driven defaults (remotes→api, packages→sandbox): <pass/fail>
- client:// MCP path removed (no compat shim): <yes/no + grep evidence>
- Migration shipped: save-rewrite=<y/n> publish-backfill=<y/n> dev-script=<path>
- Migration round-trip lossless: <yes/no + test refs>
- RUNTIME PARITY GATE: classification=<p/f> generation+migration=<p/f> non-mcp-compat=<p/f>
- CODEQL: new-vs-baseline=<count → ids/files or none>
- Verification: server-build=<p/f> server-tests=<counts> client-build=<p/f> client-tests=<counts>
- Files touched: <exhaustive list>
- Deviations / surprises: <list or "none">
```
