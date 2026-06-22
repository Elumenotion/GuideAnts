# Task — Phase 5: Cost limits and reporting

> Subagent brief. Execute top to bottom and return the Report-back contract
> verbatim.

## Mission

Enforce daily/monthly cost limits and expose wire API usage reporting without
polluting conversation-only views.

## Read first

- `../published-wire-api-implementation-plan.md` (Phase 5 section)
- `./DECISIONS.md` (monthly UTC semantics and source-channel attribution)
- `./test-gate.md`
- Existing usage summary/reporting services and published guide cost-limit
  service.

## Preconditions

- Phase 4 gate is green.

## Guardrails (hard)

- Do not force non-conversation events into conversation-only drilldowns.
- Keep owner/project totals inclusive of all usage.
- Cost-limit queries must use indexed notebook/published/date paths.

## Tasks

1. Update `PublishedGuideCostLimitService` to enforce:
   - daily UTC limits
   - monthly UTC limits
2. Rename UI copy from billing-period limit to monthly limit, unless a real
   subscription-period model is introduced.
3. Update guide usage summary/charts to include non-conversation totals.
4. Keep conversation drilldowns conversation-only.
5. Add API usage report grouped by:
   - source channel
   - endpoint
   - alias
   - provider/service mode
   - status family
   - events
   - charge
6. Add source filters:
   - conversation
   - published chat
   - MCP
   - wire API
7. Add tests for daily and monthly exceedance and reporting grouping.

## Files in scope

- Cost limit services/queries
- Usage reporting endpoints/services/DTOs
- Client reporting views/filters/copy updates tied to this phase
- Tests for limit enforcement and reporting breakdowns

Out of scope:

- New endpoint protocol behavior unrelated to cost/reporting
- Publishing APIs tab controls (Phase 6)

## Self-verification

```powershell
cd src/server
dotnet build GuideAntsApi.sln
dotnet test GuideAntsApi.sln

cd ../client
npm run build
npm test -- --run
```

## Definition of Done

- [ ] Daily and monthly (UTC) limits enforced.
- [ ] Billing-period copy replaced with monthly-limit copy where applicable.
- [ ] Non-conversation usage included in totals.
- [ ] Conversation drilldowns remain conversation-only.
- [ ] API usage report and source filters implemented.
- [ ] Tests cover reporting + daily/monthly exceedance.
- [ ] Global test gate passes vs baseline.

## Report-back contract (return exactly this)

```text
PHASE 5 REPORT
- Daily/monthly UTC enforcement: <what changed>
- Billing-period -> monthly copy updates: <paths>
- Usage totals/drilldown behavior: <how verified>
- API usage report grouping fields: <list>
- Source filters: <list>
- Reporting/cost-limit tests: <paths + summary>
- Build/tests: <counts>
- Files touched: <list>
- Deviations: <list or none>
```
