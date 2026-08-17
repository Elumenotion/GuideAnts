"""Recover session.json for captures interrupted before finalize."""

from __future__ import annotations

import json
import re
import subprocess
from datetime import datetime
from pathlib import Path
from typing import Any

from scripts.browser_session.media_probe import (
    probe_part_media,
    recover_partial_wav,
    write_pcm_as_wav,
)
from scripts.browser_session.schema import SCHEMA_VERSION_V2, MonitorGeometry, SessionClock, write_json_atomic


_PART_DIR_RE = re.compile(r"^part-(\d+)$")


def _part_sort_key(name: str) -> tuple[int, str]:
    match = _PART_DIR_RE.match(name)
    return (int(match.group(1)), name) if match else (2**31, name)


def _known_duration(value: Any) -> bool:
    return isinstance(value, int) and not isinstance(value, bool) and value >= 0


def _recover_audio_files(session_dir: Path) -> Path | None:
    wav_path = session_dir / "narration.wav"
    pcm_path = session_dir / "narration.pcm"
    if not wav_path.is_file() and pcm_path.is_file():
        write_pcm_as_wav(pcm_path, wav_path)
    elif wav_path.is_file():
        recover_partial_wav(wav_path)
    return wav_path if wav_path.is_file() else None


def _video_path(session_dir: Path) -> Path | None:
    canonical = session_dir / "video.mp4"
    if canonical.is_file():
        return canonical
    candidates = sorted(session_dir.glob("video_m*.mp4"), reverse=True)
    return candidates[0] if candidates else None


def _known_duration_ms(session: dict[str, Any]) -> int | None:
    media = session.get("media") or {}
    probe_status = media.get("probe_status", media.get("status"))
    if probe_status not in (None, "complete"):
        return None
    video = media.get("video") or {}
    value = video.get("duration_ms")
    if _known_duration(value):
        return value
    value = media.get("session_duration_ms")
    if probe_status == "complete" and _known_duration(value):
        return value
    return None


def _part_has_evidence(part_dir: Path) -> bool:
    return any(
        (
            (part_dir / "session.json").is_file(),
            (part_dir / "session.provisional.json").is_file(),
            (part_dir / "video.mp4").is_file(),
            bool(list(part_dir.glob("video_m*.mp4"))),
            (part_dir / "narration.wav").is_file(),
            (part_dir / "narration.pcm").is_file(),
            (part_dir / "events.jsonl").is_file(),
            (part_dir / "windows.jsonl").is_file(),
        )
    )


def _rewrite_part_timeline(
    part_dir: Path,
    *,
    chain_offset_ms: int | None,
    status: str,
    duration_ms: int | None,
) -> None:
    session_path = part_dir / "session.json"
    if not session_path.is_file():
        return
    payload = json.loads(session_path.read_text(encoding="utf-8"))
    part = payload.get("part")
    if isinstance(part, dict):
        previous_offset = part.get("chain_offset_ms")
        part["chain_offset_ms"] = chain_offset_ms
        part["chain_offset_status"] = "known" if chain_offset_ms is not None else "unknown"
        part["status"] = status
    else:
        previous_offset = None
    media = payload.get("media")
    if isinstance(media, dict) and duration_ms is not None:
        previous_duration = media.get("session_duration_ms")
        media["session_duration_ms"] = duration_ms
        media["duration_basis"] = "video_probe"
        anchors = media.get("anchors")
        if isinstance(anchors, dict):
            video = anchors.get("video")
            if isinstance(video, dict):
                video["logical_start_ms"] = 0
                video["logical_end_ms"] = duration_ms
            narration = anchors.get("narration")
            narration_duration = (media.get("narration") or {}).get("duration_ms")
            if isinstance(narration, dict) and _known_duration(narration_duration):
                narration["logical_start_ms"] = 0
                narration["logical_end_ms"] = narration_duration
        payload["timeline_reconciliation"] = {
            "basis": "probed_video_duration",
            "previous_chain_offset_ms": previous_offset,
            "previous_session_duration_ms": previous_duration,
            "chain_offset_ms": chain_offset_ms,
            "session_duration_ms": duration_ms,
        }
    write_json_atomic(session_path, payload)


