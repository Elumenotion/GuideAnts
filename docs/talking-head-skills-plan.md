# Talking-Head Skills Plan

Status: **Implemented (v1 scaffolding + adapter contract)**  
Date: 2026-08-23  
Owner: ComfyUI-video / sandbox skills  

**Locked:**
- Deployment = **PC sandbox → Max LAN skill gateway** (same pattern as audiocpp / qwen-image)
- **No v2v** skill or recommended client path going forward
- Client submits **avatar + audio + background**; one long-running **queued job**
- Comfy workflow **`infinitetalk-i2v-v1` preserved** (composite is post-process)
- Console telemetry = meaningful workflow progress only (no routing spam)

Related:
- Image precedent: `docs/qwen-image-skills-plan.md`, `samples/skills/qwen-image skills/`
- Adapter: `docker/build/comfyui-video/adapter/`
- Gateway: `docker/build/comfyui-video/talking-head-skill-gateway/`
- Skills: `samples/skills/talking-head skills/`, `samples/guides/TalkingHead/`
- Deploy: `guideants-video-stack/comfyui-video.yml`

---

## 1. Mission

Ship a portable **talking-head skill pack** that submits InfiniteTalk i2v through the
ComfyUI-video adapter, then CorridorKey-composites with the caller-supplied background,
as a single queueable job — without GuideAntsApi / ServiceModes / notebook product UI.

## 2. API shape

```
POST /v1/talking-head/jobs
  multipart: source (avatar), audio, background (plate)
  form: output_filename (.mp4), parameters (incl. seed), prompts
→ 202 { jobId, seed, state, progress, … }

GET  /v1/talking-head/jobs/{id}
POST /v1/talking-head/jobs/{id}/cancel
GET  /v1/talking-head/jobs/{id}/result   # delivery MP4
```

Phases: `queued` → `uploading` / `waiting` / `sampling` → `compositing` → `completed`.

## 3. SEA packaging

| Skill | Role |
|-------|------|
| `talking-head` | Probe, preflight, and job CLI (`scripts/video_tool.py`) |

Env (PC guide Environment):

```
TALKING_HEAD_SKILL_BASE_URL=http://<max-lan-ip>:8189/talking-head-skill
TALKING_HEAD_SKILL_TOKEN=<same as Max GA_TALKING_HEAD_SKILL_TOKEN>
```

Gateway: Max nginx `/talking-head-skill/` → loopback `:8098` → adapter `:8190`.

## 4. Telemetry rules

Adapter stdout logs only when `should_log_progress` sees a change in  
`(phase, message, node_class, step, max_steps, queue_position, seed)`.

Skill CLI: same; 60s heartbeat when idle; seed on accept / progress / complete.

## 5. Acceptance checklist

| AC | Proof | Status |
|----|-------|--------|
| AC-T0 | Unit: telemetry dedupe, composite runner, client multipart+background | Code landed |
| AC-T1 | Live: docker logs show sampling steps, not status spam; Comfy output visible | Needs container recreate |
| AC-T2 | Queue: second job shows queue_position while first runs | Needs live |
| AC-T3 | Adapter job avatar+audio+background → delivery mp4 + seed in status | **PASS** (job `902fc149…`, seed 1448790825, ~52 min) |
| AC-T4 | PC skill CLI via gateway → `Output/*.mp4` + run-meta.json | Needs live |
| AC-T5 | Fail-closed: missing token / background / path escape | Unit + live |
| AC-T6 | Distinct random seeds → distinct outputs (spot-check) | Needs live |

## 6. Non-goals

1. No ServiceModes / GuideAntsApi product wiring  
2. No talking-head v2v skills  
3. No edits to InfiniteTalk Comfy JSON topology  
4. No host CorridorKey as the SEA path (composite runs inside the Max job)

## 7. Operator notes

- CorridorKey must exist at `VIDEO_CORRIDORKEY_ROOT` (default  
  `/opt/guideants/corridorkey-local/CorridorKey`) with pinned checkpoint.  
- Recreate `guideants-video-comfyui` after compose/nginx/env changes so the  
  talking-head gateway starts and mounts apply.  
- Golden AC assets: doug avatar, `may_5_cover_10s.wav`, whiteboard plate, ~10s.
