# Media Extraction AI Router Plan

## Summary

This plan replaces API-local video/audio extraction with a dedicated media service inside the `guideants-ai` image, routed through nginx alongside the existing `/sandbox/`, `/asr/`, `/tts/`, `/emb/`, `/sd/`, and `/llama-admin/` endpoints.

The new design does **not** use the Script Execution Agent and does **not** depend on the API container's temp directory.

Instead:

- the API stages transient media work under shared `FileStorage:Path`
- the new AI-side media service operates only on shared-storage paths
- the AI image runs `ffmpeg`
- the API consumes the result from shared storage and continues its existing transcription flow

## Why This Change

The current `VideoAudioExtractionService` shells out to a local `ffmpeg` binary from the API process. That has a few problems:

- it forces `ffmpeg` into the API image
- it keeps media-processing concerns in the wrong container
- it does not match the existing `guideants-ai` architecture, where specialized local capabilities live behind nginx
- it creates pressure to use the Script Execution Agent for work that is not really notebook/script execution

The replacement should follow the same service model already used by:

- `asr-service`
- `tts-service`
- `emb-service`
- `sd-service`
- `llama-admin-service`

## Non-Goals

- Do not add this feature to the Script Execution Agent.
- Do not grant the AI image access to the API container's temp folder.
- Do not use `docker exec` or the legacy `DockerScriptService` path.
- Do not expose raw host paths over the API boundary.

## Target Architecture

### New Internal Route

Add a new nginx route in `guideants-ai`:

- `/media/` -> `127.0.0.1:8087`

This route should be treated like the other internal capability routes in `docker/build/guideants-ai/nginx.conf`.

### New Internal Service

Add a new Python service:

- `docker/build/guideants-ai/media-service/media_service.py`

Add a new startup wrapper:

- `docker/build/guideants-ai/start-media.sh`

Start it from:

- `docker/build/guideants-ai/entrypoint.sh`

Copy it from:

- `docker/build/guideants-ai/Dockerfile.<backend>`

## Core Design Principle

The media service works only with files under the shared storage root mounted in both containers:

- API view: `FileStorage:Path`
- AI container view: `/app/ContentFiles`

The API must stage any transient extraction work into shared storage before calling the media service.

The media service must reject any path that escapes `/app/ContentFiles`.

## Proposed Media API

### Endpoint

- `POST /media/extract-audio`

### Request Body

JSON request body, not multipart upload.

Example:

```json
{
  "sourcePath": ".system/media-extract/abc123/input.mp4",
  "outputPath": ".system/media-extract/abc123/output.mp3",
  "audioFormat": "mp3",
  "audioQuality": "2",
  "overwrite": true
}
```

### Response Body

Example:

```json
{
  "outputPath": ".system/media-extract/abc123/output.mp3",
  "contentType": "audio/mpeg",
  "fileSize": 1234567
}
```

### Health Endpoint

- `GET /media/health`

## Path Semantics

### API -> Media Service Contract

The request should send storage-relative paths, never absolute OS paths.

Examples:

- good: `.system/media-extract/abc123/input.mp4`
- good: `project-slug/notebook-slug/Output/source.mp4`
- bad: `C:\\temp\\input.mp4`
- bad: `/tmp/input.mp4`
- bad: `../../outside-root.mp4`

### AI Service Resolution

The media service resolves:

- `sourcePath` -> `/app/ContentFiles/<sourcePath>`
- `outputPath` -> `/app/ContentFiles/<outputPath>`

Then:

1. normalize
2. verify both remain under `/app/ContentFiles`
3. verify source exists
4. create output parent folder if needed
5. run `ffmpeg`

## API Refactor Plan

### Current Problem

`SpeechTranscriptionService` currently writes uploaded video streams to a local temp file before passing that path into `VideoAudioExtractionService`.

That temp path is local to the API container and should not be part of the new design.

### New API Flow

When the source media is not already in shared storage:

1. create a transient workspace under `FileStorage:Path/.system/media-extract/<guid>/`
2. write the incoming video there as `input.<ext>`
3. call the new `/media/extract-audio` endpoint with relative `sourcePath` and `outputPath`
4. read the resulting output file from shared storage
5. continue with transcription
6. clean up the transient workspace

When the source media is already in shared storage:

1. skip staging
2. call `/media/extract-audio` directly against the existing relative source path
3. consume output from shared storage

### Services to Update

Primary changes:

- `src/server/GuideAntsApi/Services/Components/VideoAudioExtractionService.cs`
- `src/server/GuideAntsApi/Services/Components/SpeechTranscriptionService.cs`
- `src/server/GuideAntsApi/Configuration/StartupConfiguration.cs`
- `src/server/GuideAntsApi/Options/AzureDocumentIntelligenceOptions.cs`
- `src/server/GuideAntsApi/appsettings.json`
- `src/server/GuideAntsApi/appsettings.Development.json`
- `docker/docker-compose.yml`

### New API Client Abstraction

Add a small dedicated client abstraction, for example:

- `IMediaExtractionClient`
- `MediaExtractionClient`

Responsibilities:

