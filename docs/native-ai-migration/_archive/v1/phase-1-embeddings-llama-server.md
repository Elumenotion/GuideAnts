# Phase 1 — Embeddings via llama-server

Parent: [`00-overview.md`](./00-overview.md). Prerequisite: none (first phase).
Decisions: [`DECISIONS.md`](./DECISIONS.md) **D2** (accept single-instance) + **D3**
(re-embed at cutover) + **D9** (curated model catalog + constrained download — **now
covers embeddings**, see [`model-catalog-and-downloads.md`](./model-catalog-and-downloads.md))
— confirm all before cutover. Gates:
[`contract-preservation-gate.md`](./contract-preservation-gate.md),
[`flavor-build-gate.md`](./flavor-build-gate.md).

**Mission:** replace the sentence-transformers inference inside
`docker/build/guideants-ai/emb-service/emb_service.py` with a dedicated
`llama-server --embeddings` instance serving a GGUF build of
`microsoft/harrier-oss-v1-0.6b`, while preserving the `/emb/` HTTP contract exactly.

---

## 1. Scope

- Convert `emb_service.py` from a torch inference service into a **thin facade/control
  process** that (a) spawns and supervises a private `llama-server --embeddings` child,
  and (b) translates the existing `/emb/embed` contract to the child's
  `/v1/embeddings` API. This mirrors the proven pattern in
  `sd-service/sd_service.py`, which spawns native `sd-server` on :18083 and fronts it
  with `/sd/txt2img` + admin/bundle endpoints.
- Model artifact switch: HF safetensors snapshot (`/models-local/emb/harrier-oss-v1-0.6b`)
  → GGUF file (e.g. `/models-local/emb/<name>.gguf`). Admin download endpoints move from
  HF snapshot downloads to single-file GGUF downloads.
- **Constrained model selection (D9).** Replace today's free-text Hugging Face browse in
  the embeddings model manager with a **curated dropdown of known-good GGUF embedders**
  (`task: emb` in the shared catalog manifest). The download handler validates the selection
  against the manifest + source allowlist and **only** installs a single GGUF that loads on
  `llama-server --embeddings` and produces a vector `<= 1536` (the DB/search width). **The
  curated set is LOCKED (D9, §4a): default `Qwen3-Embedding-0.6B` (official GGUF, Apache-2.0,
  native 1024, multilingual) + alternatives `EmbeddingGemma-300M` and `bge-m3`. This replaces
  the incumbent `microsoft/harrier-oss-v1-0.6b` as the shipped default (cutover in Phase 1,
  one-time re-embed / D3).** Free-form repo selection, safetensors-only repos, and **> 1536**
  models (which `NormalizeToTarget` would silently truncate) are **rejected loudly** (no
  fallback). Full design in [`model-catalog-and-downloads.md`](./model-catalog-and-downloads.md)
  §3.5, §5; the locked set + dimension ceiling + inclusion gate are in §4a below.
- Remove `emb-requirements.txt`'s sentence-transformers dependency from the image
  (torch itself stays until Phase 3/4 completes, since ASR/TTS still need it).

## 2. Out of scope

- Any .NET **dimension** change. `LocalEmbeddingService.SourceVectorDimensions` (`:15`) stays
  **1024** because the new curated default `Qwen3-Embedding-0.6B` is **also native 1024** —
  same width as the incumbent Harrier, so the matched-pair const is unchanged and there is no
  client/wire change. (Only a future default with a different `producedDimension` would force a
  const update.) The default **model** does change (Harrier → Qwen3), which requires a
  **corpus re-embed (D3)** — decided here, executed at cutover (below).
- The chat router (`llama-server --models-preset` on :8080) — see §5 for why the
  embedding model does **not** join it.
- Multi-GPU parity beyond the documented decision (§7, open decision 6.2).
- Corpus re-embedding execution (decision 6.3 is made here; the batch job itself is an
  operational task at cutover).

## 3. Contract to preserve (verified)

