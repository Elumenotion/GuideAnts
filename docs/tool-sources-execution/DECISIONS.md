# Tool Sources Guide Builder - Locked Decisions (single source of truth)

Last updated: 2026-06-19
Status: LOCKED

This file resolves proposal section 17 open questions and freezes cross-cutting
invariants before implementation starts.

Rules:

- If a decision below is `UNDECIDED`, any phase listed under "Blocks" is blocked.
- Changing a locked decision after a phase ships requires reverting and re-dispatching
  impacted phases.
- Subagents must not reinterpret values in this file.

---

## Part A - Open questions from proposal section 17

### D1. MCP scheme strategy

Proposal question: should server-reachable MCP use `mcp://`, or should all MCP route
through `client://` initially?

- Status: `LOCKED`
- Resolved: `client-bridge-first` — MCP routes via existing `client://` external-tool flow; no `mcp://` runtime dispatch.
- Blocks: Phase 3, Phase 4.

### D2. MCP transport scope for first release

Proposal question: which transports are in scope first (streamable HTTP, SSE, stdio,
client bridge)?

- Status: `LOCKED`
- Resolved: `streamable_http;client_bridge`
- Blocks: Phase 3, Phase 4 publish checks.

### D3. Where MCP metadata lives

Proposal question: store MCP metadata only in `x-guideants-tool-source`, in DB
fields, or both?

- Status: `LOCKED`
- Resolved: `descriptor-extension-only` — metadata in `x-guideants-tool-source` inside `SpecificationJson`; no new DB columns.
- Blocks: Phase 3 (descriptor generation). Phase 5 skipped.

### D4. Generated descriptor locking behavior

Proposal question: should generated descriptors be locked unless user selects custom
JSON mode?

- Status: `LOCKED`
- Resolved: `guided-with-custom-mode`
- Blocks: Phase 2.

### D5. Sandbox function discovery strategy

Proposal question: auto-inspect Python files or manual function entry first?

- Status: `LOCKED`
- Resolved: `manual-first`
- Blocks: Phase 2.

### D6. Structured parameter editor depth

Proposal question: how much nested JSON Schema is supported before fallback to
advanced editing?

- Status: `LOCKED`
- Resolved: `level-1-structured`
- Blocks: Phase 2.

### D7. Hidden/default-injected parameter UX

Proposal question: keep hidden/default-injected parameters in main table or in a
separate section?

- Status: `LOCKED`
- Resolved: `separate-injected-section`
- Blocks: Phase 2, Phase 4 preview presentation.

---

## Part B - Frozen invariants (not open for reinterpretation)

These come from proposal sections 2, 3, 6, 9, 10, 11, and 16:

- OpenAPI descriptors remain canonical internal tool definitions.
- `servers[0].url` scheme remains runtime dispatch selector.
- Existing descriptors using `http(s)://`, `client://`, `sandbox://`, and `tool://`
  remain valid.
- Raw OpenAPI JSON editing remains available as advanced mode escape hatch.
- Guide Builder UX is "Tool Sources", not "Web Connectors".
- UI authoring is scheme-aware and must not describe non-web identifiers as API hosts.
- Backend preview remains source of truth for final model-facing ToolDefinition.
- Existing bootstrap descriptors continue to load and execute with same behavior unless
  explicitly expanded by locked MCP decisions.
- For non-web schemes, `ApiHost` is compatibility storage only, not user-facing web
  semantics.
- No silent scheme coercion or fallback masking:
  parse/validation failures are explicit errors.
- Secrets and auth values never leak in preview payloads, logs, or non-admin responses.
- **Phase 5 skipped:** keep `AssistantOpenApiSchema`, `ApiHost`, `SpecificationJson`; no DB rename/migration; import/export unchanged.

---

## Part C - Decision ledger

| ID | Decision | Status | Resolved value | Date |
|----|----------|--------|----------------|------|
| D1 | MCP scheme strategy | LOCKED | `client-bridge-first` | 2026-06-19 |
| D2 | MCP transport scope | LOCKED | `streamable_http;client_bridge` | 2026-06-19 |
| D3 | MCP metadata storage | LOCKED | `descriptor-extension-only` | 2026-06-19 |
| D4 | Generated descriptor lock mode | LOCKED | `guided-with-custom-mode` | 2026-06-19 |
| D5 | Sandbox function discovery mode | LOCKED | `manual-first` | 2026-06-19 |
| D6 | Structured schema depth | LOCKED | `level-1-structured` | 2026-06-19 |
| D7 | Hidden/default parameter UX | LOCKED | `separate-injected-section` | 2026-06-19 |