def salvage_session(session_dir: Path) -> dict[str, Any]:
    session_dir = session_dir.resolve()
    if not session_dir.is_dir():
        raise FileNotFoundError(f"Not a directory: {session_dir}")

    existing = session_dir / "session.json"
    if existing.is_file():
        payload = json.loads(existing.read_text(encoding="utf-8"))
        audio_path = _recover_audio_files(session_dir)
        if audio_path is not None:
            payload.setdefault("paths", {})["narration"] = str(audio_path.resolve())
        if payload.get("status") == "interrupted" or payload.get("salvaged"):
            media = probe_part_media(session_dir)
            media["probe_status"] = media.get("status")
            video_duration = (media.get("video") or {}).get("duration_ms")
            if media.get("status") == "complete" and _known_duration(video_duration):
                media["session_duration_ms"] = video_duration
            payload["media"] = media
            write_json_atomic(existing, payload)
        return payload

    provisional = session_dir / "session.provisional.json"
    if provisional.is_file():
        payload = json.loads(provisional.read_text(encoding="utf-8"))
        video_path = _video_path(session_dir)
        audio_path = _recover_audio_files(session_dir)
        if video_path is not None:
            payload.setdefault("paths", {})["video"] = str(video_path.resolve())
        if audio_path is not None:
            payload.setdefault("paths", {})["narration"] = str(audio_path.resolve())
        media = probe_part_media(session_dir)
        media["probe_status"] = media.get("status")
        if media.get("status") == "complete":
            video_duration = (media.get("video") or {}).get("duration_ms")
            if _known_duration(video_duration):
                media["session_duration_ms"] = video_duration
        payload["salvaged"] = True
        payload["status"] = "interrupted"
        payload["media"] = media
        if payload.get("part"):
            payload["part"]["status"] = "interrupted"
        payload["recovery"] = {
            "status": "interrupted",
            "video_path": str(video_path.resolve()) if video_path is not None else None,
            "video_probe_status": media.get("status"),
            "narration_path": str(audio_path.resolve()) if audio_path is not None else None,
        }
        write_json_atomic(existing, payload)
        write_json_atomic(
            session_dir / "meta.json",
            {
                "session_id": session_dir.name,
                "salvaged": True,
                "status": "interrupted",
                "recovery": payload["recovery"],
            },
        )
        return payload

    video = session_dir / "video.mp4"
    video_meta: dict[str, Any] = {}
    sidecar_candidates = sorted(session_dir.glob("video_m*.json"), reverse=True)
    if sidecar_candidates:
        video_meta = json.loads(sidecar_candidates[0].read_text(encoding="utf-8"))
    if not video.is_file():
        mp4_candidates = sorted(session_dir.glob("video_m*.mp4"), reverse=True)
        if mp4_candidates:
            video = mp4_candidates[0]

    source = video_meta.get("source", {})
    recording = video_meta.get("recording", {})
    started_at = recording.get("started_at")
    if not started_at:
        raise RuntimeError(
            f"cannot salvage {session_dir.name}: no durable recording start timestamp was found"
        )
    t0_epoch_ms = int(datetime.fromisoformat(started_at).timestamp() * 1000)

    monitor = MonitorGeometry(
        index=int(source.get("monitor_index", 1)),
        left=int(source.get("left", 0)),
        top=int(source.get("top", 0)),
        width=int(source.get("width", 1920)),
        height=int(source.get("height", 1080)),
    )
    fps = int(video_meta.get("video", {}).get("fps", 30))
    clock = SessionClock(
        t0_epoch_ms=t0_epoch_ms,
        recording_started_epoch_ms=t0_epoch_ms,
        recording_lead_in_ms=0,
        fps=fps,
    )
    media = probe_part_media(session_dir) if video.is_file() else {"status": "missing_video"}
    bundle = {
        "schema_version": SCHEMA_VERSION_V2,
        "session_id": session_dir.name,
        "clock": clock.to_dict(),
        "monitor": monitor.to_dict(),
        "paths": {
            "video": str(video.resolve()) if video.is_file() else "",
            "narration": str((session_dir / "narration.wav").resolve()),
            "windows": str((session_dir / "windows.jsonl").resolve()),
            "events": str((session_dir / "events.jsonl").resolve()),
            "index": str((session_dir / "index.json").resolve()),
        },
        "capture_browser_hwnd": None,
        "media": media,
        "salvaged": True,
        "status": "salvaged",
    }
    write_json_atomic(session_dir / "session.json", bundle)
    write_json_atomic(
        session_dir / "meta.json",
        {
            "session_id": session_dir.name,
            "recording": video_meta,
            "salvaged": True,
            "status": "salvaged",
        },
    )
    return bundle


