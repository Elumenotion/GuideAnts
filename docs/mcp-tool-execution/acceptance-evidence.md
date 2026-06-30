# MCP Tool Execution — Acceptance Evidence

Last updated: 2026-06-29 (Phase 7 close-out)

Maps every locked decision (`DECISIONS.md` D1/D2 revised + E1–E17), design §9 phase
exits, and design §10 non-goals to **passing tests or file references**. No prose-only
claims.

---

## Design §9 phase exits

| Exit | Evidence |
|------|----------|
| **Phase A** — return-policy tool call completes with assistant text in notebook | `McpToolExecutorTests.StreamableHttp_CallToolAsync_ReturnPolicy_HappyPath` (MCP `tools/call` over streamable HTTP returns policy text); `McpBackingToolResolverTests` (`return_policy` backing id); notebook path enters `ThreadRun` via `ConversationStreamEngine` → `ThreadRun.ExecuteAsync` (`McpRuntimeParityAcceptanceTests.Notebook_stream_engine_delegates_tool_execution_to_ThreadRun`) |
| **Phase B** — wire hardening (Responses + Anthropic live; duplicate buffers removed) | `WireStreamAdapterTests` (incremental Chat/Responses/Anthropic); `PublishedOpenAiWireHandlersTests` (live SSE + `stream:false` fold); `WireConversationExecutor.CollectWireConversationResultAsync` fold-only (`WireStreamAdapterTests.CollectWireConversationResultAsync_Folds_token_stream_to_concatenated_text`) |
| **Phase C** — registry PyPI/npm stdio server completes tool call via notebook **or** published surface | `McpStdioEndpointTests.McpStdio_happy_path_initialize_tools_call_teardown` (stdio `initialize` → `tools/call` → teardown, E7); `McpSandboxSetupComposerTests.Compose_npm_package_writes_install_script_and_node_apt` + `Compose_pypi_package_writes_requirements_line` (registry staging); published surface shares executor: `McpRuntimeParityAcceptanceTests` (wire/embed → `SendMessageStreamAsync` → `ThreadRun.DoToolCalls`) |

---

## Part A — Revised cross-folder decisions

| ID | Decision | Evidence |
|----|----------|----------|
| **D1 (revise)** | API-only; remove `client://` MCP path | `ToolCaller.cs` dispatches `mcp+api`/`mcp+sandbox` only (`ToolCallerTests.ActionType_MapsUriSchemesToExpectedKinds`); grep: no `client://mcp-bridge` in production `src/server` (only migration tests + `McpDescriptorMigrator.cs` comment); `McpRuntimeParityAcceptanceTests.ToolCaller_dispatch_has_no_client_mcp_bridge_scheme`; UI grep clean (`toolSources/` — Phase 6) |
| **D2 (revise)** | `streamable_http` + `stdio`; drop `client_bridge` | `McpDescriptorMigratorTests` (`discoveryTransport`: `streamable_http` \| `stdio`); `ToolSourceValidatorTests.ValidateDescriptor_Rejects_legacy_mcp_transport`; `McpStdioEndpointTests` (stdio transport); client `mcpToolSourceTypes.ts` (`discoveryTransport` only) |

---

## Part B — Locked design decisions (E1–E17)