`src/server/GuideAntsApi.BackgroundJobs/Services/Embeddings/LocalEmbeddingService.cs`:

- `POST {EmbeddingsBaseUrl}/emb/embed` with camelCase JSON `{ "inputs": [..], "purpose":
  "query"|"document" }`.
- Response must contain `data: [{ embedding: float[] }]` (one per input, same order),
  `dimensions`, and `modelRef`. The local provider currently expects `dimensions == 1024`
  and vector length `== 1024` (hard-checked at `LocalEmbeddingService.cs:98-102,108-112`) —
  the width of both the incumbent Harrier 0.6B **and** the new default Qwen3-Embedding-0.6B —
  encoded in the private const `SourceVectorDimensions = 1024` (`:15`), **not** a wire
  contract. It then normalizes 1024→ **1536** (`:114`,
  `EmbeddingVectorDimensions.NormalizeToTarget`). The **consumer-visible** width is 1536 (DB
  `vector(1536)`, `DocumentChunk.cs:51-53`; search `HybridSearcher.cs:57,82,87`), reached for
  every provider at `ProviderRoutedEmbeddingService.cs:69`. Phase 1 switches the default model
  to Qwen3-Embedding-0.6B (also native 1024), so the 1024 const is unchanged and this stays a
  no-op for consumers **at the wire level** — but the model change still requires a one-time
  corpus re-embed (D3).
- Admin surface consumed by `SettingsServiceLocalModelsEndpoints.cs` +
  `LocalAiStartupWarmupService.cs`: `GET /emb/health`, `GET /emb/ready`,
  `POST /emb/admin/load` (`{model_id | model_path, …}` with `hf_token` stamped in by
  .NET), `POST /emb/admin/unload`, `GET /emb/admin/models`,
  `POST /emb/admin/models/download`, `GET /emb/admin/models/{operation_id}`,
  `POST /emb/admin/models/{operation_id}/cancel`, `DELETE /emb/admin/models/{model_ref}`.
- Entrypoint readiness monitor (`entrypoint.sh:439-446`) polls `/ready` on
  `GA_EMB_PORT` when `GA_EMB_AUTO_LOAD_ON_STARTUP=1` and
  `GA_EMB_WAIT_FOR_READY_ON_STARTUP=1`.
- Container `HEALTHCHECK` includes `curl http://localhost/emb/health`.

**Purpose semantics (prefix CHANGES with the new default):** today `emb_service.py:388-399`
applies `prompt_name="web_search_query"` for `purpose == "query"` (and no prompt for
documents) — that is **Harrier's** sentence-transformers prompt. The new default
`Qwen3-Embedding-0.6B` is **instruction-aware** and uses the Qwen **`Instruct: …\nQuery: …`**
query-instruction format instead. So the facade must (a) apply the Qwen `Instruct:` query
prefix for the default, and (b) keep prefix handling **per-entry** (EmbeddingGemma uses task
prompt templates; bge-m3 needs **no** prefix). The exact strings are **validated in this
phase** by reading each chosen model's card/config and hardcoding the verified string in the
facade with a source comment — and confirming retrieval parity. Getting the query prefix wrong
silently degrades retrieval.

## 4. Required changes

