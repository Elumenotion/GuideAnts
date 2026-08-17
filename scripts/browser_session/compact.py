"""Idle analysis and verified session compaction."""

from __future__ import annotations

import json
import shutil
import subprocess
import tempfile
from pathlib import Path
from typing import Any

from scripts.browser_session.activity import (
    dom_activity_ranges,
    sample_video_activity,
    visual_static_ranges,
    write_activity_jsonl,
)
from scripts.browser_session.audio_analysis import audio_silence_report
from scripts.browser_session.audit import audit_session, require_audit_pass
from scripts.browser_session.intervals import Interval, intersect_intervals, shrink_intervals, validate_intervals
from scripts.browser_session.media_probe import (
    probe_part_media,
    probe_video_ffprobe,
    probe_wav,
    sha256_file,
)
from scripts.browser_session.schema import (
    COMPACT_STATUS_NO_CHANGES,
    COMPACT_STATUS_VERIFIED,
    ERROR_SYNTHETIC_MEDIA_FILTER,
    FORBIDDEN_COMPACT_FILTERS,
    load_session,
    write_json_atomic,
)


def _session_duration_ms(session: dict[str, Any]) -> int:
    media = session.get("media") or {}
    video_duration = int((media.get("video") or {}).get("duration_ms", 0))
    if video_duration > 0:
        return video_duration
    narration_duration = int((media.get("narration") or {}).get("duration_ms", 0))
    return narration_duration if narration_duration > 0 else 0


def _reject_synthetic_filters(filter_complex: str) -> None:
    lowered = filter_complex.lower()
    for forbidden in FORBIDDEN_COMPACT_FILTERS:
        if forbidden in lowered:
            raise RuntimeError(f"{ERROR_SYNTHETIC_MEDIA_FILTER}: forbidden filter {forbidden!r} in compaction")


def analyze_idle(
    session_dir: Path,
    *,
    min_idle_sec: float = 8.0,
    silence_enter_db: float = -42.0,
    silence_exit_db: float = -38.0,
    pad_sec: float = 0.75,
    sample_hz: float = 2.0,
) -> dict[str, Any]:
    session_dir = session_dir.resolve()
    require_audit_pass(session_dir, operation="analyze-idle")
    session = load_session(session_dir)
    media = probe_part_media(session_dir)
    if media.get("status") != "complete":
        raise RuntimeError(f"cannot analyze idle: media status {media.get('status')}")
    video_path = session_dir / "video.mp4"
    narration_path = session_dir / "narration.wav"
    duration_ms = _session_duration_ms({**session, "media": media})
    if duration_ms <= 0:
        raise RuntimeError("session has no measurable media duration")

    visual_rows = sample_video_activity(video_path, fps=int(session["clock"].get("fps", 30)), sample_hz=sample_hz)
    dom_ranges = dom_activity_ranges(session_dir / "events.jsonl")
    for item in dom_ranges:
        visual_rows.append(
            {
                "t_ms": item.start_ms,
                "kind": "view.activity",
                "source": "dom",
                "changed_fraction": 1.0,
                "max_tile_mean": 999.0,
            }
        )
    write_activity_jsonl(session_dir / "activity.jsonl", visual_rows)

    static_ranges = visual_static_ranges(visual_rows, duration_ms=duration_ms, min_static_ms=int(min_idle_sec * 1000))
    if not narration_path.is_file():
        raise RuntimeError("missing narration.wav; cannot classify silence")
    audio_report = audio_silence_report(
        narration_path,
        enter_db=silence_enter_db,
        exit_db=silence_exit_db,
        min_ms=int(min_idle_sec * 1000),
        max_end_ms=duration_ms,
    )
    silent_ranges = [Interval(int(r["start_ms"]), int(r["end_ms"])) for r in audio_report["silent_ranges"]]
    idle = intersect_intervals(static_ranges, silent_ranges)
    # Boundary protection shrinks removal intervals (keeps margin on both sides).
    idle = shrink_intervals(idle, margin_ms=int(pad_sec * 1000), max_end=duration_ms)
    idle = validate_intervals(idle, max_end_ms=duration_ms)
    idle_ms = sum(item.duration_ms for item in idle)
    report = {
        "session_id": session_dir.name,
        "duration_ms": duration_ms,
        "idle_ms": idle_ms,
        "savings_pct": round((idle_ms / duration_ms) * 100.0, 2) if duration_ms else 0.0,
        "source_hashes": {
            "video": sha256_file(video_path),
            "narration": sha256_file(narration_path),
        },
        "thresholds": {
            "min_idle_sec": min_idle_sec,
            "silence_enter_db": silence_enter_db,
            "silence_exit_db": silence_exit_db,
            "pad_sec": pad_sec,
            "sample_hz": sample_hz,
        },
        "static_ranges": [item.to_dict() for item in static_ranges],
        "silent_ranges": audio_report["silent_ranges"],
        "idle_ranges": [item.to_dict() for item in idle],
        "audio": audio_report,
        "activity_count": len(visual_rows),
    }
    write_json_atomic(session_dir / "idle.json", report)
    return report


