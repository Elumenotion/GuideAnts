# INI-driven Warmup Orchestration — Decisions

Locked decisions for this execution track. Update only when the orchestrator explicitly revises scope.

---

## D1 — Control surface is desired INI + apply only

**Decision:** No separate emergency stop/cancel signal. Provider switches (e.g. ASR → Azure) set `desired = idle` for that service section and call `POST /warmup/apply`.

**Rationale:** Fixes SEV A1 nuclear `WarmupAllAsync` unload-all when llama returns 502.

**Status:** LOCKED (2026-07-12)

---

## D2 — Incremental per-service reconcile

**Decision:** Orchestrator diffs desired vs applied per service. Unloading ASR must not unload llama, TTS, or embeddings when those sections remain `desired = warm`.

**Status:** LOCKED (2026-07-12)

---

## D3 — D11 load/unload order is orchestrator-owned

**Decision:** Order is hardcoded in `warmup_orchestrator.py`, not in the INI file.

- Unload: ImageGeneration → SpeechSynthesis → Embeddings → SpeechTranscription
- Load: SpeechTranscription → Embeddings → SpeechSynthesis → ImageGeneration
- Llama: between aux unload and aux load when needed

**Status:** LOCKED (2026-07-12)

---

## D4 — API never calls engine `/admin/load` or `/admin/unload`

**Decision:** After migration, all API warmup authority flows through `PUT /warmup/desired` + `POST /warmup/apply` via ga-admin HTTP.

**Status:** LOCKED (2026-07-12)

---

## D5 — Persisted state on `ai_local_models` volume

**Decision:**

| File | Purpose |
|------|---------|
| `/models-local/warmup-desired.ini` | API-written desired state |
| `/models-local/.warmup-state.json` | Orchestrator applied state + revision metadata |

Revision pattern mirrors `fleet_projection.py` (`desiredRevision`, `appliedRevision`, `applyStatus`).

**Status:** LOCKED (2026-07-12)
