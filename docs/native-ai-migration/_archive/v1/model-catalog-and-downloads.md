# Model catalog & downloads — constraining ASR/TTS/embeddings selection to the native-engine known set

Parent: [`00-overview.md`](./00-overview.md). Decisions: [`DECISIONS.md`](./DECISIONS.md)
**D9** (curated catalog manifest, this doc — now covering embeddings, ASR, and TTS) +
**D1** (TTS = `chatterbox` + voice pack) + **D5** (`ga-audio-server` adapter) +
**D6** (`ga-admin` control plane) + **D2/D3** (embeddings single-instance + re-embed at
cutover). Gates: [`contract-preservation-gate.md`](./contract-preservation-gate.md),
[`flavor-build-gate.md`](./flavor-build-gate.md).

**Mission:** make the local-AI **settings UI + admin control plane** offer, for the three
native-engine-backed services (**embeddings**, **ASR**, and **TTS**), only the model set
that the target engine can actually load, and make model **downloads** proceed only from
the canonical Hugging Face sources a curated manifest names — rejecting anything else
loudly instead of coercing or silently widening the download. For ASR/TTS the engine is
**audio.cpp** and the known set is derived from its downloader (`tools/model_manager.py`),
its registered loaders (`registry.cpp`), and its `README.md`. For **embeddings** the
engine is **llama.cpp** (`llama-server --embeddings`, Phase 1), the known set is
**GGUF single-file** embedders that load with `--pooling last` **and produce a vector of
≤ 1536 dimensions** (the storage/search ceiling — see §3.5), and the canonical source is
either the model's official GGUF repo or a GuideAnts-pinned conversion of the current
safetensors model. This spans the embeddings picker in Phase 1, ASR selection/download in
Phase 2, TTS + voice-pack in Phase 3, and download-source enforcement in `ga-admin` in
Phase 4.

This is a design/plan document. It changes no source, Docker, or config files; it wires a
new decision (D9) and cross-links into the existing plan.

---

## 1. Scope

- **IN SCOPE — the three native-engine-backed local services:**
  - **Embeddings** (`Embeddings`, nginx `/emb/`) — a **GGUF** embedder on
    `llama-server --embeddings` per Phase 1 (`phase-1-embeddings-llama-server.md`).
    The current model is `microsoft/harrier-oss-v1-0.6b` (Qwen3-architecture, native
    **1024-dim**, last-token pooling — verified below); its GGUF equivalent replaces the
    sentence-transformers snapshot. **1024 is today's model's native dimension, not a
    frozen contract** — .NET normalizes every provider's vector to the DB/search width of
    **1536** (§3.5). **Added to D9 by the 2026-07-02 scope expansion** (previously out of
    scope; see the note at the end of §2).
  - **ASR** (`SpeechTranscription`, nginx `/asr/`) — Qwen3-ASR-0.6B on audio.cpp (Phase 2).
  - **TTS** (`SpeechSynthesis`, nginx `/tts/`) — Chatterbox (`ResembleAI/chatterbox`) per
    the locked **D1** decision, plus a GuideAnts-curated **voice pack** of reference WAVs
    (Phase 3).
- Constraining each **model picker** to a curated catalog (audio.cpp-derived for ASR/TTS;
  llama.cpp/GGUF + `producedDimension <= 1536` for embeddings), the **download-source allowlist**,
  gated-repo handling (HF token), and **where the enforcement lives** (`ga-admin`, Phase 4;
  the Phase 1 emb-service and Phase 2/3 engine admin skeletons before consolidation).

## 2. Out of scope (boundaries only — do not design these here)

- **llama chat models** (`/llama-admin/`, router-INI presets, `AddModelWizard.tsx`). That
  is a GGUF-alias catalog with its own onboarding wizard
  (`src/client/src/pages/settings/components/catalog/AddModelWizard.tsx:32-50`) and is
  unrelated to the audio.cpp model set.
- **Stable Diffusion bundles** (`/sd/`, `ImageBundleManager.tsx`). SD already has a
  **structured** download contract (see §5.4) that this design borrows from as a pattern,
  but the SD bundle store and its `(repo, single filename)` per-role shape are not
  redesigned here.
- audio.cpp families that are **not** ASR/TTS (diarization, VAD, source separation,
  codec, alignment, music generation) — noted in the catalog only to mark them
  out-of-catalog for these two services.

> **Scope-expansion note (2026-07-02).** Embeddings were originally **out of scope** for
> D9 (ASR/TTS only). The user has expanded the decision: **embeddings now get the same
> curated-catalog + constrained-download treatment**, offering only known-good GGUF
> embedders. The embeddings flow is *not* the same as ASR/TTS — it targets llama.cpp
> (GGUF), not the audio.cpp safetensors set, and it carries a **dimension ceiling of 1536**
> (the DB/search width; the local model's native 1024 is normalized up to it —
> `EmbeddingVectorDimensions.cs:5-18`, `LocalEmbeddingService.cs:15,114`) — so it is
> documented as a distinct task (`task: "emb"`) in the shared manifest (§3.5, §5). The
> reused UX pattern is still the operation-tracking download flow. This doc no longer
> treats embeddings as a boundary.

---

## 3. audio.cpp authoritative catalog + valid sources (the allowlist origin)

### 3.1 Two sources of truth that must be intersected

audio.cpp expresses "what can be downloaded" and "what can be loaded" in **two different
places**, and they are **not** the same set:

1. **`tools/model_manager.py` `CATALOG`** (`d:\repos\audio.cpp\tools\model_manager.py:148-899`)
   — the authoritative **downloader**: each `ModelPackage` names the canonical HF
   `repo_id`(s), the required files, and the on-disk `target_directory`. It can download
   more than the release engine can run (e.g. `kokoro_82m_bf16`, `moss_tts`).
2. **`src/framework/runtime/registry.cpp` `make_default_registry`**
   (`d:\repos\audio.cpp\src\framework\runtime\registry.cpp:205-232`) — the **loaders
   actually registered** in the release build. Development entries are **commented out**
   (`registry.cpp:6-14, 207-215`): `kokoro_tts`, `ace_step`, `demucs`, `roformer`,
   `moss_tts`, `heartmula`, `higgs_tts`, `parakeet_tdt` are **present in the downloader
   catalog but NOT loadable** in the release tree.
3. **`README.md` model table** (`d:\repos\audio.cpp\README.md:42-71`) — release status
   (`released` / `integration` / `optimization`) and languages.

> **Correctness rule for the manifest (honours "no arbitrary fallback"):** a catalog entry
> is offered to users **only if** its family is (a) in `model_manager.py` `CATALOG` with a
> confirmed HF source, **and** (b) registered in `registry.cpp` `make_default_registry`,
> **and** (c) marked `released` in the README table. An entry that is downloadable but not
> loadable (or vice versa) is a trap — it must be excluded or explicitly flagged, never
> offered "hoping it works".

### 3.2 Format constraint (per task — and it is INVERTED between the two engines)

The `format` field is task-specific and the two engines are **mutually exclusive** on it —
which is exactly why the manifest must carry `task` and enforce `format` per entry, never a
single global rule:

