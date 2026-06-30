# Task — Phase 6: Streaming + error-shape tests, OpenAPI, docs, acceptance

> Subagent brief. Execute top to bottom; return the Report-back contract verbatim.

## Mission

Harden and document the feature: add **streaming (SSE)** continuation/tool coverage
for Chat + Responses, pin **provider error shapes** for the new `conversation` cases,
refresh OpenAPI/docs, and verify every **Acceptance criterion** in the report maps to
a passing test or a file/commit reference.

## Read first

- `../published-openai-wire-continuation-gap-report.md` → §"Acceptance criteria",
  §"Test plan", §"Non-goals".
- `./DECISIONS.md` → all (DW2–DW6) for cross-checks.
- `src/server/GuideAntsApi/Endpoints/PublishedOpenAiWireEndpoints.cs`: SSE payload
  builders (`BuildOpenAiResponsesSsePayload`, `BuildAnthropicMessageSsePayload`, Chat
  SSE), `Stream == true` branches; the error helpers under `OpenAiWireErrorResults`.
- `src/server/GuideAntsApi.IntegrationTests/Services/Conversations/PublishedConversationStreamingTests.cs`.

## Preconditions

- **Phases 1–5 gates green.**

## Guardrails (hard)

- **Tests/docs only** — do not change continuation or tool-bridge behavior here. If a
  test reveals a behavior bug, **stop and report it** for re-dispatch of the owning
  phase (orchestration §5: never fix a prior phase's bug in a later phase).
- Error shapes for new `conversation` cases must match the existing
  `previous_response_id` family in `status` + `type`/`code` style (no ad-hoc shapes).
- The trace-availability acceptance item is a cross-repo (GuideAntsChat) concern —
  cover the **backend** guarantee (trace data remains available after a published
  wire API error); note the frontend "does not spin" item as out-of-scope-for-backend
  if it cannot be asserted here.

## Tasks

1. Add streaming tests (Chat + Responses) in `PublishedConversationStreamingTests.cs`:
   continuation works under SSE; tool calls emit as deltas; final chunk carries the
   correct finish/stop reason for pending vs. final.
2. Add error-shape tests for `conversation`: missing/inaccessible/scope-mismatch/
   inconsistent-with-`previous_response_id` → expected `status` + `type`/`code`.
3. Add a backend test that trace data remains available after a provider wire API
   error.
4. Refresh OpenAPI/docs: ensure the Responses `conversation` parameter and the new
   continuation/error semantics are documented (Swagger annotations + any
   `docs/` provider-API notes). Update the report if any requirement changed during
   execution.
5. Walk the report's **Acceptance criteria** and map each to a passing test or a
   file/commit reference; record the mapping in the Report-back.

## Files in scope

- `src/server/GuideAntsApi.IntegrationTests/Services/Conversations/PublishedConversationStreamingTests.cs`
- `src/server/GuideAntsApi.Tests/Endpoints/PublishedOpenAiWireHandlersTests.cs`
  (error-shape + trace tests)
- OpenAPI/Swagger annotations on the Responses endpoint as needed
- `docs/` (provider-API notes; report touch-ups only)

**Out of scope:** any continuation or tool-bridge **behavior** change (owned by
Phases 1–5).

## Self-verification

```
cd src/server && dotnet build GuideAntsApi.sln
cd src/server && dotnet test GuideAntsApi.sln
```

- Streaming tests (Chat + Responses) pass; deltas + finish/stop reasons asserted.
- `conversation` error-shape tests pass and match the `previous_response_id` family.
- Trace-availability-after-error test passes.
- Every report Acceptance criterion has a cited test/file.

## Definition of Done

- [ ] Streaming continuation + tool-delta coverage for Chat + Responses.
- [ ] `conversation` error shapes pinned and consistent with existing helpers.
- [ ] Backend trace-availability-after-error covered.
- [ ] OpenAPI/docs refreshed; report reconciled with shipped behavior.
- [ ] Acceptance-criteria → test/file map complete.
- [ ] Build + full suite (unit + integration) green.

## Report-back contract (return exactly this)

```
PHASE 6 REPORT
- Streaming tests added (chat/responses): <names>
- conversation error-shape tests (match prev-resp family): <yes> <names>
- Trace-availability-after-error test: <name>
- OpenAPI/docs updated: <files>
- Acceptance-criteria map: <criterion -> test/file> (one line each)
- Behavior bug found needing prior-phase re-dispatch: <none | which phase + detail>
- Verification: build=<pass/fail> tests=<counts incl. integration>
- Files touched: <exhaustive list>
- Deviations / surprises: <list or "none">
```
