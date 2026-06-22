# GuideAnts Guide — Execution & Orchestration Guide

Last updated: 2026-06-21

This is the **conductor** document for executing
[`../guideants-guide-implementation-plan.md`](../guideants-guide-implementation-plan.md)
(the master spec). It is written for the **top-level (orchestrating) agent**. It
defines how the work is split into **subagent task briefs**, the **dependency
order**, the **verification gates** (build/test + CodeQL) the orchestrator runs
after each phase, and the **deviation/failure protocol** that keeps the plan
on-rails so it is executed correctly the first time.

> **Audience split**
>
> - **You (orchestrator)** read this file + [`DECISIONS.md`](./DECISIONS.md) +
>   [`STATUS.md`](./STATUS.md). You dispatch subagents, run gates, and update
>   `STATUS.md`.
> - **Subagents** read their own `task-phase-N-*.md` brief, plus the sections of
>   `../guideants-guide-implementation-plan.md` it cites, plus `DECISIONS.md`. A
>   subagent should **not** need any other context.

---

## 0. How to use this folder

| File | Owner | Purpose |
|------|-------|---------|
| `00-orchestration.md` (this) | Orchestrator | Dispatch order, gates, deviation protocol. |
| `DECISIONS.md` | Orchestrator (resolve **before** any dispatch) | Locked invariants + the open decisions the user must confirm. Single source of truth. |
| `STATUS.md` | Orchestrator (update after every gate) | Living ledger: phase state, gate results, deviations, re-dispatches. |
| `test-gate.md` | Orchestrator | Build + unit/integration test gate: baseline, commands, per-phase criteria. |
| `codeql-gate.md` | Orchestrator + security-sensitive subagents | Local (no-GitHub) CodeQL security gate: baseline, commands, diff, rules to watch. |
| `task-phase-1-datamodel.md` | Subagent | Phase 1 brief. |
| `task-phase-2-published-auth.md` | Subagent | Phase 2 brief. |
| `task-phase-3-system-seeder.md` | Subagent | Phase 3 brief. |
| `task-phase-4-system-api-authz.md` | Subagent | Phase 4 brief. |
| `task-phase-5-publish-ui.md` | Subagent | Phase 5 brief. |
| `task-phase-6-guide-flyout.md` | Subagent | Phase 6 brief. |
| `task-phase-7-settings-access.md` | Subagent | Phase 7 brief. |
| `task-phase-8-tests-docs.md` | Subagent | Phase 8 brief. |

Each task brief follows the **same template** (Mission → Read first →
Preconditions → Guardrails → Tasks → Files in/out of scope → Self-verification →
Definition of Done → Report-back contract). The Report-back contract is what you
diff against the brief to **detect deviations**.

---

## 1. Pre-flight (do this once, before any subagent is dispatched)

Executing "the first time" depends on locking cross-cutting choices up front. **Do
not dispatch Phase 1 until all of the following are true.**

- [x] **All open decisions in [`DECISIONS.md`](./DECISIONS.md) are LOCKED**
      (D-GG-1..5). If a future change reopens one, **stop and ask the user** before
      re-dispatching the affected phase. D-GG-1 (same-host cookie auth) shapes
      Phases 2/4/6.
- [ ] **Branch confirmed**: on `feature/guideants-guide` with a known-good tree.
- [ ] Capture a **clean baseline** per [`test-gate.md`](./test-gate.md) §1: from
      `src/server` run `dotnet build GuideAntsApi.sln` and `dotnet test
      GuideAntsApi.sln`; from `src/client` run `npm run build` and
      `npm test -- --run`. Record pass/fail counts in `STATUS.md` as the "before"
      line. Every later gate compares against this.
- [ ] Capture the **CodeQL baseline** per [`codeql-gate.md`](./codeql-gate.md) §4.1
      (local, **no GitHub fetch/parity** — that does not apply to this branch).
      Save SARIFs to `.codeql/baseline/` and record per-language/per-rule counts in
      `STATUS.md`. Later security-sensitive gates diff against this.
