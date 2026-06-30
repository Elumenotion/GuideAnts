# Task — Phase 6: Registry staging + publish gate + Guide Builder UI

> Subagent brief. Execute top to bottom. Return the Report-back contract verbatim.

## Mission

Make `sandbox_subprocess` MCP authorable and safe to publish. Implement stage-on-import /
apply-on-action for scoped sandbox setup (E12), the E16 publish gate (block when scoped
admin state is staged ≠ applied), and the Guide Builder runtime-execution UI (design §7):
`api`/`sandbox_subprocess` only (no client host), required unique `toolNamePrefix` per MCP
source (E11), staged-vs-applied status, and generated `mcp+api://`/`mcp+sandbox://` URLs.

## Read first

- `../mcp-tool-execution-design.md` §3.4, §5.4, §7, §8 (E6, E11, E12, E16), §10.
- `./DECISIONS.md` — E6, E11, E12, E16, Part C.
- `./sandbox-apply-gate.md` §3.5, §3.6.
- `./ui-gate.md` — **the detailed Guide Builder UX contract for this phase**, including
  **§4 Reuse existing mechanisms (do not reinvent)** and **§5 Component decomposition
  contract (anti-monolith)**. This is the primary UX spec; §4 and §5 are mandatory, not
  advisory.
- `../tool-sources-execution/ui-gate.md` (the established base Tool Sources UX contract —
  source card, picker, validation, accessibility, responsive, loading/empty/error states;
  `./ui-gate.md` §3.10 inherits it).
- Existing client mechanisms to reuse (read before writing any new component):
  `src/client/src/components/common/ConfirmationDialog.tsx`,
  `.../common/Toast.tsx`, `.../LoadingSpinner.tsx`,
  `.../guides/editor/EnvironmentSecretRefField.tsx`,
  `.../guides/editor/toolSources/environmentVariableRefs.ts`,
  `.../guides/editor/toolSources/toolSourceCardViewModel.ts`,
  `.../guides/editor/toolSources/mcpToolSource.ts`,
  `.../features/guideantsGuide/guideantsAppBridge.ts` (`SandboxAdmin*`/`GetSetupStatus`/
  `Apply`), and the publish path (`PublishGuideDialog.tsx`, `GuidesDashboard`).
- `./codeql-gate.md` §6 (client rendering + secret masking).
- Touchpoints:
  - `src/client/src/components/guides/editor/toolSources/*`
    (`McpConnectionPanel.tsx`, picker, source card, classification)
  - `src/server/GuideAntsApi/Services/Guides/*` (publish path) +
    `ToolSourceValidator.cs`
  - `src/server/ScriptExecutionAgent/AdminSetupStatusRuntime.cs`,
    `AdminScopeAppliedStateRuntime.cs`, `AdminApplyJobRuntime.cs`
    (`setup-status` hashes, scoped apply)

## Preconditions

- Phase 5 gate green (`sandbox_subprocess` executes end-to-end).
- E6/E11/E12/E16 locked.

## Guardrails (hard)

- **Stage-on-import; apply-on-action (E12).** Import/Save writes scoped setup state and
  never mutates the live sandbox. Apply only happens on an explicit Test connection /
  Install packages action **with confirmation** (it mutates the sandbox for all notebooks
  using the guide in the project).
- **E16 publish block is exact:** block publish iff a `sandbox_subprocess` MCP source
  exists and scoped admin state is staged ≠ applied (`setup-status` hash mismatch). Do not
  over-block (`api`-only guides, or applied state) or under-block (drift after edit). It is
  not a per-surface decision (design §5.4).
- **Unique `toolNamePrefix` per MCP source on the same assistant (E11)** — validate and
  surface inline; default `mcp` collides across servers.
- **No client-host option** in the runtime-execution control (D1). Generated URL shows
  `mcp+api://` or `mcp+sandbox://` only. The `client_bridge` transport, the
  `client://mcp-bridge-*` URL builder, and the "client-bridge-first" info copy are
  **removed** (ui-gate §2 deltas C1–C9; grep-proven).
- **Warn, don't infer (E6):** loopback URLs under `api` show a Docker-reachability warning;
  never rewrite the URL or change mode based on hostname.
- **Mask secrets** in the panel — reuse `EnvironmentSecretRefField` +
  `environmentVariableRefs.ts` (`{{secret:NAME}}` ref syntax + masked display); never render
  resolved values and never add a new masking primitive.
