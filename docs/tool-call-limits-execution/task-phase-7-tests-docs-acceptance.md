# Task — Phase 7: Tests, docs, acceptance

> Subagent brief. Execute top to bottom. Return the Report-back contract verbatim.

## Mission

Close out the tool call limits feature: complete proposal §13 test matrix, verify proposal §16
success criteria, document operator-facing behavior, verify stream-reconnect coordination (T13),
and capture evidence in `acceptance-evidence.md`.

## Read first

- `../tool-call-limits-proposal.md` §13, §14, §16.
- `../conversation-stream-reconnect-and-cancel-proposal.md` §14.
- `./00-orchestration.md` §4.2 Phase 7, §6 final acceptance.
- All gate files.
- `./acceptance-evidence.md` (populate).

## Preconditions

- Phases 1–5 gates green.
- Phase 6 DONE per `STATUS.md`.

## Guardrails (hard)

- No new feature scope — tests, docs, and evidence only unless a gate failure requires a
  minimal fix in owning files.
- Stream reconnect test: limit-completed turn must show persisted content on GET reload (no
  empty assistant gap).

## Tasks

1. **Automated tests** covering proposal §13 server scenarios:
   - 12th executes; 13th synthetic.
   - Retry after soft block → Tier 2/3, `completed`.
   - llama.cpp wire serialization test (mock validator).
   - Nested `Agent.Invoke` budgets.
   - Evaluator inherit.
   - Export/import (if not covered in Phase 5).
2. **Manual matrix** (document in `acceptance-evidence.md`):
   - Search limit 5 + research question → limit visible, partial answer, turn completes.
   - Creative Guide long run → lock releases on completion.
   - Builder export/import preserves limits.
3. **Docs:**
   - Add execution pointer to `../tool-call-limits-proposal.md` (if missing).
   - Operator note in README or guides doc if project pattern exists (minimal).
4. **T13 verification:** after limit-completed turn, `ConversationQueryService` GET returns
   final assistant message (and stream reconnect proposal empty-cell scenario does not occur).
5. **Run CodeQL gate** (`codeql-gate.md`) — baseline-vs-current diff; only CodeQL run of the
   plan.
6. **Update `STATUS.md`:** final acceptance checklist; all phases DONE.
7. **Populate `acceptance-evidence.md`** with commands, outputs, manual steps, CodeQL result.

## Files in scope

- `src/server/**Tests**` (new/extended tests)
- `docs/tool-call-limits-execution/acceptance-evidence.md`
- `docs/tool-call-limits-execution/STATUS.md`
- `docs/tool-call-limits-proposal.md` (link to execution folder only)

## Definition of Done

- [ ] All gates green on final tree (including CodeQL at close-out).
- [ ] Proposal §16 criteria checked in STATUS.md.
- [ ] `acceptance-evidence.md` complete.
- [ ] T13 stream-reconnect check documented PASS/FAIL.

## Report-back contract

1. Test inventory (automated + manual).
2. §16 success criteria table (met / not met).
3. T13 verification outcome.
4. Gate summary (all PASS).
5. Recommended follow-ups (if any).
