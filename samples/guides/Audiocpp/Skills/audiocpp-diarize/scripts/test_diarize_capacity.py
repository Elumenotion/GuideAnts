#!/usr/bin/env python3
"""Unit tests for diarize overlap stitching (offline, no the GPU host)."""
from __future__ import annotations

import importlib.util
import unittest
from pathlib import Path


def load_mod():
    path = Path(__file__).with_name("diarize.py")
    spec = importlib.util.spec_from_file_location("diarize", path)
    assert spec and spec.loader
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


MOD = load_mod()


class WindowPlanTests(unittest.TestCase):
    def test_single_window(self):
        self.assertEqual(MOD.window_plan(40.0, 100.0, 30.0), [(0.0, 40.0)])

    def test_overlap_coverage(self):
        windows = MOD.window_plan(158.0, 100.0, 30.0)
        self.assertEqual(windows[0], (0.0, 100.0))
        self.assertEqual(windows[1][0], 70.0)
        self.assertAlmostEqual(windows[-1][1], 158.0)
        # Every time point covered
        t = 0.0
        for start, end in windows:
            self.assertLessEqual(start, t + 1e-9)
            t = end
        self.assertAlmostEqual(t, 158.0)


class StitchTests(unittest.TestCase):
    def test_overlap_remaps_swapped_labels(self):
        # Overlap frames 40-80 contain both speakers under global IDs.
        global_frames = [None] * 120
        for i in range(0, 60):
            global_frames[i] = "SPEAKER_00"
        for i in range(60, 120):
            global_frames[i] = "SPEAKER_01"
        # Local uses swapped IDs across the same overlap.
        local_frames = [None] * 120
        for i in range(40, 60):
            local_frames[i] = "SPEAKER_01"  # maps to SPEAKER_00
        for i in range(60, 100):
            local_frames[i] = "SPEAKER_00"  # maps to SPEAKER_01
        mapping, next_i = MOD.map_local_speakers_via_overlap(
            global_frames, local_frames, 40, 80, next_speaker_index=2
        )
        self.assertEqual(mapping["SPEAKER_01"], "SPEAKER_00")
        self.assertEqual(mapping["SPEAKER_00"], "SPEAKER_01")
        self.assertEqual(next_i, 2)

    def test_frames_roundtrip(self):
        turns = [
            {"start": 0.0, "end": 1.0, "speaker": "SPEAKER_00"},
            {"start": 1.0, "end": 2.5, "speaker": "SPEAKER_01"},
        ]
        frames = MOD.turns_to_frames(turns, n_frames=25, frame_s=0.1)
        back = MOD.frames_to_turns(frames, frame_s=0.1)
        self.assertEqual(back[0]["speaker"], "SPEAKER_00")
        self.assertEqual(back[1]["speaker"], "SPEAKER_01")
        self.assertAlmostEqual(back[1]["end"], 2.5, places=1)

    def test_capacity_hint(self):
        hint = MOD.capacity_error_hint(
            '{"error":{"message":"Sortformer diar input exceeds prepared session context of 20 seconds"}}'
        )
        self.assertIsNotNone(hint)
        self.assertIn("session_len_sec", hint)


if __name__ == "__main__":
    unittest.main()
