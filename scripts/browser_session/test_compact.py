"""Regression tests for duration-aligned compaction."""

from __future__ import annotations

import unittest

from scripts.browser_session.compact import _muxed_filter_complex, _reject_synthetic_filters, _session_duration_ms
from scripts.browser_session.intervals import Interval, shrink_intervals
from scripts.browser_session.schema import ERROR_SYNTHETIC_MEDIA_FILTER


class CompactTests(unittest.TestCase):
    def test_source_duration_uses_video_timeline(self) -> None:
        session = {
            "media": {
                "video": {"duration_ms": 667},
                "narration": {"duration_ms": 1003},
            }
        }
        self.assertEqual(667, _session_duration_ms(session))

    def test_muxed_filter_has_no_apad(self) -> None:
        filter_complex, _maps = _muxed_filter_complex([Interval(0, 260800)])
        self.assertNotIn("apad", filter_complex)
        self.assertIn("atrim", filter_complex)
        self.assertIn("trim", filter_complex)

    def test_reject_synthetic_filters(self) -> None:
        with self.assertRaises(RuntimeError) as ctx:
            _reject_synthetic_filters("[0:a]apad=whole_dur=1.0")
        self.assertIn(ERROR_SYNTHETIC_MEDIA_FILTER, str(ctx.exception))

    def test_boundary_protection_shrinks_removal(self) -> None:
        removed = [Interval(10000, 20000)]
        shrunk = shrink_intervals(removed, margin_ms=750, max_end=100000)
        self.assertEqual(1, len(shrunk))
        self.assertEqual(10750, shrunk[0].start_ms)
        self.assertEqual(19250, shrunk[0].end_ms)


if __name__ == "__main__":
    unittest.main()
