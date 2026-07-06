# Native local AI — documentation

GuideAnts local model services in the `guideants-ai` container share one pattern: **curated catalog manifests** drive settings UI and runtime. This folder documents that pattern and tracks progress.

**Last updated:** 2026-07-03

---

## Catalog scope

One system, multiple services. Each gets an **INVENTORY** row set and a runtime **`catalog/manifest.json`** (or domain-specific companion manifest like voice-pack).

| Service | Engine | Catalog path | INVENTORY | Status |
|---------|--------|--------------|-----------|--------|
| **Embeddings** | llama-server GGUF | `emb-service/catalog/manifest.json` | [INVENTORY.md](./INVENTORY.md#embeddings) | manifest **complete** (3) — reference implementation |
| **ASR** | audio.cpp | `asr-service/catalog/manifest.json` | [INVENTORY.md](./INVENTORY.md#asr--2-entries) | manifest **2/2** |
| **TTS** | audio.cpp | `tts-service/catalog/manifest.json` | [INVENTORY.md](./INVENTORY.md#tts--11-entries) | manifest **11/11** |
| **Stable Diffusion** | (existing SD stack) | TBD | TBD | **future** — same pattern |
| **Local chat** | llama router / GGUF | TBD | TBD | **future** — same pattern |

Embeddings belongs in this doc set **not** as “out of scope,” but as the first catalog that reached completion. ASR/TTS are the active audio.cpp track. SD and local chat will add inventory sections and manifests as we go — **no hardcoded pickers, no parallel .NET enums**.

**Active implementation focus:** audio (ASR + TTS) + shared catalog plumbing (Task 1 done). Emb is done; SD/chat are not started here yet.

---

## What we are building

Three connected layers that must stay in sync:

1. **Curated manifests** — Per-service model lists with discovered sources, required files, and configuration semantics (`voiceInput` for TTS, `producedDimension` for emb, etc.). **Authoritative lists:** [INVENTORY.md](./INVENTORY.md). **Runtime copies:** `docker/build/guideants-ai/*-service/catalog/manifest.json` (must match inventory; ASR/TTS still catching up).

2. **Settings UI** — Model pickers and configuration controls are **built from manifest metadata**, not hardcoded lists. When the user selects Chatterbox they get voice-pack presets; when they select Qwen3 CustomVoice they get built-in speaker ids; when they select VoiceDesign they get an instruct field.

3. **Runtime** — Each service loads and infers using the **active catalog entry** the user selected (`asr_service.py`, `tts_service.py`, `emb_service.py`, …). .NET forwards inference with minimal payloads; it does not embed parallel product knowledge.

**Program is done** when every row in [INVENTORY.md](./INVENTORY.md) passes the matrix in [STATE.md](./STATE.md): manifest → download → UI controls → load → inference.

---

## Document map

Read in this order when implementing or reviewing work:

| Doc | Read when… |
|-----|------------|
| [HANDOFF.md](./HANDOFF.md) | Starting a new agent session — paste the prompt block |
| [GOALS.md](./GOALS.md) | You need the product intent, boundaries, and success definition |
| [ARCHITECTURE.md](./ARCHITECTURE.md) | You need request paths, component ownership, or where code lives |
| [INVENTORY.md](./INVENTORY.md) | You need the authoritative model list, sources, files, `voiceInput` |
| [RULES.md](./RULES.md) | You need invariants, wire contracts, verify commands, anti-patterns |
| [STATE.md](./STATE.md) | You need honest current progress (only ledger — update with evidence) |
| [TASKS.md](./TASKS.md) | You need the next implementation unit |

Supporting artifacts:

| Path | Purpose |
|------|---------|
| [catalog/schema.model.json](./catalog/schema.model.json) | JSON Schema for `*-service/catalog/manifest.json` |
| [catalog/schema.voice-pack.json](./catalog/schema.voice-pack.json) | JSON Schema for voice-pack manifest |
| [goldens/](./goldens/) | Contract golden JSON for operator replay |
| `scripts/native-ai-migration/` | Operator and CI scripts |

**Do not use** `_archive/` for product decisions. It holds historical phase plans and derivation notes only.

---

## Canonical runtime paths

| Artifact | Path |
|----------|------|
| ASR catalog | `docker/build/guideants-ai/asr-service/catalog/manifest.json` |
| TTS catalog | `docker/build/guideants-ai/tts-service/catalog/manifest.json` |
| Embeddings catalog | `docker/build/guideants-ai/emb-service/catalog/manifest.json` |
| Voice pack | `docker/build/guideants-ai/voice-pack/` (`manifest.json` + `clips/`) |
| ASR service | `docker/build/guideants-ai/asr-service/asr_service.py` |
| TTS service | `docker/build/guideants-ai/tts-service/tts_service.py` |
| Embeddings service | `docker/build/guideants-ai/emb-service/emb_service.py` |
| Settings proxy | `src/server/GuideAntsApi/Endpoints/Settings/SettingsServiceLocalModelsEndpoints.cs` |
| Client model managers | `src/client/src/pages/settings/editors/**/{Asr,Tts}ModelManager.tsx`, `EmbRuntimeManager.tsx` |
| Shared download dialog | `src/client/src/pages/settings/editors/common/CatalogDownloadModelDialog.tsx` |

---

## Current snapshot (2026-07-03)

| Area | Status |
|------|--------|
| Embeddings manifest | **Complete** (3 GGUF entries) |
| ASR manifest | **Complete** (2/2 inventory entries) |
| TTS manifest | **Complete** (11/11 inventory entries) |
| Client model picker from API | **Done** (Task 1) |
| Download catalog validation | **Done** (Task 2) |
| Per-model UI config (`voiceInput`) | **Not done** — .NET still hardcodes voice ids |
| Family-aware TTS runtime | **Not done** — Chatterbox-only synthesize path |
| Full inventory runtime matrix | **2/13** ASR+TTS pass — see [STATE.md](./STATE.md) |

**Next work:** [TASKS.md](./TASKS.md) Task 5 (catalog-driven UI) then Task 6 (family-aware runtime).
