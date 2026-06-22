# Guide Builder Tool Sources Proposal

Status: Draft / design proposal
Owner: Guide Builder / tool calling
Related:
- `src/client/src/components/guides/editor/OpenApiSchemas.tsx`
- `src/server/AntRunner.Chat/AntRunner.ToolCalling/Functions/ToolCaller.cs`
- `src/server/AntRunner.Chat/AntRunner.Chat/ThreadRun.cs`
- `src/server/GuideAntsApi/Services/SandboxToolService.cs`
- `src/server/GuideAntsApi/Resources/bootstrap`

## 1. Problem Summary

The current Guide Builder UI presents tool integrations as "Web Connectors" and asks users to work directly with OpenAPI JSON. That is serviceable for real HTTP APIs, but it is a poor fit for the execution schemes the tool calling runtime already supports:

- `https://` and `http://` for web API tools.
- `client://` for client-handled tools bridged to the host application.
- `sandbox://` for Python functions executed in the sandbox.
- `tool://` for local .NET static method or property invocation.

The OpenAPI descriptor is the correct internal contract. The problem is that the UI exposes too much of that contract too early and labels every integration as a web service. Users must learn hidden scheme semantics and author JSON even when they are only trying to expose a Python function, a client action, or, soon, an MCP tool.

This proposal keeps OpenAPI descriptors as the canonical internal representation and improves the Guide Builder authoring model around them.

## 2. Firm Decisions

1. Internal tool definitions remain OpenAPI descriptors.
2. `servers[0].url` scheme remains the primary runtime dispatch signal.
3. Existing descriptors using `https://`, `client://`, `sandbox://`, and `tool://` remain valid.
4. The Guide Builder should generate and maintain OpenAPI descriptors for common tool source types instead of requiring users to hand-author JSON.
5. Raw OpenAPI JSON editing remains available as an advanced escape hatch.

## 3. Goals

1. Replace the user-facing "Web Connectors" framing with a broader "Tool Sources" authoring experience.
2. Make each supported scheme understandable through source-specific UI.
3. Let users create non-web tools without writing OpenAPI JSON by hand.
4. Add MCP client connection support as a first-class tool source.
5. Preserve the existing runtime contract so existing bootstrap tools and saved guides continue to work.
6. Keep the generated OpenAPI descriptor visible, inspectable, and exportable.

## 4. Non-goals

1. Replacing OpenAPI as the internal tool definition format.
2. Moving runtime dispatch away from URI scheme semantics.
3. Removing support for raw OpenAPI editing.
4. Refactoring all tool storage in one migration.
5. Making every MCP transport publishable in every hosting environment.

## 5. Current Behavior

The runtime reads the first OpenAPI server URL and maps the URI scheme to an action type:

| Scheme | Runtime behavior |
|--------|------------------|
| `http://` / `https://` | Execute as a web API request. |
| `client://` | Emit an external tool call and pause with `pending_client_tool`. |
| `sandbox://` | Execute a Python function in the sandbox. The URL host/path identifies the init script. |
| `tool://` | Invoke a local .NET static method or property by reflection. |

The Guide Builder UI currently extracts a "host" with `new URL(serverUrl).host`, stores it as `apiHost`, uses that for uniqueness and auth wiring, and presents the connector as a web API. That overloads a web-centric concept across non-web schemes. For example:

| Descriptor URL | Derived host today | What it really means |
|----------------|--------------------|----------------------|
| `https://api.example.com` | `api.example.com` | Web API authority. |
| `client://worm-commander-client` | `worm-commander-client` | Client bridge identifier. |
| `sandbox://__init__.py` | `__init__.py` | Sandbox initialization module. |
| `tool://localhost` | `localhost` | Local function namespace marker, with method target in the path. |

The extraction is technically useful, but the UI should name the concept according to the scheme instead of always calling it an API host.

## 6. Proposed Product Model

