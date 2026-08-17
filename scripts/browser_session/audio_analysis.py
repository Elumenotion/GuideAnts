"""Audio silence detection for idle analysis."""

from __future__ import annotations

import math
import wave
from pathlib import Path
from typing import Any

from scripts.browser_session.intervals import Interval, merge_intervals, pad_intervals


def _dbfs(value: float) -> float:
    if value <= 0:
        return -120.0
    return 20.0 * math.log10(value)


def measure_wav_rms(
    path: Path,
    *,
    frame_ms: int = 20,
    hop_ms: int = 10,
) -> list[tuple[int, float, float]]:
    """Return (center_ms, rms, peak_dbfs) windows."""
    with wave.open(str(path), "rb") as handle:
        rate = handle.getframerate()
        channels = handle.getnchannels()
        width = handle.getsampwidth()
        if channels != 1 or width != 2:
            raise ValueError("expected mono 16-bit PCM WAV")
        frame_samples = max(1, int(rate * frame_ms / 1000))
        hop_samples = max(1, int(rate * hop_ms / 1000))
        samples: list[int] = []
        while True:
            raw = handle.readframes(frame_samples)
            if not raw:
                break
            count = len(raw) // 2
            if count == 0:
                break
            import struct

            values = struct.unpack("<" + "h" * count, raw)
            samples.extend(values)
    if not samples:
        raise RuntimeError("audio file contains no samples")
    windows: list[tuple[int, float, float]] = []
    index = 0
    while index < len(samples):
        chunk = samples[index : index + frame_samples]
        if not chunk:
            break
        squares = [(value / 32768.0) ** 2 for value in chunk]
        rms = math.sqrt(sum(squares) / len(squares))
        peak = max(abs(value) for value in chunk) / 32768.0
        center_ms = int(round((index + len(chunk) / 2) * 1000.0 / rate))
        windows.append((center_ms, rms, _dbfs(peak)))
        index += hop_samples
    return windows


def detect_silent_ranges(
    windows: list[tuple[int, float, float]],
    *,
    enter_db: float = -42.0,
    exit_db: float = -38.0,
    min_ms: int = 8000,
) -> list[Interval]:
    if not windows:
        return []
    silent: list[Interval] = []
    in_silence = False
    start_ms = 0
    for center_ms, rms, _peak in windows:
        level = _dbfs(rms)
        if not in_silence and level <= enter_db:
            in_silence = True
            start_ms = center_ms
        elif in_silence and level >= exit_db:
            if center_ms - start_ms >= min_ms:
                silent.append(Interval(start_ms, center_ms))
            in_silence = False
    if in_silence:
        end_ms = windows[-1][0]
        if end_ms - start_ms >= min_ms:
            silent.append(Interval(start_ms, end_ms))
    return merge_intervals(silent)


def audio_silence_report(
    path: Path,
    *,
    enter_db: float = -42.0,
    exit_db: float = -38.0,
    min_ms: int = 8000,
    pad_ms: int = 0,
    max_end_ms: int,
) -> dict[str, Any]:
    windows = measure_wav_rms(path)
    levels = [_dbfs(rms) for _t, rms, _p in windows]
    levels_sorted = sorted(levels)
    percentile = lambda p: levels_sorted[int((len(levels_sorted) - 1) * p)] if levels_sorted else None
    silent = detect_silent_ranges(windows, enter_db=enter_db, exit_db=exit_db, min_ms=min_ms)
    if pad_ms:
        silent = pad_intervals(silent, pad_ms=pad_ms, max_end=max_end_ms)
    return {
        "path": str(path.resolve()),
        "window_count": len(windows),
        "noise_percentiles_db": {
            "p50": percentile(0.5),
            "p90": percentile(0.9),
            "p99": percentile(0.99),
        },
        "thresholds": {"enter_db": enter_db, "exit_db": exit_db, "min_ms": min_ms},
        "silent_ranges": [item.to_dict() for item in silent],
    }
