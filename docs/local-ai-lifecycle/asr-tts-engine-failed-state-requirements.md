# ASR / TTS engine failed-state — required fixes

Status: Requirements for review  
Date: 2026-08-28  
Authority: [ARCHITECTURE.md](ARCHITECTURE.md), [DECISIONS.md](DECISIONS.md) D1–D5  
Incident host: `guideants-video-ai` (`max:8112`), model `Qwen3-ASR-0.6B`

This document specifies the fixes required after a live local ASR session
accepted audio, logged `asr_transcribe_start`, then produced no transcript, no
progress, and no owner-side recovery. Manual unload/reload of an engine process
is not an acceptable operating procedure.

## 1. Incident (evidence, not anecdote)

All timestamps UTC 2026-08-28. Container was **up and healthy**. ASR Python
service was **loaded**, warmup had **succeeded** (`warmupLatencyMs: 2969` at
16:58:24). `/asr/ready` returned `ready: true` throughout the hang.

| Time | What happened | Evidence |
|------|----------------|----------|
| 16:49:33 | Short webm (`194943` B) started | `asr_transcribe_start` `9e22d755…` |
| 16:54:06 | Same request failed after 300s | `asr_transcribe_failed` `error: timed out` `latencyMs: 300374` |
| 16:56:54 | Next short webm failed immediately | `asr_transcribe_failed` HTTP 503 from `audiocpp_server`, `latencyMs: 374` |
| 16:57:25–16:58:24 | Operator unload/load + warmup | warmup transcribed the canned clip correctly |
| 16:59:36 | Next short webm succeeded | `asr_transcribe_success` `6ca0e64d…` `latencyMs: 1363` for 9.6s audio |
| 17:28:37 | Chatterbox job that had been in flight ~3600s failed | `audiocpp_skill_proxy_failed` `timed out` `2bc0dd6c…` |
| 18:00:12 | Subsequent TTS calls 503 in 3–64ms | engine still pid 405, Python TTS `busy: false` |
| 18:05:27 | User webm `218319` B started | `asr_transcribe_start` `dc05b28a…` |
| 18:06:30 | Second user webm `42033` B started | `asr_transcribe_start` `d8152c3b…` |
| 18:10:00 | First user request failed | `asr_transcribe_failed` `timed out` `latencyMs: 300591` |
| During hang | ASR `/ready` still green | `loaded: true`, `warmupSucceeded: true`, `busy: true`, `inFlightTranscriptions: 2` |
| During hang | Decoded wavs sat in `/tmp` | both clips had already passed ffmpeg; blocked on `POST :18082/v1/audio/transcriptions` |
| During hang | TTS `audiocpp_server` pid 405 | ~256% CPU, `busy: false` — GPU 0 still occupied after Python gave up |
| Separate | ~128MB POSTs never reached ASR | nginx `client intended to send too large body` vs `client_max_body_size 50m` |

Healthy baseline on this same process, same model: 9.6s audio → **1.3s**.
A 13s-class clip hanging until the 300s timeout is a stuck engine, not slow ASR.

The user-visible path was **not** a skill:

```
Electron client  →  POST /api/speech/transcribe  →  GuideAntsApi
                 →  POST {SpeechTranscriptionBaseUrl}/asr/transcribe
                 →  asr_service.py  →  audiocpp_server :18082
```

A skill-gateway TTS hang on the same GPU is a **cause of contention**. It is
not the owner of ASR recovery, not the holder of the recording, and not the
component that may retry the user's request.

## 2. Defects

### D1 — The stack is in a failed state while advertising health

`audiocpp_server` can stop completing inference (timeout, HTTP 503, GPU work
left running after the HTTP client gives up) while:

- the wrapper process is alive
- `loaded: true` and `warmupSucceeded: true`
- `/asr/ready` and `/tts/ready` return HTTP 200
- TTS Python reports `busy: false` while the engine process still burns GPU

`start_engine` / TTS equivalent treat “same model path + process alive” as
`noop-already-loaded`. A watchdog re-apply of the same plan therefore does
**nothing** to a wedged process.

### D2 — The failed state is undetected and unlogged as failure

