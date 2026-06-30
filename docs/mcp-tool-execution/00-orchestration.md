# MCP Tool Execution — Execution & Orchestration Guide

Last updated: 2026-06-29

This is the **conductor** document for executing
[`../mcp-tool-execution-design.md`](../mcp-tool-execution-design.md). It is written for
the **top-level (orchestrating) agent**. It defines how the work is split into
**subagent task briefs**, the **dependency order**, the **verification gates** the
orchestrator runs after each phase, and the **deviation/failure protocol** that keeps the
plan on-rails so it is executed correctly the first time.

> **Audience split**
>
> - **You (orchestrator)** read this file plus [`DECISIONS.md`](./DECISIONS.md),
>   [`STATUS.md`](./STATUS.md), [`runtime-parity-gate.md`](./runtime-parity-gate.md),
>   [`wire-streaming-gate.md`](./wire-streaming-gate.md),
>   [`sandbox-apply-gate.md`](./sandbox-apply-gate.md), [`ui-gate.md`](./ui-gate.md), and
>   [`codeql-gate.md`](./codeql-gate.md). You dispatch subagents, run gates, and update
>   `STATUS.md`.
> - **Subagents** read only their own `task-phase-N-*.md` brief, the design sections it
>   cites, and `DECISIONS.md`. A subagent should **not** need any other context.

---

## 0. How to use this folder

| File | Owner | Purpose |
|------|-------|---------|
| `00-orchestration.md` (this) | Orchestrator | Dispatch order, gates, deviation protocol. |
| `DECISIONS.md` | Orchestrator (locked before dispatch) | Locks design §8 (E1–E17) + the D1/D2 revisions. Single source of truth. |
| `STATUS.md` | Orchestrator (update after every gate) | Living ledger: phase state, gate results, deviations, re-dispatches. |
| `runtime-parity-gate.md` | Orchestrator + Phases 1,2,5,7 | Scheme → action-type dispatch parity, descriptor migration parity, no `client://` MCP residue. |
| `wire-streaming-gate.md` | Orchestrator + Phases 3,4,7 | Live `StreamingEvent` → provider-wire mapping; no buffer-then-emit. |
| `sandbox-apply-gate.md` | Orchestrator + Phases 5,6,7 | E16 publish-block when scoped sandbox state is staged ≠ applied. |
| `ui-gate.md` | Orchestrator + Phase 6 | Guide Builder MCP runtime-execution authoring UX contract (current→target deltas, runtime-execution control, staged/applied apply flow, publish-block surface, prefix uniqueness, migration notice). |
| `codeql-gate.md` | Orchestrator + security-sensitive phases | Local baseline-vs-current diff (secret/SSRF/subprocess risks). |
| `task-phase-1-descriptor-model-migration.md` | Subagent | `runtimeExecution`/`discoveryTransport` model + `mcp+api://`/`mcp+sandbox://` schemes + descriptor migration. |
| `task-phase-2-http-mcp-runtime.md` | Subagent | `runtimeExecution: api` server-side execution in `ThreadRun` (notebook). |
| `task-phase-3-wire-live-streaming.md` | Subagent | `WireStreamAdapter`; live `stream: true` on Chat Completions (wire prerequisite). |
| `task-phase-4-wire-hardening-parity.md` | Subagent | Responses + Anthropic live streaming; delete duplicate buffer paths. |
| `task-phase-5-sandbox-stdio-mcp.md` | Subagent | `runtimeExecution: sandbox_subprocess` stdio via `ScriptExecutionAgent` + Node in image. |
| `task-phase-6-registry-staging-publish-gate-ui.md` | Subagent | Registry import staging, E16 publish gate, Guide Builder runtime-mode UI. |
| `task-phase-7-tests-docs-acceptance.md` | Subagent | Cross-cutting close-out, docs, acceptance evidence. |

Each task brief follows the **same template**: Mission → Read first → Preconditions →
Guardrails → Tasks → Files in/out of scope → Self-verification → Definition of Done →
Report-back contract. The Report-back contract is what you diff against the brief to
**detect deviations**.

---

