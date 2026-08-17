"""Session bundle schema types and JSON helpers."""

from __future__ import annotations

import json
import os
from dataclasses import asdict, dataclass, field
from pathlib import Path
from typing import Any, Iterator


SCHEMA_VERSION_V1 = 1
SCHEMA_VERSION_V2 = 2
SCHEMA_VERSION_V3 = 3

# Session lifecycle states (R-OPS-04).
SESSION_STATUS_RECORDING = "recording"
SESSION_STATUS_COMPLETE = "complete"
SESSION_STATUS_INTERRUPTED = "interrupted"
SESSION_STATUS_RECOVERED_WITH_GAP = "recovered_with_gap"
SESSION_STATUS_SALVAGED = "salvaged"
SESSION_STATUS_FAILED = "failed"

# Compaction / salvage generation states.
COMPACT_STATUS_NO_CHANGES = "no_changes"
COMPACT_STATUS_VERIFIED = "verified"
COMPACT_STATUS_VISUAL_ONLY_DEGRADED = "visual_only_degraded"
COMPACT_STATUS_REJECTED = "rejected"

# Stable machine-readable integrity error codes.
ERROR_AUDIO_COVERAGE_GAP = "AUDIO_COVERAGE_GAP"
ERROR_VIDEO_COVERAGE_GAP = "VIDEO_COVERAGE_GAP"
ERROR_SESSION_INTERRUPTED = "SESSION_INTERRUPTED"
ERROR_PLAYWRIGHT_EVIDENCE_EMPTY = "PLAYWRIGHT_EVIDENCE_EMPTY"
ERROR_WINDOW_COVERAGE_GAP = "WINDOW_COVERAGE_GAP"
ERROR_COMPACT_SYNTHETIC_AUDIO = "COMPACT_SYNTHETIC_AUDIO"
ERROR_COMPACT_UNVERIFIED = "COMPACT_UNVERIFIED"
ERROR_COMPACT_STALE_ANALYSIS = "COMPACT_STALE_ANALYSIS"
ERROR_MEDIA_PROBE_INCOMPLETE = "MEDIA_PROBE_INCOMPLETE"
ERROR_SOURCE_HASH_CHANGED = "SOURCE_HASH_CHANGED"
ERROR_CHAIN_UNKNOWN_PART = "CHAIN_UNKNOWN_PART"
ERROR_REQUIRED_TRACK_FAILURE = "REQUIRED_TRACK_FAILURE"
ERROR_DISK_RESERVE_BREACH = "DISK_RESERVE_BREACH"
ERROR_SYNTHETIC_MEDIA_FILTER = "SYNTHETIC_MEDIA_FILTER"

FORBIDDEN_COMPACT_FILTERS = ("apad", "tpad", "adelay", "aresample=async")


@dataclass(frozen=True)
class MonitorGeometry:
    index: int
    left: int
    top: int
    width: int
    height: int

    def to_dict(self) -> dict[str, int]:
        return {
            "index": self.index,
            "left": self.left,
            "top": self.top,
            "width": self.width,
            "height": self.height,
        }


@dataclass(frozen=True)
class SessionClock:
    t0_epoch_ms: int
    recording_started_epoch_ms: int
    recording_lead_in_ms: int
    fps: int

    def to_dict(self) -> dict[str, int]:
        return asdict(self)


@dataclass(frozen=True)
class MediaAnchor:
    """Anchors a media stream to the logical session timeline."""

    logical_start_ms: int
    logical_end_ms: int
    stream_start_ms: int
    stream_end_ms: int
    duration_ms: int
    frame_or_sample_count: int | None = None
    sha256: str | None = None
    path: str | None = None

    def to_dict(self) -> dict[str, Any]:
        return asdict(self)


@dataclass(frozen=True)
class PartInfo:
    index: int
    chain_id: str
    chain_offset_ms: int
    status: str = "recording"

    def to_dict(self) -> dict[str, Any]:
        return asdict(self)


@dataclass(frozen=True)
class ScreenRect:
    left: int
    top: int
    width: int
    height: int

    def to_dict(self) -> dict[str, int]:
        return asdict(self)


@dataclass(frozen=True)
class CropRect:
    x: int
    y: int
    w: int
    h: int

    def to_dict(self) -> dict[str, int]:
        return asdict(self)