- ASR emits `asr_transcribe_start` then silence until `asr_transcribe_failed`
  at timeout. No heartbeat, no `engine_failed` event, no stall clock.
- `LocalAiRuntimeAlignmentVerifier` treats `/ready` 200 + `loaded` + matching
  `modelRef` as aligned. It does not read `failed`, does not treat a stalled
  in-flight as a mismatch, and does not notice sibling-engine GPU poison.
- `LocalAiRuntimeWatchdogHostedService` therefore does not re-plan.
- After urllib timeout, decoded wav cleanup often does not run (`decoded_path`
  is only assigned if the blocking call returns), so `/tmp` leftovers are the
  only fossil of the hang.

### D3 — Client and API observe failure and take no owner action

- `SpeechTranscriptionService` maps timeout to `TimeoutException`.
- `SpeechEndpoints` returns 504 / 500 and drops the request.
- The API does not keep the audio bytes, does not mark the local stack failed,
  does not command recycle via the existing warmup plan, and does not retry.
- `useAudioRecorder` discards the `Blob` after building the form body. On
  error the utterance is gone.
- `DraftUserCell` does not even bind the hook's `error`, so the notebook mic
  path can fail with no UI signal.

**Owner:** `GuideAntsApi`. See ARCHITECTURE.md: the API alone decides enabled
state, selection, and when a plan is applied. `ga-admin` is a dumb executor.
Engines are workers. Skills are not in this authority chain.

## 3. Glossary

| Term | Meaning |
|------|---------|
| Wrapper | `asr_service.py` or `tts_service.py` behind `/asr` and `/tts` |
| Engine | `audiocpp_server` child process on `:18082` (ASR) or `:18084` (TTS) |
| Failed | Engine cannot be trusted to complete new inference without recycle |
| Recycle | API-commanded unload then load of the **current ServiceModes selection** (no guessing a model from disk) |
| Shared GPU pair | Local ASR and TTS on the same stack host / device 0 |
| Utterance | The audio bytes the API received for this transcribe request |

## 4. Non-goals

| ID | Out of scope | Why |
|----|----------------|-----|
| NG-1 | Skill-gateway as recovery owner | Skills are not GuideAntsApi / ServiceModes. They must not choose models, keep notebook audio, or apply lifecycle plans. |
| NG-2 | Asking an operator to docker-exec kill a pid | That is the incident workaround this spec forbids as procedure. |
| NG-3 | Cloud ASR providers (Azure, OpenAI, …) | Different failure domains; they do not share this ROCm process. |
| NG-4 | Changing which model is selected | Recycle reloads the API-owned selection. No inventory backfill (D4). |
| NG-5 | Persisted desired-state / autoload | Forbidden by ARCHITECTURE.md. |
| NG-6 | Putting drift policy in `warmup_orchestrator.py` | Executor stays dumb. Verification stays in GuideAntsApi. |

Related but separate (must not be buried in D1–D3): nginx `client_max_body_size 50m` vs API “300MB” contract. Those POSTs never reached ASR. Track as **R-B.1**.

## 5. Requirements

### R-F — Failed state (fixes D1)

| ID | Requirement | Owner |
|----|-------------|-------|
| R-F.1 | Recycle is **exception-driven**, not quality-driven. See the table below. Bound: recycle once, retry the same request once, then hard-fail. | `asr_service.py`, `tts_service.py` |

**R-F.1 exception trigger (literal — not “no output”):**

| Recycle (the engine RPC **threw** or returned a failure status) | Do **not** recycle |
|---|---|
| `TimeoutError` / urllib timeout / HTTP client timeout | HTTP **200** with `text: ""` (empty transcript is success) |
| `URLError` / `ConnectionError` / connection refused, reset, broken pipe | HTTP **200** with a transcript you dislike |
| HTTP **5xx** from `audiocpp_server` (including 500 graph-alloc) | HTTP **4xx** other than 408 (400/401/403/404/413/422/…) |
| HTTP **408** | User-cancelled request |
| `RuntimeError("audiocpp_server returned empty WAV payload")` — 200 with **zero bytes**, which the wrapper **raises** | HTTP 200 with a WAV that happens to be quiet |
| Any other exception that escaped the RPC and is not an HTTP 4xx (except 408) | |

