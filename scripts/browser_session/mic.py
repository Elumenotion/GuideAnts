"""Microphone narration capture."""

from __future__ import annotations

import json
import os
import shutil
import subprocess
import sys
import threading
import time
import wave
from pathlib import Path
from typing import Any, Protocol

import sounddevice as sd

from scripts.browser_session.media_probe import write_pcm_as_wav
from scripts.browser_session.schema import write_json_atomic


class NarrationRecorder(Protocol):
    def start(self) -> None: ...
    def stop(self, timeout: float = 10.0) -> Path: ...
    @property
    def first_sample_ms(self) -> int | None: ...
    @property
    def last_sample_ms(self) -> int | None: ...
    @property
    def sample_count(self) -> int: ...


class MicRecorder:
    """Stream narration to a mono WAV file on a background thread."""

    def __init__(
        self,
        output_path: Path,
        *,
        sample_rate: int = 48000,
        device: int | None = None,
        t0_epoch_ms: int | None = None,
    ) -> None:
        self.output_path = output_path
        self.sidecar_path = output_path.with_suffix(".json")
        self.sample_rate = sample_rate
        self.device = device
        self._t0_epoch_ms = t0_epoch_ms
        self._stop = threading.Event()
        self._thread: threading.Thread | None = None
        self._error: BaseException | None = None
        self._wave: wave.Wave_write | None = None
        self._sample_count = 0
        self._first_sample_ms: int | None = None
        self._last_sample_ms: int | None = None
        self._lock = threading.Lock()

    @property
    def first_sample_ms(self) -> int | None:
        return self._first_sample_ms

    @property
    def last_sample_ms(self) -> int | None:
        return self._last_sample_ms

    @property
    def sample_count(self) -> int:
        return self._sample_count

    def start(self) -> None:
        if self._thread is not None:
            raise RuntimeError("mic recorder already started")
        for existing_path in (self.output_path, self.sidecar_path):
            if existing_path.exists():
                raise FileExistsError(
                    f"refusing to overwrite existing narration media: {existing_path}"
                )
        self.output_path.parent.mkdir(parents=True, exist_ok=True)
        self._thread = threading.Thread(target=self._record_loop, name="mic-recorder", daemon=True)
        self._thread.start()

    def stop(self, timeout: float = 10.0) -> Path:
        if self._thread is None:
            raise RuntimeError("mic recorder not started")
        self._stop.set()
        self._thread.join(timeout=timeout)
        if self._thread.is_alive():
            raise TimeoutError("mic recorder did not stop in time")
        if self._error is not None:
            raise RuntimeError(f"mic recording failed: {self._error}") from self._error
        self._write_sidecar(status="complete")
        return self.output_path

    def _mark_sample(self, frames: int) -> None:
        if self._t0_epoch_ms is None:
            return
        now_ms = int(time.time() * 1000) - self._t0_epoch_ms
        if self._first_sample_ms is None:
            self._first_sample_ms = now_ms
        self._last_sample_ms = now_ms
        self._sample_count += frames

    def _write_sidecar(self, *, status: str) -> None:
        payload = {
            "path": str(self.output_path.resolve()),
            "sample_rate": self.sample_rate,
            "channels": 1,
            "sample_width": 2,
            "sample_count": self._sample_count,
            "first_sample_ms": self._first_sample_ms,
            "last_sample_ms": self._last_sample_ms,
            "status": status,
        }
        write_json_atomic(self.sidecar_path, payload)

    def _sync_wave(self) -> None:
        if self._wave is None:
            return
        self._wave._patchheader()
        file_handle = self._wave._file
        file_handle.flush()
        os.fsync(file_handle.fileno())

    def _record_loop(self) -> None:
        try:
            self._wave = wave.open(str(self.output_path), "wb")
            self._wave.setnchannels(1)
            self._wave.setsampwidth(2)
            self._wave.setframerate(self.sample_rate)
            self._write_sidecar(status="recording")
            with sd.InputStream(
                samplerate=self.sample_rate,
                channels=1,
                dtype="int16",
                device=self.device,
            ) as stream:
                while not self._stop.is_set():
                    data, _overflowed = stream.read(1024)
                    if self._wave is not None:
                        self._wave.writeframesraw(data.tobytes())
                    with self._lock:
                        self._mark_sample(len(data))
                    self._sync_wave()
                    self._write_sidecar(status="recording")
        except Exception as exc:  # noqa: BLE001
            self._error = exc
        finally:
            if self._wave is not None:
                self._wave.close()
                self._wave = None
            if self._sample_count == 0 and self._error is None:
                self._error = RuntimeError("mic recorder captured no audio frames")


