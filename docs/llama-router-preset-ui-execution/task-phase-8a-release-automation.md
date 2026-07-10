# Task — Phase 8A: Automated release closeout

> Subagent brief. May run in parallel with Phase 8B. Return the report contract verbatim.

## Mission

Close the feature with deterministic migration/contract coverage, generated OpenAPI,
full automated tests, image builds, security scanning, and operator/developer
documentation. This phase verifies prior implementation; behavior bugs return to
their owning phase.

## Read first

- Entire proposal, especially §§7–10.
- `./00-orchestration.md` §§5–8, `DECISIONS.md`, current `STATUS.md`.
- Reports/diffs for Phases 1A–7.
- Existing Swagger export and endpoint coverage scripts.
- `installer/README.md`, Docker llama docs, settings architecture docs, local-model
  runtime/download docs.
- `./codeql-gate.md`

## Preconditions

- Phase 7 gate passed. Phase 8B may run concurrently but edits no product source.

## Hard guardrails

- Do not change runtime behavior to make a release test pass. Report the owning
  phase and stop that acceptance line.
- Generated OpenAPI comes from the running API, never hand-edited.
- A migration fixture with an issue must assert the issue, not coerce it to success.
- No live-network dependency in the deterministic test suite.
- Documentation must distinguish authoritative stores and desired/applied state.
- Do not mark unavailable image/tool infrastructure as passed.

## Tasks

1. Add/complete deterministic fixtures from `STATUS.md`: fresh, legacy clean,
   unmapped load keys, profile agreement/disagreement, hand-edited INI, no
   provenance, interrupted operation, and migration re-run.
2. Add end-to-end contract tests spanning:
   catalog → quant → curated add → operation → model/provenance → inventory;
   custom shards; attach preservation; fleet desired/applied; profile tool request;
   repair; change quant; Customize/adoption.
3. Add negative tests for no quant choice, changed commit, incomplete shards,
   identity conflict, missing projector, gated access, path escape, preset scope,
   restart failure, finalization failure, and concurrent alias operation.
4. Regenerate `guideants-swagger.json` from a running API. Confirm all new routes,
   schemas, security requirements, enum/status/error fields, and removed legacy
   request fields, including D8 migration and lifecycle routes.
5. Run the existing Swagger/client endpoint coverage script and explain every
   server-only route; no client call may target a missing route.
6. Build/test all source:
   server solution, client build/tests/orphans, Python tests/compile, shell syntax,
   schema tests. Python uses the exact Phase 0 command:
   `python -m unittest discover -s docker/build/guideants-ai/llama-admin-service/tests -p "test_*.py" -v`.
7. Build/smoke the guideants-ai CPU, CUDA, ROCm, and Vulkan Docker variants using
   the exact commands/tags frozen by Phase 0. Confirm manifest, shared
   `guideants_hf`, llama admin, runtime profiles/projection, and scripts are present
   in each image.
8. Run final CodeQL C#/Python/JS diff and manual security checks.
9. Add/update documentation:
   - curated install and installed-model lifecycle;
   - custom/attach/Customize/adoption;
   - fleet vs alias vs profile vs catalog vs provenance ownership;
   - first-boot compose seed and desired/applied troubleshooting;
   - migration report and resolving each issue type;
   - manifest authoring/versioning and live drift suite;
   - repair/change-quant failure recovery;
   - API contracts and developer test commands.
10. Produce a proposal §9 coverage checklist linking every criterion to test,
    endpoint, UI, migration, or Phase 8B evidence slot. Map proposal §§3.6–3.8 and
    §7.2 explicitly to D8 lifecycle routes.
11. Run authorization integration checks for every new catalog, fleet, migration,
    router, operation, installation, and lifecycle route: unauthenticated `401`,
    non-Admin `403`, and Admin contract behavior.

## Files in scope

- Tests/fixtures across server, client, Python, schema, and scripts.
- Generated `guideants-swagger.json`.
- Documentation and release-check artifacts.
- Build files only if required to include already-implemented assets in every image.

Out of scope: new product behavior, contract redesign, manifest entry changes,
runtime migration logic.

## Self-verification

Run every command named in tasks 4–8 and record exact counts/image IDs. Run all
migration fixtures twice. Validate every docs command against the final tree.

## Definition of Done

- [ ] Every deterministic fixture and cross-layer contract test passes.
- [ ] OpenAPI and client route coverage match.
- [ ] Full server/client/Python/shell/schema/image gates pass.
- [ ] Final CodeQL has zero new findings.
- [ ] Documentation covers operator, migration, troubleshooting, manifest, and API.
- [ ] Proposal §9 has a complete evidence map.

## Report-back contract

```text
PHASE 8A REPORT
- Migration fixtures: fresh=<p/f> clean=<p/f> unmapped=<p/f> profile-conflict=<p/f> INI=<p/f> interrupted=<p/f> rerun=<p/f>
- Cross-layer E2E tests: <list/count/results>
- Negative contract tests: <list/count/results>
- OpenAPI: generated=<command> routes/schemas/security=<p/f> legacy-fields-absent=<p/f>
- Client endpoint coverage: <result/explanations>
- Full verification: server-build=<p/f> server-tests=<counts> client-build=<p/f> client-tests=<counts> orphans=<delta> python=<counts> shell=<p/f> schema=<p/f>
- Images: cpu=<id/result> cuda=<id/result> rocm=<id/result> vulkan=<id/result>
- Authorization matrix: unauthenticated=<401 cases> non-admin=<403 cases> admin=<contract results>
- CODEQL REPORT: <required block from codeql-gate.md>
- Docs: <paths>
- Proposal §9 evidence map: <path/section>
- Owning-phase bugs discovered: <none or phase + evidence>
- Files touched: <exhaustive list>
- Deviations / surprises: <list or none>
```
