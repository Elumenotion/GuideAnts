# Qwen Image Skills Plan

Status: **Implemented (v1)**  
Date: 2026-08-22  
Owner: ComfyUI-video / sandbox skills  
**Locked:** precision = **BF16 only** (no FP8 skills, defaults, or agent knobs going forward)  
**Locked:** deployment = **PC sandbox → Max LAN skill gateway** (same pattern as audiocpp; not loopback-only / co-located shortcut)  
Related:

- Audiocpp precedent: `samples/skills/audiocpp skills/`, `samples/guides/Audiocpp/`, `docker/build/guideants-ai/audiocpp-skill-gateway/`
- Tested image surface: `docker/build/comfyui-video/` (adapter, client, workflows, catalog)
- Host harnesses: `artifacts/qwen-image-edit/`, `scripts/run-image-edit-acceptance.ps1`
- Deploy wrapper: `guideants-video-stack/` (`comfyui-video.yml`, `.env.example`)
- Skills platform: `docs/skills-execution/DECISIONS.md`, `docs/skills-support-proposal.md`
- Recovery constraints: `artifacts/qwen-image-edit/FAILURE-HANDOFF-20260812.md`

---

## 1. One-sentence mission

Ship a portable **Qwen Image skill pack** (sandbox `SKILL.md` packages) that exposes the
already-tested ComfyUI-video **BF16-only** generate / edit / inpaint workflows the same way
audiocpp skills expose raw audio.cpp — **without** FP8 paths, GuideAntsApi, ServiceModes,
notebook SD providers, or product voice/image UI changes.

---

## 2. Why this is fully defined

| Layer | Audio (done) | Image (done; skills are the missing package) |
|-------|--------------|-----------------------------------------------|
| Engine / graph | `audiocpp_server` in AI container | ComfyUI + pinned Qwen workflow JSON |
| Job / raw API | ASR/TTS/private engine HTTP | Adapter `POST /v1/image/jobs`, `/v1/image/generate/jobs` |
| Python client | skill scripts + gateway client | `guideants_video_client` (`submit_image_*`, poll, materialize) |
| Scenario proof | audiocpp skill scenarios + probe | Artifact `.ps1` harnesses + client/adapter unit tests |
| Sandbox packaging | `samples/skills/audiocpp skills/` | **This plan** |
| Optional LAN side-channel | `audiocpp-skill` gateway on Max `:8112` | **`qwen-image-skill` gateway on Max** (build in Phase 0; see §6) |

Image gen/edit/inpaint are **not** greenfield model work. They are packaging + reachability
+ agent instructions around a proven adapter surface.

---

## 3. Non-goals (lock these)

1. No GuideAntsApi / ServiceModes / LocalSd / cloud image-provider wiring.
2. No Electron notebook UI for Qwen Image.
3. No inventing new Comfy graphs (handoff rule: copy reference workflows only).
4. No Diffusers / Studio primary path.
5. No autoload of weights from skill scripts (models stay in `compose_comfyui_video_models`).
6. No replacing product SD.cpp `/txt2img`/`/img2img`.
7. No talking-head / InfiniteTalk skills in this pack (separate surface; share adapter host only).
8. **No FP8 path going forward.** Skills, defaults, readiness, and Phase 0 work target **BF16
   only**. Existing FP8 workflows/weights may remain on disk briefly for migration, but they
   are not skill surfaces, not recommended defaults, and must not be kept as dual-precision
   product options.

---

## 4. Proven scenarios → skill inventory

### 4.1 Target skills (v1)

Prefer **narrow skills** like audiocpp. Frontmatter `name` must match import materialization
(`Skills/<name>/`).

| Skill `name` | User intent | Workflow id | Cap flag | Client entry |
|--------------|-------------|-------------|----------|--------------|
| `qwen-image` | Ambiguous / probe first | — | BF16 `image_*` flags | `GET …/v1/capabilities` |
| `qwen-image-generate` | Text → PNG | `qwen-image-bf16-v1` *(see §5.1)* | `image_generate_bf16_ready` *(or renamed generate-ready once FP8 is gone)* | `submit_image_generate` |
| `qwen-image-edit` | Image + prompt → PNG | `qwen-image-edit-bf16-v1` | `image_edit_bf16_ready` | `submit_image_edit` |
| `qwen-image-inpaint` | Image + mask + prompt → PNG | `qwen-image-edit-bf16-inpaint-v1` | `image_edit_bf16_inpaint_ready` | `submit_image_edit` + `mask_path` |

