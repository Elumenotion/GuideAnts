# Task — Phase 5: Tool bridge parity + mixed server/client resume

> Subagent brief. Execute top to bottom; return the Report-back contract verbatim.

## Mission

Ensure **Chat Completions, Responses, and Anthropic Messages** all satisfy the same
internal tool-bridge contract, and that **mixed server/client tool execution**
resumes the correct pending internal turn without stale-pending-tool errors —
**proven through each provider's wire shape**, not just Anthropic. This phase closes
the gap where shared runner/service paths exist but lack provider-wire regression
coverage.

## Read first

- `../published-openai-wire-continuation-gap-report.md` → §"Tool bridge requirements"
  and §"Required implementation changes" #5; §Test plan "Shared tool-resume tests".
- `./DECISIONS.md` → **DW6** (guide-aware execution, one shared path, no fallback).
- `src/server/GuideAntsApi/Endpoints/PublishedOpenAiWireEndpoints.cs`:
  - Inbound tool-result branches: Chat (~L185–209), Responses (~L343–388), Anthropic
    (~L536). `ResolvePendingToolResultConversationAsync`,
    `AppendAnthropicToolResultsAsync`, `ResumeConversationAfterToolResultsAsync`.
  - Outbound emission: `BuildOpenAiChatToolCallsForResponse`,
    `BuildOpenAiResponsesOutputItems`, `BuildAnthropicContentBlocks`; pending-tool
    finish/stop-reason handling.
  - Tool-definition parsing into `ChatToolDefinition`.
- `src/server/AntRunner.Chat/AntRunner.Chat/ThreadRun.cs` (runner resume / skip
  already-satisfied tools; pending-client-tool turn status).
- `src/server/GuideAntsApi.IntegrationTests/Services/Conversations/PublishedConversationStreamingTests.cs`.

## Preconditions

- **Phase 2 gate green** (Chat continuation). **Phase 4 gate green** (Responses
  state complete).

## Guardrails (hard)

- **One shared tool-bridge contract.** Differences across providers are only in
  wire **shape** (parse/emit); the internal pending-tool semantics are identical and
  shared. No provider-specific divergence in resume logic.
- **Guide concerns stay active** (DW6): client-provided tools merge with guide/
  internal tools per existing policy; guide instructions, notebook context, tracing,
  persistence, usage accounting reach the runner on every provider request.
- **Historical replayed tool outputs are transcript context**, not active callbacks —
  only **trailing** callback items for the active pending turn drive resume.
- **Resume skips already-satisfied tools** (server-side and client-side); the pending
  turn stays `pending_client_tool` until the client callback arrives.
- **No new conversation on callback** (DW6): a tool-result callback resumes the
  pending internal turn; it must not spawn a new conversation.
- Prefer adding **tests + parity wiring**; do not refactor the runner beyond what
  parity requires. If a true behavior bug is found in the shared path, fix it in the
  owning file and note it.

## Tasks

1. Audit Chat and Responses tool parse/emit/append/resume against the Anthropic
   reference; close any wire-shape gaps so all three satisfy the contract in report
   §"Tool bridge requirements".
2. Verify (and wire if missing) that client tool definitions are converted to
   `ChatToolDefinition` and merged with guide/internal tools on every provider path.
3. Verify trailing-vs-historical tool-output discrimination is consistent across
   providers.
4. Add regression tests proving mixed server/client resume **through Chat and
   Responses wire shapes** (Anthropic likely already covered) in
   `PublishedOpenAiWireHandlersTests.cs`, plus streaming-path coverage in
   `PublishedConversationStreamingTests.cs` where the resume crosses SSE.

## Files in scope

- `src/server/GuideAntsApi/Endpoints/PublishedOpenAiWireEndpoints.cs` (tool
  parse/emit/append/resume parity only)
- `src/server/AntRunner.Chat/AntRunner.Chat/ThreadRun.cs` (only if a real shared-path
  bug must be fixed)
- `src/server/GuideAntsApi.Tests/Endpoints/PublishedOpenAiWireHandlersTests.cs`
- `src/server/GuideAntsApi.IntegrationTests/Services/Conversations/PublishedConversationStreamingTests.cs`

**Out of scope:** continuation resolution (Phases 1–4), `conversation` mapping
(Phase 3), OpenAPI/docs (Phase 6).

## Self-verification

```
cd src/server && dotnet build GuideAntsApi.sln
cd src/server && dotnet test GuideAntsApi.sln
```

Tests must prove (report §Test plan, shared tool-resume + provider tool tests):
- Server-executed tool call followed by client-executed tool call does **not** fail
  with a stale pending-tool error — for **Chat** and **Responses** wire shapes.
- Already-satisfied tool calls are skipped on resume after client tool output.
- Turn status stays `pending_client_tool` until the callback arrives.
- Client tool definitions are bridged while guide concerns still reach the runner
  (Chat + Responses).
- Historical non-trailing tool outputs do not trigger an active callback path.

## Definition of Done

- [ ] Chat, Responses, Anthropic share one tool-bridge contract; differences are
      wire-shape only.
- [ ] Mixed server/client resume proven through Chat **and** Responses wire shapes
      (not just Anthropic).
- [ ] Already-satisfied tools skipped; pending turn stays pending until callback;
      callback creates no new conversation.
- [ ] Guide concerns active on every provider tool path.
- [ ] Build + full suite (unit + integration) green.

## Report-back contract (return exactly this)

```
PHASE 5 REPORT
- Parity gaps found/closed (Chat/Responses vs Anthropic): <list or "none">
- Shared-path bug fixed in ThreadRun.cs: <none | file:line + what>
- Mixed-resume proven via wire shapes: chat=<pass?> responses=<pass?>
- Already-satisfied skip / pending-until-callback / no-new-convo: <yes/yes/yes>
- Guide concerns active on tool paths: <confirmed>
- Tests added: <names>
- Verification: build=<pass/fail> tests=<counts incl. integration>
- Files touched: <exhaustive list>
- Deviations / surprises: <list or "none">
```