Introduce a user-facing "Tool Source" concept in the Guide Builder. A tool source is an authoring wrapper around one OpenAPI descriptor.

The descriptor remains the stored/runtime artifact. The source type controls how the descriptor is edited.

| Tool source type | Canonical scheme | Primary user input |
|------------------|------------------|--------------------|
| Web API | `https://` or `http://` | Server URL, auth, paths, operations, request/response schemas. |
| Client Actions | `client://` | Client bridge id, actions, input schemas, result schemas. |
| Sandbox Module | `sandbox://` | Init/module file, Python functions, input schemas, result schemas. |
| MCP Connection | Proposed `mcp://` or `client://` bridge form | MCP connection settings, discovered tools, selected operations. |
| Local Function | `tool://` | .NET type/member path and schema. Advanced/developer mode only. |

The UI should stop saying "each API host must be unique" globally. Instead it should use a scheme-aware "connector key":

| Source type | Connector key label |
|-------------|---------------------|
| Web API | API host |
| Client Actions | Client bridge |
| Sandbox Module | Init module |
| MCP Connection | MCP server |
| Local Function | Local tool host |

Internally, this can still map to the existing `apiHost` field during the first implementation phase.

The tool calling system already defines what a tool looks like after an OpenAPI operation is normalized:

```text
ToolDefinition
- type: function
- function
  - name
  - description
  - parameters
    - type: object
    - properties
    - required
  - contentType
  - responseSchemas
```

The Guide Builder should use this as the primary editing mental model. Users should edit the function name, description, arguments, required fields, defaults, enums, examples, and result schema through UI controls. The OpenAPI path, method, request body, and response body are the backing descriptor details.

## 7. Guide Builder UX

Rename the section from "OpenAPI Schemas" or "Web Connectors" to "Tool Sources".

The empty state and add button should say "Add Tool Source". The picker should offer:

1. Web API
2. Client Actions
3. Sandbox Module
4. MCP Connection
5. Local Function, hidden behind advanced mode
6. Raw OpenAPI, advanced mode

Each source card should show:

- Source name.
- Source type.
- Connector key with a scheme-aware label.
- Operation count.
- Auth or connection status.
- Validation status.

Each expanded source should use shared tabs with source-specific content:

| Tab | Purpose |
|-----|---------|
| Operations | Enable, edit, add, test, or remove operations. |
| Connection | Configure server URL, bridge id, sandbox module, MCP transport, or local target. |
| Auth & Secrets | Configure scheme-appropriate auth and secret handling. |
| Advanced JSON | View and edit the generated OpenAPI descriptor. |

The Advanced JSON tab should make it clear when a descriptor is generated from a guided form. If the user edits JSON directly, the UI should either re-parse it back into the typed form or mark the source as "custom descriptor" and preserve it without trying to round-trip every field.

The UI must include actual tool definition editors, not only descriptor-level forms. The model-facing function contract is the thing authors need to understand and tune.

Minimum operation editor surfaces:

| Surface | Purpose |
|---------|---------|
| Tool Definition | Edit operation ID/function name, summary/description, argument list, required flags, defaults, examples, enums, and nested array/object shapes. |
| Execution Mapping | Edit the scheme-specific backing target: HTTP method/path, client action key, sandbox function, local method path, or MCP tool id. |
| Result Schema | Edit or inspect the `200` response schema used to filter tool output. |
| Preview | Show the generated `ToolDefinition` exactly as the LLM receives it. |
| Advanced Fragment | Show the generated operation fragment `{ path, method, operation }`. |

## 8. Source-specific Requirements

### 8.1 Web API

The Web API flow is the current flow with better labeling.

Requirements:

1. Import OpenAPI JSON/YAML by paste or file.
2. Let users edit server URL, auth, operations, request schemas, and response schemas.
3. Preserve support for hand-authored descriptors.
4. Validate that operation IDs are present and unique.
5. Keep web API auth keyed by authority.

