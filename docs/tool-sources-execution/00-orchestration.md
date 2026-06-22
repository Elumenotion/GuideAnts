# Tool Sources Guide Builder - Execution and Orchestration Guide

Last updated: 2026-06-19

This is the conductor document for executing
[`../tool-sources-guide-builder-proposal.md`](../tool-sources-guide-builder-proposal.md).
It is written for the top-level orchestrating agent. It defines how work is split
into subagent task briefs, the dependency order, the verification gates after each
phase, and the deviation protocol that keeps implementation aligned with the
proposal.

> Audience split
>
> - You (orchestrator) read this file plus `DECISIONS.md`, `STATUS.md`,
>   `runtime-parity-gate.md`, and `codeql-gate.md`.
> - Subagents read only their own `task-phase-N-*.md`, the cited proposal
>   sections, and `DECISIONS.md`.

---

## 0. How to use this folder

| File | Owner | Purpose |
|------|-------|---------|
| `00-orchestration.md` (this) | Orchestrator | Dispatch order, gates, failure protocol. |
| `DECISIONS.md` | Orchestrator (fill before dispatch) | Locks proposal section 17 open questions and cross-cutting invariants. |
| `STATUS.md` | Orchestrator (update after each gate) | Living ledger for phase state, parity gate, CodeQL, and deviations. |
| `ui-gate.md` | Orchestrator + UI-heavy subagents | Concrete UX contract: component structure, flows, states, accessibility, and responsive behavior. |
| `runtime-parity-gate.md` | Orchestrator + UI/backend subagents | Ensures Tool Source editor output matches runtime tool-definition behavior. |
| `codeql-gate.md` | Orchestrator + security-sensitive subagents | Local baseline-vs-current CodeQL gate (no GitHub parity). |
| `task-phase-1-scheme-aware-ui.md` | Subagent | Proposal phase 1 implementation brief. |
| `task-phase-2-guided-creation-existing-schemes.md` | Subagent | Proposal phase 2 implementation brief. |
| `task-phase-3-mcp-discovery-descriptor-generation.md` | Subagent | Proposal phase 3 implementation brief. |
| `task-phase-4-backend-validation-publish-checks.md` | Subagent | Proposal phase 4 implementation brief. |
| `task-phase-5-storage-cleanup-optional.md` | Subagent | Proposal phase 5 optional cleanup brief. |
| `task-phase-6-tests-docs-final-acceptance.md` | Subagent | Cross-cutting close-out, docs, and acceptance brief. |

Every task brief follows the same template:
Mission -> Read first -> Preconditions -> Guardrails -> Tasks -> Files in/out of
scope -> Self-verification -> Definition of Done -> Report-back contract.

---

## 1. Pre-flight (once, before dispatch)

Do not dispatch Phase 1 until all are true:

- [ ] Resolve proposal section 17 open questions in `DECISIONS.md`. Any value still
      `UNDECIDED` that blocks a phase keeps that phase blocked.
- [ ] Capture a clean baseline and record it in `STATUS.md`:
  - `cd src/server && dotnet build GuideAntsApi.sln`
  - `cd src/server && dotnet test GuideAntsApi.sln`
  - `cd src/client && npm run build`
  - `cd src/client && npm test -- --run`
- [ ] Capture runtime-parity baseline from `runtime-parity-gate.md` section 2 using
      current bootstrap descriptors and existing non-web tool sources.
- [ ] Capture CodeQL baseline from `codeql-gate.md` and save SARIFs under
      `.codeql/baseline/`.
- [ ] Confirm clean working tree (`git status`) and active feature branch.
- [ ] Confirm `dotnet ef --version` is available in case Phase 5 migration work is
      approved.

If any blocking decision is unresolved, stop and ask before dispatching the
dependent phase.

---

## 2. Dependency graph (dispatch order)

```text
Phase 1  Scheme-aware Tool Sources UI over existing data
   |
   v
Phase 2  Guided creation for web/client/sandbox + structured operation editor
   |
   v
Phase 3  MCP discovery + descriptor generation (+ runtime strategy decision)
   |
   v
Phase 4  Backend validation + publish checks + preview parity endpoint
   |
   +----------------------------+
   |                            |
   | (optional, if approved)    v
   +----> Phase 5  Storage cleanup compatibility work
   |
   v
Phase 6  Tests, docs, acceptance close-out
```

Rules:

