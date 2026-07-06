# Architecture

How curated catalogs flow from image bake through settings UI to inference. See [GOALS.md](./GOALS.md) for intent and [INVENTORY.md](./INVENTORY.md) for the model list.

---

## Container layout

```
guideants-ai image
├── emb-service/          → llama-server facade (:emb nginx → emb_service.py)
├── asr-service/          → audiocpp ASR (:asr nginx → asr_service.py → audiocpp_server)
├── tts-service/          → audiocpp TTS (:tts nginx → tts_service.py → audiocpp_server)
├── voice-pack/           → baked reference WAVs + manifest.json (not HF)
└── */catalog/manifest.json   → curated model entries (authority for download + metadata)
```

Model weights land under per-service `GA_*_MODEL_DIR` (typically `/models-local/{asr,tts,emb}`) after catalog-driven download.

---

## Settings data flow

```
┌─────────────────┐     GET …/local-models/catalog      ┌──────────────────┐
│ React settings  │ ────────────────────────────────────► │ GuideAnts API    │
│ ModelManager +  │                                       │ SettingsService  │
│ CatalogDownload │     GET …/local-models (disk list)    │ LocalModels      │
│ Dialog          │ ◄──────────────────────────────────── │ Endpoints        │
└────────┬────────┘                                       └────────┬─────────┘
         │                                                         │ proxy
         │  Provider editor (VoiceName, etc.)                      ▼
         │ ◄── should be enriched from catalog + voice-pack ┌──────────────┐
         │                                                  │ emb/asr/tts  │
         ▼                                                  │ /admin/*     │
┌─────────────────┐     POST /tts/synthesize               └──────────────┘
│ SpeechSynthesis │
│ Service (.NET)  │ ─────────────────────────────────────────► TTS engine
└─────────────────┘     { text, voice?, speed? }
```

### Catalog fetch (Task 1 — done)

| Layer | Endpoint |
|-------|----------|
| Client | `api.settings.localModels.catalogOutcome(serviceId)` |
| .NET | `GET /api/settings/services/{serviceId}/local-models/catalog` |
| Engine | `GET {adminBase}/admin/catalog` |

Implemented in `SettingsServiceLocalModelsEndpoints.cs`, `CatalogDownloadModelDialog.tsx`, `api.ts`.

### Download (Task 2 — in progress)

| Layer | Endpoint |
|-------|----------|
| Client | `api.settings.localModels.startDownload(serviceId, { model_id, revision? })` |
| .NET | `POST …/local-models/downloads` — **must validate** `model_id` ∈ catalog |
| Engine | `POST /{asr,tts,emb}/admin/models/download` |

### Voice pack + runtime voices

| Layer | Endpoint |
|-------|----------|
| Client | `voiceInput: voice_pack` / `optional_ref` → `GET …/local-models/voice-pack`; `voiceInput: builtin` → `GET …/local-models/voices` |
| .NET | Proxies `GET …/voice-pack` and `GET …/local-models/voices` |
| Engine | `GET /tts/admin/voice-pack`, `GET /tts/admin/voices` (proxies audiocpp `GET /v1/audio/voices`) |

At model load, `voice_pack` / `optional_ref` entries register **server voice presets** on `audiocpp_server` (`voice_ref` + `reference_text` per preset from the baked pack manifest). Synthesis passes the preset id in `voice`; the engine injects clip + transcript.

---

## TTS: two independent assets

Users must not conflate these:

| Asset | What it is | How user gets it |
|-------|------------|------------------|
| **TTS model weights** | e.g. Chatterbox safetensors | Catalog download (`chatterbox` id → `ResembleAI/chatterbox`) |
| **Reference voice** | Short WAV for clone families | Voice-pack preset id (baked clip) or user-provided ref for `optional_ref` |

For `voiceInput: builtin`, voices ship **inside** the model snapshot (e.g. PocketTTS embeddings) — picker comes from `GET /local-models/voices` after load.

---

## TTS synthesize path

```
1. User selects provider with VoiceName = "af_alloy" (example)
2. .NET SpeechSynthesisService builds POST /tts/synthesize { text, voice: "af_alloy", speed }
3. tts_service.py:
   a. Resolve active loaded catalog entry (e.g. chatterbox, voiceInput: voice_pack)
   b. Map voice id → server preset name, builtin speaker id, or instruct text
   c. Derive lang_code from voice-pack manifest / family rules
   d. Call audiocpp with family + voice (presets carry voice_ref + reference_text)
4. Return WAV + x-audio-duration-seconds header
```

---

## ASR transcribe path

```
1. Client/API uploads audio multipart to /asr/transcribe
2. asr_service.py uses active loaded family (qwen3_asr or citrinet_asr)
3. Returns JSON transcript + duration
```

No per-model voice configuration for ASR.

---

## Embeddings path (reference — largely complete)

```
1. Catalog picker (3 GGUF entries) → download single .gguf file
2. llama-server --embeddings loads GGUF
3. POST /emb/embed returns vectors; .NET normalizes to 1536 for storage
4. LocalEmbeddingService.SourceVectorDimensions must match active entry producedDimension
```

---

## Key code locations

| Concern | Location |
|---------|----------|
| Catalog proxy | `src/server/GuideAntsApi/Endpoints/Settings/SettingsServiceLocalModelsEndpoints.cs` |
| Download validation | `src/server/GuideAntsApi/Endpoints/Settings/ServiceLocalModelDownloadValidator.cs` |
| TTS inference (.NET) | `src/server/GuideAntsApi/Services/SpeechSynthesisService.cs` |
| Voice enum metadata | `src/server/GuideAntsApi/Services/ApplicationSettingsService.ServiceEditors.cs`, `ServiceEditorMetadataProvider.cs` |
| Client catalog dialog | `src/client/src/pages/settings/editors/common/CatalogDownloadModelDialog.tsx` |
| TTS engine | `docker/build/guideants-ai/tts-service/tts_service.py` |
| ASR engine | `docker/build/guideants-ai/asr-service/asr_service.py` |
| Catalog enforcement (Python) | Each `*_service.py` `/admin/catalog`, `/admin/models/download` |

---

## Nginx prefixes (stable)

| Public prefix | Service |
|---------------|---------|
| `/emb/` | Embeddings |
| `/asr/` | Speech transcription |
| `/tts/` | Speech synthesis |

Admin routes live under the same prefix (`/asr/admin/…`, etc.). Contract goldens in [goldens/](./goldens/) capture health/model shapes.
