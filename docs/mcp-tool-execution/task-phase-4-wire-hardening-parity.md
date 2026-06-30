# Task — Phase 4: Wire hardening + parity (design Phase B)

> Subagent brief. Execute top to bottom. Return the Report-back contract verbatim.

## Mission

Extend live streaming to **OpenAI Responses** and **Anthropic Messages** through the same
`WireStreamAdapter`, and **delete** the duplicate buffer paths the design calls out
(design §6.4). After this phase, all three published wire facades stream live, fold to JSON
identically on `stream: false`, and treat server-side MCP as opaque (E14). This completes
the wire side of design Phase A/B so MCP can safely reach the wire in Phase 5.

## Read first

- `../mcp-tool-execution-design.md` §6 (6.3 mapping table, 6.4 duplication, 6.5 MCP+wire),
  §8 (E13, E14).
- `./DECISIONS.md` — E13, E14, Part C (one published runtime).
- `./wire-streaming-gate.md` §3 (all rows), §3.1–§3.4.
- Wire touchpoints:
  - `src/server/GuideAntsApi/Endpoints/PublishedWire/PublishedAnthropicWireHandler.cs`
  - `src/server/GuideAntsApi/Endpoints/PublishedWire/*` (Responses handler/serializer)
  - `src/server/GuideAntsApi/Endpoints/PublishedOpenAiWireEndpoints.cs`
    (`ExecuteConversationAsync` inline buffer)
  - `src/server/GuideAntsApi/Endpoints/.../PublishedGuidesEndpoints*` invoke buffer copy
  - `src/server/GuideAntsApi/Endpoints/PublishedWire/WireConversationExecutor.cs`

## Preconditions

- Phase 3 gate green (`WireStreamAdapter` exists; Chat live; fold parity proven).
- E13/E14 locked.

## Guardrails (hard)

- Reuse the **same** `WireStreamAdapter` from Phase 3 — do not introduce a second adapter
  per provider. Provider differences are encoding-only.
- **No buffer-then-emit** on any `stream: true` path (Anthropic pseudo-SSE single
  `content_block_delta` is the specific defect to kill — design §6.5).
- **Delete, don't disable**, the duplicate buffer paths (design §6.4):
  - `PublishedOpenAiWireEndpoints.ExecuteConversationAsync` inline buffer + per-request convo
  - `PublishedGuidesEndpoints` invoke buffer copy (third copy)
  - keep `WireConversationExecutor.CollectWireConversationResultAsync` **only** for the
    `stream: false` fold.
- **No MCP-specific logic in wire handlers** (E14). Wire clients see assistant text only;
  no `tool_calls`/`tool_use`/`tool` messages.
- Preserve continuation parity already shipped in `published-wire-execution` across all
  three providers. Do not regress error shapes.
- No new silent `catch {}`.

## Tasks

1. Implement Responses mapping in the adapter (design §6.3): `token` → output text delta;
   `assistant_message` → output item complete; `usage` → usage on response; `error` →
   error event. Wire `stream: true` Responses through it.
2. Implement Anthropic Messages mapping: `token` → `content_block_delta` `text_delta`;
   `assistant_message` → block complete; `usage` → `message_delta` usage; `error` → error
   event. Replace the pseudo-SSE single-delta path with live deltas.
3. Delete the duplicate buffer paths listed in Guardrails; route all three providers'
   `stream: false` through the single fold.
4. Verify MCP opacity end-to-end: with a Phase-2 `mcp+api://` source, a wire `stream: true`
   turn streams assistant tokens while MCP runs server-side between rounds; no tool steps
   surface; no `pending_client_tool`.
5. Add/extend tests for Responses + Anthropic incremental flush, finish/stop reasons,
   `stream: false` fold parity, continuation parity, error-shape parity, and MCP opacity on
   the wire.

## Files in scope

- `src/server/GuideAntsApi/Endpoints/PublishedWire/*` (Responses + Anthropic mapping in the
  shared adapter; serializers)
- `src/server/GuideAntsApi/Endpoints/PublishedOpenAiWireEndpoints.cs` (delete inline buffer)
- `src/server/GuideAntsApi/Endpoints/.../PublishedGuidesEndpoints*` (delete invoke buffer copy)
- Tests: `src/server/GuideAntsApi.IntegrationTests/Services/Conversations/*`,
  `src/server/GuideAntsApi.Tests/.../PublishedWire*`.

Out of scope:

- Sandbox stdio executor (Phase 5).
- Builder UI + publish gate (Phase 6).

## Self-verification

```bash
cd src/server && dotnet build GuideAntsApi.sln && dotnet test GuideAntsApi.sln
cd src/client && npm run build && npm test -- --run
```

Run required gates:

- `wire-streaming-gate.md` §3.1–§3.4 (Chat + Responses + Anthropic).
- `codeql-gate.md` if the diff touches connection/secret/JSON handling.

## Definition of Done

- [ ] Responses + Anthropic stream live through the shared adapter (design §6.3 mapping).
- [ ] Anthropic pseudo-SSE single-delta path removed.
- [ ] Duplicate buffer paths deleted; `stream: false` folds the single stream for all three.
- [ ] MCP opaque on wire (no tool steps; no `pending_client_tool`).
- [ ] Continuation + error-shape parity preserved across providers.
- [ ] Build/tests green; wire-streaming gate full pass; CodeQL clean (if applicable).

## Report-back contract (return exactly this)

```text
PHASE 4 REPORT
- Responses live mapping: <pass/fail>
- Anthropic live mapping (pseudo-SSE removed): <pass/fail>
- Duplicate buffer paths deleted: <files removed>
- stream:false fold parity (all three providers): <pass/fail>
- MCP opacity on wire (no tool_calls; no pending_client_tool): <pass/fail>
- Continuation + error-shape parity preserved: <pass/fail>
- WIRE STREAMING GATE: chat=<p/f> responses=<p/f> anthropic=<p/f> fold=<p/f> single-engine=<p/f>
- CODEQL: new-vs-baseline=<count → ids/files or none / n-a>
- Verification: server-build=<p/f> server-tests=<counts> client-build=<p/f> client-tests=<counts>
- Files touched: <exhaustive list>
- Deviations / surprises: <list or "none">
```
