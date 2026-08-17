"""Video-only visual salvage for damaged sessions (R-CMP-14, R-CMP-15)."""

from __future__ import annotations

import json
import subprocess
import time
from datetime import datetime
from pathlib import Path
from typing import Any

import cv2
import numpy as np

from scripts.browser_session.activity import _diff_score, _downscale_gray
from scripts.browser_session.audit import audit_session
from scripts.browser_session.intervals import Interval, merge_intervals, validate_intervals
from scripts.browser_session.media_probe import probe_video_ffprobe, sha256_file
from scripts.browser_session.schema import (
    COMPACT_STATUS_VISUAL_ONLY_DEGRADED,
    ERROR_AUDIO_COVERAGE_GAP,
    ERROR_PLAYWRIGHT_EVIDENCE_EMPTY,
    ERROR_SESSION_INTERRUPTED,
    write_json_atomic,
)


def _every_frame_static_ranges(
    video_path: Path,
    *,
    duration_ms: int,
    min_static_ms: int,
    changed_fraction_threshold: float = 0.0025,
    max_tile_threshold: float = 8.0,
    ignore_bottom_px: int = 48,
) -> tuple[list[Interval], list[dict[str, Any]]]:
    """Analyze every decoded frame; return collapsible static intervals and frame evidence."""
    capture = cv2.VideoCapture(str(video_path))
    if not capture.isOpened():
        raise RuntimeError(f"could not open video: {video_path}")
    native_fps = capture.get(cv2.CAP_PROP_FPS) or 30.0
    static_streak_start: int | None = None
    static_ranges: list[Interval] = []
    frame_evidence: list[dict[str, Any]] = []
    reference: np.ndarray | None = None
    frame_index = 0
    while True:
        ok, frame = capture.read()
        if not ok:
            break
        if ignore_bottom_px > 0 and frame.shape[0] > ignore_bottom_px:
            frame = frame[: frame.shape[0] - ignore_bottom_px, :]
        gray = _downscale_gray(frame)
        t_ms = int(round(frame_index * 1000.0 / native_fps))
        if reference is None:
            reference = gray
            static_streak_start = t_ms
            frame_index += 1
            continue
        changed_fraction, max_tile = _diff_score(reference, gray)
        is_static = changed_fraction < changed_fraction_threshold and max_tile < max_tile_threshold
        if is_static:
            if static_streak_start is None:
                static_streak_start = t_ms
        else:
            if static_streak_start is not None and t_ms - static_streak_start >= min_static_ms:
                static_ranges.append(Interval(static_streak_start, t_ms))
            static_streak_start = None
            reference = gray
            frame_evidence.append(
                {
                    "t_ms": t_ms,
                    "changed_fraction": round(changed_fraction, 6),
                    "max_tile_mean": round(max_tile, 3),
                }
            )
        frame_index += 1
    if static_streak_start is not None:
        end_ms = int(round(frame_index * 1000.0 / native_fps))
        if end_ms - static_streak_start >= min_static_ms:
            static_ranges.append(Interval(static_streak_start, end_ms))
    capture.release()
    return validate_intervals(static_ranges, max_end_ms=duration_ms), frame_evidence


def _kept_from_removed(duration_ms: int, removed: list[Interval]) -> list[Interval]:
    kept: list[Interval] = []
    cursor = 0
    for item in validate_intervals(removed, max_end_ms=duration_ms):
        if item.start_ms > cursor:
            kept.append(Interval(cursor, item.start_ms))
        cursor = max(cursor, item.end_ms)
    if cursor < duration_ms:
        kept.append(Interval(cursor, duration_ms))
    return validate_intervals(kept, max_end_ms=duration_ms)


def _next_generation_dir(session_dir: Path) -> Path:
    derived = session_dir / "derived"
    derived.mkdir(parents=True, exist_ok=True)
    stamp = datetime.now().strftime("%Y%m%d_%H%M%S")
    gen = derived / f"visual-salvage-{stamp}"
    gen.mkdir(parents=True, exist_ok=True)
    return gen


