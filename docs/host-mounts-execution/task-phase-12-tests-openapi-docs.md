# Task — Phase 12: Integration tests, OpenAPI, documentation, final acceptance

> Subagent brief. Execute top to bottom; return the Report-back contract verbatim.
> **Docs + full docker + final CodeQL gate.**

## Mission

Close out the feature: consolidate the cross-cutting test matrix, regenerate the
OpenAPI surface, write the admin/security documentation, and carry SMB/CIFS into an
explicit deferred follow-on. This is the phase the orchestrator's **final
acceptance** (§6) reads from.

## Read first

- `../host-folder-notebook-mounts-plan.md` §22.11 (tests), §22 follow-on (SMB),
  §20/§20.1 (security to document), §5/§6 (runtime config + helper scripts to
  document).
- `./00-orchestration.md` §6 (final acceptance), `./docker-gate.md`,
  `./codeql-gate.md`.
- `scripts/find-unused-api-endpoints.mjs` (swagger vs client check).
- The existing Swagger artifact (`guideants-swagger.json` if present) + how it is
  produced.
- The `docs/` layout + the auth-system docs for documentation style.

## Preconditions

- Phase 11 gate green. All prior phases `DONE` in `STATUS.md`.

## Guardrails (hard)

- The test matrix must explicitly cover plan §22.11: **path validation**, **symlink
  creation/removal**, **project-scope notebook creation**, **script-agent
  authorization** (positive **and** the mandatory negative/unregistered-link case).
- **No fallback** sneaking into tests (e.g. tests that assert success when a source
  is missing). A missing source / unregistered link must be asserted to **fail/flag**,
  not pass.
- Documentation must include: admin runbook (map/remove/check, the self-restart
  session-drop caveat), the `GuideAntsRuntime__*` env vars, the helper scripts, the
  **security model** (registered-links-only, host-path/credential handling), and the
  no-`External/` layout.
- SMB/CIFS is **deferred**: document it as a follow-on (override CIFS branch +
  credential handling §20.1 + reuse of the symlink/guard/reconciliation layers
  unchanged). Do **not** implement it here unless the orchestrator explicitly pulled
  it forward.
- Swagger: the new admin endpoints must carry the correct security scheme; do not
  expose host paths/credentials in examples.

## Tasks

1. Consolidate/верify the §22.11 test matrix across unit + integration; fill any gaps
   left by earlier phases (especially the negative script-agent authorization case if
   not already present).
2. Regenerate the OpenAPI/Swagger artifact (run the API; fetch
   `/swagger/v1/swagger.json`); confirm the mount endpoints + security scheme; then
   run `node scripts/find-unused-api-endpoints.mjs` and confirm no surprises.
3. Write documentation under `docs/` (e.g. `docs/host-folder-mounts.md` or extend the
   plan's companion docs): admin runbook, runtime config, helper scripts, security
   model, troubleshooting (Missing source / Link error / Pending removal), and the
   deferred-SMB section.
4. Run the **full docker gate** (`docker-gate.md` §3.1–3.5 + an end-to-end `up`
   smoke + health) and the **final CodeQL diff** (`codeql-gate.md` final row).
5. Update `STATUS.md` final-acceptance checklist and the CodeQL/docker ledgers.

## Files in scope

- `src/server/GuideAntsApi.IntegrationTests/*`, `GuideAntsApi.Tests/*`,
  `ScriptExecutionAgent.Tests/*` (gap-fill only)
- Swagger artifact (regenerated)
- `docs/host-folder-mounts.md` (+ any companion doc updates)
- `docs/host-mounts-execution/STATUS.md` (ledger update)

**Out of scope:** new feature behavior (all behavior shipped in Phases 1–11);
implementing SMB.

## Self-verification

```bash
cd src/server && dotnet build GuideAntsApi.sln && dotnet test GuideAntsApi.sln
cd src/client && npm run build && npm test -- --run
node scripts/find-unused-api-endpoints.mjs --swagger <swagger.json> --client src/client/src
docker compose -f docker/docker-compose.ghcr-cpu.yml -f docker/docker-compose.host-mounts.generated.yml config > /dev/null && echo CONFIG_OK
```

Plus the full global gate (orchestration §4.1), docker gate, and final CodeQL diff.

## Definition of Done

- [ ] §22.11 test matrix complete and green (incl. negative script-agent case).
- [ ] Swagger regenerated with mount endpoints + security scheme;
      `find-unused-api-endpoints` shows no surprises.
- [ ] Documentation merged (admin runbook, runtime config, helper scripts, security
      model, troubleshooting, deferred-SMB section).
- [ ] Full docker gate green (incl. end-to-end `up` smoke + health).
- [ ] **Final CodeQL diff clean** (zero new vs baseline); counts in `STATUS.md`.
- [ ] `STATUS.md` final-acceptance checklist all checked; no open deviations.

## Report-back contract (return exactly this)

```
PHASE 12 REPORT
- §22.11 matrix coverage: validation=<y> symlink=<y> project-scope-create=<y> script-auth pos/neg=<y/y>
- Swagger regenerated + security scheme on mount endpoints: <yes>
- find-unused-api-endpoints surprises: <none/list>
- Docs written (paths): <list> incl. deferred-SMB section: <yes>
- DOCKER GATE (full + up smoke + health): <pass/fail>
- CODEQL final diff: new-vs-baseline=<count -> rules or none>
- Verification: server build/tests=<counts> client build/tests=<counts>
- STATUS.md final-acceptance updated: <yes>
- Files touched: <list>
- Deviations / surprises: <list or "none">
```