- Phases run in order. No downstream phase starts on a failed gate.
- Phase 5 is optional and requires explicit approval from the user/orchestrator.
- One subagent per phase brief.
- Phase 3 and 4 are security-sensitive due to remote endpoint metadata, scheme
  dispatch, and publishability checks; both require CodeQL gate passes.

---

## 3. Dispatch protocol (per phase)

For each phase:

1. Confirm brief preconditions and decision dependencies. Mark phase
   `IN_PROGRESS` in `STATUS.md`.
2. Dispatch one subagent with:
   "Read and execute docs/tool-sources-execution/task-phase-N-*.md end to end.
   Obey guardrails and Definition of Done. Return the Report-back contract
   verbatim."
3. Receive report-back as a claim, not proof.
4. Run independent gate checks from section 4.
5. Mark PASS -> `DONE`, otherwise follow section 5.

---

## 4. Verification gates

### 4.1 Global invariants (every phase gate)

- [ ] Server build green: `cd src/server && dotnet build GuideAntsApi.sln`.
- [ ] Server tests green: `cd src/server && dotnet test GuideAntsApi.sln`.
- [ ] Client build green: `cd src/client && npm run build`.
- [ ] Client tests green: `cd src/client && npm test -- --run`.
- [ ] No fallback masking: no new silent `catch {}`, no automatic scheme coercion,
      no "assume web API when parse fails" for non-web descriptors.
- [ ] OpenAPI descriptor remains canonical storage/runtime artifact.
- [ ] Existing `https://`, `http://`, `client://`, `sandbox://`, and `tool://`
      descriptors stay valid and executable.
- [ ] `ApiHost` compatibility behavior is preserved unless Phase 5 is explicitly
      active.
- [ ] No secret leaks: auth value templates, MCP headers, or tokens are never
      emitted to non-admin surfaces, logs, or exported JSON by default.
- [ ] Scope discipline: touched files remain within brief scope.
- [ ] UI gate passes when phase touches editor UX (`ui-gate.md`): component
      composition, interaction behavior, accessibility, and responsive layout.
- [ ] Runtime parity gate passes when phase affects classification, descriptor
      generation, preview, or dispatch (Phases 1-4 and 6).
- [ ] CodeQL diff clean after security-sensitive phases (3, 4, optional 5, 6 final).

### 4.2 Per-phase gate criteria

Each phase must pass 4.1 plus its own checks.

**Phase 1 - Scheme-aware UI over existing data**

- [ ] "Web Connectors" wording replaced with "Tool Sources" in the guide editor UI.
- [ ] Tool source classification is derived from `servers[0].url` scheme.
- [ ] Connector key labels are scheme-aware (API host, Client bridge, Init module,
      Local tool host) while still storing compatibility `apiHost`.
- [ ] Existing JSON editor remains available.
- [ ] Parsing/classification logic moved from `OpenApiSchemas.tsx` body into
      reusable helper modules.
- [ ] Source card list and states match `ui-gate.md` section 2.1 and 2.2.
- [ ] Keyboard navigation and ARIA semantics for tabs/cards meet
      `ui-gate.md` section 2.6.
- [ ] Runtime parity gate confirms existing bootstrap descriptors classify correctly.

**Phase 2 - Guided creation for existing schemes**

- [ ] Add Tool Source picker includes Web API, Client Actions, Sandbox Module.
- [ ] Advanced mode still exposes Local Function and Raw OpenAPI.
- [ ] Guided forms generate valid OpenAPI descriptors for Web, Client, Sandbox.
- [ ] Structured tool-definition operation editor exists (function name, summary,
      params, required/default/enum/example, response schema, execution mapping).
- [ ] Advanced JSON tab can switch to "custom descriptor" mode when round-trip is
      not lossless.
- [ ] Modal/drawer flow, unsaved-change handling, inline validation, and empty/error
      states match `ui-gate.md` sections 2.3 through 2.8.
- [ ] Responsive behavior meets `ui-gate.md` section 2.7 (desktop and mobile).
- [ ] Preview shown in UI is backed by backend preview endpoint, not frontend-only
      synthesis.

**Phase 3 - MCP discovery and descriptor generation**

- [ ] MCP source type supports connection config, test, discovery, selection, refresh.
- [ ] Operation IDs/backing MCP tool IDs are stable across discovery refreshes.
- [ ] Selected MCP tools generate OpenAPI operations plus metadata
      (`x-guideants-tool-source` or approved equivalent).
