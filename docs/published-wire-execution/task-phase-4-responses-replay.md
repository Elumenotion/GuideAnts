# Task — Phase 4: Responses manual replay + state-resolution ordering

> Subagent brief. Execute top to bottom; return the Report-back contract verbatim.

## Mission

Complete the Responses state model by adding **manual replay** continuation (reusing
the Phase 1 shared resolver) and composing the full **resolution order**:
`conversation` → `previous_response_id` → both-consistent → manual replay → new
conversation. After this phase, a Responses client can continue a thread by explicit
state **or** by replaying `input` items, mapping to one internal conversation.

## Read first

- `../published-openai-wire-continuation-gap-report.md` → §"OpenAI Responses"
  (Required manual replay behavior) and §"Required implementation changes" #3
  (5-step resolution order).
- `./DECISIONS.md` → **DW3/DW4/DW5** (matching/window/identity), **DW6** (no silent
  fallback, guide-aware execution, one resolver).
- `src/server/GuideAntsApi/Endpoints/PublishedOpenAiWireEndpoints.cs`:
  - `PostResponsesAsync` non-tool branch (~L390–423)
  - `BuildOpenAiResponsesClientPrompt` (~L1404)
  - `ResolveResponsesConversationAsync` (Phase 3 form, now with `conversation`)
  - The Phase 1 shared resolver `ResolveConversationFromTranscriptAsync`.

## Preconditions

- **Phase 1 gate green** (shared resolver). **Phase 3 gate green** (`conversation`
  resolution via `conv_<NotebookConversationId>`).

## Guardrails (hard)

- **Reuse the Phase 1 resolver** for manual replay; no Responses-specific matching.
- **Exact resolution order** (report §3): (1) `conversation`, (2)
  `previous_response_id`, (3) if both supplied, ensure same internal conversation,
  (4) if neither, manual replay via the shared resolver, (5) new conversation only on
  no-match.
- **No silent fallback** (DW6): invalid explicit `previous_response_id`/`conversation`
  errors — it must **not** drop through to manual replay.
- **No duplicate seed** (report §4): resolved → `clientMessages: null`; no-match →
  seed the replayed prefix into the new conversation.
- **Guide-aware execution preserved** (DW6): keep funnelling through
  `ExecuteConversationAsync`.
- Tool-bridge parity / mixed resume is **Phase 5**; only keep the tool branches
  compiling here.

## Tasks

1. In `PostResponsesAsync` non-tool branch, replace the current single
   `ResolveResponsesConversationAsync(previous_response_id)` call with a composed
   resolution that returns either `(conversationId, errorResult)` following the
   5-step order above. Explicit-state errors short-circuit and return.
2. When neither `conversation` nor `previous_response_id` is supplied, resolve via
   `ResolveConversationFromTranscriptAsync(context, clientPrompt.PrefixMessages, …)`.
3. Pass `existingConversationId` + `clientMessages: existingConversationId.HasValue ?
   null : clientPrompt.PrefixMessages` into `ExecuteConversationAsync`.
4. Add tests in `GuideAntsApi.Tests/Endpoints/PublishedOpenAiWireHandlersTests.cs`.

## Files in scope

- `src/server/GuideAntsApi/Endpoints/PublishedOpenAiWireEndpoints.cs`
  (`PostResponsesAsync` non-tool branch + the composed resolution helper)
- `src/server/GuideAntsApi.Tests/Endpoints/PublishedOpenAiWireHandlersTests.cs`

**Out of scope:** Chat (Phase 2), `conversation` resolution (Phase 3), tool
bridge parity (Phase 5), streaming/docs (Phase 6).

## Self-verification

```
cd src/server && dotnet build GuideAntsApi.sln
cd src/server && dotnet test GuideAntsApi.sln
```

Tests must prove (report §Test plan, Responses — replay/ordering subset):
- Manual replay without `conversation`/`previous_response_id` continues the existing
  conversation.
- Resolved manual replay seeds **no** duplicate prefix.
- Manual replay tolerates persisted server-side tool messages and still matches.
- `previous_response_id` continuation remains deterministic; stale/non-latest →
  error.
- Invalid `previous_response_id` does **not** fall back to transcript matching.
- Full order honored when multiple state inputs are present.

## Definition of Done

- [ ] 5-step resolution order implemented exactly; explicit-state errors short-circuit.
- [ ] Manual replay uses the Phase 1 resolver; no second matcher.
- [ ] Resolved → `clientMessages:null`; no-match → prefix seed; guide-aware execution
      intact.
- [ ] No silent fallback on invalid explicit state.
- [ ] All listed Responses tests pass; build + full suite green.

## Report-back contract (return exactly this)

```
PHASE 4 REPORT
- Resolution order implemented (1..5): <yes>
- Manual replay uses shared resolver: <yes>
- Explicit-state errors short-circuit (no fallback): <confirmed>
- clientMessages null-on-match / seed-on-no-match: <yes>
- Tests added: <names>
- Verification: build=<pass/fail> tests=<counts> replay-continues=<pass?> no-dup-seed=<pass?> invalid-prevresp-errors=<pass?>
- Files touched: <exhaustive list>
- Deviations / surprises: <list or "none">
```