Generated descriptor shape:

```json
{
  "openapi": "3.0.1",
  "servers": [{ "url": "https://api.example.com" }],
  "paths": {}
}
```

### 8.2 Client Actions

Client Actions expose host/client functionality through `client://`.

Requirements:

1. User enters a client bridge id, such as `worm-commander-client`.
2. User defines actions without writing JSON.
3. Each action maps to an OpenAPI operation.
4. Operation IDs should be stable and friendly.
5. The UI should explain execution status in product language, for example "Handled by the client application".
6. Auth UI should be hidden unless a future client bridge permission model is added.

Generated descriptor shape:

```json
{
  "openapi": "3.0.1",
  "info": {
    "title": "Client Actions",
    "version": "1.0.0"
  },
  "servers": [{ "url": "client://bridge-id" }],
  "paths": {
    "Bridge.Action": {
      "post": {
        "operationId": "Action",
        "requestBody": {
          "required": true,
          "content": {
            "application/json": {
              "schema": { "type": "object" }
            }
          }
        },
        "responses": {
          "200": {
            "description": "OK",
            "content": {
              "application/json": {
                "schema": { "type": "object" }
              }
            }
          }
        }
      }
    }
  }
}
```

### 8.3 Sandbox Module

Sandbox Module sources expose Python functions through `sandbox://`.

Requirements:

1. User selects or enters an init module filename, such as `__init__.py`.
2. The UI can optionally inspect known resource files and suggest available Python functions.
3. Each operation ID maps to the Python function name.
4. The UI should label the URL field as "Init module", not "Server URL".
5. The operation editor should make parameter schema authoring simpler with object-property rows.
6. The generated descriptor should preserve the existing `sandbox://__init__.py` convention.

Generated descriptor shape:

```json
{
  "openapi": "3.0.1",
  "servers": [
    {
      "url": "sandbox://__init__.py",
      "description": "Sandbox execution environment"
    }
  ],
  "paths": {
    "/create_presentation": {
      "post": {
        "operationId": "create_presentation",
        "requestBody": {
          "required": true,
          "content": {
            "application/json": {
              "schema": { "type": "object" }
            }
          }
        },
        "responses": {
          "200": {
            "description": "OK"
          }
        }
      }
    }
  }
}
```

### 8.4 MCP Connection

MCP should be added as a first-class tool source that still generates an OpenAPI descriptor.

There are two viable descriptor strategies:

1. Add a new runtime scheme, such as `mcp://server-id`, and implement an MCP execution branch in the tool caller.
2. Represent MCP through `client://mcp-bridge-id` when the MCP server is only reachable through the host/client bridge.

The product should support both execution locations if needed:

| MCP execution location | Suggested scheme | Notes |
|------------------------|------------------|-------|
| Server-reachable MCP | `mcp://server-id` | Server runtime owns connection, auth, discovery, and invocation. |
| Client-local MCP | `client://mcp/<bridge-id>` or `client://bridge-id` | Client host owns connection and returns external tool results. |

Requirements:

1. User configures MCP transport.
2. User tests the connection.
3. System discovers MCP tools and input schemas.
4. User selects which discovered tools to expose.
5. System generates OpenAPI operations from discovered MCP tool schemas.
6. User can refresh discovery and review changes.
7. Tool names should be stable across refreshes.
8. The UI should show changed, added, removed, and disabled tools.
9. Published guide validation should block unsupported local-only MCP configurations.

Suggested MCP source config stored alongside or inside generated descriptor metadata:

```json
{
  "transport": "streamable_http",
  "url": "https://mcp.example.com/mcp",
  "headers": {
    "Authorization": "{{secret:mcp_server_token}}"
  },
  "toolNamePrefix": "mcp"
}
```

Suggested generated descriptor shape for server-reachable MCP:

