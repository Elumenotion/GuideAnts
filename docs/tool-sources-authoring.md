# Tool Sources — Guide Builder Authoring

Last updated: 2026-06-29

## Overview

Guide Builder **Tool Sources** replace the old "Web Connectors" framing. Under the hood, every tool source is still a full **OpenAPI descriptor** stored in `SpecificationJson`. Runtime dispatch uses `servers[0].url` scheme (`https://`, `client://`, `sandbox://`, `tool://`, **`mcp+api://`**, **`mcp+sandbox://`**).

## Source types

| Type | Server URL scheme | Connector key label |
|------|-------------------|---------------------|
| Web API | `https://` / `http://` | API host |
| Client Actions | `client://` | Client bridge |
| Sandbox Module | `sandbox://` | Init module |
| MCP Connection (HTTP) | `mcp+api://{bridgeId}` | MCP server |
| MCP Connection (stdio package) | `mcp+sandbox://{bridgeId}` | MCP package |
| Local Function | `tool://` | Local tool host |

> **Note:** `client://mcp-bridge-*` was removed. Legacy MCP sources are rewritten on save (see Migration below).

## Guided creation

Use **Add Tool Source** to pick a type. Guided forms generate valid OpenAPI without hand-authoring JSON.

- **Client Actions**: enter client bridge id; operations map to client-handled external tools.
- **Sandbox Module**: enter init module filename (e.g. `__init__.py`); add operations manually (D5: manual-first).
- **MCP Connection**: configure **runtime execution** (`api` or `sandbox_subprocess`), test connection, discover tools, select operations. Metadata lives in `x-guideants-tool-source` inside the descriptor.

## MCP runtime-execution authoring

MCP tools execute **server-side only** — on notebook chat, published embed, and published wire surfaces through one shared executor (`SendMessageStreamAsync` → `ThreadRun`).

### Runtime execution modes

| Mode | `runtimeExecution` | Generated URL | Authoring fields |
|------|-------------------|---------------|------------------|
| **API (server-side HTTP)** | `api` | `mcp+api://{bridgeId}` | MCP server URL (required), optional HTTP headers (literal or `{{secret:NAME}}`), `toolNamePrefix` |
| **Sandbox subprocess (registry package)** | `sandbox_subprocess` | `mcp+sandbox://{bridgeId}` | Package: `registryType` (npm \| pypi), `identifier`, `command` (e.g. `npx`, `uvx`), `args[]`; environment variables (name + secret ref); `toolNamePrefix` |

`discoveryTransport` is **derived** from mode (remote → `streamable_http`, package → `stdio`). It is shown read-only in the UI — never inferred from hostname.

### `toolNamePrefix`

Required. Must be **unique** across all MCP sources on the same assistant. Collisions block save with an inline error.

### Headers and environment variables

- HTTP headers and sandbox env vars support `{{secret:VAR}}` templates.
- Secrets resolve **only at tool-call time** via project/guide environment configuration — never in preview, logs, or exported JSON.
- Literal values are allowed for non-secret headers.

### Package / registry import

Registry packages (npm, PyPI) are **staged on import** — scoped sandbox files (`requirements.txt`, `apt-packages.txt`, `install-scripts.json`) are written but the live sandbox is **not** mutated until the author explicitly applies (Test connection / Install packages with confirmation).

## Migration from `client://mcp-bridge-*`

The prior **client-bridge-first** MCP model is removed. There is **no** runtime compat path for `client://mcp-bridge-*`.

| Legacy shape | Migrated to |
|--------------|-------------|
| `client://mcp-bridge-{id}` + `streamable_http` remote URL | `mcp+api://{bridgeId}` + `runtimeExecution: api` |
| `client://mcp-bridge-{id}` + `client_bridge` (no package) | `mcp+api://{bridgeId}` + `runtimeExecution: api` |
| `client://mcp-bridge-{id}` + package metadata | `mcp+sandbox://{bridgeId}` + `runtimeExecution: sandbox_subprocess` |

Migration triggers:

- **Save** in Guide Builder (`ToolSourceValidator.NormalizeDescriptor`)
- **Publish** backfill (`McpDescriptorMigrator.BackfillGuideSchemasAsync`)
- Dev/bootstrap script: `scripts/migrate-mcp-descriptors.py`

Opening a legacy source in the editor shows a **Migrated to API** notice; saving writes the new descriptor.

## Wire behavior (published API)

Wire endpoints (OpenAI Chat Completions, Responses, Anthropic Messages) are **thin protocol adapters** over `SendMessageStreamAsync`.

| Setting | Behavior |
|---------|----------|
| `stream: true` | **Live** provider-shaped SSE — tokens flush incrementally as `StreamingEvent`s arrive |
| `stream: false` | Folds the **same** stream to final JSON (not a separate executor) |
| MCP tool calls (E14) | **Opaque** — server executes MCP between model rounds; wire shows assistant text tokens only. No `tool_calls` / `tool_use` on wire responses in v1. No `pending_client_tool` for MCP. |

## Sandbox limits

| Topic | v1 behavior |
|-------|-------------|
| **Scope** | `projectId + guideId` — one venv/package set shared across all notebooks using the guide |
| **Stdio lifecycle (E7)** | Per-call spawn: `initialize` → `tools/call` → teardown (no session pool) |
| **Node.js (E10)** | Baked into full **and** slim `guideants-ai` Docker images (`npx` available) |
| **Publish gate (E16)** | Publish is **blocked** when any `sandbox_subprocess` MCP source exists and scoped admin `setup-status` shows staged ≠ applied |
| **Host-local MCP** | Not supported — MCP must be reachable as a remote URL (`api`) or run as a sandbox package (`sandbox_subprocess`) |

Apply is explicit and blast-radius-aware: *"Applying installs packages into the sandbox shared by every notebook using this guide in this project."*

## Operation editor

Structured sections: Tool Definition, Parameters (level-1 schema depth), Execution Mapping, Response Schema, Preview, Advanced Fragment.

- **Preview** calls backend `POST /api/operations/preview` with parent OpenAPI spec + operation fragment — same path as runtime `OpenApiHelper`.
- **Hidden/default-injected parameters** appear in a separate section (D7).
- **Custom descriptor mode**: when advanced JSON is not round-trippable, UI shows "Custom descriptor" and preserves raw JSON (`x-guideants-custom-descriptor`).

## Advanced JSON

The **Advanced JSON** tab remains available for power users. Import/paste OpenAPI still works.

## MCP non-goals (v1)

These are **explicitly out of scope** for the first MCP runtime-execution release:

- `client_host` / `client://` MCP execution (removed; see Migration)
- Browser-direct MCP from the GuideAnts client
- Hostname-based inference of execution mode (loopback URLs get a **warning** only)
- MCP authentication via `AssistantAuthProvider` (secrets use guide/project env resolution)
- Wire exposure of MCP `tool_calls` / `tool_use` steps to external API clients
- OCI MCP without install-script support
- Per-connection product rate limits
- Egress proxy / outbound allowlist (assume API outbound network access)

## Storage / import-export

No storage migration in this release. Existing `AssistantOpenApiSchema` rows and guide zip import/export layouts are unchanged; legacy MCP descriptors are rewritten on save/publish as described above.
