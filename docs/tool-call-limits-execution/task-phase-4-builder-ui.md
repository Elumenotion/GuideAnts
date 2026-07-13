# Task — Phase 4: Builder UI (Tools + Crew tabs)

> Subagent brief. Execute top to bottom. Return the Report-back contract verbatim.

## Mission

Expose per-assistant tool execution limits in the Guide Builder: **Tools tab** → "Execution
limits" section; **Crew tab** → read-only per-member summary with link to edit member
assistant. Distinct copy from published conversation `MaxTurns` on `LimitsTab`.

## Read first

- `../tool-call-limits-proposal.md` §10 (Builder UI).
- `./DECISIONS.md` — T2, T15.
- `./ui-gate.md` — **full contract** (§2 UX, §3 reuse table, §4 decomposition, §6 tests).
- [`../skills-execution/ui-gate.md`](../skills-execution/ui-gate.md) §3–§4 — inherited reuse +
  anti-monolith rules (tool-call-limits gate cites these).
- [`src/.cursor/rules/project-rules.mdc`](../../src/.cursor/rules/project-rules.mdc) — frontend
  standards.
- `src/client/src/components/guides/editor/ToolsTab.tsx`
- `src/client/src/components/guides/editor/ToolsSelector.tsx` (where `SandboxWireApiPanel` sits)
- `src/client/src/components/guides/editor/SandboxWireApiPanel.tsx` (section divider + number input)
- `src/client/src/components/guides/editor/CrewTab.tsx`
- `src/client/src/components/guides/guideEditor/GuideCrewManager.tsx`
- `src/client/src/components/guides/editor/BaseEntityEditor.tsx` (`FormData`, save/load)
- `src/client/src/components/guides/configTabs/LimitsTab.tsx` (**read only** — publish dialog;
  do not modify; copy number-input **pattern** only)
- `src/client/src/components/guides/editor/skills/SkillsTab.tsx` (`useNavigate` precedent)

## Preconditions

- Phase 1 gate green (DTOs + client types exist).
- Phase 2 green preferred so limits can be manually smoke-tested end-to-end (not blocking if
  server enforcement lags).

## Guardrails (hard)

Everything in **`ui-gate.md` §2–§4** is mandatory. In particular:

- **Tools tab → Global Tools** only; section below `ToolsSelector` with `SandboxWireApiPanel`
  divider pattern.
- **Never** modify `configTabs/LimitsTab.tsx` or `PublishGuideDialog` limit fields.
- **Blank = unlimited** (T2). Do not default to `0`.
- **Decompose** per `ui-gate.md` §4 — `toolLimits/*` helpers + presentational components.
- **Crew tab:** read-only member limit summary + edit link in Phase 4 (member override edit is
  Phase 6).

## Tasks

1. **`toolLimits/` module** per `ui-gate.md` §4:
   - `ToolExecutionLimitsSection`, `LimitNumberField`, `toolLimitForm.ts` (+ tests),
     `toolLimitDisplay.ts` (+ tests).
2. **Wire `BaseEntityEditor` `FormData`** for `maxToolCallsPerTurn`, `maxToolRoundsPerTurn`;
   load/save with guide/assistant APIs.
3. **Mount** `ToolExecutionLimitsSection` in `ToolsTab` Global Tools sub-tab below
   `ToolsSelector`.
4. **`crew/CrewMemberLimitsRow.tsx`** — extend crew selected-member UI (via `GuideCrewManager`
   or thin `CrewTab` wrapper) with read-only limit + navigate to member assistant Tools tab.
5. **Client tests** per `ui-gate.md` §6.
6. **Manual verification** documented for report-back.

## Files in scope

- `src/client/src/components/guides/editor/ToolsTab.tsx`
- `src/client/src/components/guides/editor/ToolsSelector.tsx` (only if needed for placement)
- `src/client/src/components/guides/editor/CrewTab.tsx`
- `src/client/src/components/guides/guideEditor/GuideCrewManager.tsx` (crew row extension)
- `src/client/src/components/guides/editor/BaseEntityEditor.tsx`
- `src/client/src/components/guides/editor/toolLimits/**` *(new)*
- `src/client/src/components/guides/editor/crew/CrewMemberLimitsRow.tsx` *(new)*
- `src/client/src/types/guides.ts` (if not done in Phase 1)
- `src/client/**/__tests__/**` for new helpers/components

## Files out of scope

- `configTabs/LimitsTab.tsx`, `PublishGuideDialog.tsx` (publish limits).
- `ThreadRun.cs`, server DTOs (unless client types missing).
- Bootstrap manifests (Phase 5).
- `CrewMemberLimitOverrideField.tsx` (Phase 6).

## Self-verification

```bash
cd src/client && npm run build && npm test -- --run
```

Run full **`ui-gate.md` §5 Phase 4** checklist.

## Definition of Done

- [ ] `ui-gate.md` §5 Phase 4 — all items pass.
- [ ] `ui-gate.md` §7 — no fail modes triggered.

## Report-back contract

Return **`ui-gate.md` §8** block verbatim, filled in, plus:

1. File map with line counts for new `.tsx` files.
2. Example save payload snippet for limit fields.
3. Navigate target used for crew "Edit limits" link.