```json
{
  "openapi": "3.0.1",
  "info": {
    "title": "MCP Tools",
    "version": "1.0.0"
  },
  "servers": [
    {
      "url": "mcp://my-mcp-server",
      "description": "MCP server connection"
    }
  ],
  "x-guideants-tool-source": {
    "kind": "mcp",
    "transport": "streamable_http",
    "url": "https://mcp.example.com/mcp"
  },
  "paths": {
    "/tools/search": {
      "post": {
        "operationId": "search",
        "summary": "Search",
        "requestBody": {
          "required": true,
          "content": {
            "application/json": {
              "schema": {
                "type": "object"
              }
            }
          }
        },
        "responses": {
          "200": {
            "description": "MCP tool result"
          }
        }
      }
    }
  }
}
```

The exact extension name is flexible, but an `x-guideants-tool-source` block is useful because OpenAPI itself does not have enough standard fields to describe MCP transport and discovery metadata.

MCP-discovered tools should enter the same tool definition UI as every other source. Discovery fills the first draft of the function name, description, parameters schema, and response assumptions. Users can then rename, disable, describe, or constrain the exposed operation while the backing MCP tool id remains stable in metadata.

### 8.5 Local Function

Local Function tools should be available only in advanced/developer mode.

Requirements:

1. Preserve existing `tool://` behavior.
2. User enters the fully qualified .NET method/property path.
3. UI validates that the operation path has the expected shape when possible.
4. The source clearly indicates that it depends on server-loaded assemblies.

Generated descriptor shape:

```json
{
  "openapi": "3.0.1",
  "servers": [{ "url": "tool://localhost" }],
  "paths": {
    "AntRunner.Chat.Agent.Invoke": {
      "post": {
        "operationId": "InvokeAgent",
        "requestBody": {
          "required": true,
          "content": {
            "application/json": {
              "schema": { "type": "object" }
            }
          }
        },
        "responses": {
          "200": { "description": "OK" }
        }
      }
    }
  }
}
```

## 9. Runtime Requirements

The runtime should continue to use OpenAPI descriptor parsing and `servers[0].url` scheme dispatch.

Near-term changes:

1. Add scheme-aware labels and validation helpers shared by frontend and backend.
2. Preserve existing `ToolCaller.ActionType` behavior for `client`, `tool`, `sandbox`, and web schemes.
3. Add a new MCP action type only if the chosen MCP descriptor strategy uses `mcp://`.
4. Add an `McpToolService` only for server-reachable MCP.
5. Continue routing client-local MCP through the existing `ClientHandled` external tool bridge.

If `mcp://` is added, the scheme map becomes:

| Scheme | Action type |
|--------|-------------|
| `client` | ClientHandled |
| `tool` | LocalFunction |
| `sandbox` | SandboxHandled |
| `mcp` | McpHandled |
| anything else | WebApi |

This preserves the core design: the OpenAPI server URL scheme remains the runtime selector.

## 10. Storage Requirements

The first implementation can keep using `AssistantOpenApiSchema` and `AssistantOpenApiOperation`.

Recommended near-term additions:

1. Add optional source-kind metadata to the descriptor with `x-guideants-tool-source`.
2. Derive source kind from `servers[0].url` when metadata is absent.
3. Treat `ApiHost` as a compatibility field and populate it with the scheme-aware connector key.
4. Keep auth provider lookup compatible with existing host/authority matching for web APIs.
5. For non-web schemes, avoid presenting `ApiHost` as a web host in the UI.

A later storage cleanup can introduce clearer names while still storing OpenAPI descriptors:

```text
AssistantToolSource
- Id
- AssistantId
- Name
- SourceKind
- ConnectorKey
- SpecificationJson
- SourceMetadataJson
- AuthProviderId

AssistantToolOperation
- Id
- ToolSourceId
- OperationId
- Method
- Path
- SchemaFragmentJson
- Enabled
```

This is a storage cleanup only. It does not change the canonical descriptor model.

## 11. Validation Rules

Common validation:

1. Descriptor must have `openapi` or `swagger`.
2. Descriptor must have `info.title`.
3. Descriptor must have a usable server URL.
4. Descriptor must have `paths`.
5. Every operation must have a unique `operationId`.
6. Source kind must match the server URL scheme.

Scheme-specific validation:

| Scheme | Validation |
|--------|------------|
| `http` / `https` | URL must have authority. Auth can be configured. |
| `client` | Bridge id must be present. Auth hidden or disabled unless bridge permissions are added. |
| `sandbox` | Init module filename must be present. Operation IDs should be valid Python identifiers when possible. |
| `tool` | Operation path should identify a type/member target. Advanced mode only. |
| `mcp` | MCP connection metadata must be present and publishable for the selected runtime. |

## 12. Guide Builder Component Architecture

The implementation should respect the current guide editor structure:

```text
BaseEntityEditor
  EditorTabs
  ToolsTab
    ToolsSelector
    OpenApiSchemas
      OperationEditor
```

The first refactor should keep `ToolsTab` as the owner of the tools subtab and keep the existing controlled props:

```ts
customTools: CustomToolDto[];
onCustomToolsChange(tools: CustomToolDto[]): void;
onValidationChange?(hasErrors: boolean): void;
onDirtyChange?(): void;
```

The proposed component shape is:

```text
ToolsTab
  ToolsSelector
  ToolSources
    ToolSourceList
    ToolSourceCard
    ToolSourceTypeBadge
    ToolSourceConnectionPanel
      WebApiConnectionEditor
      ClientActionConnectionEditor
      SandboxModuleConnectionEditor
      McpConnectionEditor
      LocalFunctionConnectionEditor
    ToolOperationsPanel
      ToolOperationList
      ToolOperationCard
      ToolOperationEditorModal
        ToolDefinitionForm
        ToolParameterEditor
        ToolResponseSchemaEditor
        ToolExecutionMappingEditor
        ToolDefinitionPreview
        OpenApiFragmentEditor
    ToolSourceAuthPanel
    ToolSourceAdvancedJsonPanel
```

`OpenApiSchemas` can either be renamed to `ToolSources` or become a compatibility wrapper that renders `ToolSources`. The important boundary is that source classification, operation extraction, descriptor generation, and validation should move out of the React component body into testable helpers.

Suggested helper modules:

```text
src/client/src/components/guides/editor/toolSources/
  openApiToolSource.ts
  toolSourceClassification.ts
  toolDefinitionModel.ts
  openApiDescriptorBuilder.ts
  operationFragmentBuilder.ts
  validation.ts
```

These helpers should perform the same core transformations the runtime performs:

1. Parse `servers[0].url`.
2. Classify the scheme.
3. Extract OpenAPI operations.
4. Convert operations into editable tool-definition view models.
5. Convert edited tool-definition view models back into OpenAPI operations.
6. Preserve unknown OpenAPI fields when possible.

This keeps the UI faithful to the runtime and avoids creating a second, incompatible definition system.

## 13. Tool Definition UI Requirements

The operation editor should become a structured editor first and a JSON editor second.

### 13.1 ToolDefinitionForm

Fields:

| Field | Source of truth | Notes |
|-------|-----------------|-------|
| Function name | `operation.operationId` | Validate against tool-call naming constraints and uniqueness. |
| Display summary | `operation.summary` | Prefer summary for the model-facing description. |
| Description | `operation.description` | Optional longer detail. |
| Content type | request body media type | Defaults to `application/json`. |
| Consequential/approval metadata | OpenAPI extension if present | Future-friendly; not required for first slice. |

The form should preview the final description the model receives. Runtime currently prefers `summary` over `description`, so the UI should make that behavior obvious through field order and preview, not hidden documentation.

### 13.2 ToolParameterEditor

The parameter editor should operate on the `ParametersDefinition` shape generated by the tool calling system:

