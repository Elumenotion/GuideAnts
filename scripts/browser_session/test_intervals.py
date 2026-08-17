"""Tests for interval utilities."""

from __future__ import annotations

import unittest

from scripts.browser_session.intervals import Interval, intersect_intervals, merge_intervals, pad_intervals


class IntervalTests(unittest.TestCase):
    def test_intersect(self) -> None:
        a = [Interval(0, 10000), Interval(20000, 30000)]
        b = [Interval(5000, 15000), Interval(25000, 35000)]
        result = intersect_intervals(a, b)
        self.assertEqual([(5000, 10000), (25000, 30000)], [(r.start_ms, r.end_ms) for r in result])

    def test_pad_and_merge(self) -> None:
        items = [Interval(10000, 20000)]
        padded = pad_intervals(items, pad_ms=1000, max_end=50000)
        self.assertEqual([(9000, 21000)], [(r.start_ms, r.end_ms) for r in padded])
        merged = merge_intervals([Interval(0, 1000), Interval(1200, 2000)], gap_ms=500)
        self.assertEqual(1, len(merged))

    def test_padding_merges_newly_overlapping_ranges(self) -> None:
        padded = pad_intervals(
            [Interval(0, 10000), Interval(10000, 18000)],
            pad_ms=2500,
            max_end=30000,
        )
        self.assertEqual([(0, 20500)], [(r.start_ms, r.end_ms) for r in padded])


if __name__ == "__main__":
    unittest.main()
