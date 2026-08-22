#!/usr/bin/env python3
"""Unit tests for merge_diarized (offline, no Max)."""
from __future__ import annotations

import importlib.util
import tempfile
import unittest
from pathlib import Path


def load_mod():
    path = Path(__file__).with_name("merge_diarized.py")
    spec = importlib.util.spec_from_file_location("merge_diarized", path)
    assert spec and spec.loader
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


MOD = load_mod()


class MergeTests(unittest.TestCase):
    def test_midpoint_assignment(self):
        words = [
            {"word": "Hi", "start": 0.1, "end": 0.3},
            {"word": "Bob", "start": 1.0, "end": 1.2},
            {"word": "here", "start": 1.2, "end": 1.4},
        ]
        turns = [
            {"start": 0.0, "end": 0.5, "speaker": "SPEAKER_00"},
            {"start": 0.9, "end": 2.0, "speaker": "SPEAKER_01"},
        ]
        segs = MOD.merge_words_to_segments(words, turns)
        self.assertEqual(segs[0]["speaker"], "SPEAKER_00")
        self.assertEqual(segs[0]["text"], "Hi")
        self.assertEqual(segs[1]["speaker"], "SPEAKER_01")
        self.assertEqual(segs[1]["text"], "Bob here")

    def test_gap_keeps_previous(self):
        words = [
            {"word": "A", "start": 0.0, "end": 0.2},
            {"word": "B", "start": 1.0, "end": 1.2},
        ]
        turns = [{"start": 0.0, "end": 0.5, "speaker": "SPEAKER_00"}]
        segs = MOD.merge_words_to_segments(words, turns)
        self.assertEqual(segs[0]["speaker"], "SPEAKER_00")
        self.assertEqual(len(segs), 1)

    def test_rttm_and_srt(self):
        segments = [
            {
                "start": 0.0,
                "end": 1.0,
                "speaker": "SPEAKER_00",
                "text": "Hello",
                "words": [],
            }
        ]
        with tempfile.TemporaryDirectory() as td:
            base = Path(td) / "out"
            MOD.write_outputs(base, segments, ["SPEAKER_00"])
            rttm = base.with_suffix(".rttm").read_text(encoding="utf-8")
            srt = base.with_suffix(".srt").read_text(encoding="utf-8")
        self.assertIn("SPEAKER_00", rttm)
        self.assertIn("[SPEAKER_00] Hello", srt)


if __name__ == "__main__":
    unittest.main()
