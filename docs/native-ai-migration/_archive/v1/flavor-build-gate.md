# Per-Flavor Build/Smoke Gate — Native AI Migration

Companion to [`00-overview.md`](./00-overview.md). Run after any phase that touches a
`Dockerfile.*`, `entrypoint.sh`, `start-*.sh`, `*-requirements.txt`, or a compose file.

The migration ships four full image flavors that use **different GPU stacks**
(cuda13/vulkan/rocm/cpu). A change that builds on cuda13 can easily break vulkan or rocm —
especially the new native `ga-audio-server`, which is compiled per flavor and, on rocm,
runs the **Vulkan** build (D4). This gate proves each flavor still builds and boots healthy.

Reference: `Dockerfile.cuda:221-222` `HEALTHCHECK` curls `/asr/health`, `/tts/health`,
`/emb/health`. `Dockerfile.slim` is **out of scope** (it ships none of these services).

---

## 1. Gate intent

Pass when, for **each** of cuda13 / vulkan / rocm / cpu:

- The image **builds** cleanly (including the new per-flavor audio.cpp builder stage in
  Phases 2–3 and any venv slimming in Phases 1/4).
- The container **boots** and the `HEALTHCHECK` passes — every affected engine answers
  `/health` and, when auto-load is on, reaches `/ready` within `_READY_TIMEOUT_SECONDS`.
- The **backend actually used** matches the flavor's intent (no silent CPU degrade):
  - cuda13 → CUDA; vulkan → Vulkan; **rocm → Vulkan** (D4, Option A) or hipified CUDA
    (D4, Option B) — **never** silently CPU. If a flavor must ship CPU-backed for a task
    (e.g. unproven Vulkan op coverage), that is **explicitly logged + documented**, not
    hidden.
  - cpu → CPU (the intended state).
- The **supervised process set** is correct for the phase (emb child :18085 present after
  Phase 1; `ga-audio-server` after Phase 2; TTS task after Phase 3; one `ga-admin` and the
  four old services gone after Phase 4). Clean SIGTERM shutdown via `shutdown_all`; no
  orphaned processes.

---

## 2. Backend/flavor matrix (target state)

| Flavor | Base image (llama-server) | audio.cpp build | Engine backend | Notes |
|---|---|---|---|---|
| cuda13 | `server-cuda13` | `ENGINE_ENABLE_CUDA=ON` | CUDA | primary; align `CMAKE_CUDA_ARCHITECTURES` with SD builder `75;80;86;89;90` |
| vulkan | `server-vulkan` | `ENGINE_ENABLE_VULKAN=ON` | Vulkan | first GPU ASR/TTS/emb where torch was CPU-only; validate qwen3_asr/TTS op coverage |
| rocm | `server-rocm` | Vulkan build (D4-A) | Vulkan (RADV) | needs Vulkan ICD/loader in the rocm runtime image; llama-server stays ROCm |
| cpu | `server` (CPU) | both OFF | CPU | throughput check vs prior CPU-torch path |

---

## 3. Procedure

1. Build `docker/build/guideants-ai/Dockerfile.{cpu,cuda,rocm,vulkan}` (pin audio.cpp by
   ref, mirroring the SD builder-stage caching pattern).
2. Boot each with the phase's env (`GA_{ASR,TTS,EMB}_AUTO_LOAD_ON_STARTUP=1`,
   `_WAIT_FOR_READY_ON_STARTUP=1`); confirm `HEALTHCHECK` green.
3. Smoke each affected engine once (one `/emb/embed`, `/asr/transcribe`, `/tts/synthesize`,
   or `/sd/txt2img` call) to prove the native path runs on the intended backend.
4. Confirm the backend in the engine's own startup log (CUDA/Vulkan/ROCm/CPU) matches the
   matrix — capture the log line as evidence.
5. Record image size + process count vs the pre-migration baseline in
   [`STATUS.md`](./STATUS.md).

---

## 4. Report-back addition (phases touching build)

```text
FLAVOR BUILD/SMOKE GATE (Phase N):
- Builds green (cuda13/vulkan/rocm/cpu): <p/p/p/p>
- HEALTHCHECK green each flavor: <p/p/p/p>
- Backend used matches intent (no silent CPU degrade): <per-flavor + log ref>
- Process set correct + clean shutdown, no orphans: <pass/fail>
- Image size / process count vs baseline: <numbers>
```