def _kept_from_idle(idle_report: dict[str, Any]) -> list[Interval]:
    duration_ms = int(idle_report["duration_ms"])
    idle = [Interval(int(r["start_ms"]), int(r["end_ms"])) for r in idle_report.get("idle_ranges", [])]
    kept: list[Interval] = []
    cursor = 0
    for item in validate_intervals(idle, max_end_ms=duration_ms):
        if item.start_ms > cursor:
            kept.append(Interval(cursor, item.start_ms))
        cursor = max(cursor, item.end_ms)
    if cursor < duration_ms:
        kept.append(Interval(cursor, duration_ms))
    return validate_intervals(kept, max_end_ms=duration_ms)


def _muxed_filter_complex(kept: list[Interval]) -> tuple[str, list[str]]:
    vparts: list[str] = []
    aparts: list[str] = []
    vlabels: list[str] = []
    alabels: list[str] = []
    for index, item in enumerate(kept):
        start = item.start_ms / 1000.0
        end = item.end_ms / 1000.0
        vparts.append(f"[0:v]trim=start={start:.3f}:end={end:.3f},setpts=PTS-STARTPTS[v{index}]")
        aparts.append(f"[0:a]atrim=start={start:.3f}:end={end:.3f},asetpts=PTS-STARTPTS[a{index}]")
        vlabels.append(f"[v{index}]")
        alabels.append(f"[a{index}]")
    vparts.append(f"{''.join(vlabels)}concat=n={len(kept)}:v=1:a=0[outv]")
    aparts.append(f"{''.join(alabels)}concat=n={len(kept)}:v=0:a=1[outa]")
    filter_complex = ";".join(vparts + aparts)
    _reject_synthetic_filters(filter_complex)
    return filter_complex, ["-map", "[outv]", "-map", "[outa]"]