## 1. Pre-flight (do this once, before any subagent is dispatched)

Executing "the first time" depends on locking cross-cutting choices up front. **Do not
dispatch Phase 1 until all of the following are true.**

- [ ] **`DECISIONS.md` is fully LOCKED** (D1/D2 revisions + E1–E17). Any value still open
      that blocks a phase keeps that phase blocked.
- [ ] **Annotate the superseded folder.** Add a pointer in
      [`../tool-sources-execution/DECISIONS.md`](../tool-sources-execution/DECISIONS.md)
      D1/D2 noting they are revised by this folder (design §8 action). Do **not** silently
      diverge two "single sources of truth".
- [ ] **Confirm the wire prerequisite state.** Read
      [`../published-wire-execution/STATUS.md`](../published-wire-execution/STATUS.md).
      Wire continuation may be `DONE`, but design §6.2 says it still **buffers-then-emits**.
      Phase 3 of *this* plan owns the live-streaming conversion. Record the starting wire
      state in `STATUS.md`.
- [ ] **Capture a clean baseline** and record it in `STATUS.md`:
  - `cd src/server && dotnet build GuideAntsApi.sln`
  - `cd src/server && dotnet test GuideAntsApi.sln`
  - `cd src/client && npm run build`
  - `cd src/client && npm test -- --run`
- [ ] **Capture runtime-parity baseline** (`runtime-parity-gate.md` §2): classify the
      current MCP descriptors (`client://mcp-bridge-*`) and confirm today's dispatch.
- [ ] **Capture CodeQL baseline** (`codeql-gate.md`) and save SARIFs under
      `.codeql/baseline/`.
- [ ] **Inventory existing MCP code** so migration scope is known, not guessed:
      `src/server/GuideAntsApi/Services/Mcp/*`,
      `src/server/GuideAntsApi/Services/Guides/ToolSourceValidator.cs`,
      `src/client/src/components/guides/editor/toolSources/*`,
      `ActionType` enum + scheme dispatch in
      `src/server/AntRunner.Chat/AntRunner.ToolCalling/Functions/ToolCaller.cs`.
- [ ] Confirm clean working tree (`git status`) and an active feature branch
      (e.g. `feature/mcp-tool-execution`) per the repo branch-safety rule. **Never** set
      upstream to `origin/main`.