| ID | Decision | Evidence |
|----|----------|----------|
| **E1** | Descriptor-driven defaults: remotes → `api`, packages → `sandbox_subprocess` | `McpDescriptorMigratorTests.Migrate_Rewrites_*`; `openApiDescriptorBuilder.ts` + `mcpToolSource.test.ts` (`applyMcpDiscoveryToSpec` writes `runtimeExecution: api`); `McpSandboxConnectionReaderTests` |
| **E2** | API URL scheme `mcp+api://{bridgeId}` | `McpDescriptorMigratorTests.BuildMcpServerUrl_Uses_locked_schemes`; `mcpToolSource.test.ts` / `mcpRuntimeMode.test.ts` (`buildMcpServerUrl` / `buildMcpDispatchUrl`); `ToolCallerTests.ActionType_MapsUriSchemesToExpectedKinds` |
| **E3** | API-only everywhere; one executor on notebook, embed, wire | `McpRuntimeParityAcceptanceTests` (notebook `ConversationStreamEngine` → `ThreadRun`; wire `WireConversationExecutor` → `SendMessageStreamAsync`; embed `PublishedGuidesEndpoints` → `SendMessageStreamAsync` + shared fold); `McpToolExecutionBridge` → `IMcpToolExecutor` |
| **E4** | Save rewrite + publish backfill + dev script; no compat path | `McpDescriptorMigratorTests` (full matrix); `ToolSourceValidatorTests.NormalizeDescriptor_Migrates_legacy_client_bridge_spec`; `McpRuntimeParityAcceptanceTests.NormalizeDescriptor_Migrates_legacy_sandbox_package_round_trip`; `GuidesPublishingEndpoints.cs` (`BackfillGuideSchemasAsync`); `scripts/migrate-mcp-descriptors.py` |
| **E5** | Per-call client + per-call timeout; no product rate limits | `McpToolExecutorTests.StreamableHttp_CallToolAsync_TimesOut_PerCall`; `McpStreamableHttpToolClient.cs` (new client per call — file ref) |
| **E6** | Localhost warn in builder only; never infer mode from hostname | `mcpRuntimeMode.test.ts` (`isLoopbackMcpApiUrl`); `McpHttpConnectionFields.tsx` (loopback warning UI) |
| **E7** | Per-call stdio spawn v1 | `McpStdioEndpointTests.McpStdio_happy_path_initialize_tools_call_teardown`; `McpStdioEndpointTests.McpStdio_spawn_failure_returns_explicit_error` |
| **E8** | Sandbox URL scheme `mcp+sandbox://{bridgeId}` | `McpDescriptorMigratorTests.Migrate_Rewrites_package_descriptor_to_mcp_sandbox`; `mcpRuntimeMode.test.ts` |
| **E10** | Node in full + slim `guideants-ai` image | `docker/build/guideants-ai/Dockerfile.cpu` L118–122; `docker/build/guideants-ai/Dockerfile.slim` L81–85 |
| **E11** | Unique `toolNamePrefix` + schema name | `mcpPhase6.test.ts` (`validateMcpToolNamePrefixCollision`); `ToolSourceValidatorTests` (publish validation for duplicate prefix) |
| **E12** | Stage on import; apply on explicit action | `McpSandboxSetupComposerTests`; client `mcpSandboxSetupComposer.test.ts`; `useMcpConnection.ts` + `McpSandboxSetupStatus.tsx` (apply confirmation); `McpSandboxSetupStagingService.cs` |
| **E13** | Thin wire adapter; live streaming all facades | `WireStreamAdapter.cs`; `WireStreamAdapterTests`; `PublishedOpenAiWireHandlersTests` (Chat/Responses/Anthropic live + fold) |
| **E14** | Opaque MCP on wire; no `tool_calls`/`tool_use` in v1 | `WireStreamAdapterTests.WriteOpenAiResponsesSseAsync_Does_not_surface_tool_calls_for_text_only_stream`; `WriteAnthropicMessagesSseAsync_Does_not_surface_tool_use_for_text_only_stream`; real MCP HTTP: `McpToolExecutorTests` (server-side only, no wire exposure) |
| **E15** | Same sandbox executor on notebook, embed, wire | `McpRuntimeParityAcceptanceTests` (all three surfaces → `SendMessageStreamAsync` → `ThreadRun`); `McpSandboxExecutorTests.McpToolExecutionBridge_Exposes_shared_sandbox_executor_entrypoint` |
| **E16** | Block publish when sandbox staged ≠ applied | `McpSandboxPublishGateServiceTests`; `GuidesPublishingEndpoints.cs` publish gate; client `mcpPhase6.test.ts` + publish path in `PublishGuideDialog` |
| **E17** | Egress proxy out of scope | Documented in `tool-sources-authoring.md` §MCP non-goals; no egress-proxy implementation in `src/server/GuideAntsApi/Services/Mcp/` |

---

## Part C — Frozen invariants

