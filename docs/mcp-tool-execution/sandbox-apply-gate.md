# Sandbox Apply Gate (MCP Tool Execution)

Companion to `00-orchestration.md`.

`runtimeExecution: sandbox_subprocess` runs registry packages as a `ScriptExecutionAgent`
stdio child, scoped to `projectId + guideId`. The design stages scoped sandbox state on
import and applies it only on an explicit author action (E12), and **blocks publish** when
a guide has any `sandbox_subprocess` MCP source whose scoped admin state is staged ≠
applied (E16). This gate proves the executor is shared across surfaces (E15) and that the
publish block is exact — neither over- nor under-blocking.

---

## 1. Gate intent

Pass this gate when all are true:

- `mcp+sandbox://` tool calls execute through the **same** executor on notebook, embed,
  and wire (E15) — no separate sandbox runtime, no publish-time embed-validation fork.
- Stdio child lifecycle is per-call v1 (`initialize` → `tools/call` → teardown) (E7).
- Scope is `projectId + guideId`: one venv/package set shared across notebooks on the
  guide.
- Node.js is available in **both** the full `guideants-ai` image and the slim variant
  (E10).
- Stage-on-import / apply-on-action holds (E12): import writes scoped state but never
  mutates the sandbox; apply happens only on Test connection / Install packages with
  explicit confirmation.
- **Publish is blocked iff** any `sandbox_subprocess` MCP source exists and scoped admin
  state is staged ≠ applied (E16) — proven by `setup-status` hash comparison.

---

## 2. Baseline checks (pre-flight)

- Record existing sandbox stack capabilities reused by MCP stdio (design §5.1):
  `ScriptExecutionAgent` `POST /execute`, `ResolveExecutionEnvironmentAsync` per-run env,
  scoped `requirements.txt`, `apt-packages.txt`, `install-scripts.json`, `SandboxAdmin*`
  via `guideants-guide-admin`, and the `setup-status` hash surface
  (`AdminSetupStatusRuntime.cs` / `AdminScopeAppliedStateRuntime.cs`).
- Confirm current images do **not** yet guarantee Node (baseline for E10).

---

## 3. Gate checks

### 3.1 Shared executor parity (E15)

Drive the same `mcp+sandbox://` tool through notebook SSE, published embed SSE, and a
published wire request. Assert all three enter `McpSandboxExecutor` via
`ThreadRun`/`SendMessageStreamAsync` — verified by trace/log or test seam, not by three
copies of dispatch code.

### 3.2 Stdio child lifecycle (E7)

- A per-call spawn issues JSON-RPC `initialize`, then `tools/call`, then tears down.
- Failure modes (spawn failure, non-zero exit, malformed JSON-RPC) surface as explicit
  tool errors — **no silent fallback** to a generic `sandbox://` Python path (design §5.3
  forbids routing registry packages through generic Python tools).

### 3.3 Scope correctness

- Two notebooks on the same guide share one venv/package set (`projectId + guideId`).
- A different guide in the same project has an independent scope.

### 3.4 Node availability (E10)

- `npx` (and the chosen runner, e.g. `uvx` for PyPI) resolve and execute in both the full
  `guideants-ai` image and the slim variant. Prove with an image smoke for each.

### 3.5 Stage-on-import / apply-on-action (E12)

- Import/Save writes scoped `requirements.txt` / `apt-packages.txt` /
  `install-scripts.json` / Environment entries **without** mutating the live sandbox
  (`setup-status` shows staged ≠ applied).
- Test connection / Install packages applies with explicit confirmation; afterward
  `setup-status` shows staged == applied.

### 3.6 Publish block exactness (E16)

| Guide state | Has `sandbox_subprocess` MCP? | Scoped staged vs applied | Publish |
|---|---|---|---|
| No sandbox MCP | no | n/a | **allowed** |
| Sandbox MCP, applied | yes | staged == applied | **allowed** |
| Sandbox MCP, not applied | yes | staged ≠ applied | **blocked** (E16) |
| Sandbox MCP, drift after edit | yes | staged ≠ applied | **blocked** |

The block compares `setup-status` hashes; it is not a per-surface decision (design §5.4).
Prove both the blocked and the unblocked transitions.

---

## 4. When to run this gate

| Point | Required checks |
|---|---|
| Pre-flight baseline | §2 |
| After Phase 5 | 3.1, 3.2, 3.3, 3.4 |
| After Phase 6 | 3.5, 3.6 (+ re-confirm 3.1) |
| Final acceptance (Phase 7) | 3.1–3.6 full pass |

---

## 5. Report-back addition (Phases 5, 6, 7)

```text
SANDBOX APPLY GATE:
- Shared executor on notebook/embed/wire (E15): <pass/fail>
- Stdio child lifecycle per-call, no silent fallback (E7): <pass/fail>
- projectId+guideId scope correctness: <pass/fail>
- Node in full + slim image (E10): full=<p/f> slim=<p/f>
- Stage-on-import / apply-on-action (E12): <pass/fail>
- Publish block exactness (E16): blocked=<p/f> unblocked=<p/f>
```
