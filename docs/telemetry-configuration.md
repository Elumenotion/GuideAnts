# GuideAnts Telemetry and Visibility Configuration

This document describes the visibility surfaces GuideAnts uses today and how to tune them when debugging the API, background processing, model routing, local runtimes, document extraction, speech, search, storage, database behavior, and usage accounting.

The main API is the ASP.NET Core service in `src/server/GuideAntsApi`. Most runtime visibility comes from structured `ILogger<T>` logs, database-backed operational records, Settings Infrastructure probes, Docker/container logs, and subsystem-specific health/readiness endpoints.

## Configure API Visibility

Use **Settings -> Telemetry** for API logging visibility. The tab writes global settings to the database-backed `Telemetry` application settings section and the API reloads those values immediately after save. A container restart is not required for API log-level changes.

The tab provides subsystem presets for common investigations and an advanced category editor for direct `Logging:LogLevel:*` overrides. Its scope is the GuideAnts API process; use Docker/container logs for `guideants-ai`, SearXNG, Docling, and SQL Server.

## Visibility Surfaces

GuideAnts currently exposes operational state through these paths:

- API logs from `ILogger<T>` categories.
- Background job state in the SQL-backed `JobQueue` table and `GuideAntsApi.BackgroundJobs` logs.
- Usage events in the SQL-backed `UsageEvents` table via `GuideAnts.Usage.EfUsageRecorder`.
- Agent invocation records in `AgentInvocations` and `AgentInvocationMessages`.
- Settings -> Infrastructure probes, backed by `InfrastructureProbeService`.
- Settings service readiness and provider/model configuration state.
- Docker logs for `guideants-webapi-ui`, `guideants-ai`, `searxng`, `docling-serve-*`, and SQL Server.
- Local AI gateway endpoints under `guideants-ai`, including llama, ASR, TTS, embeddings, image generation, media extraction, and sandbox execution.

## Logging Configuration

Use category-specific log levels instead of raising `Default` broadly.

PowerShell:

```powershell
$env:Logging__LogLevel__GuideAntsApi.BackgroundJobs = "Information"
$env:Logging__LogLevel__GuideAntsApi.Services.Conversations.RoutingChatCompletionClientFactory = "Information"
dotnet run --project src/server/GuideAntsApi/GuideAntsApi.csproj
```

Docker Compose:

```yaml
environment:
  - Logging__LogLevel__GuideAntsApi.BackgroundJobs=Information
  - Logging__LogLevel__GuideAntsApi.Services.Conversations.RoutingChatCompletionClientFactory=Information
```

`appsettings.Production.json` sets broad categories to `Error`, so production or production-like runs must explicitly raise the categories needed for an investigation.

## Recommended Baseline

For normal operations, keep framework noise low and GuideAnts operational categories visible:

```yaml
environment:
  - Logging__LogLevel__Default=Warning
  - Logging__LogLevel__Microsoft.AspNetCore=Warning
  - Logging__LogLevel__Microsoft.EntityFrameworkCore=Warning
  - Logging__LogLevel__GuideAntsApi=Information
  - Logging__LogLevel__GuideAntsApi.BackgroundJobs=Information
  - Logging__LogLevel__GuideAntsApi.Services.Routing=Information
  - Logging__LogLevel__GuideAntsApi.Services.Conversations.RoutingChatCompletionClientFactory=Information
  - Logging__LogLevel__AntRunner=Warning
```

Use `Debug` only for short diagnostic windows. It can expose high-volume request, query, and tool execution detail.

## Subsystem Visibility Matrix

