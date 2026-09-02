---
name: qwen-image-generate
description: "BF16 text-to-image via GPU host ComfyUI-video adapter (qwen-image-v1). Use when the user needs a new PNG from a prompt with Qwen Image 2512 — not product SD txt2img."
metadata:
  guideants:
    enabled: true
    display_order: 41
    requires_toolsets: [sandbox]
---

# Qwen Image generate (BF16)

Paths — fixed layout, do not probe or re-derive. The sandbox CWD is the
notebook's **output directory**. This skill's scripts live under
`Skills/qwen-image-generate/scripts/` relative to it, so run the commands in this file
exactly as written. Write every deliverable to the CWD with a **bare filename**
(e.g. `-o gen.png`): never prefix an output path with `Output/`.

Text → PNG on the GPU host. Draft uses `qwen-image-v1` (`image_generate_ready`); high uses
`qwen-image-generate-20-v1` (`image_generate_20_ready`).

## When to use product SD tools instead

Plain notebook txt2img without Qwen/Comfy → use built-in SD tools.

## GPU host gateway (env-configured)

The scripts read `QWEN_IMAGE_SKILL_BASE_URL` and `QWEN_IMAGE_SKILL_TOKEN` from the
guide Environment automatically — do **not** hardcode or export them inline.

Verify before running:

```bash
printenv QWEN_IMAGE_SKILL_BASE_URL >/dev/null && printenv QWEN_IMAGE_SKILL_TOKEN >/dev/null && echo "env ok" || echo "env missing"
```

If either is missing, stop and ask the user to set it in the guide's Environment
variables. Never scan the LAN or guess the GPU host's IP.

## Canvas and quality (named choices only)

Do **not** pass pixel dimensions or sampler numbers (`--width`, `--height`, `--steps`,
`--cfg`, `--lora-strength`). The CLI maps tested profiles internally.

| Flag | Choices | When to use |
|------|---------|-------------|
| `--canvas` | `square` (default), `landscape`, `portrait` | Match user aspect intent |
| `--quality` | `draft` (default), `high` | `draft` = Lightning 4-step (`qwen-image-v1`); `high` = 20-step non-LoRA (`qwen-image-generate-20-v1`, cfg 2.5) |

Use `--quality high` only when the user explicitly asks for higher quality or a
non-Lightning render. Default to `draft`.

Run the CLI **directly** — do not wrap it in `subprocess` or `os.chdir`.

## Preflight

Draft (default):

```bash
python3 Skills/qwen-image-generate/scripts/preflight.py --for generate
```

High quality (when user asked for non-Lightning / quality job):

```bash
python3 Skills/qwen-image-generate/scripts/preflight.py --for generate --quality high
```

Match preflight `--quality` to the generate command you will run. Do **not** treat
`image_generate_ready: false` as a blocker when the user asked for high quality and
`image_generate_20_ready` is true.

## Generate

Square draft (default):

```bash
python3 Skills/qwen-image-generate/scripts/image_tool.py generate \
  "prompt text…" -o gen.png
```

Landscape still (Skynet / film frame):

```bash
python3 Skills/qwen-image-generate/scripts/image_tool.py generate \
  "prompt text…" -o gen.png --canvas landscape
```

High quality (only when user asks):

```bash
python3 Skills/qwen-image-generate/scripts/image_tool.py generate \
  "prompt text…" -o gen.png --quality high --canvas landscape
```

The script prints `jobId=…` to stderr as soon as the job is submitted. On success it
prints one JSON line to stdout with `jobId`, `outputPath`, and `bytes`.

## Polling and timeouts

- `--timeout` — **total** seconds to wait for completion (default 1800). Increase this
  for cold-start loads, not `--poll-seconds`.
- `--poll-seconds` — sleep between status checks (default 5, max 60). Never set this
  high to "wait longer"; that only slows status polling.

If a turn may hit the sandbox execution limit, submit without blocking:

```bash
python3 Skills/qwen-image-generate/scripts/image_tool.py generate \
  "prompt…" -o gen.png --canvas landscape --no-wait
```

Then poll in a follow-up turn:

```bash
python3 Skills/qwen-image-generate/scripts/image_tool.py status <job_id>
python3 Skills/qwen-image-generate/scripts/image_tool.py result <job_id> -o gen.png
```

## Job control

```bash
python3 Skills/qwen-image-generate/scripts/image_tool.py status <job_id>
python3 Skills/qwen-image-generate/scripts/image_tool.py cancel <job_id>
python3 Skills/qwen-image-generate/scripts/image_tool.py result <job_id> -o gen.png
```

## Delivery

When the PNG exists, embed using the notebook tree path from file-change events, e.g.
`![description](Output/gen.png)`. Do not embed bare filenames without the `Output/` prefix.

## Reporting

State output path, job id, canvas, quality, readiness flags used, and preflight evidence
if blocked.
