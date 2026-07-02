# Task — Phase 4: Wire + MCP distribution

> Subagent brief. Execute top to bottom. Return the Report-back contract verbatim.

## Mission

Expose a definition's skills to external agents over the published surfaces. Two modes:
**Mode A** — server-resolved skills already work for any wire client via Phase 1's discovery
block + `skills.list`/`skills.read` (verify across all chat shapes and `/invoke`); **Mode B**
— publish each skill as an MCP / orchestrator resource on `/api/published/mcp` so clients'
own `list_mcp_resources`/`read_mcp_resource` resolve them. Add `skills` trace source tagging.

## Read first

- `../skills-support-proposal.md` §11 (distribution), §17 (trace).
- `../../CodexTrace.md` (the target external-client behavior).
- `./DECISIONS.md` — S3, S4, S6, S7 + invariants (one published runtime; opaque on wire).
- `./wire-distribution-gate.md`, `./progressive-disclosure-gate.md`, `./codeql-gate.md`.
- Existing touchpoints:
  - `src/server/GuideAntsApi/Endpoints/PublishedWire/*` (Chat/Responses/Anthropic handlers)
  - `src/server/GuideAntsApi/Endpoints/PublishedGuidesEndpoints.cs` (`/invoke`; MCP config)
  - `Program.cs` `MapMcp("/api/published/mcp")` and the MCP resource provider
  - `src/server/GuideAntsApi/Services/PublishedWireApi/PublishedApiExecutionContext.cs`
  - `src/server/GuideAntsApi/Services/Conversations/Tracing/TurnTracePayload.cs`
    (`TurnTraceToolDefinition.Source`)

## Preconditions

- **Phase 1 gate green.** S7 (published locator form `skill://<guide>/<name>`) confirmed
  with the user if the default is wrong.

## Guardrails (hard)

- **No skills-specific orchestration in wire handlers.** Everything folds through
  `SendMessageStreamAsync`; skill resolution lives in the shared conversation path / MCP
  resource provider (frozen invariant).
- **Opaque on wire (S4):** `skills.*` run server-side; never surface `pending_client_tool`
  or `tool_calls`/`tool_use` for skill tools on wire responses.
- MCP resource read reuses the **same path-safety** as `skills.read`; skills are scoped to
  the resolved `PublishedApiExecutionContext` guide (no cross-guide leak).
- Gating (S6) applies to the resource listing consistently with discovery visibility.
- Skill bodies/references are returned as **text verbatim**; do not resolve `{{secret:…}}`
  and do not log content (no secret leak).
- No fallback masking.

## Tasks

1. **Mode A verification.** Confirm (add tests if missing) that a skill-bearing published
   guide gets the discovery block and resolves `skills.read` server-side on
   `/chat/completions` (stream + non-stream), `/responses`, Anthropic `/messages`, and the
   `/invoke` one-shot path — no client changes, no `pending_client_tool`.
2. **Mode B resources.** Register a skill MCP resource provider on `/api/published/mcp`:
   `list_mcp_resources` yields `skill://<guide>/<name>` per enabled skill;
   `read_mcp_resource` returns the `SKILL.md` body, and `skill://<guide>/<name>/references/
   <path>` returns a reference file. Enforce path-safety + guide scoping. Apply gating to the
   listing.
3. **Trace tagging.** Extend `TurnTraceToolDefinition.Source` to include `skills`; tag the
   discovery block and `skills.list`/`skills.read` definitions/calls with it.
4. **Tests.** Mode A across three shapes + `/invoke`; Mode B list/read (body + reference +
   path-safety + scoping + gating); trace `Source=skills`; grep proving no skills logic in
   wire handlers.

## Files in scope

- `src/server/GuideAntsApi/Endpoints/PublishedWire/*` (verification + any shared-path hook)
- MCP resource provider registration (near `Program.cs` `MapMcp` / the published MCP service)
- `src/server/GuideAntsApi/Services/Conversations/Tracing/TurnTracePayload.cs` (+ collector)
- Tests under `src/server/GuideAntsApi.IntegrationTests/Endpoints/*` and unit tests.

Out of scope:

- Storage/runtime internals (Phase 1). Authoring (Phase 2). Export/bootstrap (Phase 3).
  Sidecar (Phase 5).

## Self-verification

```bash
cd src/server && dotnet build GuideAntsApi.sln && dotnet test GuideAntsApi.sln
```

Run required gates: `wire-distribution-gate.md`, `progressive-disclosure-gate.md` (locators),
`codeql-gate.md`.

## Definition of Done

- [ ] Mode A works across Chat (stream+non-stream), Responses, Anthropic, and `/invoke`;
      server-resolved; no `pending_client_tool` for `skills.*`.
- [ ] Mode B: `skill://<guide>/<name>` resources list + read (body + reference), path-safe,
      guide-scoped, gating-consistent.
- [ ] Trace `Source=skills` on discovery + skill tool calls.
- [ ] No skills-specific logic in wire handlers (grep).
- [ ] Build/tests green; wire-distribution + progressive-disclosure + CodeQL gates pass.

## Report-back contract (return exactly this)

```text
PHASE 4 REPORT
- Mode A (Chat stream/non-stream, Responses, Anthropic, /invoke) server-resolved: <pass/fail>
- No pending_client_tool for skills.*: <pass/fail + trace ref>
- Mode B list_mcp_resources (skill://<guide>/<name>): <pass/fail>
- Mode B read_mcp_resource (body + reference + path-safety + scope + gating): <pass/fail>
- Trace Source=skills: <pass/fail + trace ref>
- No skills logic in wire handlers (grep): <pass/fail>
- WIRE DISTRIBUTION GATE: <pass/fail summary>
- PROGRESSIVE DISCLOSURE GATE (locators): <pass/fail>
- CODEQL: new-vs-baseline=<count → ids/files or none>
- Verification: server-build=<p/f> server-tests=<counts>
- Files touched: <exhaustive list>
- Deviations / surprises: <list or "none">
```
