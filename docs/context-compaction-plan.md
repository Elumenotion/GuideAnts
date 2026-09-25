# Context Compaction — Task Plan

Status: **Design approved. Pending implementation plan.**
Design authority: [`docs/superpowers/specs/2026-09-21-context-compaction-design.md`](superpowers/specs/2026-09-21-context-compaction-design.md)
Branch: `feature/compaction`

This is the task list. Where a task and the spec disagree, the spec wins.

---

## What we're building

An explicit, user-triggered way to compact a notebook conversation, a meter that tells the user
when it's worth doing, and a recall tool so compaction never loses anything permanently.
Overflow stops destroying message content.

**Justification:** user control and product capability. *Not* cost — conversations are mostly
short with a rare long tail, so compaction won't move aggregate spend. The cost lever is prompt
caching, deliberately deferred.

## Decisions already made

| # | Decision |
|---|---|
| D1 | **Manual trigger only.** No thresholds, no proactive compaction, no auto-compaction on overflow. |
| D2 | **Algorithmic extraction only.** No LLM summarization tier, no per-compaction cost. |
| D3 | **Persist the decision, recompute the content.** One boundary turn index; summary regenerated deterministically. |
| D4 | **Shape at history-build time**, not in `ThreadRun`. |
| D5 | **Overflow fails cleanly.** The destructive unwind is removed. |
| D6 | **Fixed, protocol-derived section vocabulary.** Six sections, same for every guide. |
| D7 | **Recall exposed only when a boundary exists.** Zero behavior change for uncompacted conversations. |
| D8 | **Compaction requires an idle conversation.** Enforced via the existing distributed conversation lock. |
| D9 | **Meter lives in the composer footer**, beside the Send button in `DraftUserCell.tsx`. |

## What the design removed from the original plan

Phase 4 (boundary persistence subsystem) collapsed to one nullable column. Phase 5 (cross-turn
wiring across three published call sites) collapsed to one private-path call site. Phase 8 (LLM
tier) deleted. All threshold and trigger logic deleted. Phases 9–10 (per-guide config, usage
attribution) deferred — with no LLM call and no auto-trigger, there is nothing new to attribute.

---

## W1 — Context-window metadata

Purpose changed: this drives the **meter**, not a trigger.

**Implementation plan written:** [`docs/superpowers/plans/2026-09-21-w1-context-window-metadata.md`](superpowers/plans/2026-09-21-w1-context-window-metadata.md) — 11 tasks. Note it adds a
back-fill task not listed below (1.4 needed a server-side value table, since catalog rows are
created only by admins and there is no existing model seeder).

- [ ] 1.1 Add `ContextWindowTokens` (`int?`) and `MaxOutputTokens` (`int?`) to `Model`
- [ ] 1.2 EF migration `AddModelContextWindow` (run from `src/server`)
- [ ] 1.3 Seed the 19 entries in `src/client/src/pages/settings/data/knownCloudModels.json`
- [ ] 1.4 Back-fill existing catalog rows — seeder or migration data step
- [ ] 1.5 Extend `ResolvedExecutionPolicy` with both fields
- [ ] 1.6 DTO plumbing: `ModelDto` in `Models/Guides/CatalogDto.cs`, catalog endpoints, client types
- [ ] 1.7 Settings UI: `ModelsTab.tsx`, `AddModelWizard.tsx`, `NonLocalModelParameterSurfaceEditor.tsx`
- [ ] 1.8 Resolution precedence: live llama.cpp `n_ctx` → catalog → learned-from-exception → unknown.
      Note `n_ctx_train` is the *training* context, not the loaded window
- [ ] 1.9 Cache `ContextSize` read off `ChatContextOverflowException` as the learned value.
      Metadata only — it never triggers compaction
- [ ] 1.10 Tests: precedence order, unknown state, migration round-trip

---

## W2 — Estimation and context status

