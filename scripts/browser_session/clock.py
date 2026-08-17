"""Shared session clock helpers."""

from __future__ import annotations

import re
import time
from typing import TYPE_CHECKING

if TYPE_CHECKING:
    from scripts.browser_session.schema import SessionClock

_TIME_RE = re.compile(r"^(?:(\d+):)?(\d{1,2})(?:\.(\d{1,3}))?$")


def parse_timecode(value: str) -> int:
    """Parse a timecode string into milliseconds.

    Supported forms:
    - ``83400`` — raw milliseconds when the value is an integer >= 1000 with no dot
    - ``83.4`` — seconds with optional fractional part
    - ``1:23.4`` — minutes:seconds with optional fractional part
    """
    raw = value.strip()
    if not raw:
        raise ValueError("timecode must not be empty")

    if raw.isdigit() and "." not in raw:
        ms = int(raw)
        if ms >= 1000:
            return ms

    match = _TIME_RE.match(raw)
    if not match:
        raise ValueError(f"invalid timecode: {value!r}")

    minutes = int(match.group(1) or 0)
    seconds = int(match.group(2))
    frac = match.group(3) or ""
    millis = int(frac.ljust(3, "0")[:3]) if frac else 0
    return ((minutes * 60) + seconds) * 1000 + millis


def now_ms(t0_epoch_ms: int) -> int:
    """Milliseconds since session ``t0``."""
    return int(time.time() * 1000) - t0_epoch_ms


def video_ms(t_ms: int, clock: SessionClock) -> int:
    """Map session ``t_ms`` to a position in the recorded video."""
    return clock.recording_lead_in_ms + t_ms


def frame_number(video_time_ms: int, fps: int) -> int:
    return max(0, round(video_time_ms * fps / 1000))
