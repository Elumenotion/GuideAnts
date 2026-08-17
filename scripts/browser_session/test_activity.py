"""Tests for visual activity helpers."""

from __future__ import annotations

import unittest

import numpy as np

from scripts.browser_session.activity import _diff_score, visual_static_ranges


class ActivityTests(unittest.TestCase):
    def test_static_range_when_no_activity(self) -> None:
        ranges = visual_static_ranges([], duration_ms=12000, min_static_ms=8000)
        self.assertEqual([(0, 12000)], [(r.start_ms, r.end_ms) for r in ranges])

    def test_streaming_not_static(self) -> None:
        activity = [{"t_ms": 1000}, {"t_ms": 2000}, {"t_ms": 3000}]
        ranges = visual_static_ranges(activity, duration_ms=12000, min_static_ms=8000)
        starts = [r.start_ms for r in ranges]
        self.assertNotIn(1000, starts)

    def test_small_changes_accumulate(self) -> None:
        reference = np.zeros((32, 32), dtype=np.uint8)
        current = reference.copy()
        current[4, 4] = 40
        frac, tile = _diff_score(reference, current)
        self.assertGreater(tile, 0.0)


if __name__ == "__main__":
    unittest.main()
