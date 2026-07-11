# Phase 8B — Live catalog and hardware qualification report

**Date:** 2026-07-11  
**Branch:** `feature/curated-local-llama` @ `f407a169d0371c465eb9ddfe66abac0250a01a3e`  
**Agent:** qualification-only (no product source edits)  
**Manifest version (worktree):** `2026-07-10` (14 definitions)

## Executive summary

Phase 8B could not complete live Hugging Face or representative runtime qualification on this host. The live 14-repository manifest-drift suite failed fast in `setUpClass` because no Hugging Face token is configured (`GA_LLAMA_LIVE_HF_TOKEN`, `HUGGINGFACE_TOKEN`, or `HF_TOKEN`). Deterministic unit coverage for incomplete-shard rejection passed on the host harness. Local `guideants-ai` images predate the curated catalog routes (`/admin/catalog`, `/runtime/fleet-preset` return HTTP 404; image lacks `catalog/manifest.json`), blocking runtime-lane smoke and all download/load/repair/quant-change work. CPU and CUDA images are not present locally; `nvidia-smi` is unavailable.

---

## Evidence log

### Environment probes

| Probe | Result |
|---|---|
| Host OS | Windows 10.0.26200 |
| Python (host) | 3.11.9 via `py -3.11` |
| RAM | 63.6 GB |
| Disk C: free | 1360.4 GB |
| GPU | AMD Radeon(TM) 8060S Graphics (driver 32.0.31021.5001); no `nvidia-smi` |
| HF token `GA_LLAMA_LIVE_HF_TOKEN` | NOT SET |
| HF token `HUGGINGFACE_TOKEN` | NOT SET |
| HF token `HF_TOKEN` | NOT SET |
| `docker/.env` HF entries | none found |

### Docker images present

| Tag | Digest | Created (UTC) | Notes |
|---|---|---|---|
| `guideants-ai:rocm-latest` | `sha256:f847c2cba3adc645620eb37a182505f35f48d5d41531361acacf9a2fd5812306` | 2026-07-10T21:30:06Z | llama-admin only; no `catalog/` tree |
| `ghcr.io/elumenotion/guideants-ai-vulkan:main` | `sha256:91c85262bc2b61dc5c1f0678f1bc549117833596ab601a522c259268a92588ff` | 2026-07-08T21:18:25Z | pre-curated routes |
| `guideants-ai:cpu-latest` | — | — | **not present** |
| `guideants-ai:cuda13-latest` | — | — | **not present** |

### Commands executed

```text
# Live 14-repo suite (BLOCKED)
py -3.11 -m unittest discover -s docker/build/guideants-ai/llama-admin-service/tests -p "test_live_manifest_drift.py" -v
→ setUpClass ERROR: RuntimeError: BLOCKED: no Hugging Face token in GA_LLAMA_LIVE_HF_TOKEN, HUGGINGFACE_TOKEN, or HF_TOKEN
→ Ran 0 tests, FAILED (errors=1)

# Deterministic harness including live module (44 pass, live error)
py -3.11 -m unittest discover -s docker/build/guideants-ai/llama-admin-service/tests -p "test_*.py" -v
→ 44 ok, 1 setUpClass ERROR (live drift)

# Incomplete shard rejection (deterministic PASS)
py -3.11 -m unittest ...test_quant_grouping.QuantGroupingTests.test_incomplete_shard_set_rejected -v
→ ok

# Host worktree catalog (not container)
py -3.11 -c "from llama_catalog import build_catalog_response; ..."
→ models 14, version 2026-07-10

# ROCm llama-admin smoke (disposable container, port 18088)
docker run ... guideants-ai:rocm-latest /app/llama-admin-service/llama_admin_service.py
GET /health → 200 {"status":"ok",...}
GET /admin/catalog → 404 {"detail":"Not Found"}
GET /runtime/fleet-preset → 404 {"detail":"Not Found"}
GET /router/entries → 200 []

# Vulkan llama-admin smoke (disposable container, port 18089)
docker run ... ghcr.io/elumenotion/guideants-ai-vulkan:main /app/llama-admin-service/llama_admin_service.py
GET /health → 200
GET /admin/catalog → 404
GET /runtime/fleet-preset → 404
GET /router/entries → 200 []
```

