# UI Gate — Guide Builder MCP Runtime-Execution Authoring (MCP Tool Execution)

Companion to `00-orchestration.md`.

This is the concrete UX contract for the **Guide Builder extensions** required by
[`../mcp-tool-execution-design.md`](../mcp-tool-execution-design.md) §3 and §7. It exists
because the shipped MCP authoring UI was built for the **removed** client-bridge model
(D1 old) and must be reworked for **API-only** execution (`api` / `sandbox_subprocess`).
This gate makes that rework verifiable, and pins the "related concerns" — migration of
already-authored sources, the apply/publish blast-radius flow, secret masking, and prefix
uniqueness.

> Scope boundary (locked with the user): the **notebook chat consumer** is **out of
> scope** — clean separation of concerns means once server-side tool calling works, chat
> rendering follows. This gate covers **only the Guide Builder authoring surface** (the
> Electron/React editor under `src/client/.../guides/editor/toolSources/`).

---

## 1. Gate intent

Pass when all are true:

- An author can create and configure both MCP shapes — **HTTP remote** (`api`) and
  **stdio package** (`sandbox_subprocess`) — without hand-editing raw JSON.
- The UI never offers a client-host / client-bridge runtime option, and never generates a
  `client://mcp-bridge-*` URL. Generated URLs are `mcp+api://` or `mcp+sandbox://` only.
- `runtimeExecution` and `discoveryTransport` are distinct, visible, and consistent with
  the descriptor the editor writes.
- Sandbox setup is clearly **staged vs applied**, applying is an explicit, confirmed,
  blast-radius-aware action, and publish is blocked with an actionable message when staged
  ≠ applied (E16).
- Secrets are masked; `toolNamePrefix` collisions are caught inline; loopback `api` URLs
  warn (E6).
- Existing client-bridge MCP sources opened in the editor surface as **migrated**, not
  silently broken.

---

## 2. Current → target deltas (the concrete rework)

The subagent must treat these as required changes, not optional polish. Each maps to a
shipped artifact under `src/client/src/components/guides/editor/toolSources/`.

| # | File / surface | Current (client-bridge) | Target (API-only) |
|---|---|---|---|
| C1 | `mcpToolSourceTypes.ts` `McpTransport` | `'streamable_http' \| 'client_bridge'` | `discoveryTransport: 'streamable_http' \| 'stdio'` **plus** `runtimeExecution: 'api' \| 'sandbox_subprocess'` (distinct fields, design §3.1). `client_bridge` removed. |
| C2 | `mcpToolSourceTypes.ts` `McpToolSourceMetadata` | `{ kind, transport, url?, bridgeId?, toolNamePrefix?, headers? }` | add `runtimeExecution`, `discoveryTransport`, `package?` (`registryType`/`identifier`/`command`/`args`), `environmentVariables?` (`name` + `secretRef`). |
| C3 | `mcpToolSource.ts` `buildMcpBridgeServerUrl` | emits `client://mcp-bridge-{id}` | emit `mcp+api://{bridgeId}` or `mcp+sandbox://{bridgeId}` per `runtimeExecution` (E2, E8). |
| C4 | `McpConnectionPanel.tsx` Transport `<select>` | options `Streamable HTTP` + `Client bridge (host-local MCP)` | replace with a **Runtime execution** control (`api` / `sandbox_subprocess`) that reshapes the form; `discoveryTransport` derived/shown read-only (remote→`streamable_http`, package→`stdio`). No `client_bridge` option. |
| C5 | `McpConnectionPanel.tsx` info box | teal box: *"route through `client://mcp-bridge-…` … (D1 client-bridge-first)"* | remove. Replace with mode-appropriate guidance (server-side HTTP execution, or sandbox package execution). |
| C6 | `McpConnectionPanel.tsx` `client_bridge` hint block | "requires a connected client host…" | remove. |
| C7 | `AddToolSourcePicker.tsx` MCP option description | *"…expose them via client bridge."* | *"Connect to an MCP server or registry package; tools execute server-side."* |
| C8 | generated-URL preview (`<p>` under bridge id) | shows `client://mcp-bridge-…` | shows `mcp+api://` / `mcp+sandbox://`. |
| C9 | panel state machine | `idle\|testing\|connected\|discovering\|discovery-failed` | add `applying` + `apply-failed` (sandbox setup); add `staged`/`applied` setup status display. |

