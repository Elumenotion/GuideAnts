#!/usr/bin/env python3
"""Compile timeline.json events into active/idle segments and edit hints."""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path
from typing import Any

ACTIVE_KINDS = {
    "pointer.move",
    "pointer.label",
    "ui.hover",
    "ui.click",
    "ui.fill",
    "typing.start",
    "typing.char",
    "typing.end",
    "dom.mutation",
    "navigate",
}


def _load_timeline(path: Path) -> dict[str, Any]:
    return json.loads(path.read_text(encoding="utf-8"))


def _planned_idle_ranges(events: list[dict[str, Any]]) -> list[dict[str, Any]]:
    segments: list[dict[str, Any]] = []
    open_idle: dict[str, Any] | None = None

    for event in events:
        kind = event.get("kind")
        t_ms = int(event.get("t_ms", 0))
        if kind == "idle.start":
            open_idle = {
                "kind": "idle",
                "t_start_ms": t_ms,
                "reason": event.get("reason"),
                "planned": bool(event.get("planned", True)),
            }
        elif kind == "idle.end" and open_idle is not None:
            open_idle["t_end_ms"] = t_ms
            segments.append(open_idle)
            open_idle = None

    return segments


def _active_ranges(events: list[dict[str, Any]], duration_ms: int) -> list[dict[str, Any]]:
    if not events:
        return []

    points = sorted(
        {
            int(event.get("t_ms", 0))
            for event in events
            if event.get("kind") in ACTIVE_KINDS
        }
    )
    if not points:
        return []

    segments: list[dict[str, Any]] = []
    gap_threshold = 350
    start = points[0]
    prev = points[0]

    for t_ms in points[1:]:
        if t_ms - prev > gap_threshold:
            segments.append(
                {
                    "kind": "active",
                    "t_start_ms": start,
                    "t_end_ms": prev,
                }
            )
            start = t_ms
        prev = t_ms

    segments.append(
        {
            "kind": "active",
            "t_start_ms": start,
            "t_end_ms": max(prev, duration_ms),
        }
    )
    return segments


def _unplanned_idle(
    *,
    duration_ms: int,
    planned: list[dict[str, Any]],
    active: list[dict[str, Any]],
    min_idle_ms: int,
) -> list[dict[str, Any]]:
    busy: list[tuple[int, int]] = []
    for seg in planned + active:
        busy.append((int(seg["t_start_ms"]), int(seg["t_end_ms"])))
    busy.sort()

    merged: list[tuple[int, int]] = []
    for start, end in busy:
        if not merged or start > merged[-1][1]:
            merged.append((start, end))
        else:
            merged[-1] = (merged[-1][0], max(merged[-1][1], end))

    cursor = 0
    gaps: list[dict[str, Any]] = []
    for start, end in merged:
        if start - cursor >= min_idle_ms:
            gaps.append(
                {
                    "kind": "idle",
                    "t_start_ms": cursor,
                    "t_end_ms": start,
                    "reason": "unplanned_gap",
                    "planned": False,
                    "suggest_compress": True,
                }
            )
        cursor = max(cursor, end)

    if duration_ms - cursor >= min_idle_ms:
        gaps.append(
            {
                "kind": "idle",
                "t_start_ms": cursor,
                "t_end_ms": duration_ms,
                "reason": "unplanned_tail",
                "planned": False,
                "suggest_compress": True,
            }
        )
    return gaps


def _frame_number(t_ms: int, fps: int) -> int:
    return max(0, round(t_ms * fps / 1000))


def compile_timeline(timeline: dict[str, Any], *, min_idle_ms: int = 500) -> dict[str, Any]:
    events = timeline.get("events", [])
    fps = int(timeline.get("clock", {}).get("fps", 30))
    duration_ms = int(timeline.get("video", {}).get("duration_ms", 0))

    if duration_ms <= 0 and events:
        duration_ms = int(events[-1].get("t_ms", 0))

    planned_idle = _planned_idle_ranges(events)
    active = _active_ranges(events, duration_ms)
    unplanned_idle = _unplanned_idle(
        duration_ms=duration_ms,
        planned=planned_idle,
        active=active,
        min_idle_ms=min_idle_ms,
    )

    segments = sorted(
        planned_idle + active + unplanned_idle,
        key=lambda seg: int(seg["t_start_ms"]),
    )

    edit_hints = {
        "compress": [
            {
                "t_start_ms": seg["t_start_ms"],
                "t_end_ms": seg["t_end_ms"],
                "frame_start": _frame_number(int(seg["t_start_ms"]), fps),
                "frame_end": _frame_number(int(seg["t_end_ms"]), fps),
                "reason": seg.get("reason", "idle"),
                "planned": seg.get("planned", False),
            }
            for seg in segments
            if seg["kind"] == "idle"
        ],
        "hold": [],
    }

    return {
        "segments": segments,
        "edit_hints": edit_hints,
        "summary": {
            "event_count": len(events),
            "segment_count": len(segments),
            "planned_idle_count": sum(1 for s in planned_idle if s.get("planned")),
            "unplanned_idle_count": len(unplanned_idle),
            "active_count": len(active),
            "duration_ms": duration_ms,
            "fps": fps,
        },
    }


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("timeline", type=Path, help="Path to timeline.json")
    parser.add_argument(
        "--min-idle-ms",
        type=int,
        default=500,
        help="Minimum gap to classify as unplanned idle",
    )
    parser.add_argument(
        "-o",
        "--output",
        type=Path,
        help="Output path (default: <timeline-dir>/segments.json)",
    )
    args = parser.parse_args(argv)

    timeline = _load_timeline(args.timeline)
    compiled = compile_timeline(timeline, min_idle_ms=args.min_idle_ms)

    output = args.output or args.timeline.with_name("segments.json")
    payload = {
        "schema_version": 1,
        "source_timeline": str(args.timeline.resolve()),
        **compiled,
    }
    output.write_text(json.dumps(payload, indent=2) + "\n", encoding="utf-8")
    print(f"Wrote {output}")
    print(json.dumps(compiled["summary"], indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
