# State

**Only progress ledger.** Update when something verifiably passes or fails.  
**Target:** [INVENTORY.md](./INVENTORY.md) — 2 ASR + 5 TTS (shipped) + 3 emb  
**Next work:** operator runtime verification of the `pending-operator` rows below (needs container + GPU + HF token). All code-complete criteria for Tasks 5–8 pass.

Last updated: 2026-07-03

**Legend:** `pass` = verified with evidence. `pending-operator` = code is complete and unit/contract-tested, but the load+inference proof requires the operator's container/GPU/HF token and has not been run here. `fail` = known broken.

---

## How to update this file

1. Run verify commands from [RULES.md](./RULES.md)
2. Set status to **pass** or **fail** — not “done”, “mostly”, “assumed”
3. Add evidence: test class/method, operator command, or PR link
4. For per-model runtime, **all four columns** (manifest, download, load, infer) must be pass for overall pass

---

## Executive summary

| Layer | Status | Gap |
|-------|--------|-----|
| Discovery / inventory doc | **Complete** | INVENTORY.md lists all models + config semantics |
| Emb manifest | **Complete** | 3/3 GGUF entries |
| ASR manifest | **Complete** | 2/2 entries |
| TTS manifest | **Complete** | 5/5 shipped entries |
| Client model picker (API) | **Complete** | Task 1 |
| Download .NET validation | **Complete** | Task 2 — catalog membership enforced |
| UI per-model config | **Complete (code)** | Task 5 — hardcoded `LocalTtsVoiceNames`/`LocalTtsVoiceLanguageCodes` removed; `VoiceName` is catalog-driven; voice-pack API end-to-end |
| TTS runtime all families | **Complete (code)** | Task 6 — Chatterbox funnel removed; server config + synth payload driven by catalog `family`/`voiceInput`. Per-model load+infer is `pending-operator` |
| CI drift guard | **Complete** | Task 7 — `verify-catalog-contract.ps1` green |
| NOTICE / bootstrap | **Complete** | Task 8 — NOTICE matches Kokoro-synthetic manifest; bootstrap/compose voice = `af_alloy` |
| Full runtime matrix | **1/5 TTS pass, 4/5 pending-operator** | Deferred families excluded until blockers close |

---

## Infrastructure baseline

| Check | Status | Evidence |
|-------|--------|----------|
| Torch removed from image | pass | cuda13 ~13.5GB; `import torch` fails in container |
| cuda13 ASR+TTS+emb round-trip | pass | Operator 2026-07-02 |
| cpu ASR+TTS round-trip | pass | Operator 2026-07-03 (backend env fix) |
| Contract golden replay | pending | `capture-contract-goldens.ps1` not recorded |
| Final CodeQL | pending | `run-final-codeql.ps1` not recorded |

---

## Manifest completeness

| Service | Inventory count | In `catalog/manifest.json` | Status |
|---------|-----------------|----------------------------|--------|
| ASR | 2 | 2 | **pass** |
| TTS | 5 shipped | 5 | **pass** |
| Embeddings | 3 | 3 | **pass** |

---

## Plumbing checklist

| Item | Status | Evidence / blocker |
|------|--------|-------------------|
| I2 Client catalog fetch | pass | `CatalogDownloadModelDialog`, Task 1 |
| I3 .NET catalog proxy | pass | `SettingsServiceLocalModelsEndpoints.cs` |
| I3 Download validator | **pass** | `ServiceLocalModelDownloadValidator.ValidateCatalogMembership` + `ServiceLocalModelCatalogSupport.GetCatalogIdsAsync` (1 min cache); tests: `ValidateCatalogMembership_RejectsUnknownModelId`, `ValidateCatalogMembership_AcceptsKnownModelId`, `GetCatalogIdsAsync_UsesCachedCatalogIds` |
| I4 UI from voiceInput | **pass** | `LocalTtsVoiceNames` removed; `VoiceName` kind=`text` (catalog-driven); voice-pack panel from API. Tests: `ServiceEditorMetadataProviderTests.GetProviderFields_LocalSpeechSynthesis_VoiceNameIsCatalogDrivenNotHardcodedEnum`, `TtsModelManager` "catalog-driven voice-pack presets" |
| I5 No .NET lang maps | **pass** | `LocalTtsVoiceLanguageCodes` removed; wire is `{text, voice?, speed?}`. Tests: `SpeechSynthesisServiceTests.SynthesizeToWavAsync_Local_ForwardsVoiceWithoutLangCode`, `...UsesLocalTtsProvider_WhenModeSelectsLocal` |
| I6 Family-aware runtime | **pass (code)** | `tts_service.py` server config + `resolve_voice_fields` driven by catalog `family`/`voiceInput`; `asr_service.py` family from catalog entry (`{family}.weight_type`). Per-model load+infer `pending-operator` |
| I7 Voice-pack API | **pass** | `GET /admin/voice-pack` (tts_service.py) + `.../local-models/voice-pack` proxy + `api.settings.localModels.voicePackOutcome` |
| I7 Voice-pack baked + attribution | pass | COPY in Dockerfile + `check-voice-pack-attribution.py` (54 voices, `kokoro_synthetic`/CC0-1.0) |
| I7 NOTICE aligned | **pass** | NOTICE rewritten for Kokoro-82M synthetic provenance; `sourceDataset` corrected `common_voice`→`kokoro_synthetic` |
| I10 CI drift script | **pass** | `scripts/native-ai-migration/verify-catalog-contract.ps1` — ASR 2/2, TTS 5/5 shipped, Emb 3/3, no hardcoded lists |

