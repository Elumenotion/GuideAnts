# Wire Streaming Gate (MCP Tool Execution)

Companion to `00-orchestration.md`.

The design (§6) requires **one published runtime**: wire endpoints are protocol adapters
over `SendMessageStreamAsync`, and `stream: true` must be **live** — provider-shaped SSE
flushed as `StreamingEvent`s arrive. The shipped wire path buffers-then-emits
(`StringBuilder` + a single `content_block_delta`), which is **not** streaming (§6.2).
This gate proves the conversion is real and that MCP runs opaquely server-side between
model rounds while tokens stream (§6.5, E14).

---

## 1. Gate intent

Pass this gate when all are true:

- A single shared `WireStreamAdapter` (name TBD) consumes `SendMessageStreamAsync` for
  Chat Completions, Responses, and Anthropic Messages — no per-endpoint orchestration.
- `stream: true` flushes provider events **incrementally** as tokens arrive (no full-text
  buffer, no single-delta dump).
- `stream: false` folds the **same** stream to final JSON — a convenience fold, not a
  separate executor.
- MCP is **opaque** (E14): no `tool_calls`/`tool_use` on the wire; assistant text streams
  while server-side MCP executes between rounds.
- No MCP-specific logic is added to wire handlers.

---

## 2. Baseline checks (pre-flight)

Record the shipped defect so the fix is provable:

- `PublishedOpenAiWireEndpoints.cs` rejects `stream: true` on Chat Completions / Responses
  (`unsupported_feature`) and creates a new `wire-{timestamp}` conversation per request.
- `PublishedWire/*` continuation handlers accept `stream: true` but
  `CollectWireConversationResultAsync` → `Build…SsePayload` emit full text in one
  `content_block_delta` (buffer-then-emit).

Baseline result = **FAIL** on live streaming. Record in `STATUS.md`.

---

## 3. Gate checks — live `StreamingEvent` → wire mapping

For each provider, drive a turn that produces multiple token chunks and assert the wire
output arrives **incrementally** (more than one network flush, deltas ordered, final event
correct).

| `StreamingEvent` | OpenAI Chat (`stream: true`) | OpenAI Responses | Anthropic Messages |
|---|---|---|---|
| `token` | `chat.completion.chunk` delta | output text delta | `content_block_delta` `text_delta` |
| `assistant_message` | final chunk / role | output item complete | block complete |
| `usage` | trailing usage chunk | usage on response | `message_delta` usage |
| `error` | error object | error | error event |

### 3.1 Incremental-flush proof (not buffering)

- Capture the SSE byte stream timeline; assert ≥2 distinct `token`-derived flushes before
  the terminal event for a multi-chunk response.
- Grep the streaming path: no `StringBuilder` accumulation of the full assistant message
  before emit; no single `content_block_delta` carrying the entire text.

### 3.2 `stream: false` fold parity

- The non-streaming response equals the concatenation of the streamed tokens for the same
  input (same executor, folded). It is produced by folding the stream, not by a second
  buffering executor.
- `WireConversationExecutor.CollectWireConversationResultAsync` is used **only** for the
  `stream: false` fold (design §6.4).

### 3.3 MCP opacity (E14)

- During a turn that triggers a server-side MCP tool call, the wire stream shows assistant
  **text** tokens only — no `tool_calls`/`tool_use`/`tool` messages — and the turn never
  emits `pending_client_tool`.
- Tokens continue to stream across the MCP round boundary (MCP executes between model
  rounds inside `ThreadRun`).

### 3.4 Single-engine proof

- Each wire endpoint funnels through `SendMessageStreamAsync` exactly once per turn; no
  endpoint re-orchestrates conversation/tool logic.
- Duplicate buffer paths are deleted (Phase 4): inline
  `ExecuteConversationAsync` buffer + per-request convo, and the `PublishedGuidesEndpoints`
  invoke buffer copy.

---

## 4. When to run this gate

| Point | Required checks |
|---|---|
| Pre-flight baseline | §2 (record shipped FAIL) |
| After Phase 3 | 3.1, 3.2, 3.4 — **Chat row only** |
| After Phase 4 | 3.1–3.4 — Chat + Responses + Anthropic |
| Final acceptance (Phase 7) | 3.1–3.4 full pass + 3.3 with real MCP |

Target streaming integration coverage:
`src/server/GuideAntsApi.IntegrationTests/Services/Conversations/PublishedConversationStreamingTests.cs`
(or successor) and `PublishedWire` handler tests.

---

## 5. Report-back addition (Phases 3, 4, 7)

```text
WIRE STREAMING GATE:
- Shared WireStreamAdapter (single engine): <pass/fail>
- Live incremental flush (no buffer-then-emit): chat=<p/f> responses=<p/f> anthropic=<p/f>
- stream:false fold parity (same executor): <pass/fail>
- MCP opacity (no tool_calls on wire; no pending_client_tool): <pass/fail>
- Duplicate buffer paths removed: <pass/fail + files>
- Streaming test refs: <paths>
```
