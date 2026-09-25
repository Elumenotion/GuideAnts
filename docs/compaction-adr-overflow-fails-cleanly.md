# ADR-0004: Context overflow fails cleanly instead of destructively unwinding

**Status:** Accepted (shipped)
**Date:** 2026-09-24
**Deciders:** GuideAnts context-compaction workstream (design spec [`docs/superpowers/specs/2026-09-21-context-compaction-design.md`](superpowers/specs/2026-09-21-context-compaction-design.md), decision D5)

## Context

Before this workstream, the only defense against a request exceeding the model's context window
was `ThreadRun.TryUnwindOversizedMessage`: on `ChatContextOverflowException`, it found the longest
non-system message in the turn, replaced its content with an abort notice, **persisted that
replacement**, and retried. If the retry still overflowed, it unwound the next-longest message,
and so on, silently and permanently destroying message content until the request fit or nothing
was left to unwind.

Once compaction gave users a real, one-press remedy for an oversized conversation (D1), the
question was whether the unwind mechanism should stay as a last-resort safety net or be removed
entirely.

## Decision

`TryUnwindOversizedMessage`, `BuildAbortReplacement`, `BuildContextOverflowNotice`, and the
`unwoundMessages` tracking were removed from `ThreadRun.cs` outright — not kept as a fallback.
`ChatContextOverflowException` now propagates as a plain, uncaught exception straight out of
`ThreadRun.ExecuteAsync` on the very first overflow. `ConversationStreamEngine`'s existing outer
exception handler surfaces it through `StreamingErrorEnvelope` with `code: "chat_context_overflow"`
and a message that names Compact as the remedy
(`src/server/GuideAntsApi/Services/Conversations/StreamingErrorEnvelope.cs`). No message content
is ever mutated by an overflow, verified by a test that snapshots every message before an overflow
and asserts the snapshot is unchanged afterward.

## Options Considered

### Option A: Remove the unwind mechanism entirely (chosen)

| Dimension | Assessment |
|-----------|------------|
| Complexity | Lower — deleting code, not adding a coexistence path between two overflow-handling strategies |
| Data integrity | Total — nothing in the codebase mutates a stored message's content on overflow, by construction, once the only thing that ever did is gone |
| User experience on overflow | A clean, named, actionable error, with Compact as the documented remedy and the context meter (W8) as the earlier warning |
| Risk if compaction itself is unavailable | The user is blocked until they compact — no automatic fallback survives |

**Pros:** A clear error that names the remedy beats silent data loss — the whole reason this
mechanism existed was to keep a conversation "working" at the cost of destroying content the user
never agreed to lose, and the actual remedy (Compact) is a strictly better answer once it exists.
Removing it outright means no code path in the entire application can ever again silently rewrite
a stored message's content, which the plan's own regression tests treat as a first-class
invariant. **Cons:** A turn that overflows now hard-stops rather than "limping through" — the user
must act (press Compact) before continuing, rather than the conversation silently degrading itself
to stay usable.

### Option B: Keep unwind as a last-resort fallback behind compaction

| Dimension | Assessment |
|-----------|------------|
| Complexity | Higher — two overflow-handling strategies have to coexist, with logic to decide when compaction isn't enough and unwind should still run |
| Data integrity | Compromised — the whole point of a "fallback" is that it fires when the primary remedy didn't (or couldn't) resolve things, i.e. exactly when a user still loses content silently |
| User experience on overflow | Confusing — a conversation could still silently lose content in edge cases, undermining the “Compact is the way to handle this” message the UI now gives |
| Risk if compaction itself is unavailable | Lower in the narrow sense that the turn might still complete — at the cost of reintroducing the exact failure mode this workstream exists to fix |

**Pros:** Slightly more resilient in a pathological case (a single message so large that even a
maximal compaction can't bring the conversation under the window). **Cons:** This is precisely the
"still overflowing after compaction" case, which the shipped tests handle correctly as a clean,
non-mutating failure — there was no real gap for the fallback to close. Keeping it would mean the
"never mutate a stored message" invariant this workstream establishes has an exception, which
defeats the point of establishing it as an invariant at all.

## Trade-off Analysis

Sequencing mattered more than the choice itself: removing the unwind before users had a working
remedy would have shipped a strictly worse experience (hard failures with no way out). The
checklist explicitly gated this removal on W4 (the Compact endpoint) and W8 (the client button)
both being done first. Once compaction and the button existed, Option A's "cons" column shrank to
"the user has to act," which is exactly the behavior D1 already establishes as correct — compaction
should never happen without the user asking, including via an automatic fallback that quietly
resembles the old destructive behavior. A conversation that still overflows after compaction (a
single oversized message) is covered by an explicit test asserting the clean-failure, no-mutation
behavior — the case Option B worried about already has a defined, tested outcome under Option A.

## Consequences

- **Easier:** There is exactly one invariant to hold about message content: it never changes on
  overflow. No code path is exempt from it, so no test suite has to special-case an exception to
  the rule.
- **Harder:** A turn that overflows always stops the conversation until the user compacts. There
  is no silent degrade-and-continue path anymore, by design.
- **To revisit:** If telemetry ever shows "still overflowing after compaction" happening often
  enough to be a real user-facing pain point (rather than the rare, near-pathological single
  oversized message case it's designed for today), the fix is a better meter/warning earlier in
  the flow, not resurrecting a destructive fallback.

## Action Items

1. [x] Remove `TryUnwindOversizedMessage`, `BuildAbortReplacement`, `BuildContextOverflowNotice`,
   `unwoundMessages` — shipped W6, gated on W4 + W8 landing first.
2. [x] Sharpen `chat_context_overflow`'s message to name Compact as the remedy — shipped W6.
3. [x] Regression test: overflow surfaces cleanly with no message content mutated — shipped W6,
   re-verified end-to-end (including the "still overflowing after compaction" case) in W9's
   `CompactionEndToEndTests`.
