# Auth System — Execution & Orchestration Guide

Last updated: 2026-06-05

This is the **conductor** document for executing
[`../auth-system-plan.md`](../auth-system-plan.md). It is written for the
**top-level (orchestrating) agent**. It defines how work is split into
**subagent task briefs**, the **dependency order**, the **verification gates**
the orchestrator runs after each phase, and the **deviation/failure protocol**
that keeps the plan on-rails so it is executed correctly the first time.

> **Audience split**
>
> - **You (orchestrator)** read this file + [`DECISIONS.md`](./DECISIONS.md) +
>   [`STATUS.md`](./STATUS.md). You dispatch subagents, run gates, and update
>   `STATUS.md`.
> - **Subagents** read their own `task-phase-N-*.md` brief, plus the sections of
>   `../auth-system-plan.md` it cites, plus `DECISIONS.md`. A subagent should
>   **not** need any other context.

---

## 0. How to use this folder

| File | Owner | Purpose |
|------|-------|---------|
| `00-orchestration.md` (this) | Orchestrator | Dispatch order, gates, deviation protocol. |
| `DECISIONS.md` | Orchestrator (fill **before** any dispatch) | The two open §3 decisions + locked invariants. Single source of truth. |
| `STATUS.md` | Orchestrator (update after every gate) | Living ledger: phase state, gate results, deviations, re-dispatches. |
| `codeql-gate.md` | Orchestrator + security-sensitive subagents | Local (no-GitHub) CodeQL security gate: baseline, commands, diff, rules to watch. |
| `task-phase-0-cleanup.md` | Subagent | Phase 0 brief. |
| `task-phase-1-datamodel.md` | Subagent | Phase 1 brief. |
| `task-phase-2-backend-auth.md` | Subagent | Phase 2 brief. |
| `task-phase-3-authorization.md` | Subagent | Phase 3 brief. |
| `task-phase-4-admin-users.md` | Subagent | Phase 4 brief. |
| `task-phase-4.5-tool-oauth.md` | Subagent | Phase 4.5 brief. |
| `task-phase-5-frontend.md` | Subagent | Phase 5 brief. |
| `task-phase-6-openapi-tests-docs.md` | Subagent | Phase 6 brief. |

Each task brief follows the **same template** (Mission → Read first →
Preconditions → Guardrails → Tasks → Files in/out of scope → Self-verification →
Definition of Done → Report-back contract). The Report-back contract is what you
diff against the brief to **detect deviations**.

---

## 1. Pre-flight (do this once, before any subagent is dispatched)

Executing "the first time" depends on locking cross-cutting choices up front. **Do
not dispatch Phase 1 until all of the following are true.**

- [x] **Session mechanism (D1) LOCKED → App-issued JWT Bearer.** No logout endpoint;
      `SecurityStamp` claim for revocation; Bearer header on the client.
- [x] **Role storage (D2) LOCKED → separate `UserRoles` table** (one row per user,
      unique on `UserId`; flat single-role model preserved).
- [x] **Tool-OAuth PKCE state (D4a) LOCKED → `OAuthAuthorizationState` table.**
- [x] **D3 Appendix-A questions LOCKED** → delete project = Admin; `/api/usage` =
      Admin; speech transcribe = Contributor (dictation; published path is the Public
      `/api/published/speech/transcribe`); notebook llama-runtime: **`load` =
      Contributor** (only the chat-configured model, to run a conversation),
      **`unload`/`restart` = Admin**, status reads = ApprovedUser; external-auth
      provider config = Admin; name-based avatar GETs = Public (`<img src>` + JWT).
      `header-toolbar` (full config DTO) = Admin; a **new** lean
      `header-toolbar/chat-readiness` = ApprovedUser carries run-readiness for all
      runners (Phase 3 adds it; Phase 5 mounts the full toolbar Admin-only).
      Appendix A updated to match.
- [x] **D4(b) token scope LOCKED → per (`User`, `Provider`)** (no `ProjectId` in key).
- [x] **JWT `<img src>` rule recorded** (DECISIONS D3): never gate an endpoint the
      client renders as a raw `<img src>` unless Phase 5 reworks it to a blob fetch.

> All blocking decisions are resolved. The only remaining gate to dispatch is the
> **clean baseline** capture below.
- [ ] Confirm the **role set is frozen**: `Pending=0, Reader=1, Contributor=2,
      Admin=3` — no other roles, ever (plan §1).