```text
parameters
- type: object
- properties: Record<string, PropertyDefinition>
- required: string[]
```

Each parameter row should support:

| Field | Maps to |
|-------|---------|
| Name | property key |
| Type | `type` |
| Description | `description` |
| Required | membership in `required` |
| Default | `default` |
| Example | `example` |
| Enum values | `enum` |
| Item schema | `items` for arrays |
| Nested properties | `parameters` or object properties where supported |

The editor should expose defaults and single-value enums carefully. Runtime hides request-body properties with defaults or a single enum from the LLM-facing schema and injects them later. The UI should show those fields as "provided by default" rather than making authors wonder why preview output differs from the raw request body.

### 13.3 ToolExecutionMappingEditor

Execution mapping is scheme-specific and should not look like a web endpoint for every tool source.

| Source | Mapping UI |
|--------|------------|
| Web API | HTTP method, path, path/query/header/body parameter placement. |
| Client Actions | Client action key/path and bridge id. |
| Sandbox Module | Python function name, init module, optional resource module location. |
| MCP Connection | MCP tool id, discovered name, selected connection. |
| Local Function | Fully qualified .NET method/property path. |

For web APIs, method/path are first-class. For non-web tools, method/path are still generated into OpenAPI, but they should be treated as backing details unless the user opens Advanced Fragment.

### 13.4 ToolResponseSchemaEditor

The response schema editor should support at least:

1. No structured response.
2. Object response with properties.
3. Array response with item schema.
4. Raw JSON schema advanced mode.

Runtime already uses the `200` response schema to filter web API output when present. The UI should make that response schema editable because it directly affects what the model sees after a tool call.

### 13.5 ToolDefinitionPreview

The preview should call the existing backend preview endpoint or a new equivalent endpoint that runs the same OpenAPI-to-tool-definition path as runtime.

Preview output should include:

1. Generated `ToolDefinition` JSON.
2. Hidden/default-injected parameters, if any.
3. Validation messages.
4. Source scheme and runtime action type.

This is the guardrail that keeps the UI honest: if an author edits a form, they can immediately see the exact function contract that will be sent to the model.

### 13.6 Advanced Fragment

The Advanced Fragment tab should preserve the current `OperationEditor` JSON capability:

```json
{
  "path": "/example",
  "method": "post",
  "operation": {}
}
```

Saving from this tab should re-parse into the structured UI when possible. If the fragment uses OpenAPI features the guided editor does not support, show a "custom fragment" state and continue preserving the JSON.

## 14. Backend Support Requirements

The UI can do simple drafting locally, but the backend should remain the source of truth for runtime-compatible tool previews.

Recommended backend additions:

1. Add a source classification endpoint or DTO field only if frontend classification becomes duplicated or inconsistent.
2. Extend the existing operation preview to return parsed `ToolDefinition` metadata, not only a JSON string.
3. Add a descriptor preview endpoint that accepts a full OpenAPI descriptor and returns source kind, operations, generated tool definitions, and validation messages.
4. Add MCP discovery endpoints that produce operation drafts in the same shape as guided operation editors.
5. Add tests around OpenAPI-to-editable-view-model conversion for web, client, sandbox, local, and MCP descriptors.

The backend should use the existing tool calling code path where possible, especially `OpenApiHelper.GetToolDefinitionsFromJson`, so the editor is not reverse-engineering tool semantics differently from runtime.

Suggested preview response:

```ts
interface ToolDefinitionPreviewResult {
  actionType: 'WebApi' | 'ClientHandled' | 'SandboxHandled' | 'LocalFunction' | 'McpHandled';
  toolDefinition: unknown;
  hiddenParameters: string[];
  responseSchemas: Record<string, unknown>;
  validationMessages: string[];
}
```

## 15. Migration Plan

### Phase 1: Scheme-aware UI over existing data