- [ ] Confirm `dotnet ef --version` is available (Phase 1 may need a migration **only** if
      it adds DB state; the design's default is descriptor-extension-only, so expect none).

If any blocking decision is unresolved, stop and ask before dispatching the dependent
phase.

---

## 2. Dependency graph (dispatch order)

```text
                 Phase 1  Descriptor model + migration
                 (runtimeExecution/discoveryTransport; mcp+api:// + mcp+sandbox://;
                  migrate client://mcp-bridge-* ; remove client:// MCP path)  D1/D2/E1/E2/E4/E8
                          │
              ┌───────────┴───────────────┐
              ▼                             ▼
          Phase 2                       Phase 3
   (HTTP MCP runtime: api)        (WireStreamAdapter; live stream:true
    McpApi + McpToolExecutor       on Chat Completions — wire prerequisite)
    in ThreadRun — notebook        E13 (start)
    §4 / E3 / E5
              │                             │
              └─────────────┬───────────────┘
                            ▼
                       Phase 4  Wire hardening + parity
                       (Responses + Anthropic live streaming;
                        delete duplicate buffer paths)  E13/E14
                            │   ── design "Phase A/B" complete here ──
                            ▼
                       Phase 5  Sandbox stdio packages
                       (sandbox_subprocess: McpSandbox + ScriptExecutionAgent
                        stdio child; Node in full+slim image)  §5 / E7/E8/E10
                            │
                            ▼
                       Phase 6  Registry staging + publish gate + Builder UI
                       (stage-on-import; apply-on-action; E16 publish block;
                        runtime-mode UI + toolNamePrefix uniqueness)  §3.4/§7 / E11/E12/E16
                            │   ── design "Phase C" complete here ──
                            ▼
                       Phase 7  Tests, docs, final acceptance
```

**Design-phase mapping:** design **Phase A** = orchestration Phases 1–3 (notebook HTTP MCP
+ wire-stream start); design **Phase B** = Phase 4 (wire hardening); design **Phase C** =
Phases 5–6 (sandbox stdio + authoring/publish gate).

**Rules:**

- Phases run in dependency order. **A phase is not "done" until its gate (section 4)
  passes.** A downstream phase must **never** start on top of a failed gate.
- **Phase 2 and Phase 3 may run in parallel** after Phase 1's gate is green — Phase 2
  touches `ToolCaller`/`ThreadRun`/`McpToolExecutor`; Phase 3 touches `PublishedWire/*`.
  They share no files. Prefer sequential unless schedule pressure demands it.
- **The wire prerequisite (Phase 3) must land with or before notebook MCP reaches a
  published surface.** Per `DECISIONS.md` Part C and design §6.5, MCP must not ship on the
  buffer-then-emit wire path. Phase 4 completes wire parity before Phase 5 exercises MCP
  on the wire.
- One subagent per phase brief. Do **not** hand a subagent more than its brief.
- Phases 1, 2, 5, 6 are **security-sensitive** (secret resolution, remote MCP connection,
  subprocess spawn, publish gating) and require CodeQL gate passes.

---

## 3. Dispatch protocol (per phase)

For each phase, in order:

1. **Confirm preconditions** in the brief (prior gate green; DECISIONS dependencies).
   Update `STATUS.md` → phase `IN_PROGRESS`.
2. **Dispatch one subagent** with exactly: *"Read and execute
   `docs/mcp-tool-execution/task-phase-N-*.md` end to end. Obey its guardrails and
   Definition of Done. Return the Report-back contract verbatim."* Give it no other
   instructions — the brief is the contract.
3. **Receive the Report-back** as a claim, not proof.
4. **Run the gate** (section 4 + the phase's own gate) with your own tools, not the
   subagent's word.
5. **Decide:** PASS → mark phase `DONE`, proceed. FAIL/DEVIATION → follow section 5.

> You verify; the subagent implements. Never let "the subagent said it's done" substitute
> for a green gate.

---

## 4. Verification gates

### 4.1 Global invariants — checked at **every** gate

- [ ] **Server build green:** `cd src/server && dotnet build GuideAntsApi.sln` (0 errors;
      warnings not worse than baseline).
- [ ] **Server tests green:** `cd src/server && dotnet test GuideAntsApi.sln` — no new
      failures vs baseline.
- [ ] **Client build green:** `cd src/client && npm run build`.
- [ ] **Client tests green:** `cd src/client && npm test -- --run`.
- [ ] **No `pending_client_tool` for MCP.** Grep the diff and run a notebook MCP turn:
      an MCP tool call must complete server-side and continue the turn. A paused
      `pending_client_tool` for an MCP tool is an automatic FAIL.
- [ ] **No fallback masking** (user rule). No new silent `catch {}`, no scheme coercion,
      no "assume web API on parse failure", no `api` → client-bridge downgrade, no
      hostname-based execution-mode inference. Parse/validation failures are explicit
      errors.
- [ ] **OpenAPI descriptor remains canonical;** MCP runtime mode lives only in
      `x-guideants-tool-source`. No unapproved new DB columns.
- [ ] **No secret leaks.** `{{secret:VAR}}` header/env templates resolve only at call time;
      raw values never appear in preview, logs, exported JSON, or non-admin responses.
- [ ] **One published runtime.** No MCP-specific orchestration added to wire handlers; all
      surfaces fold through `SendMessageStreamAsync`.
- [ ] **Scope discipline:** touched files stay within the brief's "Files in scope".
- [ ] **Matches `DECISIONS.md`** (D1/D2 revised + E1–E17). A subagent that kept a
      `client://` MCP path, inferred mode from hostname, or buffered a streaming wire path
      is an automatic FAIL.

### 4.2 Per-phase gate criteria

Each is **in addition** to 4.1. Commands assume `src/server` / `src/client` cwd.

**Phase 1 — Descriptor model + migration**

- [ ] `x-guideants-tool-source` carries `runtimeExecution` (`api` | `sandbox_subprocess`)
      **distinct** from `discoveryTransport` (`streamable_http` | `stdio`) (design §3.1).
- [ ] `servers[0].url` is `mcp+api://{bridgeId}` for `api` and `mcp+sandbox://{bridgeId}`
      for `sandbox_subprocess` (E2, E8). Descriptor-driven defaults: remotes → `api`,
      packages → `sandbox_subprocess` (E1).
- [ ] **Migration of existing `client://mcp-bridge-*` descriptors** is implemented as
      save-rewrite + publish backfill + a dev script (E4). **No `client://` MCP compat
      path remains** — grep proves the client-bridge MCP route is removed.
- [ ] Runtime-parity gate §3.1/§3.2 pass: migrated descriptors classify to `McpApi` /
      `McpSandbox` authoring with the correct scheme.
- [ ] CodeQL diff clean.

**Phase 2 — HTTP MCP runtime (`api`)**

- [ ] `ActionType.McpApi` exists; `ToolCaller` maps the `mcp+api` scheme to it (alongside
      existing `client`/`tool`/`sandbox` scheme dispatch).
- [ ] `McpToolExecutor` runs `tools/call` via the MCP SDK with headers resolved through
      `EnvironmentVariableConfigSerializer.DeserializeForExecution`; **per-call client +
      per-call timeout**, no product rate limits (E5).
- [ ] Runs inside `ThreadRun.DoToolCalls` during `SendMessageStreamAsync`; no client
      partition for MCP (design §4.1, §4.3). Notebook return-policy tool call completes
      with assistant text (design Phase A exit).
- [ ] Runtime-parity gate §3.1/§3.3 pass. CodeQL diff clean.

**Phase 3 — Wire live-streaming adapter (prerequisite)**

- [ ] A shared `WireStreamAdapter` (name TBD) consumes `SendMessageStreamAsync` and emits
      provider-shaped SSE **as events arrive** (design §6.3). `stream: true` on Chat
      Completions streams `token` → `chat.completion.chunk` deltas (no `StringBuilder`
      buffer, no single-`content_block_delta` dump).
- [ ] `stream: false` folds the **same** stream to final JSON (convenience fold, not a
      separate executor).
- [ ] Shipped rejection of `stream: true` on Chat Completions is removed (design §6.5: it
      is a bug, not a constraint).
- [ ] Wire-streaming gate §3 (Chat row) passes. CodeQL diff clean if security-sensitive.

**Phase 4 — Wire hardening + parity**

- [ ] Responses (`output text delta` / `output item complete` / usage) and Anthropic
      Messages (`content_block_delta` `text_delta` / block complete / `message_delta`
      usage / error) stream **live** through the same `WireStreamAdapter` (design §6.3
      mapping table).
- [ ] Duplicate buffer paths removed:
      `PublishedOpenAiWireEndpoints.ExecuteConversationAsync` inline buffer + per-request
      `wire-{timestamp}` convo; the `PublishedGuidesEndpoints` invoke buffer copy;
      `WireConversationExecutor.CollectWireConversationResultAsync` retained **only** for
      `stream: false` fold (design §6.4).
- [ ] Wire `stream: true` shows MCP as opaque (E14): assistant tokens stream while MCP
      runs server-side between rounds; no `tool_calls`/`tool_use` surfaced.
- [ ] Wire-streaming gate §3 full pass (Chat + Responses + Anthropic). CodeQL diff clean.

**Phase 5 — Sandbox stdio MCP (`sandbox_subprocess`)**

- [ ] `ActionType.McpSandbox` exists; `ToolCaller` maps the `mcp+sandbox` scheme to it.
- [ ] `McpSandboxExecutor` spawns the package command + resolved env via
      `ScriptExecutionAgent` stdio child (`npx`/`uvx`/…); JSON-RPC `initialize` →
      `tools/call` → teardown per call (E7). Scope = `projectId + guideId` (design §5.2).
- [ ] Same executor on notebook, embed, and wire (E15) — no separate sandbox runtime.
- [ ] **Node.js baked into full `guideants-ai` image AND slim variant** (E10); verify both
      images can run `npx`.
- [ ] Runtime-parity gate §3.1/§3.3, sandbox-apply gate §3 (executor path), CodeQL diff
      clean.

**Phase 6 — Registry staging + publish gate + Builder UI**

- [ ] **Stage-on-import:** Import/Save writes scoped `requirements.txt`,
      `apt-packages.txt`, `install-scripts.json`, and Environment entries; **never
      auto-applies** (E12, design §3.4).
- [ ] **Apply-on-action:** Test connection / Install packages prompts apply with explicit
      confirmation (mutates sandbox for all notebooks on that guide in the project).
- [ ] **E16 publish block:** publish is blocked when any `sandbox_subprocess` MCP source
      exists and scoped admin state is staged ≠ applied (`setup-status` hash mismatch).
      Sandbox-apply gate §3 proves block + unblock.
- [ ] Guide Builder controls per design §7 **and `ui-gate.md`**: current→target deltas
      C1–C9 done (grep-clean: no `client_bridge` / `mcp-bridge-` / "client-bridge-first" in
      `toolSources/`); runtime-execution (`api` | `sandbox_subprocess` only, no client
      host); required **unique `toolNamePrefix`** per MCP source (E11); package/URL/headers/
      env per mode; staged-vs-applied status + confirmed apply (blast-radius copy); publish-
      block surface (E16); generated URL shows `mcp+api://`/`mcp+sandbox://` only; localhost
      warning (E6); migration notice for opened `client://mcp-bridge-*` sources.
- [ ] **UI gate (`ui-gate.md`) passes** §6 phase checks + §7 test matrix, including
      **§4 reuse** (no reinvented dialog/secret/chip/spinner/admin-client) and
      **§5 decomposition** (no monolith growth of `McpConnectionPanel.tsx`).
- [ ] CodeQL diff clean.

**Phase 7 — Tests, docs, final acceptance**

- [ ] Design §9 phase exits each map to a passing test/file/commit:
      Phase A exit (notebook return-policy tool call completes with assistant text);
      Phase C exit (registry PyPI/npm stdio server completes a tool call via notebook or
      published surface).
- [ ] All gates full pass (runtime-parity, wire-streaming, sandbox-apply, ui-gate) + final
      CodeQL diff clean.
- [ ] Docs updated: MCP runtime-execution authoring, migration notes (no `client://`
      MCP), wire live-streaming behavior, sandbox stdio limits, E16 publish gate.
- [ ] `STATUS.md` final acceptance checklist complete; no open deviations.

### 4.3 Runtime parity gate (summary)

Defined in `runtime-parity-gate.md`. Run after Phases 1, 2, 5, and final 7. Pass when
descriptor classification, generated `servers[0].url` scheme, and runtime action dispatch
(`McpApi`/`McpSandbox`) agree, and no `client://` MCP path survives.

### 4.4 Wire streaming gate (summary)

Defined in `wire-streaming-gate.md`. Run after Phases 3, 4, and final 7. Pass when each
`StreamingEvent` maps to the correct live provider-wire event for Chat / Responses /
Anthropic, with no buffer-then-emit and a faithful `stream: false` fold.

### 4.5 Sandbox apply gate (summary)

Defined in `sandbox-apply-gate.md`. Run after Phases 5, 6, and final 7. Pass when the
shared executor runs stdio MCP on all surfaces and publish is blocked exactly when scoped
admin state is staged ≠ applied (E16).

### 4.6 UI gate (summary)

Defined in `ui-gate.md`. Run after Phase 6 and final Phase 7. Pass when the Guide Builder
MCP authoring surface implements the API-only runtime-execution model (no client-bridge
residue), the staged/applied + confirmed-apply flow, the E16 publish-block surface,
`toolNamePrefix` uniqueness, secret masking, the migration notice, and the inherited
accessibility/responsive contract. **Scope:** Guide Builder authoring only — the notebook
chat consumer is out of scope (clean separation; chat rendering follows working
server-side tool calling).

### 4.7 CodeQL gate (summary)

Defined in `codeql-gate.md`. Local baseline-vs-current only. Run after Phases 1, 2, 5, 6,
and final 7. Pass when NEW findings versus baseline are zero.

---

## 5. Deviation & failure protocol

When a gate fails, **stop the line.** Do not start the next phase.

1. **Classify** the failure in `STATUS.md`:
   - `build/test red` → mechanical; re-dispatch with the exact error + failing command.
   - `parity drift` → classification/dispatch disagree (e.g. migrated descriptor still
     dispatches via `client://`). Revert and re-dispatch.
   - `wire buffering` → a "streaming" path still buffers-then-emits. Hard reject.
   - `missing DoD` → under-delivered; re-dispatch with the unchecked items quoted.
   - `scope creep` → out-of-scope files touched; revert unless genuinely required, in
     which case update the brief + `DECISIONS.md` first.
   - `decision drift` → built against the wrong DECISIONS value (kept `client://` MCP,
     hostname inference, separate wire engine). Revert and re-dispatch with DECISIONS
     re-quoted.
   - `fallback/masking` → hard reject; require removal (user rule).
   - `ui monolith / reinvention` (Phase 6) → a grown `McpConnectionPanel.tsx`, business
     logic in JSX/effects, a new presentational `.tsx` over ~250 lines, or a duplicate of
     `ConfirmationDialog`/secret-ref/chip/spinner or a second sandbox-admin client. Hard
     reject; require decomposition + reuse per `ui-gate.md` §4–§5.
   - `secret leak` / `security regression` → hard reject; fix in code, no suppression.
2. **Re-dispatch** the *same* phase brief with a focused correction note ("Gate failed on
   X; fix only X; do not touch anything else"). Re-run the **full** gate afterward.
3. **Cap retries at 2.** On a required third attempt, escalate to the user with gate
   output and a root-cause hypothesis — the brief or a DECISIONS value may be wrong.
4. **Record everything** in `STATUS.md`: attempt #, failure mode, corrective diff, re-gate
   result.

**Never** advance a phase to fix a problem a later phase will "pick up". Fix it in the
phase that owns it (design §5.5 explicitly forbids deferring stdio gaps).

---

## 6. Final acceptance (after Phase 7 gate)

The plan is "executed fully" only when **all** hold:

- [ ] Design §9 phases implemented: Phase A (notebook HTTP MCP + wire live-stream start),
      Phase B (wire hardening), Phase C (sandbox stdio + publish gate) — each marked
      executed in `STATUS.md`.
- [ ] **API-only MCP everywhere** (E3): notebook, embed, and published wire execute MCP
      server-side through the single `SendMessageStreamAsync` path; no `client://` MCP
      remains; no `pending_client_tool` for MCP.
- [ ] `mcp+api://` → `McpApi` and `mcp+sandbox://` → `McpSandbox` dispatch with headers/env
      resolved at call time; secrets never leak.
- [ ] Wire `stream: true` streams live tokens for Chat, Responses, and Anthropic while MCP
      runs opaquely server-side; `stream: false` folds the same stream.
- [ ] Sandbox stdio MCP runs as a `ScriptExecutionAgent` child on `projectId + guideId`
      scope; Node is in full + slim images; publish is blocked when scoped state is staged
      ≠ applied (E16).
- [ ] Guide Builder authors `api`/`sandbox_subprocess` MCP with unique `toolNamePrefix`,
      no client-host option, and `mcp+api://`/`mcp+sandbox://` generated URLs only.
- [ ] Guide Builder authoring UX meets `ui-gate.md` (API-only model, no client-bridge
      residue, confirmed apply, E16 publish-block surface, migration notice).
- [ ] All five gates green on the final tree (runtime-parity, wire-streaming, sandbox-apply,
      ui-gate, CodeQL); `STATUS.md` shows every phase `DONE` with no open deviations.

When all are checked, summarize the run (phases, retries, any DECISIONS that changed
mid-flight, whether sandbox stdio shipped) for the user.
