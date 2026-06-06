# Task — Phase 3: Authorization (role policies on every endpoint)

> Subagent brief. Execute top to bottom; return the Report-back contract verbatim.

## Mission

Define the role policies and apply the correct guard to **every** endpoint group so
the API enforces `Pending/Reader/Contributor/Admin` exactly per **Appendix A** of
the plan. Also fix message attribution to use the authenticated user.

## Read first

- `../auth-system-plan.md` → **Appendix A** (the authoritative per-endpoint guard
  matrix), §2.4 (Waterfall Team-Owner → Admin mapping), §4 → **Phase 3**.
- `./DECISIONS.md` → **D3** (frozen invariants + the **resolved** Appendix-A open
  questions). All resolved — apply as stated below; do not reinterpret.

**Resolved guards (DECISIONS D3) — apply exactly:**

- `DELETE /api/projects/{id}` → **`RequireAdmin`**.
- `/api/usage/**` → **`RequireAdmin`**.
- `POST /api/speech/transcribe` → **`RequireContributor`** (dictation/authoring aid;
  published-guide voice input uses the Public `/api/published/speech/transcribe`).
- notebook `llama-runtime`: `POST /load` → **`RequireContributor`** (only to load
  the chat-configured model when needed to run a conversation); `POST /unload` and
  `POST /restart` → **`RequireAdmin`** (management); `GET`
  status/inventory/operations → **`RequireApprovedUser`**.
- external-auth provider config (`PUT`/`DELETE /{providerId}`) → **`RequireAdmin`**
  (the per-user `oauth/*` routes added in Phase 4.5 are `RequireApprovedUser`).
- `GET /api/notebooks/{id}/header-toolbar` → **`RequireAdmin`**. The full DTO only
  feeds the admin service toolbar (a config surface). **Split out** a lean run-readiness
  read so non-admin runners are not broken (see next bullet).
- **NEW endpoint — `GET /api/notebooks/{id}/header-toolbar/chat-readiness`** →
  **`RequireApprovedUser`** (small refactor owned by this phase since it defines the
  authz boundary):
  - Add `NotebookChatReadinessDto` (lean): `effectiveModelId`, `effectiveModelDisplayName`,
    `effectiveProvider`, `blockers[]`, `supportsLocalRuntimePower`, `localRuntimeOn`,
    `inProgressOperationId`, `inProgressState`. **No** provider/model option lists or
    per-service config.
  - Add `GetChatReadinessAsync(notebookId, conversationId, ct)` to
    `INotebookHeaderToolbarService` / `NotebookHeaderToolbarService.cs` — reuse the
    existing chat-segment computation; project to the lean DTO (no new business logic,
    no fallback).
  - Map it in `NotebookHeaderToolbarEndpoints.cs` (same `notebookId:guid` group,
    `?conversationId=` query, `KeyNotFoundException → 404`).
  - Phase 5 repoints `NotebookDetails.tsx` (`chatModelMissing`, no-model dialog
    `blockers`, Contributor load-and-run flow) to this endpoint; the full toolbar
    becomes Admin-only. Don't change client wiring here — server only.
- **Public:** `GET /api/assistants/avatar/{name}` and
  `GET /api/notebook-templates/avatar/{name}` stay `.AllowAnonymous()` (rendered as
  raw `<img src>`; cannot carry the Bearer header under JWT). But
  `GET /api/assistants/conversation-starters/{name}` → **`RequireApprovedUser`**
  (fetched via `callApi`).

> **`<img src>` rule:** do **not** gate any endpoint the client renders as a bare
> `<img src>`/direct link unless Phase 5 reworks it to the authenticated blob fetch
> (`getAuthenticatedUrl`). The `{id}/avatar` guide/assistant routes already use that
> blob pattern, so they remain `RequireAdmin`; the name-based avatars above do not,
> so they stay Public.
- All `src/server/GuideAntsApi/Endpoints/*.cs` (30 files).
- `src/server/GuideAntsApi/Services/.../ConversationService.cs` (the `UserId = null`
  and edit-history `*EditedByUserId = null` writes).
- `./codeql-gate.md` — run the local CodeQL diff before reporting (no GitHub parity).

## Preconditions

- Phase 2 gate green (auth pipeline + `ICurrentUserService` live). Appendix-A open
  questions resolved in DECISIONS.

## Guardrails (hard)

- Guards must match **Appendix A exactly**. Do not invent new tiers. Use **only**
  `RequireApprovedUser`, `RequireContributor`, `RequireAdmin`, or a justified
  `.AllowAnonymous()`.
- **Every** `Map*Endpoints` group ends up with either `.RequireAuthorization(<policy>)`
  or an explicit `.AllowAnonymous()` that Appendix A sanctions. **No group left
  bare** — a bare group is the failure mode this phase exists to prevent.
- **Non-group routes the sweep will miss — handle explicitly** (Appendix A.10/A.10b;
  the committed swagger snapshot is stale, so trust the source): the inline
  `GET /api/startup` in `Program.cs`, the `MapFallback` SPA shell in
  `Configuration/UiApplicationBuilderExtensions.cs`, and the
  `MapMethods("/api/documentserver/ds/{**path}")` reverse proxy in
  `DocumentServerEndpoints.cs` (it is `ExcludeFromDescription`, so it is **not** in
  swagger). Each must get an **explicit `.AllowAnonymous()`** with a comment — do not
  leave them implicitly open.
- The only sanctioned anonymous surfaces: `auth/register`, `auth/login`, the
  `/api/published/**` groups (per-guide API key), `documentserver`
  `download`/`callback` + the `documentserver/ds/{**path}` proxy (doc-server token /
  browser-loaded editor), `GET /api/startup`, and the SPA `MapFallback`. Everything
  else is gated. **Note:** the doc-server group is `/api/documentserver/**`
  (`DocumentServerEndpoints.cs`) — **not** `/api/onlyoffice/**` / `OnlyOfficeEndpoints.cs`
  (those are stale snapshot names that no longer exist).
