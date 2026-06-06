# Task — Phase 0: Cleanup of stripped auth scaffolding

> Subagent brief. Read this top to bottom and execute it. Return the
> **Report-back contract** at the end verbatim. Do not exceed this scope.

## Mission

Remove dead/broken auth scaffolding left over from the OSS-lite strip-down so later
phases build on a clean base. **No behavior change** — this phase only deletes or
neutralizes already-dead code. If removing something would change runtime behavior,
**stop and report it instead** (it belongs to a later phase).

## Read first

- `../auth-system-plan.md` §2.2 (current stripped state) and §4 → **Phase 0**.
- `./DECISIONS.md` (D3 invariants).
- Files referenced below.

## Preconditions

- Pre-flight baseline recorded (builds + tests green). You are starting from green.

## Guardrails (hard)

- **Behavior-preserving only.** Builds and the full test suite must stay green with
  no test changes. A test delta is a failure of this phase.
- Do **not** add any auth wiring, endpoints, policies, or UI — those are Phases 2–5.
- **No "fallback" code.** Do not replace the `'oss-lite-token'` stub with another
  stub/default. Delete it and let Phase 2/5 introduce the real `AuthProvider`. If a
  caller breaks at compile time, leave a clearly-typed TODO and report it rather
  than inventing a placeholder.
- Touch only the files in scope below.

## Tasks

1. **Inventory** (report the list) of dead auth scaffolding:
   - Unused `ClaimsPrincipal` parameters in endpoint handlers (`Endpoints/*.cs`)
     that are never read.
   - Stale `RequireAuthorization()`/JWT comments left in server code.
   - Broken `/login` links in `src/client/src/components/ErrorScreen.tsx`,
     `src/client/src/pages/Terms.tsx`, `src/client/src/pages/Privacy.tsx`.
   - Unused `VITE_MSAL_*` env stubs in `src/client/src/env.d.ts` (and any MSAL test
     mocks).
   - The `'oss-lite-token'` stub in `src/client/src/services/authService.ts`.
2. **Remove/neutralize** the items above that are provably dead:
   - Delete unread `ClaimsPrincipal` params and stale comments.
   - Remove `VITE_MSAL_*` declarations and MSAL-only test mocks.
   - Remove the `'oss-lite-token'` stub. (The real provider lands in Phase 5; if a
     call site needs a temporary compile shim, prefer leaving the function throwing
     a `NotImplementedException`/typed TODO over returning a fake token — and
     report it.)
   - For the broken `/login` links: this phase **does not** create the route.
     Either remove the dead link or leave a `// TODO(Phase 5): real /login route`
     marker — do **not** leave a link that silently 404s without a marker. Report
     which you did for each file.
3. **Decide & document** (in the Report-back, not in code) whether the existing
   `Microsoft.Identity.Web` / `JwtBearer` NuGet refs in `GuideAntsApi.csproj` will
   be **reused** for our own JWT validation or **removed** in Phase 2. Do not remove
   them in this phase (Phase 2 owns that call).

## Files in scope

- `src/server/GuideAntsApi/Endpoints/*.cs` (only to drop unread `ClaimsPrincipal`
  params / stale comments)
- `src/client/src/services/authService.ts`
- `src/client/src/env.d.ts`
- `src/client/src/components/ErrorScreen.tsx`
- `src/client/src/pages/Terms.tsx`, `src/client/src/pages/Privacy.tsx`
- MSAL-only test mocks (report exact paths before deleting)

**Out of scope:** any `.csproj` edits, `Program.cs`/`StartupConfiguration.cs`,
data model, new files.

## Self-verification (run before reporting)

```
cd src/server && dotnet build GuideAntsApi.sln
cd src/server && dotnet test GuideAntsApi.sln
cd src/client && npm run build
cd src/client && npm test -- --run
cd src/client && npm run find-orphans
```

All must be green; `find-orphans` should show fewer orphans than baseline for the
removed items.

## Definition of Done

- [ ] Inventory produced.
- [ ] Dead items removed/neutralized per Tasks, no behavior change.
- [ ] No new stub/fallback introduced.
- [ ] Builds + tests green, unchanged test results.
- [ ] NuGet reuse-vs-remove recommendation recorded for Phase 2.

## Report-back contract (return exactly this)

```
PHASE 0 REPORT
- Inventory found: <bulleted list with file:line>
- Removed: <list>
- Neutralized with TODO markers: <list with file:line and reason>
- Compile shims left (and why not a fallback): <list or "none">
- NuGet recommendation (reuse vs remove Identity.Web/JwtBearer): <reuse|remove> + 1-line reason
- Verification: build(server)=<pass/fail> test(server)=<counts> build(client)=<pass/fail> test(client)=<counts> find-orphans=<delta>
- Files touched: <exhaustive list>
- Deviations / surprises: <list or "none">
```