def _verify_compact(
    *,
    source_video: Path,
    source_narration: Path,
    compact_video: Path,
    compact_narration: Path,
    edit_map: dict[str, Any],
    expected_duration_ms: int,
    fps: int,
) -> dict[str, Any]:
    tolerance = max(40, int(round(2000 / fps)))
    compact_video_dur = probe_video_ffprobe(compact_video)["duration_ms"]
    compact_audio_dur = probe_wav(compact_narration)["duration_ms"]
    if abs(compact_video_dur - expected_duration_ms) > tolerance:
        raise RuntimeError(f"compact video duration mismatch: {compact_video_dur} vs {expected_duration_ms}")
    if abs(compact_audio_dur - expected_duration_ms) > tolerance:
        raise RuntimeError(f"compact audio duration mismatch: {compact_audio_dur} vs {expected_duration_ms}")
    source_video_dur = probe_video_ffprobe(source_video)["duration_ms"]
    source_audio_dur = probe_wav(source_narration)["duration_ms"]
    if source_audio_dur < source_video_dur - tolerance:
        raise RuntimeError(
            f"source audio coverage gap: {source_audio_dur} ms audio vs {source_video_dur} ms video"
        )
    removed_ms = sum(
        int(r["end_ms"]) - int(r["start_ms"]) for r in edit_map.get("removed", [])
    )
    kept_ms = sum(item["compact_end_ms"] - item["compact_start_ms"] for item in edit_map.get("kept", []))
    partition_ok = abs(kept_ms + removed_ms - int(edit_map["source_duration_ms"])) <= tolerance
    return {
        "content_verified": True,
        "partition_ok": partition_ok,
        "compact_video_duration_ms": compact_video_dur,
        "compact_audio_duration_ms": compact_audio_dur,
        "expected_duration_ms": expected_duration_ms,
        "tolerance_ms": tolerance,
        "compact_hashes": {
            "video": sha256_file(compact_video),
            "narration": sha256_file(compact_narration),
        },
    }


def compact_session(session_dir: Path) -> dict[str, Any]:
    session_dir = session_dir.resolve()
    require_audit_pass(session_dir, operation="compact")
    idle_path = session_dir / "idle.json"
    if not idle_path.is_file():
        raise FileNotFoundError("run analyze-idle before compact")
    idle_report = json.loads(idle_path.read_text(encoding="utf-8"))
    session = load_session(session_dir)

    # Stale analysis check
    current_hashes = {
        "video": sha256_file(session_dir / "video.mp4"),
        "narration": sha256_file(session_dir / "narration.wav"),
    }
    stored_hashes = idle_report.get("source_hashes") or {}
    if stored_hashes and (
        stored_hashes.get("video") != current_hashes["video"]
        or stored_hashes.get("narration") != current_hashes["narration"]
    ):
        raise RuntimeError("idle.json is stale: source hashes changed since analysis")

    kept = _kept_from_idle(idle_report)
    if not kept:
        raise RuntimeError("no kept ranges remain after idle analysis")

    removed_ranges = idle_report.get("idle_ranges", [])
    if not removed_ranges:
        return {
            "status": COMPACT_STATUS_NO_CHANGES,
            "message": "no removable intervals; source left untouched",
            "source_hashes": current_hashes,
        }

    video_path = session_dir / "video.mp4"
    narration_path = session_dir / "narration.wav"
    if not video_path.is_file() or not narration_path.is_file():
        raise RuntimeError("missing source media for compaction")

    # Build in temporary generation directory
    gen_dir = session_dir / "derived" / f"compact-{idle_report['session_id']}"
    gen_dir.mkdir(parents=True, exist_ok=True)
    compact_video_tmp = gen_dir / "video.mkv.tmp"
    compact_audio_tmp = gen_dir / "narration.wav.tmp"

    # Muxed source for single-timeline cut
    mux_source = gen_dir / "mux_source.mkv"
    mux_cmd = [
        "ffmpeg", "-y",
        "-i", str(video_path),
        "-i", str(narration_path),
        "-c", "copy",
        "-shortest",
        str(mux_source),
    ]
    subprocess.run(mux_cmd, check=True, capture_output=True)

    filter_complex, maps = _muxed_filter_complex(kept)
    compact_cmd = [
        "ffmpeg", "-y",
        "-i", str(mux_source),
        "-filter_complex", filter_complex,
        *maps,
        "-c:v", "libx264", "-preset", "fast", "-crf", "23",
        "-c:a", "pcm_s16le",
        str(compact_video_tmp),
    ]
    subprocess.run(compact_cmd, check=True, capture_output=True)

    extract_audio_cmd = [
        "ffmpeg", "-y",
        "-i", str(compact_video_tmp),
        "-vn", "-c:a", "pcm_s16le",
        str(compact_audio_tmp),
    ]
    subprocess.run(extract_audio_cmd, check=True, capture_output=True)

    compact_offset = 0
    kept_rows: list[dict[str, int]] = []
    for item in kept:
        duration = item.duration_ms
        kept_rows.append(
            {
                "source_start_ms": item.start_ms,
                "source_end_ms": item.end_ms,
                "compact_start_ms": compact_offset,
                "compact_end_ms": compact_offset + duration,
            }
        )
        compact_offset += duration

    edit_map = {
        "session_id": session_dir.name,
        "status": COMPACT_STATUS_VERIFIED,
        "source_duration_ms": int(idle_report["duration_ms"]),
        "compact_duration_ms": compact_offset,
        "kept": kept_rows,
        "removed": removed_ranges,
        "source_hashes": current_hashes,
        "verified": False,
        "proof": {},
    }

    proof = _verify_compact(
        source_video=video_path,
        source_narration=narration_path,
        compact_video=compact_video_tmp,
        compact_narration=compact_audio_tmp,
        edit_map=edit_map,
        expected_duration_ms=compact_offset,
        fps=int(session.get("clock", {}).get("fps", 30)),
    )
    edit_map["proof"] = proof
    edit_map["verified"] = True
    edit_map["compact_hashes"] = proof["compact_hashes"]
    write_json_atomic(gen_dir / "edit_map.json", edit_map)

    # Atomic publish
    compact_video = session_dir / "video.compact.mp4"
    compact_audio = session_dir / "narration.compact.wav"
    published_video = gen_dir / "video.mkv"
    published_audio = gen_dir / "narration.wav"
    shutil.copy2(compact_video_tmp, published_video)
    shutil.copy2(compact_audio_tmp, published_audio)
    # Also publish to session root for backward compat
    shutil.copy2(compact_video_tmp, compact_video)
    shutil.copy2(compact_audio_tmp, compact_audio)
    write_json_atomic(session_dir / "edit_map.json", edit_map)

    session_payload = dict(session)
    session_payload["compact"] = {
        "edit_map": str((session_dir / "edit_map.json").resolve()),
        "video": str(compact_video.resolve()),
        "narration": str(compact_audio.resolve()),
        "generation": str(gen_dir.resolve()),
        "status": COMPACT_STATUS_VERIFIED,
        "proof": proof,
    }
    write_json_atomic(session_dir / "session.json", session_payload)
    return edit_map