A **grep gate** confirms the removal: no `client_bridge`, no `mcp-bridge-`, and no
"client-bridge-first" string remains in the toolSources directory after Phase 6.

**Phase ownership:** the *model* fields in C1/C2 (`runtimeExecution`, `discoveryTransport`,
`package`, `environmentVariables`) are introduced with the descriptor model in **Phase 1**
(client typings travel with the backend DTO). The *rendering* — the runtime-execution
control, mode-reshaped form, removed client-bridge copy/URL builder, status/apply/publish
surfaces, and migration notice — is owned by **Phase 6** and gated here. If Phase 1 left
the typings incomplete, that is a Phase 1 deviation, not a Phase 6 workaround.

---

## 3. Required UI contract

### 3.1 Add Tool Source picker

- MCP option stays (`mcp-connection`); description reworded (C7). Single click creates an
  MCP source defaulting to `runtimeExecution: api` (descriptor-driven default, E1).
- Focus lands in the first required field (bridge id / server URL) after create
  (existing behavior — keep).

### 3.2 Runtime-execution control (replaces Transport select — C4)

- A clearly-labeled control selects **Runtime execution**: `API (server-side HTTP)` or
  `Sandbox subprocess (registry package)`. **No third option.**
- Switching mode reshapes the panel:
  - `api` → MCP server **URL** field (required), optional **HTTP headers** (secret-ref or
    literal — keep existing `EnvironmentSecretRefField` pattern).
  - `sandbox_subprocess` → **Package** fields: `registryType` (npm | pypi | …),
    `identifier`, `command` (e.g. `npx`/`uvx`), `args[]`; and **Environment variables**
    (name + guide-secret ref).
- `discoveryTransport` is shown but **derived** from mode (remote→`streamable_http`,
  package→`stdio`); it is not an independent free choice (design §3.3). Never inferred from
  hostname.

### 3.3 HTTP (`api`) specifics

- **MCP server URL** required; `bridgeId` required; generated `mcp+api://{bridgeId}`
  preview (C8).
- **E6 loopback warning:** if the URL host is `localhost`/`127.0.0.1`, show a non-blocking
  warning that Docker cannot reach it without `host.docker.internal`. **Never** rewrite the
  URL or change mode based on the hostname.
- Headers: secret-ref values stored as `{{secret:NAME}}`; literal values allowed; secret
  values **masked** in the field and never echoed in any preview/status text.

### 3.4 Sandbox (`sandbox_subprocess`) specifics

- Package + env fields per 3.2. Generated `mcp+sandbox://{bridgeId}` preview.
- **Staged vs applied status** is always visible for the source's scope
  (`projectId + guideId`): one of `Staged (not applied)`, `Applied`, `Drift (re-apply
  needed)`. Backed by the `setup-status` hash, not optimistic UI.
- **Apply is explicit and confirmed (E12):** Test connection / Install packages triggers
  apply. Before mutating, a confirmation dialog states the **blast radius**: *"Applying
  installs packages into the sandbox shared by every notebook using this guide in this
  project."* Cancel is safe (no mutation). Saving/importing **never** applies.
- States: `applying` (spinner + disabled actions) and `apply-failed` (error + retry).

### 3.5 Discovery + diff (keep, adapt)

- Discovery works for both modes: `api` via server-side MCP SDK; `sandbox_subprocess` via a
  sandbox stdio spawn (`tools/list`). Reuse the existing discovery diff UX (added /
  changed / removed / disabled chips, review-before-apply, stable `backingToolId`).
- Discovery for `sandbox_subprocess` requires applied packages; if staged ≠ applied,
  discovery prompts to apply first (no silent partial discovery).

### 3.6 Source card (list view)

Each MCP source card shows:

- Source name + **MCP** kind badge.
- **Runtime-execution sub-badge:** `API` or `Sandbox`.
- Connector key (scheme-aware): `MCP server` (URL) for `api`, `MCP package`
  (`identifier`) for `sandbox_subprocess` — **never** "API host".
- `toolNamePrefix` and operation count (`enabled/total`).
- Validation chip: `Valid` / `Needs attention` / `Custom descriptor` / `Invalid JSON`.
- For `sandbox_subprocess`: the staged/applied status line (3.4).
- For a **migrated** source (was `client://mcp-bridge-*`): a one-time `Migrated to API`
  notice (3.8).

