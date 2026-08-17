"""Tests for interrupted-session discovery and media recovery."""

from __future__ import annotations

import struct
import tempfile
import unittest
import wave
from pathlib import Path
from unittest.mock import patch

from scripts.browser_session.chain import append_part, init_chain, load_chain
from scripts.browser_session.media_probe import recover_partial_wav
from scripts.browser_session.resume import prepare_resume
from scripts.browser_session.salvage import reconcile_chain
from scripts.browser_session.schema import append_jsonl, write_json_atomic


class RecoveryTests(unittest.TestCase):
    def test_reconciliation_discovers_provisional_and_empty_parts(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            chain_dir = Path(tmp)
            init_chain(chain_dir, slug="interrupted")
            first_part_dir = chain_dir / "part-0001"
            first_part_dir.mkdir()
            write_json_atomic(
                first_part_dir / "session.json",
                {
                    "status": "complete",
                    "media": {"status": "complete", "video": {"duration_ms": 500}},
                },
            )
            append_part(chain_dir, part_name="part-0001", duration_ms=500, reason="complete")

            provisional_dir = chain_dir / "part-0002"
            provisional_dir.mkdir()
            write_json_atomic(
                provisional_dir / "session.provisional.json",
                {
                    "schema_version": 2,
                    "session_id": "part-0002",
                    "clock": {
                        "t0_epoch_ms": 0,
                        "recording_started_epoch_ms": 0,
                        "recording_lead_in_ms": 0,
                        "fps": 30,
                    },
                    "monitor": {
                        "index": 1,
                        "left": 0,
                        "top": 0,
                        "width": 1920,
                        "height": 1080,
                    },
                    "paths": {},
                    "status": "recording",
                },
            )
            (chain_dir / "part-0003").mkdir()
            append_part(chain_dir, part_name="part-0004", duration_ms=600, reason="complete")

            reports = reconcile_chain(chain_dir)
            chain = load_chain(chain_dir)
            parts = {part["name"]: part for part in chain["parts"]}

            self.assertEqual(
                ["part-0001", "part-0002", "part-0003", "part-0004"],
                [part["name"] for part in chain["parts"]],
            )
            self.assertEqual(500, chain["total_duration_ms"])
            self.assertEqual("partial", chain["duration_status"])
            self.assertEqual("complete", parts["part-0001"]["status"])
            self.assertTrue(parts["part-0001"]["duration_known"])
            self.assertFalse(parts["part-0002"]["duration_known"])
            self.assertEqual("unknown", parts["part-0002"]["duration_status"])
            self.assertFalse(parts["part-0003"]["duration_known"])
            self.assertEqual("unknown_duration", parts["part-0003"]["status"])
            self.assertEqual("missing", parts["part-0004"]["status"])
            self.assertFalse(parts["part-0004"]["duration_known"])
            self.assertIsNone(parts["part-0004"]["duration_ms"])
            self.assertTrue(any(row.get("session_id") == "part-0002" for row in reports))
            self.assertTrue((provisional_dir / "session.json").is_file())

            with self.assertRaisesRegex(RuntimeError, "unknown-duration"):
                prepare_resume(chain_dir)

    def test_recovers_partial_wav_without_losing_original_bytes(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "narration.wav"
            samples = [100, -100, 200, -200]
            with wave.open(str(path), "wb") as handle:
                handle.setnchannels(1)
                handle.setsampwidth(2)
                handle.setframerate(8000)
                handle.writeframes(struct.pack("<4h", *samples))

            partial = bytearray(path.read_bytes())
            partial[4:8] = (len(partial) + 32 - 8).to_bytes(4, "little")
            partial[40:44] = (len(samples) * 2 + 32).to_bytes(4, "little")
            path.write_bytes(partial)
            original_bytes = bytes(partial)

            recovered = recover_partial_wav(path, sample_rate=16000)

            self.assertEqual(path, recovered)
            self.assertEqual(original_bytes, (path.parent / "narration.partial.wav").read_bytes())
            with wave.open(str(path), "rb") as handle:
                self.assertEqual(16000, handle.getframerate())
                self.assertEqual(samples, list(struct.unpack("<4h", handle.readframes(4))))

    def test_jsonl_append_fsyncs_by_default(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "events.jsonl"
            with patch("scripts.browser_session.schema.os.fsync") as fsync:
                append_jsonl(path, {"kind": "checkpoint"})

            fsync.assert_called_once()
            self.assertEqual('{"kind":"checkpoint"}\n', path.read_text(encoding="utf-8"))


if __name__ == "__main__":
    unittest.main()
