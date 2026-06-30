# MCP Tool Execution — Execution Status Ledger

The orchestrator updates this after every dispatch and gate. It is the audit trail that
proves the plan was executed fully and surfaces any open deviations.

State values: `BLOCKED` · `READY` · `IN_PROGRESS` · `GATE_FAILED` · `DONE` · `SKIPPED`.

Last updated: 2026-06-29 — **Phase 7 DONE; final acceptance complete**

---

## Baseline (Pre-flight, orchestration §1)

| Check | Command | Result | Date |
|---|---|---|---|
| Server build | `dotnet build GuideAntsApi.sln` (in `src/server`) | 0 errors, 8 warnings (MSTEST0044) | 2026-06-29 |
| Server tests | `dotnet test GuideAntsApi.sln` (in `src/server`) | unit 1698/1698, script-agent 61/67 (6 skip), integration 190/241 (45 fail — env 500s) | 2026-06-29 |
| Client build | `npm run build` (in `src/client`) | pass | 2026-06-29 |
| Client tests | `npm test -- --run` (in `src/client`) | 3013/3013 pass | 2026-06-29 |
| Runtime parity baseline | `runtime-parity-gate.md` §2 | `client://mcp-bridge-*` → `ClientHandled` + `pending_client_tool`; dispatch: client/tool/sandbox→handled, else WebApi | 2026-06-29 |
| Wire start state | `../published-wire-execution/STATUS.md` | continuation DONE; wire still buffer-then-emit (design §6.2); Phase 3 owns live-stream | 2026-06-29 |
| CodeQL baseline | `codeql-gate.md` → `.codeql/baseline/` | C#=13, Python=1, JS=5 | 2026-06-29 |
| Clean tree / branch | `git status` + `git branch -vv` | `feature/mcp-tool-execution` (no upstream yet) | 2026-06-29 |
| DECISIONS resolved | `DECISIONS.md` D1/D2 revised + E1–E17 | LOCKED | 2026-06-29 |
| `tool-sources-execution` D1/D2 annotated | pointer added to superseded folder | done | 2026-06-29 |
| `dotnet ef --version` | pre-flight | 9.0.12 available | 2026-06-29 |

---

## Phase ledger

| Phase | Brief | State | Attempts | Gate result | Notes / deviations |
|---|---|---|---|---|---|
| 1 — Descriptor model + migration | `task-phase-1-descriptor-model-migration.md` | DONE | 1 | PASS | +8 unit tests; CodeQL 0 new |
| 2 — HTTP MCP runtime (`api`) | `task-phase-2-http-mcp-runtime.md` | DONE | 1 | PASS | McpApi+McpSandbox dispatch; 1763 unit tests |
| 3 — Wire live-streaming adapter | `task-phase-3-wire-live-streaming.md` | DONE | 1 | PASS | WireStreamAdapter; prereq from published-wire branch |
| 4 — Wire hardening + parity | `task-phase-4-wire-hardening-parity.md` | DONE | 1 | PASS | Responses+Anthropic live; dead buffer helpers remain unused |
| 5 — Sandbox stdio MCP | `task-phase-5-sandbox-stdio-mcp.md` | DONE | 1 | PASS | Node in full+slim Dockerfiles; CodeQL web.config fixed Phase 7 |
| 6 — Registry staging + publish gate + UI | `task-phase-6-registry-staging-publish-gate-ui.md` | DONE | 1 | PASS | Panel decomposed; grep clean; E16 gate |
| 7 — Tests / docs / acceptance | `task-phase-7-tests-docs-acceptance.md` | DONE | 1 | PASS | acceptance-evidence.md; +8 cross-cutting tests; docs |

---

## Runtime parity gate ledger

| Scan point | Scheme classification | Generated URL scheme | Runtime action dispatch | No `client://` MCP residue | Notes |
|---|---|---|---|---|---|
| Baseline | `client://mcp-bridge-*` → ClientHandled | `client://mcp-bridge-{id}` | client/tool/sandbox/WebApi only | n/a (pre-migration) | ToolCaller.cs L228-231 |
| After Phase 1 | mcp+api/mcp+sandbox + legacy migration | `mcp+api://` / `mcp+sandbox://` | +McpApi/McpSandbox | migration rewrites legacy | |
| After Phase 2 | api execution live | `mcp+api://` | McpApi → McpToolExecutor | PASS | return_policy happy path |
| After Phase 5 | sandbox execution live | `mcp+sandbox://` | McpSandbox → ScriptExecutionAgent | PASS | |
| **Final acceptance** | **PASS** §3.1–3.5 | `mcp+api://` / `mcp+sandbox://` only | McpApi/McpSandbox + non-MCP unchanged | **PASS** (grep clean prod) | `McpRuntimeParityAcceptanceTests` + existing matrix tests |

---

## Wire streaming gate ledger

