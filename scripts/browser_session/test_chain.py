"""Tests for chain resolution."""

from __future__ import annotations

import json
import tempfile
import unittest
from pathlib import Path

from scripts.browser_session.chain import append_part, init_chain, resolve_chain_time
from scripts.browser_session.schema import write_json_atomic


class ChainTests(unittest.TestCase):
    def test_resolve_chain_time(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            chain_dir = Path(tmp)
            init_chain(chain_dir, slug="demo")
            part = chain_dir / "part-0001"
            part.mkdir()
            write_json_atomic(
                part / "session.json",
                {
                    "clock": {
                        "t0_epoch_ms": 0,
                        "recording_started_epoch_ms": 0,
                        "recording_lead_in_ms": 0,
                        "fps": 30,
                    },
                    "media": {"video": {"duration_ms": 5000}},
                },
            )
            append_part(chain_dir, part_name="part-0001", duration_ms=5000, reason="test")
            resolved = resolve_chain_time(chain_dir, 2000)
            self.assertEqual("ok", resolved.status)
            self.assertEqual(2000, resolved.source_ms)


if __name__ == "__main__":
    unittest.main()
