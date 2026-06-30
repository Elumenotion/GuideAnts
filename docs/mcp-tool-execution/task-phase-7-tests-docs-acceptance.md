# Task — Phase 7: Tests, docs, final acceptance

> Subagent brief. Execute top to bottom. Return the Report-back contract verbatim.

## Mission

Close out the MCP tool execution work: prove design §9 phase exits with tests, run the full
gate set, write the authoring/migration/limits docs, and complete the `STATUS.md` final
acceptance checklist. This phase ships no new product behavior — it makes the prior phases
**verifiable and documented**.

## Read first

- `../mcp-tool-execution-design.md` §9 (phase exits), §10 (non-goals), §11.
- `./00-orchestration.md` §6 (final acceptance).
- `./DECISIONS.md` (every locked decision is the acceptance spec).
- All gates: `runtime-parity-gate.md`, `wire-streaming-gate.md`, `sandbox-apply-gate.md`,
  `ui-gate.md`, plus `codeql-gate.md`.
- `../tool-sources-authoring.md` (extend, don't fork, the authoring docs).

## Preconditions

- Phases 1–6 gates green in `STATUS.md`.

## Guardrails (hard)

- Acceptance is **evidence-based**: every design decision maps to a passing test or a
  file/commit reference, not prose. No "should work" claims.
- Do not change runtime behavior to make a test pass — if a gap is found, classify it as a
  deviation and route it to the phase that owns it (orchestration §5). Never patch a
  phase-owned defect here.
- Docs must state the **non-goals** (design §10): no `client://`/client-host MCP, no
  browser-direct MCP, no hostname inference, no `tool_calls` on the wire in v1, no
  per-connection rate limits / egress proxy.
- No new silent `catch {}` in any test helper or doc sample.

## Tasks

1. **Acceptance mapping:** produce `acceptance-evidence.md` in this folder mapping each
   locked decision (D1/D2 revised + E1–E17) and each design §9 phase exit to a test
   path / file / commit:
   - Phase A exit: notebook return-policy tool call completes with assistant text.
   - Phase C exit: registry PyPI/npm stdio server completes a tool call via notebook **or**
     a published surface.
2. **Run the full gate set** on the final tree and record results in `STATUS.md`:
   runtime-parity (§3.1–3.5), wire-streaming (§3.1–3.4 all providers, incl. real MCP
   opacity), sandbox-apply (§3.1–3.6), ui-gate (§4 checks + §5 matrix), CodeQL final diff.
3. **Cross-cutting tests:** fill any coverage gaps the gates expose — end-to-end
   `mcp+api://` and `mcp+sandbox://` turns on notebook, embed, and wire (proving E3/E15 one
   executor), and the migration round-trip from `client://mcp-bridge-*`.
4. **Docs:**
   - Extend `../tool-sources-authoring.md` with MCP runtime-execution authoring
     (`api` vs `sandbox_subprocess`, `toolNamePrefix`, headers/env, package).
   - Migration note: `client://` MCP removed; how existing descriptors were rewritten (E4).
   - Wire behavior: live `stream: true`, opaque MCP (E14), `stream: false` fold.
   - Sandbox limits: scope, per-call spawn (E7), Node in image (E10), E16 publish gate,
     no host-local MCP.
5. **Update `STATUS.md`** final acceptance checklist; confirm no open deviations.

## Files in scope

- `docs/mcp-tool-execution/acceptance-evidence.md` (new)
- `docs/mcp-tool-execution/STATUS.md` (final update)
- `docs/tool-sources-authoring.md` (MCP runtime-execution section)
- Cross-cutting test files only where a gate exposed a gap (note them explicitly).

Out of scope:

- New runtime/UI behavior (owned by Phases 1–6; route gaps back via deviation protocol).

## Self-verification

```bash
cd src/server && dotnet build GuideAntsApi.sln && dotnet test GuideAntsApi.sln
cd src/client && npm run build && npm test -- --run
```

Run **all** gates:

- `runtime-parity-gate.md` §3.1–§3.5 full pass.
- `wire-streaming-gate.md` §3.1–§3.4 full pass (incl. §3.3 with real MCP).
- `sandbox-apply-gate.md` §3.1–§3.6 full pass.
- `ui-gate.md` §4 checks + §5 test matrix full pass.
- `codeql-gate.md` final diff gate.

## Definition of Done

- [ ] `acceptance-evidence.md` maps every locked decision + §9 phase exit to test/file/commit.
- [ ] All four gates final-pass on the final tree.
- [ ] E2E coverage: `mcp+api://` + `mcp+sandbox://` on notebook, embed, wire (one executor).
- [ ] Migration round-trip from `client://mcp-bridge-*` covered.
- [ ] Docs updated (authoring, migration, wire behavior, sandbox limits, non-goals).
- [ ] `STATUS.md` final acceptance checklist complete; no open deviations.

## Report-back contract (return exactly this)

```text
PHASE 7 REPORT
- acceptance-evidence.md created (decisions + phase exits → evidence): <path>
- Phase A exit proven (notebook return-policy → assistant text): <test ref>
- Phase C exit proven (registry stdio server tool call): <test ref + surface>
- E2E one-executor coverage (api + sandbox on notebook/embed/wire): <test refs>
- Migration round-trip covered (client://mcp-bridge-* → mcp+api/mcp+sandbox): <test ref>
- RUNTIME PARITY GATE (final): <pass/fail per check>
- WIRE STREAMING GATE (final): <pass/fail per provider + MCP opacity>
- SANDBOX APPLY GATE (final): <pass/fail per check>
- UI GATE (final): <pass/fail per §4 check>
- CODEQL (final): new-vs-baseline=<count → ids/files or none>
- Docs updated: <paths>
- Verification: server-build=<p/f> server-tests=<counts> client-build=<p/f> client-tests=<counts>
- STATUS.md final acceptance complete / open deviations: <yes-no / list>
- Files touched: <exhaustive list>
- Deviations / surprises: <list or "none">
```
