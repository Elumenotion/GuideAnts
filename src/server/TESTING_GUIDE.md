# Server-Side Test Suite Guide

## Test projects

| Project | Purpose | Coverage |
|---------|---------|----------|
| `GuideAntsApi.Tests` | Unit tests (mocks, in-memory EF) | Yes (`coverlet.collector`) |
| `GuideAntsApi.IntegrationTests` | Full host + SQL Server (Testcontainers) | Yes (`coverlet.collector`) |
| `ScriptExecutionAgent.Tests` | HTTP integration tests for script sidecar | Yes (`coverlet.collector`) |
| `GuideAntsApi.DataModel.Tests` | Entity / migration checks | No |

## Run tests

From `src/server`:

```powershell
# All tests (no coverage)
dotnet test GuideAntsApi.sln

# Coverage report (unit + integration + script agent, HTML + console summary)
pwsh -File ./run-test-coverage.ps1

# Unit tests only (faster; includes ScriptExecutionAgent.Tests)
pwsh -File ./run-test-coverage.ps1 -Scope Unit

# Integration tests only (requires Docker)
pwsh -File ./run-test-coverage.ps1 -Scope Integration
```

Coverage output:

- Raw Cobertura XML: `TestResults/**/coverage.cobertura.xml`
- HTML report: `coverage-report/index.html`
- Text summary: `coverage-report/Summary.txt`

Target: **≥ 85% line coverage** on production logic (`src/RULES.md`).

### Coverage exclusions

`coverlet.runsettings` excludes non-logic code from metrics:

- API DTOs (`GuideAntsApi/Models/**`)
- EF entity POCOs (`GuideAntsApi.DataModel/Models/**`)
- Provider wire types (`AntRunner.Chat.*` request/response DTOs)
- Migrations, design-time factories, static bootstrap resources

## ScriptExecutionAgent tests

`ScriptExecutionAgent.Tests` launches the real agent process (`dotnet ScriptExecutionAgent.dll`) and exercises `/health`, `/execute`, and `/files` over HTTP. Tests cover auth, validation, path authorization, and script execution (when `python` is available).

Requires no Docker. Tests are `[DoNotParallelize]` because they bind process environment variables.

## Integration test notes

- Requires **Docker** for SQL Server Testcontainers (`GuideAntsApi.IntegrationTests`).
- Assembly is `[DoNotParallelize]` — do not enable parallel execution.
- Extend `TestContainerManager` via its public API; do not change core disposal logic.