1. Rename "Web Connectors" to "Tool Sources".
2. Classify existing schemas by `servers[0].url` scheme.
3. Replace "API host" labels with scheme-aware connector labels.
4. Keep the existing JSON editor.
5. Add source badges: Web API, Client Action, Sandbox Module, Local Function.
6. Extract source parsing and operation parsing out of `OpenApiSchemas.tsx` into helper modules.

### Phase 2: Guided creation for existing schemes

1. Add "Add Tool Source" picker.
2. Add guided creation for Web API, Client Actions, and Sandbox Module.
3. Generate OpenAPI descriptors from form inputs.
4. Move raw JSON creation into advanced mode.
5. Preserve import/paste OpenAPI flow.
6. Replace the JSON-first `OperationEditor` with the structured `ToolOperationEditorModal`.

### Phase 3: MCP discovery and descriptor generation

1. Add MCP source type to the picker.
2. Implement MCP connection test and tool discovery.
3. Generate OpenAPI operations from discovered MCP tool schemas.
4. Add refresh and diff UI.
5. Decide and implement `mcp://` runtime support for server-reachable MCP, or route local MCP through `client://`.

### Phase 4: Backend validation and publish checks

1. Add backend validation for source kind and scheme consistency.
2. Add publish-time checks for MCP reachability and local-only restrictions.
3. Add tests for descriptor classification and generated descriptors.
4. Add tests for MCP discovery-to-OpenAPI conversion.

### Phase 5: Optional storage cleanup

1. Introduce clearer source naming in storage if useful.
2. Keep `SpecificationJson` as the canonical runtime descriptor.
3. Provide compatibility reads for existing `AssistantOpenApiSchema` rows.

## 16. Acceptance Criteria

1. A user can add a client-handled tool without writing `client://` JSON manually.
2. A user can add a sandbox Python tool without writing the full OpenAPI descriptor manually.
3. Existing bootstrap descriptors for Worm Commander and Slide Shows render correctly as typed tool sources.
4. Existing saved OpenAPI schemas continue to execute with the same runtime behavior.
5. Advanced users can still paste and edit raw OpenAPI JSON.
6. MCP tools can be discovered, selected, and represented as generated OpenAPI operations.
7. The UI no longer calls non-web connector identifiers "API hosts".
8. Backend validation rejects mismatched or incomplete scheme/source configurations.
9. Every operation can be edited through structured tool-definition controls without requiring direct JSON edits.
10. The preview surface shows the exact generated `ToolDefinition` that the model will receive.
11. The component structure stays inside the existing guide editor flow and keeps `ToolsTab` as the integration point.

## 17. Open Questions

1. Should server-reachable MCP use a new `mcp://` scheme, or should all MCP be mediated through `client://` initially?
2. Which MCP transports are in scope for the first release: streamable HTTP, SSE, stdio, or client bridge only?
3. Where should MCP connection metadata live: entirely in `x-guideants-tool-source`, adjacent database fields, or both?
4. Should generated descriptors be locked unless the user chooses "custom JSON mode"?
5. Should sandbox function discovery inspect Python files automatically, or should the first release rely on manual function entry?
6. How much of nested JSON Schema should the first structured parameter editor support before falling back to advanced schema editing?
7. Should hidden/default-injected parameters be editable in the main parameter table or separated into an "injected values" section?

## 18. Recommended First Slice

Start with the smallest slice that fixes the current usability mismatch:

1. Rename the UI to "Tool Sources".
2. Classify descriptors by server URL scheme.
3. Add scheme-aware labels and source badges.
4. Add guided creation for `client://` and `sandbox://`.
5. Keep generated OpenAPI JSON visible in an Advanced JSON tab.
6. Add a structured tool-definition editor for operation name, description, parameters, required fields, defaults, enums, examples, and response schema.
7. Back the preview with the existing OpenAPI-to-`ToolDefinition` backend path.

That keeps the current runtime intact while making the existing schemes feel intentional instead of hidden inside a web-connector JSON editor.
