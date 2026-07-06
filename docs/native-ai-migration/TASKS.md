# Tasks

Implementation backlog. **Work continuously until every code-complete criterion passes.** Do not stop after a single task or ship a subset as "done." The only work that legitimately waits is runtime rows that need the operator's container + GPU + HF token — record those as `pending-operator` in STATE.md with exact commands; never as a reason to stop coding.

- Product list: [INVENTORY.md](./INVENTORY.md)
- Architecture: [ARCHITECTURE.md](./ARCHITECTURE.md)
- Mark done only in [STATE.md](./STATE.md) with evidence (test or command output)

---

## Task 1 — Catalog API + client model picker ✅

**Goal:** Settings download dialogs list models from the engine catalog API, not hardcoded arrays.

**Touch:**
- `src/server/GuideAntsApi/Endpoints/Settings/SettingsServiceLocalModelsEndpoints.cs` — `GET …/local-models/catalog`
- `src/client/src/services/api.ts` — `catalogOutcome`
- `src/client/src/pages/settings/editors/common/CatalogDownloadModelDialog.tsx`
- `src/client/src/pages/settings/editors/{EmbRuntimeManager,AsrModelManager,TtsModelManager}.tsx`

**Done when:**
- [x] Proxy returns engine `/admin/catalog` for Embeddings, SpeechTranscription, SpeechSynthesis
- [x] All three “Add model” dialogs use `CatalogDownloadModelDialog`
- [x] `rg 'catalogEntries\s*=\s*\[' src/client/src/pages/settings/editors` — no matches outside `__tests__`
- [x] Client tests updated (`TtsModelManager.test.tsx`, etc.)

**Verify:**
```powershell
rg 'catalogEntries\s*=\s*\[' src/client/src/pages/settings/editors
cd src/client && npm test -- --run src/pages/settings/editors/speech-synthesis/__tests__/TtsModelManager.test.tsx
```

---

## Task 2 — Download validator uses catalog ✅

**Goal:** .NET rejects download requests for ids not in the curated catalog before proxying to the engine.

**Depends:** Task 1

**Touch:**
- `src/server/GuideAntsApi/Endpoints/Settings/ServiceLocalModelDownloadValidator.cs`
- `GuideAntsApi.Tests` (or equivalent) — accept known id, reject unknown

**Behavior:**
- `POST …/local-models/downloads` with `model_id` not in proxied catalog → **400** with clear message
- Valid id (present in current manifest) → pass through to engine unchanged

**Done when:**
- [x] Validator fetches or caches catalog ids per service
- [x] Unit tests: reject `model_id: "whisper-large-v3"`, accept `qwen3_asr_0_6b`
- [x] STATE row “Download validator” → pass

---

## Task 3 — Full ASR manifest

**Goal:** `asr-service/catalog/manifest.json` contains both INVENTORY ASR rows with complete metadata.

**Depends:** Task 2 (recommended — downloads validated)

