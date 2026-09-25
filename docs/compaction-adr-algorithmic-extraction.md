# ADR-0002: Algorithmic extraction, no LLM summarization tier

**Status:** Accepted (shipped)
**Date:** 2026-09-24
**Deciders:** GuideAnts context-compaction workstream (design spec [`docs/superpowers/specs/2026-09-21-context-compaction-design.md`](superpowers/specs/2026-09-21-context-compaction-design.md), decision D2)

## Context

Compacting a conversation means turning everything before the boundary into something shorter
that still orients the model. The two broad approaches are: call an LLM to write a natural-language
summary, or extract a fixed set of facts algorithmically (no model call). This decision was made
before any implementation existed, so the trade-off had to be judged on the shape of the problem
GuideAnts actually has, not on a benchmark.

## Decision

`CompactionEngine` (`src/server/AntRunner.Chat/AntRunner.Chat/Compaction/CompactionEngine.cs`) is a
pure, deterministic, static library — no I/O, no DB, no vendor knowledge, no LLM call of any kind.
It extracts five fixed sections (Goal, Artifacts, Activity ledger, Unresolved errors, Directives —
see [ADR](superpowers/specs/2026-09-21-context-compaction-design.md) decision D6 for why this
vocabulary) from the pre-boundary messages using regexes and structural rules over GuideAnts' own
tool/file protocol, and renders them into one summary message.

## Options Considered

### Option A: Algorithmic extraction, no LLM tier (chosen)

| Dimension | Assessment |
|-----------|------------|
| Complexity | Medium — five extractors plus a renderer, all pure functions, straightforward to unit test |
| Cost | Zero incremental cost per compaction — no model call |
| Determinism | Total — identical input always yields byte-identical output (verified by W3's own test suite and the W9 benchmark, spec Testing item 2) |
| Availability | Works offline, with any model configured, with no dependency on a summarization-capable model being available |
| Quality | Domain-fit risk: a fixed vocabulary may not capture what matters in every domain equally well |

**Pros:** No per-compaction cost, so compaction never becomes a lever anyone has to think twice
about pressing. No quality variance between runs, providers, or model choices. No new failure mode
(a summarization call timing out, hallucinating, or being unavailable when a user needs to compact
right now to keep working). Repeated compaction never compounds loss the way LLM re-summarization
of a previous summary can, because D3's design always recomputes from the original messages.
**Cons:** Extraction quality is bounded by what the fixed vocabulary and regex-based extractors can
see. A tool whose success/failure the extractors can't read (anything other than
`ScriptExecutionResult.ExitCode`) shows up as "unknown" rather than "ok"/"error" — a real,
documented limitation, not a false claim (see the W9 cross-domain benchmark,
[`docs/compaction-benchmark-findings.md`](compaction-benchmark-findings.md)).

### Option B: LLM-based summarization

| Dimension | Assessment |
|-----------|------------|
| Complexity | Higher — needs a summarization prompt, a model/provider choice, retry/fallback handling, and a way to keep summarizing the summarizer itself deterministic-enough for D3 |
| Cost | A real per-compaction cost, proportional to conversation length, on every press |
| Determinism | None, or approximate at best — the same input can produce different summaries across calls, undermining D3's "recompute the content" guarantee |
| Availability | Depends on a summarization-capable model being configured and responsive |
| Quality | Potentially higher fidelity on content an algorithmic extractor can't parse, at the cost of hallucination risk |

**Pros:** Could produce a more natural, more complete summary, especially for domains the fixed
vocabulary fits poorly. **Cons:** Introduces cost and non-determinism into a feature designed to be
free to press repeatedly. Directly undermines D3, which depends on summary text being
byte-identical for identical input — an LLM call is not that, run to run. Also introduces a new
failure mode (the summarization call itself can fail) into a feature whose whole purpose is to keep
a user unblocked.

### Option C: Hybrid — algorithmic extraction with an LLM fallback tier

| Dimension | Assessment |
|-----------|------------|
| Complexity | Highest — all of Option A's work, plus Option B's, plus the logic to decide when to fall back |
| Cost | Occasional, unpredictable |
| Determinism | Mixed — deterministic in the common case, not in the fallback case |
| Availability | Same dependency risk as Option B, just less often |
| Quality | Best case of both, in principle |

**Pros:** Could close Option A's domain-fit gap only where it actually bites, without paying
Option B's cost on every press. **Cons:** The complexity of deciding *when* to fall back is itself
a new source of bugs and unpredictability, for a benefit that the W9 benchmark found was real but
narrower than expected — see Consequences below.

## Trade-off Analysis

The domain-fit risk Option A accepts was the single largest open question going into
implementation, explicitly named in the spec's *Risks accepted* table with "no fallback" as part
of the accepted cost. It was checked, not assumed: the W9 cross-domain benchmark ran the shipped
engine against real coding and non-coding conversations and read the output against a fixed
rubric. The finding (see [`docs/compaction-benchmark-findings.md`](compaction-benchmark-findings.md))
was that the domain-neutral vocabulary itself (Goal/Artifacts/Directives) held up with no
domain-specific misfit found in either domain; the "unknown" outcome gap that materialized is a
narrower, cross-domain protocol-signal limitation (only `ScriptExecutionResult.ExitCode` is
readable), not evidence that an LLM tier is needed to make the vocabulary itself work outside
coding. Recall (D7) is the accepted mitigation for that narrower gap, not a summarization fallback.

## Consequences

- **Easier:** Testing (pure functions, no network calls to mock), reasoning about cost (compaction
  is free, full stop), and reasoning about determinism (D3 holds by construction, not by hoping an
  LLM call happens to reproduce itself).
- **Harder:** Extraction quality has a hard ceiling — sections not covered by the fixed vocabulary
  or not readable from the tool-call protocol are invisible in the summary until a user reaches for
  Recall (D7).
- **To revisit:** The W9 benchmark's own follow-up notes an engine-level improvement worth scoping
  separately — teaching the Activity ledger extractor to read a generic `{"error": ...}` /
  `{"status": "failed"}` shape from tool results that aren't `ScriptExecutionResult`, so more tool
  types can report something better than "unknown". This is a mechanical extension to Option A, not
  a reason to revisit Option B or C.

## Action Items

1. [x] `CompactionEngine` and its five extractors — shipped W3.
2. [x] Cross-domain benchmark validating the domain-fit risk — shipped W9,
   [`docs/compaction-benchmark-findings.md`](compaction-benchmark-findings.md).
3. [ ] Follow-up (not in this workstream): generic error-shape detection for non-`ScriptExecutionResult`
   tool outcomes, to narrow the "unknown" ledger gap the benchmark found.