@dataclass
class WindowSample:
    t_ms: int
    hwnd: str
    process: str
    title: str
    is_capture_browser: bool
    screen: ScreenRect
    crop: CropRect
    visible_on_monitor: bool
    clamped: bool = False

    def to_dict(self) -> dict[str, Any]:
        return {
            "t_ms": self.t_ms,
            "hwnd": self.hwnd,
            "process": self.process,
            "title": self.title,
            "is_capture_browser": self.is_capture_browser,
            "screen": self.screen.to_dict(),
            "crop": self.crop.to_dict(),
            "visible_on_monitor": self.visible_on_monitor,
            "clamped": self.clamped,
        }


@dataclass
class CheckpointMeta:
    id: str
    t_ms: int
    tab_id: str
    foreground: bool
    trigger: str
    url: str
    title: str
    scroll_x: int = 0
    scroll_y: int = 0
    selection: str = ""
    has_screenshot: bool = False
    has_text: bool = False
    has_mhtml: bool = False

    def to_dict(self) -> dict[str, Any]:
        return asdict(self)


@dataclass
class TabState:
    tab_id: str
    opened_at_ms: int
    closed_at_ms: int | None = None
    last_url: str = ""
    last_title: str = ""

    def to_dict(self) -> dict[str, Any]:
        return asdict(self)


@dataclass
class SessionBundle:
    schema_version: int
    session_id: str
    clock: SessionClock
    monitor: MonitorGeometry
    paths: dict[str, str]
    capture_browser_hwnd: str | None = None
    part: PartInfo | None = None
    media: dict[str, Any] | None = None
    compact: dict[str, Any] | None = None

    def to_dict(self) -> dict[str, Any]:
        payload: dict[str, Any] = {
            "schema_version": self.schema_version,
            "session_id": self.session_id,
            "clock": self.clock.to_dict(),
            "monitor": self.monitor.to_dict(),
            "paths": self.paths,
            "capture_browser_hwnd": self.capture_browser_hwnd,
        }
        if self.part is not None:
            payload["part"] = self.part.to_dict()
        if self.media is not None:
            payload["media"] = self.media
        if self.compact is not None:
            payload["compact"] = self.compact
        return payload


def write_json_atomic(path: Path, payload: dict[str, Any]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_suffix(path.suffix + ".tmp")
    with tmp.open("w", encoding="utf-8") as handle:
        handle.write(json.dumps(payload, indent=2) + "\n")
        handle.flush()
        os.fsync(handle.fileno())
    os.replace(tmp, path)


def write_json(path: Path, payload: dict[str, Any]) -> None:
    write_json_atomic(path, payload)


def append_jsonl(path: Path, row: dict[str, Any], *, fsync: bool = True) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    line = json.dumps(row, separators=(",", ":")) + "\n"
    with path.open("a", encoding="utf-8", newline="") as handle:
        handle.write(line)
        handle.flush()
        if fsync:
            os.fsync(handle.fileno())


def read_jsonl(path: Path) -> list[dict[str, Any]]:
    rows, _report = read_jsonl_report(path)
    return rows


def read_jsonl_report(path: Path) -> tuple[list[dict[str, Any]], dict[str, Any]]:
    if not path.is_file():
        return [], {"truncated_final_line": False, "corrupt_line": None}
    rows: list[dict[str, Any]] = []
    corrupt_line: int | None = None
    truncated = False
    for line_no, line in enumerate(path.read_text(encoding="utf-8").splitlines(), start=1):
        stripped = line.strip()
        if not stripped:
            continue
        try:
            rows.append(json.loads(stripped))
        except json.JSONDecodeError:
            if line_no == len(path.read_text(encoding="utf-8").splitlines()):
                truncated = True
            else:
                corrupt_line = line_no
            break
    return rows, {"truncated_final_line": truncated, "corrupt_line": corrupt_line}


def iter_jsonl(path: Path) -> Iterator[dict[str, Any]]:
    if not path.is_file():
        return
    with path.open(encoding="utf-8") as handle:
        for line in handle:
            stripped = line.strip()
            if stripped:
                yield json.loads(stripped)


def load_session(session_dir: Path) -> dict[str, Any]:
    path = session_dir / "session.json"
    if path.is_file():
        return json.loads(path.read_text(encoding="utf-8"))
    provisional = session_dir / "session.provisional.json"
    if provisional.is_file():
        return json.loads(provisional.read_text(encoding="utf-8"))
    raise FileNotFoundError(f"Missing session.json in {session_dir}")


def load_index(session_dir: Path) -> dict[str, Any]:
    path = session_dir / "index.json"
    if not path.is_file():
        return {"checkpoints": [], "tabs": {}}
    return json.loads(path.read_text(encoding="utf-8"))


def write_provisional_session(session_dir: Path, payload: dict[str, Any]) -> None:
    write_json_atomic(session_dir / "session.provisional.json", payload)
