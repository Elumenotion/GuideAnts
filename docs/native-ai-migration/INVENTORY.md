# Product inventory

**Authoritative list of models GuideAnts ships.** Every row must appear in the runtime catalog manifest for its service, with full metadata, and must pass the runtime matrix in [STATE.md](./STATE.md).

Discovery source: audio.cpp `model_manager.py` ∩ release `registry.cpp` ∩ README `released`. Historical derivation: `_archive/v1/model-catalog-and-downloads.md`.

**Partial manifests are defects.** As of 2026-07-06: TTS manifest ships **5** fully-supported entries (see below); **7** additional families are documented under [Deferred TTS](#deferred-tts-not-in-catalog) until runtime gaps close. ASR manifest has 2/2.

---

## Inclusion rules

An entry is listed here only if:

1. Download source is documented in audio.cpp model manager (or equivalent for emb GGUF)
2. Loader is **registered** in release `registry.cpp` (not commented out)
3. README status is **`released`** (not `integration` / `testing` only)

**Never offer:** Kokoro (`kokoro_tts` — loader not in release tree), `parakeet_tdt` (downloadable but loader commented out), voice-conversion-only families (`seed_vc`), downloader-only packages (`moss_tts`, `heartmula`, etc.).

---

## ASR — 2 entries

### `qwen3_asr_0_6b` (default)

| Field | Value |
|-------|-------|
| family | `qwen3_asr` |
| displayName | Qwen3 ASR 0.6B |
| source | `Qwen/Qwen3-ASR-0.6B` (revision `main`) |
| layout | `hf_snapshot` |
| format | `safetensors` |
| targetDirectory | `Qwen3-ASR-0.6B` |
| gated | false |
| license | Apache-2.0 |
| multilingual | true (zh, en, yue, ar, de, fr, es, pt, + more per audio.cpp README) |

**requiredFiles:** `config.json`, `generation_config.json`, `model.safetensors`, `preprocessor_config.json`, `tokenizer_config.json`, `vocab.json`, `merges.txt`

**UI configuration:** none (no voice/speaker controls). Operator env: `GA_ASR_BACKEND`, weight type via session options.

**Runtime:** `audiocpp_cli --task asr --family qwen3_asr`. Default compose: `GA_ASR_DEFAULT_MODEL_PATH=Qwen3-ASR-0.6B`.

---

### `citrinet_asr`

| Field | Value |
|-------|-------|
| family | `citrinet_asr` |
| displayName | Citrinet 256 (English) |
| source | NGC NeMo archive `stt_en_citrinet_256.nemo` via `api.ngc.nvidia.com` — **converter path**, not plain HF snapshot |
| layout | `converter` |
| format | `safetensors` |
| targetDirectory | `citrinet` |
| gated | false |
| license | (NeMo / NVIDIA terms) |
| multilingual | false (en-only) |

**requiredFiles (post-convert):** `citrinet_256.safetensors`, config/tokenizer artifacts per model_manager

**UI configuration:** none.

**Notes:** Second loadable ASR family in audio.cpp. Manifest must document converter source distinctly from HF snapshot entries.

---

## TTS — 5 shipped entries

Only models with a complete GuideAnts path today: single-repo `hf_snapshot` download, load, voice UI matching `voiceInput`, and synthesize via the existing `{ text, voice?, speed? }` contract. Default catalog id: **`chatterbox`**.

**Shipped criteria:** `layout: hf_snapshot` with one `sourceRepos` entry; all `requiredFiles` present after `snapshot_download`; no composite multi-repo fetch; no post-download conversion; voice control wired in settings UI.

### `chatterbox`

| Field | Value |
|-------|-------|
| family | `chatterbox` |
| voiceInput | **`voice_pack`** |
| source | `ResembleAI/chatterbox` |
| layout | `hf_snapshot` |
| targetDirectory | `chatterbox` |
| gated | false |
| license | MIT |
| languages | 19 (ar, da, de, el, en, es, fi, fr, hi, it, ko, ms, nl, no, pl, pt, sv, sw, tr — no ja/zh) |

**requiredFiles:** `ve.safetensors`, `t3_cfg.safetensors`, `t3_mtl23ls_v2.safetensors`, `t3_mtl23ls_v3.safetensors`, `s3gen.safetensors`, `tokenizer.json`, `grapheme_mtl_merged_expanded_v1.json`, `Cangjie5_TC.json`, `conds.pt`

**UI:** Voice preset picker from `voice-pack/manifest.json` (54 presets today, e.g. `af_alloy`). **No built-in speakers** — every synthesize needs a reference WAV resolved from pack or upload.

**audio.cpp:** `docs/tts.md` — required `--voice-ref`. Built-in voices: not exposed by this integration.

**capabilities:** `nativeSpeed: false`, `deterministic: false`, `requiresReferenceText: false`

---

### `qwen3_tts_0_6b_base`

| Field | Value |
|-------|-------|
| family | `qwen3_tts` |
| voiceInput | **`voice_pack`** |
| source | `Qwen/Qwen3-TTS-12Hz-0.6B-Base` |
| targetDirectory | `Qwen3-TTS-12Hz-0.6B-Base` |

**requiredFiles:** `config.json`, `generation_config.json`, `model.safetensors`, `speech_tokenizer/config.json`, `speech_tokenizer/model.safetensors`, `tokenizer_config.json`, `vocab.json`, `merges.txt`

**audio.cpp:** `docs/qwen3.md` — voice clone; `--voice-ref` required, optional `--reference-text`. Built-in speakers: not exposed.

**UI:** Voice-pack preset picker (reference clip + transcript).

---

### `qwen3_tts_1_7b_base`

Same as 0.6B Base with source `Qwen/Qwen3-TTS-12Hz-1.7B-Base`, targetDirectory `Qwen3-TTS-12Hz-1.7B-Base`, `voiceInput: voice_pack`.

---

### `qwen3_tts_1_7b_voice_design`

| Field | Value |
|-------|-------|
| family | `qwen3_tts` |
| voiceInput | **`instruct`** |
| source | `Qwen/Qwen3-TTS-12Hz-1.7B-VoiceDesign` |
| targetDirectory | `Qwen3-TTS-12Hz-1.7B-VoiceDesign` |

**UI:** Natural-language voice design text field; instruct payload assembled in `tts_service.py`.

---

### `omnivoice`

| Field | Value |
|-------|-------|
| family | `omnivoice` |
| voiceInput | **`voice_pack`** |
| source | `k2-fsa/OmniVoice` |
| targetDirectory | `OmniVoice` |
| languages | 646+ (per README) |

**requiredFiles:** `config.json`, `model.safetensors`, `tokenizer.json`, `audio_tokenizer/config.json`, `audio_tokenizer/model.safetensors`

**audio.cpp:** `docs/tts.md` — clone via `--voice-ref` + optional `--reference-text`; design via `--instruct`. Built-in voices: auto voice exists upstream but **not enumerable** in this integration.

**UI:** Voice-pack preset picker (reference clip + transcript).

---

## Deferred TTS (not in catalog)

Released in audio.cpp but **excluded from the picker** until the blockers below are fixed. Do not add to `manifest.json` until re-qualified.

| id | Blocker |
|----|---------|
| `qwen3_tts_1_7b_custom_voice` | `builtin` speaker ids (`Vivian`, `Ryan`, …) live in model config, not in `/v1/audio/voices`; picker stays empty after load |
| `pocket_tts` | Gated repo; download does not scope to `languages/english/`; `requiredFiles` paths won't verify on a full-repo snapshot |
| `miotts_1_7b` | Composite download (MioTTS + MioCodec + WavLM); `tts_service.py` only fetches `sourceRepos[0]` |
| `voxcpm2` | Post-download `audiovae.pth` → `audiovae.safetensors` conversion not implemented |
| `vibevoice_1_5b`, `vibevoice_7b` | HF repos lack Qwen2.5 tokenizer files (need `model_manager` bundle copy); primary API is multi-speaker `voice_samples`, not single voice-pack preset |
| `vevo2` | Composite layout + `whisper_stats` conversion; multi-route family beyond current synth contract |

---

## voiceInput reference

Derived from `audio.cpp` family docs (`docs/tts.md`, `docs/qwen3.md`, `docs/vevo2.md`). Each catalog row gets exactly one `voiceInput`:

| audio.cpp signal | GuideAnts `voiceInput` |
|------------------|------------------------|
| Required `--voice-ref`; built-in voices not exposed | `voice_pack` |
| Built-in speaker/voice id (`--voice-id`, `--speaker`, or `embeddings/*.safetensors`) | `builtin` |
| Instruction / design text (`--instruct`, voice-design task) | `instruct` |
| Optional `--voice-ref`; can synthesize without reference | `optional_ref` |

| voiceInput | User-facing control | Runtime |
|------------|---------------------|---------|
| `voice_pack` | Preset id from voice-pack manifest | Resolve `clips/{id}.wav` under `GA_TTS_VOICE_PACK_PATH` |
| `builtin` | Speaker id from engine metadata | Family `voice-id` / equivalent |
| `instruct` | Voice design description text | Family instruct API |
| `optional_ref` | Optional reference audio | Clone when present; else default speaker |
| `none` | — | No voice parameter |

.NET synthesis wire: `{ text, voice?, speed? }` only. **No `lang_code` from .NET.**

---

## Embeddings — 3 entries (manifest complete)

| id | displayName | producedDimension | pooling | GGUF source |
|----|-------------|-------------------|---------|---------------|
| `qwen3_embedding_0_6b` | Qwen3-Embedding-0.6B | 1024 | last | `Qwen/Qwen3-Embedding-0.6B-GGUF` → `Qwen3-Embedding-0.6B-Q8_0.gguf` |
| `embedding_gemma_300m` | EmbeddingGemma-300M | 768 | mean | `ggml-org/embeddinggemma-300m-GGUF` |
| `bge_m3` | bge-m3 | 1024 | mean | pinned community `bge-m3-Q8_0.gguf` |

**Rules:** `producedDimension <= 1536`; GGUF only; changing active model requires `SourceVectorDimensions` match + corpus re-embed.

Canonical file: `docker/build/guideants-ai/emb-service/catalog/manifest.json`

---

## Voice pack (not a catalog model)

| Field | Value |
|-------|-------|
| path | `docker/build/guideants-ai/voice-pack/` |
| manifest | `manifest.json` (54 `voiceId` entries as of 2026-07-03) |
| used by | TTS catalog entries with `voiceInput: voice_pack` (`chatterbox`, Qwen3 Base, `omnivoice`) |
| build gate | `scripts/check-voice-pack-attribution.py` |

Voice pack ids (e.g. `af_alloy`) are **not** HF downloads and **not** rows in the TTS model catalog.

---

## Changing this inventory

To add or remove a model:

1. Edit this file with full metadata
2. Update `*-service/catalog/manifest.json`
3. Update [STATE.md](./STATE.md) matrix
4. Implement runtime branch in `*_service.py` if new `family`
5. Implement UI controls for new `voiceInput`
6. Run verify commands in [RULES.md](./RULES.md)

Do not add models by hardcoding in client or .NET without updating this inventory.
