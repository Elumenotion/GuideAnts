#!/usr/bin/env python3
"""Unit tests for timed_transcribe cue builders (no Max / no network)."""
from __future__ import annotations

import importlib.util
import tempfile
import unittest
import wave
from pathlib import Path


def load_mod():
    path = Path(__file__).with_name("timed_transcribe.py")
    spec = importlib.util.spec_from_file_location("timed_transcribe", path)
    assert spec and spec.loader
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


MOD = load_mod()


class CueTests(unittest.TestCase):
    def test_words_to_seconds_offset(self):
        words = MOD.words_to_seconds(
            [
                {"word": "Hi", "start_sample": 1600, "end_sample": 3200},
                {"word": "there", "start_sample": 3200, "end_sample": 4800},
            ],
            offset_s=10.0,
        )
        self.assertEqual(words[0]["start"], 10.1)
        self.assertEqual(words[0]["end"], 10.2)
        self.assertEqual(words[1]["start"], 10.2)

    def test_build_cues_respects_max_span(self):
        words = [
            {"word": "one", "start": 0.0, "end": 0.2},
            {"word": "two.", "start": 0.2, "end": 0.4},
            {"word": "three", "start": 3.5, "end": 3.7},
            {"word": "four", "start": 3.7, "end": 4.0},
        ]
        cues = MOD.build_cues(words, min_cue_s=3.0, max_cue_s=8.0)
        self.assertGreaterEqual(len(cues), 2)
        self.assertEqual(cues[0]["text"], "one two.")

    def test_srt_and_vtt_roundtrip_shape(self):
        cues = [{"id": 1, "start": 0.5, "end": 2.0, "text": "Hello world"}]
        with tempfile.TemporaryDirectory() as td:
            base = Path(td) / "out"
            MOD.write_srt(base.with_suffix(".srt"), cues)
            MOD.write_vtt(base.with_suffix(".vtt"), cues)
            srt = base.with_suffix(".srt").read_text(encoding="utf-8")
            vtt = base.with_suffix(".vtt").read_text(encoding="utf-8")
        self.assertIn("00:00:00,500 --> 00:00:02,000", srt)
        self.assertIn("Hello world", srt)
        self.assertTrue(vtt.startswith("WEBVTT"))
        self.assertIn("00:00:00.500 --> 00:00:02.000", vtt)

    def test_chunk_wav_grid(self):
        with tempfile.TemporaryDirectory() as td:
            path = Path(td) / "tone.wav"
            # 5.0 s mono 16 kHz PCM16 silence
            frames = b"\x00\x00" * (16000 * 5)
            with wave.open(str(path), "wb") as handle:
                handle.setnchannels(1)
                handle.setsampwidth(2)
                handle.setframerate(16000)
                handle.writeframes(frames)
            chunks = list(MOD.chunk_wav(str(path), td, max_s=2.0))
            self.assertEqual(len(chunks), 3)
            self.assertEqual(chunks[0][1], 0.0)
            self.assertAlmostEqual(chunks[1][1], 2.0, places=3)
            # non-final offsets land on 80 ms grid; final may absorb a sub-frame tail
            for _, offset in chunks[:-1]:
                self.assertAlmostEqual(offset / 0.08, round(offset / 0.08), places=5)
            # exact 5.0 s / 2.0 s => three chunks covering all samples
            with wave.open(chunks[-1][0], "rb") as handle:
                last_frames = handle.getnframes()
            self.assertGreater(last_frames, 0)

    def test_no_torch_import(self):
        source = Path(__file__).with_name("timed_transcribe.py").read_text(encoding="utf-8")
        self.assertNotIn("import torch", source)
        self.assertNotIn("transformers", source)


if __name__ == "__main__":
    unittest.main()