- **ASR/TTS (audio.cpp):** models are **safetensors / ggml** layouts. **GGUF loading is
  NOT supported yet** (`README.md:653` "GGUF model loading is planned, but not supported
  yet"). So for `task: asr|tts` the manifest and download path must:
  - Only reference safetensors/ggml snapshots (never GGUF single-file repos).
  - Reject a free-form GGUF repo id even if it "looks like" an audio model. The current ASR
    add-model dialog placeholder is literally `openai/whisper-large-v3`
    (`AsrModelManager.tsx:678`) — Whisper is **not an audio.cpp family at all**; that is
    exactly the free-form choice this design must stop.
- **Embeddings (llama.cpp):** the **opposite** — `llama-server --embeddings` loads a
  **single-file GGUF**, not safetensors. So for `task: emb` the manifest must:
  - Only reference a **single `.gguf` file** in an allowlisted repo (reuse the no-glob rule;
    llama-admin's own downloader already resolves exactly one GGUF via a quant-include
    regex, `llama_admin_service.py:908-919,934-941`).
  - **Reject a safetensors-only snapshot** for embeddings — that is the *old torch path*
    (today's `emb_service.py` `snapshot_download`, §4.5) and does not load under
    `--embeddings`. Rejecting it is loud, not silent.
  - Enforce the **≤ 1536 dimension ceiling** at manifest level: only offer entries whose
    `producedDimension <= 1536` (see §3.5). A model that produces **> 1536** would be
    **silently truncated** by `EmbeddingVectorDimensions.NormalizeToTarget`
    (`EmbeddingVectorDimensions.cs:15`, `Math.Min(source.Length, Target)`) — so it must be
    **rejected loudly**, not offered. This is a `format`-adjacent hard constraint unique to
    embeddings (the ≤ 1536 rule, not a "must == 1024" rule).

### 3.3 In-scope catalog (ASR + TTS) — model → family → source → files → layout → status

Every repo id and file list below is copied from `model_manager.py`; every registry line
is `registry.cpp`; every status/language is `README.md`. Nothing here is invented.

#### ASR

| Manifest entry (id) | Family | Canonical HF source(s) | Kind | Required files (from model_manager) | On-disk dir | Registered? | README status | Offer for ASR? |
|---|---|---|---|---|---|---|---|---|
| `qwen3_asr_0_6b` | `qwen3_asr` | `Qwen/Qwen3-ASR-0.6B` (`model_manager.py:251`) | HF snapshot | `config.json, generation_config.json, model.safetensors, preprocessor_config.json, tokenizer_config.json, vocab.json, merges.txt` (`:252-260`) | `Qwen3-ASR-0.6B` | **Yes** (`registry.cpp:223`) | released (`README.md:51`) | **YES — default/only** |
| `citrinet_asr` | `citrinet_asr` | NeMo archive `stt_en_citrinet_256.nemo` via `api.ngc.nvidia.com` (`model_manager.py:832`) — **converter, not HF** | NeMo→safetensors converter | `citrinet_256.safetensors, *_config.json, *_tokenizer.model, *_vocab.txt` (`:836-841`) | `citrinet` | Yes (`registry.cpp:227`) | released (`README.md:45`) | Optional (en-only; converter source, not a plain snapshot) — **defer to Phase 2 "extras"** |
| `parakeet_tdt_0_6b_v3` | `parakeet_tdt` | `nvidia/parakeet-tdt-0.6b-v3` (`model_manager.py:377`) | HF snapshot | `config.json, model.safetensors, processor_config.json, tokenizer.json` (`:378`) | `parakeet-tdt-0.6b-v3` | **NO — loader commented out** (`registry.cpp:13,215`) | integration (`README.md:68`) | **NO — downloadable but not loadable** |

GuideAnts ships ASR = `qwen3_asr_0_6b` (matches `GA_ASR_DEFAULT_MODEL_ID=Qwen/Qwen3-ASR-0.6B`,
`GA_ASR_DEFAULT_MODEL_PATH=Qwen3-ASR-0.6B`, `phase-2-asr-audiocpp.md:64-68`). `citrinet_asr`
is the only other *loadable* ASR family; whether to expose it is a Phase 2 call
(en-only, and its source is an NGC converter, not a plain HF snapshot). `parakeet_tdt`
must **not** be offered — it is in the downloader but its loader is commented out.

#### TTS (family = `chatterbox` is the D1 choice; others listed for completeness of the loadable set)

| Manifest entry (id) | Family | Canonical HF source(s) | Kind | Required files (from model_manager) | On-disk dir | Registered? | README status | Offer for TTS? |
|---|---|---|---|---|---|---|---|---|
| `chatterbox` | `chatterbox` | `ResembleAI/chatterbox` (`model_manager.py:353`) | HF snapshot (README "Yes"/ready) | `ve.safetensors, t3_cfg.safetensors, t3_mtl23ls_v2.safetensors, t3_mtl23ls_v3.safetensors, s3gen.safetensors, tokenizer.json, grapheme_mtl_merged_expanded_v1.json, Cangjie5_TC.json, conds.pt` (`:354-364`) | `chatterbox` | **Yes** (`registry.cpp:231`) | released, 19 langs, no ja/zh (`README.md:44`) | **YES — D1 default** |
| `qwen3_tts_0_6b_base` | `qwen3_tts` | `Qwen/Qwen3-TTS-12Hz-0.6B-Base` (`:281`) | HF snapshot | `config.json, generation_config.json, model.safetensors, speech_tokenizer/config.json, speech_tokenizer/model.safetensors, tokenizer_config.json, vocab.json, merges.txt` (`:282-291`) | `Qwen3-TTS-12Hz-0.6B-Base` | Yes (`registry.cpp:224`) | released (`README.md:53`) | Candidate (Phase 3 extras) |
| `qwen3_tts_1_7b_base` | `qwen3_tts` | `Qwen/Qwen3-TTS-12Hz-1.7B-Base` (`:297`) | HF snapshot | same shape as 0.6B (`:298-307`) | `Qwen3-TTS-12Hz-1.7B-Base` | Yes | released | Candidate |
| `qwen3_tts_1_7b_custom_voice` | `qwen3_tts` | `Qwen/Qwen3-TTS-12Hz-1.7B-CustomVoice` (`:313`) | HF snapshot | same shape (`:314-323`) | `Qwen3-TTS-12Hz-1.7B-CustomVoice` | Yes | released | Candidate |
| `qwen3_tts_1_7b_voice_design` | `qwen3_tts` | `Qwen/Qwen3-TTS-12Hz-1.7B-VoiceDesign` (`:329`) | HF snapshot | same shape (`:330-339`) | `Qwen3-TTS-12Hz-1.7B-VoiceDesign` | Yes | released | Candidate |
| `omnivoice` | `omnivoice` | `k2-fsa/OmniVoice` (`:238`) | HF snapshot | `config.json, model.safetensors, tokenizer.json, audio_tokenizer/config.json, audio_tokenizer/model.safetensors` (`:239-245`) | `OmniVoice` | Yes (`registry.cpp:216`) | released, 646+ langs (`README.md:49`) | Candidate |
| `miotts_1_7b` | `miotts` (+`miocodec`) | **composite**: `Aratako/MioTTS-1.7B` + `Aratako/MioCodec-25Hz-44.1kHz-v2` + `mlx-community/wavlm-base-plus-mlx` (`:431-467`) | composite snapshot | see `:471-479` + sibling MioCodec | `MioTTS-1.7B` | Yes (`registry.cpp:218,217`) | released, en/ja (`README.md:48`) | Candidate (multi-repo) |
| `voxcpm2` | `voxcpm2` | `OpenBMB/VoxCPM2` (`:864`) | HF snapshot **+ post-process** (`audiovae.pth`→`.safetensors`, `model_manager.py:125,1195-1201,1249-1251`) | `config.json, model.safetensors, tokenizer.json, tokenizer_config.json, audiovae.pth, audiovae.safetensors` (`:865-872`) | `VoxCPM2` | Yes (`registry.cpp:219`) | released, ~30 langs (`README.md:59`) | Candidate (needs post-process step) |
| `vibevoice_1_5b` | `vibevoice` | `microsoft/VibeVoice-1.5B` (`:489`) — **composite + bundled tokenizer assets** copied from `assets/model_manager/vibevoice_1_5b` (`:1308-1313`) | composite snapshot | `:501-512` | `VibeVoice-1.5B` | Yes (`registry.cpp:220`) | released, en/zh (`README.md:58`) | Candidate (bundled sidecar assets) |
| `pocket_tts` | `pocket_tts` | `kyutai/pocket-tts` — **GATED**, English + `alba` only (`:384-392`; `README.md:430-431`) | HF snapshot (gated, `include_prefixes=languages/english/`) | `languages/english/model.safetensors, languages/english/tokenizer.model, languages/english/embeddings/alba.safetensors` (`:388-392`) | `pocket-tts` | Yes (`registry.cpp:221`) | released (`README.md:50`) | Candidate — **needs HF token; document gated** |
| `vevo2` | `vevo2` | `RMSnow/Vevo2` (`:704`) + downloaded `whisper-medium` dependency (`:1296-1299`) | composite snapshot + Whisper dep | large set `:757-785` | `Vevo2` | Yes (`registry.cpp:229`) | released, en/zh (`README.md:57`) | Candidate (heavy, multi-part) |

**Loadable-but-not-primary VC/other:** `seed_vc` (`mlx-community/SeedVC-MLX`, `:792`,
`registry.cpp:230`) is **voice conversion**, not TTS — exclude from the TTS picker.

**Downloadable-but-NOT-loadable in the release tree (must never be offered as TTS):**
`kokoro_82m_bf16` (`mlx-community/Kokoro-82M-bf16`, `:187`), `moss_tts` /
`moss_tts_nano_100m` (`:197,222`), `heartmula` (`:535`), `higgs_audio_v3_tts_4b`
(`:518`), `irodori_tts_500m_v3` / `irodori_tts_600m_v3_voice_design` (`:584,613`),
`supertonic_3` (`:681`) — their loaders are commented out (`registry.cpp:6-14,207-215`)
or the README lists them `integration`, not `released`. This is the crucial reason the
manifest is an **intersection**, not a copy of `model_manager.py`.

### 3.4 Chatterbox: model download vs voice pack (the boundary)

- **What `model_manager` downloads for `chatterbox`:** the model weights snapshot from
  `ResembleAI/chatterbox` (files listed above). That is the *only* HF artifact.
- **Built-in voices: none.** Chatterbox is voice-clone-only; every request needs a
  reference WAV (`docs/tts.md:34-35`, task `clon`). `model_manager` downloads **no**
  reference voices.
- **The voice pack is GuideAnts's own asset, not a model_manager model and not an HF
  download.** Per D1 and `phase-3-tts-decision.md:113-118, 143`, GuideAnts bakes a
  curated set of short reference WAVs (CC0 Common Voice / GLOBE preferred; VCTK /
  LibriTTS-R with a NOTICE) into the image. These are **local assets** selected by preset
  id (§5.5) — they are entirely outside the download/allowlist machinery.

So there are two distinct flows the UI must keep separate:

1. **"Download the Chatterbox model"** → manifest entry `chatterbox` → allowlisted repo
   `ResembleAI/chatterbox` → `/tts/admin/models/download` (constrained, §5).
2. **"Pick a voice"** → a preset id resolving to a bundled reference WAV → no download,
   no HF (§5.5). This replaces the Kokoro voice enum.

### 3.5 Embeddings authoritative set (llama.cpp / GGUF) — the actual dimension model

Embeddings do **not** use the audio.cpp intersection (§3.1). Their engine is
`llama-server --embeddings` (Phase 1, `phase-1-embeddings-llama-server.md:9-12,71`), so the
correctness rule is different — and the dimension story is **not** "must be 1024". Here is
what the code actually does, end to end:

- **Storage is `vector(1536)`.** `DocumentChunk.Embedding` maps to SQL Server
  `vector(1536)` (`DocumentChunk.cs:51-53`).
- **Search is 1536 on both sides.** `HybridSearcher` declares `@queryVec vector(1536)` and
  runs `vector_distance('cosine', Embedding, @queryVec)` (`HybridSearcher.cs:57,82,87`).
- **Every provider is normalized to 1536 at the router.**
  `ProviderRoutedEmbeddingService.cs:69` calls `EmbeddingVectorDimensions.NormalizeBatchToTarget`
  for Azure/Local/Gemini/HF/OpenRouter/OpenAI alike. `EmbeddingVectorDimensions.Target = 1536`
  (`EmbeddingVectorDimensions.cs:5`); `NormalizeToTarget` (`:7-18`) **zero-pads when source
  < 1536 and SILENTLY TRUNCATES when source > 1536** (`Array.Copy` with
  `Math.Min(source.Length, Target)`).
- **1024 is not a contract — it is one const.** `LocalEmbeddingService.SourceVectorDimensions
  = 1024` (`LocalEmbeddingService.cs:15`) is the **local provider's expectation of *today's*
  model** (Harrier 0.6B). It rejects `dimensions != 1024` (`:98-102`), rejects vector length
  `!= 1024` (`:108-112`), then pads 1024→1536 (`:114`). Change the local model and you change
  this const **as a matched pair** with the `/emb/embed` server.

> **What is actually invisible to clients:** the **1536** DB column + search vector, reached
> by normalization. `dimensions: 1024` on the `/emb/embed` wire is an internal detail of the
> local provider, **not** the consumer contract. Choosing a different local embedding model
> is therefore **not a wire/client break**: you update the `/emb/embed` server *and*
> `LocalEmbeddingService.SourceVectorDimensions` together; the `/emb/embed` `dimensions`
> value may change, but .NET consumers still see 1536 after normalization.

> **Correctness rule for an `emb` manifest entry (honours "no arbitrary fallback"):** an
> entry is offered **only if** (a) it is a **single-file GGUF** on an allowlisted repo,
> **and** (b) it loads in the flavor's shipped `llama-server --embeddings --pooling last`,
> **and** (c) its `producedDimension <= 1536`, **and** (d) the local emb server +
> `LocalEmbeddingService.SourceVectorDimensions` are set to match that `producedDimension`.
> A GGUF that downloads but does not load is the **embeddings equivalent of the
> downloadable≠loadable trap**. A GGUF whose `producedDimension > 1536` must be **rejected
> loudly** — otherwise `NormalizeToTarget` silently truncates it (`EmbeddingVectorDimensions.cs:15`),
> losing dimensions with no error (the banned silent fallback). Because no GGUF can be
> *proven* loadable without loading it once, the table below marks every candidate **"to
> validate in Phase 1"** and the load-after-download check (§7) promotes it to "known good".

**The real blocker to a model swap is not the dimension — it is embedding-space
incompatibility.** Cosine similarity across two different models' vector spaces is
meaningless, so **any** model change (even to a *same-dimension* different model) requires a
**full corpus re-embed** (the existing D3 decision / `RebuildEmbeddings` path). Treat the
dimension as secondary; the re-embed is the actual cost. The new curated default
`Qwen3-Embedding-0.6B` (native 1024, same width as the incumbent) is still a **model change**
from Harrier, so the Phase 1 cutover pays this one-time re-embed — 1024 is not sacred, it just
happens to match.

> **Latent silent-normalization to be aware of.** `NormalizeToTarget` pads/truncates
> **silently** for *any* source length (`EmbeddingVectorDimensions.cs:7-18`). Today only the
> local provider is pinned (via `SourceVectorDimensions == 1024`), so this is dormant. Once
> the local model becomes catalog-configurable, this is exactly why the catalog must reject
> a > 1536 model loudly instead of leaning on truncation.

#### `emb` catalog — model → arch → source(s) → file → producedDimension → offer?

> **LOCKED — curated set (D9 emb sub-decision, RESOLVED 2026-07-02).** The catalog ships a
> **small, vetted set** (not free browse): **default = `Qwen3-Embedding-0.6B`**, plus two
> non-default alternatives (`EmbeddingGemma-300M`, `bge-m3`), each still **to validate in
> Phase 1** via a retrieval-parity harness on a representative GuideAnts corpus (benchmarks
> are cross-source/directional — do not claim proven quality). The incumbent Harrier is kept
> as **historical context only** (was the default), not re-blessed. Selection criteria that
> mattered: retrieval quality, native dim ≤ 1536 (+ MRL), small footprint for the co-located
> multi-flavor image, GGUF provenance (**prefer official GGUF**; pin revision + SHA256), and
> multilingual coverage (**treated as a required/default-safe capability** — the default is
> multilingual; English-only models may be listed but are tagged and never default).

> **License is NOT a curation filter.** The end user downloads the model from its source, so
> **license compliance is the user's responsibility**, not ours — we surface license info for
> **transparency only** and never disqualify a model on license grounds. (Contrast the **voice
> pack (D10)**: there GuideAnts *redistributes* the clips in-image, so license compliance IS
> ours — the difference is **redistribution vs. user-initiated download**.) We also do **not**
> convert-and-ship a GuideAnts-owned GGUF artifact: the catalog **points at the model source**
> and the user pulls it (prefer official GGUF where one exists; otherwise a pinned trusted
> community GGUF by repo + revision + SHA256 — that is a *provenance* note, not a license
> issue).

**Verification is a build/ship-time inclusion gate, not a runtime state:** each catalog entry
is verified once during Phase 1 — it (a) loads on our `llama-server --embeddings` build and
(b) emits its recorded `producedDimension` (≤ 1536) — and only then is it listed; the user
later downloads that vetted entry. The picker shows exactly the published entries, with no
"unverified" runtime state (§7). Repo ids and dimensions below are from the linked HF model
cards.

| Manifest entry (id) | Role | Arch / pooling | GGUF source (pinned rev + SHA256) | File (single `.gguf`) | `producedDimension` | Multilingual | Prompt format |
|---|---|---|---|---|---|---|---|
| `qwen3_embedding_0_6b` | **DEFAULT** | Qwen3 / last-token | **Official GGUF** `Qwen/Qwen3-Embedding-0.6B-GGUF` (q8_0 / f16) | e.g. `Qwen3-Embedding-0.6B-Q8_0.gguf` (prefer F16/Q8_0 for retrieval parity, `phase-1-…:120-122`) | **1024** native (MRL 32–1024 → **pin output ≤ 1536**) | **Yes** (100+) | Instruction-aware: **`Instruct:` query prefix** (replaces Harrier's `web_search_query`, `emb_service.py:394,399`; re-validate) |
| `embedding_gemma_300m` | alt (footprint-first) | Gemma-embedding / mean | **Official GGUF** `ggml-org/embeddinggemma-300m-GGUF` | e.g. `embeddinggemma-300m-Q8_0.gguf` | **768** (MRL 512/256/128) | **Yes** (100+) | Needs **task prompt templates** (query/document) |
| `bge_m3` | alt (no-prefix multilingual) | XLM-RoBERTa / mean | **No official GGUF** → user pulls a **pinned trusted GGUF** (repo + revision + SHA256); provenance note | e.g. `bge-m3-Q8_0.gguf` | **1024** | **Yes** (100+) | **None** for dense (simplest facade) |

The default replaces Harrier: it **reuses our `llama-server --embeddings --pooling last` path**
(same Qwen3 arch as the incumbent → **lowest-risk swap**), but cutting over is still a **model
change** requiring a **one-time full corpus re-embed (D3)** — the real cost — plus the facade
query-prefix change to the Qwen `Instruct:` form. Each alternative is likewise a model change
(matched-pair `SourceVectorDimensions` update + re-embed) if later selected.

**On the dimension rule:** every listed entry is native (or MRL-pinned) ≤ 1536, so none hits
the truncation ceiling. A smaller dim (e.g. EmbeddingGemma's 768) leaves more of the 1536-wide
column zero-padded (lower storage utilization) — a note, not a blocker.

**Must be REJECTED LOUDLY (`producedDimension > 1536` → silent truncation, data loss):**
`microsoft/harrier-oss-v1-27b` (**5376**), `Qwen/Qwen3-Embedding-4B` (**2560**),
`Qwen/Qwen3-Embedding-8B` (**4096**). These load fine but exceed the 1536 ceiling —
`NormalizeToTarget` would truncate them silently (`EmbeddingVectorDimensions.cs:15`),
corrupting retrieval with no error. Reject at the catalog; do **not** rely on truncation.

GuideAnts ships embeddings = `harrier_oss_v1_0_6b` **today** (matches
`GA_EMB_DEFAULT_MODEL_PATH=harrier-oss-v1-0.6b`, `docker/.env:32`; repo
`microsoft/harrier-oss-v1-0.6b`, `download-emb-models.ps1:3`), but the **new curated default
is `Qwen3-Embedding-0.6B`** (locked above) — cutover happens in Phase 1 with a one-time
re-embed (D3). The catalog's job is to replace the free-text HF snapshot browse (§4.5) with a
dropdown of the **curated set** (default + alternatives) — published GGUF entries only.

---

## 4. Current state (what exists today) — free-form, unconstrained

### 4.1 ASR: free-form Hugging Face id (unconstrained)

- Client: `AsrModelManager.tsx` "Add model" opens `DownloadModelDialog`, which browses an
  arbitrary HF repo (placeholder `openai/whisper-large-v3`,
  `AsrModelManager.tsx:654,678`) and posts `{ model_id }` to the download endpoint
  (`:217-224`). The only guard is a preview-browse of the repo listing (`:604-622`) —
  that verifies the repo *exists/is readable*, not that it is an audio.cpp-supported
  model.
- .NET: `SettingsServiceLocalModelsEndpoints.MapPost(".../local-models/downloads")`
  (`SettingsServiceLocalModelsEndpoints.cs:61-97`) validates via
  `ServiceLocalModelDownloadValidator.ValidateDownloadPayload`, which for ASR/TTS only
  requires **`model_id` present** (`ServiceLocalModelDownloadValidator.cs:14-18`). No
  allowlist. It stamps the server-resolved HF token
  (`SettingsServiceLocalModelsEndpoints.cs:85-93`,
  `LocalServiceAdminRouting.BuildForwardedBodyWithHfToken`) and proxies to
  `{host}/asr/admin/models/download`.
- Python engine: `asr_service.py` `admin_download_model` accepts any `model_id`
  (`asr_service.py:648-651`) and calls `snapshot_download(repo_id=request.model_id, …)`
  (`:317-324`). **Any HF repo id is accepted and downloaded** — the definition of
  free-form.

### 4.1b Embeddings: free-form Hugging Face snapshot browse (unconstrained)

Today the embeddings picker is **as free-form as ASR** — and identically unconstrained on
the server:

- Client: `EmbRuntimeManager.tsx` "Add model" opens a local `DownloadModelDialog`
  (`EmbRuntimeManager.tsx:466-470,590-717`) that browses an **arbitrary HF repo** via
  `RepositoryFilePicker` (placeholder `microsoft/harrier-oss-v1-0.6b`,
  `:668-691`) and posts `{ model_id }` (the browsed repo) to the download endpoint
  (`startDownload`, `:228-235`). The only guard is a preview-browse that verifies the repo
  *exists/is readable* (`:617-631`), **not** that it is a llama.cpp-loadable GGUF embedder
  of the right dimension.
- .NET: the **same** `SettingsServiceLocalModelsEndpoints` `.../local-models/downloads`
  proxy (`SettingsServiceLocalModelsEndpoints.cs:61-97`) and the **same**
  `ServiceLocalModelDownloadValidator.ValidateDownloadPayload`, which for a non-ImageGeneration
  service only requires **`model_id` present** (`ServiceLocalModelDownloadValidator.cs:14-18`).
  No allowlist, no format check. HF token stamped as usual (`:85-93`).
- Python engine: `emb_service.py` `admin_download_model` accepts any non-empty `model_id`
  (`emb_service.py:771-776`) and runs `snapshot_download(repo_id=request.model_id, …)`
  (`:227-237`) into a leaf-named folder (`canonical_model_folder_name`, `:179-193`).
  **Any HF repo id is accepted and the whole snapshot is pulled** — the same free-form trap
  as ASR, and it pulls **safetensors** (the torch path), not the GGUF Phase 1 needs.

So embeddings today has the ASR problem *plus* a format mismatch with its Phase 1 engine:
the picker can fetch a safetensors repo that `llama-server --embeddings` cannot load, or a
GGUF of the wrong dimension that .NET will reject at `/emb/embed`.

### 4.2 TTS: pinned to a single hardcoded id today (Kokoro), voice = enum

- Client: `TtsModelManager.tsx` `DownloadModelDialog` is **hard-locked** to
  `hexgrad/Kokoro-82M` (`KOKORO_MODEL_ID`, `:63,605,678,694-696`; repo input is
  `repoInputReadOnly`). So TTS model selection is not free-form today — it is a single
  pinned model. But the enforcement is **client-side only**: the same server validator
  (§4.1) would accept any `model_id`, and `tts_service.py`'s download endpoint mirrors
  ASR's free-form `snapshot_download`.
- Voice selection: `ServiceEditorMetadataProvider.cs` `KokoroVoiceNames`
  (`:14-70`) — a hardcoded enum of 50+ Kokoro voice ids — is rendered as the `VoiceName`
  enum field for `SpeechSynthesisLocalTtsHttp` (`:224-228`). This is the list D1 replaces
  with voice-pack ids.

### 4.3 SD: the closest existing "structured" pattern (reuse, don't conflate)

`ServiceLocalModelDownloadValidator.ValidateImageGenerationBundle`
(`ServiceLocalModelDownloadValidator.cs:22-68`) is strict: each role is a
`(repo, single filename)` pair, all required, and glob metacharacters (`*`, `?`) are
**rejected** so a bundle download "cannot accidentally pull a whole multi-quantization
repo" (`:24-28,50-65`). This is the shape to generalise: **name exactly what to fetch,
reject anything wider.** But SD is still *free-form repos* — it validates *structure*, not
an *allowlist*. The D9 design adds the allowlist on top.

### 4.4 Summary of today's constraint level

| Service | Model selection today | Enforcement | Constraint |
|---|---|---|---|
| Embeddings | Browse+download **any** HF snapshot | `model_id` present only | **Free-form (unconstrained)** — and pulls safetensors, not the Phase 1 GGUF |
| ASR | Browse+download **any** HF repo | `model_id` present only | **Free-form (unconstrained)** |
| TTS | Client-pinned to `hexgrad/Kokoro-82M`; voice = fixed enum | `model_id` present only (server accepts anything) | Client-pinned model; server unconstrained |
| SD | Structured `(repo, file)` per role, no globs | Strict per-field validator | Structured but still arbitrary repos |

---

## 5. Target design — constrain selection to the known set; downloads from valid sources only

### 5.1 Source of truth: a curated catalog manifest shipped with GuideAnts (D9)

Ship a **curated model catalog manifest** (JSON) with GuideAnts. The `asr`/`tts` entries
are **derived from** audio.cpp's `model_manager.py` + `registry.cpp` + `README.md` (per
the intersection rule §3.1); the `emb` entries are **derived from** the llama.cpp
GGUF + `producedDimension <= 1536` rule (§3.5). It is a **curated, versioned artifact** — not a live import of
`model_manager.py`, not a live HF search, and not free-form HF ids.

Proposed per-entry shape (one schema, three tasks — embeddings adds a few fields):

```jsonc
{
  "schemaVersion": 2,
  "sourceAudioCppRef": "<audio.cpp git ref the asr/tts entries were derived from>",
  "models": [
    {
      "id": "qwen3_asr_0_6b",            // stable manifest id (== model_manager package id)
      "family": "qwen3_asr",             // registry.cpp family; must be a registered loader
      "task": "asr",                     // "emb" | "asr" | "tts" — selects which picker shows it
      "displayName": "Qwen3 ASR 0.6B",
      "targetDirectory": "Qwen3-ASR-0.6B", // on-disk dir the engine loads from
      "sourceRepos": ["Qwen/Qwen3-ASR-0.6B"], // ALLOWLIST: exact repo id(s), no globs
      "requiredFiles": ["config.json", "model.safetensors", "..."],
      "layout": "hf_snapshot",           // hf_snapshot | composite_snapshot | converter | gguf_single_file
      "format": "safetensors",           // asr/tts: safetensors/ggml (never gguf, README:653)
      "gated": false,                    // true → requires HF token (e.g. pocket_tts)
      "languages": ["zh","en","..."],    // from README table
      "releaseStatus": "released",        // asr/tts: must be "released"
      "notes": ""
    },
    {
      // Curated DEFAULT emb entry (D9 LOCKED). Alternatives (embedding_gemma_300m, bge_m3)
      // follow the same shape; exactly one entry is isDefault:true.
      "id": "qwen3_embedding_0_6b",      // stable manifest id
      "task": "emb",                     // shown ONLY in the embeddings picker
      "arch": "qwen3",                   // documentation only; llama.cpp autodetects
      "displayName": "Qwen3-Embedding 0.6B (GGUF)",
      "targetDirectory": "qwen3-embedding-0.6b", // on-disk dir + GA_EMB_DEFAULT_MODEL_PATH leaf
      "isDefault": true,                 // exactly one emb entry is the default
      "sourceRepos": ["Qwen/Qwen3-Embedding-0.6B-GGUF"], // ALLOWLIST: official GGUF (prefer official; else a pinned trusted community GGUF)
      "ggufFile": "Qwen3-Embedding-0.6B-Q8_0.gguf", // exactly ONE .gguf file, no globs
      "revision": "<pinned commit sha>", // pin the source revision
      "sha256": "<published checksum>",  // verify the single file after download
      "layout": "gguf_single_file",
      "format": "gguf",                  // emb: MUST be gguf (llama-server --embeddings)
      "pooling": "last",                 // llama-server --pooling last
      "producedDimension": 1024,         // native (MRL 32–1024); REQUIRE <= 1536 (DB/search width).
                                         //   set LocalEmbeddingService.SourceVectorDimensions to match (matched pair)
      "queryInstruction": "instruct",    // Qwen `Instruct:` query prefix (replaces web_search_query; re-validate)
      "gated": false,
      "languages": ["multilingual"],     // 100+; multilingual is a default-safe requirement
      "license": "Apache-2.0",           // TRANSPARENCY ONLY — not a curation filter (user downloads from source)
      "notes": "Curated default (D9). Cutover from Harrier = model change → corpus re-embed (D3). See §3.5."
    }
  ]
}
```

> **No `loadVerified` runtime field.** Verification is a **build/ship-time inclusion gate**
> (§7), not a per-entry runtime flag: every published entry is verified **by construction**,
> so the manifest is the verified set and the client never branches on a verification flag.
> (If a build-time assertion needs a name, it is "published ⇒ verified"; it is not emitted as
> a client-consumed field.)

- **Where it lives:** co-located with `ga-admin` in the image (Phase 4), because that is
  where the download/enforcement code lives and where the audio.cpp repo/file mapping is
  reimplemented (or the `model_manager.py --json` payload is vendored). **Before Phase 4:**
  the **Phase 1 emb-service admin skeleton** serves the `emb` entries and the Phase 2/3
  `ga-audio-server` admin skeleton serves the `asr`/`tts` entries. Served over HTTP so the
  settings UI reads it live rather than duplicating it in the client bundle.
- **Embeddings entries (`task: emb`)** are single-file GGUF (`layout: gguf_single_file`,
  `format: gguf`) and carry the extra `ggufFile`, `pooling`, and `producedDimension` fields
  (plus `sha256`/`revision` pinning the source, and an optional `license` string surfaced for
  **transparency only**). The catalog points at the model **source** and the user downloads it
  (prefer official GGUF; else a pinned trusted community GGUF) — GuideAnts does not
  convert-and-ship its own artifact.
  `producedDimension` is the model's **native** output width; the manifest rule is
  `producedDimension <= 1536` (the DB/search ceiling, §3.5), **not** `== 1024`. An entry with
  `producedDimension > 1536` is a bug (it would be silently truncated) and must be rejected.
  Whatever `producedDimension` a chosen entry declares,
  `LocalEmbeddingService.SourceVectorDimensions` (`LocalEmbeddingService.cs:15`) is set to the
  **same** value as a matched pair with the `/emb/embed` server. **There is no `loadVerified`
  runtime field:** publication into the manifest *is* the verification (§7 inclusion gate), so
  the picker shows exactly the published set and never branches on a verification flag.
- **Composite/converter layouts** (`miotts_1_7b`, `vibevoice_1_5b`, `voxcpm2`, `vevo2`,
  `citrinet_asr`) carry **multiple** `sourceRepos` (or a converter URL) — the allowlist is
  the *union* of the placements' repos (`model_manager.py` `CompositeSnapshotSource`,
  `:96-98,428-469`). To validate in Phase 3/4: whether GuideAnts ships only single-repo
  snapshot entries first (`chatterbox`, `qwen3_*`, `omnivoice`, `pocket_tts`) and adds
  composites later.

### 5.2 Selection UX: constrained dropdown, no free-text

- The embeddings picker shows **only `task: "emb"`** entries; the ASR picker shows **only
  `task: "asr"`** entries; the TTS picker shows **only `task: "tts"`** entries. Each
  becomes a **dropdown of catalog entries** (id + displayName + languages + size hint; for
  `emb`, also the produced dimension and quant), replacing the free-text HF repo browse in
  `EmbRuntimeManager`/`AsrModelManager`/`TtsModelManager` `DownloadModelDialog`.
- **Embeddings specifically:** the `EmbRuntimeManager` `DownloadModelDialog`
  (`EmbRuntimeManager.tsx:590-717`) currently mounts `RepositoryFilePicker` for a free-text
  HF repo (`:668-691`). That is replaced by the catalog dropdown; the free-text repo input
  and the browse-then-download gate are removed. The dropdown lists exactly the **published**
  curated entries (all `producedDimension <= 1536` by the inclusion gate, §7), so a user
  cannot pick a > 1536 embedder that `NormalizeToTarget` would silently truncate. Note this
  is a **model change** either way (re-embed required, D3) unless the chosen entry is the
  currently-active one.
- No free-text model id for embeddings/ASR/TTS. (The llama chat `AddModelWizard` typeahead
  and the SD bundle form keep their own shapes — out of scope.)
- "Add model" = "install a catalog entry": the UI sends the **catalog `id`**, not a raw
  repo id; the control plane resolves id → allowlisted repo + (for `emb`) the single GGUF
  file.
- Gated entries (`pocket_tts`) render a "requires Hugging Face token" affordance; the
  token is still the single server-resolved value stamped by .NET
  (`SettingsServiceLocalModelsEndpoints.cs:85-93`), never entered per-request in the UI.

### 5.3 Download-source validation (honours "no arbitrary fallback")

Download proceeds **only** when the requested catalog `id` resolves to a manifest entry
**and** every repo it pulls from is on that entry's `sourceRepos` allowlist (and every
file is within `requiredFiles` / the entry's declared prefixes). Otherwise the request is
**rejected loudly** with a contract-compatible 400 — never silently coerced to a
"closest" model, never widened to a whole repo, never falling back to a default.

- A request naming a repo id directly (legacy shape) is only accepted if that repo id is
  the allowlisted source of exactly one manifest entry; anything else → reject.
- Glob metacharacters in any file field are rejected, reusing the SD validator's rule
  (`ServiceLocalModelDownloadValidator.cs:50-65`).
- **Format check is per task (§3.2, INVERTED between engines):** for `asr`/`tts`, a GGUF
  source is rejected; for `emb`, a **non-GGUF** source is rejected (a safetensors-only repo
  is the old torch path and does not load under `--embeddings`), and the download must
  resolve to **exactly one** `.gguf` file — reusing llama-admin's single-GGUF resolution
  (`llama_admin_service.py:908-919,934-941`) rather than pulling a whole multi-quant repo.
- **Embeddings dimension gate (honours "no arbitrary fallback"):** an `emb` entry must
  declare `producedDimension <= 1536`. After the file loads, the **actual** produced
  dimension is checked against the declared `producedDimension`; a mismatch is a **loud**
  failure (the model is not activated, the operation is marked failed). A model whose actual
  dimension is **> 1536** is rejected loudly rather than allowed to be silently truncated by
  `EmbeddingVectorDimensions.NormalizeToTarget` (`EmbeddingVectorDimensions.cs:15`) — that
  silent truncation is exactly the banned fallback and would corrupt retrieval. On acceptance,
  `LocalEmbeddingService.SourceVectorDimensions` (`LocalEmbeddingService.cs:15`, currently
  1024) is updated to the entry's `producedDimension` as a **matched pair**, and the corpus
  is re-embedded (D3) because the vector space changed.
- Gated repos: if `gated: true` and no HF token is configured, fail loudly with a "token
  required for `<repo>`" message (do not attempt an anonymous download that 401s opaquely).

### 5.4 Where enforcement lives

- **Authoritative gate: `ga-admin`** (Phase 4, D6). It owns the artifact/download
  subsystem (`phase-4-control-plane-consolidation.md:39-41,120-126`) and validates the
  requested model against the manifest + allowlist **before** invoking the download. It
  reuses/reimplements `model_manager.py`'s repo/file mapping (or vendors its `--json`
  output). `/asr/admin/*` and `/tts/admin/*` public routes are preserved via the nginx
  admin-prefix splits already planned (`phase-4-…:53-68`).
- **Before Phase 4:** the Phase 1 (`emb-service`), Phase 2 (`ga-audio-server` ASR), and
  Phase 3 (TTS) admin skeletons enforce the same manifest check in their
  `/admin/models/download` handler — this is where today's free-form `snapshot_download`
  (`asr_service.py:317-324`; **embeddings**: `emb_service.py:227-237,771-776`) is replaced
  by "resolve catalog id → allowlisted artifact". For embeddings that means "resolve id →
  allowlisted repo + single GGUF file → download → **load-and-check-dimension** before
  marking the operation complete" (the load-after-download step, §7).
- **Defense-in-depth in .NET (optional, not the sole gate):**
  `ServiceLocalModelDownloadValidator` can additionally check the requested id against the
  catalog it fetches from the control plane, so an obviously invalid request is rejected
  before proxying. The **loud** authoritative rejection still lives at the control plane
  so the rule cannot be bypassed by calling the engine directly.

### 5.5 Chatterbox voice-pack management in the UI

- Voices are **local bundled assets**, selected by preset id — **not** HF downloads and
  **not** catalog entries. They are presented in the **service editor** (provider
  settings), not in the model-download manager.
- Replace `ServiceEditorMetadataProvider.cs` `KokoroVoiceNames` (`:14-70`) with the
  **voice-pack preset ids** for the `VoiceName` enum on `SpeechSynthesisLocalTtsHttp`
  (`:224-228`). The client already renders that enum as a dropdown (`type: "enum"`,
  `enumOptions`), so no client rendering change is needed — only the option set changes.
- Stored `VoiceName` values (e.g. `af_heart`) are **not migrated** — no backwards
  compatibility (product decision). An unknown/legacy id is rejected loudly and the user
  reselects (`phase-3-tts-decision.md:147,224`); no mapping table, no reverse map.
- The `chatterbox` model download (§5.1) and the voice-pack selection are independent: a
  user downloads the model once (constrained), then picks any pack voice (local).

### 5.6 Contract / UX changes (enumerated)

Public nginx route shapes stay stable (Part B invariant). New endpoints live under
existing prefixes.

| Layer | Change | New or modified |
|---|---|---|
| Control plane (`ga-admin`, P4; engine skeletons P1/P2/P3) | Serve the catalog: `GET /emb/admin/catalog`, `GET /asr/admin/catalog`, `GET /tts/admin/catalog` (under existing `/emb/`, `/asr/`, `/tts/` prefixes). Enforce manifest+allowlist in `POST /{svc}/admin/models/download`; for `emb`, also enforce single-GGUF + `producedDimension <= 1536` on load. | New endpoint; modified download handler |
| .NET settings | New proxy `GET /{serviceId}/local-models/catalog` alongside the existing local-models endpoints (`SettingsServiceLocalModelsEndpoints.cs`); download validator additionally checks the fetched catalog. **Also applies to `serviceId=Embeddings`.** | New endpoint under existing settings group |
| .NET metadata | `ServiceEditorMetadataProvider.KokoroVoiceNames` → voice-pack preset ids for TTS `VoiceName` (`:227`). (This is the D1-approved contract change.) Embeddings has **no** metadata-enum change. | Modified (TTS only) |
| Client — Embeddings | `EmbRuntimeManager` `DownloadModelDialog` free-text HF browse (`EmbRuntimeManager.tsx:590-717`, `RepositoryFilePicker` at `:668-691`) → catalog dropdown (task=emb, `producedDimension <= 1536`). | Modified |
| Client — ASR | `AsrModelManager` `DownloadModelDialog` free-text HF browse → catalog dropdown (task=asr). | Modified |
| Client — TTS | `TtsModelManager` `DownloadModelDialog` Kokoro-pinned browse → catalog dropdown (task=tts); voice dropdown re-sourced from pack ids. | Modified |
| Data (service modes) | **None** — no `VoiceName` migration (no backwards compat); legacy ids rejected loudly, user reselects. **Embeddings: no service-mode change and no consumer-visible contract change** — the `/emb/embed` route shape is stable; its `dimensions` value equals the active model's `producedDimension` (1024 today) and .NET normalizes to **1536** for all consumers. | — |
| Code (matched pair, if the default model changes) | If a future default `emb` entry has a different `producedDimension`, `LocalEmbeddingService.SourceVectorDimensions` (`LocalEmbeddingService.cs:15`) is updated to match — a **server-side .NET const**, not a client/wire change. Requires a corpus re-embed (D3). | Modified only on model change |

`LocalEmbeddingService.cs` / `SpeechTranscriptionService.cs` / `SpeechSynthesisService.cs`
inference **route shapes** are **unchanged** (embed → `{ data:[{embedding}], dimensions,
modelRef }`; transcribe multipart; synthesize WAV+duration). `LocalAiStartupWarmupService`
load/unload/ready flow is unchanged. **The embeddings changes are confined to model
*selection/download* (plus the `SourceVectorDimensions` matched-pair const on a model
change); the `/emb/embed` route shape and the normalized 1536-wide vector consumers
actually store/search remain invisible to clients.**

---

## 6. Risks

- **Manifest drift vs audio.cpp.** The manifest is a curated snapshot of `model_manager.py`
  and `registry.cpp`; if audio.cpp adds/removes a family the manifest goes stale. Mitigation:
  record `sourceAudioCppRef`; regenerate from `model_manager.py --json` intersected with
  `registry.cpp` on each audio.cpp bump; the flavor-build gate rebuilds audio.cpp anyway.
- **Downloadable≠loadable trap.** `model_manager.py` lists families the release engine
  cannot load (§3.3). Copying the downloader catalog verbatim would offer broken choices —
  the intersection rule (§3.1) is the guard; **to validate in Phase 2/3** by loading each
  offered entry once.
- **Composite/converter entries** (`miotts_1_7b`, `vibevoice_1_5b`, `voxcpm2`, `vevo2`,
  `citrinet_asr`) pull from multiple repos or run a post-process/convert step
  (`model_manager.py:1195-1201,1296-1313`, `:1847-1888`). The allowlist must cover **all**
  placement repos; enforcement + post-process parity is **to validate in Phase 3/4**.
- **Gated `pocket_tts`** needs an HF token and downloads only English + `alba`
  (`README.md:430-431`). Surfacing it without a token yields a 401 — handle explicitly
  (§5.3), do not offer it as if ungated.
- **GGUF confusion.** Users may paste a GGUF repo into the ASR/TTS picker (embeddings/llama
  habits). The per-task `format` check rejects it loudly; message must point them to the
  correct service. **Symmetric embeddings risk:** a safetensors embedder repo pasted into
  the old emb browse would download but not load under `--embeddings` — the `emb` format
  check rejects non-GGUF loudly.
- **VC vs TTS.** `seed_vc` is voice conversion, not TTS — excluding it is a manifest
  `task` decision; do not let it leak into the TTS picker.

**Embeddings-specific risks:**

- **Corpus re-embed is the real cost of any model swap (the load-bearing embeddings risk).**
  Cosine distance across two models' vector spaces is meaningless, so switching the local
  embedder — **even to a same-dimension different model** — requires a full re-embed of the
  stored corpus (`DocumentChunk.Embedding`, searched by `HybridSearcher.cs:82,87`) via the
  D3 / `RebuildEmbeddings` path. The dimension is secondary; the re-embed is the actual
  blocker and must be scheduled at cutover. Mixed-provenance vectors in one index degrade
  retrieval silently.
- **Silent truncation for > 1536 (banned fallback).** `EmbeddingVectorDimensions.NormalizeToTarget`
  (`EmbeddingVectorDimensions.cs:7-18`) pads or **silently truncates** to `Target = 1536`
  (`Math.Min(source.Length, Target)`). An `emb` entry whose `producedDimension > 1536`
  (Harrier 27b @5376, Qwen3-Embedding 4B @2560, 8B @4096) would be truncated with no error —
  data loss. Mitigation: the manifest `producedDimension <= 1536` gate (§5.3) rejects such
  models **loudly**; ≤ 1536 models (Harrier 270m @640, Qwen3-Embedding-0.6B @≤1024) are
  swappable but still need the matched-pair `SourceVectorDimensions` update + a re-embed.
  This truncation is a **latent silent-normalization** to keep in mind once the local model
  is catalog-configurable.
- **GGUF source provenance (LOCKED set, §3.5).** The curated set is **default
  `Qwen3-Embedding-0.6B` + alternatives `EmbeddingGemma-300M`, `bge-m3`**. Provenance rule:
  prefer an **official GGUF** (Qwen3-Embedding and EmbeddingGemma both ship one), else the user
  pulls a **pinned trusted community GGUF** by repo + revision + SHA256 (bge-m3 has no official
  GGUF). Unpinned/floating community uploads are a supply-chain + reproducibility risk and are
  not allowlisted. **This is a provenance concern, not a license one — see next.**
- **License is NOT a curation filter (by decision).** Because the **user** downloads the model
  from its source, **license compliance is the user's responsibility**; we surface a `license`
  string for **transparency only** and never disqualify an entry on license grounds (e.g. a
  CC-BY-NC model like Jina-v3 is *listable — the user's call*, not "disqualified"). This is the
  **opposite** of the **voice pack (D10)**, where GuideAnts *redistributes* the assets in-image
  and therefore owns license compliance. The distinction is **redistribution vs. user-initiated
  download**.
- **Downloadable≠loadable for GGUF.** A GGUF can download yet fail to load on a given
  flavor's `llama-server` build, or load but emit a different dimension. This is the
  embeddings twin of the audio.cpp trap; the Phase 1 **load-after-download inclusion gate**
  (§7) is the guard — a candidate is only published into the manifest after it loads and
  emits its recorded dimension, so every shipped entry is verified by construction.
- **Query-prefix / MRL semantics differ per model.** Harrier uses the sentence-transformers
  `web_search_query` prompt (`emb_service.py:394,399`); Qwen3-Embedding uses an
  `Instruct: …\nQuery: …` format and supports MRL output dims 32–1024. Switching models is
  **not** a silent drop-in: the facade's query-prefix string and retrieval parity must be
  re-validated (`phase-1-…:59-65,128-130`) and the corpus re-embedded (D3). Document per
  entry; do not offer a second model as if it were vector-compatible with the first.

## 7. Validation

1. **Manifest correctness:**
   - *ASR/TTS:* for every offered entry, assert (a) `model_manager.py` has a matching
     package id + repo(s), (b) `registry.cpp` registers the family, (c) `README.md` marks
     it `released`. Fail the build if any offered entry violates the intersection.
   - *Embeddings:* for every `emb` entry, assert `format == "gguf"`, `layout ==
     "gguf_single_file"`, exactly one `ggufFile`, and `producedDimension <= 1536`. Fail the
     build if any `emb` entry declares `producedDimension > 1536` (would be silently
     truncated), and assert the active entry's `producedDimension` equals
     `LocalEmbeddingService.SourceVectorDimensions` (matched pair).
2. **Download allowlist:** a download for a manifest id fetches only its allowlisted
   repo(s)/file(s); a request for a non-manifest repo (e.g. `openai/whisper-large-v3` for
   ASR, or an arbitrary embedder repo for emb) is rejected with a loud 400. Per-task format:
   a GGUF repo is rejected for ASR/TTS; a **non-GGUF / multi-file** source is rejected for
   emb. A gated entry without a token fails with a token-required message.
3. **Load-after-download:**
   - *ASR/TTS:* each offered entry, once downloaded, actually loads in `ga-audio-server`
     (closes the downloadable≠loadable gap).
   - *Embeddings (inclusion gate — a precondition for publishing, not a runtime flag):* a
     candidate `emb` model is published into the manifest **only after** it loads on our
     `llama-server --embeddings --pooling last` build **and** its actual produced dimension
     equals the recorded `producedDimension` **and** is `<= 1536`. A > 1536 model is rejected
     loudly (no silent truncation) and simply never ships. The shipped manifest is therefore
     the verified set — there is no runtime "unverified" state and the picker does not branch
     on any verification flag.
   - *Embeddings re-embed:* a model change (any different `emb` entry) triggers a full
     corpus re-embed (D3 / `RebuildEmbeddings`) — validate that stored + query vectors come
     from the same model before trusting retrieval scores.
4. **Voice pack:** the TTS voice dropdown lists only pack ids; a stored legacy id (e.g.
   `af_heart`) is rejected loudly (no migration, no remap) — see Phase 3 validation.
5. **Contract preservation:** public `/emb/`, `/asr/`, `/tts/`, and their `/admin/*` shapes
   unchanged except the new `…/admin/catalog` GET and the D1 voice enum; the `/emb/embed`
   response still reports `dimensions` (= the active model's `producedDimension`) + `modelRef`,
   and .NET consumers still receive **1536**-wide vectors after normalization; golden replay
   per the contract-preservation gate.

## 8. Rollback

Manifest and catalog endpoints are additive; rollback is image-tag rollback (no volume
migration for the catalog — it is shipped code, not state). There is **no `VoiceName` data
migration** to reverse (no backwards compat — product decision), so rollback is fully
stateless on that axis. Downloaded model dirs are unchanged in layout, so a previous image
boots against the same `/models-local/{emb,asr,tts}` content. **Embeddings caveat:** the
catalog constraint is additive to the model dir, but the Phase 1 GGUF vs the previous
safetensors model are different artifacts — keep both on `/models-local/emb` during the
transition window (`phase-1-…:158-163`) so either image boots; and if the corpus was
re-embedded (D3), rolling back the image does **not** roll back vectors.

## 9. Definition of Done

- [ ] Curated catalog manifest (D9) exists. ASR/TTS entries derived from `model_manager.py`
      ∩ `registry.cpp` ∩ `README.md` (with `sourceAudioCppRef`); `emb` entries are
      single-file GGUF with `producedDimension <= 1536`. Only `released`+registered families
      (ASR/TTS) and ≤ 1536-dim GGUF (emb) are offered; > 1536 rejected loudly.
- [ ] **D9 emb curated set is LOCKED (§3.5):** default `Qwen3-Embedding-0.6B` (official GGUF,
      Apache-2.0, native 1024, multilingual) + alternatives `EmbeddingGemma-300M` and `bge-m3`;
      the incumbent Harrier is historical context only. `Qwen3-Embedding-0.6B` replaces Harrier
      as the shipped default at the Phase 1 cutover (one-time corpus re-embed, D3); the facade
      query prefix moves to the Qwen `Instruct:` form and is re-validated. Each entry is still
      **to validate in Phase 1** (retrieval-parity harness on a representative corpus).
- [ ] Embeddings picker offers only the **published** `task: emb` entries, no free-text HF
      browse; `EmbRuntimeManager`'s `RepositoryFilePicker` is replaced by the catalog
      dropdown; every published GGUF passed the Phase 1 **inclusion gate** (loads on
      `llama-server --embeddings`, actual dim == recorded `producedDimension` ≤ 1536) — so the
      manifest is the verified set and the picker branches on **no** verification flag;
      `SourceVectorDimensions` matches the active entry; a model change triggers a re-embed
      (D3); the `/emb/embed` route shape is unchanged and consumers still receive 1536-wide
      vectors.
- [ ] ASR picker offers `qwen3_asr_0_6b` (and, if chosen in Phase 2, `citrinet_asr`); no
      free-text HF id; `parakeet_tdt` and other unloadable entries excluded.
- [ ] TTS picker offers `chatterbox` (D1) from `ResembleAI/chatterbox` only; voice pack ids
      replace `KokoroVoiceNames`; the model-download vs voice-pack boundary is explicit in
      the UI.
- [ ] `ga-admin` (or the P1/P2/P3 engine skeleton pre-consolidation) enforces manifest +
      source allowlist on `/{svc}/admin/models/download`; non-allowlisted / wrong-format
      (GGUF for asr/tts, non-GGUF for emb) / `> 1536` dimension (emb) / gated-without-token
      requests are rejected loudly, never coerced (a ≤ 1536 swap is accepted but triggers the
      `SourceVectorDimensions` matched-pair update + re-embed D3).
- [ ] New `…/admin/catalog` (incl. `/emb/admin/catalog`) + `.NET …/local-models/catalog`
      endpoints live under existing prefixes; public route shapes unchanged;
      contract-preservation gate green.
- [ ] Every offered entry validated to actually load (downloadable≠loadable gap closed) —
      including the emb dimension assertion (actual == declared `producedDimension` ≤ 1536).

## 10. Gates cross-links

- [`contract-preservation-gate.md`](./contract-preservation-gate.md): the catalog endpoint
  and constrained download must not alter existing `/emb/`, `/asr/`, `/tts/`, `/*/admin/*`
  shapes; the `/emb/embed` route shape + the normalized **1536**-wide consumer vector are
  asserted every phase (its `dimensions` field = the active model's `producedDimension`); the
  D1 voice-enum change is the one approved TTS contract delta and must round-trip reversibly
  (already tracked in `STATUS.md` "After Phase 3"). Embeddings adds **no** approved contract
  delta — selection/download only.
- [`flavor-build-gate.md`](./flavor-build-gate.md): the manifest is flavor-agnostic, but
  heavier candidate families (`voxcpm2` 2B, `vibevoice` 1.5B, `vevo2`) have real
  CPU-flavor latency cost (`README.md:600-628`) — if a family is too slow on the `cpu`
  flavor, document it, do not silently degrade. For embeddings, the load-after-download
  check must pass on **each** flavor's shipped `llama-server` build (`phase-1-…:107-112`).
- [`torch-removal-gate.md`](./torch-removal-gate.md): unaffected by the constraint machinery
  itself. (Note: `model_manager.py` imports torch, `model_manager.py:23-24`, but it runs on
  the audio.cpp side; GuideAnts reimplements/vendors only the repo/file **mapping**, so no
  torch dependency is introduced into `ga-admin`.) The embeddings *engine* move to GGUF is
  what drops `sentence-transformers` (Phase 1, Tier A) — this doc only constrains which GGUF
  is offered.
- [`codeql-gate.md`](./codeql-gate.md): the new `ga-admin` download-validation code
  (`python`) and any `.NET` catalog endpoint are scanned in the end-only run per the normal
  language-matrix derivation.
