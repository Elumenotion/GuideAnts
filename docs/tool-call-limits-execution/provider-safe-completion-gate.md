# Provider-Safe Completion Gate

Last updated: 2026-07-12

Run after Phases 2, 3, 6, and final Phase 7.

## Purpose

Prove limit escalation never produces upstream chat API 400s from invalid message sequences.
This gate encodes proposal §7–§9 and `DECISIONS.md` T4–T7.

## Pass criteria

### 1. Message pairing invariants

After any limit-hit scenario (Tier 1, 2, or 3):

- [ ] Every assistant message with `tool_calls` has a matching `role: tool` result for each
      `tool_call_id` (synthetic limit message or real result).
- [ ] No `role: tool` message exists without a prior assistant `tool_call_id` reference.
- [ ] No assistant message has `tool_calls: null` or `tool_calls: []` when the provider wire
      forbids it (OpenAI-compatible).

### 2. Request shape invariants

On every completion request **after** limit escalation:

- [ ] When history contains prior tool rounds, the `tools` array is still declared (T4, T5).
- [ ] Tier 2 sets `tool_choice: "none"` **only** when `SupportsToolChoiceNone` is true; `tools`
      remains non-empty when tools were available before limit.
- [ ] Tier 3 does **not** issue another tool-capable LLM request after force-complete is
      triggered.

### 3. Automated proof (required)

- [ ] Integration or unit tests mock an OpenAI-compatible validator that rejects:
  - orphan `tool` messages
  - `tool` results for unknown IDs
  - assistant `tool_calls` without results
- [ ] At least one test exercises **llama.cpp wire path** (`LlamaCppChatClient` serialization)
      with limit-hit history — no 400 from the mock server.
- [ ] At least one test: model returns `tool_calls` after Tier 1 soft block → Tier 2 or Tier 3
      fires → turn `completed`.

### 4. Tier 4 (Phase 6 — required)

- [ ] Compacted summarization request contains **no** `role: tool` messages and **no**
      assistant messages with `tool_calls`.
- [ ] `tools` param omitted on the compaction-only request.
- [ ] Force-complete path produces `completed` turn with model-written summary or Tier 3 stub
      fallback.

## Fail modes (automatic FAIL)

- `strip-tools-with-tool-history` — T4 violation
- `orphan-tool-call-id` — pairing broken
- `throw-on-limit` — turn `error` instead of `completed`
- `silent-drop` — tool calls removed without synthetic results
- `retry-after-limit` — tools executed after budget exhausted