**Implementation plan written:** [`docs/superpowers/plans/2026-09-21-w2-estimation-and-context-status.md`](superpowers/plans/2026-09-21-w2-estimation-and-context-status.md) — 6 tasks. It also
wires two W1 gaps (nothing yet calls `IContextWindowResolver.Resolve` or
`ILearnedContextWindowCache.Record`).

**Implemented (uncommitted on `feature/compaction`); manual verification outstanding.** Notes:

- W1 left two gaps that W2 closed: nothing called `IContextWindowResolver.Resolve` or
  `ILearnedContextWindowCache.Record`. Learned windows are recorded from the stream engine's
  failure path; until W6 removes the unwind, overflows the unwind absorbs are not learned.
- `boundaryTurnIndex` is returned `null` until W4 adds the column.
- The status DTO carries three fields beyond the spec: `estimateSource` (renders "~" for
  character estimates), `modelDeploymentId` and `contextWindowSource` (so W8 can show "unknown"
  when the model selected for the next send differs from the model the window describes).
- Calibration is per conversation and only refines the tail added after the last provider-reported
  round; the character fallback always uses 4.0 (calibration data exists only when usage does).
- Known undercounts: a guide/assistant switch is invisible until the next completed turn (the last
  provider count bakes in the old instruction prefix and tools); `ThinkingBlocks` and images are
  not counted in character estimates (revisit in W6).
- **Still to do (plan Task 6 step 3):** read `contextStatus` from one real conversation against a
  running API and compare `estimatedPromptTokens` with the provider's own number.

- [x] 2.1 chars/4 token estimator with per-model calibration from observed usage
- [x] 2.2 Track `lastRoundPromptTokens` separately. `MergeRoundUsage` (`ThreadRun.cs:1470`)
      **sums** across rounds, so the accumulated value is cumulative spend, not context size.
      Leave the accumulation alone — usage reporting depends on it
- [x] 2.3 Extend the conversation read with `{ contextWindowTokens, estimatedPromptTokens, boundaryTurnIndex }`
- [x] 2.4 Tests: calibration, nullable window handling

---

## W3 — Compaction engine

Pure static library in `AntRunner.Chat`, alongside `ToolOutputTruncator`. No I/O, no DB, no
vendor knowledge. **Must not reference `GuideAntsApi` or `GuideAntsApi.DataModel`.**

