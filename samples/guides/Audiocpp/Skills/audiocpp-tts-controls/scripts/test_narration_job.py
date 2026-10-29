#!/usr/bin/env python3
import os
import sys
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import narration as job


class NarrationJobTests(unittest.TestCase):
    def test_public_status_payload_hides_chunk_internals(self) -> None:
        job_dict = {
            "status": "running",
            "output": "/tmp/narration.wav",
            "voice": "narrator",
            "script_path": "/tmp/script.txt",
            "words_total": 2228,
            "estimated_seconds_total": 718.71,
            "created_at": "2026-08-28T14:00:00+00:00",
            "worker_started_at": "2026-08-28T14:00:00+00:00",
            "chunks": [
                {"status": "done"},
                {"status": "done"},
                {"status": "pending"},
                {"status": "pending"},
            ],
            "chunk_texts": ["a", "b", "c", "d"],
            "error": None,
        }
        payload = job._public_status_payload(job_dict)
        self.assertEqual(payload["progress"], 0.5)
        self.assertNotIn("chunks", payload)
        self.assertNotIn("chunks_total", payload)
        self.assertNotIn("chunk_texts", payload)
        self.assertNotIn("chunking_required", payload)

    def test_public_status_payload_includes_result_when_done(self) -> None:
        job_dict = {
            "status": "done",
            "output": "/tmp/narration.wav",
            "voice": "narrator",
            "script_path": "/tmp/script.txt",
            "words_total": 100,
            "estimated_seconds_total": 32.0,
            "chunks": [{"status": "done"}],
            "result": {
                "output": "/tmp/narration.wav",
                "duration_seconds": 31.5,
                "sample_rate": 24000,
                "segments": [{"path": "internal.wav"}],
            },
            "error": None,
        }
        payload = job._public_status_payload(job_dict)
        self.assertEqual(payload["progress"], 1.0)
        self.assertEqual(payload["result"]["duration_seconds"], 31.5)
        self.assertNotIn("segments", payload["result"])

    def test_job_progress_done_is_one(self) -> None:
        job_dict = {
            "status": "done",
            "chunks": [{"status": "done"}, {"status": "pending"}],
        }
        self.assertEqual(job._job_progress(job_dict), 1.0)


if __name__ == "__main__":
    unittest.main()
