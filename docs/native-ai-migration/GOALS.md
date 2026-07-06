# Goals

This document states **what** we are building and **why**. Implementation order is in [TASKS.md](./TASKS.md). The authoritative model list is in [INVENTORY.md](./INVENTORY.md).

---

## Scope

This migration establishes the **curated-catalog pattern** for all local model settings in GuideAnts:

- **Now:** Embeddings (complete), ASR, TTS — audio.cpp + llama emb facade
- **Later:** Stable Diffusion bundles, local chat (llama GGUF) — same INVENTORY + manifest + catalog-driven UI pattern

Embeddings is included because it is the **finished example** of the pattern we are extending to audio and will extend to SD/chat. Tasks in [TASKS.md](./TASKS.md) focus on audio + shared plumbing unless labeled otherwise.

---

## Problem we solved in discovery

GuideAnts previously let users browse arbitrary Hugging Face repos for ASR/TTS (e.g. Whisper placeholders) while the runtime only supports specific **audio.cpp families**. That mismatch produced downloads that cannot load, hardcoded voice enums that drifted from the baked voice pack, and .NET language maps that duplicated knowledge already in the engine.

Discovery is **finished**. For each ASR/TTS family we ship, we know:

- Canonical download source(s) and required file set
- audio.cpp `family` name registered in release `registry.cpp`
- How voice/configuration works for that family (`voiceInput`)
- Whether the repo is gated, composite, or needs post-processing

The remaining work is **encoding that discovery in manifests and wiring UI + runtime to them** — not re-deciding which models to offer.

---

## Goal 1 — Curated manifests are the product

### What this means

- The only models users can add in settings are rows in [INVENTORY.md](./INVENTORY.md), encoded in service catalog manifests.
- Each manifest entry is self-contained: `id`, `task`, `family`, `displayName`, `sourceRepos`, `requiredFiles`, `layout`, `format`, `gated`, `releaseStatus`, and for TTS `voiceInput` + optional `capabilities`.
- `POST …/admin/models/download` (and the .NET proxy) accepts **only** catalog ids; the engine resolves the allowlisted source — never a free-form repo string from the client.
- A manifest with **fewer entries than the inventory is incomplete**, not a shipping milestone. Today we have 1/2 ASR and 1/11 TTS — that is behind schedule, not “phase complete.”

### Embeddings (same pattern, different engine — complete)

Embeddings use **GGUF single files** for `llama-server --embeddings`, not audio.cpp safetensors. The emb manifest is **complete** (3 entries) and is the template for how SD/chat catalogs should work when added. Rules: `producedDimension <= 1536`, GGUF only, model change requires corpus re-embed. See [INVENTORY.md](./INVENTORY.md#embeddings).

### Voice pack (separate from model catalog)

Chatterbox has **no built-in voices**. Reference WAVs for clone TTS live in `voice-pack/` (baked into the image, not HF downloads). Catalog entries with `voiceInput: "voice_pack"` point settings UI at `voice-pack/manifest.json`. Model download and voice selection are **two distinct flows**.

---

## Goal 2 — UI built from the catalog

### Model picker

- Fetch `GET /api/settings/services/{Embeddings|SpeechTranscription|SpeechSynthesis}/local-models/catalog` (proxies engine `/admin/catalog`).
- `CatalogDownloadModelDialog` populates the dropdown from that response only — **no** `catalogEntries = [...]` in React.
- Label formatting uses `displayName`, `license`, `producedDimension` (emb), `default` flag from manifest.

### Per-model configuration controls

When a TTS model is **active** in provider settings, the UI must show controls appropriate to that entry’s `voiceInput`:

| voiceInput | Settings UI must expose | Data source |
|------------|-------------------------|-------------|
| `voice_pack` | Voice preset dropdown | `GET /tts/admin/voice-pack` → `voice-pack/manifest.json` |
| `builtin` | Speaker / voice id dropdown | Runtime metadata for loaded model (not a static .NET enum) |
| `instruct` | Voice design text field | User text → TTS service |
| `optional_ref` | Optional reference clip + fallback speaker | User upload or preset; family docs in audio.cpp |
| `none` | No voice control | — |

Additional manifest-driven UI:

- `gated: true` → show Hugging Face token requirement before download
- `capabilities` → expose only supported knobs (e.g. speed if `nativeSpeed`)

### What must be removed

- `ServiceEditorMetadataProvider.LocalTtsVoiceNames` (hardcoded 4 legacy ids)
- `SpeechSynthesisService.LocalTtsVoiceLanguageCodes` (hardcoded lang map)
- Free-form HF repository browse in ASR/TTS/emb download dialogs (retired)

---

## Goal 3 — Services use the selection correctly

### Load path

When the user loads a model (or autoload on start), the engine admin handler looks up the catalog entry by id, verifies files on disk, and initializes the audio.cpp / llama stack with the entry’s `family`.

### Inference path

| Service | .NET sends | Engine uses |
|---------|------------|-------------|
| ASR | multipart `audio` | Active ASR `family` (e.g. `qwen3_asr`) |
| TTS | `{ text, voice?, speed? }` | Active TTS `family` + `voiceInput` semantics |
| Emb | text batch | Active GGUF + `producedDimension` + prefix templates |

TTS language (`lang_code`) is **derived in `tts_service.py`** from voice-pack metadata, built-in speaker metadata, or family defaults — not sent from .NET.

### Failure mode

Unknown catalog id, unknown voice id, missing gated token, wrong file layout → **HTTP 4xx with clear error**. No silent fallback to Chatterbox, no legacy Kokoro id remap, no “try anyway.”

---

## Success definition

For **each** inventory row (2 ASR + 11 TTS + 3 emb already done):

1. Entry present in runtime manifest with full metadata
2. Download from allowlisted source succeeds
3. Settings UI shows correct controls when that model is active
4. Load succeeds
5. At least one inference succeeds (transcribe / synthesize / embed)

All five recorded as **pass** in [STATE.md](./STATE.md) with test name or operator command output.

---

## Explicit non-goals (this task track)

- Free-form Hugging Face model browse in settings
- Offering audio.cpp families not in [INVENTORY.md](./INVENTORY.md) (Kokoro, parakeet_tdt, integration-only loaders)
- Marking work complete because the container boots or one model works

**Not out of scope for this doc set:** embeddings (done), or future SD / local chat catalogs — those get new INVENTORY sections when we start them, not a new documentation scheme.

---

## How discovery was validated

Inventory rows are the intersection of:

1. `audio.cpp` `tools/model_manager.py` `CATALOG` (download sources + files)
2. `audio.cpp` `src/framework/runtime/registry.cpp` `make_default_registry` (loaders registered in release build)
3. `audio.cpp` `README.md` model table (`released` status)

Derivation tables and file lists: `_archive/v1/model-catalog-and-downloads.md` (reference only — **INVENTORY.md is authoritative for what we ship**).
