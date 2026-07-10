# Task — Phase 0: Contracts, physical proofs, and baseline

> Subagent brief. Execute top to bottom and return the report contract verbatim.

## Mission

Establish a safe starting point and prove the two runtime assumptions that later
phases depend on: how the bundled llama.cpp opens sharded GGUFs from a models preset,
and how revisioned fleet settings can be projected into every router respawn.
Freeze cross-language DTO fixtures. Do not implement product behavior.

## Read first

- Proposal §§2.3–2.6, 4.3, 4.7, 5, 6, 7, 9.
- `./DECISIONS.md`, especially D1, D2, D4, D5, D8.
- `docker/build/guideants-ai/start-llama.sh`
- `docker/build/guideants-ai/entrypoint.sh`
- `docker/build/guideants-ai/llama-admin-service/llama_admin_service.py`
- `src/server/GuideAntsApi/Services/LlamaCpp/LlamaRuntimeAdminClient.cs`
- `src/server/GuideAntsApi/Services/LlamaCpp/LocalModelOnboarding/*`
- `src/client/src/features/localModelOnboarding/*`
- `./codeql-gate.md`

## Preconditions

- None. This is the only initially ready phase.

## Hard guardrails

- Do not change product source, migrations, manifests, compose files, or API behavior.
- Do not alter/discard current modified or untracked files.
- Test fixtures/spike artifacts must be temporary or live under this execution
  folder; do not commit downloaded model weights.
- Do not record tokens, local secrets, or machine-specific credentials.
- A failed proof is a blocking result, not a reason to choose a convenient value.

## Tasks

1. Inventory every existing modified/untracked file related to this proposal.
   Classify it as accepted starting work for a named phase, unrelated user work, or
   generated output. Record the list for the orchestrator; do not edit it.
2. Capture exact tool versions and baseline commands/results:
   - server build/tests and EF migration head;
   - client build/tests/orphan scan;
   - Python compile and existing Python tests;
   - `bash -n` for runtime scripts;
   - current Docker build/smoke mechanism for CPU/CUDA/ROCm/Vulkan;
   - local CodeQL baseline from the accepted tree.
3. Prove D1 with the bundled llama.cpp version:
   - create/use a valid small split-GGUF fixture or an existing two-shard model;
   - put the ordered first shard in one temporary INI section;
   - show the router/child discovers every shard and fails when a shard is absent;
   - record exact filenames, INI, command, version, and result.
4. Prove D4 without implementing it:
   - verify `start-llama.sh` can consume a revisioned projection file on each spawn;
   - verify an atomic file replacement is visible after SIGTERM/respawn;
   - document how desired/applied revision and an apply error can be observed;
   - prove alias-only keys can be excluded from fleet arguments.
5. Commit representative JSON fixtures under
   `docs/llama-router-preset-ui-execution/contracts/`:
   catalog, quant group, router entry GET/PUT, fleet GET/PUT, curated add, immutable
   operation input/status, provenance, change quant, repair, adoption, and custom add.
   Include internal llama-admin route payloads from D8. Treat D12 as authoritative:
   the MTP resolution fixture must omit projector and `image-min-tokens`.
6. Parse each applicable fixture with a minimal Python, C#, and TypeScript contract
   test or Phase 0 validation script. Later phases must import/copy these exact files,
   not rewrite private equivalents.
7. Validate route naming and manifest shape against existing endpoint/catalog
   conventions and confirm D8/D11 have no collision.
8. Lock the Python test harness to:
   `python -m unittest discover -s docker/build/guideants-ai/llama-admin-service/tests -p "test_*.py" -v`.
   Phase 1A creates this suite and includes JSON Schema validation with an explicit
   recorded validator dependency. Later Python phases use this command unchanged.
9. Identify test commands/environments for:
   deterministic Python fixtures, 14 live repository checks, API integration tests,
   frontend parity, migration fixtures, and representative hardware tests.
   Record exact CPU/CUDA/ROCm/Vulkan build commands and image tags from the current
   repository docs; “use the project command” is not sufficient.

## Files in scope

- Read-only product tree.
- Required contract fixtures/validation artifacts only under
  `docs/llama-router-preset-ui-execution/contracts/`.

Out of scope: all product implementation and migration generation.

## Self-verification

Run and record every baseline from task 2. Re-run the shard proof once with a
missing shard and confirm it fails explicitly. Re-run the fleet proof with a stale
revision and confirm desired/applied mismatch remains visible.

## Definition of Done

- [ ] Worktree ownership inventory is exhaustive.
- [ ] Exact baseline counts/versions/results are returned.
- [ ] D1 is proven or contradicted with reproducible evidence.
- [ ] D4 projection/restart mechanics are proven or contradicted.
- [ ] Committed cross-language contracts, internal/public routes, and manifest shape
      are frozen and parsed.
- [ ] Exact Python/schema/image commands are frozen.
- [ ] Test environments and release prerequisites are explicit.
- [ ] No product code changed.

## Report-back contract

```text
PHASE 0 REPORT
- Accepted worktree identity: <branch/SHA + dirty-file ownership inventory>
- Tools: dotnet=<v> ef=<v> node=<v> npm=<v> python=<v> docker=<v> bash=<v> codeql=<v>
- Baseline: server-build=<p/f> server-tests=<count> client-build=<p/f> client-tests=<count> orphans=<count>
- Python/shell/images baseline: <commands and results>
- CodeQL baseline: C#=<n> Python=<n> JS=<n> SARIF location=<path>
- D1 shard proof: bundled-version=<v> INI-model-value=<value> complete=<result> missing-shard=<result>
- D4 fleet proof: projection=<result> atomic-replace=<result> respawn-read=<result> desired/applied-observable=<result>
- Frozen contracts: <committed exhaustive list under contracts/>
- Cross-language parse: Python=<p/f> C#=<p/f> TypeScript=<p/f>
- D8 route + D11 manifest collision check: <result>
- Frozen commands: python=<exact> schema-validator=<exact/dependency> images=<exact per variant>
- Required credentials/hardware: <available/blockers>
- Files touched: <must be none, or contract fixtures only>
- Decision contradiction: <none or exact evidence and affected decisions>
- Deviations / surprises: <list or none>
```
