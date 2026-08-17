"""Tests for time mapping without clamping."""

from __future__ import annotations

import unittest

from scripts.browser_session.schema import SessionClock
from scripts.browser_session.time_map import clamp_source_to_media, source_to_video_ms


class TimeMapClampTests(unittest.TestCase):
    def test_clamp_does_not_reduce_source_ms(self) -> None:
        clock = SessionClock(t0_epoch_ms=0, recording_started_epoch_ms=0, recording_lead_in_ms=0, fps=30)
        session = {"media": {"video": {"duration_ms": 1000}}}
        self.assertEqual(5000, clamp_source_to_media(5000, session, clock))

    def test_out_of_range_returns_negative_video_ms(self) -> None:
        clock = SessionClock(t0_epoch_ms=0, recording_started_epoch_ms=0, recording_lead_in_ms=0, fps=30)
        session = {"media": {"video": {"duration_ms": 1000}}}
        self.assertEqual(-1, source_to_video_ms(2000, session, clock))


if __name__ == "__main__":
    unittest.main()
