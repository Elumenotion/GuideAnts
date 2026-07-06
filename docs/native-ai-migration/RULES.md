# Rules

Invariants, verification commands, wire contracts, and anti-patterns. Cite rule ids in PRs.

---

## Invariants

### I1 — Catalog matches inventory

Runtime `docker/build/guideants-ai/{asr,tts,emb}-service/catalog/manifest.json` must contain **every** row in [INVENTORY.md](./INVENTORY.md) for that service, with correct `family`, `voiceInput` (TTS), `sourceRepos`, `requiredFiles`, `gated`, `layout`, `releaseStatus`.

- ASR target: **2** entries
- TTS target: **5** shipped entries ([INVENTORY.md](./INVENTORY.md#tts--5-shipped-entries)); deferred families documented separately
- Emb target: **3** entries (already met)

Fewer **shipped** entries than documented in [INVENTORY.md](./INVENTORY.md#tts--5-shipped-entries) = **fail**. Deferred families stay documented but out of `manifest.json` until blockers close.

### I2 — Client fetches catalog

Model pickers use `GET /api/settings/services/{serviceId}/local-models/catalog` via `catalogOutcome`. No `catalogEntries = [...]` in `*ModelManager*.tsx` except test fixtures.

### I3 — .NET proxies and validates catalog

- Proxy: `SettingsServiceLocalModelsEndpoints.cs` → engine `/admin/catalog`
- Download: `ServiceLocalModelDownloadValidator.cs` rejects `model_id` not in catalog (Task 2)

### I4 — UI config is catalog-driven

TTS provider editor controls follow **active loaded model** `voiceInput`:

- `voice_pack` → voice-pack API
- `builtin` → runtime speaker list from `GET /settings/services/SpeechSynthesis/local-models/voices` (proxies audiocpp `GET /v1/audio/voices`)
- `instruct` → design text field
- `gated` → HF token UX

No `LocalTtsVoiceNames` static enum.

### I5 — No .NET language maps

`SpeechSynthesisService` sends `{ text, voice?, speed? }`. `lang_code` derived in `tts_service.py`.

### I6 — Family-aware runtime

`asr_service.py` / `tts_service.py` branch on active catalog entry `family`. Every inventory row must load + infer successfully. No Chatterbox-only funnel.

### I7 — Voice pack separate from model catalog

Baked at `voice-pack/`; `GET /tts/admin/voice-pack` for UI. Not an HF download. Attribution: `check-voice-pack-attribution.py`.

### I8 — Embeddings dimension rules

GGUF only; `producedDimension <= 1536`; `LocalEmbeddingService.SourceVectorDimensions` matches active entry; model change ⇒ re-embed.

### I9 — Public inference routes unchanged

`/emb/embed`, `/asr/transcribe` (multipart `audio`), `/tts/synthesize` → WAV + `x-audio-duration-seconds`.

### I10 — STATE is the only progress ledger

Pass requires test name or operator command in [STATE.md](./STATE.md). Not “container boots.”

---

## Verification commands

Run before claiming a task complete. Paste output into STATE or PR.

```powershell
# --- Catalog completeness ---
# ASR: expect qwen3_asr_0_6b + citrinet_asr
rg '"id":' docker/build/guideants-ai/asr-service/catalog/manifest.json

# TTS: expect all shipped ids from INVENTORY.md (5 today)
rg '"id":' docker/build/guideants-ai/tts-service/catalog/manifest.json

# Emb: expect 3 ids (should already pass)
rg '"id":' docker/build/guideants-ai/emb-service/catalog/manifest.json

# --- Anti-hardcoding ---
rg 'catalogEntries\s*=\s*\[' src/client/src/pages/settings/editors
rg 'LocalTtsVoiceNames|LocalTtsVoiceLanguageCodes' src/server

# --- Voice pack ---
python docker/build/guideants-ai/scripts/check-voice-pack-attribution.py docker/build/guideants-ai/voice-pack

# --- Client tests (after UI/runtime changes) ---
cd src/client && npm test -- --run src/pages/settings/editors/speech-synthesis/__tests__/TtsModelManager.test.tsx

# --- Contract goldens (operator, container up) ---
.\scripts\native-ai-migration\capture-contract-goldens.ps1 -BaseUrl http://localhost
```

Task 7 will add `verify-catalog-contract.ps1` to automate inventory ↔ manifest id equality.

---

## Wire contracts

### Inference (stable — do not break)

| Route | Method | Body | Response |
|-------|--------|------|----------|
| `/emb/embed` | POST | JSON texts | embedding vectors + `dimensions` |
| `/asr/transcribe` | POST | multipart, field `audio` | transcript JSON + duration |
| `/tts/synthesize` | POST | `{ text, voice?, speed? }` | WAV body + `x-audio-duration-seconds` |

### Admin (engine)

| Route | Purpose |
|-------|---------|
| `GET /{emb,asr,tts}/admin/catalog` | Curated model list |
| `GET /tts/admin/voice-pack` | Voice preset manifest (to add) |
| `POST /{service}/admin/models/download` | Start catalog download |
| `POST /{service}/admin/load` | Load model by catalog/disk ref |
| `GET /{service}/ready` | Runtime readiness |

### Settings API (.NET proxy)

| Route | Purpose |
|-------|---------|
| `GET /api/settings/services/{id}/local-models/catalog` | Catalog for pickers |
| `GET /api/settings/services/{id}/local-models` | Disk model list |
| `POST /api/settings/services/{id}/local-models/downloads` | Start download |
| `POST /api/settings/services/{id}/local-models/load` | Load engine |

`{id}` ∈ `Embeddings`, `SpeechTranscription`, `SpeechSynthesis`.

---

## Anti-patterns (with examples)

| Anti-pattern | Why it fails | Example to reject |
|--------------|--------------|-------------------|
| Subset manifest as milestone | User expects full inventory | Shipping only `chatterbox` while calling TTS “done” |
| Hardcoded picker lists | Drifts from manifest | `catalogEntries = [{ id: 'chatterbox', … }]` in React |
| Static voice enum | Drifts from voice-pack | `LocalTtsVoiceNames` with 4 legacy ids |
| .NET lang map | Duplicates engine knowledge | `LocalTtsVoiceLanguageCodes["en_us_cv_001"]` |
| Chatterbox funnel | Wrong family behavior | `tts_service.py` always uses chatterbox load args |
| “Candidate” / defer language | Hides incomplete work | Task that says “add one more TTS or defer” |
| Silent fallback | Hides misconfiguration | Unknown voice → default to first pack voice |
| STATE pass without evidence | False progress | Checking Task 4 done with no runtime test |
| Reading `_archive/` for product list | Stale / contradictory | Using phase doc “optional citrinet” over INVENTORY |

---

## Schemas

- [catalog/schema.model.json](./catalog/schema.model.json) — requires `family` + `voiceInput` for `task: tts`
- [catalog/schema.voice-pack.json](./catalog/schema.voice-pack.json)

Validate after manifest edits:

```powershell
npx --yes ajv-cli validate -s docs/native-ai-migration/catalog/schema.model.json -d docker/build/guideants-ai/tts-service/catalog/manifest.json
```

---

## Operator scripts

| Script | Purpose |
|--------|---------|
| `scripts/native-ai-migration/capture-contract-goldens.ps1` | Record health/admin JSON → `goldens/` |
| `scripts/native-ai-migration/verify-emb-catalog-inclusion.py` | Load-after-download for emb GGUF |
| `scripts/native-ai-migration/run-final-codeql.ps1` | Final security scan |
| `scripts/native-ai-migration/verify-catalog-contract.ps1` | (Task 7) inventory drift + hardcode scan |

Record results in [STATE.md](./STATE.md).