### 3.7 `toolNamePrefix` uniqueness (E11)

- The editor validates uniqueness of `toolNamePrefix` across **all** MCP sources on the
  same assistant and uniqueness of schema `name`. Collisions surface inline on the field
  and **block save** with a specific message ("`toolNamePrefix` 'mcp' already used by
  source X"). Default `mcp` colliding across sources is the common case to catch.

### 3.8 Migration UX (related concern)

- Opening a guide whose MCP source still uses `client://mcp-bridge-*` shows it classified
  as MCP with `runtimeExecution: api` (the migration default for remotes), surfaces a
  `Migrated to API` notice explaining the client-bridge path was removed, and writes the
  new descriptor on save (Phase 1 owns the rewrite; this gate owns the *surfacing*).
- No silent breakage: a pre-migration source must never render as an unknown/invalid card.

### 3.9 Publish-block surface (E16)

- When publish is attempted (or pre-checked) and any `sandbox_subprocess` source has
  staged ≠ applied, the Publish action is **blocked** with an actionable message and a
  direct affordance to apply ("Apply sandbox packages to publish"). The block message maps
  to the backend publish-check error (no frontend-only guess).
- `api`-only guides and fully-applied guides are **not** blocked.

### 3.10 Loading / empty / error / accessibility / responsive

- Keep the established Tool Sources contract from
  [`../tool-sources-execution/ui-gate.md`](../tool-sources-execution/ui-gate.md) §2.6–§2.8:
  explicit loading/empty/error/retry states, `role="alert"` / `aria-live="polite"` for
  validation and status, keyboard reachability, focus management in the confirm dialog
  (trap + restore), and single-column mobile flow with footer actions visible.
- The apply confirmation dialog is a modal: focus trapped, `Escape` cancels (no mutation),
  focus restored to trigger on close.

---

## 4. Reuse existing mechanisms (do not reinvent)

The concerns this gate covers are **mostly already solved** in the client. The subagent
**must** reuse these and is **forbidden** from re-implementing a parallel version. Building
fresh is allowed **only** for the two rows explicitly marked "build" — and even those reuse
the listed lower-level mechanism.

| Concern | Reuse this (path) | Rule |
|---|---|---|
| Confirm / blast-radius dialog (apply, discard) | `src/client/src/components/common/ConfirmationDialog.tsx` | Use for the apply-confirmation (§3.4) and any discard prompt. Do **not** hand-roll an overlay (the `PublishGuideDialog` inline confirm is the anti-pattern). |
| Secret-ref input + masking | `.../guides/editor/EnvironmentSecretRefField.tsx`, `.../toolSources/environmentVariableRefs.ts` (`formatSecretRef`/`parseSecretRef`/`resolveHeaderValues`), `environmentVariableValidation.ts` (`MASKED_SECRET_VALUE`) | Headers/env secrets use the existing ref syntax + password/masked display. Never add a new masking primitive. |
| Status chips / diff chips | `.../toolSources/toolSourceCardViewModel.ts` (`statusChipClassName`, `sourceKindBadgeClassName`), `.../toolSources/mcpToolSource.ts` (`diffStateChipClassName`/`diffStateLabel`) | Add the runtime sub-badge + staged/applied status as new className helpers **in the view-model**, matching the existing chip pattern (`inline-flex px-2 py-0.5 rounded text-xs font-medium`). |
| Toast notifications | `.../common/Toast.tsx` (`useToast`) | Apply success/failure + publish errors surface via toast, as `GuidesDashboard`/`BaseEntityEditor` already do. |
| Loading / empty | `.../LoadingSpinner.tsx`, `.../guides/EmptyState.tsx`, inline `FaSpinner animate-spin` for button in-flight | Use the existing in-flight button pattern (as the current panel does). |
| **Sandbox admin apply + setup-status** (build thin UI, reuse API) | `.../features/guideantsGuide/guideantsAppBridge.ts` — `SandboxAdminGetRequirements`/`SetRequirements`/`GetAptPackages`/`SetAptPackages`/`GetInstallScripts`/`SetInstallScripts`/`Apply`/`GetApplyJob`/`GetSetupStatus` over `/api/system-guide/sandbox-admin/*` (scoped `projectId`+`guideId`) | There is **no React UI** yet, but the **client API/bridge exists**. Build the staged/applied + apply UI **on top of these calls** — do **not** add a new endpoint or a second admin client. |
| **Publish-block surfacing** (build, reuse path) | Existing publish path: `PublishGuideDialog.tsx` / `GuidesDashboard.handlePublishSubmit` (toast on `api.guides.guides.publish()` error) + `EditorHeader` save-disable on `hasValidationErrors` | Surface the E16 block through the **existing** publish validation/error channel (mirror the friendly-name validation pattern in `configTabs/GeneralTab.tsx`). Do **not** invent a separate publish-gate modal. |

Genuinely-absent generic primitives (`Skeleton`, shared `ErrorAlert`, shared retry button)
are **out of scope** — match the existing inline `role="alert"` error + `FaRedo` retry
convention already used in `McpConnectionPanel.tsx` rather than introducing new shared
components in this phase.

---

## 5. Component decomposition contract (anti-monolith)

`project-rules.mdc` mandates **one component per file** and ≤120-char lines. The shipped
`McpConnectionPanel.tsx` (~490–527 lines, with test/discover/apply side-effects inlined) is
already at the edge; this rework **adds** a runtime-mode control, package + env fields,
staged/applied status, an apply flow, a migration notice, and publish-block wiring. Dumping
those into the existing panel would produce another `StructuredOperationEditor`-class
monolith. **Do not.** The established healthy split in this directory is:

> **presentational panel(s)** + **pure `*.ts` helpers (unit-tested under `__tests__/`)** +
> **view-model** + **types** + **side-effect hook**.

Required decomposition (target file map — names indicative, follow existing conventions):

| File | Kind | Responsibility |
|---|---|---|
| `mcpToolSourceTypes.ts` | types | Extend with `runtimeExecution`/`discoveryTransport`/`package`/`environmentVariables` (**lands in Phase 1**). |
| `mcpToolSource.ts` | pure helpers | Extend descriptor parse/build for the new schemes, package, env (tested). |
| `mcpRuntimeMode.ts` *(new)* | pure helpers | Mode/discovery-transport derivation + defaults + `mcp+api://`/`mcp+sandbox://` URL build + E6 loopback detection. Unit-tested; **no JSX**. |
| `useMcpConnection.ts` *(new hook)* | side-effect hook | Owns panel state machine + test/discover/**apply** orchestration (currently inlined in the panel). Calls the sandbox-admin bridge (§4). |
| `McpConnectionPanel.tsx` | presentational shell | Thin composition of the sub-panels below; no business logic. |
| `McpHttpConnectionFields.tsx` *(new)* | presentational | URL + headers (via `EnvironmentSecretRefField`) + E6 warning. |
| `McpPackageConnectionFields.tsx` *(new)* | presentational | Package (`registryType`/`identifier`/`command`/`args`) + env-var rows. |
| `McpSandboxSetupStatus.tsx` *(new)* | presentational | Staged/applied status chip + Apply button → `ConfirmationDialog`. |
| `McpDiscoveryResults.tsx` *(new)* | presentational | Discovered-tools list + diff chips (extracted from the current panel). |
| `toolSourceCardViewModel.ts` | view-model | Add runtime sub-badge + scheme-aware connector key + migration-notice className helpers (pure). |

Hard decomposition rules (gate-enforced):

- **Net line count of `McpConnectionPanel.tsx` must not increase** versus its current size;
  the expectation is it **shrinks** as logic moves to the hook + sub-panels.
- No new presentational `.tsx` in this phase exceeds ~250 lines; if a section grows past
  that, split it (separate file, or colocated named section components as
  `StructuredOperationEditor` does — but prefer separate files for new work).
- **No business logic in JSX/effects**: mode derivation, URL building, secret resolution,
  diff computation, and apply orchestration live in `*.ts` helpers or the hook, each with
  `__tests__/` coverage — mirroring `operationFragmentBuilder.ts` / `mcpToolSource.ts`.
- Reuse the shared primitives from §4; introducing a duplicate dialog/spinner/badge is an
  automatic FAIL.

---

## 6. Phase gate checks (Phase 6)

- [ ] §2 deltas C1–C9 implemented; grep gate clean (no `client_bridge` / `mcp-bridge-` /
      "client-bridge-first" in `toolSources/`).
- [ ] Runtime-execution control offers only `api` / `sandbox_subprocess`; form reshapes
      per mode (§3.2).
- [ ] HTTP mode: URL + headers + masked secrets + `mcp+api://` preview + E6 loopback
      warning (§3.3).
- [ ] Sandbox mode: package + env fields + `mcp+sandbox://` preview + staged/applied status
      + confirmed apply with blast-radius copy + `applying`/`apply-failed` states (§3.4).
- [ ] Discovery + diff works both modes; sandbox discovery requires applied packages
      (§3.5).
- [ ] Source card shows MCP badge + runtime sub-badge + scheme-aware connector key +
      staged/applied (sandbox) + migration notice (§3.6, §3.8).
- [ ] `toolNamePrefix`/schema-name uniqueness blocks save with inline message (§3.7).
- [ ] Publish-block surface reflects backend E16 check; not over/under-blocking (§3.9).
- [ ] Accessibility + responsive + loading/empty/error per §3.10.
- [ ] **Reuse (§4):** apply/discard use `ConfirmationDialog`; secrets use
      `EnvironmentSecretRefField` + `environmentVariableRefs.ts`; chips use the view-model
      helpers; toasts use `useToast`; sandbox apply/setup-status calls go through the
      existing `guideantsAppBridge.ts` `SandboxAdmin*`/`GetSetupStatus`/`Apply` bridge (no
      new endpoint/admin client); publish-block uses the existing publish path. No
      duplicate dialog/spinner/badge introduced.
- [ ] **Decomposition (§5):** `McpConnectionPanel.tsx` net line count did **not** grow;
      logic extracted to `useMcpConnection.ts` + pure `*.ts` helpers with `__tests__/`;
      new presentational `.tsx` each ≤ ~250 lines; no business logic in JSX/effects.

---

## 7. Required UI test matrix

**Component/unit:**

- `runtimeExecution` mode switch reshapes fields (api↔sandbox).
- URL→`mcp+api://` and package→`mcp+sandbox://` preview generation.
- E6 loopback warning shown for `localhost`/`127.0.0.1`, hidden otherwise, URL unchanged.
- Header secret masking; `{{secret:NAME}}` stored, raw value never rendered.
- `toolNamePrefix` collision → inline error + save blocked.
- Staged/applied status mapping from `setup-status`; drift after edit.
- Migration notice rendered for `client://mcp-bridge-*` source; new descriptor on save.
- Publish-block message rendered when staged ≠ applied; absent when applied / api-only.

**Interaction:**

- Create MCP source → switch to sandbox → fill package → Test (apply) → confirm dialog →
  applied; Cancel → no mutation.
- Discovery → diff review → apply to descriptor; stable ids preserved across refresh.
- Keyboard path through picker → panel → confirm dialog (focus trap + restore).

**Responsive:**

- Mobile single-column panel; confirm dialog footer actions reachable.

**Decomposition / reuse (structural — verified by review + grep, not only runtime):**

- Pure helpers (`mcpRuntimeMode.ts`, extended `mcpToolSource.ts`) have `__tests__/`
  coverage for mode derivation, URL build, E6 detection, and diff computation.
- `useMcpConnection.ts` owns test/discover/apply orchestration; the panel renders state.
- Grep proves reuse: apply/discard render `ConfirmationDialog`; sandbox apply calls route
  through `guideantsAppBridge.ts`; no new `createPortal`/overlay or masking primitive added.

---

## 8. Report-back addition (Phase 6)

```text
UI GATE (Guide Builder MCP):
- Deltas C1–C9 + grep clean (no client_bridge/mcp-bridge-/client-bridge-first): <pass/fail>
- Runtime-execution control (api/sandbox only, reshapes form): <pass/fail>
- HTTP mode (URL/headers/masking/mcp+api preview/E6 warning): <pass/fail>
- Sandbox mode (package/env/staged-applied/confirmed apply/states): <pass/fail>
- Discovery + diff both modes: <pass/fail>
- Source card (badges/connector key/status/migration notice): <pass/fail>
- toolNamePrefix uniqueness blocks save: <pass/fail>
- Publish-block surface (E16, backend-mapped): <pass/fail>
- Accessibility + responsive + loading/empty/error: <pass/fail>
- Reuse (§4 — ConfirmationDialog / secret-ref / chips / toast / sandbox bridge / publish path): <pass/fail + any duplication introduced>
- Decomposition (§5 — panel line delta ≤0, hook + tested helpers, new .tsx ≤~250 lines): <pass/fail + file map>
- UI test matrix additions: <paths>
```