**Precision policy:** BF16 only. Do not add FP8 skills, FP8 defaults, or “fp8 vs bf16”
agent choices.

Optional later (quality/speed variants on **BF16**, not precision forks):

| Skill | Workflow | Cap | Notes |
|-------|----------|-----|-------|
| `qwen-image-edit-20` | BF16 20-step edit (promote or rename from today’s 20-step graph) | readiness for that BF16 workflow | Only if still useful after Lightning defaults; still BF16 UNet |

### 4.2 Scenario ↔ acceptance evidence (already exist)

| Scenario | Evidence to treat as AC source of truth |
|----------|-----------------------------------------|
| BF16 edit / overlay restyle | `artifacts/qwen-image-edit/run-overlay-lightning-style-bf16.ps1` |
| BF16 inpaint + mask | `artifacts/qwen-image-edit/run-alpha-mask-completion-bf16.ps1` |
| Generate lightning / full20 (**BF16 runs only**) | `artifacts/qwen-image-edit/run-image-generate.ps1` — treat `-Precision bf16` as the keep path; retire fp8 mode |
| Sandbox client edit acceptance | `scripts/run-image-edit-acceptance.ps1` — retarget from FP8 AC2/AC3 to **BF16** edit workflows |
| Client contract | `docker/build/comfyui-video/client/.../tests/test_client.py` |
| Adapter readiness / params | `docker/build/comfyui-video/adapter/.../tests/test_core.py` |

Skill acceptance must re-run the **same adapter contracts** via skill CLIs from a notebook
sandbox, not re-prove Comfy node graphs.

### 4.3 Default parameters agents should prefer

From adapter defaults (`IMAGE_DEFAULT_PARAMETERS` / workflow overrides):

| Workflow | Defaults agents should start with |
|----------|-----------------------------------|
| Edit BF16 / inpaint BF16 | `steps=4`, `cfg=1`, `denoise=1`, `shift=3.1`, `megapixels=1.6`, Lightning LoRA on |
| Edit BF16 20-step (optional) | `steps=20`, `cfg=4` |
| Generate BF16 | `width`/`height` multiples of 8, default square (~1328); lightning ≈ `steps=4 cfg=1 lora_strength=1`; full20 ≈ `steps=20 cfg=4 lora_strength=0` |

Artifact harnesses sometimes use higher `cfg` / `megapixels` for quality experiments
(e.g. overlay bf16 `cfg=4`, `megapixels=2.0`). Skills should document **adapter defaults**
as safe first call, and list proven experimental knobs in `references/` — not invent new
knobs the adapter rejects.

---

## 5. Gaps to close before / during skill authoring

### 5.1 Generate must become first-class BF16 (FP8 generate exits)

**Fact:** `submit_image_generate` only accepts `qwen-image-v1` (FP8 UNet today). The host
harness achieves bf16 generate by **temporarily rewriting** the mounted workflow UNet
(`qwen_image_2512_fp8_e4m3fn` ↔ `qwen_image_2512_bf16`) then restoring FP8.

That is unacceptable for skills and for a BF16-only future (shared mutable workflow file,
racey, not notebook-safe, preserves FP8 as the “real” default).

**Required prerequisite (Phase 0):**

1. Add `workflows/qwen-image-bf16-v1.json` (same graph as current generate, **BF16 UNet**).
2. Make BF16 the **only** generate workflow the adapter mounts and the client accepts
   (rename readiness to a single generate-ready flag, or keep `image_generate_bf16_ready`
   during cutover then drop the FP8 flag).
3. Point compose / `IMAGE_GENERATE_WORKFLOW_PATH` at the BF16 JSON; stop mounting FP8
   generate as the active path.