- **Reuse existing mechanisms (ui-gate §4) — do not reinvent.** Apply/discard prompts use
  the shared `ConfirmationDialog`; status/diff chips use the `toolSourceCardViewModel.ts` /
  `mcpToolSource.ts` className helpers; notifications use `useToast`; loading uses
  `LoadingSpinner` / the inline `FaSpinner` button pattern. **Sandbox apply + setup-status
  must call the existing `guideantsAppBridge.ts` `SandboxAdmin*`/`GetSetupStatus`/`Apply`
  bridge** — no new endpoint or second admin client. Publish-block surfaces through the
  **existing** publish validation/error path, not a new modal. A duplicate dialog/spinner/
  badge/admin-client is an automatic FAIL.
- **Anti-monolith decomposition (ui-gate §5) — enforced.** Follow the directory's split
  (presentational panel + pure `*.ts` helpers with `__tests__/` + view-model + types +
  side-effect hook). **`McpConnectionPanel.tsx` net line count must not grow**; extract
  test/discover/apply orchestration into `useMcpConnection.ts` and the new fields/status
  into separate sub-panels (`McpHttpConnectionFields`, `McpPackageConnectionFields`,
  `McpSandboxSetupStatus`, `McpDiscoveryResults`). No business logic in JSX/effects; new
  presentational `.tsx` ≤ ~250 lines; one component per file (`project-rules.mdc`).
- MCP panel honors the established UX contract: explicit loading/empty/error/retry states,
  accessibility, responsive (`ui-gate.md` §3.10 ← `tool-sources-execution/ui-gate.md`).
- No new silent `catch {}`; staged/applied mismatch and validation failures are explicit.

## Tasks

1. **Registry import staging (§3.4):** on import/save of a `sandbox_subprocess` source,
   write scoped `requirements.txt`, `apt-packages.txt`, `install-scripts.json`, and
   Environment tab entries — staged only.
2. **Apply-on-action:** wire Test connection / Install packages to the scoped admin apply
   (`AdminApplyJobRuntime`) with a confirmation dialog; reflect applied state via
   `setup-status`.
3. **E16 publish gate:** in the publish path, compute the staged-vs-applied `setup-status`
   hash for each `sandbox_subprocess` source's scope and block publish on mismatch with a
   clear, actionable message. Add backend validation + a publish-check test matrix
   (sandbox-apply gate §3.6).
4. **Guide Builder UI — implement `ui-gate.md` in full:** apply current→target deltas
   C1–C9 (model fields, runtime-execution control replacing the transport select, removed
   client-bridge copy/URL builder, `mcp+api://`/`mcp+sandbox://` previews); reshape the
   panel per mode; package/URL/headers/env fields; staged-vs-applied status; confirmed
   apply with blast-radius copy (`applying`/`apply-failed` states); E6 loopback warning;
   secret masking; and the **migration notice** for opened `client://mcp-bridge-*` sources
   (ui-gate §3.8).
5. **Multiple MCP sources (E11):** enforce unique `toolNamePrefix` + unique schema name
   across MCP sources on the same assistant; surface collisions inline and block save.
6. Add tests: staging writes without apply; apply transitions `setup-status`; publish
   blocked/unblocked transitions; prefix-collision validation; UI state model (idle/test/
   connected/discovery/apply states) and accessibility.

## Files in scope

Frontend (follow the ui-gate §5 decomposition — slim shell + sub-panels + hook + tested
helpers, not one growing panel):

- `.../toolSources/McpConnectionPanel.tsx` — slim composition shell (must not grow)
- `.../toolSources/useMcpConnection.ts` *(new)* — state machine + test/discover/apply
  orchestration (calls `guideantsAppBridge.ts` sandbox-admin bridge)
- `.../toolSources/McpHttpConnectionFields.tsx` *(new)* — URL/headers/E6 warning
- `.../toolSources/McpPackageConnectionFields.tsx` *(new)* — package/env fields
- `.../toolSources/McpSandboxSetupStatus.tsx` *(new)* — staged/applied + Apply
  (`ConfirmationDialog`)
- `.../toolSources/McpDiscoveryResults.tsx` *(new)* — discovered tools + diff chips
- `.../toolSources/mcpRuntimeMode.ts` *(new)* — pure mode/URL/E6 derivation (tested)
- `.../toolSources/mcpToolSource.ts`, `.../toolSourceCardViewModel.ts`,
  `.../AddToolSourcePicker.tsx` — extend (C3/C6/C7/C8 deltas, runtime sub-badge, connector
  key, migration notice)
