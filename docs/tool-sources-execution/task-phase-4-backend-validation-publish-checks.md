# Task - Phase 4: Backend validation and publish checks

> Subagent brief. Execute top to bottom. Return the Report-back contract verbatim.

## Mission

Make backend validation and preview the source of truth for Tool Sources across all
in-scope schemes. Add publish-time checks for unsupported configurations and expose
runtime-compatible preview metadata to the editor.

## Read first

- `../tool-sources-guide-builder-proposal.md` sections 9, 10, 11, 13.5, 14, 15
  (Phase 4), and 16.
- `./DECISIONS.md` (all locked values, especially D1-D3 and D7).
- `./ui-gate.md` section 2.8 and phase checks in section 3.
- `./runtime-parity-gate.md`, `./codeql-gate.md`.
- Existing backend preview path:
  - `src/server/GuideAntsApi/Endpoints/GuidesEndpoints.cs`
  - `src/server/GuideAntsApi/Services/Guides/GuidesService.cs`
  - `src/server/AntRunner.Chat/AntRunner.ToolCalling/Functions/OpenApiHelper.cs`

## Preconditions

- Phase 3 gate green.
- MCP strategy decisions and transport scope are locked.

## Guardrails (hard)

- Backend remains the only source of truth for runtime-compatible preview.
- Do not fork logic so frontend and runtime diverge.
- Validation failures must be explicit and actionable.
- No secret-bearing fields in preview payloads or validation output.
- Publish checks must enforce local-only restrictions for unsupported deployments.
- Backend validation errors must map to stable frontend error contracts (field +
  message + severity where applicable).

## Tasks

1. Add/extend backend descriptor preview endpoint returning structured metadata:
   - source kind
   - action type
   - generated tool definition(s)
   - hidden/default-injected parameters
   - response schema view
   - validation messages.
2. Add backend validation for:
   - required OpenAPI fields
   - unique `operationId`
   - source-kind/scheme consistency
   - scheme-specific requirements
   - MCP metadata completeness and transport constraints.
3. Add publish-time validation checks for unsupported MCP configurations based on
   locked D1/D2 decisions.
4. Ensure preview and validation logic call shared runtime-compatible helper paths
   (no duplicated inconsistent parser behavior).
5. Update frontend API typings and UI validation surfaces to consume new preview
   metadata/messages.
6. Add tests for classification and validation matrices across in-scope schemes.

## UI integration details (required)

1. Validation payload contract for UI
   - include machine-readable code
   - include human-readable message
   - include optional field path
   - include severity (`error`, `warning`).
2. Preview payload contract for UI
   - include source kind + action type
   - include hidden/default-injected parameter list
   - include tool definition payload
   - avoid exposing sensitive values.
3. Error mapping behavior
   - field-targeted errors render inline in the corresponding editor section.
   - global errors render in summary region.
   - publish-blocking errors are clearly marked.

## Files in scope

- `src/server/GuideAntsApi/Endpoints/GuidesEndpoints.cs`
- `src/server/GuideAntsApi/Services/Guides/GuidesService.cs`
- `src/server/GuideAntsApi/Models/Guides/GuideDto.cs` (or sibling DTO files)
- Shared helper modules in `src/server/GuideAntsApi/Services/Guides/*`
- `src/server/AntRunner.Chat/AntRunner.ToolCalling/Functions/OpenApiHelper.cs`
  (only if needed for shared validation/preview behavior)
- `src/client/src/services/api.ts`
- `src/client/src/types/guides.ts`
- Related tests in server/client suites.

Out of scope:

- Storage model migration cleanup (Phase 5).
- Docs/final acceptance wrap-up (Phase 6).

## Self-verification

```bash
cd src/server && dotnet build GuideAntsApi.sln && dotnet test GuideAntsApi.sln
cd src/client && npm run build && npm test -- --run
```

Run required gates:

- UI gate integration checks (`ui-gate.md` phase checks).
- Runtime parity full checks (`runtime-parity-gate.md` section 3).
- CodeQL full diff (`codeql-gate.md`).

## Definition of Done

- [ ] Backend validation rejects mismatched/incomplete source configurations.
- [ ] Preview endpoint provides runtime-compatible structured metadata.
- [ ] Frontend consumes structured preview/validation responses.
- [ ] Validation payloads map cleanly to UI inline + summary error surfaces.
- [ ] Publish checks block unsupported MCP configurations.
- [ ] Tests cover all in-scope schemes and key mismatch paths.
- [ ] Build/tests green, runtime parity pass, and CodeQL diff clean.

## Report-back contract (return exactly this)

```text
PHASE 4 REPORT
- Preview endpoint contract delivered (fields): <list>
- Backend validation rules implemented: <list>
- Publish-time MCP checks implemented: <yes/no + details>
- Shared runtime-compatible path reused (OpenApiHelper/etc): <yes/no>
- Frontend preview/validation integration updated: <paths>
- UI validation payload contract (code/message/field/severity): <implemented/not implemented>
- UI GATE: validation-surface-mapping=<pass/fail>
- RUNTIME PARITY GATE: classification=<pass/fail> generation=<pass/fail> preview=<pass/fail> compatibility=<pass/fail>
- CODEQL: new-vs-baseline=<count -> ids/files or none>
- Verification: server-build=<pass/fail> server-tests=<counts> client-build=<pass/fail> client-tests=<counts>
- Files touched: <exhaustive list>
- Deviations / surprises: <list or "none">
```
