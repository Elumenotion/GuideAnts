# Task — Phase 3: Wire live-streaming adapter (prerequisite)

> Subagent brief. Execute top to bottom. Return the Report-back contract verbatim.

## Mission

Convert the published wire path from buffer-then-emit to **live** streaming for OpenAI
Chat Completions. Introduce a shared `WireStreamAdapter` (name TBD) that consumes
`SendMessageStreamAsync` and flushes provider-shaped SSE as `StreamingEvent`s arrive. Make
`stream: false` a fold of the same stream. This is the **prerequisite** that lets MCP ship
on a correct wire (design §6.3, §6.5) — it must land with or before MCP reaches a published
surface. Responses + Anthropic are Phase 4.

## Read first

- `../mcp-tool-execution-design.md` §6 (6.1–6.5), §8 (E13, E14).
- `../published-openai-wire-continuation-gap-report.md` (wire continuation + tool bridge).
- `../published-wire-execution/00-orchestration.md` and `.../STATUS.md` (what already
  shipped vs. what still buffers).
- `./DECISIONS.md` — E13, E14, Part C (one published runtime; wire is a façade).
- `./wire-streaming-gate.md` §2, §3 (Chat row), §3.1, §3.2, §3.4.
- Wire touchpoints:
  - `src/server/GuideAntsApi/Endpoints/PublishedOpenAiWireEndpoints.cs`
    (`ExecuteConversationAsync`, the `stream: true` rejection)
  - `src/server/GuideAntsApi/Endpoints/PublishedWire/*`
    (`PublishedOpenAiChatWireHandler.cs`, `WireConversationExecutor.cs`,
    `WireResponseSerializer.cs`, `WireStreamPayloadReader.cs`)

## Preconditions

- Phase 1 gate green. (Phase 3 may run in parallel with Phase 2 — disjoint files.)
- E13/E14 locked.

## Guardrails (hard)

- **No buffer-then-emit on `stream: true`.** No `StringBuilder` accumulation of the full
  assistant message before emit; no single `content_block_delta`/chunk carrying the whole
  text. Tokens flush incrementally.
- **One engine.** The adapter consumes `SendMessageStreamAsync`; it does **not**
  re-orchestrate conversation/tool logic, and it does **not** create a new
  `wire-{timestamp}` conversation per request (continuation is owned by the existing
  `PublishedWire` resolver work — reuse it, do not fork it).
- **No MCP-specific logic in wire handlers** (E13/E14). MCP stays opaque; the adapter only
  maps `StreamingEvent`s.
- `stream: false` must fold the **same** stream — not a separate buffering executor.
- Removing the `stream: true` rejection must not regress non-streaming behavior or
  continuation parity already shipped in `published-wire-execution`.
- No new silent `catch {}`; stream errors map to the `error` wire event.

## Tasks

1. Add `WireStreamAdapter` (shared module under `PublishedWire/`) that takes the
   `IAsyncEnumerable<StreamingEvent>` from `SendMessageStreamAsync` and yields
   provider-shaped output for a target protocol.
2. Implement the **Chat Completions** mapping (design §6.3 table):
   `token` → `chat.completion.chunk` delta; `assistant_message` → final chunk/role;
   `usage` → trailing usage chunk; `error` → error object. Flush each as it arrives.
3. Remove the shipped `stream: true` rejection (`unsupported_feature`) for Chat Completions
   and route it through the adapter (design §6.5: the rejection is a bug).
4. Implement `stream: false` as a fold over the same adapter stream to final
   `chat.completion` JSON. Keep `WireConversationExecutor.CollectWireConversationResultAsync`
   only for this fold (design §6.4); do not use it on the streaming path.
5. Ensure continuation reuses the existing `PublishedWire` resolver (no new per-request
   conversation). Confirm guide-aware execution is intact (instructions, context,
   persistence, usage).
6. Add streaming tests proving incremental flush, ordered deltas, correct finish/stop on
   the terminal chunk, and `stream: false` == folded stream. Target
   `GuideAntsApi.IntegrationTests/Services/Conversations/PublishedConversationStreamingTests.cs`
   (or successor) + `PublishedWire` handler tests.

## Files in scope

- `src/server/GuideAntsApi/Endpoints/PublishedWire/*` (new `WireStreamAdapter`; Chat
  handler wiring; serializer)
- `src/server/GuideAntsApi/Endpoints/PublishedOpenAiWireEndpoints.cs` (remove Chat
  `stream: true` rejection; route through adapter)
- Tests: `src/server/GuideAntsApi.IntegrationTests/Services/Conversations/*`,
  `src/server/GuideAntsApi.Tests/.../PublishedWire*` handler tests.

Out of scope:

- Responses + Anthropic live mapping and deletion of duplicate buffer paths (Phase 4).
- Any MCP executor change (Phases 2, 5).

## Self-verification

```bash
cd src/server && dotnet build GuideAntsApi.sln && dotnet test GuideAntsApi.sln
cd src/client && npm run build && npm test -- --run
```

Run required gates:

- `wire-streaming-gate.md` §3.1, §3.2, §3.4 (Chat row).
- `codeql-gate.md` if the diff touches connection/secret/JSON handling.

## Definition of Done

- [ ] `WireStreamAdapter` consumes `SendMessageStreamAsync`; single engine, no re-orchestration.
- [ ] Chat Completions `stream: true` flushes live `chat.completion.chunk` deltas (no buffer).
- [ ] `stream: true` rejection removed for Chat; `stream: false` folds the same stream.
- [ ] No new per-request `wire-{timestamp}` conversation; continuation reuse intact.
- [ ] Build/tests green; wire-streaming gate Chat row passes; CodeQL clean (if applicable).

## Report-back contract (return exactly this)

```text
PHASE 3 REPORT
- WireStreamAdapter added (single engine over SendMessageStreamAsync): <paths>
- Chat Completions live deltas (no buffer-then-emit): <pass/fail>
- stream:true rejection removed for Chat: <yes/no>
- stream:false folds same stream (fold-only CollectWireConversationResultAsync): <pass/fail>
- New per-request conversation eliminated / continuation reuse: <pass/fail>
- WIRE STREAMING GATE (Chat): incremental-flush=<p/f> fold-parity=<p/f> single-engine=<p/f>
- CODEQL: new-vs-baseline=<count → ids/files or none / n-a>
- Verification: server-build=<p/f> server-tests=<counts> client-build=<p/f> client-tests=<counts>
- Files touched: <exhaustive list>
- Deviations / surprises: <list or "none">
```
