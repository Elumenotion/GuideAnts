# Wire Distribution Gate — Skills over the Published Wire (Skills Support)

Companion to `00-orchestration.md`. Run after Phase 4.

This gate verifies that a definition's skills reach external agents through the published
surfaces in the two modes from proposal §11 — **Mode A** (server-resolved skills, works for
any wire client with no client change) and **Mode B** (skills as MCP / orchestrator
resources) — and that skill activity is observable in the prompt trace.

Reference: [`../../CodexTrace.md`](../../CodexTrace.md) shows the target — an external client
resolving `orchestrator resource` skills via `list_mcp_resources` / `read_mcp_resource`.

---

## 1. Gate intent

Pass when all are true:

- **Mode A (server-resolved):** for a skill-bearing published guide, the discovery block +
  `skills.list`/`skills.read` operate server-side for OpenAI `/chat/completions`,
  `/responses`, and Anthropic `/messages` with **no client changes**, and the `/invoke`
  one-shot path also benefits.
- **Mode B (MCP resources):** each skill is exposed on `/api/published/mcp` as
  `skill://<guide>/<name>` (body) and `skill://<guide>/<name>/references/<path>` (reference),
  discoverable via `list_mcp_resources` and readable via `read_mcp_resource`.
- Skill activity is trace-tagged: `TurnTraceToolDefinition.Source` supports `skills`; the
  discovery block and `skills.*` calls appear with that source in the captured trace.
- No skills-specific orchestration is added to wire handlers; everything folds through
  `SendMessageStreamAsync` (frozen invariant).

---

## 2. Checks

### 2.1 Mode A — server-resolved

- [ ] Publish a guide with ≥1 enabled skill. Call `/chat/completions` (non-stream and
      stream) with a prompt that matches the skill's description. The model receives the
      discovery block; a `skills.read` call is resolved **server-side** and the turn
      completes with assistant text. No `pending_client_tool` for `skills.*`.
- [ ] Repeat for `/responses` and Anthropic `/messages` — same behavior; no client-side
      skill tooling required.
- [ ] `/api/published/guides/{pubId}/invoke` (which does not accept client tools) also gets
      the discovery block and can resolve `skills.read` server-side.

### 2.2 Mode B — MCP / orchestrator resources

- [ ] `list_mcp_resources` against `/api/published/mcp?pubId=…` includes one entry per
      enabled skill with URI `skill://<guide>/<name>` (S7).
- [ ] `read_mcp_resource` on `skill://<guide>/<name>` returns the `SKILL.md` body; on
      `skill://<guide>/<name>/references/<path>` returns that reference file. Path-safety is
      enforced (reject `..`/absolute/cross-skill), consistent with `skills.read`.
- [ ] Gating (S6) is applied to the resource listing: a skill hidden from discovery for a
      given assistant/guide context is not advertised as a resource in that context (or the
      listing matches the discovery visibility rule — document which).

### 2.3 Trace tagging

- [ ] `TurnTraceToolDefinition.Source` enum/string supports `skills`.
- [ ] In a captured trace, the discovery block is attributable to skills and `skills.list`/
      `skills.read` tool definitions/calls carry `Source = "skills"` (distinct from `guide`
      and `client`).

### 2.4 Invariants

- [ ] Grep wire handlers (`PublishedWire/*`): no skills-specific branching/orchestration;
      skill resolution happens in the shared conversation path / MCP resource provider, not
      in per-endpoint handlers.
- [ ] Secrets: if a skill body/reference references `{{secret:…}}`, it is returned verbatim
      as text (skills are instructions, not resolved credentials); no secret resolution
      happens in `skills.read`/resource read.

---

## 3. Report-back addition (Phase 4)

```text
WIRE DISTRIBUTION GATE:
- Mode A Chat (stream+non-stream) server-resolved, no pending_client_tool: <pass/fail>
- Mode A Responses + Anthropic: <pass/fail>
- Mode A /invoke benefits: <pass/fail>
- Mode B list_mcp_resources shows skill://<guide>/<name>: <pass/fail>
- Mode B read_mcp_resource body + reference (+ path-safety): <pass/fail>
- Gating applied to resource listing: <pass/fail + rule>
- Trace Source=skills on discovery + skills.* calls: <pass/fail + trace ref>
- No skills-specific logic in wire handlers (grep): <pass/fail>
```
