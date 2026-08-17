"""Tests for session integrity audit."""

from __future__ import annotations

import unittest
from pathlib import Path

from scripts.browser_session.audit import audit_session
from scripts.browser_session.schema import (
    ERROR_AUDIO_COVERAGE_GAP,
    ERROR_COMPACT_SYNTHETIC_AUDIO,
    ERROR_PLAYWRIGHT_EVIDENCE_EMPTY,
    ERROR_SESSION_INTERRUPTED,
)


ROOT = Path(__file__).resolve().parents[2]
FAILED_SESSION = ROOT / "recordings" / "sessions" / "20260817_170050_session"


class AuditTests(unittest.TestCase):
    @unittest.skipUnless(FAILED_SESSION.is_dir(), "regression session not present")
    def test_failed_session_reports_expected_codes(self) -> None:
        report = audit_session(FAILED_SESSION)
        codes = set(report.rejection_codes())
        self.assertFalse(report.passed)
        self.assertIn(ERROR_AUDIO_COVERAGE_GAP, codes)
        self.assertIn(ERROR_PLAYWRIGHT_EVIDENCE_EMPTY, codes)
        self.assertIn(ERROR_SESSION_INTERRUPTED, codes)
        self.assertIn(ERROR_COMPACT_SYNTHETIC_AUDIO, codes)

    def test_compact_rejects_failed_session(self) -> None:
        if not FAILED_SESSION.is_dir():
            self.skipTest("regression session not present")
        from scripts.browser_session.compact import compact_session

        with self.assertRaises(RuntimeError) as ctx:
            compact_session(FAILED_SESSION)
        self.assertIn("AUDIO_COVERAGE_GAP", str(ctx.exception))


if __name__ == "__main__":
    unittest.main()
