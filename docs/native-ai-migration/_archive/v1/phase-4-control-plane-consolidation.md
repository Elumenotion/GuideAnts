# Phase 4 — Control-plane consolidation / service-surface simplification

Parent: [`00-overview.md`](./00-overview.md). Prerequisite: Phases 1–3 complete (the set
of engines and their lifecycle needs must be final before consolidating around them).
Decision: [`DECISIONS.md`](./DECISIONS.md) **D6** — nginx + one Python `ga-admin`;
Kestrel-YARP / .NET rewrite are optional post-Phase-4; **`ga-admin` MUST NOT merge into
`ScriptExecutionAgent`** (security). Gates:
[`contract-preservation-gate.md`](./contract-preservation-gate.md),
[`flavor-build-gate.md`](./flavor-build-gate.md), and — since this is the last phase — the
**end-only** [`codeql-gate.md`](./codeql-gate.md).

**Mission:** collapse the four separate FastAPI admin implementations
(`llama_admin_service.py` ~1500 lines, `sd_service.py`'s admin half ~2200 lines total,
and the admin skeletons of the new emb facade / `ga-audio-server`) into **one**
control-plane service, without changing any route the .NET server or client consumes.

---

## 1. What exists after Phase 3 (the consolidation inputs)

| Surface | Where it lives after Phase 3 | Responsibilities |
|---------|------------------------------|------------------|
| `/llama-admin/` (:8086) | `llama-admin-service/llama_admin_service.py` | `GET /health`, `GET/POST /router/entries`, `DELETE /router/entries/{alias}`, `POST /downloads` + `GET /downloads/{operation_id}` (HF GGUF via urllib), `GET /llama/last-exit`, `POST /llama/restart` (SIGTERM → entrypoint respawn). Consumed by `ILlamaRuntimeAdminClient` (`LlamaRuntimeAdminClient.cs`), `RouterModelsConfigService`, `HuggingFaceModelDownloadService`, `LlamaRouterIniSyncService`. |
| `/sd/admin/*` + `/sd/txt2img|img2img` (:8083) | `sd-service/sd_service.py` | sd-server subprocess lifecycle, bundle store + HF downloads (huggingface_hub), inference facade translating to native `/sdcpp/v1/img_gen` + job polling, warmup, health-with-unloaded-semantics (`sd_service.py:1763-2198`). Consumed by `NotebookImageService.LocalSd.cs`, `ImageBundleManager.tsx`, settings endpoints. |
| `/asr/admin/*`, `/tts/admin/*` (:8082/:8084) | `ga-audio-server` admin skeleton (Phase 2/3) | load/unload/models/download/ready per engine. |
| `/emb/admin/*` (:8085) | emb facade (Phase 1) | Same pattern + llama-server child lifecycle. |

Common pattern across all four: HF/artifact downloads with operation tracking
(start → poll → cancel/delete), model/bundle listing, load/unload of a native engine,
warmup + readiness, health. That quadruplication is the thing being removed.

## 2. Target design