- [ ] Confirm `dotnet ef` is installed (`dotnet ef --version`) — Phase 1 needs it.
- [ ] Confirm the **auth transport reality** (D-GG-1): the app JWT lives in an
      **HttpOnly cookie** (`GuideAnts.Auth`) and **API + UI are same-host**, so the
      browser attaches the cookie to the same-origin published-guide requests
      `guideants-chat` makes. The `AppIdentity` validator reads/validates that
      cookie — no token is minted or stored in JS. (Phase 6 verifies the component
      actually transmits the cookie; if not, escalate — do not add a fallback.)

---

## 2. Dependency graph (dispatch order)

```
              Phase 1  (data model + 2 migrations; no behavior change)
                 │
                 ▼
              Phase 2  (published-guide AppIdentity auth)        D-GG-1 (validate session cookie)
                 │       validation • identity persistence • API guards • config exposure
        ┌────────┴───────────────┐
        ▼                        ▼
     Phase 3                  Phase 5
 (system seeder +        (publish UI read-only
  bootstrap guides;       App-Identity indicator;
  internal publish        needs Phase 2 DTO authMode)
  AppIdentity)
        │
        ▼
     Phase 4  (system project API + authz: hide listings,
        │      access guard, session + workspace endpoints)     (session = config only, no token)
        ├──────────────────────┐
        ▼                       ▼
     Phase 6                 Phase 7
 (guide flyout: button,   (Settings "System Guides"
  provider, chat mount,    entry + route guard;
  app bridge stub;         needs workspace endpoint)            D-GG-2 (link shape)
  needs session endpoint)
        │                       │
        └───────────┬───────────┘
                    ▼
                 Phase 8  (tests sweep, docs, final acceptance; needs everything)
```

**Rules:**

- Phases run **in order** along each chain. Allowed parallelism: **Phase 5** may
  run any time after Phase 2; **Phase 6 and Phase 7** may run in parallel after
  Phase 4. Prefer sequential unless schedule pressure demands it.
- **A phase is not "done" until its gate (section 4) passes.** A downstream phase
  must **never** start on top of a failed gate. This is the core mechanism that
  prevents compounding failures.
- One subagent per phase. Do **not** hand a subagent more than its brief.

---

## 3. Dispatch protocol (per phase)

For each phase, in order:

1. **Confirm preconditions** listed in the brief (prior gate green; DECISIONS
   locked). Update `STATUS.md` → phase `IN_PROGRESS`.
2. **Dispatch one subagent** with a prompt that is exactly: *"Read and execute
   `docs/guideants-guide/task-phase-N-*.md` end to end. Obey its guardrails and
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

Run/inspect these after every phase. Any failure blocks the next phase. Full
commands and baseline in [`test-gate.md`](./test-gate.md).

- [ ] **Server build green**: `cd src/server && dotnet build GuideAntsApi.sln`
      (0 errors; warning count not worse than baseline).
- [ ] **Server tests green**: `cd src/server && dotnet test GuideAntsApi.sln` — no
      new failures vs the Pre-flight baseline.
- [ ] **Client build green**: `cd src/client && npm run build` (tsc + vite, 0 errors).
- [ ] **Client tests green**: `cd src/client && npm test -- --run`.
- [ ] **No "fallback" anti-patterns** (per user rule — *fallback is a bug
      generator*). Grep the diff for newly added: `fallback`, default-identity/role
      (`?? "Admin"`, "first `Users` row"), empty `catch {}`, or `catch` that
      swallows a `401`/`403`/`404`. A missing/invalid principal must surface as
      `401`, an unauthorized one as `403`/`404` — never be masked.
- [ ] **No AppIdentity self-enable path**: `AuthMode = AppIdentity` is **only**
      settable by the seeder / internal publish helper. Grep proves the publish/
      update API rejects it (DECISIONS D-GG-A). No UI control sets it.
- [ ] **System project stays hidden**: it is excluded from `GET /api/projects` and
      all listings; non-admin access returns **404** (not 403 — do not leak
      existence). No stable system-project UUID is baked into the client bundle.
