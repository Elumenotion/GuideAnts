"""Tests for audio silence detection."""

from __future__ import annotations

import struct
import tempfile
import unittest
import wave
from pathlib import Path

from scripts.browser_session.audio_analysis import detect_silent_ranges, measure_wav_rms


def _write_tone(path: Path, *, seconds: float, amplitude: float) -> None:
    rate = 48000
    frames = int(rate * seconds)
    with wave.open(str(path), "wb") as handle:
        handle.setnchannels(1)
        handle.setsampwidth(2)
        handle.setframerate(rate)
        samples = [int(amplitude * 32767)] * frames
        handle.writeframes(struct.pack(f"<{frames}h", *samples))


class AudioAnalysisTests(unittest.TestCase):
    def test_detects_silence_between_speech(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "audio.wav"
            rate = 48000
            silent = [0] * int(rate * 10)
            loud = [20000] * int(rate * 1)
            with wave.open(str(path), "wb") as handle:
                handle.setnchannels(1)
                handle.setsampwidth(2)
                handle.setframerate(rate)
                handle.writeframes(struct.pack(f"<{len(silent) + 2 * len(loud)}h", *(loud + silent + loud)))
            windows = measure_wav_rms(path)
            silent_ranges = detect_silent_ranges(windows, enter_db=-42.0, exit_db=-38.0, min_ms=8000)
            self.assertTrue(any(end - start >= 8000 for start, end in ((r.start_ms, r.end_ms) for r in silent_ranges)))


if __name__ == "__main__":
    unittest.main()
