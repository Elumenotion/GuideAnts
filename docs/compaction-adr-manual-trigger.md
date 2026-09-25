# ADR-0001: Manual-only compaction trigger

**Status:** Accepted (shipped)
**Date:** 2026-09-24

## Context

`ConversationHistoryBuilder.FilterMessages` replays the entire conversation on every turn — no
window, no cap, no summarization. Long conversations eventually overflow the model's context
window. Some form of compaction (replacing older messages with a summary) is needed to let users
keep working past that point.

The open question was *when* compaction happens: automatically, in response to some signal
(a token threshold, an overflow event, a background job), or only when the user explicitly asks
for it.

## Decision

Compaction happens **only** when the user presses the Compact button in the notebook UI, and at
no other time. No thresholds, no proactive triggers, no compaction in response to overflow. This
is implemented as a single explicit endpoint,
`POST /api/notebooks/{notebookId}/conversations/{conversationId}/compact`
(`src/server/GuideAntsApi/Endpoints/NotebookConversationsEndpoints.cs`), calling
`ICompactionService.CompactConversationAsync`
(`src/server/GuideAntsApi/Services/Conversations/Commands/CompactionService.cs`). Nothing else in
the codebase writes `NotebookConversation.CompactionBoundaryTurnIndex`.

## Options Considered

### Option A: Manual trigger only (chosen)

| Dimension | Assessment |
|-----------|------------|
| Complexity | Low — one endpoint, one boundary column, no scheduling/threshold logic |
| User trust | High — context is never mutated without an explicit user action |
| Predictability | High — the same conversation state always produces the same request until the user acts |
| Coverage of the overflow case | Partial — overflow can still interrupt a turn; the user must notice and press Compact themselves (mitigated by the context meter, shipped in W8, and a corrected overflow error message naming Compact as the remedy, shipped in W6) |

**Pros:** No implicit context mutation — a hard requirement carried over from the user's own
stated preference. Simple to reason about, test, and explain. Zero risk of a background trigger
firing at the wrong moment (e.g. mid-turn) or compacting content the user still needed verbatim.
**Cons:** Users who never notice the context meter get no automatic help before an overflow.

### Option B: Threshold-based automatic compaction

| Dimension | Assessment |
|-----------|------------|
| Complexity | Medium-High — needs a threshold policy, a trigger point in the turn lifecycle, and safeguards against triggering mid-stream |
| User trust | Lower — context is silently rewritten without the user asking, which the project's own "no implicit context mutation" rule rules out |
| Predictability | Lower — the same conversation could compact at different points depending on model/window changes |
| Coverage of the overflow case | Better — could compact before the user ever hits an overflow |

**Pros:** Removes the burden of remembering to press a button; could pre-empt overflows entirely.
**Cons:** Violates the explicit user requirement against implicit context mutation. Introduces a
whole new failure mode (a badly timed automatic compaction) that this project's design deliberately
avoided.

### Option C: Automatic compaction only in response to an overflow (reactive)

| Dimension | Assessment |
|-----------|------------|
| Complexity | Medium — needs to catch the overflow, retry, and only compact on that specific failure path |
| User trust | Medium — narrower scope than Option B, but still an implicit mutation triggered by an error |
| Predictability | Medium |
| Coverage of the overflow case | Full, by construction |

**Pros:** Directly addresses the one case D1 leaves uncovered.
**Cons:** This is functionally what the pre-existing `TryUnwindOversizedMessage` mechanism did
(see [ADR-0004](compaction-adr-overflow-fails-cleanly.md)) — reactive, destructive, and the reason it was
removed. Any reactive auto-compaction reintroduces the same "the user didn't ask for this and now
their history looks different" problem in a new shape.

## Trade-off Analysis

The overflow-coverage gap (Option A's one real weakness) is deliberately not closed by automating
the trigger. It is closed instead by making the *manual* path good enough to reach for: the
context meter (W8) surfaces "how full am I" at the moment of the decision it informs — right
before sending a message — and the overflow error itself was rewritten (W6) to name Compact as the
remedy rather than silently destroying data. Both are cheaper and safer than any of the automatic
options, and neither compromises the "no implicit context mutation" requirement.

## Consequences

- **Easier:** Testing (one code path decides the boundary — a user's button press), reasoning
  about state (a conversation's history is stable until the user acts), and explaining the
  feature to users (there's exactly one way it can change).
- **Harder:** A user can still hit an overflow before ever pressing Compact. This is accepted and
  mitigated, not eliminated — see the *Risks accepted* section of the design spec.
- **To revisit:** If usage data ever shows most users hit overflow before discovering the Compact
  button, the meter's visibility/threshold color states are the lever to pull, not the trigger
  model itself.

## Action Items

1. [x] `POST .../compact` endpoint, idle-conversation lock (D8) — shipped W4.
2. [x] Context meter in the composer footer — shipped W8.
3. [x] Overflow error message names Compact as the remedy — shipped W6.
