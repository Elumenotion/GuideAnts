"""Rolling session chain metadata and cross-part lookup."""

from __future__ import annotations

import json
import re
from pathlib import Path
from typing import Any

from scripts.browser_session.schema import SessionClock, load_session, write_json_atomic
from scripts.browser_session.time_map import ResolvedTime, source_to_video_ms


_PART_DIR_RE = re.compile(r"^part-(\d+)$")


def _part_sort_key(name: str) -> tuple[int, str]:
    match = _PART_DIR_RE.match(name)
    return (int(match.group(1)), name) if match else (2**31, name)


def _known_duration(value: Any) -> bool:
    return isinstance(value, int) and not isinstance(value, bool) and value >= 0


def _entry_has_known_duration(entry: dict[str, Any]) -> bool:
    return _known_duration(entry.get("duration_ms")) and entry.get("duration_known") is not False


def chain_path(chain_dir: Path) -> Path:
    return chain_dir / "chain.json"


def load_chain(chain_dir: Path) -> dict[str, Any]:
    path = chain_path(chain_dir)
    if not path.is_file():
        raise FileNotFoundError(f"missing chain.json in {chain_dir}")
    return json.loads(path.read_text(encoding="utf-8"))


def write_chain(chain_dir: Path, payload: dict[str, Any]) -> None:
    write_json_atomic(chain_path(chain_dir), payload)


def init_chain(chain_dir: Path, *, slug: str) -> dict[str, Any]:
    chain_dir.mkdir(parents=True, exist_ok=True)
    payload = {
        "slug": slug,
        "parts": [],
        "total_duration_ms": 0,
    }
    write_chain(chain_dir, payload)
    return payload


def append_part(
    chain_dir: Path,
    *,
    part_name: str,
    duration_ms: int | None,
    reason: str,
    status: str | None = None,
) -> dict[str, Any]:
    chain = load_chain(chain_dir)
    entry = next((item for item in chain.get("parts", []) if item.get("name") == part_name), None)
    if entry is None:
        entry = {"name": part_name}
        chain.setdefault("parts", []).append(entry)
    entry["duration_ms"] = duration_ms
    duration_known = _known_duration(duration_ms)
    entry["duration_known"] = duration_known
    entry["duration_status"] = "known" if duration_known else "unknown"
    entry["reason"] = reason
    if status is not None:
        entry["status"] = status
    elif not duration_known:
        entry["status"] = "unknown_duration"
    chain["parts"].sort(key=lambda item: _part_sort_key(str(item.get("name", ""))))
    known_durations = [
        int(item["duration_ms"])
        for item in chain["parts"]
        if _entry_has_known_duration(item)
    ]
    unknown_parts = [
        str(item.get("name"))
        for item in chain["parts"]
        if not _entry_has_known_duration(item)
    ]
    chain["total_duration_ms"] = sum(known_durations)
    if unknown_parts:
        chain["duration_status"] = "partial"
        chain["unknown_parts"] = unknown_parts
    else:
        chain["duration_status"] = "complete"
        chain.pop("unknown_parts", None)
    write_chain(chain_dir, chain)
    return chain


def part_dirs(chain_dir: Path) -> list[Path]:
    chain = load_chain(chain_dir)
    names = {
        str(item["name"])
        for item in chain.get("parts", [])
        if item.get("name")
    }
    names.update(
        child.name
        for child in chain_dir.iterdir()
        if child.is_dir() and _PART_DIR_RE.match(child.name)
    )
    return [chain_dir / name for name in sorted(names, key=_part_sort_key)]


def resolve_chain_time(chain_dir: Path, chain_ms: int) -> ResolvedTime:
    chain = load_chain(chain_dir)
    cursor = 0
    for index, part in enumerate(chain.get("parts", []), start=1):
        duration_value = part.get("duration_ms")
        if part.get("duration_known") is False or not _known_duration(duration_value):
            return ResolvedTime(
                status="unavailable",
                basis="chain",
                query_ms=chain_ms,
                message=f"part {part.get('name', index)} has unknown duration",
            )
        duration = duration_value
        if chain_ms < cursor + duration:
            local_ms = chain_ms - cursor
            part_dir = chain_dir / part["name"]
            if not part_dir.is_dir():
                return ResolvedTime(
                    status="unavailable",
                    basis="chain",
                    query_ms=chain_ms,
                    message=f"part {part.get('name', index)} is missing",
                )
            session = load_session(part_dir)
            clock = SessionClock(
                t0_epoch_ms=int(session["clock"]["t0_epoch_ms"]),
                recording_started_epoch_ms=int(session["clock"]["recording_started_epoch_ms"]),
                recording_lead_in_ms=int(session["clock"]["recording_lead_in_ms"]),
                fps=int(session["clock"].get("fps", 30)),
            )
            return ResolvedTime(
                status="ok",
                basis="chain",
                query_ms=chain_ms,
                source_ms=local_ms,
                chain_ms=chain_ms,
                video_ms=source_to_video_ms(local_ms, session, clock),
                part_dir=part_dir,
                part_index=index,
            )
        cursor += duration
    return ResolvedTime(
        status="unavailable",
        basis="chain",
        query_ms=chain_ms,
        message="chain time out of range",
    )
