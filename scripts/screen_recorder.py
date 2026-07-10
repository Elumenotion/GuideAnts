"""Screen recorder with per-monitor selection and sidecar metadata."""

from __future__ import annotations

import json
import platform
import socket
import threading
import time
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

import cv2
import mss
import numpy as np


@dataclass(frozen=True)
class MonitorInfo:
    """User-facing monitor descriptor (1-based index)."""

    index: int
    left: int
    top: int
    width: int
    height: int

    @property
    def label(self) -> str:
        return (
            f"Monitor {self.index}: {self.width}x{self.height} "
            f"@ ({self.left}, {self.top})"
        )


def list_monitors() -> list[MonitorInfo]:
    """Return physical monitors numbered 1, 2, 3, ... (excludes mss 'all' monitor)."""
    with mss.mss() as sct:
        return [
            MonitorInfo(
                index=i,
                left=mon["left"],
                top=mon["top"],
                width=mon["width"],
                height=mon["height"],
            )
            for i, mon in enumerate(sct.monitors[1:], start=1)
        ]


def _resolve_monitor(monitor: int) -> MonitorInfo:
    monitors = list_monitors()
    if not monitors:
        raise ValueError("No monitors detected.")
    if monitor < 1 or monitor > len(monitors):
        valid = ", ".join(str(m.index) for m in monitors)
        raise ValueError(f"Monitor must be one of: {valid} (got {monitor}).")
    return monitors[monitor - 1]


def _iso_now() -> str:
    return datetime.now(timezone.utc).astimezone().isoformat(timespec="milliseconds")


class ScreenRecorder:
    """Capture a single monitor to MP4 until :meth:`stop` is called."""

    def __init__(
        self,
        monitor: int,
        output_dir: str | Path = "recordings",
        fps: int = 30,
        filename_prefix: str = "screen",
    ) -> None:
        self.monitor_number = monitor
        self._monitor = _resolve_monitor(monitor)
        self.output_dir = Path(output_dir)
        self.fps = fps
        self.filename_prefix = filename_prefix

        self._stop_event = threading.Event()
        self._thread: threading.Thread | None = None
        self._started_at: str | None = None
        self._stopped_at: str | None = None
        self._video_path: Path | None = None
        self._metadata_path: Path | None = None
        self._frame_count = 0
        self._error: BaseException | None = None

    @property
    def is_recording(self) -> bool:
        return self._thread is not None and self._thread.is_alive()

    @property
    def video_path(self) -> Path | None:
        return self._video_path

    @property
    def metadata_path(self) -> Path | None:
        return self._metadata_path

    def start(self) -> Path:
        """Begin recording on a background thread. Returns the target video path."""
        if self.is_recording:
            raise RuntimeError("Recording is already in progress.")

        self.output_dir.mkdir(parents=True, exist_ok=True)
        stamp = datetime.now().strftime("%Y%m%d_%H%M%S")
        self._video_path = self.output_dir / (
            f"{self.filename_prefix}_m{self.monitor_number}_{stamp}.mp4"
        )
        self._metadata_path = self._video_path.with_suffix(".json")
        self._frame_count = 0
        self._error = None
        self._started_at = _iso_now()
        self._stopped_at = None
        self._stop_event.clear()

        self._thread = threading.Thread(
            target=self._record_loop,
            name=f"screen-recorder-m{self.monitor_number}",
            daemon=True,
        )
        self._thread.start()
        return self._video_path

    def stop(self, timeout: float = 30.0) -> dict[str, Any]:
        """Stop recording, finalize the video, and write metadata JSON."""
        if self._thread is None:
            raise RuntimeError("No recording has been started.")

        self._stop_event.set()
        self._thread.join(timeout=timeout)
        if self._thread.is_alive():
            raise TimeoutError("Recorder thread did not stop in time.")

        if self._error is not None:
            raise RuntimeError(f"Recording failed: {self._error}") from self._error

        self._stopped_at = _iso_now()
        metadata = self._build_metadata()
        assert self._metadata_path is not None
        self._metadata_path.write_text(
            json.dumps(metadata, indent=2),
            encoding="utf-8",
        )
        return metadata

    def _record_loop(self) -> None:
        assert self._video_path is not None
        mon = self._monitor
        capture_region = {
            "left": mon.left,
            "top": mon.top,
            "width": mon.width,
            "height": mon.height,
        }
        fourcc = cv2.VideoWriter_fourcc(*"mp4v")
        writer = cv2.VideoWriter(
            str(self._video_path),
            fourcc,
            float(self.fps),
            (mon.width, mon.height),
        )
        if not writer.isOpened():
            self._error = RuntimeError(f"Could not open video writer: {self._video_path}")
            return

        frame_interval = 1.0 / self.fps
        try:
            with mss.mss() as sct:
                next_frame_at = time.perf_counter()
                while not self._stop_event.is_set():
                    shot = sct.grab(capture_region)
                    frame = cv2.cvtColor(np.asarray(shot), cv2.COLOR_BGRA2BGR)
                    writer.write(frame)
                    self._frame_count += 1

                    next_frame_at += frame_interval
                    sleep_for = next_frame_at - time.perf_counter()
                    if sleep_for > 0:
                        time.sleep(sleep_for)
                    else:
                        next_frame_at = time.perf_counter()
        except BaseException as exc:  # noqa: BLE001 — propagate after cleanup
            self._error = exc
        finally:
            writer.release()

    def _build_metadata(self) -> dict[str, Any]:
        assert self._video_path is not None
        assert self._started_at is not None
        assert self._stopped_at is not None

        started = datetime.fromisoformat(self._started_at)
        stopped = datetime.fromisoformat(self._stopped_at)
        duration = max(0.0, (stopped - started).total_seconds())

        return {
            "recording": {
                "started_at": self._started_at,
                "stopped_at": self._stopped_at,
                "duration_seconds": round(duration, 3),
            },
            "source": {
                "type": "monitor",
                "monitor_index": self.monitor_number,
                "left": self._monitor.left,
                "top": self._monitor.top,
                "width": self._monitor.width,
                "height": self._monitor.height,
            },
            "host": {
                "hostname": socket.gethostname(),
                "platform": platform.platform(),
            },
            "video": {
                "path": str(self._video_path.resolve()),
                "fps": self.fps,
                "frame_count": self._frame_count,
                "codec": "mp4v",
                "container": "mp4",
            },
        }


def print_monitors() -> list[MonitorInfo]:
    """Print monitors and return the list."""
    monitors = list_monitors()
    if not monitors:
        print("No monitors found.")
        return monitors
    for mon in monitors:
        print(mon.label)
    return monitors
