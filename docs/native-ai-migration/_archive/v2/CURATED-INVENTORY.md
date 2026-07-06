# Curated ASR & TTS inventory

**This is the product list.** Discovery is done. Each row is a manifest entry we intend to ship: sources, files, layout, and per-model configuration semantics are already known (see archived [`_archive/v1/model-catalog-and-downloads.md`](./_archive/v1/model-catalog-and-downloads.md) for derivation from audio.cpp `model_manager.py` ∩ `registry.cpp` ∩ README `released`).

Canonical runtime manifests must match this inventory. A manifest with fewer entries than this table is **incomplete**, not a milestone.

Last updated: 2026-07-03

---

## What “curated” means here

1. **We picked the models** — not free-form Hugging Face browse.
2. **We documented how each one configures** — `family`, `voiceInput`, sources, files, gating, capabilities.
3. **Download just works** — user picks a catalog id; the engine downloads from the allowlisted source(s) with the required file set.
4. **UI shows the right knobs for that pick** — voice pack vs built-in speaker vs design prompt vs none; HF token when `gated`; nothing hardcoded in React or .NET parallel lists.
5. **Runtime uses the loaded entry correctly** — load, transcribe, and synthesize branch on the active catalog entry’s `family` and `voiceInput`.

Partial catalogs (today: 1 ASR + 1 TTS in `*-service/catalog/manifest.json`) are **defects** until every row below is in the manifest **and** works end-to-end.

---

## ASR (2 entries)

| id | family | displayName | source(s) | default | voiceInput |
|----|--------|-------------|-----------|---------|------------|
| `qwen3_asr_0_6b` | `qwen3_asr` | Qwen3 ASR 0.6B | `Qwen/Qwen3-ASR-0.6B` | **yes** | — |
| `citrinet_asr` | `citrinet_asr` | Citrinet 256 (en) | NGC NeMo converter → `citrinet_256.safetensors` | no | — |

**Excluded (never offer):** `parakeet_tdt_0_6b_v3` and any family whose loader is commented out of release `registry.cpp` or README status is not `released`.

**Per-model UI config (ASR):** no voice/speaker knobs. Optional runtime knobs (backend, weight type) come from env/operator settings, not per-entry catalog fields today.

---

## TTS (11 entries)

| id | family | displayName | source(s) | voiceInput | UI configuration surface |
|----|--------|-------------|-----------|------------|--------------------------|
| `chatterbox` | `chatterbox` | Chatterbox | `ResembleAI/chatterbox` | `voice_pack` | Voice picker from baked voice-pack manifest (54 presets today) |
| `qwen3_tts_0_6b_base` | `qwen3_tts` | Qwen3 TTS 0.6B Base | `Qwen/Qwen3-TTS-12Hz-0.6B-Base` | `optional_ref` | Reference voice optional; clone when provided |
| `qwen3_tts_1_7b_base` | `qwen3_tts` | Qwen3 TTS 1.7B Base | `Qwen/Qwen3-TTS-12Hz-1.7B-Base` | `optional_ref` | Same as 0.6B Base |
| `qwen3_tts_1_7b_custom_voice` | `qwen3_tts` | Qwen3 TTS 1.7B CustomVoice | `Qwen/Qwen3-TTS-12Hz-1.7B-CustomVoice` | `builtin` | Built-in speaker id picker (from model) |
| `qwen3_tts_1_7b_voice_design` | `qwen3_tts` | Qwen3 TTS 1.7B VoiceDesign | `Qwen/Qwen3-TTS-12Hz-1.7B-VoiceDesign` | `instruct` | Natural-language voice design field |
| `pocket_tts` | `pocket_tts` | PocketTTS | `kyutai/pocket-tts` (**gated**) | `builtin` | Built-in `alba` (+ HF token affordance); optional ref per family docs |
| `omnivoice` | `omnivoice` | OmniVoice | `k2-fsa/OmniVoice` | `builtin` | Built-in speaker ids |
| `miotts_1_7b` | `miotts` | MioTTS 1.7B | composite: `Aratako/MioTTS-1.7B` + MioCodec + WavLM | `builtin` | Built-in speaker ids (en/ja) |
| `voxcpm2` | `voxcpm2` | VoxCPM2 | `OpenBMB/VoxCPM2` (+ post-process audiovae) | `builtin` | Built-in speaker ids |
| `vibevoice_1_5b` | `vibevoice` | VibeVoice 1.5B | `microsoft/VibeVoice-1.5B` (+ bundled tokenizer assets) | `builtin` | Built-in speaker ids (en/zh) |
| `vevo2` | `vevo2` | Vevo2 | `RMSnow/Vevo2` (+ whisper-medium dep) | `builtin` | Built-in speaker ids (en/zh) |

Default TTS entry: `chatterbox`.

**Excluded from TTS picker:** voice conversion (`seed_vc`), Kokoro (`kokoro_tts` — not release-loadable), and any downloader-only / integration-only families listed in the archived catalog §3.3.

---

## voiceInput semantics (drives UI + runtime)

| value | User selects | Runtime receives |
|-------|--------------|------------------|
| `voice_pack` | Preset id from `voice-pack/manifest.json` | Reference WAV path resolved from pack |
| `builtin` | Speaker id from model metadata / engine | `--voice-id` (or family equivalent) |
| `instruct` | Voice design text | Family-specific instruct payload |
| `optional_ref` | Optional reference clip or built-in fallback | Clone when ref present; else default speaker |
| `none` | (no voice control) | No voice field |

`.NET` sends `{ text, voice?, speed? }` only. Language and family-specific fields are derived in `tts_service.py` from the active catalog entry + voice-pack — never hardcoded maps in `SpeechSynthesisService.cs`.

---

## Manifest paths

| Service | File |
|---------|------|
| ASR | `docker/build/guideants-ai/asr-service/catalog/manifest.json` |
| TTS | `docker/build/guideants-ai/tts-service/catalog/manifest.json` |
| Voice pack | `docker/build/guideants-ai/voice-pack/manifest.json` |

Schema: [`catalog/schema.model.json`](./catalog/schema.model.json), [`catalog/schema.voice-pack.json`](./catalog/schema.voice-pack.json).

---

## Done when this inventory is real

- [ ] ASR manifest contains **both** rows; download + load + transcribe work for each.
- [ ] TTS manifest contains **all 11** rows with correct `family` + `voiceInput`.
- [ ] Settings UI: model picker from catalog API only; configuration controls match active entry’s `voiceInput` (and `gated` when true).
- [ ] `tts_service.py` / `asr_service.py`: load and inference use active catalog entry — no Chatterbox-only funnel.
- [ ] Unknown model id or voice → loud error (no fallback).

Track progress in [`STATE.md`](./STATE.md). Implementation order in [`TASKS.md`](./TASKS.md).
