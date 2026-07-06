# GuideAnts voice pack — NOTICE / ATTRIBUTIONS

This file records provenance for the reference clips baked into the `guideants-ai` image at
`/opt/guideants/voice-pack/`. The authoritative source is `manifest.json` in the same directory.

## What these clips are (current pack: `packVersion` 0.3.0, `generatedFrom` `kokoro-local-2026-07-03`)

Every clip in this pack is **synthesized locally with the Kokoro-82M text-to-speech model**
(`hexgrad/Kokoro-82M`, Apache-2.0). They are **not recordings of any real person** and are **not
drawn from Common Voice, LibriVox, or any human speech dataset.** Each entry's `sourceClipRef`
records the exact generation input, e.g. `synthetic:kokoro-82m:af_alloy:"This is the reference
transcript for voices"`, and `sourceDataset` is `kokoro_synthetic`.

Because the audio is model-generated and contains no third-party recording, no recording
copyright attaches. The clips are published under **CC0-1.0** (`licence` in the manifest). Kokoro
voice ids (`af_*`, `am_*`, `bf_*`, `bm_*`, and the other locale-prefixed families) are reused here
purely as stable preset identifiers.

## Voice ids

The pack contains **54 presets** (see `manifest.json` for the complete list and per-clip
checksums), for example `af_alloy`, `af_heart`, `am_adam`, `bf_emma`, `bm_george`. Voices whose
Kokoro locale prefix is Japanese (`j*`) or Chinese (`z*`) have their manifest `language` mapped to
`en` for Local TTS compatibility, as noted in the manifest `generationNote`; the underlying clip is
still the Kokoro-synthesized audio for that preset.

## Processing

Each clip was synthesized with Kokoro-82M and then normalized to the pack format: **mono, 24 kHz**
(`modified.changes`: `tts_synthesis`, `resample_24k`, `mono`). Durations and SHA-256 checksums are
recorded per entry in `manifest.json`.

## Licences present in this pack

| licence | count | attribution required |
|---|---|---|
| CC0-1.0 | 54 (all) | No |

_No CC-BY-4.0 clips are present, so no per-clip attribution block is required. If a CC-BY clip is
ever added, `scripts/check-voice-pack-attribution.py` enforces a complete `attribution` object._

---

**Honest limitations (not silently hidden):**

- These are **synthetic reference timbres**, not curated human voices. Zero-shot clone quality for
  a cloning model (e.g. Chatterbox) depends on the reference clip; a Kokoro-synthesized reference
  is a functional starter, not a quality-reviewed final pack.
- Locale/accent implied by a Kokoro voice id (e.g. `af_` / `bm_`) has **not** been aurally verified
  against the mapped manifest `language`.

Regenerated from local Kokoro clips in `clips/`; the build-time gate
`scripts/check-voice-pack-attribution.py` validates that this NOTICE and every manifest entry stay
consistent (allowed dataset/licence, existing clip files, unique ids).