---

## Per-model runtime matrix

**Pass** = manifest yes + download pass + load pass + at least one successful inference.

### ASR

| id | manifest | download | load | transcribe | Overall |
|----|----------|----------|------|------------|---------|
| `qwen3_asr_0_6b` | yes | pass | pass | pass | **pass** |
| `citrinet_asr` | yes | pending-operator | pending-operator | pending-operator | **pending-operator** (family=`citrinet_asr` wired; needs converter download + load) |

### TTS

Runtime columns below are `pending-operator`: the code path is family-aware
(`resolve_voice_fields` + `resolve_engine_task`), but load+synthesize proof
needs the operator's container. `voiceInput` shows the wired voice contract.

| id | voiceInput | manifest | download | load | synthesize | Overall |
|----|-----------|----------|----------|------|------------|---------|
| `chatterbox` | voice_pack | yes | pass | pass | pass | **pass** |
| `qwen3_tts_0_6b_base` | voice_pack | yes | pending-operator | pending-operator | pending-operator | **pending-operator** |
| `qwen3_tts_1_7b_base` | voice_pack | yes | pending-operator | pending-operator | pending-operator | **pending-operator** |
| `qwen3_tts_1_7b_voice_design` | instruct | yes | pending-operator | pending-operator | pending-operator | **pending-operator** |
| `omnivoice` | voice_pack | yes | pending-operator | pending-operator | pending-operator | **pending-operator** |

**Deferred (not in catalog):** `qwen3_tts_1_7b_custom_voice`, `pocket_tts`, `miotts_1_7b`, `voxcpm2`, `vibevoice_1_5b`, `vibevoice_7b`, `vevo2` — see [INVENTORY.md](./INVENTORY.md#deferred-tts-not-in-catalog).

**Operator verification commands** (per model id `<ID>`, service `SpeechSynthesis`/`SpeechTranscription`):

```
# 1. download (enforced against catalog): POST /settings/services/<svc>/local-models/downloads {"model_id":"<ID>"}
#    gated (pocket_tts): include {"hf_token":"hf_..."}
# 2. load:      POST /settings/services/<svc>/local-models/load   {"model_id":"<ID>"}
# 3. readiness: GET  /settings/services/<svc>/local-models/ready  (expect 200 ready:true)
# 4. synthesize (TTS): POST tts /tts/synthesize {"text":"hello","voice":"<per voiceInput>"}
#    voice_pack -> pack id (e.g. af_alloy); builtin -> engine speaker id; instruct -> design text; optional_ref -> pack id or omit
# 4. transcribe (ASR): POST /v1/audio/transcriptions {"audio":"/path.wav"}
# Record wav duration / transcript here and flip the row to pass.
```

### Embeddings (reference)

| id | manifest | download | load | embed | Overall |
|----|----------|----------|------|-------|---------|
| `qwen3_embedding_0_6b` | yes | pass | pass | pass | **pass** |
| `embedding_gemma_300m` | yes | not recorded | not recorded | not recorded | partial |
| `bge_m3` | yes | not recorded | not recorded | not recorded | partial |

---

## Task completion

| Task | Status |
|------|--------|
| 1 Catalog API + client picker | ✅ complete |
| 2 Download validator | ✅ complete |
| 3 ASR full manifest | ✅ manifest complete; runtime matrix pending |
| 4 TTS full manifest | ✅ manifest complete; runtime matrix pending |
| 5 Catalog-driven UI config | ✅ complete (code + tests green) |
| 6 Family-aware runtime | ✅ code-complete; per-model load+infer `pending-operator` |
| 7 CI drift checks | ✅ complete (`verify-catalog-contract.ps1` green) |
| 8 NOTICE + bootstrap | ✅ complete (NOTICE + attribution check + voice ids) |

---

## What “done” is not

- Container boots with Chatterbox + Qwen3 ASR
- Task 1 catalog picker shipped
- Docs marked “phase complete” in archived plans
- 1/5 TTS shipped models in manifest (7 deferred)

**Done** = STATE per-model matrix all pass for ASR + TTS inventory + Task 7 CI.