4. Delete or archive the FP8 generate workflow from active compose (do not leave a skill-
   selectable FP8 generate).
5. Retarget `run-image-generate.ps1` to BF16-only (remove `-Precision fp8` / dual mode).
6. Retarget edit acceptance away from FP8 `qwen-image-edit-v1` toward
   `qwen-image-edit-bf16-v1`.

Do not ship a skill that mutates workflow JSON on disk. Do not keep “fp8|bf16” as an agent
parameter.

### 5.2 Client packaging in the sandbox

Match audiocpp: each skill ships `scripts/skill_gateway_client.py` plus scenario CLIs
(`image_tool.py`, `probe.py`, `preflight.py`). When `QWEN_IMAGE_SKILL_BASE_URL` is set,
scripts talk to the Max LAN gateway — **not** `127.0.0.1:8190` from a PC sandbox.

**Locked (same pattern as audiocpp):**

| Piece | Role |
|-------|------|
| `skill_gateway_client.py` | Stdlib helper: base URL, token header, multipart submit, optional `/files` staging |
| `image_tool.py` | Scenario CLI: generate / edit / inpaint / status / result / cancel |
| `preflight.py` / `probe.py` | Capabilities + readiness via gateway |

Do **not** assume `guideants_video_client` is on `PYTHONPATH` in the PC sandbox. Do **not**
document loopback `:8190` as the default deployment path. The gateway is the product
surface; adapter loopback is an internal implementation detail on Max.

If useful, the gateway client may mirror `submit_image_*` call shapes from
`guideants_video_client`, but skills vendor their own thin copy — same as audiocpp’s
`engine_tool.py` vs raw `audiocpp_server`.

### 5.3 Notebook path scoping

`guideants_video_client.resolve_notebook_path` requires `.guideants/notebook.json`. Skill
scripts must:

- Run with CWD inside the notebook (normal SEA behavior).
- Accept workspace-relative paths under `Output/…`.
- Refuse path escape (preserve client rule; no “fallback” to host absolute paths outside
  notebook).

Host `.ps1` harnesses that curl absolute Windows paths are **not** the skill runtime model.

---

## 6. Topology — match audiocpp (locked)

Audiocpp skills **default to PC sandbox talking to Max** over a token-gated raw gateway.
Image skills must follow the same contract. A co-located loopback-only path (`:8190` inside
the comfy container) is useful for host harnesses and adapter dev, but it is **not** the
skill deployment model and must not be shipped as v1.

### Audiocpp reference (copy this shape)

| Layer | Audiocpp | Qwen image (target) |
|-------|----------|---------------------|
| Max host token | `GA_AUDIOCPP_SKILL_TOKEN` | `GA_QWEN_IMAGE_SKILL_TOKEN` |
| Internal gateway | `127.0.0.1:8096` (`skill_gateway.py`) | `127.0.0.1:<port>` (new gateway) |
| Nginx prefix | `/audiocpp-skill/` on AI `:8112` | `/qwen-image-skill/` on published Max port |
| PC sandbox env | `AUDIOCPP_SKILL_BASE_URL` + `AUDIOCPP_SKILL_TOKEN` | `QWEN_IMAGE_SKILL_BASE_URL` + `QWEN_IMAGE_SKILL_TOKEN` |
| Auth header | `X-Audiocpp-Skill-Token` | `X-Qwen-Image-Skill-Token` |
| Proxy target | ASR / TTS / private engines | Video adapter (`127.0.0.1:8190` inside comfyui-video) |
| Skill scripts | `skill_gateway_client.py` auto-routes | Same |

### What exists today (gap)

| Binding | Value | Problem for skills |
|---------|--------|-------------------|
| Adapter | `127.0.0.1:8190` inside comfyui-video | Correct internal target for gateway |
| Host publish | `127.0.0.1:${GA_COMFYUI_VIDEO_PORT:-8189}:80` | **Loopback only** — PC cannot reach it |
| Video skill gateway | **Does not exist** | Must build (Phase 0) |

Comfy nginx already exposes `/video/` → adapter. The missing piece is the **token-gated
LAN-facing gateway** plus compose publish — not “run skills inside the comfy container.”