Empty transcript is **not** an exception. Empty WAV **bytes** on TTS **is** an exception because the wrapper raises. Those are different.
| R-F.2 | Failed MUST be visible on `GET /ready` and `GET /health`: `ready: false`, `failed: true`, `failedReason`, `failedAtUtc`. HTTP status for `/ready` MUST be **503**, not 200. `loaded` MAY remain true (weights are still selected); ready MUST not. | wrappers |
| R-F.3 | HTTP timeout of the wrapper→engine client MUST NOT be treated as engine idle. GPU work started by that call is still in-flight until the **engine process is recycled**. | wrappers |
| R-F.4 | `noop-already-loaded` MUST NOT apply when `failed` is true. A subsequent `/admin/load` for the same selection MUST stop the process and load again. | wrappers |
| R-F.5 | After a successful load or a successful recycle of the same selection, `failed` MUST be cleared only if the new engine process is alive and warmup (when enabled) succeeded. | wrappers |
| R-F.6 | ASR and TTS on the same stack share a GPU. A failed ASR inference that is consistent with a wedged sibling TTS engine is still a **stack** failure for recovery purposes (see R-A.3). The wrapper that timed out MUST still mark **itself** failed; it MUST NOT diagnose the sibling by reading the sibling's folders or env. | wrappers + API |

### R-L — Detect and log (fixes D2)

| ID | Requirement | Owner |
|----|-------------|-------|
| R-L.1 | While a transcribe is in flight, ASR MUST emit a structured heartbeat on a bounded interval (same idea as TTS `tts_synthesize_heartbeat`): event name `asr_transcribe_heartbeat`, `requestId`, `elapsedMs`, `inFlight`, `modelRef`. Silence after `asr_transcribe_start` is a defect. | `asr_service.py` |
| R-L.2 | Entering failed MUST log `asr_engine_failed` / `tts_engine_failed` with `reason`, `requestId` (if any), `modelRef`, `enginePid`, `oldestInFlightAgeMs`. This is a failure log, not a debug trace. | wrappers |
| R-L.3 | Recycle of the engine process MUST log start and success/failure (`asr_engine_restart_*` / `tts_engine_restart_*` or the equivalent load/unload events when the API commands recycle). | wrappers |
| R-L.4 | `LocalAiRuntimeAlignmentVerifier` MUST treat `/ready` HTTP 503, `ready: false`, or `failed: true` as a mismatch for an enabled local service, even when `loaded: true` and `modelRef` matches the plan. | `GuideAntsApi` |
| R-L.5 | Mismatch text MUST say the engine is **failed/unready**, not merely “not loaded”, when `failed` is set. | `GuideAntsApi` |
| R-L.6 | Temp decode files MUST be deleted on timeout as well as on success (the assignment of `decoded_path` after `await_blocking` returns is not an acceptable cleanup trigger). | `asr_service.py` |
| R-L.7 | Watchdog MUST treat R-L.4 mismatches as “needs plan” the same way it treats a missing load today. Re-apply is not sufficient unless R-F.4 holds (otherwise the executor calls load and the wrapper noops). | `GuideAntsApi` |

### R-A — API owns the utterance and the recovery (fixes D3)