def list_input_devices() -> list[dict[str, Any]]:
    devices: list[dict[str, Any]] = []
    for index, dev in enumerate(sd.query_devices()):
        if dev.get("max_input_channels", 0) > 0:
            devices.append(
                {
                    "index": index,
                    "name": dev.get("name"),
                    "channels": dev.get("max_input_channels"),
                    "sample_rate": dev.get("default_samplerate"),
                }
            )
    return devices


def record_with_ffmpeg_dshow(output_path: Path, device_name: str | None = None) -> subprocess.Popen[Any]:
    if sys.platform != "win32":
        raise RuntimeError("ffmpeg dshow narration capture is only implemented on Windows")
    if shutil.which("ffmpeg") is None:
        raise RuntimeError("ffmpeg is required for dshow narration capture but was not found on PATH")
    input_spec = f"audio={device_name}" if device_name else "audio=default"
    raw_path = output_path.with_suffix(".pcm")
    cmd = [
        "ffmpeg",
        "-n",
        "-f",
        "dshow",
        "-i",
        input_spec,
        "-ac",
        "1",
        "-ar",
        "48000",
        "-f",
        "s16le",
        str(raw_path),
    ]
    return subprocess.Popen(cmd, stdin=subprocess.PIPE, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)


def stop_ffmpeg_recording(proc: subprocess.Popen[Any]) -> None:
    if proc.poll() is not None:
        return
    if proc.stdin is not None:
        proc.stdin.write(b"q")
        proc.stdin.flush()
    deadline = time.time() + 10.0
    while proc.poll() is None and time.time() < deadline:
        time.sleep(0.1)
    if proc.poll() is None:
        proc.terminate()


class FfmpegDshowRecorder:
    """Record narration via ffmpeg dshow when sounddevice is unavailable."""

    def __init__(
        self,
        output_path: Path,
        *,
        device_name: str | None = None,
        t0_epoch_ms: int | None = None,
    ) -> None:
        self.output_path = output_path
        self.device_name = device_name
        self._t0_epoch_ms = t0_epoch_ms
        self._proc: subprocess.Popen[Any] | None = None
        self._raw_path = output_path.with_suffix(".pcm")
        self._started_ms: int | None = None
        self._stopped_ms: int | None = None

    @property
    def first_sample_ms(self) -> int | None:
        return self._started_ms

    @property
    def last_sample_ms(self) -> int | None:
        return self._stopped_ms

    @property
    def sample_count(self) -> int:
        if not self._raw_path.is_file():
            return 0
        return self._raw_path.stat().st_size // 2

    def start(self) -> None:
        existing_paths = (
            self.output_path,
            self.output_path.with_suffix(".json"),
            self._raw_path,
        )
        for existing_path in existing_paths:
            if existing_path.exists():
                raise FileExistsError(
                    f"refusing to overwrite existing narration media: {existing_path}"
                )
        self.output_path.parent.mkdir(parents=True, exist_ok=True)
        self._proc = record_with_ffmpeg_dshow(self.output_path, self.device_name)
        if self._t0_epoch_ms is not None:
            self._started_ms = int(time.time() * 1000) - self._t0_epoch_ms

    def stop(self, timeout: float = 10.0) -> Path:
        if self._proc is None:
            raise RuntimeError("ffmpeg narration recorder not started")
        stop_ffmpeg_recording(self._proc)
        if not self._raw_path.is_file():
            raise RuntimeError("ffmpeg narration recorder did not produce a PCM output file")
        write_pcm_as_wav(self._raw_path, self.output_path)
        if self._t0_epoch_ms is not None:
            self._stopped_ms = int(time.time() * 1000) - self._t0_epoch_ms
        write_json_atomic(
            self.output_path.with_suffix(".json"),
            {
                "path": str(self.output_path.resolve()),
                "sample_count": self.sample_count,
                "first_sample_ms": self._started_ms,
                "last_sample_ms": self._stopped_ms,
                "status": "complete",
            },
        )
        return self.output_path


def create_narration_recorder(output_path: Path, *, t0_epoch_ms: int | None = None) -> NarrationRecorder:
    return MicRecorder(output_path, t0_epoch_ms=t0_epoch_ms)
