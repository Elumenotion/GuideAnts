# Task — Phase 6: Rounds, crew overrides, Tier 4, trace

> Subagent brief. Execute top to bottom. Return the Report-back contract verbatim.

## Mission

Complete proposal Phase C (bootstrap defaults are Phase 5): enforce
`max_tool_rounds_per_turn`, wire `GuideMember.MaxToolCallsPerInvocation` in the nested crew
budget formula and Crew tab, implement **Tier 4** compacted summarization on the force-complete
path, expose limit-hit metadata in turn trace, and enable the advanced tool-rounds field in
the Tools tab.

## Read first

- `../tool-call-limits-proposal.md` §4.1 (all fields), §5 (scoped budgets), §8 Tier 4,
  §10 (Crew tab), §12 Phase C.
- `./DECISIONS.md` — T10, T11, T12.
- `./ui-gate.md` — Phase 6 §5 checks (member override + rounds enforcement).
- `task-phase-4-builder-ui.md` — rounds field and crew read-only summary shipped there;
  this phase makes them fully enforced and editable.

## Preconditions

- Phases 2–5 gates green.
- Phases 3 escalation (Tier 1–3) working.

## Guardrails (hard)

- **`null` on a limit field = unlimited** (T2). That is configuration semantics, not a reason
  to skip implementation of the field or enforcement path.
- **Tier 4 (T12):** tool-free history for one summarization call; no `tool` roles; no
  `tool_calls` in compacted history. Wire into the escalation ladder when force-complete would
  fire — prefer model-written summary per proposal §8 Tier 4; Tier 3 stub remains fallback if
  summarization fails.
- **Crew override formula** exactly per proposal §5: `min(remaining parent, member override ??
  child assistant limit)` (T11).
- **Tool rounds** count LLM rounds with `finish_reason: tool_calls`, not individual tool
  executions (T10).
- Do not weaken Tier 1–3 behavior.

## Tasks

1. **`max_tool_rounds_per_turn`:** separate counter; enforce in `ThreadRun` loop (increment
   when a tool round completes). Surface in Tools tab advanced section (editable, not
   placeholder).
2. **Crew tab + DTO:** `crew/CrewMemberLimitOverrideField.tsx` per `ui-gate.md` §4; editable
   `max_tool_calls_per_invocation` per guide member.
3. **Nested budget:** apply T11 formula on every `Agent.Invoke` child run.
4. **Turn trace:** expose limit-hit count and `LimitEscalationPhase` reached in trace
   collector / prompt trace.
5. **Tier 4 (T12):** `BuildCompactedHistoryForLimitSummary` — precedent from `Conversation`
   handoff filtering; one LLM call without `tools`. Integrate on force-complete escalation
   path.
6. **Tests:** rounds limit hit; crew override caps child below child assistant limit;
   nested budget formula; trace metadata; Tier 4 compaction produces valid provider-safe
   request and `completed` turn.

## Files in scope

- `ThreadRun.cs`, `InvocationContext` / `ToolLimitState`
- `CrewTab.tsx`, `GuideCrewManager.tsx`, `crew/CrewMemberLimitOverrideField.tsx`
- `ToolsTab.tsx`, `toolLimits/*` (advanced rounds — already wired Phase 4)
- Crew member DTOs, `GuidesService`, `BaseEntityEditor.tsx`
- Trace collector types
- Tests

## Files out of scope

- Bootstrap defaults (Phase 5).
- Tier 1–3 escalation logic changes unless required for rounds counting or Tier 4 handoff.

## Self-verification

```bash
cd src/server && dotnet build GuideAntsApi.sln && dotnet test GuideAntsApi.sln
cd src/client && npm run build && npm test -- --run
```

Runtime-parity gate §3 and provider-safe-completion gate §4 must pass.
Run **`ui-gate.md` §5 Phase 6** checklist.

## Definition of Done

- [ ] `max_tool_rounds_per_turn` enforced and editable in UI.
- [ ] `max_tool_calls_per_invocation` editable on Crew tab and enforced at runtime.
- [ ] Tier 4 compaction implemented and tested on force-complete path.
- [ ] Turn trace shows limit-hit metadata.
- [ ] **ui-gate** §5 Phase 6 passes.
- [ ] **runtime-parity gate** §3 and **provider-safe-completion gate** §4 pass.

## Report-back contract

1. Rounds counting insertion point in `ThreadRun`.
2. Nested budget formula implementation (code path for `Agent.Invoke`).
3. Crew tab / Tools tab changes.
4. Tier 4 integration point in escalation ladder (vs Tier 3 stub fallback).
5. Test names + results.
6. Files touched.