| ID | Requirement | Owner |
|----|-------------|-------|
| R-A.1 | On `POST /api/speech/transcribe` (and published equivalent), the API MUST buffer the audio bytes before the first local-ASR attempt so a retry does not depend on a consumed stream or on the client still holding a Blob. | `SpeechTranscriptionService` |
| R-A.2 | A local-ASR timeout, HTTP 503, HTTP 5xx, or transport failure is an **engine failure**. The API MUST log `asr_api_engine_failed` with `requestId`, `status`/`errorType`, `latencyMs`, and payload size bucket. Returning 504/500 **without** this log and without R-A.3 is non-compliant. | `GuideAntsApi` |
| R-A.3 | After R-A.2, the API MUST command recycle of the **shared GPU pair on that stack** (SpeechTranscription and SpeechSynthesis) using the existing warmup machinery: idle those two services, wait for apply, then apply the current ServiceModes plan (the same selections as before). No new model is chosen. Embeddings/image/llama are not idled unless they are on that pair's plan for other reasons. | `ILocalAiStartupWarmupService` (new recovery entry point allowed; new policy source is not) |
| R-A.4 | After recycle completes (or after a bounded recycle failure is logged), the API MUST retry **one** transcribe of the buffered utterance against local ASR. | `SpeechTranscriptionService` |
| R-A.5 | Only if the retry fails MAY the API return 504/500 to the client. The error body MUST remain the existing machine fields (`transcription_timeout` / `transcription_failed`) plus enough for the client to keep/retry the same bytes if it still has them. | `SpeechEndpoints` |
| R-A.6 | Recycle+retry runs under the API process. The client MUST NOT be required to re-record for the first failure. Wall time may exceed a single engine timeout; that is acceptable. A second failure is a hard error. | `GuideAntsApi` |
| R-A.7 | Per-request timeout MUST be the transcription option (`TimeoutSeconds`), not the typed HttpClient default of 100s. The CTS owns the attempt timeout; recovery is a second attempt, not an extension of the first hung socket. | `SpeechTranscriptionService` |
| R-A.8 | Local TTS timeout/503/5xx on the API synthesis path MUST log `tts_api_engine_failed` and use the same R-A.3 recycle. TTS has no “utterance to keep” in the ASR sense; retry of the same synthesize text once after recycle is required. | `SpeechSynthesisService` |

### R-C — Client may not discard the utterance

| ID | Requirement | Owner |
|----|-------------|-------|
| R-C.1 | `useAudioRecorder` MUST retain the last recording Blob until transcription **succeeds** or the user starts a new recording or cancels. Failure of `/api/speech/transcribe` MUST NOT drop the bytes. | client |
| R-C.2 | The hook MUST expose the last error string and a `retryLastTranscription` action that resends the retained Blob. | client |
| R-C.3 | `DraftUserCell` / `MicrophoneButton` MUST show that error and a Retry control when a retained Blob exists. Binding only `isProcessing` is non-compliant. | client |
| R-C.4 | The client MUST NOT implement engine recycle, model selection, or stack diagnostics. Retry is “send the same bytes to the API again.” | client |

### R-B — Body-size contract (related incident)

| ID | Requirement | Owner |
|----|-------------|-------|
| R-B.1 | Nginx `client_max_body_size` for `/asr/` MUST be at least the API's documented maximum (currently 300MB class). A 413 at the gateway with no `asr_transcribe_*` line is a silent drop. | `docker/build/guideants-ai/nginx.conf` (and slim sibling) |

## 6. Authority and call sequence

Normative sequence for a local ASR failure:

```
1. Client POST /api/speech/transcribe (multipart audio)
2. API buffers bytes, logs asr_api_request_start, POST /asr/transcribe
3. Wrapper logs asr_transcribe_start and heartbeats (R-L.1)
4. Engine does not complete
      wrapper: mark failed, log asr_engine_failed, /ready → 503 (R-F.*)
      wrapper MAY kill/respawn its own process to stop GPU work (worker, not policy)
5. API sees timeout/503/5xx
      log asr_api_engine_failed (R-A.2)
      idle ASR+TTS on that stack → apply current plan (R-A.3)
      retry once with buffered bytes (R-A.4)
6. Success → 200 text to client; client may drop Blob (R-C.1)
   Failure → 504/500; client keeps Blob and shows Retry (R-C.*)
```

Watchdog is a **backstop**, not the owner of the in-flight utterance:

```
Watchdog / verifier sees /asr/ready 503 failed
  → mismatch (R-L.4)
  → submit current API plan
  → wrapper must not noop (R-F.4)
```

`ga-admin` still only executes the plan the API submits. Wrappers may restart
their own `audiocpp_server` child as a **mechanical** response to a hung RPC
(same class as existing TTS `synthesize_with_engine_recovery`). That restart
does not select a model and does not replace R-A.3: the API still recycles the
pair because the sibling engine may be the GPU hog.

