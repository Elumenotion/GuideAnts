# Native local AI — execution contract (v2)

**Read this first.**  
**Product list:** [`CURATED-INVENTORY.md`](./CURATED-INVENTORY.md)  
**Progress:** [`STATE.md`](./STATE.md) · **Work order:** [`TASKS.md`](./TASKS.md)

Last updated: 2026-07-03

---

## Goals (three sentences)

1. **Curated manifests** — We already picked ASR/TTS models, discovered sources, files, and per-model configuration semantics. Manifests are the single authority; download from allowlisted sources just works.

2. **UI from the catalog** — Model pickers and configuration controls are built from manifest metadata. Each selection exposes a **complete and appropriate** control set for that entry (`voiceInput`, gating, capabilities) — never parallel hardcoded lists in React or .NET.

3. **Services use the selection** — The loaded ASR/TTS model and user’s configuration are what `asr_service.py` / `tts_service.py` and .NET inference actually use. Family-aware runtime; loud failure on mismatch — no silent subset, no Chatterbox funnel for everything.

Embeddings follow the same pattern (separate task track; 3 GGUF entries already in emb manifest).

---

## Non-goals

- Free-form Hugging Face browse in settings.
- “Minimum viable” catalogs (1 ASR + 1 TTS) as a completion milestone.
- Labeling unreleased audio.cpp families as “candidates” while shipping a subset — if it is not in [`CURATED-INVENTORY.md`](./CURATED-INVENTORY.md), we do not offer it; if it is in the inventory, we ship it fully.
- Silent fallback, legacy voice remap, or assuming all TTS is voice-pack clone.

---

## Invariants (cite in PRs)

| # | Invariant |
|---|-----------|
| **I1** | **Catalog matches inventory.** `*-service/catalog/manifest.json` contains every row in [`CURATED-INVENTORY.md`](./CURATED-INVENTORY.md) for that service, with correct `family`, `voiceInput`, sources, and files. Fewer entries = fail. |
| **I2** | **Client fetches catalog** via `GET …/local-models/catalog`. No hardcoded model lists in `*ModelManager*.tsx` (test fixtures excepted). |
| **I3** | **.NET proxies catalog** and **download validator** rejects `model_id` not in catalog. |
| **I4** | **UI config is catalog-driven.** TTS controls follow active entry’s `voiceInput`: voice-pack manifest, built-in speakers, instruct field, etc. No `LocalTtsVoiceNames` / static enums. |
| **I5** | **No .NET language maps.** Synthesis request: `{ text, voice?, speed? }`; TTS service derives language from voice-pack / catalog / family. |
| **I6** | **Family-aware runtime.** Load + transcribe/synthesize branch on active catalog entry `family`. Every inventory entry works or is not yet marked done in STATE. |
| **I7** | **Voice pack** is separate from model catalog; used when `voiceInput: "voice_pack"`. Baked at `docker/build/guideants-ai/voice-pack/`. |
| **I8** | **Embeddings:** GGUF only; `producedDimension <= 1536`; dimension match on active entry. |
| **I9** | **Public routes unchanged:** `/emb/embed`, `/asr/transcribe`, `/tts/synthesize`. |
| **I10** | **Done = STATE passes** — not “container boots” or “one model works”. |

---

## Anti-patterns

- Ship or declare victory with a manifest that is a subset of [`CURATED-INVENTORY.md`](./CURATED-INVENTORY.md).
- Hardcode model ids, voice ids, or per-family config in client or .NET.
- Implement “family not implemented” stubs as the end state for inventory entries.
- Mark tasks complete without tests or operator verification recorded in STATE.

---

## Wire contracts (stable)

| Service | Inference | Admin |
|---------|-----------|-------|
| Embeddings | `POST /emb/embed` | `GET /emb/admin/catalog`, `/admin/models`, `/admin/load`, `/ready` |
| ASR | `POST /asr/transcribe` (multipart) | `GET /asr/admin/catalog`, … |
| TTS | `POST /tts/synthesize` `{ text, voice?, speed? }` → WAV + duration | `GET /tts/admin/catalog`, `GET /tts/admin/voice-pack`, … |

Settings proxy: `SettingsServiceLocalModelsEndpoints` — includes `GET …/local-models/catalog`.

---

## Schemas & canonical manifests

| Artifact | Schema | Canonical path |
|----------|--------|----------------|
| Model catalog | [`catalog/schema.model.json`](./catalog/schema.model.json) | `docker/build/guideants-ai/*-service/catalog/manifest.json` |
| Voice pack | [`catalog/schema.voice-pack.json`](./catalog/schema.voice-pack.json) | `docker/build/guideants-ai/voice-pack/manifest.json` |
| **Inventory (target)** | — | [`CURATED-INVENTORY.md`](./CURATED-INVENTORY.md) |

Voice pack build gate:

```powershell
python docker/build/guideants-ai/scripts/check-voice-pack-attribution.py docker/build/guideants-ai/voice-pack
```

---

## Operator scripts

- Contract goldens: `scripts/native-ai-migration/capture-contract-goldens.ps1`
- Emb inclusion gate: `scripts/native-ai-migration/verify-emb-catalog-inclusion.py`
- Catalog contract verify (Task 6): `scripts/native-ai-migration/verify-catalog-contract.ps1` (to add)

Record results in [`STATE.md`](./STATE.md).