- **No fallback**: insufficient role → `403`, unauthenticated → `401`. Do not add
  code paths that "let it through if role lookup fails."
- Do not add admin **endpoints** here (Phase 4) — only apply the `RequireAdmin`
  policy to existing groups Appendix A marks Admin. **One sanctioned exception:** the
  lean `header-toolbar/chat-readiness` GET (DECISIONS D3), because it _is_ the authz
  boundary being drawn for the toolbar split. Keep it minimal — a projection of
  existing data, no new business logic.

## Tasks

1. Define policies in `StartupConfiguration.cs` `AddAuthorization`:
   - `RequireApprovedUser` — role ∈ {Reader, Contributor, Admin} (i.e. not Pending).
   - `RequireContributor` — role ∈ {Contributor, Admin}.
   - `RequireAdmin` — role == Admin.
2. Apply guards to **each** group, using Appendix A as the checklist. Work file by
   file (A.3–A.12). For groups with mixed verbs, apply the group default and
   override per-route where Appendix A differs (e.g. reads `RequireApprovedUser`,
   writes `RequireContributor`).
3. Mark the sanctioned anonymous endpoints `.AllowAnonymous()` explicitly (even
   though pipeline default will now require auth) so intent is visible.
4. **Re-gate Waterfall Team-Owner features to Admin** (§2.4): Guides, Assistants,
   Operations, Guide usage, and **all** `/api/settings/**` groups → `RequireAdmin`.
5. Ensure `Pending` is blocked from every feature endpoint but can still reach
   `GET /api/auth/me`.
6. **Fix attribution**: conversation message creation must set `UserId` to the
   authenticated user (not `null`); edit history sets
   `FirstEditedByUserId`/`LastEditedByUserId` to the current user.
7. **Split the header-toolbar read** (see "Resolved guards" above): lock the existing
   `GET .../header-toolbar` to `RequireAdmin`, and add the lean
   `GET .../header-toolbar/chat-readiness` (`RequireApprovedUser`) +
   `NotebookChatReadinessDto` + `GetChatReadinessAsync` projection. Server-side only.

## Files in scope

- `src/server/GuideAntsApi/Configuration/StartupConfiguration.cs` (policies)
- `src/server/GuideAntsApi/Endpoints/*.cs` (apply `.RequireAuthorization`/`.AllowAnonymous`)
- `ConversationService.cs` (attribution only)
- `src/server/GuideAntsApi/Endpoints/NotebookHeaderToolbarEndpoints.cs`,
  `Services/NotebookHeaderToolbar/{INotebookHeaderToolbarService,NotebookHeaderToolbarService}.cs`,
  and the new `NotebookChatReadinessDto` (toolbar read split — task 7)

**Out of scope:** new endpoints (the **one** exception is task 7's `chat-readiness`
read), data model, client, Phase 4.5 OAuth endpoints (the external-auth group's _new_
routes are Phase 4.5; you still gate the **existing** external-auth group per
Appendix A.12).

## Self-verification

```
cd src/server && dotnet build GuideAntsApi.sln
cd src/server && dotnet test GuideAntsApi.sln
```

Coverage check — for **every** endpoint file, confirm no group lacks a guard:
- grep each `MapGroup(...)` and confirm a `.RequireAuthorization(` or
  `.AllowAnonymous(` is attached to the group or all its routes.

Role-matrix spot checks (integration tests or curl with role tokens once Phase 6
handler exists; until then, assert via unit tests of the policy or manual reasoning
documented in the report):
- Reader → `403` on a `RequireContributor` route (e.g. `POST /api/projects`).
- Contributor → `403` on a `RequireAdmin` route (e.g. `POST /api/guides`).
- Pending → `403/401` everywhere except `GET /api/auth/me`.

CodeQL (local, per `./codeql-gate.md` — **C# `build-mode=none`**, no GitHub parity):
diff vs `.codeql/baseline/`; expect **no new** findings from the endpoint wiring.

## Definition of Done

- [ ] Three policies defined.
- [ ] Every endpoint group guarded or explicitly+justified anonymous (no bare group).
- [ ] Guards match Appendix A and the DECISIONS open-question resolutions.
- [ ] Guides/Assistants/Operations/Usage/Settings = `RequireAdmin`.
- [ ] `header-toolbar` (full) = `RequireAdmin`; `header-toolbar/chat-readiness` added =
      `RequireApprovedUser` (lean projection, no new logic).
- [ ] Attribution uses authenticated user (no `null`).
- [ ] Build + tests green.

## Report-back contract (return exactly this)

```
PHASE 3 REPORT
- Policies defined: RequireApprovedUser / RequireContributor / RequireAdmin = yes/yes/yes
- Per-file guard map: <file> -> <group default + overrides>  (one line per endpoint file, all 30)
- Sanctioned AllowAnonymous: <list> (must equal the Appendix-A set)
- Bare groups remaining: <MUST be "none">
- Attribution fixed in: <files:line>
- Toolbar split: header-toolbar=RequireAdmin=<yes?> chat-readiness-added=<yes?> readiness-DTO-fields=<list> service-method=<GetChatReadinessAsync?>
- Role-matrix checks: reader->contributor-route=<403?> contributor->admin-route=<403?> pending->me=<allowed?>
- Verification: build=<pass/fail> tests=<counts>
- CodeQL (local, no GitHub parity): C#-build-mode-none=<yes> new-findings-vs-baseline=<count> -> <RuleId@file:line or "none">
- Files touched: <exhaustive list>
- Deviations / surprises: <list or "none">
```
