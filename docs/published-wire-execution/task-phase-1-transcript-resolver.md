# Task — Phase 1: Provider-neutral transcript resolver

> Subagent brief. Execute top to bottom; return the Report-back contract verbatim.

## Mission

Generalize the existing **Anthropic-named** transcript/message-id resolution in
`PublishedOpenAiWireEndpoints.cs` into a **single provider-neutral resolver** that
all published wire endpoints can reuse, and implement the bounded **candidate
selection** (notebook + identity scope, 60-minute activity window, recency ordering,
short-circuit on first mismatch). Switch the Anthropic handler onto the shared
resolver with **no behavior change**. Do **not** touch the Chat or Responses
handlers in this phase.

## Read first

- `../published-openai-wire-continuation-gap-report.md` → §"Transcript matching
  requirements", §"Candidate selection and performance", §"Required implementation
  changes" #1.
- `./DECISIONS.md` → **DW3** (matching rule), **DW4** (60-min window), **DW5**
  (identity scoping), **DW6** (no fallback, one resolver).
- `src/server/GuideAntsApi/Endpoints/PublishedOpenAiWireEndpoints.cs`:
  - `ResolveAnthropicConversationFromTranscriptAsync` (~L2802)
  - `ResolveAnthropicConversationFromMessageIdsAsync` (~L2737)
  - `BuildAnthropicTranscriptHistory` (~L2894)
  - `ResolveLatestAssistantMessageIdAsync` (~L3243)
  - `TurnMatchesCallerScopeAsync` (caller-scope helper)
  - `ClientPromptParts` / `SplitClientPrompt` (~L1418) and the `ChatMessage` shape.
- `src/server/GuideAntsApi.DataModel/Models/NotebookConversation.cs`,
  `NotebookConversationMessage.cs` (timestamps available for the activity window).

## Preconditions

- Pre-flight baseline captured (`STATUS.md`). DW3/DW4/DW5/DW6 locked.

## Guardrails (hard)

- **One resolver** (DW6). Rename/extract the Anthropic helpers into provider-neutral
  ones; do **not** create a second parallel resolver. The Anthropic handler must end
  this phase calling the shared method.
- **No behavior change for Anthropic.** This is a refactor + additive candidate
  bounding. The existing Anthropic tests must pass unchanged.
- **Identity always scoped (DW5).** Candidate queries filter by published
  notebook/guide **and** caller identity (internal user id, or API-key-derived
  external identity). Never return a conversation owned by another identity.
- **60-minute window (DW4)** applies to **transcript** candidate selection only, not
  to explicit-id resolution. Idle-longer conversations are excluded → caller falls
  through to new-conversation creation.
- **Short-circuit (DW4):** compare the replayed prefix against each candidate message
  by message; abandon a candidate on the first mismatch and move on. Do not load full
  histories for all candidates up front beyond what the ordered/windowed query needs.
- **No fuzzy matching, no guessing (DW3/DW6).** Ordinal text equality after
  normalization; ambiguous/empty/insufficient → return no match (`null`).
- **Do not edit** `PostChatCompletionsAsync`, `PostResponsesAsync`, or the Responses
  request model in this phase (those are Phases 2–4).

## Tasks

1. Introduce provider-neutral names for the transcript surface, e.g.
   `WireTranscriptMessage`, `BuildWireTranscriptHistory(IReadOnlyList<ChatMessage>)`,
   and `ResolveConversationFromTranscriptAsync(context, prefixMessages, db, ct)`,
   reusing the current Anthropic logic. Keep the message-id resolver too, generalized
   (e.g. `ResolveConversationFromLatestAssistantIdAsync`) since Anthropic still uses
   it. Update internal callers.
2. Implement candidate selection inside the transcript resolver per DW3/DW4/DW5:
   - Filter candidates to `context.NotebookId` + resolved caller identity.
   - Restrict to conversations with most-recent-message `created` within the last
     **60 minutes**.
   - Order most-recent-activity first (turn index, then created, descending).
   - For each candidate, verify the full normalized replayed prefix; short-circuit on
     first mismatch.
   - Require the latest replayed assistant to equal the latest persisted assistant.
   - Return no match on empty/ambiguous/insufficient input.
3. Centralize the activity-window threshold as a single named constant
   (e.g. `TranscriptContinuationWindow = TimeSpan.FromMinutes(60)`).
4. Point `ResolveAnthropicConversationFromTranscriptAsync`'s call site (the Anthropic
   handler, ~L569) at the shared method. If you keep a thin Anthropic-named wrapper
   for clarity, it must delegate — no logic fork.
5. Add unit tests (see Self-verification) in
   `GuideAntsApi.Tests/Endpoints/PublishedOpenAiWireHandlersTests.cs`.

## Files in scope

- `src/server/GuideAntsApi/Endpoints/PublishedOpenAiWireEndpoints.cs` (resolver
  rename/extract + candidate selection + Anthropic call-site swap only)
- `src/server/GuideAntsApi.Tests/Endpoints/PublishedOpenAiWireHandlersTests.cs`
  (new resolver unit tests)

**Out of scope:** Chat handler (Phase 2), Responses handler + request model
(Phases 3–4), tool bridge (Phase 5), streaming/docs (Phase 6).

## Self-verification

```
cd src/server && dotnet build GuideAntsApi.sln
cd src/server && dotnet test GuideAntsApi.sln
```

New tests must prove:
- A candidate active within 60 min is matched; one whose last activity is older than
  60 min is **not** matched (→ no match).
- Identical replayed transcript text owned by a different caller identity → no match.
- When an earlier-ordered candidate diverges mid-prefix, the resolver short-circuits
  and resolves the correct later candidate.
- Empty/ambiguous prefix → no match.
- All pre-existing Anthropic resolution tests still pass (no behavior delta).

## Definition of Done

- [ ] Provider-neutral resolver (transcript + latest-assistant-id) exists; Anthropic
      handler delegates to it; no duplicate resolver (grep proves one).
- [ ] Candidate selection implements scope + 60-min window + recency order +
      short-circuit + latest-assistant equality, per DW3/DW4/DW5.
- [ ] Window threshold is a single named constant.
- [ ] New unit tests for window, identity scoping, short-circuit, ambiguity pass.
- [ ] Build + full server test suite green; Anthropic behavior unchanged.

## Report-back contract (return exactly this)

```
PHASE 1 REPORT
- Shared resolver names: <type/method list>
- Anthropic handler now delegates: <yes/no> duplicate-resolver-removed: <yes/no>
- Candidate selection: scope=<notebook+identity?> window=<60min const name> order=<recency?> short-circuit=<yes>
- Latest-assistant-equality enforced: <yes>
- New tests added: <names>
- Anthropic behavior unchanged (existing tests pass): <yes/no>
- Verification: build=<pass/fail> tests=<counts>
- Files touched: <exhaustive list>
- Deviations / surprises: <list or "none">
```