def salvage_chain(chain_dir: Path) -> list[dict[str, Any]]:
    reports = reconcile_chain(chain_dir)
    return reports


def reconcile_chain(chain_dir: Path) -> list[dict[str, Any]]:
    chain_dir = chain_dir.resolve()
    chain_file = chain_dir / "chain.json"
    if not chain_file.is_file():
        raise FileNotFoundError(f"missing chain.json in {chain_dir}")
    chain = json.loads(chain_file.read_text(encoding="utf-8"))
    existing_entries = {
        str(part.get("name")): dict(part)
        for part in chain.get("parts", [])
        if part.get("name")
    }
    part_names = set(existing_entries)
    part_names.update(
        child.name
        for child in chain_dir.iterdir()
        if child.is_dir() and _PART_DIR_RE.match(child.name)
    )
    reports: list[dict[str, Any]] = []
    reconciled_parts: list[dict[str, Any]] = []
    for part_name in sorted(part_names, key=_part_sort_key):
        part_dir = chain_dir / part_name
        entry = existing_entries.get(part_name, {"name": part_name})
        if not part_dir.is_dir():
            entry.update(
                {
                    "duration_ms": None,
                    "duration_known": False,
                    "duration_status": "unknown",
                    "status": "missing",
                    "media_status": "missing_part",
                }
            )
            reports.append({"session_id": part_name, "status": "missing"})
            reconciled_parts.append(entry)
            continue
        if not _part_has_evidence(part_dir):
            entry.update(
                {
                    "duration_ms": None,
                    "duration_known": False,
                    "duration_status": "unknown",
                    "status": "unknown_duration",
                    "media_status": "missing_media",
                }
            )
            reports.append({"session_id": part_name, "status": "unknown_duration"})
            reconciled_parts.append(entry)
            continue
        try:
            payload = salvage_session(part_dir)
        except (
            OSError,
            RuntimeError,
            ValueError,
            json.JSONDecodeError,
            subprocess.CalledProcessError,
        ) as exc:
            reports.append({"session_id": part_name, "salvage_error": str(exc), "status": "unrecoverable"})
            entry.update(
                {
                    "duration_ms": None,
                    "duration_known": False,
                    "duration_status": "unknown",
                    "status": "unrecoverable",
                    "media_status": "salvage_error",
                    "salvage_error": str(exc),
                }
            )
            reconciled_parts.append(entry)
            continue
        reports.append(payload)
        duration_ms = _known_duration_ms(payload)
        entry["duration_ms"] = duration_ms
        entry["duration_known"] = duration_ms is not None
        entry["duration_status"] = "known" if duration_ms is not None else "unknown"
        entry["status"] = payload.get("status", "complete")
        media = payload.get("media") or {}
        entry["media_status"] = media.get("probe_status", media.get("status", "unknown"))
        entry.pop("salvage_error", None)
        reconciled_parts.append(entry)

    unknown_parts = [
        str(part["name"])
        for part in reconciled_parts
        if part.get("duration_known") is not True
    ]
    missing_parts = [
        str(part["name"])
        for part in reconciled_parts
        if part.get("status") == "missing"
    ]
    chain["parts"] = reconciled_parts
    chain["total_duration_ms"] = sum(
        int(part["duration_ms"])
        for part in reconciled_parts
        if part.get("duration_known") is True and _known_duration(part.get("duration_ms"))
    )
    chain["duration_status"] = "partial" if unknown_parts else "complete"
    if unknown_parts:
        chain["unknown_parts"] = unknown_parts
        chain["missing_parts"] = missing_parts
    else:
        chain.pop("unknown_parts", None)
        chain.pop("missing_parts", None)
    chain_cursor = 0
    offsets_known = True
    for part in reconciled_parts:
        duration_ms = part.get("duration_ms") if part.get("duration_known") is True else None
        offset_ms = chain_cursor if offsets_known and duration_ms is not None else None
        _rewrite_part_timeline(
            chain_dir / str(part["name"]),
            chain_offset_ms=offset_ms,
            status=str(part.get("status", "unknown")),
            duration_ms=duration_ms,
        )
        if duration_ms is None:
            offsets_known = False
        elif offsets_known:
            chain_cursor += int(duration_ms)
    write_json_atomic(chain_file, chain)
    return reports
