# Task — Phase 5: Publish UI read-only App-Identity indication

> Subagent brief. Execute top to bottom; return the Report-back contract verbatim.

## Mission

Make the publish dialog **indicate** (read-only) when a published guide uses
`AppIdentity` auth, and **prevent** any configuration of it from the UI. All other
auth modes behave exactly as today.

## Read first

- `../guideants-guide-implementation-plan.md` §4.5 (read-only indication, exact
  requirement), §4.4 (public config DTO shape).
- `./DECISIONS.md` → **D-GG-A** (no self-enable from UI), **D-GG-5** (UI defaults).
- `PublishGuideDialog.tsx`, `configTabs/AuthTab.tsx`, `types/guides.ts`
  (`PublishedGuideDto`, `UpdatePublishedGuideDto`).
- The existing read-only/info panel + disabled-control primitives already used in
  these dialogs (reuse them; no new components/icon libs).

## Preconditions

- Phase 2 gate green (server exposes `authMode` on the published config DTO).

## Guardrails (hard)

- **No new Auth-tab control** for AppIdentity (no radio/toggle). It is **read-only
  indication only** (D-GG-A).
- The form must **never** include `authMode` in `UpdatePublishedGuideDto` payloads
  (no self-enable/disable path).
- When `authMode === 'AppIdentity'`: show the info panel and **hide/disable**
  webhook URL, webhook timeout, API-key generate/remove — anything implying another
  mode.
- When `authMode` is `Webhook` / `ApiKey` / `Anonymous`: **unchanged** behavior.
- Reuse existing dialog/panel/button/typography primitives. No new icon library, no
  bespoke modal markup (UI-convention gate, orchestration §4.2 Phase 5).
- Frontend only. Do not touch server code.

## Tasks

1. Add `authMode` to client `PublishedGuideDto` (and any mapping) in
   `types/guides.ts`; type it as the four-value union.
2. In `AuthTab`, when `authMode === 'AppIdentity'`, render a read-only info panel:
   *"Authentication: GuideAnts app identity — callers must present a signed-in
   GuideAnts user token. Managed by the system; cannot be changed here."*
3. Disable/hide webhook + API-key controls in that state.
4. Ensure the form's update payload omits `authMode`.
5. Component test: AppIdentity guide → panel shown + controls disabled; a
   Webhook/ApiKey guide → unchanged controls.
6. (Optional, D-GG-5) `GuideCard` published-badge tooltip mentions app-identity.

## Files in scope

- `src/client/src/types/guides.ts`
- `src/client/src/components/guides/PublishGuideDialog.tsx`
- `src/client/src/components/guides/configTabs/AuthTab.tsx`
- `src/client/src/components/guides/__tests__/…` (component test)
- (optional) `GuideCard.tsx`

**Out of scope:** server, seeder, flyout, settings route.

## Self-verification

```powershell
cd src/client; npm run build
cd src/client; npm test -- --run
```

## Definition of Done

- [ ] `authMode` on client `PublishedGuideDto`.
- [ ] AppIdentity → read-only info panel + webhook/API-key controls disabled/hidden.
- [ ] Update payload never carries `authMode`.
- [ ] Other modes unchanged.
- [ ] Component test green; build + client tests green; UI conventions respected.

## Report-back contract (return exactly this)

```
PHASE 5 REPORT
- authMode added to client PublishedGuideDto: <yes>
- AppIdentity read-only panel: <yes>  controls disabled/hidden: <list>
- Update payload omits authMode: <yes>
- Other modes unchanged: <verified how>
- New components/icon libs introduced: <none>
- Component test: <name>  build: <pass/fail>  client tests: <counts>
- Files touched: <exhaustive list>
- Deviations / surprises: <list or "none">
```
