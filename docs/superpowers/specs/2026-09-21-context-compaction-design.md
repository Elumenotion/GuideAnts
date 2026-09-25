# Context Compaction — Design

Date: 2026-09-21
Branch: `feature/compaction`
Status: Design approved, pending implementation plan
Task list: [`docs/context-compaction-plan.md`](../../context-compaction-plan.md) — this spec is
the design authority; where the two disagree, this one wins.

---

## Problem

`ConversationHistoryBuilder.FilterMessages` replays the entire conversation on every turn —
no window, no cap, no summarization. `ThreadRun` re-sends the full message array each round,
and `Assistant.MaxToolCallsPerTurn` defaults to unlimited.

The only existing defense is reactive and destructive. On `ChatContextOverflowException`,
`TryUnwindOversizedMessage` finds the longest non-system message, replaces its content with an
abort notice, **persists that replacement**, and retries. Long conversations end in silent,
permanent data loss.

GuideAnts also cannot currently tell how full a context is: the `Models` catalog has no
context-window column, so only llama.cpp router entries know their own limit.

## Goals

1. Give users an explicit, visible way to compact a conversation they choose to compact.
2. Show users how full their context is, so the choice is informed.
3. Stop destroying message content on overflow.
4. Keep compacted history reachable, so compaction loses nothing permanently.

## Non-goals

- **Automatic compaction of any kind.** No thresholds, no proactive triggers, no compaction
  in response to overflow. Compaction happens when a user asks for it and at no other time.
- **LLM-based summarization.** No summarization model call, no per-compaction token cost.
- **Cost reduction.** Conversations are mostly short with a rare long tail, so compaction will
  not move aggregate cost. The cost lever is prompt caching, which is deliberately out of scope
  (see *Deferred work*).
- **Published guides, the public API, and MCP surfaces.** v1 is notebook UI only.
- **Intra-turn compaction.** A single turn that overflows on its own is not addressed here.

---

## Decisions

| # | Decision | Rationale |
|---|---|---|
| D1 | Manual trigger only | User requirement. No implicit context mutation. |
| D2 | Algorithmic extraction, no LLM tier | Deterministic, free, offline-capable, no quality variance. Accepts domain-fit risk with no fallback. |
| D3 | Persist the decision, recompute the content | A boundary turn index is user-authored and can never go stale. Summary text recomputed deterministically, so editing a message behind the boundary stays correct. |
| D4 | Shape at history-build time, not in `ThreadRun` | Manual triggering means the boundary is known before the turn starts. `ThreadRun`'s choke-point advantage only mattered for mid-flight decisions. |
| D5 | Overflow fails cleanly | Remove `TryUnwindOversizedMessage`. A clear error that names the remedy beats silent data loss. |
| D6 | Fixed, protocol-derived section vocabulary | Sections derived from GuideAnts' own tool/file protocol are domain-neutral; pi-vcc's content-derived sections are coding-specific. |
| D7 | Recall exposed only when a boundary exists | Zero behavior change for conversations that were never compacted. |
| D8 | Compaction requires an idle conversation | Avoids racing `ConversationStreamEngine`. Enforced by acquiring the existing distributed conversation lock, which also blocks a stream from starting mid-write. |

---

## Architecture

Four components.

### 1. `CompactionEngine` — pure library in `AntRunner.Chat`

Static, deterministic, no I/O, no DB, no vendor knowledge.

```
Compact(IReadOnlyList<ChatMessage> preBoundary,
        IReadOnlyList<TurnFacts> turnFacts) -> CompactionResult
```

`TurnFacts` is a provider-neutral projection of persisted per-turn data the caller loads —
turn index, and the files created/modified during that turn. The engine stays pure: it
performs no I/O, it simply takes richer input than the message list alone. This matters
because some facts worth summarizing (file mutations) are recorded at turn completion and
are not recoverable from the message list without re-parsing tool results.

Sits alongside `ToolOutputTruncator`, which is the precedent: a pure message-shaping utility
in the same project. **Must not reference `GuideAntsApi` or `GuideAntsApi.DataModel`** or it
stops being provider-neutral.

Pipeline: normalize to uniform blocks → filter noise → extract sections → render.

Note there is no cut-selection stage. See *The boundary* below — the user's press supplies the
cut, so no token-budget backwalk is needed.

### 2. Context-window metadata on `ResolvedExecutionPolicy`

Extend the record with `ContextWindowTokens` and `MaxOutputTokens`. It already declares itself
the single source of truth for model parameters and is already threaded into the run, so no new
plumbing channel is introduced.

Resolution precedence:

1. Live `n_ctx` from the llama.cpp runtime (local models)
2. `Model.ContextWindowTokens` from the catalog
3. Value learned from a previous `ChatContextOverflowException` (which already carries `ContextSize`)
4. Unknown — meter shows "unknown", nothing else changes

Step 3 is metadata learning only. It never triggers compaction.

