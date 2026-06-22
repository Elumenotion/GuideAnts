# Task - Phase 6: Tests, docs, and final acceptance

> Subagent brief. Execute top to bottom. Return the Report-back contract verbatim.

## Mission

Close out Tool Sources implementation with full test coverage, documentation updates,
acceptance-criteria mapping, and final parity/security gate passes.

## Read first

- `../tool-sources-guide-builder-proposal.md` sections 15, 16, and 18.
- `./00-orchestration.md` section 6 final acceptance.
- `./STATUS.md` ledger sections.
- `./ui-gate.md`, `./runtime-parity-gate.md`, and `./codeql-gate.md`.
- Existing docs under `docs/` related to guide builder/editor/tool calling.

## Preconditions

- Phase 4 gate green.
- If Phase 5 was approved, Phase 5 gate green; otherwise Phase 5 marked `SKIPPED`.

## Guardrails (hard)

- Acceptance criteria must map to concrete code/tests/docs evidence.
- Do not claim parity from frontend-only checks; backend/runtime checks are required.
- No silent downgrade of MCP behavior vs locked decisions.
- No fallback assertions that mask broken descriptors as valid.

## Tasks

1. Complete test matrix coverage for proposal section 16:
   - guided creation for client and sandbox
   - existing descriptor compatibility
   - structured operation editing
   - preview exactness
   - MCP discovery/selection if in scope
   - backend validation rejection paths.
2. Update docs under `docs/` for:
   - Tool Sources UX and source types
   - component-level UI behavior (picker, cards, operation editor, custom mode)
   - advanced JSON and custom descriptor mode behavior
   - preview semantics and runtime parity expectations
   - MCP scope and publish restrictions for first release
   - optional storage cleanup notes if Phase 5 executed.
3. Run full runtime parity gate and final CodeQL diff gate.
4. Run full UI gate checks (desktop/mobile/accessibility) and record results.
5. Update `STATUS.md` with final phase states, UI/parity/codeql ledgers, deviations,
   and final acceptance checklist.
6. Produce a concise evidence table mapping each proposal acceptance criterion to
   file/tests.

## Files in scope

- Tests in `src/client/src/**` and `src/server/GuideAntsApi.Tests/**` relevant to tool
  sources and preview.
- Docs under `docs/` (including this execution folder ledger updates).
- `docs/tool-sources-execution/STATUS.md`.

Out of scope:

- New feature behavior not required for acceptance criteria.

## Self-verification

```bash
cd src/server && dotnet build GuideAntsApi.sln && dotnet test GuideAntsApi.sln
cd src/client && npm run build && npm test -- --run
```

Run full:

- `ui-gate.md` section 3 checks
- `runtime-parity-gate.md` section 3
- `codeql-gate.md` full diff

## Definition of Done

- [ ] Proposal section 16 criteria all mapped to passing tests/docs/code evidence.
- [ ] UI gate final pass (desktop + mobile + accessibility).
- [ ] Runtime parity final pass.
- [ ] CodeQL final diff clean.
- [ ] Tool Sources docs updated for user/admin/developer workflows.
- [ ] `STATUS.md` final acceptance checklist completed with no open deviations.

## Report-back contract (return exactly this)

```text
PHASE 6 REPORT
- Acceptance criteria mapping completed: <yes/no + path to evidence table>
- Test matrix additions/updates: <paths + summary>
- Docs updated: <paths>
- UI GATE final: <pass/fail>
- RUNTIME PARITY GATE final: <pass/fail>
- CODEQL final diff: <count -> ids/files or none>
- STATUS.md final sections updated: <yes/no>
- Verification: server-build=<pass/fail> server-tests=<counts> client-build=<pass/fail> client-tests=<counts>
- Files touched: <exhaustive list>
- Deviations / surprises: <list or "none">
```