### Phase 0 gateway deliverable (required, not optional)

Build `docker/build/comfyui-video/qwen-image-skill-gateway/` (or shared
`video-skill-gateway/`) modeled on `audiocpp-skill-gateway/`:

1. **Transparent reverse proxy** to adapter paths: `/v1/capabilities`, `/v1/image/jobs`,
   `/v1/image/generate/jobs`, job status/cancel/result, and admin install if skills need it.
2. **`POST /files`** — stage notebook uploads on Max when needed (multipart image jobs may
   stream bytes directly; still provide staging for parity and large files).
3. **Token auth** — `X-Qwen-Image-Skill-Token` == `GA_QWEN_IMAGE_SKILL_TOKEN`; 503 if unset.
4. **Start script + entrypoint** in comfyui-video image (like `start-audiocpp-skill.sh`).
5. **Nginx location** `/qwen-image-skill/` → internal gateway port.
6. **LAN publish** in `guideants-video-stack` — bind `0.0.0.0` (not `127.0.0.1`) on a
   dedicated port, e.g. `${GA_QWEN_IMAGE_SKILL_PORT:-8189}:80`, or front via AI `:8112`
   if you prefer one Max port (document the chosen URL in pack README).
7. **`.env.example`** — `GA_QWEN_IMAGE_SKILL_TOKEN` shared with PC guide env
   `QWEN_IMAGE_SKILL_TOKEN` (mirror audiocpp / `GA_AUDIOCPP_SKILL_TOKEN`).

### PC sandbox required env (locked)

```text
QWEN_IMAGE_SKILL_BASE_URL=http://<max-lan-ip>:<port>/qwen-image-skill
QWEN_IMAGE_SKILL_TOKEN=<same as Max GA_QWEN_IMAGE_SKILL_TOKEN>
```

Skill README and guide `instructions.md` must state: **do not** call `127.0.0.1:8189` or
`:8190` from a PC sandbox — those are inside Max.

Co-located fallback (adapter-direct on `:8190`) may exist only for **host harnesses** and
in-container debugging; skills must not document it as the primary path.

---

## 7. Proposed tree

```text
samples/skills/qwen-image skills/
  README.md
  qwen-image/                         # umbrella (frontmatter name: qwen-image)
    SKILL.md
    scripts/
      probe.py
      preflight.py
      skill_gateway_client.py         # shared gateway helper (audiocpp pattern)
    references/
      adapter-api.md
      workflows.md
      parameters.md
  qwen-image-generate/
    SKILL.md
    scripts/
      preflight.py
      image_tool.py                   # generate | status | result | cancel
      skill_gateway_client.py
  qwen-image-edit/
    SKILL.md
    scripts/
      preflight.py
      image_tool.py                   # edit
      skill_gateway_client.py
  qwen-image-inpaint/
    SKILL.md
    scripts/
      preflight.py
      image_tool.py                   # edit + mask
      skill_gateway_client.py
```

Optional productized guide (phase after pack works), mirroring Audiocpp:

```text
samples/guides/QwenImage/
  instructions.md
  manifest.json
  Skills/   # folder names == frontmatter names
```

### Frontmatter convention

```yaml
---
name: qwen-image-edit
description: "…"
metadata:
  guideants:
    enabled: true
    display_order: 50
    requires_toolsets: [sandbox]
---
```

Descriptions must be discovery-grade (when to pick this skill vs product SD tools).

---

## 8. Script / API contract (skill-facing)

### 8.1 Environment

| Var | Meaning |
|-----|---------|
| `QWEN_IMAGE_SKILL_BASE_URL` | Max LAN gateway base, e.g. `http://<max-lan-ip>:8189/qwen-image-skill` |
| `QWEN_IMAGE_SKILL_TOKEN` | Same secret as Max `GA_QWEN_IMAGE_SKILL_TOKEN` |
| Auth header | `X-Qwen-Image-Skill-Token` on every gateway call |

**Required for PC sandbox** (same contract as audiocpp). Scripts exit with a clear error if
`QWEN_IMAGE_SKILL_BASE_URL` is set but token is missing.

