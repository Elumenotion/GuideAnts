# Curated Local Llama — Phase 8A Developer Test Commands

Frozen commands from `contracts/FROZEN-COMMANDS.md`.

## Server

```powershell
cd src/server
dotnet build GuideAntsApi.sln
dotnet test GuideAntsApi.sln --no-build
```

Targeted llama closeout filters:

```powershell
dotnet test GuideAntsApi.sln --filter "FullyQualifiedName~LocalModelMigrationServiceTests"
dotnet test GuideAntsApi.sln --filter "FullyQualifiedName~LlamaCrossLayerContractTests"
dotnet test GuideAntsApi.sln --filter "FullyQualifiedName~LlamaNegativeContractTests"
dotnet test GuideAntsApi.sln --filter "FullyQualifiedName~LlamaAuthorizationEndpointsTests"
dotnet test GuideAntsApi.sln --filter "FullyQualifiedName~GuideantsSwaggerExportTests"
```

## Client

```powershell
cd src/client
npm run build
npm test -- --run
npm run find-orphans
```

## Python (deterministic)

```powershell
python -m unittest discover -s docker/build/guideants-ai/llama-admin-service/tests -p "test_*.py" -v
```

Excludes `test_live_manifest_drift.py` when run with the pattern above (live network deferred to Phase 8B).

## Contract validators

```powershell
python docs/llama-router-preset-ui-execution/contracts/validate-contracts.py
dotnet run --project docs/llama-router-preset-ui-execution/contracts/ContractsValidation.csproj
npx tsx docs/llama-router-preset-ui-execution/contracts/validate-contracts.ts
node docs/llama-router-preset-ui-execution/contracts/check-routes-and-manifest.mjs
```

## Shell syntax

```powershell
bash -n docker/build/guideants-ai/start-llama.sh
```

## OpenAPI export

```powershell
cd src/server
dotnet test GuideAntsApi.IntegrationTests/GuideAntsApi.IntegrationTests.csproj --filter "FullyQualifiedName~GuideantsSwaggerExportTests" --no-build
```

Writes `guideants-swagger.json` at repository root from `GET /swagger/v1/swagger.json`.

## Client route coverage

```powershell
node scripts/find-unused-api-endpoints.mjs --swagger guideants-swagger.json --client src/client/src
```

## Docker images (Phase 0 tags)

```powershell
$env:DOCKER_BUILDKIT=1
pwsh docker/build/build_guideants_ai.ps1 -Backend cpu
pwsh docker/build/build_guideants_ai.ps1 -Backend cuda13
pwsh docker/build/build_guideants_ai.ps1 -Backend rocm
pwsh docker/build/build_guideants_ai.ps1 -Backend vulkan
```

## CodeQL (Phase 8A only)

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/run-codeql-sln-triage.ps1 -CleanCodeqlOutputs -SkipGitHubParityCheck
```

Save baseline SARIF under `.codeql/baseline/` after first accepted scan.

## Migration fixtures

Run `LocalModelMigrationServiceTests` twice in one session to verify idempotency:

```powershell
dotnet test GuideAntsApi.sln --filter "FullyQualifiedName~LocalModelMigrationServiceTests" --no-build
dotnet test GuideAntsApi.sln --filter "FullyQualifiedName~LocalModelMigrationServiceTests" --no-build
```