### 3. `ConversationRecallService` — `GuideAntsApi.Services.Conversations`

The one component that needs EF and authorization, so it cannot live in the engine.

### 4. Trace and stream signals

`CaptureCompaction` on `IThreadRunTraceCollector`, following the `CaptureToolLimitState`
precedent. A stream event via `ConversationStreamEventWriter` so the client can render a
boundary marker.

---

## The boundary

One nullable column on `NotebookConversation`:

```csharp
/// Turn index at which the user last compacted. Messages with a lower TurnIndex are
/// replaced by a generated summary at history-build time. Null = never compacted.
public int? CompactionBoundaryTurnIndex { get; set; }
```

**Selection.** When the user presses compact, the boundary becomes the index of the last
*complete* turn. If a turn is in flight the boundary stops short of it, so an assistant message
carrying `tool_calls` is never separated from its `tool_result` messages, and a turn paused
awaiting a client-handled tool (cf. commit `91398ae3`) survives intact. This is the only
invariant the selection has to satisfy — the user chose the moment, so there is no token
budget to satisfy.

**Monotonic.** Compacting again moves the boundary forward. It never moves back.

**No summary-of-summary degradation.** Because the summary is always recomputed from the
original pre-boundary messages rather than merged into a previous summary, repeated compaction
does not compound loss. This is a direct advantage over merge-based compactors, which have to
re-cap sections to stop them drifting.

---

## Section vocabulary

Derived from facts GuideAnts' own protocol guarantees, not from content heuristics.

| Section | Source | Domain-neutral because |
|---|---|---|
| Goal | First user message, plus later scope-change markers | Every conversation has a first message |
| Artifacts | `ConversationTurn.FilesCreated` / `FilesModified` — persisted columns, populated from `ChatRunOutput` at turn completion, supplied to the engine as `TurnFacts` | Notebooks produce files whether code, audio, or documents |
| Activity ledger | One line per tool call: name, key args, ok/error | Derived from the tool-call protocol, not content |
| Unresolved errors | Tool results that errored with no later successful call to the same tool | Error shape is protocol-level (`ScriptExecutionResult`, exit codes) |
| Directives | Regex over user messages (`always`, `never`, `prefer`, `don't`) | Language-level, not domain-level |
| Tail | Verbatim turns after the boundary | — |

**Placement.** The summary is injected as a `system` message after the instructions prefix and
before the kept tail, with an explicit framing line marking it as a handoff briefing rather than
text to continue. Both pi-core and Codex specifically engineer against the model continuing a
summary; we inherit that lesson rather than rediscovering it.

---

## Data flow

```
User presses Compact
  -> POST /api/notebooks/{id}/conversations/{conversationId}/compact
  -> resolve last complete turn index
  -> write CompactionBoundaryTurnIndex
  -> return new boundary + estimated token delta

Next turn
  -> ConversationHistoryBuilder loads conversation
  -> boundary set?  no  -> unchanged behavior, no compaction, no recall tool
                    yes -> load ConversationTurn rows before boundary as TurnFacts
                        -> CompactionEngine.Compact(preBoundaryMessages, turnFacts)
                        -> [system: summary] + verbatim tail
                        -> attach conversation_recall tool
  -> ThreadRun (unchanged)
```

`ThreadRun` requires no changes for compaction itself. The three
`BuildPublishedMessagesForAssistantAsync` call sites are untouched, so published and
sandbox-wire paths keep current behavior.

---

## API surface

**Compact**
`POST /api/notebooks/{notebookId}/conversations/{conversationId}/compact`
Returns `{ boundaryTurnIndex, messagesSummarized, estimatedTokensBefore, estimatedTokensAfter }`.
Idempotent when no new complete turn exists since the last boundary.

**Requires an idle conversation (D8).** The endpoint acquires the same distributed conversation
lock `ConversationStreamEngine` uses, writes the boundary, and releases. If the lock is held it
returns `409 Conflict` naming the holder — the same shape the existing lock surface already
produces via `LockAcquisitionResult.AlreadyLocked`. Using the lock rather than a read-only
`ConversationStreamRunRegistry.IsAnyActiveForConversation` check matters for two reasons: it is
authoritative across API instances in a multi-instance self-hosted deployment, and it closes the
race in both directions by preventing a stream from starting while the boundary is being written.

**Context status** — extends the existing conversation read rather than adding an endpoint:
`{ contextWindowTokens, estimatedPromptTokens, boundaryTurnIndex }`.
`contextWindowTokens` is nullable; the meter renders an unknown state rather than guessing.

---

## Client surface

**Meter placement.** The context meter sits in the composer footer row in
`DraftUserCell.tsx`, beside the Send button and the existing `Ctrl+Enter` hint. That puts the
"how full am I" signal at the moment of the decision it informs — immediately before sending
the message that would grow the context — rather than in the header where it competes with
notebook-level controls.

