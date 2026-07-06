# Phase 3 — TTS decision + migration to audio.cpp

Parent: [`00-overview.md`](./00-overview.md). Prerequisite: Phase 2 complete
(`ga-audio-server` adapter + per-flavor audio.cpp build stages exist).
Decision: [`DECISIONS.md`](./DECISIONS.md) **D1** (TTS model family + preset strategy) —
**LOCKED: Option B → `chatterbox`** (product call 2026-07-02). Gates:
[`contract-preservation-gate.md`](./contract-preservation-gate.md),
[`flavor-build-gate.md`](./flavor-build-gate.md).

**Mission:** move speech synthesis off the torch/Kokoro Python stack
(`docker/build/guideants-ai/tts-service/tts_service.py`, hexgrad/Kokoro-82M via the
`kokoro` 0.9.4 package) onto audio.cpp.

**Decision (D1, LOCKED 2026-07-02): Option B → `chatterbox`** (`ResembleAI/chatterbox`).
§§3–4 retain the Option A/B analysis as rationale; §5 records the decision and §5.1 the
Chatterbox-specific design. Because `chatterbox` is native C++ (no torch), shipping it drops
the last direct torch importer (`kokoro`) and satisfies **D8** for the torch-removal goal.

---

## 1. Current TTS surface (verified)

- Contract (`SpeechSynthesisService.SynthesizeViaLocalTtsAsync`,
  `src/server/GuideAntsApi/Services/Components/SpeechSynthesisService.cs:662-757`):
  `POST {SpeechSynthesisBaseUrl}/tts/synthesize` with JSON
  `{ text, voice, lang_code, speed }` → WAV bytes + duration header
  (read by `ParseDurationSeconds`). .NET strips SSML before sending
  (`StripSsmlMarkup`), so the local provider receives plain text.
- Voice/lang/speed semantics (from `tts_service.py`): `voice` must match
  `^[a-z]{2}_[a-z0-9_]{1,64}$` (Kokoro ids like `af_heart`); `lang_code` ∈
  `a,b,e,f,h,i,j,p,z`; `speed` clamped 0.25–4.0. Language pipelines are retargeted at
  request time (`retarget_kokoro_pipeline_lang`).
- Voice presets exposed to users:
  `src/server/GuideAntsApi/Settings/ServiceEditorMetadataProvider.cs:14-16`
  (`KokoroVoiceNames`, first entry `af_heart`) drives the `VoiceName` enum field in the
  SpeechSynthesis service editor; client UI renders it.
- Admin surface: same `/admin/*` + `/health` + `/ready` pattern as ASR
  (`tts_service.py:703-854`), consumed by the same .NET settings/warmup endpoints.
- Model artifacts: hexgrad layout — `kokoro-v1_0.pth` (or `kokoro-v1_1-zh.pth`) +
  `config.json` + `voices/*.pt` under `/models-local/tts/Kokoro-82M`
  (`GA_TTS_DEFAULT_MODEL_PATH=Kokoro-82M`, `docker-compose.cuda.yml:114-115`).
- Env: `GA_TTS_*` incl. `VOICE` (default `af_heart`), `LANG_CODE` (default `a`),
  `SAMPLE_RATE=24000`, `DTYPE`, `DEVICE_MAP`.

## 2. audio.cpp TTS landscape (verified)