- [ ] **Scope discipline**: the subagent only touched files its brief authorized.
      Diff the file list against the brief's "Files in scope". Unexpected files =
      deviation.
- [ ] **No secrets committed** (no real keys/tokens in `appsettings*.json` or
      bootstrap guide JSON).
- [ ] **No new CodeQL findings** vs the pre-flight baseline — run the local gate
      ([`codeql-gate.md`](./codeql-gate.md)) at minimum after every
      **security-sensitive** phase (**2, 4, 6**) and at final acceptance.
      C# **must** use `build-mode=none`; **no GitHub parity** (inapplicable);
      **no alert suppression** — fix the code.
- [ ] **Matches `DECISIONS.md`** (cookie auth, settings link, auth mode rules). A
      subagent that minted/stored a token when D-GG-1 says same-host cookie, or
      built a self-enable UI for AppIdentity, is an automatic FAIL.

### 4.2 Per-phase gate criteria

Each is **in addition** to 4.1. Commands assume `src/server` or `src/client` cwd
as noted.

**Phase 1 — Data model & migrations**

- [ ] `dotnet ef migrations list` (DataModel project, GuideAntsApi startup) shows
      the new migration(s) at the head: `PublishedGuide.AuthMode` and
      `Project.IsSystemProject` (one or two migrations).
- [ ] `dotnet ef migrations script` review: adds `AuthMode` (int, default 0) and
      `IsSystemProject` (bit, default 0); **backfills** `AuthMode` from existing
      `ApiKeyHash` / `AuthValidationWebhookUrl` (ApiKey=2, Webhook=1, else 0); adds
      **no** `HasData` seed for users/projects/guides.
- [ ] Fresh-DB apply succeeds (`database drop --force` then `database update`);
      existing rows backfill to the correct `AuthMode`.
- [ ] `AuthValidationResult.InternalUserId` (nullable `Guid`) added to the result
      type only — **no behavior** wired yet.
- [ ] `DataModel.Tests` green; solution builds.

**Phase 2 — Published-guide AppIdentity auth** *(security-sensitive → CodeQL)*

- [ ] `PublishedGuideAuthMode` enum used by `PublishedGuideAuthService`; the
      `AppIdentity` branch validates the app JWT with the **same signing
      key/issuer/lifetime** as `RequireApprovedUser` (reuses the existing
      validator — no duplicated crypto), resolves the user, checks
      `SecurityStamp` + approval status, and sets `InternalUserId`.
- [ ] **Per D-GG-1**: the `AppIdentity` validator reads the JWT from the
      `GuideAnts.Auth` **session cookie** (same-host), validates it, and **rejects**
      missing/expired/invalid with `401` — never falls back to anonymous when
      `AuthMode == AppIdentity`. (May also accept `X-Published-Auth` Bearer for API
      testing.)
- [ ] Identity persists: user messages on AppIdentity guides write **both**
      `UserId` (= `InternalUserId`) and `ExternalUserIdentity` (GUID string);
      `PublishedConversationStreamPolicy.ResolveUserIdentityAsync` returns the
      internal id, not `null`.
- [ ] Publish/update API **rejects** `AuthMode = AppIdentity` (400) and rejects
      switching an AppIdentity row away from it.
- [ ] `GET /api/published/{pubId}` exposes `authMode` and `requiresAuth =
      authMode != Anonymous`.
- [ ] **CodeQL diff clean**: no new `cs/log-forging` (logging token/user claims),
      no clear-text token storage, no hard-coded signing key.

**Phase 3 — System seeder & bootstrap guides**

- [ ] `GuideAntsSystemSeeder` runs after `RequiredGuidesAssistantsSeeder`, is
      **idempotent** (second run creates no duplicates; repairs missing rows).
- [ ] First run creates: the **GuideAnts System** project (`IsSystemProject=true`,
      slug `guideants-system`), two guides (user + admin) + notebooks, two
      **internal** `PublishedGuide` rows (`Active`, `FriendlyName=null`,
      `CommandMode=true`, `AuthMode=AppIdentity`), and the `GuideAntsSystem`
      settings section with all IDs.
