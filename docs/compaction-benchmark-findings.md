# Compaction benchmark — findings (W9 / spec Testing item 5)

Date: 2026-09-24. Engine at commit dec3c8d. Corpus: `guideants` dev database. Corpus not widened (declined) — the sample below is exactly what already existed on this database, with the coverage gaps that implies.

## Corpus

| Conversation | Domain | Completed turns | Tools | Pre-boundary chars → summary chars |
|---|---|---|---|---|
| 716804F2 | non-coding (synthetic) | 14 | (none) | 1584 → 413 |
| 7EEAA723 | non-coding | 11 | external_tool | 7891 → 822 |
| FAC2F4F7 | coding | 8 | Code_Executor, Search, skills_read | 131559 → 2683 |
| D486EE32 | non-coding | 6 | SearchAssistantFiles, external_tool | 11413 → 505 |
| DD0DDA4B | coding | 6 | Code_Executor, run_bash, run_python, skills_read | 249067 → 5951 |
| 504058DC | coding | 5 | Code_Executor, run_python, search_notebook, search_project, skills_read | 115983 → 11758 |

**716804F2 is not a genuine user conversation.** Its Goal line reads "Hello, this is a preflight timing test. Please respond briefly." — a synthetic conversation, almost certainly left behind by an automated test run against this shared dev database rather than a real notebook session. It's included in the table for completeness (it's what the query returned) but excluded from the domain judgment below.

Coverage gaps: no notebook producing documents, images, or audio is represented. The two genuine non-coding conversations are both voice/chat concierge-style transactional or Q&A assistants using client-side `external_tool`/`SearchAssistantFiles` calls, not GuideAnts' own document/image/audio-generation tools. This narrows what the non-coding verdict below can claim — it speaks to "domain without a coding sandbox," not to every non-coding domain GuideAnts supports.

## Per-conversation verdicts

| Conversation | R1 Goal | R2 Artifacts | R3 Ledger | R4 Errors | R5 Directives | R6 Overall | Evidence (paraphrased) |
|---|---|---|---|---|---|---|---|
| FAC2F4F7 | yes | yes | partial | clean | n/a | partial | Goal states the SSH intent accurately. Artifacts correctly lists the 5 files actually created/modified for the SSH tooling. 20 of 24 ledger lines read "unknown" because every one is a `Code_Executor` sub-agent call, which returns free text rather than a structured exit code the engine can read — spot-checked one such result directly against the database and confirmed it was an ordinary successful file dump, not a failure the engine silently missed. No unresolved-error claim contradicted the raw messages. |
| DD0DDA4B | yes | yes | partial | clean | n/a | useful | Goal and the 12 listed artifacts (screenshots, a session doc, a PDF, a verification script) accurately summarize a real "build a frontend" session. 49 of 65 ledger lines are unknown for the same `Code_Executor`-opacity reason. Checked the raw messages for a repeating `run_python` failure inside the pre-boundary window and confirmed it was later followed by a successful call to the same tool before the boundary — the engine's "no later success" rule correctly left it out of Unresolved errors rather than missing a live failure. |
| 504058DC | yes | partial | partial | clean | n/a | useful | Goal is accurate. Artifacts is technically correct but almost entirely ~200 auto-generated Python dependency files from a package install, burying the handful of genuinely meaningful outputs (a couple of JSON result files) a reader would actually want. The ledger does carry real `ok`/`error` signal for several direct `run_python` calls — checked the one `error` line against the database and confirmed a later `run_python` call succeeded before the boundary, so the "(none)" in Unresolved errors is correct, not a miss. |
| 7EEAA723 | yes | n/a | no | clean | n/a | partial | Goal ("rent the bike tomorrow at noon") is accurate. No artifacts is correct for this domain. All 8 ledger lines read "unknown," including `createReservation` and `sendPaymentLink` — the two calls whose outcome matters most. Read the actual tool results directly from the database: every one was a genuine success (availability confirmed, a real reservation record, a real payment link) — the engine isn't wrong, it simply has no way to read success out of this tool's ad-hoc JSON shape the way it reads `ExitCode` from a script result, so the single most consequential fact in the conversation is invisible in the summary and recoverable only through recall. |
| D486EE32 | yes | n/a | no | clean | n/a | partial | Goal ("what's the name of this place") is accurate. No artifacts is correct. All 3 ledger lines (two knowledge-base searches, one catalog lookup) read "unknown" for the same reason as above — a Q&A/knowledge-lookup tool protocol the engine has no structured signal for. |
| 716804F2 | yes | n/a | n/a | clean | n/a | useful | Excluded from the domain judgment (synthetic test fixture, not a real session) but included here for completeness: a 3-line Goal-only summary of a trivial timing ping, with nothing to lose in compaction. |