| Invariant | Evidence |
|-----------|----------|
| OpenAPI descriptor canonical; MCP metadata in `x-guideants-tool-source` | `McpToolSourceMetadata.cs`; `mcpToolSource.ts`; no new MCP DB columns |
| No `pending_client_tool` for MCP | `McpToolExecutorTests.McpApi_ActionType_IsNotClientHandled_SoTurnDoesNotPauseForClient`; `McpSandboxExecutorTests.McpSandbox_ActionType_IsNotClientHandled_SoTurnDoesNotPauseForClient`; `WireStreamAdapterTests` (`PendingClientTool` false for text streams) |
| No fallback masking | `McpStdioEndpointTests.McpStdio_rejects_shell_string_command_injection_fields`; explicit errors in `McpToolExecutor.cs` / `ThreadRun.cs` MCP catch blocks (message only, no downgrade) |
| Secrets never leak | `McpSecretTemplateResolverTests`; `McpSecretTemplateResolverEnvironmentTests`; `McpSandboxExecutorTests.ResolveEnvironmentVariables_does_not_log_resolved_secrets`; client `mcpPhase6.test.ts` (secret masking) |
| One published runtime (wire = adapter over `SendMessageStreamAsync`) | `McpRuntimeParityAcceptanceTests.Wire_path_uses_SendMessageStreamAsync_as_single_engine_entry`; `PublishedOpenAiChatWireHandler.cs` uses `WireStreamAdapter` |
| Sandbox scope `projectId + guideId` | `McpStdioEndpointTests.McpStdio_scopes_guideId_in_request`; `McpSandboxAdminApiClient.cs` |
| No host-local MCP | Design §10 + authoring docs; builder offers `api`/`sandbox_subprocess` only (`ui-gate.md` grep clean) |

---

## Design §10 non-goals (v1)

| Non-goal | Evidence (absence or explicit doc) |
|----------|-----------------------------------|
| `client_host` / `client://` MCP execution | Removed from dispatch + UI; migration rewrites legacy descriptors |
| Browser-direct MCP; hostname inference | No browser MCP client; `isLoopbackMcpApiUrl` warns only (E6) |
| MCP auth via `AssistantAuthProvider` | `McpToolExecutor` uses `DeserializeForExecution` / `McpSecretTemplateResolver` |
| Wire exposure of MCP `tool_calls` / `tool_use` | `WireStreamAdapterTests` (no tool_calls/tool_use on text streams) |
| OCI MCP without install-script support | `ToolSourceValidator` publish checks; registry types npm/pypi only in UI |
| Per-connection rate limits; egress proxy | No rate-limit middleware on MCP paths; E17 locked out of scope |

---

## E2E one-executor matrix (Phase 7 cross-cutting)

| Surface | `mcp+api://` | `mcp+sandbox://` |
|---------|--------------|------------------|
| Notebook | `ConversationStreamEngine` → `ThreadRun` → `McpToolExecutionBridge.ExecuteMcpApiTool` (`McpRuntimeParityAcceptanceTests`) | Same bridge → `ExecuteMcpSandboxTool` → `McpSandboxExecutor` |
| Embed | `PublishedGuidesEndpoints` → `SendMessageStreamAsync` (`McpRuntimeParityAcceptanceTests.Embed_invoke_path_*`) | Same path |
| Wire | `WireConversationExecutor` → `SendMessageStreamAsync` (`McpRuntimeParityAcceptanceTests.Wire_path_*`) | Same path |

HTTP happy-path execution: `McpToolExecutorTests.StreamableHttp_CallToolAsync_ReturnPolicy_HappyPath`  
Stdio happy-path execution: `McpStdioEndpointTests.McpStdio_happy_path_initialize_tools_call_teardown`

---

## Migration round-trip (`client://mcp-bridge-*` → `mcp+api` / `mcp+sandbox`)

| Case | Evidence |
|------|----------|
| HTTP remote → `mcp+api://` | `McpDescriptorMigratorTests.Migrate_Rewrites_streamable_http_to_mcp_api`; `ToolSourceValidatorTests.NormalizeDescriptor_Migrates_legacy_client_bridge_spec` |
| Client bridge (no package) → `mcp+api://` | `McpDescriptorMigratorTests.Migrate_Rewrites_client_bridge_without_package_to_api` |
| Package → `mcp+sandbox://` | `McpDescriptorMigratorTests.Migrate_Rewrites_package_descriptor_to_mcp_sandbox`; `McpRuntimeParityAcceptanceTests.NormalizeDescriptor_Migrates_legacy_sandbox_package_round_trip` |
| Idempotent re-migration | `McpDescriptorMigratorTests.Migrate_Is_idempotent_for_modern_descriptor` |
| Client migration notice | `mcpPhase6.test.ts` (`isLegacyClientBridgeMcpSource`) |

---

## Gate ledgers (final)

See `STATUS.md` for runtime-parity, wire-streaming, sandbox-apply, ui-gate, and CodeQL final-pass results recorded 2026-06-29.
