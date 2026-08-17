"""Screen recorder with per-monitor selection and sidecar metadata."""

from __future__ import annotations

import json
import os
import platform
import shutil
import socket
import subprocess
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

    _STARTUP_TIMEOUT_SECONDS = 10.0

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
        self._ffmpeg: subprocess.Popen[bytes] | None = None
        self._startup_event = threading.Event()
        self._frame_count = 0
        self._error: BaseException | None = None
        self._first_frame_monotonic: float | None = None
        self._last_frame_monotonic: float | None = None

    @property
    def is_recording(self) -> bool:
        return self._thread is not None and self._thread.is_alive()

    @property
    def video_path(self) -> Path | None:
        return self._video_path

    @property
    def metadata_path(self) -> Path | None:
        return self._metadata_path

    @property
    def frame_count(self) -> int:
        return self._frame_count

    @property
    def measured_duration_ms(self) -> int | None:
        if self._first_frame_monotonic is None or self._last_frame_monotonic is None:
            return None
        return int(round((self._last_frame_monotonic - self._first_frame_monotonic) * 1000.0))

    def request_rotation(self) -> None:
        """Reject unsupported in-file rotation; capture rotates at part boundaries."""
        raise RuntimeError("video rotation must be performed by starting a new capture part")

    def start(self) -> Path:
        """Begin recording on a background thread. Returns the target video path."""
        if self.is_recording:
            raise RuntimeError("Recording is already in progress.")
        if self._thread is not None:
            raise RuntimeError("Recorder instances cannot be restarted.")
        if shutil.which("ffmpeg") is None:
            raise RuntimeError(
                "ffmpeg is required for crash-resilient video capture but was not found on PATH"
            )

        self.output_dir.mkdir(parents=True, exist_ok=True)
        self._video_path = self.output_dir / f"{self.filename_prefix}.mp4"
        self._metadata_path = self._video_path.with_suffix(".json")
        for existing_path in (self._video_path, self._metadata_path):
            if existing_path.exists():
                raise FileExistsError(
                    f"refusing to overwrite existing capture media: {existing_path}"
                )
        self._frame_count = 0
        self._error = None
        self._first_frame_monotonic = None
        self._last_frame_monotonic = None
        self._started_at = _iso_now()
        self._stopped_at = None
        self._stop_event.clear()
        self._startup_event.clear()

        self._thread = threading.Thread(
            target=self._record_loop,
            name=f"screen-recorder-m{self.monitor_number}",
            daemon=True,
        )
        try:
            self._thread.start()
        except BaseException as exc:  # noqa: BLE001
            self._thread = None
            self._error = exc
            raise RuntimeError("screen recorder thread failed to start") from exc

        if not self._startup_event.wait(timeout=self._STARTUP_TIMEOUT_SECONDS):
            self._stop_event.set()
            if self._ffmpeg is not None and self._ffmpeg.poll() is None:
                self._ffmpeg.terminate()
            self._thread.join(timeout=5.0)
            raise TimeoutError("screen recorder thread did not finish startup in time")
        if self._error is not None:
            error = self._error
            if self._ffmpeg is not None and self._ffmpeg.poll() is None:
                self._ffmpeg.terminate()
            self._thread.join(timeout=5.0)
            raise RuntimeError(f"screen recorder failed to start: {error}") from error
        return self._video_path

    def stop(self, timeout: float = 30.0) -> dict[str, Any]:
        """Stop recording, finalize the video, and write metadata JSON."""
        if self._thread is None:
            raise RuntimeError("No recording has been started.")

        self._stop_event.set()
        self._thread.join(timeout=timeout)
        if self._thread.is_alive():
            if self._ffmpeg is not None and self._ffmpeg.poll() is None:
                self._ffmpeg.terminate()
            self._thread.join(timeout=5.0)
        if self._thread.is_alive():
            if self._ffmpeg is not None and self._ffmpeg.poll() is None:
                self._ffmpeg.kill()
            self._thread.join(timeout=5.0)
        if self._thread.is_alive():
            raise TimeoutError("Recorder thread did not stop in time.")

        if self._error is not None:
            raise RuntimeError(f"Recording failed: {self._error}") from self._error

        self._stopped_at = _iso_now()
        metadata = self._build_metadata()
        assert self._metadata_path is not None
        temporary = self._metadata_path.with_suffix(self._metadata_path.suffix + ".tmp")
        with temporary.open("w", encoding="utf-8") as handle:
            handle.write(json.dumps(metadata, indent=2) + "\n")
            handle.flush()
            os.fsync(handle.fileno())
        os.replace(temporary, self._metadata_path)
        return metadata

    def _record_loop(self) -> None:
        process: subprocess.Popen[bytes] | None = None
        try:
            assert self._video_path is not None
            mon = self._monitor
            ffmpeg_cmd = [
                "ffmpeg",
                "-hide_banner",
                "-loglevel",
                "error",
                "-n",
                "-f",
                "rawvideo",
                "-pix_fmt",
                "bgr24",
                "-video_size",
                f"{mon.width}x{mon.height}",
                "-framerate",
                str(self.fps),
                "-i",
                "-",
                "-an",
                "-c:v",
                "libx264",
                "-preset",
                "ultrafast",
                "-tune",
                "zerolatency",
                "-g",
                str(self.fps),
                "-keyint_min",
                str(self.fps),
                "-sc_threshold",
                "0",
                "-movflags",
                "+frag_keyframe+empty_moov+default_base_moof",
                "-flush_packets",
                "1",
                "-pix_fmt",
                "yuv420p",
                "-f",
                "mp4",
                str(self._video_path),
            ]
            process = subprocess.Popen(
                ffmpeg_cmd,
                stdin=subprocess.PIPE,
                stdout=subprocess.DEVNULL,
                stderr=subprocess.PIPE,
            )
            self._ffmpeg = process
            if process.poll() is not None:
                details = (
                    process.stderr.read().decode("utf-8", errors="replace").strip()
                    if process.stderr is not None
                    else ""
                )
                raise RuntimeError(
                    f"ffmpeg exited during startup with code {process.returncode}: {details}"
                )

            frame_interval = 1.0 / self.fps
            capture_region = {
                "left": mon.left,
                "top": mon.top,
                "width": mon.width,
                "height": mon.height,
            }
            with mss.mss() as sct:
                self._startup_event.set()
                next_frame_at = time.perf_counter()
                while not self._stop_event.is_set():
                    if process.poll() is not None:
                        details = (
                            process.stderr.read().decode("utf-8", errors="replace").strip()
                            if process.stderr is not None
                            else ""
                        )
                        raise RuntimeError(
                            f"ffmpeg exited during capture with code {process.returncode}: {details}"
                        )
                    shot = sct.grab(capture_region)
                    frame = cv2.cvtColor(np.asarray(shot), cv2.COLOR_BGRA2BGR)
                    if process.stdin is None:
                        raise RuntimeError("ffmpeg capture stdin is unavailable")
                    process.stdin.write(frame.tobytes())
                    now = time.perf_counter()
                    if self._first_frame_monotonic is None:
                        self._first_frame_monotonic = now
                    self._last_frame_monotonic = now
                    self._frame_count += 1

                    next_frame_at += frame_interval
                    sleep_for = next_frame_at - time.perf_counter()
                    if sleep_for > 0:
                        time.sleep(sleep_for)
                    else:
                        next_frame_at = time.perf_counter()
        except BaseException as exc:  # noqa: BLE001 — propagate after cleanup
            self._error = exc
            self._startup_event.set()
        finally:
            self._startup_event.set()
            if process is not None and process.stdin is not None:
                try:
                    process.stdin.close()
                except (BrokenPipeError, OSError, ValueError):
                    pass
            if process is not None:
                try:
                    process.wait(timeout=30.0)
                except subprocess.TimeoutExpired as exc:
                    self._error = self._error or exc
                    try:
                        process.kill()
                        process.wait()
                    except BaseException as kill_exc:  # noqa: BLE001
                        self._error = self._error or kill_exc
                except BaseException as exc:  # noqa: BLE001
                    self._error = self._error or exc
                try:
                    details = (
                        process.stderr.read().decode("utf-8", errors="replace").strip()
                        if process.stderr is not None
                        else ""
                    )
                except BaseException as exc:  # noqa: BLE001
                    self._error = self._error or exc
                    details = ""
                if process.returncode != 0 and self._error is None:
                    self._error = RuntimeError(
                        f"ffmpeg exited during capture with code {process.returncode}: {details}"
                    )
            self._ffmpeg = None

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
                "codec": "h264",
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