Gateway paths mirror adapter paths under the base:

```text
{BASE}/v1/capabilities
{BASE}/v1/image/jobs
{BASE}/v1/image/generate/jobs
{BASE}/v1/image/jobs/{id}
{BASE}/v1/image/jobs/{id}/cancel
{BASE}/v1/image/jobs/{id}/result
{BASE}/files
```

Do **not** document `GUIDEANTS_VIDEO_ADAPTER_URL` or loopback `:8190`/`:8189` as the skill
default. Host `.ps1` harnesses may still use loopback for adapter dev; skills do not.

### 8.2 CLI surface (mirror `engine_tool.py`)

```bash
# Probe / preflight
python3 Output/Skills/qwen-image/scripts/probe.py
python3 Output/Skills/qwen-image-edit/scripts/preflight.py --for edit-bf16

# Generate
python3 Output/Skills/qwen-image-generate/scripts/image_tool.py generate \
  "prompt…" -o Output/gen.png \
  [--width 1328 --height 1328] [--steps 4 --cfg 1] [--seed 42] \
  [--workflow qwen-image-bf16-v1]

# Edit
python3 Output/Skills/qwen-image-edit/scripts/image_tool.py edit \
  Output/uploads/source.png "prompt…" -o Output/edit.png \
  [--workflow qwen-image-edit-bf16-v1] [--megapixels 1.6] …

# Inpaint
python3 Output/Skills/qwen-image-inpaint/scripts/image_tool.py inpaint \
  Output/uploads/source.png Output/uploads/mask.png "prompt…" -o Output/inpaint.png

# Job control
python3 …/image_tool.py status <job_id>
python3 …/image_tool.py cancel <job_id>
python3 …/image_tool.py result <job_id> -o Output/out.png
```

Behavior:

1. Preflight reads capabilities; exit non-zero if required flag is false; print missing
   models/workflow keys from adapter details (no guessing).
2. Submit returns `jobId`; poll until terminal state; materialize PNG under `Output/`.
3. Timeouts: long (first BF16 load can be many minutes). Default poll budget ≥ 1800s with
   resume-friendly status command (audiocpp “~5 min script budget” does **not** apply to
   cold BF16 UNet load — document honestly).
4. Always report path used, job id, readiness flags, and output path.

### 8.3 Adapter endpoints (reference)

| Action | Method / path |
|--------|----------------|
| Caps | `GET /v1/capabilities` |
| Edit / inpaint | `POST /v1/image/jobs` (multipart: `source`, optional `mask`, fields) |
| Generate | `POST /v1/image/generate/jobs` |
| Status | `GET /v1/image/jobs/{id}` |
| Cancel | `POST /v1/image/jobs/{id}/cancel` |
| Result | `GET /v1/image/jobs/{id}/result` (client materialize helper) |

Do not call ComfyUI `/prompt` directly from skills.

---

## 9. SKILL.md content requirements

Each skill body must include:

1. **When to use product SD tools instead** (plain notebook image with no Qwen/Comfy need).
2. **Required env: PC → Max gateway** (§6, §8.1) — same deployment assumption as audiocpp.
3. **Preflight first** — trust probe over docs.
4. Exact commands with `Output/Skills/<name>/scripts/…`.
5. Workflow id + readiness flag.
6. Input constraints (PNG preferred; mask: white=editable / black=preserve for inpaint).
7. Output always under `Output/`.
8. Honest limits (VRAM, cold-load time, single-host queue, no UI picker).
9. Reporting rule: what worked / what was blocked with preflight evidence.

Umbrella `qwen-image` routes:

| User wants… | Skill |
|-------------|--------|
| New image from text | `qwen-image-generate` |
| Restyle / edit existing image | `qwen-image-edit` |
| Masked fill / whiteboard completion | `qwen-image-inpaint` |
| Unclear / “is Max ready?” | `qwen-image` probe |

---

## 10. Implementation phases

### Phase 0 — Prerequisites (adapter + gateway, not skills)