**Implementation plan written:** [`docs/superpowers/plans/2026-09-22-w3-compaction-engine.md`](superpowers/plans/2026-09-22-w3-compaction-engine.md) — 10 tasks. Only `CompactionEngine`, `CompactionResult`, and `TurnFacts` are public; every pipeline stage is `internal`, reached in tests via the existing `InternalsVisibleTo`. The full cross-domain benchmark against real non-coding transcripts (spec's Testing item 5) is W9's job, not this workstream's — W3's tests only prove the pipeline is correct and deterministic on synthetic input.

- [x] 3.1 `CompactionEngine.Compact(preBoundaryMessages, turnFacts) -> CompactionResult`
- [x] 3.2 `TurnFacts` projection — turn index, files created/modified. Sourced from persisted
      `ConversationTurn.FilesCreated` / `FilesModified`, *not* `ThreadRun`'s in-memory sets,
      which a history-build-time caller cannot see
- [x] 3.3 Normalize `ChatMessage[]` → uniform blocks
- [x] 3.4 Filter noise: empty blocks, superseded system nudges, tool-limit scaffolding
- [x] 3.5 Extractor — Goal (first user message + scope-change markers)
- [x] 3.6 Extractor — Artifacts (from `TurnFacts`)
- [x] 3.7 Extractor — Activity ledger (one line per tool call: name, key args, ok/error)
- [x] 3.8 Extractor — Unresolved errors (errored tool results with no later success for the same tool)
- [x] 3.9 Extractor — Directives (regex over user messages)
- [x] 3.10 Renderer, including the handoff framing line so the model doesn't continue the summary
- [x] 3.11 Tests: determinism (byte-identical output), each extractor, degenerate inputs

**No cut-selection stage.** The user's press supplies the cut, so there is no token-budget
backwalk. Boundary selection lives in W4.

---

## W4 — Boundary and the compact endpoint

**Implementation plan written:** [`docs/superpowers/plans/2026-09-22-w4-boundary-and-compact-endpoint.md`](superpowers/plans/2026-09-22-w4-boundary-and-compact-endpoint.md) — 8 tasks, plus Task 5 below (wiring `BoundaryTurnIndex` into `ConversationContextStatusService`) added to close a gap W2's plan explicitly flagged as "until W4 adds the column."

- [x] 4.1 Add `CompactionBoundaryTurnIndex` (`int?`) to `NotebookConversation`
- [x] 4.2 EF migration
- [x] 4.3 Boundary selection: last **complete** turn at press time. Must not split an assistant
      `tool_calls` message from its `tool_result` messages, and must not split a turn paused
      awaiting a client-handled tool (cf. commit `91398ae3`)
- [x] 4.4 `POST /api/notebooks/{notebookId}/conversations/{conversationId}/compact`, returning
      `{ boundaryTurnIndex, messagesSummarized, estimatedTokensBefore, estimatedTokensAfter }`
- [x] 4.5 Idempotent when no new complete turn exists since the last boundary
- [x] 4.6 Monotonic — the boundary never moves backward
- [x] 4.7 **Idle-only (D8).** Acquire the same distributed conversation lock
      `ConversationStreamEngine` uses, write the boundary, release. Return `409 Conflict` naming
      the holder when held, matching the existing `LockAcquisitionResult.AlreadyLocked` shape.
      Use the lock rather than a read-only `ConversationStreamRunRegistry.IsAnyActiveForConversation`
      check — the lock is authoritative across API instances and closes the race in both directions
- [x] 4.8 Tests: invariants, idempotency, monotonicity, no-complete-turn no-op, and 409 while a
      turn is streaming

---

## W5 — History-builder wiring

**Implementation plan written:** [`docs/superpowers/plans/2026-09-22-w5-history-builder-wiring.md`](superpowers/plans/2026-09-22-w5-history-builder-wiring.md) — 4 tasks. Executed inline (native execution). Task 4's verification found and fixed a pre-existing-shaped test flakiness in the new compaction test fixture (an uncached `AssistantUtility` DB lookup racing the test assembly's method-level parallelization) and left two full-solution failure clusters out of scope — the `estimateSource` deserialization failures turned out to be caused by W2, not pre-existing, and were fixed in W9 Task 1; `ScriptExecutionAgent.Tests`' sandbox/MCP-stdio failures are classified in W9's CI-mirror table.

- [x] 5.1 In `ConversationHistoryBuilder`: when a boundary is set, load pre-boundary
      `ConversationTurn` rows as `TurnFacts`, call the engine, emit `[system: summary]` + verbatim tail
- [x] 5.2 No boundary → entirely unchanged behavior, no engine call
- [x] 5.3 Leave the three `BuildPublishedMessagesForAssistantAsync` call sites untouched —
      published and sandbox-wire keep current behavior in v1
- [x] 5.4 Verify the assistant-switch path (`ApplyAssistantSwitchLogicAsync`) composes correctly
      with a boundary
- [x] 5.5 `CaptureCompaction` on `IThreadRunTraceCollector`, following `CaptureToolLimitState`
- [x] 5.6 Stream event via `ConversationStreamEventWriter` for the client boundary marker
- [x] 5.7 Tests: with and without boundary, assistant switch, repeated compaction loses no more
      than a single compaction

---

## W6 — Overflow cleanup *(sequencing: must land after W4 + W8's button — both done)*

Removing the unwind removes the only thing keeping an overflowing conversation alive. It cannot
ship before users have a working remedy.