- build the request to `LocalServiceHosts:MediaBaseUrl`
- post JSON to `/media/extract-audio`
- deserialize response
- surface meaningful errors

`VideoAudioExtractionService` should own staging and cleanup.
`MediaExtractionClient` should own transport only.

## AI Service Implementation Plan

### Service Shape

Use the same stack as the existing Python internal services:

- FastAPI
- uvicorn
- structured logging similar to `asr_service.py`

### Endpoints

- `GET /health`
- `POST /extract-audio`

### Request Model

Suggested fields:

- `sourcePath: str`
- `outputPath: str`
- `audioFormat: str = "mp3"`
- `audioQuality: str = "2"`
- `overwrite: bool = true`

### Processing Steps

1. validate request fields
2. resolve source/output under `/app/ContentFiles`
3. reject traversal or out-of-root paths
4. verify source file exists
5. optionally reject unsupported source extensions
6. create output directory
7. run `ffmpeg`
8. verify output exists and is non-empty
9. return metadata

### Command Shape

Initial `ffmpeg` command:

```bash
ffmpeg -hide_banner -loglevel error -nostdin \
  -i "<source>" \
  -vn \
  -acodec libmp3lame \
  -q:a 2 \
  -y "<output>"
```

### Error Handling

Return `4xx` for:

- invalid relative paths
- path traversal
- source missing
- unsupported format
- overwrite denied

Return `5xx` for:

- `ffmpeg` failure
- unexpected subprocess failure
- output missing after successful exit

## nginx Changes

Update `docker/build/guideants-ai/nginx.conf`:

1. add redirect:
   - `location = /media { return 301 /media/; }`
2. add proxied route:
   - `location /media/ { proxy_pass http://127.0.0.1:8087/; ... }`

Use the same proxy style as `/asr/`, `/tts/`, and `/emb/`:

- `proxy_http_version 1.1`
- `proxy_buffering off`
- long read/send timeouts
- `X-Forwarded-Prefix /media`

## entrypoint Changes

Update `docker/build/guideants-ai/entrypoint.sh`:

1. start `/app/start-media.sh` in background
2. capture `MEDIA_PID`
3. optionally add readiness monitoring
4. include media service in shutdown handling
5. include media service in liveness supervision

Suggested port:

- `GA_MEDIA_PORT=8087`

## Dockerfile Changes

Update backend Dockerfiles (`docker/build/guideants-ai/Dockerfile.cpu`, `Dockerfile.cuda`, `Dockerfile.rocm`):

1. copy `media-service/` into `/app/media-service/`
2. copy `start-media.sh` into `/app/start-media.sh`
3. mark `start-media.sh` executable

No new package should be required if we use the existing runtime where `ffmpeg` is already installed.

## Configuration Changes

### Add LocalServiceHosts Setting

Add to `LocalServiceHostsOptions`:

- `MediaBaseUrl`

Suggested defaults:

- local/dev: `http://localhost:8110`
- compose: `http://guideants-ai:80`

### Compose Wiring

Update `docker/docker-compose.yml` for `guideants-webapi-ui`:

- `LocalServiceHosts__MediaBaseUrl=http://guideants-ai:80`

## Cleanup Changes

Once the API uses the new media service:

1. remove `VideoAudioExtraction:FfmpegPath` from appsettings/options
2. remove `ffmpeg` from `docker/build/api/Dockerfile`

`docker.io` removal from the API image is related but separate. It depends on retiring the old `/sandbox/run/...` legacy path.

## Testing Plan

### AI Service Tests

Add focused tests for:

- valid relative path resolution
- traversal rejection
- source missing rejection
- output written successfully
- overwrite behavior

### API Tests

Add tests for:

- upload video -> staged under shared storage -> media service called
- shared-storage source -> no extra staging
- cleanup of `.system/media-extract/<guid>/`
- failure propagation when media service returns an error

### Integration Checks

Verify:

1. video upload transcription still works
2. notebook-stored video transcription still works
3. API image no longer needs local `ffmpeg`
4. `guideants-ai` handles extraction through `/media/`

## Rollout Sequence

1. add the AI-side media service and nginx route
2. add API-side transport client and config
3. refactor `VideoAudioExtractionService` to use shared-storage + `/media/extract-audio`
4. refactor `SpeechTranscriptionService` to stop using API-local temp paths for video extraction inputs
5. verify transcription flows
6. remove API-local `ffmpeg` dependency
7. optionally, in a later change, migrate the legacy `/sandbox/run/...` endpoint off `DockerScriptService`

## Open Decisions

1. Should the media service support only audio extraction initially, or also duration/probe endpoints in the first pass?
2. Should the API always generate `outputPath`, or should the media service be allowed to generate it when omitted?
3. Do we want a generic `.system/media-extract/` retention cleanup policy, or should the API delete all transient work immediately?

## Recommendation

Implement the narrowest first version:

- one new `/media/extract-audio` endpoint
- path-based JSON contract
- shared-storage-only semantics
- API-owned transient workspace lifecycle

That gets video/audio extraction out of the API image cleanly while keeping the design aligned with the rest of the `guideants-ai` router architecture.
