# Native AI Migration — Acceptance Evidence

Companion to [`00-overview.md`](./00-overview.md). This is the evidence template the
orchestrator fills in as each phase completes — the concrete artifacts (commands, outputs,
logs, test refs) that prove a phase is actually done, not just claimed.

Status: **Phases 0–4 implemented in tree (2026-07-02).** Operator-run gates (Docker
builds, golden replay, `pipdeptree`, CodeQL) remain pending — see Evidence column notes.

Branch: _(record the active feature branch @ commit at acceptance)_

---

## Phase exits

| Exit | What proves it | Evidence |
|------|----------------|----------|
| **Phase 0** — D11 API reconcile | Remote-routed services idle after reconcile; local-routed warm; llama aliases unloaded when default chat is not `llama-cpp`; `ServiceModes` save triggers reconcile; `GA_*_AUTO_LOAD_ON_STARTUP=0` | `LocalAiStartupWarmupService.cs`: `LocalRoutingDesiredState` Warm/Idle/Unknown; unload on remote; `UnloadAllLoadedLlamaAliasesAsync` when chat ≠ llama-cpp; `SettingsServiceEditorEndpoints` calls `WarmupAllAsync` on routing save. Operator: routing-flip integration test still pending. |
| **Phase 1** — embeddings via `llama-server --embeddings` | `/emb/embed` golden replay; D9 catalog; inclusion gate script | `emb_service.py` llama-server facade :18085; `catalog/manifest.json`; `EmbRuntimeManager.tsx` curated dropdown; `scripts/native-ai-migration/verify-emb-catalog-inclusion.py`; `re-embed-runbook.md`. Operator: golden replay + inclusion gate run pending. |
| **Phase 2** — ASR via audiocpp_server | `/asr/transcribe` multipart; all four flavors build | `asr_service.py` spawns `audiocpp_server`; Docker clones audio.cpp → `audiocpp_server`; catalog in `asr-service/catalog/`. Operator: WER golden set + flavor builds pending. |
| **Phase 3** — TTS via `chatterbox` (D1 LOCKED) | Voice-pack; `/tts/synthesize`; preset replacement | `tts_api.cpp`, `voice-pack/`; `ServiceEditorMetadataProvider.cs`, `SpeechSynthesisService.cs`; `TtsModelManager.tsx`; `scripts/check-voice-pack-attribution.py`; torch TTS pip deps removed from Dockerfiles. Operator: golden replay pending. |
| **Phase 4** — control-plane consolidation | Admin golden proxy; process count ~7 | `admin-service/ga_admin_service.py`, `start-ga-admin.sh`; `nginx.conf` admin → :8086; `entrypoint.sh` retires llama-admin + sd processes. Operator: golden proxy + process count verification pending. |

---

## Open decisions at acceptance (from `DECISIONS.md`)

| ID | Decision | Resolution recorded | Evidence |
|----|----------|---------------------|----------|
| D1 | TTS model family + presets | **LOCKED — Option B / `chatterbox`** (2026-07-02) | voice-pack selection notes + decision record |
| D2 | cuda-multi embeddings single-instance | **confirmed** (2026-07-02) | accepted default; throughput TBD at deploy |
| D3 | Corpus re-embedding at cutover | **confirmed** (2026-07-02) | `re-embed-runbook.md`; job at deploy |
| D4 | ROCm route = Vulkan build | **confirmed** (2026-07-02) | `Dockerfile.rocm` `ga-audio-vulkan-builder` |
| D5 | Thin Python facade over upstream `audiocpp_server` (not fork, not vendored C++) | **confirmed** (2026-07-02) | `asr_service.py` / `tts_service.py` spawn cloned binary |
| D6 | nginx + one Python `ga-admin`; not merged into ScriptExecutionAgent | **implemented** | separate `ga_admin_service.py` process |
| D7 | Sandbox torch — Tier B NOT ATTEMPTED | **confirmed out of scope** | torch retained for sandbox |
| D8 | Torch removal blocked until TTS off torch (depends on D1) | **Tier A pip lines removed** | `asr-requirements.txt`, `tts-requirements.txt`, `transformers`/`tokenizers`/`accelerate` dropped from Dockerfiles; operator `pipdeptree` pending |
| D9 | Curated catalog + download allowlist (emb/ASR/TTS); emb = GGUF, `producedDimension <= 1536` | **LOCKED — recommended** (2026-07-02); **emb sub-decision RESOLVED: default `Qwen3-Embedding-0.6B` + alts `EmbeddingGemma-300M`/`bge-m3`; license-not-a-filter; multilingual-default** | emb/asr/tts pickers are curated dropdowns; download-allowlist rejection test (non-manifest / wrong-format / `> 1536` emb); emb build-time inclusion-gate proof (published ⇒ verified: actual dim == recorded `producedDimension` ≤ 1536, no runtime flag); Harrier → Qwen3 cutover: `Instruct:` prefix + re-embed (D3), `SourceVectorDimensions` stays 1024; **license surfaced for transparency only (never disqualifying); no convert-and-ship (point at source, prefer official GGUF)** |
| D10 | Voice-pack sourcing + attribution (Phase 3) | **LOCKED — recommended** (2026-07-02) | voice-pack manifest + NOTICE + build-time attribution-completeness check |
| D11 | Routing-aware local runtime reconciliation | **LOCKED** (2026-07-02) | routing-flip idle/warm tests; partial-stack test; ServiceModes save triggers reconcile; container autoload off |