---

## Live catalog qualification table (for STATUS.md copy)

| Definition | Repository check | Commit | Quant/shard check | Projector | Result |
|---|---|---|---|---|---|
| `qwen3.6-35b-a3b` | unsloth/Qwen3.6-35B-A3B-GGUF | — | — | mmproj-F16.gguf (manifest) | **BLOCKED** — no HF token |
| `qwen3.6-27b` | unsloth/Qwen3.6-27B-GGUF | — | — | mmproj-F16.gguf | **BLOCKED** — no HF token |
| `qwen3.6-35b-a3b-mtp` | unsloth/Qwen3.6-35B-A3B-MTP-GGUF | — | — | none (MTP) | **BLOCKED** — no HF token |
| `qwen3.6-27b-mtp` | unsloth/Qwen3.6-27B-MTP-GGUF | — | — | none (MTP) | **BLOCKED** — no HF token |
| `qwen3.5-35b-a3b` | unsloth/Qwen3.5-35B-A3B-GGUF | — | — | mmproj-F16.gguf | **BLOCKED** — no HF token |
| `qwen3.5-27b` | unsloth/Qwen3.5-27B-GGUF | — | — | mmproj-F16.gguf | **BLOCKED** — no HF token |
| `qwen3.5-9b` | unsloth/Qwen3.5-9B-GGUF | — | — | mmproj-F16.gguf | **BLOCKED** — no HF token |
| `gemma4-31b` | unsloth/gemma-4-31b-it-GGUF | — | — | mmproj-F16.gguf | **BLOCKED** — no HF token |
| `gemma4-26b-a4b` | unsloth/gemma-4-26b-it-GGUF | — | — | mmproj-F16.gguf | **BLOCKED** — no HF token |
| `gemma4-12b` | unsloth/gemma-4-12b-it-GGUF | — | — | mmproj-F16.gguf | **BLOCKED** — no HF token |
| `gemma4-e4b` | unsloth/gemma-4-4b-it-GGUF | — | — | mmproj-F16.gguf | **BLOCKED** — no HF token |
| `gpt-oss-20b` | unsloth/gpt-oss-20b-GGUF | — | — | none | **BLOCKED** — no HF token |
| `deepseek-r1-14b` | unsloth/DeepSeek-R1-Distill-Qwen-14B-GGUF | — | — | none | **BLOCKED** — no HF token |
| `qwen3-coder-30b` | unsloth/Qwen3-Coder-30B-A3B-Instruct-GGUF | — | — | none | **BLOCKED** — no HF token |

---

## Representative runtime qualification table (for STATUS.md copy)

| Definition | Backend/image | Quant/commit | Required capabilities | Result/evidence |
|---|---|---|---|---|
| `qwen3.6-35b-a3b` | ROCm primary (`guideants-ai:rocm-latest`) | — | install, vision, reasoning, tools, restart, repair, quant change | **BLOCKED** — no HF token; image lacks curated catalog routes and manifest |
| `qwen3.6-35b-a3b-mtp` | ROCm primary | — | install, text, reasoning, tools, MTP, restart | **BLOCKED** — same |
| `gemma4-31b` | ROCm primary | — | install, vision, reasoning, tools | **BLOCKED** — same |
| `deepseek-r1-14b` | ROCm primary | — | install, reasoning, single tool policy | **BLOCKED** — same |
| `qwen3-coder-30b` | ROCm primary | — | install, coding, parallel tools | **BLOCKED** — same |
| `gpt-oss-20b` | ROCm primary | — | install, reasoning, tools | **BLOCKED** — same |

---

## Verbatim report contract

