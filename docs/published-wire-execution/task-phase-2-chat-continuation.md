# Task — Phase 2: Chat Completions transcript continuation

> Subagent brief. Execute top to bottom; return the Report-back contract verbatim.

## Mission

Make the published **Chat Completions** endpoint behave like a normal caller-managed
Chat API: resolve the replayed `messages` prefix to the existing internal
conversation via the **shared resolver from Phase 1** before executing, so a
multi-turn client thread maps to **one** internal GuideAnts conversation instead of
spawning a new one per turn.

## Read first

- `../published-openai-wire-continuation-gap-report.md` → §"OpenAI Chat Completions"
  (Required external behavior) and §"Required implementation changes" #2 (control
  flow example).
- `./DECISIONS.md` → **DW1** (ship on, no flag), **DW3/DW4/DW5** (matching/window/
  identity), **DW6** (no `chatcmpl_*` thread key, no fallback, guide-aware execution).
- `src/server/GuideAntsApi/Endpoints/PublishedOpenAiWireEndpoints.cs`:
  - `PostChatCompletionsAsync` non-tool branch (~L211–232)
  - `BuildOpenAiChatClientPrompt` (~L1397), `ClientPromptParts`
  - `ExecuteConversationAsync` (~L1100+) — existing `existingConversationId` /
    `clientMessages` parameters (the Anthropic path at ~L576 is the reference).
  - The Phase 1 shared resolver (`ResolveConversationFromTranscriptAsync`).

## Preconditions

- **Phase 1 gate green** (shared resolver + candidate selection exist).

## Guardrails (hard)

- **Reuse the Phase 1 resolver.** Do not write Chat-specific matching logic.
- **No `chatcmpl_*` as a conversation key** (DW6). Continuation is transcript replay.
- **Guide-aware execution preserved** (DW6): keep funnelling through
  `ExecuteConversationAsync`; resolution only chooses the conversation id.
- **No duplicate seed** (report §4): when resolved, pass `clientMessages: null`; when
  not resolved, pass the replayed prefix as seed for the new conversation.
- **No fallback** (DW6): a confident no-match → new conversation (correct), but never
  mask an error as "start fresh"; do not swallow resolver/db exceptions.
- Do **not** change the existing inbound-tool-result branch behavior except as needed
  to keep it compiling; full tool-bridge parity is Phase 5.

## Tasks

1. In `PostChatCompletionsAsync` non-tool branch, after building `clientPrompt`,
   resolve the prefix:
   ```csharp
   var existingConversationId = await ResolveConversationFromTranscriptAsync(
       context, clientPrompt.PrefixMessages, db, httpContext.RequestAborted);

   conversation = await ExecuteConversationAsync(
       publishedConversationService, db, context, instructions,
       httpContext.RequestAborted,
       existingConversationId: existingConversationId,
       clientMessages: existingConversationId.HasValue ? null : clientPrompt.PrefixMessages,
       clientToolDefinitions: clientToolDefinitions);
   ```
2. Confirm the empty-instruction guard and error shapes are unchanged.
3. Add tests in `GuideAntsApi.Tests/Endpoints/PublishedOpenAiWireHandlersTests.cs`.

## Files in scope

- `src/server/GuideAntsApi/Endpoints/PublishedOpenAiWireEndpoints.cs`
  (`PostChatCompletionsAsync` non-tool branch only)
- `src/server/GuideAntsApi.Tests/Endpoints/PublishedOpenAiWireHandlersTests.cs`

**Out of scope:** Responses (Phases 3–4), tool-bridge parity / mixed resume
(Phase 5), streaming/docs (Phase 6), the resolver itself (Phase 1).

## Self-verification

```
cd src/server && dotnet build GuideAntsApi.sln
cd src/server && dotnet test GuideAntsApi.sln
```

Tests must prove (report §Test plan, Chat Completions):
- Second request replaying `user→assistant→user` continues the first conversation.
- Repeated assistant text across conversations does not mis-attach when full
  transcripts differ.
- Replay omitting server-internal tool messages still matches.
- Resolved continuation seeds **no** duplicate prefix into the next model call.
- Historical non-trailing tool messages do not trigger a pending-tool resume.
- A pending client tool callback resumes the same conversation (no new one).

## Definition of Done

- [ ] Chat non-tool path resolves via the shared resolver and passes
      `existingConversationId` + `clientMessages:null` on match; seeds prefix only on
      no-match.
- [ ] No `chatcmpl_*` continuation; guide-aware execution intact.
- [ ] All listed Chat tests pass; build + full suite green.

## Report-back contract (return exactly this)

```
PHASE 2 REPORT
- Resolver call added before ExecuteConversationAsync: <yes>
- clientMessages null-on-match / seed-on-no-match: <yes>
- No chatcmpl_* thread key introduced: <confirmed>
- Tests added: <names>
- Verification: build=<pass/fail> tests=<counts> continuation=<pass?> no-misattach=<pass?> no-dup-seed=<pass?> tool-callback-same-convo=<pass?>
- Files touched: <exhaustive list>
- Deviations / surprises: <list or "none">
```
