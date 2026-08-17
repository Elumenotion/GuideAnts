"""Timecode lookup across video, windows, and browser checkpoints."""

from __future__ import annotations

import json
import subprocess
from pathlib import Path
from typing import Any, Literal

from scripts.browser_session.clock import frame_number, video_ms
from scripts.browser_session.schema import (
    CheckpointMeta,
    CropRect,
    MonitorGeometry,
    SessionClock,
    load_index,
    load_session,
    read_jsonl,
)
from scripts.browser_session.time_map import ResolvedTime, resolve_time

TimeBasis = Literal["source", "compact", "chain"]


def _last_at_or_before(rows: list[dict[str, Any]], t_ms: int, key: str = "t_ms") -> dict[str, Any] | None:
    best: dict[str, Any] | None = None
    best_t = -1
    for row in rows:
        row_t = int(row.get(key, -1))
        if row_t <= t_ms and row_t >= best_t:
            best = row
            best_t = row_t
    return best


def _tabs_open_at(index: dict[str, Any], t_ms: int) -> dict[str, dict[str, Any]]:
    tabs: dict[str, dict[str, Any]] = {}
    for tab_id, tab in index.get("tabs", {}).items():
        opened = int(tab.get("opened_at_ms", 0))
        closed = tab.get("closed_at_ms")
        if opened > t_ms:
            continue
        if closed is not None and int(closed) <= t_ms:
            continue
        tabs[tab_id] = tab
    return tabs


def _last_checkpoint_per_tab(
    checkpoints: list[dict[str, Any]],
    tab_ids: set[str],
    t_ms: int,
) -> dict[str, dict[str, Any]]:
    result: dict[str, dict[str, Any]] = {}
    for cp in checkpoints:
        cp_t = int(cp.get("t_ms", -1))
        tab_id = str(cp.get("tab_id", ""))
        if tab_id not in tab_ids or cp_t > t_ms:
            continue
        prev = result.get(tab_id)
        if prev is None or cp_t >= int(prev.get("t_ms", -1)):
            result[tab_id] = cp
    return result


def _focused_tab_at(
    events: list[dict[str, Any]],
    tab_ids: set[str],
    t_ms: int,
) -> str | None:
    focused: str | None = None
    for event in events:
        event_t = int(event.get("t_ms", -1))
        if event_t > t_ms:
            break
        kind = event.get("kind")
        if kind == "tab.focus":
            tab_id = str(event.get("tab_id", ""))
            if tab_id in tab_ids:
                focused = tab_id
        elif kind == "tab.close":
            tab_id = str(event.get("tab_id", ""))
            if focused == tab_id:
                focused = None
    return focused


def _checkpoint_detail(session_dir: Path, cp: dict[str, Any]) -> dict[str, Any]:
    cp_id = str(cp.get("id", ""))
    base = session_dir / "checkpoints" / cp_id
    detail = dict(cp)
    detail["paths"] = {
        "meta": str((base / "meta.json").resolve()) if (base / "meta.json").is_file() else None,
        "screenshot": str((base / "screenshot.png").resolve())
        if (base / "screenshot.png").is_file()
        else None,
        "text": str((base / "text.txt").resolve()) if (base / "text.txt").is_file() else None,
        "mhtml": str((base / "page.mhtml").resolve()) if (base / "page.mhtml").is_file() else None,
    }
    return detail


def _clock_from_session(session: dict[str, Any]) -> SessionClock:
    clock = session["clock"]
    return SessionClock(
        t0_epoch_ms=int(clock["t0_epoch_ms"]),
        recording_started_epoch_ms=int(clock["recording_started_epoch_ms"]),
        recording_lead_in_ms=int(clock["recording_lead_in_ms"]),
        fps=int(clock.get("fps", 30)),
    )


def _monitor_from_session(session: dict[str, Any]) -> MonitorGeometry:
    mon = session["monitor"]
    return MonitorGeometry(
        index=int(mon["index"]),
        left=int(mon["left"]),
        top=int(mon["top"]),
        width=int(mon["width"]),
        height=int(mon["height"]),
    )


def extract_video_frame(
    video_path: Path,
    video_time_ms: int,
    fps: int,
    output: Path,
) -> Path:
    output.parent.mkdir(parents=True, exist_ok=True)
    timestamp = max(0, video_time_ms) / 1000.0
    cmd = [
        "ffmpeg",
        "-y",
        "-i",
        str(video_path),
        "-ss",
        f"{timestamp:.3f}",
        "-frames:v",
        "1",
        "-q:v",
        "2",
        str(output),
    ]
    subprocess.run(cmd, check=True, capture_output=True)
    return output


