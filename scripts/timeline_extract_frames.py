#!/usr/bin/env python3
"""Extract video frames at timeline event positions for walkthrough review."""

from __future__ import annotations

import argparse
import json
import subprocess
import sys
from pathlib import Path
from typing import Any

KEY_KINDS = {
    "navigate",
    "pointer.move",
    "pointer.label",
    "ui.hover",
    "idle.start",
    "idle.end",
}


def _video_time_ms(event_t_ms: int, lead_in_ms: int) -> int:
    return lead_in_ms + event_t_ms


def _frame_number(video_time_ms: int, fps: int) -> int:
    return max(0, round(video_time_ms * fps / 1000))


def _slug(text: str, max_len: int = 40) -> str:
    out = "".join(ch if ch.isalnum() else "_" for ch in text).strip("_")
    while "__" in out:
        out = out.replace("__", "_")
    return out[:max_len] or "event"


def extract_frames(run_dir: Path, *, kinds: set[str] | None = None) -> list[dict[str, Any]]:
    timeline_path = run_dir / "timeline.json"
    if not timeline_path.is_file():
        raise FileNotFoundError(f"Missing timeline: {timeline_path}")

    timeline = json.loads(timeline_path.read_text(encoding="utf-8"))
    video_path = Path(timeline["video"]["path"])
    if not video_path.is_file():
        video_path = run_dir / "video.mp4"
    if not video_path.is_file():
        raise FileNotFoundError(f"Missing video in {run_dir}")

    clock = timeline.get("clock", {})
    lead_in_ms = int(clock.get("recording_lead_in_ms", 0))
    fps = int(clock.get("fps", 30))

    out_dir = run_dir / "frames"
    out_dir.mkdir(parents=True, exist_ok=True)

    selected_kinds = kinds or KEY_KINDS
    manifest: list[dict[str, Any]] = []

    for index, event in enumerate(timeline.get("events", [])):
        kind = str(event.get("kind", ""))
        if kind not in selected_kinds:
            continue

        t_ms = int(event.get("t_ms", 0))
        video_ms = _video_time_ms(t_ms, lead_in_ms)
        frame = _frame_number(video_ms, fps)
        label_parts = [kind]
        if event.get("label"):
            label_parts.append(str(event["label"]))
        elif event.get("target"):
            label_parts.append(str(event["target"]))
        elif event.get("phase"):
            label_parts.append(str(event["phase"]))
        elif event.get("reason"):
            label_parts.append(str(event["reason"]))

        slug = _slug("_".join(label_parts))
        filename = f"{index:03d}_f{frame:05d}_{slug}.png"
        output = out_dir / filename

        timestamp = video_ms / 1000.0
        cmd = [
            "ffmpeg",
            "-y",
            "-ss",
            f"{timestamp:.3f}",
            "-i",
            str(video_path),
            "-frames:v",
            "1",
            "-q:v",
            "2",
            str(output),
        ]
        subprocess.run(cmd, check=True, capture_output=True)

        entry = {
            "index": index,
            "kind": kind,
            "t_ms": t_ms,
            "video_ms": video_ms,
            "frame": frame,
            "timestamp_sec": round(timestamp, 3),
            "path": str(output),
            "event": {k: v for k, v in event.items() if k != "kind"},
        }
        manifest.append(entry)

    manifest_path = out_dir / "manifest.json"
    manifest_path.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    return manifest


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("run_dir", type=Path, help="Walkthrough run directory")
    parser.add_argument(
        "--kinds",
        nargs="*",
        help="Event kinds to extract (default: key presentation kinds)",
    )
    args = parser.parse_args(argv)
    kinds = set(args.kinds) if args.kinds else None
    manifest = extract_frames(args.run_dir, kinds=kinds)
    print(f"Extracted {len(manifest)} frames -> {args.run_dir / 'frames'}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