---

## Frozen invariants at acceptance (from `DECISIONS.md` Part B)

| Invariant | Evidence |
|-----------|----------|
| Public routes `/asr/ /tts/ /emb/ /sd/ /llama-cpp/ /llama-admin/ /sandbox/ /media/` unchanged | contract-preservation golden replay per phase |
| No .NET/client change (except approved Phase 3 presets) | `git diff` shows no `src/server`/`src/client` change beyond the approved file(s) |
| Embeddings consumer width `== 1536` (via normalization); local provider `dimensions == 1024` for today's model | `/emb/embed` response `dimensions == 1024` + `LocalEmbeddingService.cs:98-102` still passes; consumers store/search **1536**-wide vectors (`DocumentChunk.cs:51-53`, `ProviderRoutedEmbeddingService.cs:69`) |
| No runtime fallback (rollback = image-tag redeploy) | rollback runbook per phase §Rollback; no in-image torch path retained |
| Data plane native, control plane consolidated; persistent processes | process list; no per-request CLI |
| `ga-admin` ≠ ScriptExecutionAgent (D6 security) | separate processes; sandbox has no model-mgmt authority |
| `Dockerfile.slim` untouched | `git diff` clean for `Dockerfile.slim`/`entrypoint.slim.sh` |
| SD inference path unchanged | `/sd/txt2img` + `/sd/img2img` golden replay |
| Single shared `/opt/venv`; sandbox torch = separate decision (D7) | `Sandboxes/*` requirements unchanged while D7 out of scope; `pip show torch` still succeeds (Tier B not attempted) |
| D11 routing reconcile: warm local, idle remote; global routing only | routing-flip + partial-stack evidence (see D11 row above) |

---

## Torch-removal evidence (per [`torch-removal-gate.md`](./torch-removal-gate.md))

**Tier A — torch-dependent service packages gone** (achievable by Phases 1–3; invisible to
sandbox users). Fill from `pipdeptree -r -p torch` on each full flavor.

| Package (service) | Removed by | Reverse-dep tree no longer lists it (cuda13/vulkan/rocm/cpu) | Evidence |
|---|---|---|---|
| `sentence-transformers` | Phase 1 | _pending_ | `pipdeptree -r -p torch` |
| `qwen-asr` | Phase 2 | _pending_ | `pipdeptree -r -p torch` |
| `accelerate` (ASR+TTS use) | Phase 3 (after TTS) | _pending_ | `pipdeptree -r -p torch` |
| `kokoro`, `misaki` | Phase 3 | _pending_ | `pipdeptree -r -p torch` |
| `curated-transformers`, `spacy-curated-transformers` | Phase 3 (via misaki→kokoro) | _pending_ | `pipdeptree -r -p torch`; confirm no direct `spacy`/`curated` importer anywhere (verified none at plan time) |
| `transformers`, `tokenizers` | Phase 3/4 **iff** no remaining requirer | _pending_ | `pipdeptree` shows no requirer incl. transitive **sandbox** packages before deletion |

**Tier B — torch itself** (`torch`/`torchaudio`/`torchvision`). Only attempted if **D7**
authorizes dropping sandbox torch (single shared `/opt/venv`).

| Check | Result |
|---|---|
| D7 resolution (keep sandbox torch = Tier B NOT ATTEMPTED, or drop = attempt) | _pending (recommended: out of scope)_ |
| `pip show torch` **fails** in `/opt/venv` (cuda13/vulkan/rocm/cpu) | _pending / N/A while D7 out of scope_ |
| `pipdeptree -r -p torch` errors "package not found" | _pending / N/A_ |
| No `download.pytorch.org` index left in any `Dockerfile.*`; no `torch*` in copied sandbox `requirements.txt` | _pending / N/A_ |
| `torchvision` removed — note it was installed but **imported by nothing** and not declared by the sandbox (dead weight; safe to drop even under Tier A) | _pending_ |
| `torchaudio` removed — note it is declared by the sandbox reqs (`python311TorchCUDA:2`) but imported by no service → its removal is a **sandbox** concern (D7) | _pending_ |

**Image-size delta (no invented numbers — measure at acceptance).**

| Flavor | `/opt/venv` before | after Tier A | after Tier B (if D7) | Evidence |
|---|---|---|---|---|
| cuda13 | _pending_ | _pending_ | _pending / N/A_ | `du -sh /opt/venv` |
| vulkan | _pending_ | _pending_ | _pending / N/A_ | `du -sh /opt/venv` |
| rocm | _pending_ | _pending_ | _pending / N/A_ | `du -sh /opt/venv` |
| cpu | _pending_ | _pending_ | _pending / N/A_ | `du -sh /opt/venv` |

