#!/usr/bin/env python3
"""Styled PlantUML pipeline CLI (BF16 Qwen-Image-Edit via GPU host gateway).

Two distinct stages of the skill, one CLI:

  prepare   split a themed SVG into chrome + text layers, rasterize both 2x
  submit    submit the chrome PNG to the Comfy BF16 edit workflow (ONE call)
  poll      fetch job state ONCE and return (repeat as separate calls)
  result    download a completed job PNG
  overlay   ffmpeg: scale the text PNG to the restyle size, composite labels

Job protocol (mandatory, see SKILL.md):
  - The submit call is the only long-ish call. It writes a sentinel file
    (<chrome-stem>.job.json) holding the job id BEFORE returning.
  - poll fetches status exactly once per call and returns to the assistant.
    NEVER block in a loop for the job's lifetime in one sandbox call.
  - poll exit codes: 0=completed, 1=failed/cancelled, 3=still running.

Stdlib + cairosvg (prepare) + ffmpeg (overlay, shell) + PIL (dimension read).
Requires QWEN_IMAGE_SKILL_BASE_URL + QWEN_IMAGE_SKILL_TOKEN (guide Environment).
"""
from __future__ import annotations

import argparse
import json
import os
import re
import subprocess
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from skill_gateway_client import (  # noqa: E402
    fail_http,
    fetch_capabilities,
    gateway_download,
    gateway_request,
    gateway_request_multipart,
    require_gateway,
    using_skill_gateway,
)
import split_plantuml_svg  # noqa: E402
import rasterize_svg  # noqa: E402

WORKFLOW = "qwen-image-edit-bf16-v1"
READY_FLAG = "image_edit_bf16_ready"
HEX_UUID = re.compile(r"^[0-9a-f]{32}$")

PROMPT = (
    "Add subtle styling to the provided diagram to make it more visually "
    "appealing and professional and include a lemon-chiffon gradient "
    "background. Preserve all original layout and do not add new elements"
)
NEGATIVE = "extra elements, text"

# Locked sampler profile (accepted 2026-09-10, AC-E1 + d08 random-seed batch).
# See references/parameters.md for the denoise quantization finding.
PROFILE = {
    "steps": 4,
    "cfg": 4.0,
    "denoise": 0.8,
    "shift": 3.1,
    "megapixels": 2.0,
    "lora_strength": 0.5,
}
RASTER_SCALE = 2.0
DEFAULT_POLL_SLEEP = 45  # seconds; one sleep + one fetch per poll call


def _die(msg: str) -> None:
    sys.stderr.write(msg + "\n")
    sys.exit(1)


def _random_seed() -> int:
    return int.from_bytes(os.urandom(4), "big")


def _sentinel_for(chrome_png: Path) -> Path:
    return chrome_png.with_suffix(".job.json")


# ---------------------------------------------------------------- prepare ---
def cmd_prepare(args: argparse.Namespace) -> None:
    src = Path(args.svg)
    if not src.is_file():
        _die(f"missing SVG: {src}")
    stem_dir = src.parent
    chrome_svg = stem_dir / f"{src.stem}.chrome.svg"
    text_svg = stem_dir / f"{src.stem}.text.svg"
    chrome_png = stem_dir / f"{src.stem}.chrome-{args.scale:g}x.png"
    text_png = stem_dir / f"{src.stem}.text-{args.scale:g}x.png"

    split_plantuml_svg.split_svg(src, chrome_svg, text_svg)
    rasterize_svg.main_like(text_svg, text_png, args.scale, True)
    rasterize_svg.main_like(chrome_svg, chrome_png, args.scale, False)

    import xml.etree.ElementTree as ET  # noqa: E402
    ns = "{http://www.w3.org/2000/svg}"
    text_nodes = len(list(ET.parse(text_svg).getroot().iter(ns + "text")))
    report = {
        "svg": str(src),
        "chrome_svg": str(chrome_svg),
        "text_svg": str(text_svg),
        "chrome_png": str(chrome_png),
        "text_png": str(text_png),
        "raster_scale": args.scale,
        "text_nodes": text_nodes,
    }
    print(json.dumps(report, indent=2))
    if text_nodes == 0:
        _die("text layer has 0 <text> nodes: the source SVG rasterized its "
             "labels into paths. Labels will be LOST in the composite. "
             "Regenerate the SVG with real text nodes (Step 1 of the skill).")


# ---------------------------------------------------------------- submit ----
def cmd_submit(args: argparse.Namespace) -> None:
    if not using_skill_gateway():
        require_gateway()
    chrome_png = Path(args.chrome)
    if not chrome_png.is_file():
        _die(f"missing chrome PNG: {chrome_png}")
    caps = fetch_capabilities()
    if caps.get(READY_FLAG) is not True:
        _die(f"{READY_FLAG} is not true on the gateway: {caps.get(READY_FLAG)}")
    seed = args.seed if args.seed is not None else _random_seed()
    params = dict(PROFILE, seed=seed)
    output_name = args.output_name or f"{chrome_png.stem}.restyled.png"
    fields = {
        "prompt": PROMPT,
        "output_filename": output_name,
        "workflow_version": WORKFLOW,
        "negative_prompt": NEGATIVE,
        "parameters": json.dumps(params, separators=(",", ":")),
    }
    files = {"source": (chrome_png.name, chrome_png.read_bytes(), "image/png")}
    body = gateway_request_multipart("/v1/image/jobs", fields, files)
    submit = json.loads(body.decode())
    job_id = str(submit.get("jobId") or "")
    if not HEX_UUID.fullmatch(job_id):
        _die(f"invalid jobId in submit response: {submit}")
    sentinel = _sentinel_for(chrome_png)
    sentinel.write_text(json.dumps({
        "job_id": job_id,
        "source": str(chrome_png),
        "output_name": output_name,
        "workflow": WORKFLOW,
        "params": params,
        "submitted_at": time.strftime("%Y-%m-%d %H:%M:%S"),
        "state": "submitted",
    }, indent=2))
    print(json.dumps({"job_id": job_id, "seed": seed, "params": params,
                      "sentinel": str(sentinel), "state": submit.get("state")},
                     indent=2))


