"""Tests for crash-safe screen recorder startup."""

from __future__ import annotations

import tempfile
import unittest
from pathlib import Path
from unittest.mock import MagicMock, patch

from scripts.screen_recorder import MonitorInfo, ScreenRecorder


class ScreenRecorderTests(unittest.TestCase):
    def _recorder(self, output_dir: Path) -> ScreenRecorder:
        with patch(
            "scripts.screen_recorder._resolve_monitor",
            return_value=MonitorInfo(index=1, left=0, top=0, width=4, height=4),
        ):
            return ScreenRecorder(monitor=1, output_dir=output_dir, filename_prefix="video")

    def test_refuses_to_overwrite_existing_video(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            output_dir = Path(tmp)
            video = output_dir / "video.mp4"
            original = b"original media"
            video.write_bytes(original)
            recorder = self._recorder(output_dir)

            with (
                patch("scripts.screen_recorder.shutil.which", return_value="ffmpeg"),
                patch("scripts.screen_recorder.subprocess.Popen") as popen,
                self.assertRaises(FileExistsError),
            ):
                recorder.start()

            popen.assert_not_called()
            self.assertEqual(original, video.read_bytes())

    def test_reports_ffmpeg_startup_failure(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            recorder = self._recorder(Path(tmp))
            with (
                patch("scripts.screen_recorder.shutil.which", return_value="ffmpeg"),
                patch(
                    "scripts.screen_recorder.subprocess.Popen",
                    side_effect=OSError("ffmpeg could not start"),
                ),
            ):
                with self.assertRaisesRegex(RuntimeError, "failed to start"):
                    recorder.start()

    def test_reports_capture_thread_failure(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            recorder = self._recorder(Path(tmp))
            process = MagicMock()
            process.poll.return_value = None
            process.returncode = 0
            process.stderr.read.return_value = b""
            with (
                patch("scripts.screen_recorder.shutil.which", return_value="ffmpeg"),
                patch("scripts.screen_recorder.subprocess.Popen", return_value=process),
                patch(
                    "scripts.screen_recorder.mss.mss",
                    side_effect=RuntimeError("screen capture unavailable"),
                ),
            ):
                with self.assertRaisesRegex(RuntimeError, "failed to start"):
                    recorder.start()


if __name__ == "__main__":
    unittest.main()
