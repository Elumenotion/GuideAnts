# DocumentServer Full API Proxy Plan (CUDA Stack + API-Only Test Compose)

## Summary

This document captures the implementation to fully proxy DocumentServer through the API layer so browser traffic is same-origin and no longer depends on direct host exposure of the DocumentServer container.

Implemented scope:
- API reverse proxy route for DocumentServer runtime traffic (`/api/documentserver/ds/{**path}`), including WebSocket support.
- Existing `download` and `callback` endpoints remain unchanged.
- `editor-config` now returns API-proxied `documentServerUrl` based on `DocumentServer:PublicUrl`.
- Callback download URL rewriting now handles proxied path prefixes correctly.
- Added CUDA compose test stack copy with only API host port mapping.

## Key Changes

### 1. API Proxy Route

Added a reverse proxy route under `DocumentServerEndpoints`:
- Route: `/api/documentserver/ds/{**path}`
- Methods: `GET, POST, PUT, PATCH, DELETE, HEAD, OPTIONS`
- Backing transport: YARP `IHttpForwarder` + shared `HttpMessageInvoker`
- Behavior:
  - Streams request/response bodies.
  - Supports long-lived connections and WebSocket upgrade traffic.
  - Preserves critical forwarded headers.
  - Returns `502` when upstream forwarding fails.

Also enabled WebSocket middleware in `Program.cs` via `app.UseWebSockets()`.

### 2. DocumentServer Callback Rewrite

Updated `DocumentServerService.ResolveDocumentServerDownloadUrl` to support proxied public URLs.

When callback URLs use API proxy paths (for example `/api/documentserver/ds/cache/files/...`), the service now strips the proxy prefix before rewriting to the internal container URL (`http://documentserver/...`).

This keeps save callbacks working while browser access stays API-mediated.

### 3. CUDA API-Only Test Compose

Added:
- `docker/docker-compose.cuda.api-only-test.yml`

Derived from `docker/docker-compose.cuda.yml`, with these test-focused changes:
- Keeps `guideants-webapi-ui` host mapping: `5107:8080`
- Removes host `ports` mappings from:
  - `mssql-express`
  - `guideants-ai`
  - `docling-serve`
  - `documentserver`
  - `plantuml`
  - `searxng`
- Sets default API-proxied DocumentServer URL:
  - `DocumentServer__PublicUrl=http://localhost:5107/api/documentserver/ds`

## Run Instructions (CUDA API-Only Test Stack)

From repo root:

```powershell
docker compose -f docker/docker-compose.cuda.api-only-test.yml up -d
```

Optional teardown:

```powershell
docker compose -f docker/docker-compose.cuda.api-only-test.yml down
```

## Verification Checklist

1. Open the app at `http://localhost:5107`.
2. Open a supported Office file in project content and notebook preview.
3. Confirm editor assets/runtime traffic uses only:
   - `/api/documentserver/ds/...`
4. Confirm browser does not call a direct host DocumentServer port (for example `:8082`).
5. Edit and save a document.
6. Verify save persists (new content/version is visible).
7. Confirm no regressions in `download` / `callback` behavior.

## Test Coverage Added

Updated unit tests in `DocumentServerServiceTests` to cover:
- Proxied `documentServerUrl` expectation.
- Callback rewrite from proxied public path to internal DocumentServer URL.
- Invalid callback source URL handling (non-absolute URL).

## Notes

This scope is intentionally focused on full functional proxying for DocumentServer via API in CUDA stack test topology.

The API-only test compose pins `DocumentServer__PublicUrl` directly to the proxied URL to avoid accidental override from `docker/.env` values like `GA_DOCUMENTSERVER_PUBLIC_URL=http://localhost:8082`, which would reintroduce direct browser dependency on a non-exposed host port.