```text
PHASE 8B REPORT
- Environment: images=guideants-ai:rocm-latest@sha256:f847c2cba3adc645620eb37a182505f35f48d5d41531361acacf9a2fd5812306; ghcr.io/elumenotion/guideants-ai-vulkan:main@sha256:91c85262bc2b61dc5c1f0678f1bc549117833596ab601a522c259268a92588ff; cpu-latest=ABSENT; cuda13-latest=ABSENT hardware=AMD Radeon 8060S / driver 32.0.31021.5001 / 63.6GB RAM / no nvidia-smi storage=1360.4GB free on C:
- Live 14 suite command/result: py -3.11 -m unittest discover -s docker/build/guideants-ai/llama-admin-service/tests -p "test_live_manifest_drift.py" -v pass=0 fail=0 blocked=14 (setUpClass RuntimeError: BLOCKED: no Hugging Face token; 0 tests executed)
- Live table: qwen3.6-35b-a3b -> commit=—, quants=—, projector=mmproj-F16.gguf (manifest), result=BLOCKED(no HF token); qwen3.6-27b -> —, —, mmproj-F16.gguf, BLOCKED; qwen3.6-35b-a3b-mtp -> —, —, none, BLOCKED; qwen3.6-27b-mtp -> —, —, none, BLOCKED; qwen3.5-35b-a3b -> —, —, mmproj-F16.gguf, BLOCKED; qwen3.5-27b -> —, —, mmproj-F16.gguf, BLOCKED; qwen3.5-9b -> —, —, mmproj-F16.gguf, BLOCKED; gemma4-31b -> —, —, mmproj-F16.gguf, BLOCKED; gemma4-26b-a4b -> —, —, mmproj-F16.gguf, BLOCKED; gemma4-12b -> —, —, mmproj-F16.gguf, BLOCKED; gemma4-e4b -> —, —, mmproj-F16.gguf, BLOCKED; gpt-oss-20b -> —, —, none, BLOCKED; deepseek-r1-14b -> —, —, none, BLOCKED; qwen3-coder-30b -> —, —, none, BLOCKED
- Repeat-at-commit determinism: BLOCKED (no HF token; live resolution never ran)
- Representative table: qwen3.6-35b-a3b -> quant/commit=—, install=BLOCKED, load=BLOCKED, chat=BLOCKED, tools=BLOCKED, reasoning=BLOCKED, vision/MTP=BLOCKED, restart=BLOCKED, repair=BLOCKED, result=BLOCKED; qwen3.6-35b-a3b-mtp -> —, all BLOCKED; gemma4-31b -> —, all BLOCKED; deepseek-r1-14b -> —, all BLOCKED; qwen3-coder-30b -> —, all BLOCKED; gpt-oss-20b -> —, all BLOCKED
- Quant change: single=BLOCKED(no HF token / no curated image) sharded=BLOCKED(same)
- Corruption repair: BLOCKED(no disposable install performed; no artifact to corrupt)
- Restart durability: API=BLOCKED(not exercised; full stack not running) llama-admin=BLOCKED(disposable container only; no durable operation under test)
- Runtime lanes: CPU=BLOCKED(image absent) CUDA=BLOCKED(image absent; nvidia-smi unavailable) ROCm=PARTIAL_FAIL(health+router/entries 200; /admin/catalog and /runtime/fleet-preset 404; image missing catalog/manifest tree; qwen3.5-9b smoke not run) Vulkan=PARTIAL_FAIL(health+router/entries 200; curated routes 404; image dated 2026-07-08)
- Token/log inspection: clean (no token configured; logs inspected for bearer patterns — none observed)
- Product files touched: none
- Blockers / failures: (1) HF token missing — live drift suite setUpClass RuntimeError; (2) local ROCm/Vulkan images predate curated catalog — /admin/catalog HTTP 404, no /app/llama-admin-service/catalog/ in rocm-latest; (3) CPU/CUDA images not built locally; (4) representative download/load/chat/tool/repair/quant-change not attempted without token and current images
- Deviations / surprises: (1) guideants-ai:rocm-latest built 2026-07-10 but contains only llama_admin_service.py without catalog assets or curated HTTP routes; (2) host worktree manifest validates 14 models at 2026-07-10 but container cannot serve them; (3) deterministic incomplete-shard rejection passes on host (test_incomplete_shard_set_rejected → INCOMPLETE_QUANT_GROUP)
```