- **`kokoro_tts` is NOT in the release tree.** The loader include and registry entry are
  commented out (`src/framework/runtime/registry.cpp:11,208` — "Development registry
  entries from Share/AudioCPP that are not present in this release tree yet") and
  `src/models/` has no `kokoro_tts/` directory. `docs/tts.md:53-75` documents the family
  (dir `models/kokoro-82m-v1_0-ggml`, languages **`a`/`b` only**, `--voice-id` packaged
  voices like `af_heart`) — the docs describe the development tree, not shipped code.
- Released, registered TTS loaders (`registry.cpp:216-231`): `omnivoice`, `miotts` (+
  `miocodec`), `voxcpm2`, `vibevoice`, `pocket_tts`, `qwen3_tts`, `vevo2`, `seed_vc`
  (voice conversion), `chatterbox`.
- `qwen3_tts` variants documented in `docs/qwen3.md`: Base / VoiceDesign / CustomVoice
  (dirs `models/Qwen3-TTS-12Hz-1.7B-*`).

## 3. Option A — enable/finish Kokoro in audio.cpp

**Work:** port `kokoro_tts` loader+session from the audio.cpp development tree into the
release tree (not a mere uncomment — sources are absent); convert/obtain the
`kokoro-82m-v1_0-ggml` artifact layout; extend language coverage from `a,b` to the codes
GuideAnts accepts (`e,f,h,i,j,p,z`) or explicitly shrink the supported set; wire packaged
voice tensors so existing ids (`af_heart`, …) keep working.

| Pro | Con |
|-----|-----|
| Zero user-visible change: same voices, same presets, `ServiceEditorMetadataProvider.cs` untouched. | Upstream porting effort of unknown size in a codebase GuideAnts doesn't own; language coverage beyond `a`/`b` may require espeak-ng G2P work per language. |
| `voice`/`lang_code`/`speed` contract maps 1:1. | Model artifact migration on the volume (`.pth`+`voices/*.pt` → ggml layout) — download/admin flow changes anyway. |
| 82M model: cheap on every backend incl. CPU. | GuideAnts becomes the maintainer of a non-released family until upstream accepts it. |

## 4. Option B — migrate to a released family

Candidate shortlist (all registered in the default registry):

| Family | Size | Notes for GuideAnts |
|--------|------|--------------------|
| `qwen3_tts` (CustomVoice / VoiceDesign / Base) | 0.6B/1.7B | Same model family as ASR (one vendor story), ~10 languages, voice clone + voice design; preset-voice UX must be rebuilt on top of reference/designed voices. |
| `voxcpm2` | 2B | ~30 languages; heavier. |
| `omnivoice` | 0.6B | Very wide language coverage (646+ claimed upstream). |
| `vibevoice` | 1.5B | Already referenced (stale) in `docker-compose.ghcr-vulkan.yml:109-112` — evidence of prior intent; long-form oriented. |
| `pocket_tts` / `miotts` / `chatterbox` / `vevo2` | various | Voice-clone-first; reference-audio driven. |

**Work:** `ga-audio-server` grows the TTS task (Phase 2 adapter design anticipates
this); `/tts/synthesize` keeps its request shape but `voice` becomes a **GuideAnts voice
preset id** resolved by the adapter to family-specific conditioning (built-in voice id,
reference wav, or voice-design prompt); `lang_code` maps to the family's language
option; `speed` maps to the family's rate control **if it has one — to be validated per
candidate; if absent, post-process tempo with ffmpeg `atempo` or drop the knob
explicitly**. `ServiceEditorMetadataProvider.cs` `KokoroVoiceNames` is replaced by the
new preset list (one .NET + UI change, explicitly in scope). Curate a preset set (e.g.
bundled reference voices on the volume) so users still pick from a dropdown.

| Pro | Con |
|-----|-----|
| No upstream porting; ships on code audio.cpp already tests. | User-visible voice change — existing voices go away; users must reselect (no migration, no backwards compat); needs comms. |
| Better language coverage than Kokoro-in-audio.cpp would have at first. | `ServiceEditorMetadataProvider.cs` + client UI + service-mode presets change; legacy stored `VoiceName` values become invalid and are rejected loudly (not migrated). |
| Voice clone/design unlocks future features. | Bigger models than 82M → more VRAM/latency, matters on CPU flavor. |

## 5. Decision (D1, LOCKED)

**Option B with `chatterbox`** (`ResembleAI/chatterbox`; MIT). Chosen over `qwen3_tts` /
`omnivoice` because it is released **with full sources in the audio.cpp release tree**
(`src/models/chatterbox/`, `include/engine/models/chatterbox/`) — no upstream porting, unlike
Kokoro (Option A) — and its voice-clone path lets GuideAnts define its own voice set from
open reference audio. The costs are bounded and local: a voice pack + preset UX + one
metadata provider (`ServiceEditorMetadataProvider.cs`) + a stored-`VoiceName` migration.

### 5.1 Chatterbox specifics (verified against audio.cpp)

- **Voice-clone only; no built-in voices** (`docs/tts.md:34-35`, task `clon`, offline). Every
  request needs a reference WAV (`--voice-ref`). GuideAnts ships a **curated voice pack** —
  short (~5–10s) clean clips baked into the image — and maps each preset id to a clip.
- **Open voice sources** (redistributed in the image): prefer **CC0** — Mozilla **Common
  Voice** and **GLOBE** — so there is no attribution burden. **VCTK** / **LibriTTS-R**
  (CC BY 4.0) are allowed but require a NOTICE/attribution file. CC covers copyright, not
  likeness; CC0 consented-synthesis corpora are safest for a product persona. The full pack
  design — manifest schema, per-language CC0-first sourcing, the baked NOTICE, and the
  build-time attribution-completeness check — is
  [`voice-pack-and-attribution.md`](./voice-pack-and-attribution.md) (**D10**). The pack is
  **model-agnostic** (reusable by any clone family); Chatterbox is the active consumer.
- **No native `speed` control.** The exposed knobs are `--guidance-scale`, `--temperature`,
  `--top-p`, `--repetition-penalty`, `--max-tokens`, `--do-sample`, `exaggeration`
  (`docs/tts.md:41-51`, `src/models/chatterbox/tts.cpp:134`). The contract's `speed` is
  emulated with ffmpeg `atempo` (already in-image) **or dropped explicitly** — never silently
  ignored.
- **Non-deterministic output** (`--do-sample true`, `temperature 0.8`). Pin a per-request
  **seed** so a given (text, voice) is reproducible; Kokoro was effectively deterministic.
- **Language coverage shrinks.** audio.cpp Chatterbox = 19 langs (`docs/tts.md:33`), with
  **no Japanese and no Chinese**. The `lang_code` set `a,b,e,f,h,i,j,p,z` maps `a,b→en`,
  `e→es`, `f→fr`, `h→hi`, `i→it`, `p→pt`; **`j` (ja) and `z` (zh) are unsupported and must be
  rejected loudly** (contract-compatible error), not approximated.
- **No PerTh watermark** in the audio.cpp port (verified: no `perth`/`watermark` refs under
  `src/models/chatterbox/`; upstream Python Chatterbox always watermarks). If provenance
  marking is wanted it must be added deliberately.

**Implementation detail still to settle (not blocking D1):** the exact preset list (which
pack voices per language). **No legacy `VoiceName` migration** — per product decision there is
no backwards compatibility; stored Kokoro ids are not mapped, and an unknown/legacy voice id
is rejected loudly (user reselects), never silently remapped or defaulted.

## 6. Required changes

| Component | Change |
|-----------|--------|
| `ga-audio-server` | Add `chatterbox` TTS (task `clon`) hosting (same admin pattern; separate port :8084 process or same-process multi-task — decide by VRAM behavior). WAV output + duration header per current contract. Resolve preset id → bundled reference WAV; pin a per-request seed; map `lang_code` (`a,b→en`, `e→es`, `f→fr`, `h→hi`, `i→it`, `p→pt`) and **reject `j`/`z` loudly**; emulate `speed` via ffmpeg `atempo` or drop it explicitly. |
| Voice-pack assets | New: curated reference WAVs baked into the image (CC0 Common Voice / GLOBE preferred; VCTK / LibriTTS-R with a NOTICE file). One clip per preset id; documented provenance/licence per clip. **These are local assets, not HF downloads and not catalog entries** — kept out of the model-download/allowlist path (D9, [`model-catalog-and-downloads.md`](./model-catalog-and-downloads.md) §3.4/§5.5). Manifest schema, sourcing strategy, NOTICE path, and the build-time attribution-completeness check are in [`voice-pack-and-attribution.md`](./voice-pack-and-attribution.md) (**D10**). |
| TTS model picker (D9) | Replace the Kokoro-pinned download dialog (`TtsModelManager.tsx:63,605,694-696`) with a curated catalog dropdown offering only `task:tts` entries; this phase ships `chatterbox` from the allowlisted repo `ResembleAI/chatterbox` only. Add `GET /tts/admin/catalog`; `/tts/admin/models/download` enforces the manifest + source allowlist (GGUF / non-allowlisted / gated-without-token rejected loudly). See [`model-catalog-and-downloads.md`](./model-catalog-and-downloads.md). |
| `docker/build/guideants-ai/tts-service/` | Retired. `start-tts.sh` launches the native process. |
| `tts-requirements.txt` | Removed. Drops `kokoro`/`misaki`/`curated-transformers`/`spacy-curated-transformers`/`accelerate` — the **last direct torch importer** — from `/opt/venv` (Tier A; see §12 + D8). |
| `ServiceEditorMetadataProvider.cs` | Voice preset list (`KokoroVoiceNames`) replaced by the voice-pack preset ids. **The one approved contract change** (D1 = Option B). Also update the internal `lang_code` resolver: today it derives the code from the voice id's **first char** (`SpeechSynthesisService.cs:823-826`), which is Kokoro-specific and breaks on pack `voiceId`s — resolve language from the pack manifest instead (wire shape unchanged). See [`voice-pack-and-attribution.md`](./voice-pack-and-attribution.md) §6.1. |
| Service-mode data | **None.** No migration of stored `VoiceName` values (no backwards compatibility — product decision). Legacy Kokoro ids are not mapped; an unknown voice id is rejected loudly and the user reselects. See [`voice-pack-and-attribution.md`](./voice-pack-and-attribution.md) §6. |
| compose files | `GA_TTS_DEFAULT_MODEL_PATH/_ID` set to `chatterbox`; fix the stale `VibeVoice-1.5B` values in `docker-compose.ghcr-vulkan.yml`. `GA_TTS_DTYPE`/`DEVICE_MAP` retired as in Phase 2; `GA_TTS_TOKENIZER_PATH/_ID` (ghcr-vulkan only) retired. New env for the seed default + `speed`-handling mode (`atempo` vs disabled). |

## 7. Backend/flavor matrix

Same as Phase 2 (§6/§7 there): cuda13 native CUDA, vulkan native Vulkan, rocm per
decision 6.4, cpu CPU. TTS-specific check: chosen family's op coverage + latency on
Vulkan and CPU — larger models than Kokoro may be too slow on the cpu flavor; if so, say
so in the flavor docs rather than shipping an implicit degradation.

## 8. Risks

- **Voice identity change** is user-visible; mitigations: voice-pack samples reviewed before
  rollout, release notes stating old voices are gone and must be reselected (no migration).
- **Language regression (confirmed)**: Chatterbox in audio.cpp has **no `ja`/`zh`**; those
  `lang_code`s (`j`,`z`) must be rejected loudly (contract-compatible error), never
  approximated. `a`/`b` both collapse to `en`.
- **`speed` has no native control (confirmed)** — Chatterbox exposes no rate parameter
  (`docs/tts.md:41-51`). Emulate with ffmpeg `atempo` or drop the knob explicitly; do not
  silently ignore a requested `speed`.
- **Non-determinism**: Chatterbox samples stochastically; without a pinned seed the same
  (text, voice) varies per call. Pin a per-request seed; note it in the contract tests.
- **Voice-pack licensing**: every bundled clip must have recorded provenance + licence; CC0
  (Common Voice / GLOBE) needs no attribution, CC BY 4.0 (VCTK / LibriTTS-R) needs a NOTICE.
- **No watermark in the port**: audio.cpp Chatterbox does not apply PerTh (upstream does);
  if provenance marking is required it is a deliberate add, not assumed.
- **Duration header**: keep producing it; .NET meters minutes from it.
- **Warmup**: current `/ready` gating semantics must be preserved
  (`GA_TTS_WARMUP_ON_LOAD` behavior — synth a short fixed phrase on load).

## 9. Validation

1. Voice-pack selection (pre-implementation): render a fixed script of ≥20 sentences across
   the supported languages using each candidate reference clip on `chatterbox`; human
   listening review → chosen preset list + provenance/licence recorded. Verify a pinned seed
   makes a given (text, voice) reproducible.
2. Contract tests: `/tts/synthesize` (WAV bytes, duration header, error on bad
   voice/lang, **`lang_code` `j`/`z` rejected loudly**, `speed` honored via `atempo` or the
   documented "disabled" behavior), full `/admin/*` cycle, `/ready` warmup gating.
3. .NET e2e: `SpeechSynthesisService` happy path + settings UI voice preset round-trip;
   `LocalAiStartupWarmupService` cycle.
4. All-flavor build + `HEALTHCHECK` gate as in Phase 2.
5. Latency (time-to-first-byte for a fixed sentence) recorded vs torch baseline on
   cuda13 and cpu.

## 10. Rollback

Image-tag rollback. Unlike ASR, model artifacts **differ** between old and new images
(hexgrad `.pth` layout vs the chosen family's layout); keep the Kokoro artifacts on
`/models-local/tts` during the transition window. **No service-mode data migration is
shipped**, so there is nothing to reverse on rollback — a rollback to the Kokoro image simply
restores the old voice enum; any service mode saved with a new pack `voiceId` is rejected
loudly on the old image and the user reselects.

## 11. Gates

Run after this phase; record results in [`STATUS.md`](./STATUS.md).

- [`contract-preservation-gate.md`](./contract-preservation-gate.md) — `/tts/synthesize`
  WAV + duration-header golden replay; error-on-bad-voice/lang parity (incl. `j`/`z`
  rejected loudly); `/tts/admin/*` cycle; warmup-gated `/ready`. **The one approved contract
  change** is the voice-preset list (`ServiceEditorMetadataProvider.cs`, D1 = Option B) — the
  gate asserts the new presets round-trip and the `VoiceName` migration is reversible, and
  (D10) that the **voice-pack attribution-completeness check** is green — no CC-BY clip ships
  without complete attribution. See [`voice-pack-and-attribution.md`](./voice-pack-and-attribution.md) §5.4/§9.
- [`flavor-build-gate.md`](./flavor-build-gate.md) — all four flavors; TTS-task op coverage +
  latency on Vulkan and CPU (a heavier family than Kokoro may be too slow on cpu — document,
  do not silently degrade).
- CodeQL is **not** run here; any C++/`csharp` changes are scanned in the end-only
  [`codeql-gate.md`](./codeql-gate.md).

## 12. Definition of Done

- [x] **D1 resolved** — Option B / `chatterbox` (recorded in `DECISIONS.md`/`STATUS.md`).
      The `af_heart` → pack-id migration disposition is settled before implementation starts.
- [ ] Voice pack curated per [`voice-pack-and-attribution.md`](./voice-pack-and-attribution.md)
      (D10): reference clips chosen, each with recorded provenance + licence (CC0 preferred;
      NOTICE baked in for any CC BY 4.0 clip); listening review done; **build-time
      attribution-completeness check green** (loud fail if a non-CC0 clip lacks attribution).
- [ ] `/tts/synthesize` contract preserved (WAV + duration header); `lang_code`→language
      mapping implemented; **`j`/`z` rejected loudly, not approximated**; `speed` emulated via
      ffmpeg `atempo` or explicitly disabled; per-request seed pinned for reproducibility.
- [ ] **No `VoiceName` migration code**: legacy Kokoro ids are not mapped; an unknown/legacy
      voice id is rejected loudly (no silent remap, no fallback) and requires reselection, per
      [`voice-pack-and-attribution.md`](./voice-pack-and-attribution.md) §6.
- [ ] `ServiceEditorMetadataProvider.cs` `KokoroVoiceNames` replaced by the voice-pack ids.
- [ ] Stale `VibeVoice-1.5B` values in `docker-compose.ghcr-vulkan.yml` fixed.
- [ ] Contract-preservation + per-flavor build gates green on all four flavors.
- [ ] **Torch-removal step (Tier A, completes):** `tts-requirements.txt` retired →
      `kokoro`, `misaki`, `curated-transformers`, `spacy-curated-transformers`, and
      `accelerate` all gone from `pipdeptree -r -p torch` on every full flavor. Then delete
      the Dockerfile `accelerate==… transformers==… tokenizers==…` line (`Dockerfile.cuda:164`)
      **only after** `pipdeptree` proves no remaining requirer (incl. transitive sandbox
      packages) — do not delete on assumption. This drops the **last direct torch importer**
      (`kokoro`), satisfying D8.
- [ ] **Torch itself (Tier B) is _not_ removed here unless D7 says so.** With D7 = out of
      scope, `torch`/`torchaudio`/`torchvision` stay in `/opt/venv` for sandbox scripts and
      `pip show torch` still succeeds — state this in the PR, do not claim a torch/image-size
      win. See [`torch-removal-gate.md`](./torch-removal-gate.md) §1 Tier A/B and DECISIONS D7.
