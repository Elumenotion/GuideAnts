# Tool Sources — Guide Builder Authoring

Last updated: 2026-06-19

## Overview

Guide Builder **Tool Sources** replace the old "Web Connectors" framing. Under the hood, every tool source is still a full **OpenAPI descriptor** stored in `SpecificationJson`. Runtime dispatch uses `servers[0].url` scheme (`https://`, `client://`, `sandbox://`, `tool://`).

## Source types

| Type | Server URL scheme | Connector key label |
|------|-------------------|---------------------|
| Web API | `https://` / `http://` | API host |
| Client Actions | `client://` | Client bridge |
| Sandbox Module | `sandbox://` | Init module |
| MCP Connection | `client://mcp-bridge-*` (client-bridge-first) | MCP server |
| Local Function | `tool://` | Local tool host |

## Guided creation

Use **Add Tool Source** to pick a type. Guided forms generate valid OpenAPI without hand-authoring JSON.

- **Client Actions**: enter client bridge id; operations map to client-handled external tools.
- **Sandbox Module**: enter init module filename (e.g. `__init__.py`); add operations manually (D5: manual-first).
- **MCP Connection**: configure transport (`streamable_http` or `client_bridge`), test connection, discover tools, select operations. Metadata lives in `x-guideants-tool-source` inside the descriptor.

## Operation editor

Structured sections: Tool Definition, Parameters (level-1 schema depth), Execution Mapping, Response Schema, Preview, Advanced Fragment.

- **Preview** calls backend `POST /api/operations/preview` with parent OpenAPI spec + operation fragment — same path as runtime `OpenApiHelper`.
- **Hidden/default-injected parameters** appear in a separate section (D7).
- **Custom descriptor mode**: when advanced JSON is not round-trippable, UI shows "Custom descriptor" and preserves raw JSON (`x-guideants-custom-descriptor`).

## Advanced JSON

The **Advanced JSON** tab remains available for power users. Import/paste OpenAPI still works.

## MCP limits (first release)

- Transports: `streamable_http`, `client_bridge` only.
- Runtime: MCP routes via `client://` external-tool bridge (no server-side `mcp://` dispatch).
- `client_bridge` discovery requires tools reported by the connected client host.
- Unsupported transports blocked at publish time.

## Storage / import-export

No storage migration in this release. Existing `AssistantOpenApiSchema` rows and guide zip import/export layouts are unchanged.
