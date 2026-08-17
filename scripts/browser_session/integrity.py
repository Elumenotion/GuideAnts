"""Capture integrity state machine and failure handling."""

from __future__ import annotations

import json
import sys
import threading
import time
from dataclasses import dataclass, field
from enum import Enum
from pathlib import Path
from typing import Any, Callable

from scripts.browser_session.journal import EventJournal
from scripts.browser_session.schema import (
    ERROR_REQUIRED_TRACK_FAILURE,
    SESSION_STATUS_COMPLETE,
    SESSION_STATUS_FAILED,
    SESSION_STATUS_INTERRUPTED,
    SESSION_STATUS_RECOVERED_WITH_GAP,
    SESSION_STATUS_RECORDING,
    write_json_atomic,
)


class IntegrityState(str, Enum):
    PREFLIGHT = "preflight"
    RECORDING = "recording"
    SEALING = "sealing"
    ALERTING = "alerting"
    RESTARTING = "restarting"
    STOPPED = "stopped"
    FAILED = "failed"


@dataclass
class TrackState:
    name: str
    required: bool = True
    healthy: bool = True
    last_pts_ms: int = 0
    last_event_mono: float = 0.0
    error: str | None = None

    def to_dict(self) -> dict[str, Any]:
        return {
            "name": self.name,
            "required": self.required,
            "healthy": self.healthy,
            "last_pts_ms": self.last_pts_ms,
            "error": self.error,
        }


@dataclass
class IntegritySnapshot:
    state: str
    session_status: str
    tracks: dict[str, dict[str, Any]]
    gap_ms: int = 0
    restart_count: int = 0
    disk_reserve_mb: float | None = None
    last_heartbeat_mono: float = 0.0

    def to_dict(self) -> dict[str, Any]:
        return {
            "state": self.state,
            "session_status": self.session_status,
            "tracks": self.tracks,
            "gap_ms": self.gap_ms,
            "restart_count": self.restart_count,
            "disk_reserve_mb": self.disk_reserve_mb,
        }


class CaptureIntegrityStateMachine:
    """Orchestrates required-track health and truthful session status."""

    def __init__(
        self,
        session_dir: Path,
        *,
        on_failure_alert: Callable[[str, str], None] | None = None,
        on_restart: Callable[[], bool] | None = None,
    ) -> None:
        self.session_dir = session_dir
        self._state = IntegrityState.PREFLIGHT
        self._session_status = SESSION_STATUS_RECORDING
        self._journal = EventJournal(session_dir / "integrity.jsonl")
        self._tracks: dict[str, TrackState] = {
            "av": TrackState("av"),
            "browser": TrackState("browser"),
            "windows": TrackState("windows"),
        }
        self._lock = threading.Lock()
        self._restart_count = 0
        self._gap_ms = 0
        self._on_failure_alert = on_failure_alert
        self._on_restart = on_restart
        self._heartbeat_mono = time.monotonic()
        self._stop_requested = False

    @property
    def state(self) -> IntegrityState:
        return self._state

    @property
    def session_status(self) -> str:
        return self._session_status

    def heartbeat(self) -> None:
        self._heartbeat_mono = time.monotonic()
        heartbeat_path = self.session_dir / "heartbeat.json"
        write_json_atomic(
            heartbeat_path,
            {"t_mono": self._heartbeat_mono, "state": self._state.value},
        )

    def update_track(
        self,
        name: str,
        *,
        healthy: bool | None = None,
        last_pts_ms: int | None = None,
        error: str | None = None,
    ) -> None:
        with self._lock:
            track = self._tracks.get(name)
            if track is None:
                return
            if healthy is not None:
                track.healthy = healthy
            if last_pts_ms is not None:
                track.last_pts_ms = last_pts_ms
            if error is not None:
                track.error = error
            track.last_event_mono = time.monotonic()

    def enter_recording(self) -> None:
        with self._lock:
            self._state = IntegrityState.RECORDING
            self._session_status = SESSION_STATUS_RECORDING
        self._journal.append("state.recording", t_mono_ms=self._mono_ms())

    def check_required_tracks(self) -> str | None:
        """Return failure code if any required track is unhealthy."""
        with self._lock:
            for track in self._tracks.values():
                if track.required and not track.healthy:
                    return track.error or ERROR_REQUIRED_TRACK_FAILURE
        return None

    def handle_track_failure(self, track_name: str, error: str) -> None:
        with self._lock:
            if self._state not in (IntegrityState.RECORDING, IntegrityState.RESTARTING):
                return
            self._state = IntegrityState.SEALING
            track = self._tracks.get(track_name)
            if track:
                track.healthy = False
                track.error = error
        self._journal.append(
            "track.failure",
            t_mono_ms=self._mono_ms(),
            track=track_name,
            error=error,
            code=ERROR_REQUIRED_TRACK_FAILURE,
        )
        self._seal_and_alert(error)

    def _seal_and_alert(self, reason: str) -> None:
        with self._lock:
            self._state = IntegrityState.ALERTING
            self._session_status = SESSION_STATUS_INTERRUPTED
        self._journal.append("part.seal", t_mono_ms=self._mono_ms(), reason=reason)
        if self._on_failure_alert:
            self._on_failure_alert(ERROR_REQUIRED_TRACK_FAILURE, reason)
        self._attempt_restart()

    def _attempt_restart(self) -> None:
        with self._lock:
            self._state = IntegrityState.RESTARTING
        if self._on_restart and self._on_restart():
            with self._lock:
                self._restart_count += 1
                self._session_status = SESSION_STATUS_RECOVERED_WITH_GAP
                self._state = IntegrityState.RECORDING
                for track in self._tracks.values():
                    track.healthy = True
                    track.error = None
            self._journal.append(
                "part.restart",
                t_mono_ms=self._mono_ms(),
                restart_count=self._restart_count,
            )
        else:
            with self._lock:
                self._state = IntegrityState.FAILED
                self._session_status = SESSION_STATUS_FAILED
            self._journal.append("part.restart_failed", t_mono_ms=self._mono_ms())

    def request_stop(self, *, status: str = SESSION_STATUS_COMPLETE) -> None:
        self._stop_requested = True
        with self._lock:
            self._state = IntegrityState.STOPPED
            if self._restart_count > 0 and status == SESSION_STATUS_COMPLETE:
                self._session_status = SESSION_STATUS_RECOVERED_WITH_GAP
            else:
                self._session_status = status

    def snapshot(self) -> IntegritySnapshot:
        with self._lock:
            return IntegritySnapshot(
                state=self._state.value,
                session_status=self._session_status,
                tracks={name: track.to_dict() for name, track in self._tracks.items()},
                gap_ms=self._gap_ms,
                restart_count=self._restart_count,
                last_heartbeat_mono=self._heartbeat_mono,
            )

    def _mono_ms(self) -> int:
        return int(time.monotonic() * 1000)


def show_blocking_alert(title: str, message: str) -> None:
    """Raise a topmost modal alert on Windows."""
    print(f"\n*** INTEGRITY ALERT: {title} ***\n{message}\n", file=sys.stderr)
    if sys.platform == "win32":
        try:
            import ctypes

            ctypes.windll.user32.MessageBoxW(0, message, title, 0x00000000 | 0x00001000 | 0x00040000)
        except Exception:  # noqa: BLE001
            pass