## By domain

- **Coding:** 3 conversations, all `partial` or `useful`, none `misleading`. Goal and Artifacts are consistently accurate. The recurring gap is the Activity ledger reading "unknown" for the crew's `Code_Executor` sub-agent calls (which return free text, not a structured exit code) — spot-checked against the raw database content in three places and never found a case where "unknown" was hiding a real failure or a real success the engine got wrong; it's an honest gap, not a wrong claim. One conversation (504058DC) also showed Artifacts diluted by ~200 auto-generated dependency files, a signal-to-noise problem rather than a correctness problem.
- **Non-coding:** 2 genuine conversations (excluding the synthetic one), both `partial`, none `misleading`. Both are transactional/Q&A concierge assistants using client-side tools whose JSON result shape the engine has no way to read structured success from — Goal is accurate in both, and reading the raw tool results directly confirmed the engine never claimed a false outcome; it simply has no outcome signal to report for this tool shape, including for the one case (the bike reservation) where the outcome is the most important fact in the whole conversation.

## Verdict on D2's accepted risk

**(b) Partially materialized.** The part of D2's risk that's specifically about domain fit — whether the fixed, protocol-derived section vocabulary (Goal / Artifacts / Activity ledger / Unresolved errors / Directives) makes sense outside coding — did **not** materialize: Goal correctly captured user intent in every conversation regardless of domain, and Artifacts correctly rendered "(none)" for domains that produce no files rather than forcing a coding-shaped concept where it doesn't apply. No section in either genuine non-coding conversation said anything false.

What did materialize, and what pulls this from "not materialized" to "partially materialized," is a narrower, cross-domain limitation that happens to bite hardest outside coding: the Activity ledger and Unresolved-errors sections can only read success/failure from `ScriptExecutionResult.ExitCode` — the one protocol-guaranteed signal the design already named as its accepted risk (see the engine's own `ScriptExecutionResult`-only comment). Any tool that reports its outcome a different way — the crew's `Code_Executor` sub-agent in coding, and the client-side `external_tool`/`SearchAssistantFiles` calls that are typically *all* a non-coding transcript has — comes back "unknown" rather than "ok" or "error." That's honest (the engine never claimed a wrong outcome in six checked instances), but for the bike-reservation conversation it means the single most consequential fact in the transcript — did the reservation and payment actually go through — is invisible in the summary. Recall is the design's own named mitigation for exactly this: a model that needs to know the outcome can still ask.

## Carried to W10

- The ADR for D2 should cite this finding by name: the domain-fit risk did not materialize for the Goal/Artifacts/Directives vocabulary itself, but a separate, already-documented limitation (outcome signal exists only for `ScriptExecutionResult.ExitCode`) affects ledger usefulness in both domains and disproportionately in non-coding transcripts where nearly every tool call is client-side. Recall is the accepted mitigation, not a fallback to fix the extractor.
- Follow-up worth scoping later, not done here: teach the Activity ledger extractor to read a generic `{"error": ...}` / `{"status": "failed"}` shape from tool results that aren't `ScriptExecutionResult`, so `external_tool` and `Code_Executor` calls can report something better than "unknown" when the underlying JSON already carries a clear success/failure signal.
- Follow-up worth scoping later: when a conversation's Artifacts list is dominated by auto-generated dependency files (as in 504058DC), consider filtering well-known vendor/package-install paths so the section highlights user-meaningful outputs.
- Coverage gap to note, not fixed here: this run had no notebook producing documents, images, or audio, and the two real non-coding samples were both external-tool-driven concierge/booking assistants — the same narrow shape. A future benchmark run should widen the corpus once such notebooks exist, rather than treat this run as the last word on non-coding domains generally.
- The synthetic `716804F2` conversation (a leftover integration-test preflight ping) suggests this shared dev database accumulates test-fixture noise; a future benchmark run should filter or flag such conversations rather than rely on a human noticing, as happened here.

---

Reviewed by the project owner on 2026-09-24.