| Scan point | Chat live deltas | Responses live | Anthropic live | No buffer-then-emit | `stream:false` fold parity | Notes |
|---|---|---|---|---|---|---|
| Baseline | buffered | buffered | pseudo-SSE | FAIL (shipped) | n/a | published-wire DONE; buffer-then-emit remains |
| After Phase 3 | live | n/a | n/a | PASS (Chat) | — | |
| After Phase 4 | live | live | live | PASS | fold parity pass | |
| **Final acceptance** | **PASS** | **PASS** | **PASS** | **PASS** | **PASS** | `WireStreamAdapterTests` + `PublishedOpenAiWireHandlersTests`; MCP opacity E14 |

---

## Sandbox apply gate ledger

| Scan point | Shared executor (notebook/embed/wire) | Stdio child spawn | Node in full+slim image | E16 publish block (staged≠applied) | Notes |
|---|---|---|---|---|---|
| After Phase 5 | PASS | PASS E7 | PASS E10 Dockerfiles | n/a | |
| After Phase 6 | PASS | — | — | PASS E16 | |
| **Final acceptance** | **PASS** E15 | **PASS** E7 | **PASS** E10 | **PASS** E12+E16 | `McpRuntimeParityAcceptanceTests` + `McpStdioEndpointTests` + publish gate tests |

---

## UI gate ledger (Guide Builder MCP authoring — Phase 6)

| Scan point | Deltas C1–C9 + grep clean | Runtime-exec control (api/sandbox) | HTTP mode (URL/headers/E6) | Sandbox mode (pkg/env/apply) | Source card + migration notice | Prefix uniqueness | Publish-block surface | a11y / responsive | Notes |
|---|---|---|---|---|---|---|---|---|---|
| Baseline | FAIL (client-bridge UI shipped) | n/a | n/a | n/a | n/a | n/a | n/a | n/a | `McpConnectionPanel` = client_bridge model |
| After Phase 6 | PASS | PASS | PASS | PASS | PASS | PASS | PASS | PASS | decomposed panel (~235 lines) |
| **Final acceptance** | **PASS** | **PASS** | **PASS** | **PASS** | **PASS** | **PASS** | **PASS** | **PASS** | grep: 0 `client_bridge`/`mcp-bridge-`/`client-bridge-first` in toolSources/ |

> Scope: Guide Builder authoring only. Notebook chat consumer is out of scope (clean
> separation — chat rendering follows working server-side tool calling).

---

## CodeQL findings ledger (local, no GitHub parity)

| Scan point | C# count | Python count | JS count | New vs baseline | Notes |
|---|---|---|---|---|---|
| Baseline | 13 | 1 | 5 | — | `.codeql/baseline/` |
| After Phase 1 | — | — | — | 0 | |
| After Phase 2 | — | — | — | 0 | |
| After Phase 5 | 15 | 1 | 5 | +2 (docker ScriptExecutionAgent web.config) | pending Phase 7 |
| After Phase 6 | — | — | — | — | |
| **Final acceptance** | **12** | **1** | **5** | **0 new** (RuleId+File diff) | Fixed `X-Frame-Options` on Phase 5 docker web.config copies |

---

## Open decisions blocking dispatch

None. D1/D2 revised + E1–E17 locked (`DECISIONS.md`). Pre-flight baselines captured 2026-06-29.

---

## Deviation log

| # | Phase | Attempt | Classification | What failed | Action taken | Re-gate result |
|---|---|---|---|---|---|---|
| — | — | — | — | none | — | — |

---

## Final acceptance checklist (orchestration §6)

- [x] Design §9 Phase A / B / C implemented (or intentionally scoped).
- [x] API-only MCP everywhere; no `client://` MCP; no `pending_client_tool` for MCP.
- [x] `mcp+api://` → `McpApi`, `mcp+sandbox://` → `McpSandbox`; secrets resolved at call
      time, never leaked.
- [x] Wire `stream: true` live for Chat + Responses + Anthropic; `stream: false` folds the
      same stream; MCP opaque (E14).
- [x] Sandbox stdio MCP via `ScriptExecutionAgent` child; `projectId+guideId` scope; Node
      in full + slim; E16 publish gate enforced.
- [x] Guide Builder authors `api`/`sandbox_subprocess` only; unique `toolNamePrefix`;
      generated `mcp+api://`/`mcp+sandbox://` URLs; no client-bridge residue; confirmed
      apply + E16 publish-block surface; migration notice for old sources (`ui-gate.md`).
- [x] Runtime-parity, wire-streaming, sandbox-apply, ui-gate, and CodeQL gates all
      final-pass.
- [x] No open deviations.

### Final verification (Phase 7, 2026-06-29)

| Check | Result |
|---|---|
| Server build | PASS (0 errors) |
| Server unit tests | PASS 1789/1789 |
| ScriptExecutionAgent tests | PASS 65/71 (6 skip) |
| Integration tests | PASS 235/241 (6 skip) |
| Client build | PASS |
| Client tests | PASS 3020/3020 |
| Acceptance evidence | `acceptance-evidence.md` |
| Cross-cutting tests | `McpRuntimeParityAcceptanceTests.cs` (+8) |
