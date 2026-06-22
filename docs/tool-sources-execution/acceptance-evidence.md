# Tool Sources Guide Builder — Acceptance Evidence

Last updated: 2026-06-19

Maps proposal [section 16](../tool-sources-guide-builder-proposal.md#16-acceptance-criteria) criteria to code, tests, and docs.

| # | Criterion | Evidence |
|---|-----------|----------|
| 1 | Add client-handled tool without manual `client://` JSON | `AddToolSourcePicker.tsx` Client Actions; `openApiDescriptorBuilder.ts`; `openApiDescriptorBuilder.test.ts` |
| 2 | Add sandbox Python tool without full OpenAPI manual authoring | `AddToolSourcePicker.tsx` Sandbox Module; `openApiDescriptorBuilder.ts`; tests |
| 3 | Bootstrap Worm Commander / Slide Shows render as typed sources | `toolSourceClassification.test.ts`; `toolSourceCardViewModel.test.ts`; bootstrap fixtures in `Resources/bootstrap/` |
| 4 | Existing saved OpenAPI schemas execute with same runtime behavior | No dispatch changes for http/client/sandbox/tool; `ToolCallerTests`; Phase 5 skipped |
| 5 | Advanced users can paste/edit raw OpenAPI JSON | `OpenApiSchemas.tsx` Advanced JSON tab; `x-guideants-custom-descriptor` mode |
| 6 | MCP tools discovered, selected, generated as OpenAPI operations | `McpConnectionPanel.tsx`, `McpToolSourceDiscoveryService.cs`, `mcpToolSource.test.ts` |
| 7 | UI no longer calls non-web identifiers "API hosts" | `CONNECTOR_KEY_LABELS` in `toolSourceClassification.ts`; card view model |
| 8 | Backend validation rejects mismatched/incomplete configs | `ToolSourceValidator.cs`, `ToolSourceValidatorTests.cs` |
| 9 | Operations editable via structured tool-definition controls | `StructuredOperationEditor.tsx` |
| 10 | Preview shows exact generated ToolDefinition for model | Structured preview endpoint; `OpenApiHelper` shared path |
| 11 | Component structure stays in guide editor / ToolsTab | `ToolsTab.tsx` integration boundary unchanged |

## Phase 5

**SKIPPED**: no DB rename, no import/export changes.

## Final verification (2026-06-19)

- Server: 1580 + 198 + 45 tests passed
- Client: 2960 tests passed