- [x] Promote BF16 generate to the **sole** generate workflow + client + compose mount (§5.1).
- [x] Remove FP8 generate from active mounts/client acceptance; stop dual-precision harnesses.
- [x] **Build and ship `qwen-image-skill` gateway** on comfyui-video (§6) — token, nginx,
      LAN publish, `.env.example` — **before** skill pack work.
- [x] Confirm BF16 edit/inpaint/generate weights on Max volume; capabilities green via
      **gateway URL** from a PC (or LAN test client), not loopback-only.
- [x] Retarget `scripts/run-image-edit-acceptance.ps1` to BF16 edit.
- [x] Document `QWEN_IMAGE_SKILL_BASE_URL` + `QWEN_IMAGE_SKILL_TOKEN` in pack README and
      `guideants-video-stack/README.md` (mirror audiocpp section).

**Exit:** PC-reachable gateway returns capabilities with all v1 BF16 flags ready; no FP8
skill/default path; no workflow JSON mutation on disk.

### Phase 1 — Skill pack skeleton

- [x] Create `samples/skills/qwen-image skills/` tree + pack `README.md`.
- [x] Write four `SKILL.md` files (discovery descriptions + rules).
- [x] Shared `skill_gateway_client.py` + per-skill `preflight.py` / `image_tool.py`.
- [x] `references/adapter-api.md`, `workflows.md`, `parameters.md`.

**Exit:** import zip into a guide; notebook materializes `Output/Skills/.../scripts`.

### Phase 2 — Scenario parity (PC sandbox → Max gateway)

Re-run each proven scenario **through skill CLIs** from a **PC sandbox** with gateway env set:

| AC | Scenario | Pass criteria | Status |
|----|----------|---------------|--------|
| AC-G1 | Generate bf16 lightning | PNG in `Output/`, non-empty, job succeeded | **Pass** — `image-gen-skill-ac-g1.png` (~7.3 min cold load) |
| AC-G2 | Generate bf16 full20 (optional) | same | Not run (optional) |
| AC-E1 | Edit bf16 (office / overlay-style prompt) | PNG; preflight `image_edit_bf16_ready` | **Pass** — overlay harness + `skill-cli-edit.png` via gateway |
| AC-I1 | Inpaint bf16 with mask | PNG; mask required; flag `image_edit_bf16_inpaint_ready` | **Pass** — `skill-ac-i1.png` harness + skill CLI inpaint |
| AC-P1 | Probe/preflight fails closed when caps false | non-zero + clear missing list | **Pass** — unit tests + live probe open |
| AC-P2 | Path escape rejected | error; no write outside notebook | **Pass** — `test_path_escape_rejected` |

Keep host `.ps1` harnesses as regression oracles; do not delete them.

### Phase 3 — Guide bootstrap (recommended; mirror Audiocpp)

- [x] `samples/guides/QwenImage/` with `instructions.md` routing table and **PC→Max env**
      block (copy shape from `samples/guides/Audiocpp/instructions.md`).
- [x] Seed/import docs for lab use.
- [x] Environment variable checklist in guide instructions.

### Phase 4 — Hardening

- [x] Unit tests for skill HTTP helpers (mock adapter).
- [x] Sync helper copies across skills (or single shared module strategy).
- [x] Document cold-start / VRAM coexistence with InfiniteTalk (unload / sequential jobs).
- [x] Explicit “do not invent graphs” pointer to failure handoff.

---

## 11. Mapping to audiocpp lessons (do / don’t)

| Do (copy) | Don’t (avoid) |
|-----------|----------------|
| Narrow skills + umbrella probe | One mega-skill that hides all modes |
| Preflight before work | Trust stale docs over live capabilities |
| Env-documented PC→Max gateway story | Loopback-only or “run inside comfy container” shortcuts |
| Deliverables in `Output/` | Claim product UI / notebook image button support |
| Honest blocked reporting | Silent fallbacks / alternate graphs / guessed weights |
| Side-channel only | ServiceModes / API lifecycle changes for v1 |
| Consent-style rules where relevant | N/A for images; still refuse unsafe “forge identity photo” requests if product policy says so — call out in SKILL if needed |

---

## 12. Explicit do-nots (from image recovery)