**Per-service contract-preservation + behavior-equivalence sign-off** (honest verdict —
contract shape is preservable for all three; behavior is _not_ equivalent everywhere).

| Service | Wire contract preserved (shape) | Behavior verdict + required caveat | Evidence |
|---|---|---|---|
| **ASR** (`/asr/transcribe`) | _pending_ — multipart field `audio` → `{ requestId, text, durationSeconds, modelRef }` | **Near-equivalent, validate.** Same model/weights (Qwen3-ASR-0.6B safetensors) but ggml runtime/decoder ≠ torch/qwen-asr → transcript text not bit-identical (WER/CER review). **`durationSeconds` caveat:** current `get_audio_duration_seconds` uses libsndfile (`asr_service.py:82-89`) which returns 0 for webm/compressed uploads; a native `ffprobe` path would report real duration → a small observable change to validate. | WER/CER golden set; duration parity check |
| **Embeddings** (`/emb/embed`) | _pending_ — `{ data:[{embedding}], dimensions (=active model's dim, 1024 today), modelRef }`; consumers store/search **1536**-wide vectors after normalization | **NOT numerically equivalent — not fully invisible.** GGUF quantization + llama.cpp runtime + hand-rolled query prefix vs sentence-transformers `prompt_name="web_search_query"` (`emb_service.py:394,399`) → vectors differ; retrieval results not guaranteed identical; stored corpus becomes mixed-provenance unless re-embedded. **The real blocker to any model swap is embedding-space incompatibility → a full corpus re-embed (D3), required even for a same-dimension model.** API **route shape** unchanged; the consumer-visible **1536**-wide vector (reached by `NormalizeToTarget`) is invisible to clients, not the on-wire `dimensions` value. **Model selection now curated (D9):** the emb picker offers only `producedDimension <= 1536` `task:emb` GGUF entries (no free-text browse); `> 1536` is rejected loudly (else silently truncated by `NormalizeToTarget` — data loss), and on a model change `SourceVectorDimensions` is updated as a matched pair. Retrieval quality is the caveat, not the contract shape. | retrieval-parity report + D3 re-embed job log; curated-dropdown + load-after-download (actual dim == declared `producedDimension` ≤ 1536) proof |
| **TTS** (`/tts/synthesize`) | _pending_ — `{ text, voice, lang_code, speed }` → WAV + `x-audio-duration-seconds` header | **D1 LOCKED = `chatterbox` (Option B): request/WAV/duration shape preserved, but voices are NOT invisible.** The Kokoro voice-id space (`ServiceEditorMetadataProvider.cs:14` `KokoroVoiceNames`) is replaced by voice-pack ids; audio identity, `speed` handling (no native control → `atempo`/dropped), determinism (seed-pinned), and language set (**no `ja`/`zh`**) all change → **visible** to clients. Requires the approved `ServiceEditorMetadataProvider` change. **No `VoiceName` migration / no backwards compat** — legacy Kokoro ids are not mapped and are rejected loudly (user reselects). | voice-pack selection notes; unknown-voice rejection check; contract golden |

## Gate final-pass references

| Gate | Cadence | Final result | Evidence |
|------|---------|--------------|----------|
| [Contract preservation](./contract-preservation-gate.md) | every phase + final | _pending_ | golden replay suite |
| [Per-flavor build/smoke](./flavor-build-gate.md) | build-touching phases + final | _pending_ | 4× build logs + `HEALTHCHECK` |
| [CodeQL](./codeql-gate.md) | **end-only, changed-languages-only** | **deferred** | `scripts/native-ai-migration/run-final-codeql.ps1` — run after Phase 4 operator gates |
| [Torch removal](./torch-removal-gate.md) | after each package-tier removal + final | _pending_ | Tier A reverse-dep tree; Tier B `pip show torch` (if D7); per-flavor size delta |

---

## Final verification (fill at acceptance)

| Check | Result |
|---|---|
| All four flavors build | _pending_ |
| `HEALTHCHECK` green all flavors | _pending_ |
| Contract goldens replay clean (all routes) | _pending_ |
| CodeQL end-only clean/triaged (changed languages) | _pending_ |
| Torch-dependent service packages removed (Tier A); process count reduced | _pending_ |
| Torch itself removed (Tier B) — only if D7 drops sandbox torch; else NOT ATTEMPTED, `pip show torch` still succeeds (recorded, not claimed as a win) | _pending / N/A_ |
| No open deviations in `STATUS.md` | _pending_ |

### Commands to run at final acceptance (fill outputs)

```text
# per flavor
docker build -f docker/build/guideants-ai/Dockerfile.<flavor> ...   → <result>
docker run ... && curl /emb/health /asr/health /tts/health /sd/health → <result>
# contract goldens
<golden-replay-runner> --routes emb,asr,tts,sd,llama-admin           → <diff summary>
# codeql (end-only, matrix derived from diff)
codeql database analyze ... <cpp|python|csharp>                      → <new vs baseline>
```
