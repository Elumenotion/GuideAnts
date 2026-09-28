# ADR-0003: Persist the boundary decision, recompute the summary content

**Status:** Accepted (shipped)
**Date:** 2026-09-24

## Context

When a user compacts a conversation, something has to be stored so future turns know what's been
compacted. The two obvious shapes are: store the *result* (the generated summary text itself,
like a cache), or store the *decision* (just where the boundary is) and regenerate the summary
every time it's needed. This choice determines what happens when a user edits a message that sits
behind an already-set boundary, and what happens if a user presses Compact more than once.

## Decision

`NotebookConversation.CompactionBoundaryTurnIndex` (`int?`,
`src/server/GuideAntsApi.DataModel/Models/NotebookConversation.cs`) stores only the turn index.
`ConversationHistoryBuilder.BuildCompactionSummaryMessageAsync`
(`src/server/GuideAntsApi/Services/Conversations/Mapping/ConversationHistoryBuilder.cs`) reloads
every pre-boundary message from source and runs them through `CompactionEngine.Compact` **on every
history build**, never reading a previously-stored summary. No summary text is ever persisted.

## Options Considered

### Option A: Persist the boundary, recompute the content (chosen)

| Dimension | Assessment |
|-----------|------------|
| Complexity | Low — one nullable int column, no summary storage, no cache-invalidation logic |
| Correctness under edits | Total — a message behind the boundary can be edited and the very next history build reflects it, because the summary is never stale by construction |
| Cost of repeated compaction | Flat — pressing Compact again just recomputes from the (larger) pre-boundary set; no compounding |
| Read cost | One extra deterministic computation per history build (bounded, pure, no I/O beyond the message load already needed) |

**Pros:** The boundary is user-authored and literally cannot go stale — it's just "the last turn
the user chose." Recomputing means repeated compaction never compounds information loss the way
re-summarizing a summary can (a real weakness of merge-based compactors), because every compaction
starts from the original messages again. Editing a message behind the boundary "just works" with
no invalidation code needed anywhere.
**Cons:** Every history build repeats the extraction work, even though the input hasn't changed
since the last build in the common case.

### Option B: Persist the generated summary text as a cache

| Dimension | Assessment |
|-----------|------------|
| Complexity | Higher — needs a summary-text column, and invalidation logic for every way the pre-boundary set can change (message edit, a later compaction moving the boundary forward) |
| Correctness under edits | Fragile — requires catching every edit path to a pre-boundary message and re-triggering summarization, or the stored summary silently drifts from the messages it's supposed to represent |
| Cost of repeated compaction | Depends on invalidation correctness; a missed invalidation path risks compounding staleness |
| Read cost | Lower per read (no recomputation) — but only if invalidation is airtight |

**Pros:** Cheaper on the read path once cached. **Cons:** Every edit path to a pre-boundary
message becomes a place a bug can leave the summary stale, and `CompactionEngine` is deterministic
and pure specifically so there's nothing expensive enough here to justify that risk — the
"correctness under edits" column is where this option loses.

## Trade-off Analysis

`CompactionEngine.Compact` is deterministic and does no I/O (see
[ADR-0002](compaction-adr-algorithmic-extraction.md)), so the "recompute every time" cost is a
handful of regex passes over already-loaded messages, not a network call or a database write. That
made Option A's correctness guarantee essentially free to buy. The spec's Testing item 2
("identical input yields byte-identical output") is the property this decision rests on and is
verified both in W3's unit tests and again, on real transcripts, in the W9 benchmark
([`docs/compaction-benchmark-findings.md`](compaction-benchmark-findings.md)).

## Consequences

- **Easier:** No cache-invalidation code exists anywhere for this feature, because there is no
  cache. Editing a message behind the boundary requires zero special-case handling.
- **Harder:** Nothing measurable — the recomputation is cheap enough that this trade never showed
  up as a performance concern during implementation or in the W9 CI mirror's test timings.
- **To revisit:** If a future conversation shape makes pre-boundary sets large enough that
  recomputation on every history build becomes measurably expensive, revisit whether a
  short-lived, aggressively-invalidated cache is worth the complexity Option B would add — but
  nothing in this workstream's evidence suggests that threshold has been reached.

## Action Items

1. [x] `CompactionBoundaryTurnIndex` column, no summary-text column — shipped W4.
2. [x] `BuildCompactionSummaryMessageAsync` always recomputes from source messages — shipped W5.
3. [x] Determinism verified on synthetic input (W3) and real transcripts (W9,
   [`docs/compaction-benchmark-findings.md`](compaction-benchmark-findings.md)).