**Implementation plan written:** [`docs/superpowers/plans/2026-09-24-w6-overflow-cleanup.md`](superpowers/plans/2026-09-24-w6-overflow-cleanup.md) — 5 tasks. Resolves both "verify first" items below before touching code: `IsReplacement` has **zero** readers anywhere (not just outside the unwind path — stronger than 6.1's framing), and `ConversationPersistence.cs`/`ConversationHistoryBuilder.cs`'s dedup-by-`ToolCallId` logic is written generically and stays correct after removal (their comments get reworded, not their logic). Also corrects the checklist's own line citation — `ConversationHistoryBuilder.cs:607` is an unrelated `Role` switch; the real comment is at line 739. Adds one task beyond the 5 items listed: removing the now-dead `IsReplacement` flag/parameter as part of the same change, since it has no readers and the unwind removal is what exposes that.

- [x] 6.1 **Verify first:** does `MessageAddedEventArgs.IsReplacement` have consumers beyond the
      unwind path? `ConversationPersistence.cs:769` and `ConversationHistoryBuilder.cs:607` both
      reference overflow-unwind inserts (see Q-ii)
- [x] 6.2 **Verify first:** does any existing test assert unwind behavior?
- [x] 6.3 Remove `TryUnwindOversizedMessage`, `BuildAbortReplacement`,
      `BuildContextOverflowNotice`, and the `unwoundMessages` tracking
- [x] 6.4 Sharpen the `chat_context_overflow` message in `StreamingErrorEnvelope` to name
      compaction as the remedy
- [x] 6.5 Test: a conversation past the window returns the error with **no message content mutated**

---

## W7 — Recall tool

**Implementation plan written:** [`docs/superpowers/plans/2026-09-24-w7-recall-tool.md`](superpowers/plans/2026-09-24-w7-recall-tool.md) — 5 tasks. Two corrections to the checklist and spec fell out of the research: (1) **7.3's search space is `TurnIndex <= boundary`, not `<`** — the shipped `ConversationHistoryBuilder` summarizes `<= boundary` and keeps `> boundary` verbatim, so a `<` recall would leave the boundary turn reachable by neither the tail nor recall; (2) **7.5 could not use the `AssistantUtility` injection point** that `skills_list`/`skills_read` use, because that cache is keyed by assistant name and shared process-wide, and "has a compaction boundary" is a property of the conversation. Exposure travels instead on a new run-scoped `ChatRunOptions.EnableConversationRecall`, set at the one private-path call site in `ConversationService.BuildRunContext`. The plan also adds a task beyond the 7 items: registering the dispatch-side request builder unconditionally in `EnsureRequestBuilderCache`, since a run-scoped tool is invisible to that method's `assistantOperationIds` gate and a missing builder drops the tool result silently.

- [x] 7.1 `conversation_recall(query, page?)` as a static `[Tool]` method following `MemoryTools`
- [x] 7.2 Scope via `[Parameter(Hidden = true)] InvocationContext? context` — `ConversationId`
      is framework-injected, never model-supplied
- [x] 7.3 Search space: this conversation, `TurnIndex <= CompactionBoundaryTurnIndex`
- [x] 7.4 In-memory relevance ranking. No SQL Server full-text dependency, so behavior is
      identical on every deployment
- [x] 7.5 Attach the tool only when a boundary exists
- [x] 7.6 Paginated, structure-preserving result formatting
- [x] 7.7 Tests: ranking, pagination, and a cross-conversation read that must fail

---

## W8 — Client

Primary surface for the feature — the button is the whole trigger, so this is not polish.

**Implementation plan written:** [`docs/superpowers/plans/2026-09-23-w8-client-context-meter-and-compact.md`](superpowers/plans/2026-09-23-w8-client-context-meter-and-compact.md) — 8 tasks. Task 8's rollup ran `npm run test:coverage` clean: typecheck passes and the new `ContextMeter`/`CompactButton` code is well covered (`ContextMeter.tsx` 100% across all four metrics; `CompactButton.tsx` 100% lines). The rollup surfaced three pre-existing issues out of this workstream's scope, all verified against `main` before this branch existed: (1) this sandbox's default Node (26.3.0) makes `localStorage` a conflicting global with jsdom, failing 7 sidebar/layout test files unrelated to compaction — resolved for verification by rerunning under the repo's supported Node 22.19.0; (2) `ProjectLayout.test.tsx`'s sidebar-width-persistence test fails even on Node 22, unrelated to this plan; (3) `vitest.config.ts`'s coverage thresholds are flat keys (`lines`/`functions`/`branches`/`statements` directly under `coverage`), which Vitest 4's schema expects nested under `coverage.thresholds` — the gate has not actually been enforced since the Vitest 4 upgrade, and the resulting aggregate shortfall (statements ~84.8% vs. the intended 85%, branches ~76.5% vs. 80%) is identical within rounding on `main`, so it predates this plan rather than being caused by it. Manual browser smoke check (8's step 2) was skipped — no running API/dev stack in this environment.

