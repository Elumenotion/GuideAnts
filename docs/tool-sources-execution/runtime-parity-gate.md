# Runtime Parity Gate (Tool Sources Guide Builder)

Companion to `00-orchestration.md`.

This feature is UI-heavy, but correctness depends on runtime parity:
the Tool Source forms, generated OpenAPI, preview responses, and runtime dispatch
must agree. This gate prevents a split-brain model where UI says one thing and
runtime executes another.

---

## 1. Gate intent

Pass this gate when all are true:

- Scheme classification is deterministic and shared (`http(s)`, `client`,
  `sandbox`, `tool`, and `mcp` if enabled).
- Guided forms generate descriptors that the backend parses exactly as expected.
- Preview output matches runtime `OpenApiHelper` transformation behavior.
- Existing descriptors (especially bootstrap client/sandbox schemas) still work.

---

## 2. Baseline checks (pre-flight)

Use current descriptors before any Tool Sources refactor.

### 2.1 Bootstrap descriptor smoke

Use these fixtures:

- `src/server/GuideAntsApi/Resources/bootstrap/guides/worm-commander/OpenAPI/Web Connector.json`
- `src/server/GuideAntsApi/Resources/bootstrap/assistants/slide-shows/OpenAPI/Web Connector.json`

Confirm they parse and still map to expected runtime action behavior
(`client://` and `sandbox://` respectively).

### 2.2 Existing preview endpoint smoke

Confirm `/api/operations/preview` still returns a valid tool definition for a known
operation fragment.

### 2.3 Baseline command set

```bash
cd src/server && dotnet build GuideAntsApi.sln && dotnet test GuideAntsApi.sln
cd src/client && npm run build && npm test -- --run
```

Record baseline results in `STATUS.md`.

---

## 3. Gate checks

### 3.1 Scheme classification parity

Given representative descriptors, frontend helpers and backend validators classify
to the same source kind and expected runtime action type.

Minimum matrix:

| URL | Source kind | Runtime action type |
|---|---|---|
| `https://api.example.com` | Web API | WebApi |
| `client://worm-commander-client` | Client Actions | ClientHandled |
| `sandbox://__init__.py` | Sandbox Module | SandboxHandled |
| `tool://localhost` | Local Function | LocalFunction |
| `mcp://my-server` (if enabled) | MCP Connection | McpHandled |

If D1 locks to client-bridge-first, MCP descriptors should classify as MCP source
authoring but route runtime through `client://` strategy consistently.

### 3.2 Connector key parity

Scheme-aware connector key labels and values are correct:

- Web API -> API host.
- Client Actions -> Client bridge.
- Sandbox Module -> Init module.
- MCP Connection -> MCP server.
- Local Function -> Local tool host.

No non-web source is labeled as "API host" in UI.

### 3.3 Descriptor generation parity

Guided forms produce valid OpenAPI with expected shapes:

- Web API keeps `servers[0].url` with `http(s)`.
- Client Actions produces `client://<bridge-id>`.
- Sandbox Module produces `sandbox://<init-module>`.
- MCP (if enabled) includes chosen scheme and metadata extension.

### 3.4 Preview parity (load-bearing)

For each in-scope source type, preview response must align with
`OpenApiHelper.GetToolDefinitionsFromJson` semantics:

- Function name from `operationId`.
- Description preference `summary` over `description`.
- Parameter handling for required/default/enum behavior.
- Response schema extraction behavior.

### 3.5 Advanced JSON/custom mode parity

If user edits advanced JSON beyond guided coverage:

- Descriptor is preserved.
- UI clearly marks custom state.
- Save/load does not silently delete unknown fields.

### 3.6 Compatibility parity

Saved guides and assistants with existing custom tools keep behavior:

- No tool loss in edit-save roundtrip.
- Auth linkage based on compatibility fields remains intact.
- Existing operation IDs remain stable.

---

## 4. When to run this gate

| Point | Required checks |
|---|---|
| Pre-flight baseline | 2.1, 2.2, 2.3 |
| After Phase 1 | 3.1, 3.2, 3.6 |
| After Phase 2 | 3.1-3.6 |
| After Phase 3 | 3.1-3.6 |
| After Phase 4 | 3.1-3.6 |
| Final acceptance (Phase 6) | 3.1-3.6 full pass |

---

## 5. Report-back addition for phases that touch classification/generation/preview

Subagents on Phases 1-4 and 6 append:

```text
RUNTIME PARITY GATE:
- Scheme classification parity matrix: <pass/fail + notes>
- Connector key labeling parity: <pass/fail>
- Descriptor generation parity (source types touched): <pass/fail>
- Preview/runtime tool-definition parity: <pass/fail>
- Existing bootstrap descriptor compatibility: <pass/fail>
```
