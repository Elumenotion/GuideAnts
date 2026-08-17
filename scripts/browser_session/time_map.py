"""Source, compact, and chain time mapping utilities."""

from __future__ import annotations

import json
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Literal

from scripts.browser_session.clock import video_ms
from scripts.browser_session.intervals import Interval, validate_intervals
from scripts.browser_session.schema import SessionClock, load_session


TimeBasis = Literal["source", "compact", "chain"]
MappingStatus = Literal["ok", "removed", "unavailable"]


@dataclass(frozen=True)
class ResolvedTime:
    status: MappingStatus
    basis: TimeBasis
    query_ms: int
    source_ms: int | None = None
    compact_ms: int | None = None
    chain_ms: int | None = None
    video_ms: int | None = None
    part_dir: Path | None = None
    part_index: int | None = None
    adjacent_kept_before_ms: int | None = None
    adjacent_kept_after_ms: int | None = None
    message: str | None = None

    def to_dict(self) -> dict[str, Any]:
        return {
            "status": self.status,
            "basis": self.basis,
            "query_ms": self.query_ms,
            "source_ms": self.source_ms,
            "compact_ms": self.compact_ms,
            "chain_ms": self.chain_ms,
            "video_ms": self.video_ms,
            "part_dir": str(self.part_dir.resolve()) if self.part_dir else None,
            "part_index": self.part_index,
            "adjacent_kept_before_ms": self.adjacent_kept_before_ms,
            "adjacent_kept_after_ms": self.adjacent_kept_after_ms,
            "message": self.message,
        }


def load_edit_map(session_dir: Path) -> dict[str, Any] | None:
    path = session_dir / "edit_map.json"
    if not path.is_file():
        compact = load_session(session_dir).get("compact") or {}
        edit_path = compact.get("edit_map")
        if edit_path and Path(edit_path).is_file():
            path = Path(edit_path)
        else:
            return None
    return json.loads(path.read_text(encoding="utf-8"))


def kept_ranges_from_edit_map(edit_map: dict[str, Any]) -> list[Interval]:
    kept = [
        Interval(int(item["source_start_ms"]), int(item["source_end_ms"]))
        for item in edit_map.get("kept", [])
    ]
    return validate_intervals(kept)


def source_to_compact(source_ms: int, edit_map: dict[str, Any]) -> ResolvedTime:
    for item in edit_map.get("kept", []):
        start = int(item["source_start_ms"])
        end = int(item["source_end_ms"])
        if start <= source_ms < end:
            offset = source_ms - start
            compact_ms = int(item["compact_start_ms"]) + offset
            return ResolvedTime(
                status="ok",
                basis="compact",
                query_ms=source_ms,
                source_ms=source_ms,
                compact_ms=compact_ms,
            )
    before = None
    after = None
    for item in edit_map.get("kept", []):
        end = int(item["source_end_ms"])
        start = int(item["source_start_ms"])
        if end <= source_ms:
            before = end
        elif start > source_ms and after is None:
            after = start
    return ResolvedTime(
        status="removed",
        basis="source",
        query_ms=source_ms,
        source_ms=source_ms,
        adjacent_kept_before_ms=before,
        adjacent_kept_after_ms=after,
        message="source time falls in a removed compact range",
    )


def compact_to_source(compact_ms: int, edit_map: dict[str, Any]) -> ResolvedTime:
    compact_duration = int(edit_map.get("compact_duration_ms", 0))
    if compact_ms < 0 or compact_ms >= compact_duration:
        return ResolvedTime(
            status="unavailable",
            basis="compact",
            query_ms=compact_ms,
            message="compact time out of range",
        )
    for item in edit_map.get("kept", []):
        start = int(item["compact_start_ms"])
        end = int(item["compact_end_ms"])
        if start <= compact_ms < end:
            offset = compact_ms - start
            source_ms = int(item["source_start_ms"]) + offset
            return ResolvedTime(
                status="ok",
                basis="source",
                query_ms=compact_ms,
                source_ms=source_ms,
                compact_ms=compact_ms,
            )
    return ResolvedTime(
        status="unavailable",
        basis="compact",
        query_ms=compact_ms,
        message="compact time not mapped",
    )


def media_video_duration_ms(session: dict[str, Any]) -> int | None:
    media = session.get("media") or {}
    video = media.get("video") or {}
    duration = video.get("duration_ms")
    if duration is not None:
        return int(duration)
    return None


def clamp_source_to_media(source_ms: int, session: dict[str, Any], clock: SessionClock) -> int:
    """Return source_ms unchanged; out-of-range queries must be typed unavailable, not clamped."""
    return source_ms


def source_to_video_ms(source_ms: int, session: dict[str, Any], clock: SessionClock) -> int:
    duration = media_video_duration_ms(session)
    if duration is not None:
        max_source = max(0, duration - clock.recording_lead_in_ms)
        if source_ms >= max_source:
            return -1
    return video_ms(source_ms, clock)


def resolve_time(
    session_dir: Path,
    query_ms: int,
    *,
    basis: TimeBasis = "source",
    chain_dir: Path | None = None,
) -> ResolvedTime:
    if basis == "chain":
        from scripts.browser_session.chain import resolve_chain_time

        return resolve_chain_time(chain_dir or session_dir, query_ms)
    session = load_session(session_dir)
    clock = SessionClock(
        t0_epoch_ms=int(session["clock"]["t0_epoch_ms"]),
        recording_started_epoch_ms=int(session["clock"]["recording_started_epoch_ms"]),
        recording_lead_in_ms=int(session["clock"]["recording_lead_in_ms"]),
        fps=int(session["clock"].get("fps", 30)),
    )
    if basis == "compact":
        edit_map = load_edit_map(session_dir)
        if edit_map is None:
            return ResolvedTime(
                status="unavailable",
                basis="compact",
                query_ms=query_ms,
                message="no edit_map.json for session",
            )
        resolved = compact_to_source(query_ms, edit_map)
        if resolved.status != "ok" or resolved.source_ms is None:
            return resolved
        source_ms = resolved.source_ms
    else:
        source_ms = query_ms
    video_time = source_to_video_ms(source_ms, session, clock)
    if video_time < 0:
        return ResolvedTime(
            status="unavailable",
            basis=basis,
            query_ms=query_ms,
            source_ms=source_ms,
            message="source time exceeds media coverage",
        )
    return ResolvedTime(
        status="ok",
        basis=basis,
        query_ms=query_ms,
        source_ms=source_ms,
        compact_ms=query_ms if basis == "compact" else None,
        video_ms=video_time,
        part_dir=session_dir,
    )
