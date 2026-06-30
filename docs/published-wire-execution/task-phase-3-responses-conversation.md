# Task — Phase 3: Responses `conversation` (encode the internal id)

> Subagent brief. Execute top to bottom; return the Report-back contract verbatim.

## Mission

Add support for the OpenAI Responses **`conversation`** state mechanism by mirroring
exactly how `previous_response_id` already works: encode the internal conversation id
as `conv_<NotebookConversationId:N>`, decode it on input, and resolve it by loading
the conversation and validating notebook + caller identity. **No mapping table, no EF
migration, no create endpoint** — the id *is* the encoded internal identifier.

## Read first

- `../published-openai-wire-continuation-gap-report.md` → §"OpenAI Responses"
  (Required `conversation` behavior).
- `./DECISIONS.md` → **DW2** (encode-the-id, authoritative), **DW5** (identity scope),
  **DW6** (no silent fallback, guide-aware execution).
- `src/server/GuideAntsApi/Endpoints/PublishedOpenAiWireEndpoints.cs`:
  - `OpenAiResponsesRequest` (~L4084) — add `conversation`.
  - `FormatResponsesId` (~L3260) + `TryParseResponsesMessageId` (~L3284) — the encode/
    decode pattern to mirror for `conv_*`.
  - `ResolveResponsesConversationAsync` (~L3161) — the existing scope/identity
    validation + error helpers (`InvalidPreviousResponseId`,
    `PreviousResponseNotFound`, `PreviousResponseScopeMismatch`) to mirror.
  - Where the Responses output id is produced (~L433, `conversation.ResponseId`) so the
    conversation's `conv_<id>` can be surfaced for round-tripping.

## Preconditions

- **Phase 1 gate green.** (DW2 is locked — no decision blocker.)

## Guardrails (hard)

- **Mirror `resp_*` exactly** (DW2). `conversation` = `conv_<NotebookConversationId:N>`;
  encode/decode helpers analogous to `FormatResponsesId`/`TryParseResponsesMessageId`.
- **Do NOT add** a mapping entity, a `DbSet`, an EF migration, or any
  `/v1/conversations` endpoint. If you think you need one, re-read DW2 — you do not.
- **Identity + notebook scoped** (DW5). Non-decodable / unknown / inaccessible /
  identity-mismatched `conv_*` → provider-shaped error (DW6) — **never** a silent
  fallback to transcript matching.
- **`conversation` + `previous_response_id` together** must refer to the same internal
  conversation, else a provider-shaped invalid-request error.
- **Surface the conversation id** in the Responses output so the client can round-trip
  it (mirror of returning `resp_*` as the response `id`).
- Do **not** wire the manual-replay fallback or re-order the full resolution flow here
  — that is Phase 4. Add `conversation` resolution and leave a clear seam for Phase 4
  to compose.

## Tasks

1. Add `conversation` to `OpenAiResponsesRequest` (`[JsonPropertyName("conversation")]`),
   accepting the OpenAI shape (string id, or object carrying `id` — match provider).
2. Add `FormatConversationId(Guid notebookConversationId) => $"conv_{id:N}"` and a
   `TryParseConversationId` mirroring the `resp_*` helpers.
3. Add `conversation` resolution: decode the id, load the `NotebookConversation`,
   validate it belongs to `context.NotebookId` and the caller identity (reuse the
   identity-scope logic from `ResolveResponsesConversationAsync`), and return the
   internal conversation id or a `conversation`-specific provider-shaped error helper
   (mirroring the `previous_response_id` family).
4. Implement the **both-supplied consistency** check: when `conversation` and
   `previous_response_id` are both present, the conversation decoded from each must be
   the same internal conversation, else error.
5. Surface the conversation's `conv_<id>` on the Responses output (and on resumed
   turns) so the client can pass it back.
6. Add tests in `GuideAntsApi.Tests/Endpoints/PublishedOpenAiWireHandlersTests.cs`.

## Files in scope

- `src/server/GuideAntsApi/Endpoints/PublishedOpenAiWireEndpoints.cs` (request model
  + `conv_*` encode/decode + `conversation` resolution/validation + error helpers +
  output surfacing; compose seam for Phase 4)
- `src/server/GuideAntsApi.Tests/Endpoints/PublishedOpenAiWireHandlersTests.cs`

**Out of scope:** any `DbSet`/entity/migration (explicitly forbidden by DW2),
manual-replay fallback + final ordering (Phase 4), Chat (Phase 2), tool bridge
(Phase 5), streaming/docs (Phase 6).

## Self-verification

```
cd src/server && dotnet build GuideAntsApi.sln
cd src/server && dotnet test GuideAntsApi.sln
```

Tests must prove (report §Test plan, Responses — conversation subset):
- `conversation` continues the encoded internal conversation.
- `conversation` returns an error when non-decodable/unknown/inaccessible/scoped to a
  different caller.
- `conversation` + `previous_response_id` succeed only when both decode to the same
  internal conversation; conflict → error.
- Invalid `conversation` does **not** fall back to transcript matching.
- The Responses output includes the conversation's `conv_<id>` for round-tripping.

## Definition of Done

- [ ] `conversation` modeled on `OpenAiResponsesRequest`.
- [ ] `conv_<NotebookConversationId>` encode/decode helpers mirror `resp_*`; **no**
      entity/`DbSet`/migration/create endpoint added (grep proves none).
- [ ] `conversation` resolution validates scope/identity, errors (no fallback) on bad
      ids, and enforces `conversation`/`previous_response_id` consistency.
- [ ] Conversation `conv_<id>` surfaced on output for round-tripping.
- [ ] Build + full suite green.

## Report-back contract (return exactly this)

```
PHASE 3 REPORT
- conversation field added to OpenAiResponsesRequest: <yes>
- conv_<id> encode/decode helpers (mirror resp_*): <names>
- Mapping entity/DbSet/migration/endpoint added: <none — confirm>
- Scope/identity validation + error helpers: <list>
- conversation+previous_response_id consistency enforced: <yes>
- conv_<id> surfaced on output: <yes>
- No silent fallback on invalid conversation: <confirmed>
- Tests added: <names>
- Verification: build=<pass/fail> tests=<counts>
- Files touched: <exhaustive list>
- Deviations / surprises: <list or "none">
```