From `FAILURE-HANDOFF-20260812.md`, still binding:

1. Do not invent edit/generate graphs.
2. Do not make Diffusers the primary path.
3. Do not delete model volume files without approval.
4. Do not “fix” readiness by guessing weights from disk inventory into ServiceModes
   (N/A to skills, but do not add skill-side autoload that bypasses adapter readiness).

Skills call the adapter; adapter owns readiness.

---

## 13. Review checklist (approve / amend)

Please mark each:

| # | Decision | Status |
|---|----------|--------|
| R1 | v1 skill set = umbrella + generate + edit + inpaint (all BF16) | **Locked** |
| R2 | Phase 0: BF16-only generate workflow; retire FP8 generate path | **Locked** |
| R3 | Topology = PC sandbox → Max LAN gateway (match audiocpp) | **Locked** |
| R4 | Client = `skill_gateway_client.py` + `image_tool.py` per skill (audiocpp pattern) | **Locked** |
| R5 | Pack path `samples/skills/qwen-image skills/` | Proposed |
| R6 | Guide bootstrap `samples/guides/QwenImage/` in same delivery as skills | Proposed (mirror Audiocpp) |
| R7 | Precision = BF16 only | **Locked** |
| R8 | Optional later: BF16 20-step edit skill | Not v1 |
| R9 | Gateway env names: `QWEN_IMAGE_SKILL_*` / `GA_QWEN_IMAGE_SKILL_TOKEN` | **Locked** (audiocpp parallel) |
| R10 | LAN port: dedicated `GA_QWEN_IMAGE_SKILL_PORT` vs single `:8112` prefix | **Open** — pick one URL shape and document |

---

## 14. Suggested first implementation slice (after approval)

1. Phase 0: BF16 adapter cutover **and** `qwen-image-skill` gateway (PC-reachable).
2. Phase 1 pack skeleton + edit skill; AC-E1 from **PC sandbox** via gateway.
3. Generate + inpaint skills; AC-G1 + AC-I1 from PC sandbox.
4. Umbrella probe + pack README + `guideants-video-stack` docs.
5. Phase 3 guide bootstrap (Audiocpp-shaped `instructions.md`).
6. Hardening + unit tests for gateway client.

---

## 15. File index (implementers)

| Role | Path |
|------|------|
| Edit BF16 workflow | `docker/build/comfyui-video/workflows/qwen-image-edit-bf16-v1.json` |
| Inpaint BF16 workflow | `docker/build/comfyui-video/workflows/qwen-image-edit-bf16-inpaint-v1.json` |
| Generate (FP8 legacy; replace in Phase 0) | `docker/build/comfyui-video/workflows/qwen-image-v1.json` |
| Generate BF16 (Phase 0 deliverable) | `docker/build/comfyui-video/workflows/qwen-image-bf16-v1.json` |
| Adapter | `docker/build/comfyui-video/adapter/guideants_video_adapter/` |
| Client | `docker/build/comfyui-video/client/guideants_video_client/` |
| Compose mounts | `guideants-video-stack/comfyui-video.yml`, `docker/compose/comfyui-video-*.yml` |
| Audiocpp pack template | `samples/skills/audiocpp skills/` |
| Audiocpp guide template | `samples/guides/Audiocpp/` |
| Audiocpp gateway template | `docker/build/guideants-ai/audiocpp-skill-gateway/` |
| Qwen image gateway (Phase 0) | `docker/build/comfyui-video/qwen-image-skill-gateway/` *(new)* |

---

## 16. Success definition

v1 is done when a sandbox agent, using only imported Qwen Image skills and preflight, can:

1. Detect Max/comfy **BF16** image readiness from capabilities.
2. Produce a BF16 text-to-image PNG (no FP8 generate path available).
3. Produce a BF16 image-edit PNG from a notebook-scoped source.
4. Produce a BF16 inpaint PNG from source + mask.
5. Report failures with capability evidence when weights/workflows are missing.

No product API changes. No FP8 skill/default path. No new Comfy graphs beyond Phase 0
BF16 generate promotion and FP8 generate retirement from the active surface.