def crop_frame(
    frame_path: Path,
    crop: CropRect,
    output: Path,
) -> Path:
    output.parent.mkdir(parents=True, exist_ok=True)
    cmd = [
        "ffmpeg",
        "-y",
        "-i",
        str(frame_path),
        "-vf",
        f"crop={crop.w}:{crop.h}:{crop.x}:{crop.y}",
        str(output),
    ]
    subprocess.run(cmd, check=True, capture_output=True)
    return output


def lookup_at(
    session_dir: Path,
    t_ms: int,
    *,
    extract_frame: bool = False,
    extract_crop: bool = False,
    time_basis: TimeBasis = "source",
) -> dict[str, Any]:
    resolved = resolve_time(session_dir, t_ms, basis=time_basis)
    if resolved.status != "ok" or resolved.source_ms is None:
        return {"query": resolved.to_dict(), "status": resolved.status}

    lookup_dir = resolved.part_dir or session_dir
    session = load_session(lookup_dir)
    clock = _clock_from_session(session)
    fps = clock.fps
    source_ms = resolved.source_ms
    compact_ms = resolved.compact_ms if time_basis == "compact" else None
    v_ms = resolved.video_ms if resolved.video_ms is not None else video_ms(source_ms, clock)
    seek_ms = compact_ms if compact_ms is not None else v_ms

    compact = session.get("compact") or {}
    if time_basis == "compact" and compact.get("video"):
        video_path = Path(compact["video"])
    else:
        video_path = Path(session["paths"].get("video", str(lookup_dir / "video.mp4")))
    if not video_path.is_file():
        video_path = lookup_dir / "video.mp4"

    windows = read_jsonl(lookup_dir / "windows.jsonl")
    window_sample = _last_at_or_before(windows, source_ms)

    index = load_index(lookup_dir)
    checkpoints = index.get("checkpoints", [])
    open_tabs = _tabs_open_at(index, source_ms)
    tab_ids = set(open_tabs.keys())
    events = read_jsonl(lookup_dir / "events.jsonl")
    focused_tab_id = _focused_tab_at(events, tab_ids, source_ms)

    per_tab = _last_checkpoint_per_tab(checkpoints, tab_ids, source_ms)

    surface = "other_window"
    browser_foreground: dict[str, Any] | None = None
    if window_sample and window_sample.get("is_capture_browser"):
        surface = "browser"
        if focused_tab_id and focused_tab_id in per_tab:
            browser_foreground = _checkpoint_detail(lookup_dir, per_tab[focused_tab_id])

    tabs_out: dict[str, Any] = {}
    for tab_id, tab_meta in open_tabs.items():
        cp = per_tab.get(tab_id)
        tabs_out[tab_id] = {
            "meta": tab_meta,
            "checkpoint": _checkpoint_detail(lookup_dir, cp) if cp else None,
            "focused": tab_id == focused_tab_id,
        }

    tab_list = [
        {
            "tab_id": tab_id,
            "title": (per_tab.get(tab_id) or {}).get("title") or tab_meta.get("last_title", ""),
            "url": (per_tab.get(tab_id) or {}).get("url") or tab_meta.get("last_url", ""),
            "focused": tab_id == focused_tab_id,
        }
        for tab_id, tab_meta in sorted(open_tabs.items(), key=lambda item: item[1].get("opened_at_ms", 0))
    ]

    result: dict[str, Any] = {
        "status": "ok",
        "query": {
            "time_basis": time_basis,
            "query_ms": t_ms,
            "source_ms": source_ms,
            "video_ms": v_ms,
            "frame": frame_number(v_ms, fps),
            "resolution": resolved.to_dict(),
        },
        "surface": surface,
        "window": window_sample,
        "foreground": browser_foreground,
        "tabs": tabs_out,
        "tab_list": tab_list,
        "paths": {
            "video": str(video_path.resolve()) if video_path.is_file() else None,
        },
    }

    if extract_frame and video_path.is_file():
        frame_out = lookup_dir / "lookup" / f"frame_{source_ms:06d}.png"
        extract_video_frame(video_path, seek_ms, fps, frame_out)
        result["paths"]["frame"] = str(frame_out.resolve())

        if extract_crop and window_sample:
            crop_data = window_sample.get("crop", {})
            crop = CropRect(
                x=int(crop_data.get("x", 0)),
                y=int(crop_data.get("y", 0)),
                w=int(crop_data.get("w", 0)),
                h=int(crop_data.get("h", 0)),
            )
            if crop.w > 0 and crop.h > 0:
                crop_out = lookup_dir / "lookup" / f"crop_{source_ms:06d}.png"
                crop_frame(frame_out, crop, crop_out)
                result["paths"]["crop"] = str(crop_out.resolve())

    return result


def lookup_at_json(session_dir: Path, t_ms: int, **kwargs: Any) -> str:
    return json.dumps(lookup_at(session_dir, t_ms, **kwargs), indent=2) + "\n"