- [ ] Decision D1 strategy is implemented consistently:
  - `mcp://` path includes runtime dispatch support and tests, or
  - client bridge path routes via existing `client://` external tool flow.
- [ ] UI surfaces added/removed/changed/disabled diff after refresh.
- [ ] MCP UI state flows align with `ui-gate.md` sections 2.3, 2.8.
- [ ] Publishability restrictions for local-only MCP configurations are surfaced.
- [ ] CodeQL diff clean.

**Phase 4 - Backend validation and publish checks**

- [ ] Backend validates scheme/source-kind consistency and required scheme-specific
      fields.
- [ ] Descriptor preview endpoint returns source kind, action type, generated
      tool definitions, hidden/defaulted parameters, and validation messages.
- [ ] Preview endpoint uses runtime-compatible path (`OpenApiHelper` flow), not a
      divergent frontend-only model.
- [ ] Publish checks reject unsupported MCP transport/runtime combinations.
- [ ] Tests cover web/client/sandbox/tool/(mcp if enabled) descriptors and mismatch
      rejection paths.
- [ ] UI validation message mapping to backend errors follows `ui-gate.md` section 3.
- [ ] CodeQL diff clean.

**Phase 5 - Optional storage cleanup**

- [ ] Only runs if explicitly approved.
- [ ] New storage naming (if added) preserves `SpecificationJson` as canonical
      descriptor payload.
- [ ] Compatibility reads/writes for existing `AssistantOpenApiSchema` rows hold.
- [ ] Auth provider linkage remains compatible for existing data.
- [ ] Migration/backfill is reversible and documented.
- [ ] CodeQL diff clean for migration/serialization changes.

**Phase 6 - Tests, docs, final acceptance**

- [ ] Proposal acceptance criteria section 16 is explicitly mapped to test/docs/code.
- [ ] Bootstrap descriptors render as typed Tool Sources without runtime regression.
- [ ] Runtime parity gate full pass across all supported schemes in scope.
- [ ] Final CodeQL diff clean versus baseline.
- [ ] Docs updated for Tool Sources authoring, advanced JSON behavior, MCP limits,
      and migration notes.
- [ ] `STATUS.md` final acceptance checklist complete.

### 4.3 Runtime parity gate (summary)

Defined in `runtime-parity-gate.md`.
Run after Phases 1, 2, 3, 4, and final Phase 6.
Pass when descriptor classification, generated OpenAPI, preview output, and runtime
action dispatch agree for in-scope schemes.

### 4.4 UI gate (summary)

Defined in `ui-gate.md`.
Run after Phases 1, 2, 3 (MCP surfaces), and final Phase 6.
Pass when editor UX behavior, validation surfaces, accessibility, and responsive
layout match the concrete UI contract.

### 4.5 CodeQL gate (summary)

Defined in `codeql-gate.md`.
Local baseline-vs-current only (no GitHub parity checks).
Run after Phases 3, 4, optional 5, and final Phase 6.
Pass when NEW findings versus baseline are zero.

---

## 5. Deviation and failure protocol

If a gate fails, stop the line.

1. Classify failure in `STATUS.md`:
   - `build/test red`
   - `parity drift`
   - `missing DoD`
   - `scope creep`
   - `decision drift`
   - `schema corruption`
   - `security regression`
2. Re-dispatch same phase with focused correction note and full gate rerun.
3. Cap retries at 2. On required third attempt, escalate with gate output and
   root-cause hypothesis.
4. Record attempt count, failure mode, corrective diff, and rerun result.

Never defer a phase-owned defect into a later phase.

---

## 6. Final acceptance (after Phase 6 gate)

Implementation is complete only when all hold:

- [ ] Section 15 migration phases implemented as planned (with Phase 5 marked
      executed or intentionally skipped).
- [ ] Section 16 acceptance criteria mapped to code/tests/docs and checked.
- [ ] Existing saved schemas and bootstrap descriptors execute with unchanged runtime
      behavior unless explicitly expanded by decision-locked MCP work.
- [ ] Tool Source UI can create client and sandbox tools without manual raw JSON.
- [ ] Structured tool-definition editing covers required operation surfaces.
- [ ] Preview reflects exact runtime-facing tool definition contract.
- [ ] UI gate final pass.
- [ ] Runtime parity gate final pass.
- [ ] CodeQL final diff clean.
- [ ] `STATUS.md` has no open deviations.

When complete, provide a run summary: phases, retries, decisions locked, and whether
optional storage cleanup was shipped.