- [ ] Bootstrap guide folders exist with `manifest.json`, `instructions.md`, and
      `OpenAPI/Web Connector.json` using `client://guideants-app` and a single
      stub `AppEcho` operation (admin guide also lists future admin ops, not
      registered). Instructions reference the literal `operationId` `AppEcho`.
- [ ] Seeder unit tests: idempotency + settings round-trip green.

**Phase 4 — System project API & authorization** *(security-sensitive → CodeQL)*

- [ ] System project excluded from `GET /api/projects` and any home/recent/search
      aggregations (grep the query for `!IsSystemProject`).
- [ ] `SystemProjectAccessGuard` applied on `GET /api/projects/{id}`,
      `/details`, and all `/api/projects/{id}/…` notebook/file/guide routes:
      non-admin → **404**; admin → 200. `PUT`/`DELETE` on the system project
      itself is Admin-only and delete is blocked while `IsSystemProject`.
- [ ] `GET /api/system-guide/session` (`RequireApprovedUser`) returns the correct
      published guide id by role (Admin → admin guide; other approved → user
      guide); Pending/unapproved → 403. **Per D-GG-1** it returns **config only**
      (pub-id, role flags) — **no token in the body**.
- [ ] `GET /api/system-guide/workspace` (`RequireAdmin`) returns
      `{ projectId, projectSlug }`; non-admin → 404.
- [ ] Integration tests: non-admin cannot read system project; session returns
      role-correct pub-id; workspace is admin-only.
- [ ] **CodeQL diff clean**: no new findings from endpoint wiring; no token logging.

**Phase 5 — Publish UI read-only indication** *(frontend)*

- [ ] `authMode` added to client `PublishedGuideDto` / `types/guides.ts`.
- [ ] `AuthTab` shows a **read-only info panel** when `authMode === 'AppIdentity'`
      and **disables/hides** webhook URL, webhook timeout, API-key generate/remove.
- [ ] The form **never** sends `authMode` in `UpdatePublishedGuideDto` (no
      self-enable path from UI).
- [ ] Webhook/ApiKey/Anonymous behavior unchanged.
- [ ] Component test: AppIdentity guide shows indicator + controls disabled.
- [ ] **UI-convention gate**: reuses existing dialog/panel/button primitives; no
      new icon library or bespoke modal markup.

**Phase 6 — Guide flyout** *(security-sensitive → CodeQL, JS focus)*

- [ ] `GuideAntsGuideProvider` mounted in `App.tsx` (inside auth context);
      `GuideAntsGuideButton` is the **leftmost** child in **every** `HeaderActionsBar`
      call site listed in plan §8.2; hidden when `status !== 'authenticated'` or
      `role === 'Pending'`.
- [ ] Flyout fetches `GET /api/system-guide/session`, lazy-loads `guideants`,
      mounts `guideants-chat` with `pub-id` + `api-base-url` + `speech-to-text-enabled`
      (D-GG-5), relies on the **same-host session cookie** for auth (D-GG-1 — **no**
      `setAuthToken`, **no** token storage), wires `setContextProvider(...)` and the
      `guideantsAppBridge` stub (`AppEcho`).
- [ ] Context provider refreshes on route change while open.
- [ ] **Cookie transmission verified** (D-GG-1): the component actually sends the
      session cookie to the API (same-origin default, or `credentials:'include'` +
      CORS for cross-port). If it does not, this is a **blocker to escalate**, not a
      fallback.
- [ ] Component tests: button visibility by role; flyout open/close; session mock
      → `pub-id` set.
- [ ] **CodeQL diff clean (JS)**: **no** `js/clear-text-storage` (no token is
      stored by construction).

**Phase 7 — Settings access entry**

- [ ] Per D-GG-2 link shape: Settings header shows an **admin-only** "System
      Guides" entry; route guarded by `ProtectedRoute requireAdmin` **and** the
      server guard (Phase 4). Non-admin direct-navigation is redirected (UX) and
      404'd (server).
