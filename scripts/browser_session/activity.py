"""Visual activity detection from recorded video."""

from __future__ import annotations

import json
import subprocess
from pathlib import Path
from typing import Any

import cv2
import numpy as np

from scripts.browser_session.intervals import Interval, merge_intervals
from scripts.browser_session.schema import iter_jsonl


def _downscale_gray(frame: np.ndarray, width: int = 320) -> np.ndarray:
    height = max(1, int(frame.shape[0] * width / frame.shape[1]))
    resized = cv2.resize(frame, (width, height), interpolation=cv2.INTER_AREA)
    return cv2.cvtColor(resized, cv2.COLOR_BGR2GRAY)


def _diff_score(reference: np.ndarray, current: np.ndarray) -> tuple[float, float]:
    diff = cv2.absdiff(reference, current)
    changed_fraction = float(np.count_nonzero(diff > 12)) / diff.size
    tile_h = max(1, diff.shape[0] // 8)
    tile_w = max(1, diff.shape[1] // 8)
    max_tile = 0.0
    for y in range(0, diff.shape[0], tile_h):
        for x in range(0, diff.shape[1], tile_w):
            tile = diff[y : y + tile_h, x : x + tile_w]
            max_tile = max(max_tile, float(np.mean(tile)))
    return changed_fraction, max_tile


def sample_video_activity(
    video_path: Path,
    *,
    fps: int,
    sample_hz: float = 2.0,
    changed_fraction_threshold: float = 0.0025,
    max_tile_threshold: float = 8.0,
    ignore_bottom_px: int = 48,
) -> list[dict[str, Any]]:
    capture = cv2.VideoCapture(str(video_path))
    if not capture.isOpened():
        raise RuntimeError(f"could not open video: {video_path}")
    native_fps = capture.get(cv2.CAP_PROP_FPS) or fps
    frame_count = int(capture.get(cv2.CAP_PROP_FRAME_COUNT) or 0)
    step = max(1, int(round(native_fps / sample_hz)))
    activity: list[dict[str, Any]] = []
    reference: np.ndarray | None = None
    static_candidate: np.ndarray | None = None
    frame_index = 0
    while True:
        ok, frame = capture.read()
        if not ok:
            break
        if frame_index % step != 0:
            frame_index += 1
            continue
        if ignore_bottom_px > 0 and frame.shape[0] > ignore_bottom_px:
            frame = frame[: frame.shape[0] - ignore_bottom_px, :]
        gray = _downscale_gray(frame)
        t_ms = int(round(frame_index * 1000.0 / native_fps))
        if reference is None:
            reference = gray
            static_candidate = gray
            frame_index += 1
            continue
        assert static_candidate is not None
        changed_fraction, max_tile = _diff_score(static_candidate, gray)
        active = changed_fraction >= changed_fraction_threshold or max_tile >= max_tile_threshold
        if active:
            activity.append(
                {
                    "t_ms": t_ms,
                    "kind": "view.activity",
                    "source": "visual",
                    "changed_fraction": round(changed_fraction, 6),
                    "max_tile_mean": round(max_tile, 3),
                }
            )
            static_candidate = gray
        frame_index += 1
    capture.release()
    if frame_count and frame_index == 0:
        raise RuntimeError(f"video produced no readable frames: {video_path}")
    return activity


def dom_activity_ranges(events_path: Path) -> list[Interval]:
    ranges: list[Interval] = []
    for row in iter_jsonl(events_path):
        if row.get("kind") != "view.activity" or row.get("source") != "dom":
            continue
        t_ms = int(row["t_ms"])
        ranges.append(Interval(max(0, t_ms - 500), t_ms + 500))
    return merge_intervals(ranges)


def visual_static_ranges(
    activity: list[dict[str, Any]],
    *,
    duration_ms: int,
    min_static_ms: int = 8000,
) -> list[Interval]:
    active_times = sorted(int(item["t_ms"]) for item in activity)
    if not active_times:
        if duration_ms >= min_static_ms:
            return [Interval(0, duration_ms)]
        return []
    static: list[Interval] = []
    cursor = 0
    for t_ms in active_times:
        if t_ms - cursor >= min_static_ms:
            static.append(Interval(cursor, t_ms))
        cursor = max(cursor, t_ms)
    if duration_ms - cursor >= min_static_ms:
        static.append(Interval(cursor, duration_ms))
    return static


def write_activity_jsonl(path: Path, rows: list[dict[str, Any]]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("w", encoding="utf-8") as handle:
        for row in rows:
            handle.write(json.dumps(row, separators=(",", ":")) + "\n")


def extract_frame_at_ms(video_path: Path, t_ms: int, output: Path) -> None:
    timestamp = t_ms / 1000.0
    cmd = [
        "ffmpeg",
        "-y",
        "-i",
        str(video_path),
        "-ss",
        f"{timestamp:.3f}",
        "-frames:v",
        "1",
        str(output),
    ]
    subprocess.run(cmd, check=True, capture_output=True)