**Touch:**
- `docker/build/guideants-ai/asr-service/catalog/manifest.json`
- Copy field patterns from existing `qwen3_asr_0_6b` entry; add `citrinet_asr` per [INVENTORY.md](./INVENTORY.md#citrinet_asr)

**Done when:**
- [x] Both ids present with `family`, `sourceRepos`, `requiredFiles`, `layout`, `gated`, `releaseStatus`
- [ ] `GET /asr/admin/catalog` returns 2 entries
- [ ] Download + load + transcribe for **each** entry (operator or test) — update STATE per-model matrix

**Verify:**
```powershell
rg '"id":' docker/build/guideants-ai/asr-service/catalog/manifest.json
# expect qwen3_asr_0_6b and citrinet_asr
```

---

## Task 4 — Full TTS manifest

**Goal:** `tts-service/catalog/manifest.json` contains all 11 INVENTORY TTS rows.

**Depends:** Task 2

**Touch:**
- `docker/build/guideants-ai/tts-service/catalog/manifest.json`
- `docs/native-ai-migration/catalog/schema.model.json` if new fields needed

**Each entry must include:** `id`, `task: tts`, `family`, `voiceInput`, `displayName`, `sourceRepos`, `requiredFiles`, `targetDirectory`, `layout`, `format`, `gated`, `releaseStatus`, `default` (one entry), `capabilities` where useful.

**Done when:**
- [x] 11 entries — ids match [INVENTORY.md](./INVENTORY.md#tts--11-entries)
- [x] `voiceInput` correct per row (`chatterbox` → `voice_pack`, CustomVoice → `builtin`, VoiceDesign → `instruct`, etc.)
- [x] `pocket_tts` has `gated: true`
- [x] Composite entries (`miotts_1_7b`, `vevo2`, `vibevoice_1_5b`) document all `sourceRepos`
- [ ] JSON Schema validation passes
- [ ] Download starts for each id (gated: with HF token — document in STATE)

---

## Task 5 — Catalog-driven UI configuration

**Goal:** TTS provider settings show the **right controls for the active model** — not a static voice enum.

**Depends:** Tasks 1, 4 (need full catalog metadata including `voiceInput`)

**Touch:**
- `docker/build/guideants-ai/tts-service/tts_service.py` — implement `GET /admin/voice-pack`
- `SettingsServiceLocalModelsEndpoints.cs` — proxy voice-pack to client
- `ApplicationSettingsService.ServiceEditors.cs` — dynamic `VoiceName` options when `voiceInput: voice_pack`
- Runtime metadata endpoint or catalog enrichment for `builtin` speaker lists — **implemented** via `/local-models/voices` proxy to audiocpp `GET /v1/audio/voices`
- Instruct field in provider editor when `voiceInput: instruct`
- **Remove:** `ServiceEditorMetadataProvider.LocalTtsVoiceNames`, `SpeechSynthesisService.LocalTtsVoiceLanguageCodes`

**Done when:**
- [ ] Active `chatterbox` → voice picker shows all voice-pack ids (54+)
- [x] Switching provider model to a `builtin` entry changes UI to speaker picker (from runtime, not hardcoded)
- [ ] `voice_design` entry shows instruct field
- [ ] `gated` model download UI shows token requirement
- [ ] Synthesis succeeds with pack voice; `lang_code` not sent from .NET
- [ ] `rg 'LocalTtsVoiceNames|LocalTtsVoiceLanguageCodes' src/server` — no matches
- [ ] Client/server tests for at least chatterbox + one `builtin` family

---

## Task 6 — Family-aware ASR/TTS runtime

**Goal:** Engine load and inference use the catalog entry’s `family` and `voiceInput` — every inventory row works.

**Depends:** Tasks 3, 4, 5

**Touch:**
- `docker/build/guideants-ai/asr-service/asr_service.py` — load/transcribe per `qwen3_asr`, `citrinet_asr`
- `docker/build/guideants-ai/tts-service/tts_service.py` — load/synthesize per all 11 families

**Done when:**
- [ ] No code path assumes Chatterbox for all TTS requests
- [ ] Each INVENTORY row: load + one inference — **pass** in STATE per-model matrix
- [ ] Wrong voice for active family → 4xx error, not fallback
- [ ] Operator notes or automated smoke recorded in STATE

**This task is the definition of “services use the selection correctly.”**

---

## Task 7 — CI / drift checks

**Goal:** CI fails if manifests drift from INVENTORY or hardcoded lists reappear.

**Depends:** Tasks 5–6

**Touch:** `scripts/native-ai-migration/verify-catalog-contract.ps1`, CI workflow or pre-commit

**Checks:**
- Manifest entry ids == INVENTORY ids (per service)
- No `catalogEntries = [` in client editors
- No `LocalTtsVoiceNames` / `LocalTtsVoiceLanguageCodes` in server
- Document in [RULES.md](./RULES.md)

---

## Task 8 — Voice pack NOTICE + bootstrap

**Goal:** Legal/provenance docs and default bootstrap voice ids match current voice-pack manifest.

**Depends:** Task 5 (voice-pack API)

**Touch:**
- `docker/build/guideants-ai/voice-pack/NOTICE.md`
- `src/server/GuideAntsApi/Resources/bootstrap/provider-stack-profiles/local-ai.json`
- Docker compose `GA_TTS_VOICE` — derive from voice-pack default or remove

**Done when:**
- [ ] NOTICE describes actual clip provenance (not obsolete LibriVox-only story if manifest is Kokoro-synthetic)
- [ ] Bootstrap `VoiceName` is a valid pack id (e.g. `af_alloy`), not `en_us_cv_001`
- [ ] `check-voice-pack-attribution.py` passes

---

## Dependency graph

```
Task 1 ✅
  └─► Task 2 ✅
        ├─► Task 3 (ASR manifest — entries done; runtime pending)
        └─► Task 4 (TTS manifest — entries done; runtime pending)
              └─► Task 5 (UI config)
                    ├─► Task 6 (runtime — BLOCKS “done”)
                    ├─► Task 8 (NOTICE/bootstrap)
                    └─► Task 7 (CI)
```

**Program complete:** Task 6 matrix all pass + Task 7 CI green.
