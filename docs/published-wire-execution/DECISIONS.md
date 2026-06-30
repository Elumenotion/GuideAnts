# Published Wire Continuation — Locked Decisions (single source of truth)

Last updated: 2026-06-25 · Status: **ALL LOCKED** (DW1–DW6).

Every subagent reads this file. If a value here is `UNDECIDED`, the orchestrator
**must** resolve it with the user (see `00-orchestration.md` §1) before dispatching
the phase that depends on it. Changing a value after a phase has shipped requires a
revert + re-dispatch of that phase — so get these right first.

The authoritative requirements live in
[`../published-openai-wire-continuation-gap-report.md`](../published-openai-wire-continuation-gap-report.md).
These decisions resolve the choices that report left open.

---

## DW1. Chat Completions behavior change rollout — **LOCKED: ship on, no flag**

Today the Chat endpoint creates a **new** internal conversation for every non-tool
request (`PostChatCompletionsAsync` calls `ExecuteConversationAsync` with no
`existingConversationId`). Phase 2 makes it continue the matched conversation.

- [x] **Ship the corrected behavior directly** — it is a correctness fix toward
      provider-compatible, caller-managed `messages` semantics, and it is already
      the behavior the Anthropic endpoint ships. No feature flag.
- [ ] ~~Gate behind a config flag / staged rollout~~ — rejected: a flag would leave
      two divergent continuation code paths and invite drift; the resolver only
      *continues* on a confident, identity-scoped match, so the blast radius is
      bounded.

> If the user later wants a kill-switch, add it as a single config check at the
> resolver entry — never a second resolution path.

---

## DW2. External `conversation` id — **LOCKED: encode the internal conversation id, exactly like `previous_response_id`**

This was never an open question. Responses **already** maps external state to internal
state without any mapping table: `previous_response_id` is `resp_<assistantMessageId:N>`
(`FormatResponsesId`, ~L3260), decoded by `TryParseResponsesMessageId` (~L3284), and
scope-checked against the DB in `ResolveResponsesConversationAsync` (~L3161). **The id
*is* the encoded internal identifier.** `conversation` mirrors this exactly.

- [x] **`conversation` id = `conv_<NotebookConversationId:N>`.** Encode the internal
      conversation id; decode it on the way in; resolve by loading the conversation and
      validating notebook + caller identity (DW5) — the same shape as the
      `previous_response_id` path.
- [x] **No mapping table, no EF migration, no `/v1/conversations` endpoint, no
      lazy-create ambiguity.** There is nothing to "map" — the value round-trips the
      internal id, just as `resp_*` round-trips the assistant message id.
- [x] **Issuance:** the Responses output carries the conversation's `conv_<id>` so the
      client can pass it back on the next turn (mirror of how `resp_*` is returned as
      the response `id`). The first turn creates the internal conversation as today and
      surfaces its `conv_<id>`.
- [x] **Errors, never fallback (DW6):** a non-decodable / unknown / inaccessible /
      identity-mismatched `conv_*` is a provider-shaped error from the same family as
      the `previous_response_id` errors — it must **not** silently downgrade to
      transcript matching.
- [ ] ~~Durable external→internal mapping table~~ — rejected: redundant with the
      existing encode-the-id approach already shipped for `resp_*`.

---

## DW3. Transcript matching rule — **LOCKED: normalize + ordinal-equal, no fuzzy**

- [x] Normalize each provider message/item into the common transcript model (role,
      text content, assistant tool-call ids, tool-result ids). Normalize text
      (trim/whitespace-collapse) and map provider tool ids to internal ids before
      comparison; compare normalized text with **ordinal equality**.
- [x] Match the **full** client-visible replayed prefix, not a single message.
      Persisted internal-only messages the client could not have replayed are
      allowed (skipped) in the comparison.
- [x] The replayed latest assistant candidate **must** correspond to the latest
      persisted assistant message in the internal conversation.
- [x] Include provider roles that affect behavior (`system`, `developer`, `user`,
      `assistant`, `tool`) where present.
- [ ] ~~Fuzzy / similarity / longest-common-subsequence matching~~ — rejected.
- [x] **Return no match instead of guessing** when the replayed prefix is empty,
      ambiguous (more than one candidate matches equally after scope + window), or
      lacks enough evidence. Ambiguity → new conversation, never a coin-flip.

---

## DW4. Candidate activity window — **LOCKED: 60 minutes**

- [x] Transcript candidates are restricted to conversations whose most recent
      persisted message was created within the **previous 60 minutes**.
- [x] Conversations idle longer than the window are **not eligible** for replay
      continuation and fall through to new-conversation creation.
- [x] Candidates are ordered most-recent-activity first (turn index, then created,
      descending) and evaluation **short-circuits** to the next candidate on the
      first message mismatch.

> Performance consequence: index the "most recent message created" lookup alongside
> notebook id + caller identity so the candidate query stays cheap. Explicit-id and
> `conversation`-mapping resolution are **not** subject to the window — the window
> only bounds transcript matching.

---

## DW5. Identity scoping — **LOCKED: always scope, never cross-attach**

- [x] Every published caller carries an identity: an authenticated internal user id
      (`context.InternalUserId`), **or** an API-key-derived external caller identity
      (`context.ExternalUserIdentity`) for otherwise anonymous callers.
- [x] All continuation resolution (transcript, `previous_response_id`,
      `conversation`, tool-callback) is scoped to that identity and to the published
      notebook/guide.
- [x] A request **must never** attach to a conversation owned by a different
      identity, even when the replayed transcript text is identical. On identity
      mismatch: transcript path → no match (new conversation); explicit-id path →
      provider-shaped scope error (as the existing `PreviousResponseScopeMismatch`).

---

## DW6. Frozen invariants (NOT open for subagent reinterpretation)

- **No `chatcmpl_*` as a conversation key.** Chat continuation is transcript replay,
  not response-id chaining.
- **No synthetic thread keys** built from repeated assistant content.
- **No silent recovery from invalid explicit state.** An invalid/inaccessible/stale
  `previous_response_id` or `conversation` is a provider-shaped error; it must
  **not** quietly downgrade to transcript matching.
- **No bypass of guide-aware execution.** Continuation resolution only *chooses* the
  internal conversation; it never replaces guide instruction injection, context
  building, internal tool routing, tracing, persistence, or usage reporting. Every
  path funnels through `ExecuteConversationAsync` /
  `ResumeConversationAfterToolResultsAsync`.
- **One resolver.** Anthropic's existing transcript/message-id resolution is the
  reference; Phase 1 generalizes it and all providers reuse it. No second resolver.
- **No "fallback" logic** (user rule). Missing evidence → explicit no-match or
  explicit error; never a permissive default.