- [ ] Capture a **clean baseline**: from `src/server` run `dotnet build
      GuideAntsApi.sln` and `dotnet test GuideAntsApi.sln`; from `src/client` run
      `npm run build` and `npm test -- --run`. Record pass/fail counts in
      `STATUS.md` as the "before" line. Every later gate compares against this.
- [ ] Capture the **CodeQL baseline** per [`codeql-gate.md`](./codeql-gate.md) §4.1
      (local, **no GitHub fetch/parity** — that does not apply to this branch). Save
      SARIFs to `.codeql/baseline/` and record per-language/per-rule counts in
      `STATUS.md`. Later security-sensitive gates diff against this.
- [ ] Confirm a clean working tree (`git status`) and create the feature branch if
      not already on it.
- [ ] Confirm `dotnet ef` is installed (`dotnet ef --version`) — Phase 1 needs it.

If the user has not decided #1/#2, **stop and ask** (use a structured question).
Do not pick for them — these are architecture, not formatting.

---

## 2. Dependency graph (dispatch order)

```
                 Phase 0  (cleanup, no behavior change)
                    │
                    ▼
                 Phase 1  (data model + migration)         D2 ✅ (UserRoles table)
                    │
                    ▼
                 Phase 2  (auth pipeline, login/register)  D1 ✅ (JWT Bearer)
                    │
          ┌─────────┴───────────┐
          ▼                     ▼
       Phase 3              Phase 4
   (authorization        (admin user mgmt;
    policies on            depends on RequireAdmin
    every group)           from Phase 3)
    ← needs D3 open Qs
          │                     │
          └─────────┬───────────┘
                    ▼
                 Phase 4.5  (server-side tool-OAuth; needs authed user + policies)
                    ← needs D4(b) token scope
                    │
                    ▼
                 Phase 5  (frontend; needs all endpoints + guards to exist)
                    │
                    ▼
                 Phase 6  (OpenAPI security, tests, docs; needs everything)
```

**Rules:**

- Phases run **strictly in order**; the only allowed parallelism is **Phase 3 and
  Phase 4** *after* Phase 3's policy definitions land — and even then prefer
  sequential unless schedule pressure demands it, because Phase 4 endpoints depend
  on the `RequireAdmin` policy object existing.
- **A phase is not "done" until its gate (section 4) passes.** A downstream phase
  must **never** start on top of a failed gate. This is the core mechanism that
  prevents compounding failures.
- One subagent per phase. Do **not** hand a subagent more than its brief.

---

## 3. Dispatch protocol (per phase)

For each phase, in order:

1. **Confirm preconditions** listed in the brief (prior gate green; DECISIONS
   filled). Update `STATUS.md` → phase `IN_PROGRESS`.
2. **Dispatch one subagent** with a prompt that is exactly: *"Read and execute
   `docs/auth-execution/task-phase-N-*.md` end to end. Obey its guardrails and
   Definition of Done. Return the Report-back contract verbatim."* Give it no
   other instructions — the brief is the contract.