def prune_session(session_dir: Path) -> dict[str, Any]:
    session_dir = session_dir.resolve()
    require_audit_pass(session_dir, operation="prune")
    edit_map_path = session_dir / "edit_map.json"
    if not edit_map_path.is_file():
        raise FileNotFoundError("missing edit_map.json")
    edit_map = json.loads(edit_map_path.read_text(encoding="utf-8"))
    proof = edit_map.get("proof") or {}
    if not edit_map.get("verified") or not proof.get("content_verified"):
        raise RuntimeError("refusing to prune: compact outputs lack content verification proof")
    source_hashes = edit_map.get("source_hashes") or {}
    video_path = session_dir / "video.mp4"
    narration_path = session_dir / "narration.wav"
    if sha256_file(video_path) != source_hashes.get("video"):
        raise RuntimeError("source video hash changed since compact verification")
    if sha256_file(narration_path) != source_hashes.get("narration"):
        raise RuntimeError("source narration hash changed since compact verification")
    removed: list[str] = []
    for path in (video_path, narration_path):
        backup = path.with_suffix(path.suffix + ".source")
        if backup.exists():
            raise RuntimeError(f"backup already exists: {backup}")
        shutil.move(path, backup)
        removed.append(str(backup.resolve()))
    manifest = {
        "session_id": session_dir.name,
        "pruned": True,
        "backups": removed,
        "edit_map": str(edit_map_path.resolve()),
        "proof_revalidated": True,
    }
    write_json_atomic(session_dir / "prune.json", manifest)
    return manifest