| Component | Change |
|-----------|--------|
| `docker/build/guideants-ai/emb-service/emb_service.py` | Rewrite: keep FastAPI shell + admin/threading skeleton; replace torch/sentence-transformers internals with (1) subprocess management of `llama-server --embeddings --pooling last -m <gguf> --host 127.0.0.1 --port 18085` (port pattern follows sd-server's :18083), (2) `/embed` → `/v1/embeddings` translation incl. query prefix, order preservation, and `dimensions`/`modelRef` reporting, (3) GGUF-file download ops instead of HF snapshot downloads. |
| `docker/build/guideants-ai/emb-requirements.txt` | Drop `sentence-transformers` (and anything only it needed). |
| `docker/build/guideants-ai/start-emb.sh` | Env plumbing for the child process (GPU device selection via existing `apply_cuda_visible_devices_override` helper pattern; new `GA_EMB_NGL`-style knob if needed for `--n-gpu-layers`). |
| `docker/build/guideants-ai/Dockerfile.{cpu,cuda,rocm,vulkan}` | Remove emb-requirements torch-adjacent installs; no new binaries needed — `llama-server` is already `/app/llama-server` from the `ghcr.io/ggml-org/llama.cpp:server-*` base image of each flavor. |
| `docker/docker-compose.*.yml`, `docker/.env` | `GA_EMB_DEFAULT_MODEL_PATH` now names a GGUF (today `docker/.env:32` has `harrier-oss-v1-0.6b`, a directory name). Update defaults + `docker/guideants-ai-build.md` §model docs. |
| nginx.conf | No change (`/emb/` → :8085 stays; the llama-server child on :18085 is loopback-only, like sd-server). |

### Env var disposition

| Var | Disposition |
|-----|-------------|
| `GA_EMB_HOST/PORT/MODEL_DIR/DEFAULT_MODEL_PATH/AUTO_LOAD_ON_STARTUP/WAIT_FOR_READY_ON_STARTUP/READY_TIMEOUT_SECONDS/WARMUP_ON_LOAD` | Kept, same meaning. |
| `GA_EMB_DEVICE` (`cpu`/`cuda`/`cuda-multi`/`rocm`/`mps`) | Repurposed: maps to child launch config (`--n-gpu-layers`, device selection). `cuda-multi` handling per §7. |
| `GA_EMB_FIX_MISTRAL_REGEX` | Retired (sentence-transformers tokenizer workaround; meaningless under llama.cpp). Fail loudly if set, or log-and-ignore with a deprecation warning — pick one, do not silently honor. |

## 4a. Curated model catalog + constrained download (D9) — known-good GGUF only

The embeddings model manager is currently **free-form**: `EmbRuntimeManager.tsx`'s
`DownloadModelDialog` browses an arbitrary HF repo via `RepositoryFilePicker`
(`EmbRuntimeManager.tsx:590-717`, repo input `:668-691`) and posts `{ model_id }`
(`:228-235`); `emb_service.py` accepts any non-empty `model_id`
(`emb_service.py:771-776`) and `snapshot_download`s the whole snapshot (`:227-237`); the
.NET validator only checks `model_id` is present (`ServiceLocalModelDownloadValidator.cs:14-18`).
That lets a user install a repo that `llama-server --embeddings` cannot load, or a GGUF
whose dimension exceeds the storage/search width. **D9 (now extended to embeddings) replaces
this with a curated dropdown** of `task: emb` catalog entries — see
[`model-catalog-and-downloads.md`](./model-catalog-and-downloads.md) §3.5 (the actual
dimension model), §5.2 (dropdown UX), §5.3 (download validation), §5.6 (contract table).

### 4a.1 The dimension model (what is actually invariant) + the ≤ 1536 ceiling

**The client-/consumer-invisible width is 1536, not 1024.** Storage is `vector(1536)`
(`DocumentChunk.cs:51-53`); search declares `@queryVec vector(1536)` and runs
`vector_distance('cosine', …)` (`HybridSearcher.cs:57,82,87`); and **every** provider is
normalized to 1536 at the router (`ProviderRoutedEmbeddingService.cs:69` →
`EmbeddingVectorDimensions.NormalizeBatchToTarget`, `Target = 1536`,
`EmbeddingVectorDimensions.cs:5`). **1024 is not the contract** — it is the private const
`LocalEmbeddingService.SourceVectorDimensions = 1024` (`:15`), the local provider's
expectation of *today's* model, paired with the `/emb/embed` server. `NormalizeToTarget`
(`EmbeddingVectorDimensions.cs:7-18`) **zero-pads when < 1536 and SILENTLY TRUNCATES when >
1536** (`Math.Min(source.Length, Target)`).

Consequences the catalog enforces (the rule is `<= 1536`, not `== 1024`):

- Only GGUF embedders with `producedDimension <= 1536` may be offered. A model producing
  **> 1536** would be **silently truncated** (data loss) — so it must be **rejected loudly**,
  never allowed to lean on truncation. This is the genuinely-dangerous case (the banned
  silent fallback lives in `NormalizeToTarget`).
- A **≤ 1536** model (including a different one at 1024) is *swappable* but is **not** a
  free drop-in: the local emb server and `SourceVectorDimensions` are updated together (a
  **matched pair**), and — the real cost — the **corpus is re-embedded** (D3), because
  cosine distance across two models' spaces is meaningless. The dimension is secondary; the
  re-embed is the actual blocker.
- The manifest encodes the model's native width as `producedDimension` (require `<= 1536`);
  the download handler asserts the **actual** produced dimension equals the declared value
  after load before marking the model usable (load-after-download, §8). This closes the
  "downloadable ≠ loadable ≠ within-ceiling" gap for embeddings.

Phase 1 switches the default to `qwen3_embedding_0_6b` (also native 1024), so
`SourceVectorDimensions` stays 1024 and nothing changes for consumers **at the wire level** —
but the model change still requires a one-time corpus re-embed (D3).

### 4a.2 Embedding model set — LOCKED (D9, 2026-07-02)

**The curated set is a small, vetted list (not free browse):**

| Manifest id | Role | Arch / pooling | GGUF source (pinned rev + SHA256) | `producedDimension` | Multilingual | Prompt format |
|---|---|---|---|---|---|---|
| `qwen3_embedding_0_6b` | **DEFAULT** | Qwen3 / last-token | **Official GGUF** `Qwen/Qwen3-Embedding-0.6B-GGUF` (q8_0 / f16) | **1024** native (MRL 32–1024 → pin ≤ 1536) | **Yes** (100+) | **`Instruct:` query prefix** (re-validate) |
| `embedding_gemma_300m` | alt — footprint-first | Gemma-embedding / mean | **Official GGUF** `ggml-org/embeddinggemma-300m-GGUF` (≈308M, best CPU-flavor/image size) | **768** (MRL 512/256/128) | **Yes** (100+) | task prompt templates (query/document) |
| `bge_m3` | alt — no-prefix multilingual | XLM-RoBERTa / mean | **No official GGUF** → pinned trusted community GGUF (repo + revision + SHA256) | **1024** | **Yes** (100+) | **None** for dense (simplest facade) |

Each entry is still **to validate in Phase 1** via a retrieval-parity harness on a
representative GuideAnts corpus — do **not** claim proven quality (benchmarks are
cross-source/directional). The **incumbent** `harrier_oss_v1_0_6b` (Qwen3 / last-token,
L2-norm, native 1024) is kept as **historical context only** (was the default); it is **not
re-blessed**.

**Default cutover consequences (Harrier → `Qwen3-Embedding-0.6B`):**
- Reuses our `llama-server --embeddings --pooling last` path — **same Qwen3 arch as the
  incumbent → lowest-risk swap.**
- The facade query prefix changes from the current `web_search_query` form
  (`emb_service.py:394,399`) to the Qwen **`Instruct:`** query-instruction format, and is
  re-validated.
- Requires a **one-time full corpus re-embed (D3)** — the real cost — because cosine across
  two models' spaces is meaningless (a same-1024-dim model is still a model change).

**Multilingual is treated as a required/default-safe capability** (the default satisfies it).
English-only models (e.g. `nomic-embed-text-v1.5`) may be listed as alternatives but are
clearly tagged **English-only** and are **never the default**.

**License is NOT a curation filter.** The user downloads each model from its source, so
**license compliance is the user's responsibility**; we surface license info for transparency
only and never disqualify an entry on license grounds. (Contrast the **voice pack (D10)**,
where GuideAnts redistributes assets in-image and *does* own compliance — the difference is
redistribution vs. user-initiated download.)

**Dimension rule:** every listed entry is native (or MRL-pinned) ≤ 1536. A model producing
**> 1536** (e.g. larger Qwen3-Embedding siblings at 2560/4096, Harrier-27b @5376) is
**rejected loudly** — `NormalizeToTarget` would silently truncate it (data loss).

### 4a.3 GGUF source / provenance (LOCKED)

We **point the catalog at the model source and the user downloads it** — GuideAnts does **not**
convert-and-ship its own artifact. Provenance rule per entry:

- **Prefer an official GGUF** where one exists (Qwen3-Embedding and EmbeddingGemma both ship
  one).
- Where none exists (**bge-m3**), the user pulls a **pinned trusted community GGUF** by repo +
  revision + SHA256; the picker never resolves a floating "latest" upload. This is a
  **provenance** note, not a license issue.

Every entry is validated by the inclusion gate (§8) once during Phase 1 before it is listed.

## 5. Why a dedicated instance, not the chat router

Investigated (llama.cpp PR #17859 + router docs): the router's per-alias INI accepts
arbitrary long/short llama-server args as keys, so an alias with `embeddings = true`,
`pooling = last` is *syntactically* expressible in `router-models.ini`. Rejected anyway:

1. **LRU eviction**: the router unloads least-recently-used models at `--models-max`
   (`GA_LLAMA_MODELS_MAX`); background embedding jobs would thrash chat models and
   vice-versa, and an evicted embedding model adds a full reload to the next job.
2. **Lifecycle coupling**: `llama-admin` SIGTERMs llama-server on INI change and the
   entrypoint respawns it (`entrypoint.sh:463-490`); embeddings would flap on every chat-
   model edit. The entrypoint also *sanitizes* the INI at boot, deleting entries whose
   files are missing — an embedding alias becomes another moving part in that machinery.
3. **Contract**: the admin/download/ready surface .NET expects lives on `/emb/*`; a
   router alias provides none of it.
4. Whether the router actually routes `/v1/embeddings` by `model` field to a child is
   **unverified** — if someone wants the router option later, that is the first thing to
   test. Not needed for this phase.

## 6. Backend/flavor matrix

| Flavor | llama-server build (already shipped) | Embedding child |
|--------|--------------------------------------|-----------------|
| cuda13 | `server-cuda13` base image | GPU (`--n-gpu-layers` max) |
| vulkan | `server-vulkan` base image | GPU — **first time local embeddings get GPU on this flavor** (today `GA_EMB_DEVICE=cpu`, see `docker-compose.vulkan.yml:144`) |
| rocm | `server-rocm` base image | GPU (today `GA_EMB_DEVICE=rocm` with CPU torch wheels — verify what that actually did; likely CPU) |
| cpu | `server` CPU base image | CPU |

The 0.6B model is small; CPU serving may be acceptable on weak-GPU hosts — the existing
`GA_EMB_DEVICE=cpu` escape hatch stays meaningful.

## 7. Risks

- **Numerical drift / retrieval quality** (open decision 6.3): GGUF quantization changes
  vectors. Mitigation: prefer F16 or Q8_0 GGUF; run a retrieval-parity check (embed a
  sample doc set + queries both ways; compare cosine sims and top-k overlap). Decide
  re-embed-at-cutover vs mixed vectors **before** rollout. Recommended: re-embed.
- **GGUF availability/dimensions**: the default `Qwen3-Embedding-0.6B` ships an **official
  GGUF** (`Qwen/Qwen3-Embedding-0.6B-GGUF`); it must be validated to emit its expected
  **1024**-dim vectors with `--pooling last` — the local provider hard-fails on `!= 1024`
  today (`LocalEmbeddingService.cs:98-102`; good: loud, not silent) because 1024 is the pinned
  `SourceVectorDimensions` (unchanged, since Qwen3 is also 1024). Alternatives: EmbeddingGemma
  has an official GGUF (native 768); bge-m3 has **no** official GGUF, so a pinned trusted
  community GGUF (repo + revision + SHA256) is used. **Ceiling risk (any future model):** a
  model producing **> 1536** would be silently truncated by `NormalizeToTarget`
  (`EmbeddingVectorDimensions.cs:15`) — the catalog rejects `> 1536` loudly (§4a.1), never
  relies on truncation.
- **Query prefix fidelity**: the default moves to the Qwen **`Instruct:`** query format;
  getting the per-entry query prefix wrong silently degrades query embeddings. Mitigation:
  parity test asserts query-vs-document vectors differ from each other as expected for the
  active model, validated against its model card/config.
- **cuda-multi parity** (open decision 6.2): options — (a) accept one instance (current
  .NET client is serialized anyway via `_requestGate`; recommended), (b) facade spawns N
  children on N GPUs and round-robins, (c) llama.cpp `--split-mode layer`. Measure
  before choosing (b)/(c).
- **Batching**: `emb_service.py` batches internally; llama-server `/v1/embeddings`
  accepts arrays. Validate max batch/context behavior with the largest input batches the
  background jobs send (chunked documents).

## 8. Validation

1. Golden parity set: ≥200 texts (mixed lengths incl. >512-token chunks) + ≥50 queries.
   Record current-service vectors before switching. Compare: per-vector cosine ≥ target
   (define after F16 measurement), top-k retrieval overlap on a seeded corpus.
2. Contract tests against `/emb/embed`, `/emb/health`, `/emb/ready`, all `/emb/admin/*`
   ops (download → progress poll → load → ready → unload → delete) — exact JSON field
   names, incl. `dimensions` (= the active model's dim, **1024** for the default
   Qwen3-Embedding-0.6B) and `modelRef`; and that .NET consumers still receive **1536**-wide
   vectors after normalization (`ProviderRoutedEmbeddingService.cs:69`).
3. `LocalAiStartupWarmupService` end-to-end: container boot with
   `GA_EMB_AUTO_LOAD_ON_STARTUP=1`, `GA_EMB_WAIT_FOR_READY_ON_STARTUP=1` reaches ready
   within timeout on all four flavors.
4. Settings UI: model list/download/load/unload round-trip via
   `/{serviceId}/local-models*` endpoints (serviceId=`Embeddings`).
5. **Curated catalog + constrained download (D9):** the embeddings picker offers **only**
   `task: emb` catalog entries (a dropdown, not the free-text `RepositoryFilePicker`); a
   download for a catalog id fetches only its allowlisted repo + single GGUF file; a
   non-manifest repo, a safetensors-only repo, a multi-file/globbed request, or a gated repo
   without a token is **rejected loudly** (contract-compatible 400/failed op), never coerced.
6. **Inclusion gate (load-after-download dimension assertion):** a candidate `emb` model is
   published into the manifest **only after** it loads on our `llama-server --embeddings
   --pooling last` build and its actual produced dimension equals the recorded
   `producedDimension` and is `<= 1536`; a `> 1536` model (or an actual≠declared mismatch)
   fails the gate loudly — never silently truncated by `NormalizeToTarget` — and simply never
   ships. There is **no** runtime `loadVerified` flag: the shipped manifest is the verified
   set. For the default `Qwen3-Embedding-0.6B` this dimension is `== 1024`, matching the pinned
   `SourceVectorDimensions`. **The Harrier → Qwen3 cutover (and any later model change) also
   triggers a corpus re-embed (D3) before retrieval is trusted.**
7. Kill-child resilience: SIGKILL the llama-server child; facade must report unhealthy
   (`/ready` false, `/health` degraded) and recover on next `/admin/load` — no silent
   in-process fallback.

## 9. Rollback

Image-tag rollback only. The previous image (torch emb_service) and this image are
config-compatible except `GA_EMB_DEFAULT_MODEL_PATH` (directory vs GGUF file) — keep both
artifacts on the `/models-local/emb` volume during the transition window so either image
boots. Document the pair of known-good values in the release notes. If corpus re-embedding
(6.3) has already run, note that rolling back the image does NOT roll back vectors —
re-embedding back is the symmetric operation.

## 10. Gates

Run after this phase; record results in [`STATUS.md`](./STATUS.md).

- [`contract-preservation-gate.md`](./contract-preservation-gate.md) — `/emb/*` golden
  replay identical (`dimensions` = active model's dim, **1024** for the default
  Qwen3-Embedding-0.6B; `modelRef`, input order; and .NET consumers still get **1536**-wide vectors after
  normalization); `/emb/admin/*` full cycle; `/ready` warmup gating; kill-child recovery
  with no silent in-process fallback. Also assert the **D9 embeddings-catalog** checks (gate
  §3.2): new `/emb/admin/catalog` GET is additive under the existing `/emb/` prefix; the
  picker is a curated dropdown; the download handler rejects non-manifest / non-GGUF /
  `> 1536` requests loudly; the `/emb/embed` route shape is unchanged.
- [`flavor-build-gate.md`](./flavor-build-gate.md) — cuda13/vulkan/rocm/cpu build + boot +
  `HEALTHCHECK`; the emb `llama-server --embeddings` child runs on the intended backend
  (vulkan flavor now GPU, not CPU).
- CodeQL is **not** run here — it is end-only (see [`codeql-gate.md`](./codeql-gate.md)); the
  rewritten `python` facade is scanned in the final gate.

## 11. Definition of Done

- [ ] `emb_service.py` is a thin facade spawning/supervising `llama-server --embeddings`;
      no torch/sentence-transformers in its import path.
- [ ] `/emb/embed` route shape preserved exactly; `dimensions` reflects the active model
      (**1024** for the default Qwen3-Embedding-0.6B) and consumers still receive **1536**-wide
      vectors; the per-entry query prefix (default = Qwen **`Instruct:`**; verified from the
      model config, source-commented) and retrieval parity re-validated.
- [ ] **D9 emb curated set LOCKED** (§4a.2): **default = `Qwen3-Embedding-0.6B`** (official
      GGUF, Apache-2.0, native 1024, multilingual), alternatives `EmbeddingGemma-300M` + `bge-m3`;
      Harrier is historical context only. Default cutover (Harrier → Qwen3) done with the
      `Instruct:` prefix change and a one-time corpus re-embed (D3); each entry validated by the
      inclusion gate to emit its recorded dim (≤ 1536) with `--pooling last`.
- [ ] **Curated catalog (D9):** the embeddings picker is a dropdown of the **published**
      `task: emb` entries only (no free-text HF browse); the download handler enforces
      manifest + source allowlist + single-GGUF + `producedDimension <= 1536`; free-form /
      safetensors-only / `> 1536` / gated-without-token requests are rejected loudly (no
      fallback); every published entry passed the Phase 1 **inclusion gate** (loaded once,
      actual dim == recorded `producedDimension` ≤ 1536) so the manifest is the verified set
      and the picker branches on **no** verification flag; `SourceVectorDimensions` matches
      the active entry (matched pair); a model change triggers a re-embed (D3).
- [ ] Retrieval-parity measured; D3 (re-embed) confirmed before cutover; D2 confirmed.
- [ ] `GA_EMB_FIX_MISTRAL_REGEX` disposition is explicit (fail-loud or deprecation-log), not
      silently honored.
- [ ] Contract-preservation + per-flavor build gates green on all four flavors.
- [ ] **Torch-removal step (Tier A, partial):** `sentence-transformers` dropped from
      `emb-requirements.txt`; `pipdeptree -r -p torch` no longer lists `sentence-transformers`
      on any full flavor. `torch` itself stays (ASR/TTS still need it) — recorded, not claimed
      as removed. See [`torch-removal-gate.md`](./torch-removal-gate.md) §3 step 2.
