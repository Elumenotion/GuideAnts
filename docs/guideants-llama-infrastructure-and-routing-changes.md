# GuideAnts: Llama runtime, infrastructure probes, and routing (change log)

This document records a coordinated set of updates: **where models and router state live**, **how the Infrastructure tab probes `LlamaCpp:BaseUrl`**, **service-mode normalization** (legacy `default` → `cloud` + `local`), and **related tests/UI**. It is aimed at operators and maintainers.

---

## 1. Scope summary

| Area | Change |
|------|--------|
| **Llama model storage** | The API does **not** mount or read a host `models` directory (e.g. under `docker/volumes/llama/models`). GGUF paths and router configuration are owned by the **guideants-ai** runtime; the API talks to it over HTTP. |
| **`LlamaModelManagement` options** | `ModelStorePath` and `RouterModelsConfigPath` were **removed**. Remaining options: `HfToken` (optional), `AllowOverwrite`. |
| **appsettings** | `LlamaModelManagement` is reduced to `{ "AllowOverwrite": false }` (plus optional token where used). |
| **Infrastructure tab** | No catalog rows for removed paths. **`LlamaCpp:BaseUrl` probes** use `{BaseUrl}/health` (see §4). |
| **Local Llama UI** | The **“Runtime paths”** table (host paths + probes) was removed from the local llama settings tab; runtime paths are not API concerns. |
| **Routing readiness tests** | Tests that seeded `ModeId: "default"` and probed `"default"` were updated: after bootstrap, modes normalize to **`cloud`** (+ **`local`**), so probes and overview expectations match. |
| **Infrastructure probe** | For dependency id `LlamaCpp:BaseUrl`, HTTP GET targets **`ToLlamaCppHealthProbeUri(base)`** (e.g. `.../llama-cpp/health`), not the bare OpenAI-compatible base. |

---

## 2. Llama model management (API vs runtime)

### 2.1 Product model

- **Models on disk** and **router alias state** live on the **guideants-ai** container (or equivalent runtime), not on the GuideAnts API host process filesystem.
- The API uses **HTTP clients** configured with `LlamaCpp:BaseUrl` (OpenAI-compatible path under the gateway, e.g. `/llama-cpp`) and **admin/inventory** APIs as implemented today.
- **Hugging Face downloads** delegated from the app still use `LlamaModelManagement` for token/overwrites; there is **no** API option that points at a host bind-mount for the model store.

### 2.2 Code reference

- `GuideAntsApi.Configuration.LlamaModelManagementOptions` — documents the above; only `HfToken` and `AllowOverwrite` remain as configurable members.
- `LlamaModelStorePathResolver` may still exist as a **path-mapping helper** for edge cases; it is **not** wired to removed `LlamaModelManagement` path options.

---

## 3. Configuration surfaces

### 3.1 `appsettings` / environment

- `LlamaCpp:BaseUrl` — base URL for the OpenAI-compatible surface (must include the gateway prefix, e.g. `http://localhost:8110/llama-cpp` on the host, or `http://guideants-ai:80/llama-cpp` from another container on the same Docker network).
- `LlamaModelManagement` — minimal JSON as in `appsettings.json` (`AllowOverwrite` only unless extending).

### 3.2 Database-backed settings

- Stored **Application Settings** can override environment/appsettings. If `LlamaCpp:BaseUrl` is saved as `http://localhost:8110/...` but the API runs **inside Docker**, **`localhost` is wrong** from the API container’s perspective (it refers to the container itself). Use the **Compose service hostname** (e.g. `guideants-ai`) and **internal port** (`80`), as in `docker/docker-compose.yml` (`LlamaCpp__BaseUrl=http://guideants-ai:80/llama-cpp`).

### 3.3 Docker Compose reference

From `docker/docker-compose.yml`, the web API service sets (among others):

- `LlamaCpp__BaseUrl=http://guideants-ai:80/llama-cpp`

Host port **8110** maps to **guideants-ai:80**; use **8110** only when calling from the **host** (browser, curl on the machine). Use **guideants-ai:80** from **peer containers**.

---

## 4. Infrastructure tab: `LlamaCpp:BaseUrl` probe

### 4.1 Behavior

- Service: `GuideAntsApi.Services.Infrastructure.InfrastructureProbeService`.
- URL probes use **GET** with **`Range: bytes=0-0`**, not HEAD (many stacks hang on HEAD for inference paths).
- **Timeout:** 3 seconds per probe (fixed; not user-configurable).

### 4.2 Llama-specific probe URL

For probe items with **`id` = `LlamaCpp:BaseUrl`**, the request URL is **not** the raw configured base. It is:

```text
{BaseUrl with path trimmed}/health
```

Implemented by `InfrastructureProbeService.ToLlamaCppHealthProbeUri(Uri)`.

Examples:

| Configured `LlamaCpp:BaseUrl` | Probe URL |
|-------------------------------|-----------|
| `http://localhost:8110/llama-cpp` | `http://localhost:8110/llama-cpp/health` |
| `http://guideants-ai:80/llama-cpp` | `http://guideants-ai:80/llama-cpp/health` |
| `http://host/` (empty path) | `http://host/llama-cpp/health` (fallback) |