- [x] 8.1 Context meter in the composer footer row of `DraftUserCell.tsx`, beside the Send
      button and the existing `Ctrl+Enter` hint — the signal sits at the moment of the decision
      it informs
- [x] 8.2 Extract the meter and compact button as their own components rather than inlining.
      `DraftUserCell.tsx` is already 836 lines; adding markup there makes it worse
- [x] 8.3 Meter states: known utilization, unknown window (explicit, never a guess), compacted,
      and busy/disabled while the conversation is locked (D8)
- [x] 8.4 Compact button wired to the endpoint, with before/after token feedback
- [x] 8.5 Handle `409 Conflict` from the compact endpoint — surface "conversation is busy"
      rather than a generic failure
- [x] 8.6 Boundary marker rendered in the transcript
- [x] 8.7 Turn `chat_context_overflow` into a clear, actionable message naming compaction
- [x] 8.8 Tests: all four meter states, button, 409 path, marker. Vitest, ≥85% line gate

---

## W9 — Validation

**Implementation plan written:** [`docs/superpowers/plans/2026-09-24-w9-validation.md`](superpowers/plans/2026-09-24-w9-validation.md) — 6 tasks. The research found that the integration failures W5 and W7 called "pre-existing" were caused by this branch: W2's `ContextStatus` enums could not be read with default JSON options (`$.contextStatus.estimateSource`). Task 1 fixed that by annotating the enum types. 9.3 is split in two:
- A hermetic end-to-end test that runs in CI (`CompactionEndToEndTests`).
- An opt-in run against a real llama.cpp (`CompactionLlamaCppEndToEndTests`, runbook in `docs/compaction-llamacpp-e2e-runbook.md`). Result: passed on 2026-09-24 with qwen2.5-0.5b-instruct (a throwaway `-c 2048` llama-server on port 8199).

9.1 findings: [`docs/compaction-benchmark-findings.md`](compaction-benchmark-findings.md) — D2 risk partially materialized. Corpus caveat: the local dev database's non-coding sample is two concierge/booking-style transcripts plus one synthetic test-fixture conversation; no document/image/audio-producing notebook is represented.

- [x] 9.1 **Cross-domain benchmark** — run the engine over real transcripts from coding *and*
      non-coding notebooks and read the output. With a fixed vocabulary and no LLM fallback,
      this is the only evidence the sections carry meaning outside sandbox guides. Not optional
- [x] 9.2 Server unit tests (`dotnet test GuideAntsApi.Tests/...`)
- [x] 9.3 Integration: long conversation → overflow error → compact → continue → recall, against
      a small-context local llama.cpp model (`run-test-coverage.ps1 -Scope Integration`, needs Docker)
- [x] 9.4 Mirror the CI jobs in `.github/workflows/client-test.yml`