| Subsystem | Primary categories and records | What to inspect | Suggested normal level | Investigation level |
| --- | --- | --- | --- | --- |
| API requests | `Microsoft.AspNetCore`, `GuideAntsApi.Program` | Request failures, exception handler logs, startup readiness | `Warning` | `Information` |
| Chat routing | `GuideAntsApi.Services.Conversations.RoutingChatCompletionClientFactory`, `GuideAntsApi.Services.Routing` | Requested model, resolved catalog model, provider, routing problem details | `Information` | `Debug` |
| Chat providers | `AntRunner.Chat`, `AntRunner.Chat.OpenAI`, `AntRunner.Chat.Anthropic`, `AntRunner.Chat.GoogleVertex`, `AntRunner.Chat.HuggingFace`, `AntRunner.Chat.OpenRouter`, `AntRunner.Chat.LlamaCpp` | Provider call outcomes, provider-specific failures, llama client diagnostics | `Warning` | `Information` |
| Local llama runtime | `GuideAntsApi.Services.LlamaCpp`, `AntRunner.Chat.LlamaCpp`, Settings Infrastructure | Router alias updates, runtime inventory, load/unload/restart failures, `/llama-cpp/health` | `Information` | `Debug` |
| Background jobs | `GuideAntsApi.BackgroundJobs`, `JobQueue` table | Enqueue, claim, complete, retry, lease cleanup, permanent failure | `Information` | `Debug` |
| Document extraction | `GuideAntsApi.BackgroundJobs.Services.DocumentIntelligenceService`, `GuideAntsApi.BackgroundJobs.Jobs.Extract*MarkdownHandler` | Extraction source, Docling/Azure Document Intelligence routing, async conversion failures | `Information` | `Debug` |
| Embeddings and indexing | `GuideAntsApi.BackgroundJobs.Services.Embeddings`, `GuideAntsApi.BackgroundJobs.Services.Indexing`, `GuideAntsApi.BackgroundJobs.Jobs.Index*` | Chunk counts, provider routing, throttling, indexing failures | `Information` | `Debug` |
| Speech transcription | `GuideAntsApi.Services.Components.SpeechTranscriptionService`, `GuideAntsApi.BackgroundJobs.Jobs.Transcribe*` | Audio/video extraction, ASR routing, timeouts, empty transcription cases | `Information` | `Debug` |
| Speech synthesis and podcasts | `GuideAntsApi.Services.Components.SpeechSynthesisService`, `GuideAntsApi.Services.PodcastTools`, `GuideAntsApi.Services.NotebookPodcastService` | TTS provider calls, podcast generation, generated file sync | `Information` | `Debug` |
| Image generation | `GuideAntsApi.Services.NotebookImageService` | Provider/local image generation, image edit failures, selected output dimensions | `Information` | `Debug` |
| Search and browser rendering | `GuideAntsApi.Services.SearXngWebSearchService`, `GuideAntsApi.Services.SearXngBrowserRenderingClient`, `GuideAntsApi.Services.WebScrapingService` | SearXNG queries, browser render failures, markdown fetch errors | `Information` | `Debug` |
| Infrastructure probes | `GuideAntsApi.Services.Infrastructure.InfrastructureProbeService` | Settings Infrastructure reachability and path writability probe failures | `Warning` | `Debug` |
| File storage | `GuideAntsApi.Services.Components.NotebookFileService`, `GuideAntsApi.Services.Components.NotebookFileSyncService`, `GuideAntsApi.Services.Components.ContentFileService`, `GuideAntsApi.Services.StoragePathResolver` | File sync, missing physical files, path recovery, storage root issues | `Warning` | `Information` |
| Database and EF Core | `Microsoft.EntityFrameworkCore`, `Microsoft.EntityFrameworkCore.Database.Command`, `Microsoft.EntityFrameworkCore.Query`, `GuideAntsApi.Extensions.EfQueryWarningInterceptor` | SQL warnings, query-shape warnings, command failures | `Warning` | Short-lived `Information` |
| Usage and agent invocation records | `GuideAnts.Usage`, `UsageEvents`, `AgentInvocations`, `AgentInvocationMessages` | Usage attribution warnings, agent run status, duration, tool count, token/usage JSON | `Warning` | `Debug` |

## Investigation Profiles

### Chat routing is wrong or unexpectedly unavailable

```yaml
environment:
  - Logging__LogLevel__GuideAntsApi.Services.Conversations.RoutingChatCompletionClientFactory=Information
  - Logging__LogLevel__GuideAntsApi.Services.Routing=Debug
  - Logging__LogLevel__AntRunner.Chat=Information
```

Look for `Chat provider route resolved` with:

- `RequestedModelId`
- `CatalogModelId`
- `Provider`

Routing failures are returned as `application/problem+json` with stable fields such as `code`, `service`, `modeId`, `modelId`, `provider`, and `action`.

### Local llama runtime is unavailable

```yaml
environment:
  - Logging__LogLevel__GuideAntsApi.Services.LlamaCpp=Debug
  - Logging__LogLevel__AntRunner.Chat.LlamaCpp=Information
  - Logging__LogLevel__GuideAntsApi.Services.Infrastructure.InfrastructureProbeService=Debug
```

Check:

- Settings -> Infrastructure for `LlamaCpp:BaseUrl`.
- The probe target `{LlamaCpp:BaseUrl}/health`, for example `http://guideants-ai:80/llama-cpp/health`.
- `guideants-ai` container logs.
- Router model alias state in the local runtime/admin surface.
- Catalog model rows and `LocalRuntimeJson` for the selected model.

### Background jobs are stuck or retrying

```yaml
environment:
  - Logging__LogLevel__GuideAntsApi.BackgroundJobs=Debug
```

Watch for:

- `Enqueued job`
- `Claimed job`
- `Successfully processed job`
- `Job handler failed`
- `permanently failed`
- `Cleaned up expired job leases`
- `Requeued ... in-flight jobs on startup`

Also inspect the `JobQueue` table for status, attempts, claim token, retry time, and lease expiration.

Important configuration:

- `BackgroundJobs:PollingIntervalSeconds`
- `BackgroundJobs:JobTypes:*:MaxConcurrency`
- `BackgroundJobs:JobTypes:*:LeaseSeconds`
- `BackgroundJobs:RequeueProcessingOnStartup`