This aligns with the gateway routing in `docker/build/guideants-ai/nginx.conf` (`/llama-cpp/` → llama-server) and the documented health checks under `docker/llama/README.md` (e.g. `/llama-cpp/health`).

### 4.3 Why not probe the bare base?

The bare base often **301**s to `/llama-cpp/`, and the **root** of the OpenAI-compatible surface is not guaranteed to return a fast, ranged-friendly response within 3 seconds. **`/health`** is the intended liveness endpoint for this stack.

### 4.4 UI note

The Infrastructure table still displays the **configured** `currentValue` for `LlamaCpp:BaseUrl`; probe success depends on the **resolved** health URL above. If the probe fails with timeout, verify **hostname reachability** from the API process (Docker vs host) first, then path.

---

## 5. Nginx gateway and `http://localhost:8110/`

The guideants-ai image runs **nginx** on port **80** (host **8110**). The custom `nginx.conf` routes **prefixes** (`/llama-cpp/`, `/sandbox/`, `/emb/`, …). There is **no** dedicated `location /` for the app; opening **`http://localhost:8110/`** may show the default **“Welcome to nginx!”** page. That does **not** mean the gateway is misconfigured for **`/llama-cpp/health`**—use path-scoped URLs for checks.

---

## 6. Service modes: legacy `default` normalization

### 6.1 Behavior

During `ApplicationSettingsService.BootstrapAsync`, `NormalizeLegacySingleModeRowsAsync` runs. For each routed service with **exactly one** mode whose `ModeId` is **`default`**, the code infers **cloud** vs **local** from the provider section and:

- Renames the mode id to **`cloud`** (when the section is a cloud provider), and  
- Adds a **`local`** mode for the counterpart (e.g. `LocalServiceHosts:EmbeddingsBaseUrl`) when applicable.

So after bootstrap, **two modes** per service are common (`cloud` + `local`), not a single `default` row.

### 6.2 Impact on tests and API usage

- **`ProbeModeAsync(service, modeId)`** must use an **existing** `modeId`. Probing **`default`** after normalization yields **mode not found** / blocked if that id no longer exists.
- Tests that seed `ModeId: "default"` should probe **`cloud`** (or read modes from `GetServiceModesAsync` and use the default mode’s id).
- Overview-style tests that assumed **one mode per service** were updated to expect **`Total == 2`** and **`Ready == 2`** when full config satisfies both cloud and local rows.

Implementation reference: `ApplicationSettingsService.ServiceModes.cs` (`NormalizeLegacySingleModeRowsAsync`, `InferModeKind`, etc.).

---

## 7. Tests updated (reference)

| Suite / file | Change |
|--------------|--------|
| `GuideAntsApi.Tests/Services/Routing/RoutingReadinessServiceTests.cs` | Embeddings Azure scenarios: `ProbeModeAsync(..., "cloud")` instead of `"default"`. `Overview_Shape_*`: expects **2** modes per routed service, **2** ready. |
| `GuideAntsApi.Tests/Services/Infrastructure/InfrastructureProbeServiceTests.cs` | `LlamaCpp:BaseUrl` probe asserts request to `.../llama-cpp/health`; `ToLlamaCppHealthProbeUri` unit cases. |
| Integration / schema tests | Previously adjusted for removed Llama path catalog rows and appsettings alignment (see git history for `ApplicationSettingsServiceSchemaAndReadinessTests`, integration factories, Qwen/concurrency tests). |

---

## 8. File index (primary touchpoints)

| Path | Role |
|------|------|
| `src/server/GuideAntsApi/Configuration/LlamaModelManagementOptions.cs` | Reduced options; XML docs for runtime ownership. |
| `src/server/GuideAntsApi/appsettings.json` | `LlamaModelManagement` minimal block. |
| `src/server/GuideAntsApi/Settings/ApplicationSettingsService.RuntimeDependencies.cs` | Runtime dependency catalog for Infrastructure (no host model paths). |
| `src/server/GuideAntsApi/Services/Infrastructure/InfrastructureProbeService.cs` | `LlamaCpp:BaseUrl` → health URL mapping. |
| `src/server/GuideAntsApi/Settings/ApplicationSettingsService.ServiceModes.cs` | Mode normalization. |
| `docker/docker-compose.yml` | `LlamaCpp__BaseUrl`, `guideants-ai` service, port **8110:80**. |
| `docker/build/guideants-ai/nginx.conf` | Gateway prefixes; `/llama-cpp/` → llama-server. |
| `src/client/src/pages/settings/components/LocalLlamaRuntimeTab.tsx` | **Runtime paths** dependency/probe UI removed; tab focuses on inventory, downloads, alias status. |

---

## 9. Related documentation

- `docs/llama-model-download-and-runtime-management.md` — download/runtime concepts (verify for any older path language).
- `docs/settings-and-llama-completion-requirements.md` — settings/llama requirements (may need a pass if it still mentions API host model directories).
- `docker/llama/README.md` — gateway ports and health URL examples.

---

## 10. Revision

| Date | Notes |
|------|--------|
| 2026-04-20 | Initial document: model path removal, health probe, mode normalization, Docker networking, file index. |