## 7. Traceability

| Defect | Requirement IDs | Current gap (file) | Acceptance |
|--------|-----------------|--------------------|------------|
| D1 failed while healthy | R-F.1–R-F.6 | `asr_service.py` `/ready` 200 + `loaded`; TTS pid spinning + `busy: false`; `start_engine` noop | After a forced engine timeout, `/asr/ready` is 503 with `failed: true`. `/admin/load` of the same ref starts a new pid. |
| D2 undetected / unlogged | R-L.1–R-L.7 | No ASR heartbeats; verifier ignores `failed`; watchdog idle; `/tmp` wavs left | Logs contain heartbeats then `asr_engine_failed`. Verifier unit test: 200+loaded+failed → mismatch. Timeout deletes temp wav. |
| D3 no owner action | R-A.1–R-A.8, R-C.1–R-C.4 | `SpeechEndpoints` 504; stream not buffered; no recycle; Blob dropped; mic UI ignores `error` | API test: first local ASR 504/timeout → warmup idle+apply invoked for SpeechTranscription+SpeechSynthesis → second transcribe of **same bytes** succeeds. Client test: failed transcribe still has Blob; Retry visible. |
| Related drop | R-B.1 | nginx 50m vs 128MB POST | 128MB POST reaches ASR or returns an API-visible 413 with a log line, not only nginx error. |

## 8. Test obligations

| ID | Test | Must prove |
|----|------|------------|
| T-1 | `asr_service` unit | Timeout/503 from engine → `failed` on snapshot; `/ready` 503; second `start_engine` is not noop |
| T-2 | `asr_service` unit | Heartbeat function emits while blocking work runs (same pattern as TTS heartbeat tests) |
| T-3 | `asr_service` unit | Temp wav removed when transcribe raises TimeoutError |
| T-4 | `LocalAiRuntimeAlignmentVerifier` | JSON `failed: true` or HTTP 503 `/ready` → mismatch for enabled ASR/TTS; message includes failed reason when present |
| T-5 | `SpeechTranscriptionService` | Local ASR first response timeout/503 → recovery called → same buffered body posted again → 200 |
| T-6 | `SpeechTranscriptionService` | Recovery is **not** invoked for Azure/OpenAI paths |
| T-7 | Warmup recovery helper | Builds a plan that idles SpeechTranscription and SpeechSynthesis then a full current plan; does not invent model refs |
| T-8 | `useAudioRecorder` | Rejected transcribe keeps Blob; `retryLastTranscription` calls `transcribeAudio` again |
| T-9 | `MicrophoneButton` / `DraftUserCell` | Error text + Retry when hook reports error and retained recording |

No test may pass by stubbing recycle as “log and continue” without a second transcribe attempt.

## 9. Explicit rejections

| Rejected idea | Reason |
|---------------|--------|
| Operator kills pid 405 / docker restart as the fix | D3: API is the owner. That is incident response, not product behavior. |
| Skill gateway owns recycle / keeps audio / retries notebook ASR | Wrong authority chain. Skills are not ServiceModes. |
| Watchdog-only recovery (no in-request retry) | Loses the utterance. Watchdog cannot see the Blob. |
| Client-only retry without API recycle | Retries into the same wedged GPU. |
| `/ready` stays 200 because “model is loaded” | That is the lie that hid D1. |
| Guess a model from `/models-local/asr` after failure | Violates D4 / ARCHITECTURE failure behavior. |
| Fallback to cloud ASR on local timeout | Silent provider switch. Fail-fast; retry local after recycle. |

## 10. Review checklist

A reviewer should be able to answer each of these with a file+test cite after implementation:

1. Can `/asr/ready` return 200 `ready: true` while a transcribe is known-timed-out and the engine was not recycled? (**Must be no.**)
2. Does the API still have the audio after the first local attempt fails? (**Must be yes.**)
3. Does the API command unload/load of the **configured** ASR and TTS selections without reading engine inventory? (**Must be yes.**)
4. Does the client still have the Blob if the API's retry also fails? (**Must be yes.**)
5. Did any skill-gateway code become the lifecycle owner? (**Must be no.**)
