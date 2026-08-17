"""Tests for session resume."""

from __future__ import annotations

import json
import tempfile
import unittest
from pathlib import Path

from scripts.browser_session.resume import ensure_chain, prepare_resume
from scripts.browser_session.schema import write_json_atomic


class ResumeTests(unittest.TestCase):
    def test_flat_session_migrates_to_chain(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp) / "20260817_demo_session"
            root.mkdir()
            write_json_atomic(
                root / "session.json",
                {
                    "clock": {"t0_epoch_ms": 0, "recording_started_epoch_ms": 0, "recording_lead_in_ms": 0, "fps": 30},
                    "monitor": {"index": 1, "left": 0, "top": 0, "width": 1920, "height": 1080},
                    "media": {"session_duration_ms": 5000, "video": {"duration_ms": 5000}},
                },
            )
            (root / "windows.jsonl").write_text('{"t_ms":0}\n', encoding="utf-8")

            chain_dir = ensure_chain(root)
            self.assertTrue((chain_dir / "chain.json").is_file())
            self.assertTrue((chain_dir / "part-0001" / "session.json").is_file())
            self.assertTrue((chain_dir / "session.json").is_file())
            self.assertTrue((chain_dir / "migration.json").is_file())

            resume = prepare_resume(chain_dir)
            self.assertEqual(2, resume.part_index)
            self.assertEqual(5000, resume.chain_offset_ms)
            self.assertEqual("part-0002", resume.part_dir.name)


if __name__ == "__main__":
    unittest.main()
