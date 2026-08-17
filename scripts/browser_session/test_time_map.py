"""Tests for time mapping."""

from __future__ import annotations

import json
import tempfile
import unittest
from pathlib import Path

from scripts.browser_session.time_map import compact_to_source, source_to_compact


class TimeMapTests(unittest.TestCase):
    def test_round_trip(self) -> None:
        edit_map = {
            "compact_duration_ms": 15000,
            "kept": [
                {
                    "source_start_ms": 0,
                    "source_end_ms": 10000,
                    "compact_start_ms": 0,
                    "compact_end_ms": 10000,
                },
                {
                    "source_start_ms": 20000,
                    "source_end_ms": 25000,
                    "compact_start_ms": 10000,
                    "compact_end_ms": 15000,
                },
            ],
        }
        removed = source_to_compact(15000, edit_map)
        self.assertEqual("removed", removed.status)
        ok = compact_to_source(12000, edit_map)
        self.assertEqual("ok", ok.status)
        self.assertEqual(22000, ok.source_ms)

    def test_load_edit_map_from_session(self) -> None:
        from scripts.browser_session.time_map import load_edit_map

        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            edit = {"compact_duration_ms": 1000, "kept": []}
            (root / "edit_map.json").write_text(json.dumps(edit), encoding="utf-8")
            loaded = load_edit_map(root)
            self.assertEqual(1000, loaded["compact_duration_ms"])


if __name__ == "__main__":
    unittest.main()