# ----------------------------------------------------------------- poll -----
def cmd_poll(args: argparse.Namespace) -> None:
    if not using_skill_gateway():
        require_gateway()
    sentinel = Path(args.sentinel)
    if not sentinel.is_file():
        _die(f"missing sentinel: {sentinel}")
    data = json.loads(sentinel.read_text())
    job_id = data["job_id"]
    if args.sleep:
        time.sleep(args.sleep)
    raw = gateway_request(f"/v1/image/jobs/{job_id}", timeout=60)
    job = json.loads(raw.decode())
    state = str(job.get("state", "")).lower()
    prog = job.get("progress") or {}
    data["state"] = state
    sentinel.write_text(json.dumps(data, indent=2))
    print(json.dumps({
        "job_id": job_id,
        "state": state,
        "phase": prog.get("phase"),
        "message": prog.get("message"),
        "step": f"{prog.get('step')}/{prog.get('max_steps')}",
        "result_available": job.get("result_available"),
        "error": job.get("error"),
    }))
    if state == "completed":
        sys.exit(0)
    if state in ("failed", "cancelled"):
        sys.exit(1)
    sys.exit(3)  # still running: return to the assistant, poll again later


# --------------------------------------------------------------- result -----
def cmd_result(args: argparse.Namespace) -> None:
    if not using_skill_gateway():
        require_gateway()
    sentinel = Path(args.sentinel)
    if not sentinel.is_file():
        _die(f"missing sentinel: {sentinel}")
    data = json.loads(sentinel.read_text())
    job_id = data["job_id"]
    out = Path(args.output)
    payload = gateway_download(f"/v1/image/jobs/{job_id}/result")
    if not payload or len(payload) < 8:
        _die("adapter returned an empty result")
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_bytes(payload)
    data["state"] = "downloaded"
    data["result_path"] = str(out)
    sentinel.write_text(json.dumps(data, indent=2))
    print(json.dumps({"job_id": job_id, "output": str(out), "bytes": len(payload)}))


# -------------------------------------------------------------- overlay -----
def cmd_overlay(args: argparse.Namespace) -> None:
    restyle = Path(args.restyle)
    text = Path(args.text)
    if not restyle.is_file():
        _die(f"missing restyle PNG: {restyle}")
    if not text.is_file():
        _die(f"missing text PNG: {text}")
    # Read the ACTUAL restyle dimensions. The adapter rescales the output to
    # the megapixels target (e.g. 1350x1166 -> 1552x1344 at 2.0 MP); the text
    # layer must be scaled to these, never assumed.
    import PIL.Image  # noqa: E402
    w, h = PIL.Image.open(restyle).size
    out = Path(args.output)
    filt = (f"[1:v]scale={w}:{h}:flags=lanczos,format=rgba[txt];"
            f"[0:v]format=rgba[bg];[bg][txt]overlay=0:0")
    r = subprocess.run(
        ["ffmpeg", "-y", "-hide_banner", "-loglevel", "error",
         "-i", str(restyle), "-i", str(text),
         "-filter_complex", filt, str(out)],
        capture_output=True, text=True)
    if r.returncode != 0:
        _die(f"ffmpeg overlay failed rc={r.returncode}: {r.stderr[:500]}")
    if out.stat().st_size < 8:
        _die("empty overlay output")
    print(json.dumps({"output": str(out), "size": f"{w}x{h}",
                      "bytes": out.stat().st_size}))


# ----------------------------------------------------------------- main -----
def main() -> None:
    parser = argparse.ArgumentParser(description="Styled PlantUML pipeline (BF16)")
    sub = parser.add_subparsers(dest="command", required=True)

    p = sub.add_parser("prepare", help="split + rasterize a themed SVG")
    p.add_argument("svg")
    p.add_argument("--scale", type=float, default=RASTER_SCALE)
    p.set_defaults(func=cmd_prepare)

    p = sub.add_parser("submit", help="submit chrome PNG to Comfy (one call)")
    p.add_argument("chrome")
    p.add_argument("--seed", type=int, default=None,
                   help="default: fresh random seed (the variation knob)")
    p.add_argument("--output-name", default=None)
    p.set_defaults(func=cmd_submit)

    p = sub.add_parser("poll", help="fetch job state ONCE, then return")
    p.add_argument("sentinel",
                   help="job.json sentinel written by submit")
    p.add_argument("--sleep", type=int, default=DEFAULT_POLL_SLEEP,
                   help="sleep this many seconds BEFORE the single fetch "
                        "(default 45; 0 = fetch now)")
    p.set_defaults(func=cmd_poll)

    p = sub.add_parser("result", help="download a completed job PNG")
    p.add_argument("sentinel")
    p.add_argument("-o", "--output", required=True)
    p.set_defaults(func=cmd_result)

    p = sub.add_parser("overlay", help="composite text layer onto restyle")
    p.add_argument("restyle")
    p.add_argument("text")
    p.add_argument("-o", "--output", required=True)
    p.set_defaults(func=cmd_overlay)

    args = parser.parse_args()
    try:
        args.func(args)
    except __import__("urllib.error", fromlist=["HTTPError"]).HTTPError as exc:
        fail_http(exc, args.command)


if __name__ == "__main__":
    main()