**Extract, don't inline.** `DraftUserCell.tsx` is already 836 lines. The meter and the compact
button ship as their own small components consumed by that footer row, not as additional inline
markup. This is targeted to the work, not general refactoring.

**States the meter must render:**

| State | Trigger |
|---|---|
| Known utilization | `contextWindowTokens` resolved; show estimate against window |
| Unknown window | `contextWindowTokens` is null; render an explicit unknown state, never a guess |
| Compacted | `boundaryTurnIndex` set; meter reflects post-boundary size |
| Busy | Conversation locked; compact action disabled (D8) |

---

## Recall

```
conversation_recall(query, page?)
```

Static method with `[Tool]` and a `[Parameter(Hidden = true)] InvocationContext? context`,
following `MemoryTools` exactly.

**Scoping is structural.** `InvocationContext` already carries `ConversationId` and `TurnIndex`
as framework-injected fields. The model supplies only `query`; it cannot reach another
conversation. This reuses the mechanism that already scopes `search_project`.

**Search space.** Messages in this conversation with `TurnIndex <= CompactionBoundaryTurnIndex` —
exactly the set `ConversationHistoryBuilder` replaces with the generated summary. (An earlier draft
said `<`, which would have left the boundary turn itself in neither the verbatim tail nor recall.)

**Ranking.** In-memory relevance scan. No SQL Server full-text dependency, so behavior is
identical on every deployment including the containerized SQL Server image.

**Exposure.** Attached only when `CompactionBoundaryTurnIndex` is set.

---

## Error handling

| Case | Behavior |
|---|---|
| Context overflow | `ChatContextOverflowException` surfaces as `chat_context_overflow` through the existing `StreamingErrorEnvelope`, with a message naming compaction as the remedy. **No message content is mutated.** |
| Engine throws | Log, proceed with uncompacted history. Compaction failing must never fail a turn. |
| Engine cannot reduce | Normal return value, not an exception. History passes through unchanged. |
| Compact pressed with no complete turn | No-op, returns the existing boundary. |

**Removal work.** `TryUnwindOversizedMessage`, `BuildAbortReplacement`,
`BuildContextOverflowNotice`, and the `unwoundMessages` tracking come out. Before removing,
verify: (a) whether `MessageAddedEventArgs.IsReplacement` has consumers other than the unwind
path — `ConversationPersistence.cs:769` and `ConversationHistoryBuilder.cs:607` both reference
overflow-unwind inserts and must be checked; (b) that no existing test asserts unwind behavior.

---

## Testing

Ordered by consequence of failure.

1. **Boundary invariants** — `tool_calls`/`tool_result` pairing preserved; a turn paused on a
   client-handled tool is never split. Broken invariants produce provider-rejected requests.
2. **Determinism** — identical input yields byte-identical output. This is the property D3 rests on.
3. **Repeated compaction** — compacting three times loses no more than compacting once.
4. **Overflow fails cleanly** — assert explicitly that no message content changed.
5. **Cross-domain benchmark** — run the engine over real transcripts from coding *and*
   non-coding notebooks and read the output. With a fixed vocabulary and no LLM fallback, this
   is the only evidence that the sections carry meaning outside sandbox guides.
6. **Recall scoping** — a cross-conversation read must fail.
7. **Client** — meter renders (including unknown state), button calls the endpoint, boundary
   marker appears. Vitest, ≥85% line gate.

**Integration.** One end-to-end run against a small-context local llama.cpp model: long
conversation → overflow error → compact → continue → recall. llama.cpp's small windows make
this cheap to reproduce.

---

## Risks accepted

| Risk | Why accepted |
|---|---|
| Overflow still interrupts work | D1 rules out auto-recovery. Mitigated by a clear error and a one-press remedy, and by the meter warning beforehand. |
| Extraction quality may be poor for non-coding domains | D2 leaves no fallback. Mitigated only by test 5 — which is why it is not optional. |
| Users may never press the button | Compaction is worthless if invisible. The meter is the mitigation, which is why it is in v1 rather than deferred. |
| Prefix ordering may need rework when caching lands | Accepted when caching was deferred. Keep section ordering cheap to change. |

---

## Deferred work

- **Prompt caching.** The actual cost lever: no `cache_control` breakpoints exist in any
  provider client, and guide instructions run up to 256,000 characters
  (`ThreadRun.cs:380`) re-billed in full every turn. Scoped out deliberately; worth its own
  effort.
- Published guides, public API, MCP surfaces.
- Intra-turn compaction.
- Per-guide compaction policy and export/import manifest keys.
- `MaxOutputTokens` as a per-guide override (model-level only in v1).

---

## Open questions for implementation planning

1. Does `MessageAddedEventArgs.IsReplacement` have consumers beyond the unwind path? Determines
   how much of the removal in *Error handling* is safe. Answerable by reading code — listed
   because the answer changes the task, not because it needs a decision.