### Document extraction or transcription is slow

```yaml
environment:
  - Logging__LogLevel__GuideAntsApi.BackgroundJobs.Services.DocumentIntelligenceService=Debug
  - Logging__LogLevel__GuideAntsApi.BackgroundJobs.Jobs=Information
  - Logging__LogLevel__GuideAntsApi.Services.Components.SpeechTranscriptionService=Information
  - Logging__LogLevel__GuideAntsApi.Services.Components.VideoAudioExtractionService=Information
```

Check:

- `docling-serve-*` container logs if using local document intelligence.
- `guideants-ai` logs for ASR/media extraction.
- `DocumentIntelligence:MaxConcurrentConversions`
- `DocumentIntelligence:AsyncStatusPollIntervalMs`
- `SpeechTranscription:TimeoutSeconds`
- `VideoAudioExtraction:TimeoutSeconds`
- Relevant background job lease and concurrency settings.

### Search or browser rendering is failing

```yaml
environment:
  - Logging__LogLevel__GuideAntsApi.Services.SearXngWebSearchService=Information
  - Logging__LogLevel__GuideAntsApi.Services.SearXngBrowserRenderingClient=Debug
  - Logging__LogLevel__GuideAntsApi.Services.WebScrapingService=Information
```

Check:

- `searxng` container logs.
- `SearXngSearch:BaseUrl`
- `SearXngSearch:TimeoutMs`
- `BrowserRendering:BaseUrl`
- `BrowserRendering:RenderHtmlPath`
- `BrowserRendering:TimeoutMs`

### EF Core query warnings or database pressure

```yaml
environment:
  - Logging__LogLevel__Microsoft.EntityFrameworkCore=Warning
  - Logging__LogLevel__Microsoft.EntityFrameworkCore.Database.Command=Information
  - Logging__LogLevel__Microsoft.EntityFrameworkCore.Query=Information
  - Logging__LogLevel__GuideAntsApi.Extensions.EfQueryWarningInterceptor=Warning
```

Use `Information` on `Microsoft.EntityFrameworkCore.Database.Command` only briefly. SQL command logging can be very high volume.

In Debug builds, the API enables EF sensitive data logging and detailed errors. Do not rely on those settings being present in Release production builds.

## Database Visibility

The API records domain-level activity in SQL. These tables are usually more useful than raw logs for business and workflow visibility:

- `JobQueue`: queued, processing, completed, failed, retry, lease, and attempt state.
- `UsageEvents`: service, operation, usage category, model deployment, cost/charge fields, project/notebook/conversation attribution.
- `AgentInvocations`: nested assistant/tool execution state, status, duration, model, assistant, tool count, LLM round trips, usage JSON.
- `AgentInvocationMessages`: message-level record for agent invocation threads.
- `FileLineageEvents`: file lifecycle and generated artifact lineage.

Typical local SQL checks:

```sql
select top 50 *
from JobQueue
order by CreatedAt desc;

select top 50 *
from UsageEvents
order by CreatedAt desc;

select top 50 *
from AgentInvocations
order by CreatedAt desc;
```

Column names may differ slightly by migration state; use the current EF model or database schema as source of truth.

## Container and Service Logs

For Compose-based runs, start with:

```powershell
docker compose -f docker/docker-compose.yml logs --tail 200 guideants-webapi-ui
docker compose -f docker/docker-compose.yml logs --tail 200 guideants-ai
docker compose -f docker/docker-compose.yml logs --tail 200 searxng
docker compose -f docker/docker-compose.yml logs --tail 200 docling-serve-cpu
docker compose -f docker/docker-compose.yml logs --tail 200 docling-serve-cuda
```

Use `-f` when reproducing a problem:

```powershell
docker compose -f docker/docker-compose.yml logs -f guideants-webapi-ui guideants-ai
```

## Settings Infrastructure Probes

The Settings Infrastructure tab calls `InfrastructureProbeService` to test URLs and filesystem paths. URL probes use ranged `GET` requests with a 3-second timeout, and path probes check existence and directory writability.

Special handling:

- `LlamaCpp:BaseUrl` is probed at `{base}/health`.
- URL probe failures are captured per item and returned to the UI instead of throwing.
- Files are checked for existence only; directories are checked for existence and writability.

Raise `GuideAntsApi.Services.Infrastructure.InfrastructureProbeService` to `Debug` when probe errors are unclear.

## Current Gaps

The API does not currently provide custom spans or metrics for each domain operation. Visibility is mostly logs plus SQL records. If deeper telemetry is needed later, useful additions would be:

- Custom operation IDs/log scopes for a full chat turn across routing, provider calls, usage recording, and streamed response.
- Job duration and queue depth metrics.
- Provider-specific counters for success, retry, timeout, and model-not-ready outcomes.
- Local runtime metrics for loaded models, GPU assignment, queue depth, and restart/load duration.
- Extraction/transcription duration records by file type and provider.