- [ ] Link is **not** added to Home, Projects, or global nav.
- [ ] Admin can reach the system project workspace and edit guide instructions.
- [ ] Component test: link admin-only; non-admin route → redirect.

**Phase 8 — Tests, docs, final acceptance**

- [ ] Full test matrix in plan §10.1/§10.2 covered by automated tests.
- [ ] Manual acceptance script (plan §10.3) executed and recorded in `STATUS.md`.
- [ ] `docs/developer-config-guide.md` updated with a brief System Guide overview.
- [ ] No real secrets in bootstrap JSON / appsettings.
- [ ] Final CodeQL diff clean; counts recorded in `STATUS.md`.

### 4.3 Gates summary

- **Test gate** ([`test-gate.md`](./test-gate.md)): build + unit/integration green
  with **no new failures vs baseline**, run after **every** phase.
- **CodeQL gate** ([`codeql-gate.md`](./codeql-gate.md)): local
  baseline-vs-current, **zero NEW findings**, run after **security-sensitive
  phases (2, 4, 6)** and at final acceptance. C# `build-mode=none`; code-scanning
  suites only; **no suppression — fix the code**.

---

## 5. Deviation & failure protocol

When a gate fails, **stop the line**. Do not start the next phase.

1. **Classify** the failure in `STATUS.md`:
   - **Build/test red** → mechanical; re-dispatch same subagent with the exact
     error output and the failing command.
   - **Missing DoD item** → the subagent under-delivered; re-dispatch with the
     specific unchecked items quoted.
   - **Scope creep** (touched out-of-scope files) → review those edits; revert the
     unauthorized ones unless genuinely required, in which case update the brief +
     `DECISIONS.md` first so the change is intentional and recorded.
   - **Decision drift** (built against the wrong DECISIONS value — e.g. token in
     `localStorage`, AppIdentity self-enable UI) → revert the phase's changes and
     re-dispatch with DECISIONS re-quoted at the top.
   - **Fallback/masking introduced** → hard reject; require removal. Per user rule,
     fallback logic that hides bugs (anonymous fallback on AppIdentity, swallowed
     401/404) is never acceptable.
2. **Re-dispatch** the *same* phase brief with a focused correction note appended
   ("Gate failed on X; fix only X; do not touch anything else"). Re-run the
   **full** gate afterward (not just the failed check) to catch regressions.
3. **Cap retries at 2.** If a third attempt is needed, escalate to the user with
   the gate output and your hypothesis — the brief itself may be wrong or a
   DECISIONS value may need to change.
4. **Record everything** in `STATUS.md`: attempt #, what failed, what changed,
   gate re-run result.

**Never** advance a phase to fix a problem in a later phase ("I'll wire the guard
in Phase 6") — that is how deviations compound. Fix it in the phase that owns it.

---

## 6. Final acceptance (after Phase 8 gate)

The plan is "executed fully" only when **all** hold:

- [ ] Every success criterion in `../guideants-guide-implementation-plan.md` §1 is
      satisfiable by pointing at a commit/file/test.
- [ ] Every row in the testing plan (plan §10.1/§10.2) has a passing automated test;
      §10.3 manual acceptance executed and recorded.
- [ ] Fresh install: seeder creates exactly one system project + 2 AppIdentity
      published guides; second run = no duplicates.
- [ ] Contributor and Admin both get a working flyout chat with the role-correct
      guide; non-admins cannot see or reach the system project.
- [ ] `AuthMode = AppIdentity` is not settable from any UI/API path.
- [ ] No token minted or stored client-side; flyout auth is the same-host cookie.
- [ ] Global invariants (4.1) green on the final tree.
- [ ] **Final CodeQL diff clean** ([`codeql-gate.md`](./codeql-gate.md)): zero new
      findings vs the pre-flight baseline; any new finding fixed in-code (never
      suppressed). Final counts recorded in `STATUS.md`.
- [ ] `STATUS.md` shows every phase `DONE` with a passing gate and no open
      deviations.

When all are checked, summarize the run (phases, retries, any DECISIONS that
changed mid-flight) for the user.