CI mirror (2026-09-24, commit f82e6f1): server unit 2622p/0f/1s (GuideAntsApi.Tests) + 87p/6f/3s (ScriptExecutionAgent.Tests), integration 268p/0f/1s, client 3621p/1f (367 of 368 files). Pre-existing failures, all confirmed failing on `main` too: ScriptExecutionAgent.Tests' 6 `McpStdio_*`/`Execute_*` tests (`pwsh` not installed on this host — environmental, not code); `ProjectLayout.test.tsx`'s sidebar-width-persistence test (unrelated to compaction). No branch-caused failures found.

---

## W10 — Ship

Carried from W9:
- `src/client/vitest.config.ts` puts coverage thresholds as flat keys that Vitest 4 ignores, so the ≥85% gate `CLAUDE.md` describes is not enforced in CI (pre-existing on `main`, found in W8).
- The client suite fails under Node 26 (jsdom `localStorage`); CI pins Node 22.
- The ADR for D2 should cite the benchmark findings by name: the domain-fit risk did not materialize for the Goal/Artifacts/Directives vocabulary itself, but a separate, already-documented limitation (outcome signal exists only for `ScriptExecutionResult.ExitCode`) affects Activity-ledger usefulness in both domains, disproportionately in non-coding transcripts where nearly every tool call is client-side. Recall is the accepted mitigation, not a fallback to fix the extractor.
- Follow-up worth scoping later, not done in W9: teach the Activity ledger extractor to read a generic `{"error": ...}` / `{"status": "failed"}` shape from tool results that aren't `ScriptExecutionResult`, so `external_tool` and `Code_Executor` calls can report something better than "unknown" when the underlying JSON already carries a clear success/failure signal.
- Follow-up worth scoping later: when a conversation's Artifacts list is dominated by auto-generated dependency files (observed in one benchmarked conversation), consider filtering well-known vendor/package-install paths so the section highlights user-meaningful outputs.
- Coverage gap to note, not fixed in W9: the benchmark corpus had no notebook producing documents, images, or audio, and both real non-coding samples were external-tool-driven concierge/booking assistants — the same narrow shape. A future benchmark run should widen the corpus once such notebooks exist.
- The `guideants` dev database accumulates test-fixture noise (one benchmarked "conversation" was a leftover integration-test preflight ping, not a real session) — a future benchmark run should filter or flag such conversations rather than rely on a human noticing.

- [ ] 10.1 ADRs for D1, D2, D3, D5
- [ ] 10.2 Update `CLAUDE.md` architecture section if the engine lands as a new namespace
- [ ] 10.3 `/code-review`
- [ ] 10.4 `gh pr create` against `main`; delete the branch locally and on origin after merge

---

## Open questions

None remain — Q-ii resolved below during W6.

> **Q-ii — Does `MessageAddedEventArgs.IsReplacement` have non-unwind consumers?** (blocks: 6.3)
> **Resolved.** `IsReplacement` was found to have zero readers anywhere in the codebase — not just
> outside the unwind path — confirmed by exhaustive grep during this plan's research and
> independently re-confirmed by three separate task reviewers during execution. The two
> originally-cited call sites, `ConversationPersistence.cs` and `ConversationHistoryBuilder.cs`,
> referenced generic dedup-by-`ToolCallId` comments, not `IsReplacement` reads — both stayed
> correct and non-dead after removal; only their comments were reworded (Task 3), not their logic.
> The checklist's original line citation for `ConversationHistoryBuilder.cs` was also off — it
> named line 607 (an unrelated `Role` switch), when the real comment was at line 739.

---

## Deferred

- **Prompt caching.** The actual cost lever — no `cache_control` breakpoints exist in any
  provider client, and guide instructions run up to 256,000 characters (`ThreadRun.cs:380`)
  re-billed in full every turn. Worth its own effort.
- Published guides, public API, MCP surfaces.
- Intra-turn compaction (a single turn that overflows on its own).
- Per-guide compaction policy and export/import manifest keys.
- `MaxOutputTokens` as a per-guide override — model-level only in v1.
- HTTP response compression (`AddResponseCompression` is not configured). Unrelated to context
  management; noted so it isn't silently absorbed.
