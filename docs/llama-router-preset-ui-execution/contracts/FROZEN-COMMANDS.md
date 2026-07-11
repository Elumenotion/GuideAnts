# Phase 0 frozen commands and environments

## Python test harness (locked)

```bash
python -m unittest discover -s docker/build/guideants-ai/llama-admin-service/tests -p "test_*.py" -v
```

Phase 1A creates the suite. Schema validator dependency (Phase 1A): `jsonschema` Draft 2020-12 via project dependency mechanism.

## Contract validation

```bash
# Python (host or container /opt/venv/bin/python)
python docs/llama-router-preset-ui-execution/contracts/validate-contracts.py

# C#
dotnet run --project docs/llama-router-preset-ui-execution/contracts/ContractsValidation.csproj

# TypeScript
npx tsx docs/llama-router-preset-ui-execution/contracts/validate-contracts.ts

# D8/D11 collision
node docs/llama-router-preset-ui-execution/contracts/check-routes-and-manifest.mjs
```

## Physical proofs

```bash
docker run --rm --entrypoint bash \
  -v "<repo>/docs/llama-router-preset-ui-execution/contracts/phase0-proofs:/work:ro" \
  -v "<repo>/docs/llama-router-preset-ui-execution/contracts/phase0-proofs/output:/work-out" \
  -e WORKDIR=/tmp/phase0-d1 \
  -e RESULTS_FILE=/work-out/results-d1.txt \
  guideants-ai:rocm-latest \
  /work/run-d1-shard-proof.sh
```

## GuideAnts AI image build commands (from docker/guideants-ai-build.md + build_guideants_ai.ps1)

From repository root, with `$env:DOCKER_BUILDKIT=1`:

| Variant | Script | Dockerfile | Buildx target | Image tags |
| --- | --- | --- | --- | --- |
| CPU | `pwsh docker/build/build_guideants_ai.ps1 -Backend cpu` | `docker/build/guideants-ai/Dockerfile.cpu` | `final-cpu` (deps: `deps-cpu`) | `guideants-ai:cpu-<yyDDD>.<HHmm>`, `guideants-ai:cpu-latest` |
| CUDA 13 | `pwsh docker/build/build_guideants_ai.ps1 -Backend cuda13` | `docker/build/guideants-ai/Dockerfile.cuda` | `final-cuda13` (deps: `deps-cuda13`) | `guideants-ai:cuda13-<yyDDD>.<HHmm>`, `guideants-ai:cuda13-latest` |
| ROCm | `pwsh docker/build/build_guideants_ai.ps1 -Backend rocm` | `docker/build/guideants-ai/Dockerfile.rocm` | `final-rocm` (deps: `deps-rocm`) | `guideants-ai:rocm-<yyDDD>.<HHmm>`, `guideants-ai:rocm-latest` |
| Vulkan | `pwsh docker/build/build_guideants_ai.ps1 -Backend vulkan` | `docker/build/guideants-ai/Dockerfile.vulkan` | `final-vulkan` (deps: `deps-vulkan`) | `guideants-ai:vulkan-<yyDDD>.<HHmm>`, `guideants-ai:vulkan-latest` |

Equivalent manual final-stage pattern (example CPU):

```powershell
docker buildx build --load `
  --build-arg GA_DEPS_CPU_IMAGE=guideants-ai-deps:cpu-<hash12> `
  --target final-cpu `
  -t guideants-ai:cpu-<yyDDD>.<HHmm> `
  -t guideants-ai:cpu-latest `
  -f docker/build/guideants-ai/Dockerfile.cpu `
  docker/build/guideants-ai
```

GHCR publish tags (workflow): `ghcr.io/<owner>/guideants-ai-{cpu,cuda13,rocm,vulkan}` with `main`, `sha-*`, `latest`.

## Verification tiers (recorded environments)

| Tier | Command / environment | Phase 0 status |
| --- | --- | --- |
| Deterministic Python fixtures | contract validators above + locked unittest harness | fixtures committed; harness directory absent (Phase 1A) |
| 14 live repository checks | release CI / recorded gate with HF token | BLOCKED (no HF token in Phase 0) |
| API integration tests | `dotnet test src/server/GuideAntsApi.sln --filter FullyQualifiedName~IntegrationTests` | runnable; llama hardware tests skipped without runtime |
| Frontend parity | `cd src/client && npm test -- --run` | PASS (3361 tests) |
| Migration fixtures | Phase 3+ migration status/issues endpoints | fixtures frozen only |
| Hardware qualification | representative image × accelerator × model (proposal §8) | BLOCKED (no GPU qualification lane recorded) |

## CodeQL baseline

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/run-codeql-sln-triage.ps1 -CleanCodeqlOutputs -SkipGitHubParityCheck
```

Phase 0 host: CodeQL CLI not installed (`codeql=NOT_INSTALLED`).