- `.../toolSources/__tests__/*` — helper + hook + interaction coverage
- Reused (read-only, do not fork): `common/ConfirmationDialog.tsx`, `common/Toast.tsx`,
  `LoadingSpinner.tsx`, `EnvironmentSecretRefField.tsx`, `environmentVariableRefs.ts`,
  `features/guideantsGuide/guideantsAppBridge.ts`, publish path files.

Backend:

- `src/server/GuideAntsApi/Services/Guides/*` (publish gate + staging hooks)
- `src/server/GuideAntsApi/Services/Guides/ToolSourceValidator.cs` (prefix uniqueness,
  scheme/mode consistency)
- `src/server/ScriptExecutionAgent/Admin*Runtime.cs` (scoped staged/applied + apply job)
- Tests: `src/server/GuideAntsApi.Tests/Services/Guides/*`, ScriptExecutionAgent tests.

Out of scope:

- Runtime executor internals (Phases 2, 5).
- Wire mapping (Phases 3, 4).

## Self-verification

```bash
cd src/server && dotnet build GuideAntsApi.sln && dotnet test GuideAntsApi.sln
cd src/client && npm run build && npm test -- --run
```

Run required gates:

- `sandbox-apply-gate.md` §3.5, §3.6 (+ re-confirm §3.1).
- `ui-gate.md` §6 phase gate checks (deltas C1–C9 grep-clean, runtime-execution control,
  HTTP + sandbox modes, source card, prefix uniqueness, publish-block surface, a11y,
  responsive, **reuse §4, decomposition §5**) + §7 test matrix.
- `codeql-gate.md` full diff gate.

## Definition of Done

- [ ] Import/Save stages scoped setup; never auto-applies (E12).
- [ ] Test/Install applies with confirmation; `setup-status` reflects applied state.
- [ ] E16 publish gate blocks/unblocks exactly per the staged-vs-applied matrix.
- [ ] Builder UI: ui-gate deltas C1–C9 done (grep-clean: no `client_bridge`/`mcp-bridge-`/
      "client-bridge-first"); runtime mode (no client host); unique `toolNamePrefix`;
      staged/applied status; confirmed apply; generated `mcp+api://`/`mcp+sandbox://` URL;
      E6 loopback warning; migration notice for old sources.
- [ ] Secrets masked; UX contract (states/a11y/responsive) honored.
- [ ] Build/tests green; sandbox-apply + UI gates pass; CodeQL diff clean.

## Report-back contract (return exactly this)

```text
PHASE 6 REPORT
- Stage-on-import (no auto-apply): <pass/fail>
- Apply-on-action (Test/Install + confirmation): <pass/fail>
- E16 publish gate matrix (blocked/unblocked): <pass/fail + cases>
- Runtime-mode UI (api/sandbox only, no client host): <pass/fail>
- Unique toolNamePrefix enforcement (E11): <pass/fail>
- Generated URL display + E6 loopback warning: <pass/fail>
- Secret masking in panel: <pass/fail>
- ui-gate deltas C1–C9 + grep clean: <pass/fail>
- Migration notice for client://mcp-bridge-* sources: <pass/fail>
- Reuse (ui-gate §4): ConfirmationDialog=<y/n> secret-ref=<y/n> chips=<y/n> toast=<y/n> sandbox-bridge=<y/n> publish-path=<y/n> duplicates-introduced=<none/list>
- Decomposition (ui-gate §5): McpConnectionPanel.tsx line delta=<+/-N> new-files=<list w/ line counts> logic-in-helpers-hook=<y/n>
- SANDBOX APPLY GATE: stage/apply=<p/f> publish-block=<p/f> shared-executor=<p/f>
- UI GATE (Guide Builder MCP): runtime-control=<p/f> http-mode=<p/f> sandbox-mode=<p/f> source-card=<p/f> prefix-uniqueness=<p/f> publish-block-surface=<p/f> a11y=<p/f> responsive=<p/f>
- CODEQL: new-vs-baseline=<count → ids/files or none>
- Verification: server-build=<p/f> server-tests=<counts> client-build=<p/f> client-tests=<counts>
- Files touched: <exhaustive list>
- Deviations / surprises: <list or "none">
```