**One control-plane service** (working name `ga-admin`, Python/FastAPI initially — the
lowest-risk choice given it absorbs three existing Python codebases; a native or .NET
rewrite is possible later but out of scope), listening on one internal port (:8086,
inheriting llama-admin's slot), owning:

1. **Artifact manager** — single download/operation subsystem (HF snapshot, single-file
   GGUF, SD bundles) with one operation-id store, replacing three separate
   implementations. For ASR/TTS it is the **authoritative manifest + source-allowlist
   enforcement point** (D9, [`model-catalog-and-downloads.md`](./model-catalog-and-downloads.md)):
   it serves the curated catalog (`GET /asr/admin/catalog`, `GET /tts/admin/catalog`) and
   validates every `/{svc}/admin/models/download` against the manifest's allowlisted repos
   before invoking the download — rejecting non-allowlisted / GGUF / gated-without-token
   requests loudly, never coercing. (Embeddings/llama GGUF and SD bundles keep their own
   selection rules; the audio.cpp catalog is ASR/TTS-only.)
2. **Router INI manager** — `router-models.ini` CRUD + sanitize + llama-server
   SIGTERM/restart signaling (absorbed from `llama_admin_service.py`; the entrypoint
   respawn contract stays).
3. **Engine lifecycle manager** — spawn/stop/supervise the native data-plane processes
   that are *not* respawned by the entrypoint: `sd-server`, the emb `llama-server
   --embeddings` child, and (decision §5.2) possibly the `ga-audio-server` processes.
4. **Per-service admin API adapters** — the existing external shapes re-exposed
   unchanged.

**Native engines keep the data plane.** Inference requests never pass through `ga-admin`.

### Routing: preserve every public path with nginx splits

nginx `location` blocks split admin vs inference on shared prefixes (longest-prefix
match), so **zero** .NET/client changes:

```nginx
location /asr/admin/  { proxy_pass http://127.0.0.1:8086/asr/admin/; }
location /asr/        { proxy_pass http://127.0.0.1:8082/; }          # ga-audio-server
location /tts/admin/  { proxy_pass http://127.0.0.1:8086/tts/admin/; }
location /tts/        { proxy_pass http://127.0.0.1:8084/; }
location /emb/admin/  { proxy_pass http://127.0.0.1:8086/emb/admin/; }
location /emb/        { proxy_pass http://127.0.0.1:8085/; }
location /sd/admin/   { proxy_pass http://127.0.0.1:8086/sd/admin/; }
location /sd/         { proxy_pass http://127.0.0.1:8086/sd/; }        # see §5.1
location /llama-admin/ { proxy_pass http://127.0.0.1:8086/llama-admin/; }
```

Caveat: `/health` and `/ready` are served per-service at the service root (e.g.
`/asr/health` must reflect the *engine*, not the control plane) — those stay routed to
the engines, which keep serving their own health/ready. The control plane proxies or
aggregates engine state for its own admin views but does not impersonate engine health.

## 3. Scope

- Implement `ga-admin`; port router INI logic, download subsystems, SD bundle store +
  sd-server lifecycle, and the ASR/TTS/emb admin surfaces into it.
- Strip admin/download responsibilities out of the Phase 1–3 engines where they were
  implemented as stopgaps (engines keep: inference, `/health`, `/ready`, load/unload
  execution — see §5.3 for the split).
- `entrypoint.sh` simplification: starts nginx, llama-server (+respawn loop, unchanged),
  ScriptExecutionAgent, media, the native engines, and `ga-admin`; deletes the four
  separate service starts. Readiness-monitor blocks keyed on `GA_*_WAIT_FOR_READY_ON_STARTUP`
  keep working against the engines' `/ready`.
- Finalize **Tier A** torch-removal if Phase 3 hasn't already: `transformers`/`tokenizers`/
  `accelerate` removed from `/opt/venv` **iff** `pipdeptree` proves no remaining requirer —
  including transitive sandbox packages, since the sandbox shares this venv (verify, do not
  assume). Final Tier-A state: no package that *depends on* torch remains for the services.
  **Torch itself (Tier B) is removed only under D7** — while D7 = out of scope, `torch`/
  `torchaudio`/`torchvision` stay for sandbox scripts. Do not claim torch removed unless D7
  authorizes stripping it from the sandbox requirements + Dockerfile. See
  [`torch-removal-gate.md`](./torch-removal-gate.md).

## 4. Out of scope

- Rewriting the SD inference facade contract (`/sd/txt2img`, `/sd/img2img` shapes stay).
- Changing `ILlamaRuntimeAdminClient` or any settings endpoint in .NET.
- Auth between container-internal services (localhost-only today; unchanged).
- Rewriting `ga-admin` in a compiled language.

## 5. Design decisions to settle at implementation (not product-blocking)

### 5.1 Where the SD inference facade lives

`sd_service.py` is *both* admin and inference facade (translating `/sd/txt2img` to native
`/sdcpp/v1/img_gen` + polling). Options: (a) keep the facade inside `ga-admin` (SD
inference traffic transits the control plane — pragmatic, matches today's process
count), or (b) split a minimal `sd-facade` process. Recommend (a) v1: SD calls are
low-QPS and the translation is stateful against the bundle store anyway.

### 5.2 Who supervises the audio engines

Entrypoint currently supervises everything and respawns only llama-server. Options:
entrypoint keeps spawning `ga-audio-server` (simplest; `ga-admin` only signals it), or
`ga-admin` owns spawn/respawn like it does for sd-server. Recommend: entrypoint spawns,
`ga-admin` signals — matches the existing llama-server precedent
(`entrypoint.sh:463-490`) and keeps one supervision model.

### 5.3 Load/unload split between control plane and engine

`POST /{svc}/admin/load` lands on `ga-admin`, which (a) ensures artifacts exist
(download if `model_id` given — token stamped by .NET per
`SettingsServiceLocalModelsEndpoints.cs:85-93`), then (b) calls the engine's internal
load hook. Engines expose a private localhost load/unload endpoint; `ga-admin` is the
only caller. This keeps the settings-UI operation-tracking contract in one place.

## 6. Required changes

| Component | Change |
|-----------|--------|
| New `docker/build/guideants-ai/admin-service/` | `ga-admin` (FastAPI). Ports of: router INI CRUD + restart (from `llama_admin_service.py`), download/op subsystem (merge the urllib + huggingface_hub variants into one), SD bundle store + sd-server lifecycle + inference facade (from `sd_service.py`), ASR/TTS/emb admin adapters. |
| Removed | `llama-admin-service/`, `sd-service/` (as separate processes), stopgap admin code in the engines, `start-llama-admin.sh`, `start-sd.sh`, `start-emb.sh`'s admin half (per final layout). |
| `nginx.conf` | Admin-prefix splits per §2; public prefixes unchanged. |
| `entrypoint.sh` | Reduced service list; PID monitor loop entries updated; llama-server respawn machinery untouched. |
| Dockerfiles | Venv slimming (Tier A torch-dependent service packages out; the torch wheels themselves only if D7 drops sandbox torch — see [`torch-removal-gate.md`](./torch-removal-gate.md)); copy `admin-service/`. |
| `docker/guideants-ai-build.md` + compose files | Document the new process model; env var cleanup (`GA_SD_*`, `GA_EMB_*` hosts/ports unchanged externally). |

## 7. Contract preservation strategy

- **Golden proxy tests** captured *before* the refactor: for every route in
  `LocalServiceAdminRouting`/`SettingsServiceLocalModelsEndpoints`,
  `LlamaRuntimeAdminClient`, `NotebookImageService.LocalSd.cs`, and
  `ImageBundleManager.tsx`, record request/response pairs on the pre-Phase-4 image and
  replay against the consolidated one.
- `GET /llama-admin/llama/last-exit` is currently unused by .NET but is part of the
  admin surface — keep it (it reads `/run/llama-server.last-exit.json` written by the
  entrypoint; that file contract is unchanged).
- Operation-id semantics: downloads started before a `ga-admin` restart are lost today
  per-service; consolidation must not make this worse (persist op state or document the
  same limitation).

## 8. Risks

- **Big-bang port risk**: ~3700 lines of battle-tested Python (llama-admin + sd) carry
  subtle behaviors (INI sanitize interplay with the entrypoint's own sanitizers, crash
  classification, bundle activation edge cases). Mitigation: port file-by-file with the
  golden proxy tests as the gate; do llama-admin and sd in separate PRs behind the same
  service skeleton.
- **Single point of failure**: one admin process now fronts all model management. It is
  control-plane only — inference survives its crash — but the entrypoint should log-and-
  drop it like the services it replaces (or respawn it; decide and document).
- **Route-split subtleties**: `/sd/health` has "unloaded" semantics
  (`sd_service.py:1763`) that the container `HEALTHCHECK` and warmup service rely on;
  after consolidation `/sd/health` is served by `ga-admin` and must keep those
  semantics.
- **Timing**: consolidating while Phases 1–3 contracts are still moving would force
  rework — hence last position in the ordering.

## 9. Validation

1. Golden proxy replay suite green (all admin routes, all four services + llama-admin).
2. Full lifecycle e2e per service on all four flavors: download → load → infer → unload
   → delete, via the real settings UI.
3. Router INI e2e: add/remove alias via UI → `ga-admin` rewrites INI → llama-server
   SIGTERM → entrypoint respawn → alias serves; `POST /llama/restart` works.
4. `LocalAiStartupWarmupService` cold-boot warmup on a fresh volume (seed INI path,
   `entrypoint.sh:6-11`).
5. Container `HEALTHCHECK` and `docker logs` sanity: no orphan processes, clean SIGTERM
   shutdown via `shutdown_all`.
6. Image size + process count recorded vs pre-Phase-4 baseline.

## 10. Rollback

Image-tag rollback restores the four-service layout; volumes are compatible (router INI,
model dirs, SD bundle store formats unchanged — any change to the bundle store format is
out of scope precisely to keep this true). No .NET or client rollback needed since no
contract changed.

## 11. Gates

Run after this phase; record results in [`STATUS.md`](./STATUS.md).

- [`contract-preservation-gate.md`](./contract-preservation-gate.md) — golden proxy replay
  for **every** admin route (`/llama-admin/`, `/sd/`, `/*/admin/*`); per-service `/health` +
  `/ready` still served by the engines (control plane does not impersonate engine health);
  operation-id semantics not regressed.
- [`flavor-build-gate.md`](./flavor-build-gate.md) — all four flavors build with the slimmed
  venv (torch stack out) + one `ga-admin`; process count 9→7; clean SIGTERM via
  `shutdown_all`; no orphaned processes.
- [`codeql-gate.md`](./codeql-gate.md) — **this triggers the end-only run.** After Phase 4
  merges (or after the last in-scope phase, if Phase 4 is deferred), derive the language
  matrix from the cumulative diff (`cpp` for `ga-audio-server`, `python` for the emb facade
  + `ga-admin`, `csharp` only if a .NET change landed) and diff vs the pre-migration
  baseline. Dockerfiles/nginx/shell are not analyzable — covered by the other two gates.

## 12. Definition of Done

- [ ] One `ga-admin` (Python/FastAPI) owns router-INI CRUD, downloads/ops, SD bundle store
      + sd-server lifecycle, and the ASR/TTS/emb admin adapters — **not** merged into
      `ScriptExecutionAgent` (D6).
- [ ] nginx admin-prefix splits preserve every public path; longest-prefix `location` blocks
      route `/svc/admin/` → `ga-admin`, `/svc/` → engine.
- [ ] `/sd/health` "unloaded" semantics preserved under `ga-admin`.
- [ ] `GET /llama-admin/llama/last-exit` retained (reads the entrypoint's exit file).
- [ ] **Tier A complete:** no torch-dependent *service* package remains in
      `pipdeptree -r -p torch` (`transformers`/`tokenizers`/`accelerate` removed only after
      proving no remaining requirer). **Tier B (torch itself)** removed **only if** D7
      authorizes dropping sandbox torch; otherwise recorded as NOT ATTEMPTED (sandbox
      decision), with `pip show torch` still succeeding — not presented as a torch win.
      Torch-removal gate green for the attempted tier(s).
- [ ] Contract-preservation + per-flavor build gates green; **CodeQL end-only gate clean or
      triaged** for each changed language.
