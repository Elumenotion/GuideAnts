# Task — Phase 3: Tier 2–3 escalation + provider capability

> Subagent brief. Execute top to bottom. Return the Report-back contract verbatim.

## Mission

Complete the provider-safe escalation ladder for stubborn models (e.g. Search retry
instructions): Tier 2 `tool_choice: "none"` when supported, Tier 3 force-complete with
server-authored assistant message, and Tier 5 runtime instruction override. Add
`ChatCompletionRequest.ToolChoice` and per-provider capability flags.

## Read first

- `../tool-call-limits-proposal.md` §7 (upstream constraints), §8 (full ladder + §8.5 matrix),
  §11 (`ToolChoice` field).
- `./DECISIONS.md` — T4, T6, T7.
- `./provider-safe-completion-gate.md`.
- `src/server/AntRunner.Chat/AntRunner.Chat.Abstractions/ChatCompletionRequest.cs`
- `src/server/AntRunner.Chat/AntRunner.Chat/OpenAiChatClient.cs`
- `src/server/GuideAntsApi/Services/LlamaCpp/*ChatClient*.cs`
- Anthropic / Gemini clients if present in `AntRunner.Chat`

## Preconditions

- Phase 2 gate green (Tier 1 + `ToolLimitState` + `SoftBlocked` phase).

## Guardrails (hard)

- **Never strip `tools` from request** while tool-shaped history exists (T4).
- **Tier 2:** `tool_choice: "none"` with **tools still declared** (T6).
- **Skip Tier 2** when `SupportsToolChoiceNone` is false → go to Tier 3 directly on retry after
  soft block (not a silent re-run of Tier 1).
- **Tier 3:** persist pairing for any pending batch, append server assistant message, set
  `continueChat = false`, status `completed` (T7). No further tool-capable LLM call.
- **Runtime instruction override** when `Phase >= SoftBlocked`: prepend system context per
  proposal §8 Tier 5.
- **No Tier 4 compaction** in this phase — Phase 6 owns Tier 4 (T12).

## Tasks

1. **Extend `ChatCompletionRequest`** with nullable `ToolChoice` (`null` | `"none"`).
2. **Map `ToolChoice`** in OpenAI-compatible, LlamaCpp, Anthropic (and Gemini if applicable)
   request serializers.
3. **Add `SupportsToolChoiceNone`** to `IChatCompletionClient` or runtime profile — set per
   provider per proposal §8.5.
4. **Escalation state machine** in `ThreadRun` `tool_calls` limit hook:
   - `Phase == None` + limit → Tier 1 (already Phase 2).
   - `Phase == SoftBlocked` + model requests tools again → Tier 2 if supported, else Tier 3.
   - `Phase == ToolChoiceNone` + model requests tools again → Tier 3.
5. **Tier 3 force-complete** helper: server-authored assistant message text per proposal §8.
6. **Tests:**
   - Model retries after soft block → Tier 2 on OpenAI mock → text response OR Tier 3.
   - Model retries after Tier 2 → Tier 3, turn `completed`.
   - Provider without `tool_choice` support skips to Tier 3 without 400.
   - Mock validator rejects invalid histories — all scenarios pass.

## Files in scope

- `src/server/AntRunner.Chat/AntRunner.Chat.Abstractions/ChatCompletionRequest.cs`
- `src/server/AntRunner.Chat/AntRunner.Chat/ThreadRun.cs` (escalation only)
- Chat client implementations (`OpenAiChatClient`, `LlamaCppChatClient`, Anthropic, etc.)
- `src/server/GuideAntsApi/Services/Conversations/Mapping/ConversationHistoryBuilder.cs`
  (runtime instruction override injection point if system messages assembled here)
- Tests under `src/server/**Tests**`

## Files out of scope

- Tier 4 history compaction (Phase 6).
- UI, export/import.

## Self-verification

```bash
cd src/server && dotnet build GuideAntsApi.sln && dotnet test GuideAntsApi.sln
```

Full **provider-safe-completion gate** must pass.

## Definition of Done

- [ ] Escalation ladder Tier 1 → 2 → 3 implemented.
- [ ] `ToolChoice` wired for primary providers.
- [ ] Runtime instruction override active after soft block.
- [ ] No upstream 400 in limit-retry tests.

## Report-back contract

1. Provider capability matrix as implemented (`SupportsToolChoiceNone` per client).
2. State machine diagram or table (Phase transitions).
3. Tier 3 message persistence path.
4. Test names for retry-after-soft-block scenarios.
5. Files touched.
6. provider-safe-completion gate: PASS/FAIL with details.