def _build_video_only(video_path: Path, kept: list[Interval], output: Path) -> None:
    if not kept:
        raise RuntimeError("no kept ranges for visual salvage output")
    parts: list[str] = []
    labels: list[str] = []
    for index, item in enumerate(kept):
        start = item.start_ms / 1000.0
        end = item.end_ms / 1000.0
        parts.append(f"[0:v]trim=start={start:.3f}:end={end:.3f},setpts=PTS-STARTPTS[v{index}]")
        labels.append(f"[v{index}]")
    parts.append(f"{''.join(labels)}concat=n={len(kept)}:v=1:a=0[outv]")
    filter_complex = ";".join(parts)
    cmd = [
        "ffmpeg", "-y",
        "-i", str(video_path),
        "-filter_complex", filter_complex,
        "-map", "[outv]",
        "-c:v", "libx264", "-preset", "fast", "-crf", "23",
        "-an",
        str(output),
    ]
    proc = subprocess.run(cmd, capture_output=True, text=True)
    if proc.returncode != 0:
        raise RuntimeError(f"visual salvage encode failed: {proc.stderr[-500:]}")


def _extract_frame_ffmpeg(video_path: Path, t_ms: int, output: Path) -> bool:
    output.parent.mkdir(parents=True, exist_ok=True)
    cmd = [
        "ffmpeg", "-y",
        "-ss", f"{t_ms / 1000.0:.3f}",
        "-i", str(video_path),
        "-frames:v", "1",
        str(output),
    ]
    proc = subprocess.run(cmd, capture_output=True)
    return proc.returncode == 0 and output.is_file()