3. **Receive the Report-back.** Do not trust it blind — it is a claim.
4. **Run the gate** (section 4 + the phase's own gate). The gate is **your**
   independent verification, run with your own tools, not the subagent's word.
5. **Decide**: PASS → mark phase `DONE` in `STATUS.md`, proceed. FAIL/DEVIATION →
   follow section 5.

> You verify; the subagent implements. Never let "the subagent said it's done"
> substitute for a green gate.

---

## 4. Verification gates

### 4.1 Global invariants — checked at **every** gate

Run/inspect these after every phase. Any failure blocks the next phase.

- [ ] **Server build green**: `cd src/server && dotnet build GuideAntsApi.sln`
      (0 errors; warning count not worse than baseline).
- [ ] **Server tests green**: `cd src/server && dotnet test GuideAntsApi.sln` — no
      new failures vs the Pre-flight baseline.
- [ ] **Client build green**: `cd src/client && npm run build` (tsc + vite, 0
      errors).
- [ ] **Client tests green**: `cd src/client && npm test -- --run`.
- [ ] **No "fallback" anti-patterns** (per user rule — *fallback is a bug
      generator*). Grep the diff for newly added: `fallback`, `?? "admin"`-style
      default-identity, empty `catch {}`, `catch` that swallows a `401`/`403`, or
      "first `Users` row" reads sneaking back in. A `401` must surface as re-auth,
      never be masked.
- [ ] **Role model intact**: still exactly `Pending/Reader/Contributor/Admin`. No
      new role names; **no re-introduction** of `Teams`, `TeamMemberships`,
      `TeamInvitations`, `ProjectUserRoles`, `ProjectRoles`, `TeamRoles`,
      `AccessCodes`, `Projects.OwnerUserId` (plan §2.2 — they were removed and are
      out of scope).
- [ ] **Scope discipline**: the subagent only touched files its brief authorized.
      Diff the file list against the brief's "Files in scope". Unexpected files =
      deviation.
- [ ] **No secrets committed** (no real keys in `appsettings*.json`; signing keys
      via config/user-secrets only).
- [ ] **No new CodeQL findings** vs the pre-flight baseline — run the local gate
      ([`codeql-gate.md`](./codeql-gate.md)) at minimum after every
      **security-sensitive** phase (2, 3, 4, 4.5, 5) and at final acceptance.
      C# **must** use `build-mode=none`; **no GitHub parity** (inapplicable);
      **no alert suppression** — fix the code.
- [ ] **Matches `DECISIONS.md`** (session mechanism, role storage). A subagent that
      built cookies when JWT was chosen is an automatic FAIL.

### 4.2 Per-phase gate criteria

Each is **in addition** to 4.1. Commands assume `src/server` or `src/client` cwd
as noted.

**Phase 0 — Cleanup**

- [ ] `cd src/client && npm run find-orphans` shows the targeted dead items
      (`'oss-lite-token'`, `VITE_MSAL_*`, broken `/login` links) **reduced**, not
      increased.
- [ ] Grep confirms removed: `oss-lite-token` (authService stub), `VITE_MSAL_`
      (env.d.ts/stubs). Broken `/login` references in `ErrorScreen.tsx`,
      `Terms.tsx`, `Privacy.tsx` are either removed or pointed at the real route
      (decide in Phase 5, but they must not 404 silently).
- [ ] **Behavior unchanged**: builds + tests still green (this phase deletes dead
      code only; any test delta is a deviation).

**Phase 1 — Data model & migration**

- [ ] `cd src/server && dotnet ef migrations list --project
      GuideAntsApi.DataModel/GuideAntsApi.DataModel.csproj --startup-project
      GuideAntsApi/GuideAntsApi.csproj` shows the new migration at the head.
- [ ] `dotnet ef migrations script` (last → head) review: the script **adds** the
      Role/PasswordHash/etc. columns **and deletes** seed user
      `fd787545-ffae-4ea9-81fa-700db2fffccd`; it does **not** add a `HasData` seed
      for any user (plan Phase 1).
- [ ] Fresh-DB apply succeeds and leaves **zero** users:
      `dotnet ef database drop --force …` then `dotnet ef database update …`, then
      confirm `SELECT COUNT(*) FROM Users = 0`.
- [ ] `DataModel.Tests` green.

**Phase 2 — Backend authentication**

- [ ] `Program.cs`/`StartupConfiguration.cs` now call
      `AddAuthentication`/`AddAuthorization` + `UseAuthentication`/
      `UseAuthorization` (grep), using **app-issued** tokens (no `AddMicrosoftIdentityWebApi`).
- [ ] Endpoints exist and respond: `POST /api/auth/register`, `POST /api/auth/login`,
      `GET /api/auth/me`. **No `logout` endpoint** (JWT). The JWT carries role +
      `SecurityStamp` claims. Smoke via integration test or `curl`.
- [ ] **First-registrant-is-Admin** proven by test: first register → `Admin`+active;
      second register → `Pending`. A concurrency test (or documented
      transaction/constraint) shows two simultaneous first-registrations cannot
      both become Admin.
- [ ] `ICurrentUserService` resolves the DB user from the principal; the old
      "first `Users` row" logic in `UserEndpoints`/`ContextOptionsService` is gone
      (grep returns nothing).
- [ ] **CodeQL diff clean** (`codeql-gate.md`): no new `cs/log-forging` (email/name
      logging), clear-text password storage, or hard-coded JWT secret.

**Phase 3 — Authorization**

- [ ] Policies `RequireApprovedUser`, `RequireContributor`, `RequireAdmin` exist.
- [ ] **Every** `Map*Endpoints` group has `.RequireAuthorization(...)` **or** an
      explicit `.AllowAnonymous()` justified by Appendix A (login/register,
      `/api/published/**`, `documentserver` `download`/`callback` + `ds/{**path}`
      proxy). Grep each endpoint file; a group with neither is a FAIL.
- [ ] **Non-group routes covered explicitly** (the source has 3 the group sweep
      misses; swagger snapshot is stale): `GET /api/startup` (`Program.cs` inline),
      the `MapFallback` SPA shell (`UiApplicationBuilderExtensions.cs`), and the
      `documentserver/ds/{**path}` proxy (`ExcludeFromDescription`) each have an
      explicit `.AllowAnonymous()`. Confirm the doc-server group is
      `/api/documentserver/**`, not the stale `/api/onlyoffice/**`.
- [ ] Spot-check guard correctness against **Appendix A** of the plan: a Reader
      token is `403` on a `RequireContributor` route; a Contributor token is `403`
      on a `RequireAdmin` route; `Pending` is blocked everywhere except
      `/api/auth/me`.
- [ ] Guide/Assistant/Operations/Usage/Settings groups are `RequireAdmin` (§2.4).
- [ ] Message attribution writes the **authenticated** `UserId` (no more `null`).
- [ ] **CodeQL diff clean** (`codeql-gate.md`): no new findings from the endpoint
      wiring.

**Phase 4 — Admin user management**

- [ ] `/api/admin/users` list/approve/role/set-password/deactivate exist and are
      all `RequireAdmin` (non-admin → `403`).
- [ ] Last-Admin safeguard: an Admin cannot deactivate/demote the final Admin
      (test returns a guarded error, not a lockout).
- [ ] `set-password` re-hashes with the Phase 2 hasher and invalidates the target's
      sessions; sets `MustChangePassword`.
- [ ] **CodeQL diff clean** (`codeql-gate.md`): no clear-text password
      storage/logging in set-password; no user-enumeration/log-forging.

**Phase 4.5 — Tool OAuth (server-side)**

- [ ] `ExternalOAuthToken` + `OAuthAuthorizationState` (or cache) added; token
      columns **encrypted** via the existing Data Protection / `EncryptSecrets`
      path — grep proves no plaintext token column.
- [ ] New `oauth/authorize-url|callback|status` + `DELETE oauth` endpoints exist,
      are auth+project-access gated, and `status` **never** returns a token.
- [ ] **Client no longer stores tokens**: grep client for
      `oauth_tokens_`, `oauth_pkce_`, `collectOAuthTokensForTemplate`,
      `refreshOAuthTokens` → all gone; `sendMessageStream` no longer takes
      `oauthTokens`.
- [ ] **CodeQL diff clean** (`codeql-gate.md`): no clear-text token storage and no
      `cs/path-injection` from token/file work; **`js/*` clear-text `localStorage`
      findings should DROP** (tokens removed) — if any remain, removal is incomplete.

**Phase 5 — Frontend**

- [ ] `npm run build` green; `npm test -- --run` green incl. new tests.
- [ ] Routes wired: public `/login`,`/register`; gated `/pending`,`/change-password`;
      feature routes wrapped in `ProtectedRoute`; works under **both**
      `HashRouter` (Electron) and `BrowserRouter` (web).
- [ ] `api.ts` attaches credentials (Bearer **or** `credentials:'include'` per
      DECISIONS) and broadcasts `AUTH_EXPIRED` on `401` — no silent swallow.
- [ ] **UI-convention gate (§5.0)**: no new icon library, no bespoke modal/button
      markup; new pages reuse `ConfirmationDialog`, `ActionButtons`, `Toast`,
      `LoadingSpinner`, `PersonalizationTab` styling. Reject on violation.
- [ ] Non-admins see **only** Personalization in Settings; admin tab content is
      guarded server-side too (not just hidden).
- [ ] **CodeQL diff clean** (`codeql-gate.md`, JS focus): no clear-text credential
      storage in `localStorage`; the JWT is not logged.

**Phase 6 — OpenAPI, tests, docs**

- [ ] Regenerate `guideants-swagger.json` (run the API, fetch
      `/swagger/v1/swagger.json`) and confirm it now has `securitySchemes` + per-
      operation `security`. Then run
      `node scripts/find-unused-api-endpoints.mjs --swagger guideants-swagger.json
      --client src/client/src` — no surprises (no client calls to nonexistent/
      now-protected routes).
- [ ] Integration test auth handler replaces the no-op `Bearer test_token`
      (`BaseIntegrationTest.SetupAuthentication`) with a handler that can
      impersonate each role; `SetupAuthenticationWithClaims` actually applies the
      claims.
- [ ] Tests cover register→pending→approve and role-gated 401/403 paths.
- [ ] `appsettings*.json` has token signing config (issuer/audience/lifetime/key
      source) with **no real secret** in source control.
- [ ] Final auth flow + bootstrap-admin procedure documented under `./docs`.

---

### 4.3 CodeQL security gate (local)

Defined in [`codeql-gate.md`](./codeql-gate.md). Summary:

- **Local baseline-vs-current**, not GitHub parity (the branch is not on GitHub —
  skip `fetch-github-code-scanning.ps1` and all parity checks).
- Run after the **security-sensitive phases (2, 3, 4, 4.5, 5)** and at final
  acceptance; diff against `.codeql/baseline/`.
- **Pass = zero NEW findings** vs baseline. Watch `cs/log-forging` (auth logging),
  `cs/path-injection` (file/OAuth work), clear-text password/token storage, hard-
  coded JWT secret, and `js/*` clear-text `localStorage` (which Phase 4.5 should
  *remove*).
- C# **`build-mode=none`** only; code-scanning suites only; **no suppression — fix
  the code**.

## 5. Deviation & failure protocol

When a gate fails, **stop the line**. Do not start the next phase.

1. **Classify** the failure in `STATUS.md`:
   - **Build/test red** → mechanical; re-dispatch same subagent with the exact
     error output and the failing command.
   - **Missing DoD item** → the subagent under-delivered; re-dispatch with the
     specific unchecked items quoted.
   - **Scope creep** (touched out-of-scope files) → review those edits; revert the
     unauthorized ones (`git checkout -- <file>` or `git revert`) unless they are
     genuinely required, in which case update the brief + `DECISIONS.md` first so
     the change is intentional and recorded.
   - **Decision drift** (built against the wrong DECISIONS value) → revert the
     phase's changes and re-dispatch with DECISIONS re-quoted at the top.
   - **Fallback/masking introduced** → hard reject; require removal. Per user rule,
     fallback logic that hides bugs is never acceptable.
2. **Re-dispatch** the *same* phase brief with a focused correction note
   appended ("Gate failed on X; fix only X; do not touch anything else"). Re-run
   the **full** gate afterward (not just the failed check) to catch regressions.
3. **Cap retries at 2.** If a third attempt is needed, escalate to the user with
   the gate output and your hypothesis — the brief itself may be wrong or a
   DECISIONS value may need to change.
4. **Record everything** in `STATUS.md`: attempt #, what failed, what was changed,
   gate re-run result. This ledger is how you prove the plan was executed fully.

**Never** advance a phase to fix a problem in a later phase ("I'll wire the guard
in Phase 5") — that is how deviations compound. Fix it in the phase that owns it.

---

## 6. Final acceptance (after Phase 6 gate)

The plan is "executed fully" only when **all** hold:

- [ ] Every checkbox in `../auth-system-plan.md` §4 (Phases 0–6) is satisfiable by
      pointing at a commit/file/test.
- [ ] Every endpoint in the plan's **Appendix A** has the stated guard (verify by
      grep across `Endpoints/*.cs` + a role-matrix integration test).
- [ ] Fresh install: zero users → first register = Admin → second register =
      Pending → Admin approves → Contributor/Reader behave per Appendix A.
- [ ] No `localStorage` tool-OAuth tokens remain; tool calls inject server-side.
- [ ] Global invariants (4.1) green on the final tree.
- [ ] **Final CodeQL diff clean** ([`codeql-gate.md`](./codeql-gate.md)): zero new
      findings vs the pre-flight baseline; any new finding fixed in-code (never
      suppressed). Final counts recorded in `STATUS.md`.
- [ ] `STATUS.md` shows every phase `DONE` with a passing gate and no open
      deviations.

When all are checked, summarize the run (phases, retries, any DECISIONS that
changed mid-flight) for the user.