def _verify_frame_mapping(
    source_path: Path,
    output_path: Path,
    edit_map: dict[str, Any],
    *,
    encoding_tolerance: float = 0.12,
) -> dict[str, Any]:
    """Verify output frames map to expected source frames (interior samples only)."""
    import tempfile

    mappings_checked = 0
    mismatches: list[dict[str, Any]] = []
    boundary_reviews: list[dict[str, Any]] = []
    with tempfile.TemporaryDirectory() as tmp:
        tmp_path = Path(tmp)
        for kept_row in edit_map.get("kept", []):
            compact_start = int(kept_row["compact_start_ms"])
            source_start = int(kept_row["source_start_ms"])
            duration = int(kept_row["compact_end_ms"]) - compact_start
            if duration < 2000:
                continue
            # Interior sample only — avoid trim/encode boundary artifacts
            for offset_ms in (duration // 4, duration // 2, 3 * duration // 4):
                compact_ms = compact_start + offset_ms
                source_ms = source_start + offset_ms
                out_frame = tmp_path / f"out_{compact_ms}.png"
                src_frame = tmp_path / f"src_{source_ms}.png"
                if not _extract_frame_ffmpeg(output_path, compact_ms, out_frame):
                    continue
                if not _extract_frame_ffmpeg(source_path, source_ms, src_frame):
                    continue
                out_img = cv2.imread(str(out_frame))
                src_img = cv2.imread(str(src_frame))
                if out_img is None or src_img is None:
                    continue
                out_gray = _downscale_gray(out_img)
                src_gray = _downscale_gray(src_img)
                cf, mt = _diff_score(src_gray, out_gray)
                mappings_checked += 1
                if cf > encoding_tolerance:
                    mismatches.append(
                        {"compact_ms": compact_ms, "source_ms": source_ms, "changed_fraction": cf}
                    )
            # Boundary review images at segment edges
            for edge_offset in (500, max(500, duration - 500)):
                compact_ms = compact_start + edge_offset
                source_ms = source_start + edge_offset
                boundary_reviews.append({"compact_ms": compact_ms, "source_ms": source_ms})
    return {
        "mappings_checked": mappings_checked,
        "mismatches": mismatches,
        "boundary_reviews": boundary_reviews,
        "passed": len(mismatches) == 0 and mappings_checked > 0,
        "encoding_tolerance": encoding_tolerance,
    }


def visual_salvage_session(
    session_dir: Path,
    *,
    min_static_sec: float = 8.0,
    changed_fraction_threshold: float = 0.0025,
    max_tile_threshold: float = 8.0,
) -> dict[str, Any]:
    """Run video-only salvage on a damaged session without touching existing compact outputs."""
    session_dir = session_dir.resolve()
    video_path = session_dir / "video.mp4"
    if not video_path.is_file():
        raise FileNotFoundError(f"missing source video: {video_path}")

    source_hash_before = sha256_file(video_path)
    audit = audit_session(session_dir)

    video_probe = probe_video_ffprobe(video_path)
    duration_ms = int(video_probe["duration_ms"])
    min_static_ms = int(min_static_sec * 1000)

    static_ranges, frame_evidence = _every_frame_static_ranges(
        video_path,
        duration_ms=duration_ms,
        min_static_ms=min_static_ms,
        changed_fraction_threshold=changed_fraction_threshold,
        max_tile_threshold=max_tile_threshold,
    )
    kept = _kept_from_removed(duration_ms, static_ranges)
    if not kept or sum(item.duration_ms for item in kept) == duration_ms:
        return {
            "status": "no_changes",
            "message": "no proven visually static intervals to collapse",
            "source_hash": source_hash_before,
            "static_ranges": [item.to_dict() for item in static_ranges],
        }

    gen_dir = _next_generation_dir(session_dir)
    output_video = gen_dir / "video.mkv"
    _build_video_only(video_path, kept, output_video)

    compact_offset = 0
    kept_rows: list[dict[str, int]] = []
    for item in kept:
        kept_rows.append(
            {
                "source_start_ms": item.start_ms,
                "source_end_ms": item.end_ms,
                "compact_start_ms": compact_offset,
                "compact_end_ms": compact_offset + item.duration_ms,
            }
        )
        compact_offset += item.duration_ms

    removed_ms = sum(item.duration_ms for item in static_ranges)
    edit_map = {
        "session_id": session_dir.name,
        "generation": gen_dir.name,
        "status": COMPACT_STATUS_VISUAL_ONLY_DEGRADED,
        "source_duration_ms": duration_ms,
        "compact_duration_ms": compact_offset,
        "removed_duration_ms": removed_ms,
        "kept": kept_rows,
        "removed": [item.to_dict() for item in static_ranges],
        "source_hashes": {"video": source_hash_before},
        "known_defects": audit.rejection_codes(),
        "frame_evidence_count": len(frame_evidence),
        "thresholds": {
            "min_static_sec": min_static_sec,
            "changed_fraction_threshold": changed_fraction_threshold,
            "max_tile_threshold": max_tile_threshold,
        },
    }
    write_json_atomic(gen_dir / "edit_map.json", edit_map)

    output_probe = probe_video_ffprobe(output_video)
    mapping_proof = _verify_frame_mapping(video_path, output_video, edit_map)
    source_hash_after = sha256_file(video_path)

    verification = {
        "status": COMPACT_STATUS_VISUAL_ONLY_DEGRADED,
        "source_hash_unchanged": source_hash_before == source_hash_after,
        "source_hash_before": source_hash_before,
        "source_hash_after": source_hash_after,
        "output_hash": sha256_file(output_video),
        "output_duration_ms": output_probe["duration_ms"],
        "removed_ms": removed_ms,
        "kept_ms": compact_offset,
        "savings_pct": round((removed_ms / duration_ms) * 100.0, 2) if duration_ms else 0,
        "mapping_proof": mapping_proof,
        "has_audio_stream": False,
        "audio_artifact": None,
        "known_session_defects": [
            code
            for code in (
                ERROR_SESSION_INTERRUPTED,
                ERROR_AUDIO_COVERAGE_GAP,
                ERROR_PLAYWRIGHT_EVIDENCE_EMPTY,
            )
            if code in audit.rejection_codes()
        ],
        "content_verified": mapping_proof["passed"],
    }
    write_json_atomic(gen_dir / "verification.json", verification)

    if not verification["source_hash_unchanged"]:
        raise RuntimeError("source video hash changed during visual salvage — aborting")

    return {
        "status": COMPACT_STATUS_VISUAL_ONLY_DEGRADED,
        "generation_dir": str(gen_dir.resolve()),
        "edit_map": str((gen_dir / "edit_map.json").resolve()),
        "verification": str((gen_dir / "verification.json").resolve()),
        "video": str(output_video.resolve()),
        "source_duration_ms": duration_ms,
        "compact_duration_ms": compact_offset,
        "removed_duration_ms": removed_ms,
        "savings_pct": verification["savings_pct"],
        "verification_passed": mapping_proof["passed"],
    }
