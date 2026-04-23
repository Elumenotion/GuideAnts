import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parent))

import media_service


class MediaServiceTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp_dir = tempfile.TemporaryDirectory()
        self.storage_root = Path(self.temp_dir.name)

    def tearDown(self) -> None:
        self.temp_dir.cleanup()

    def test_resolve_storage_path_allows_valid_relative_path(self) -> None:
        resolved = media_service.resolve_storage_path(
            ".system/media-extract/abc123/input.mp4",
            "sourcePath",
            self.storage_root,
        )

        self.assertEqual(
            resolved,
            self.storage_root / ".system" / "media-extract" / "abc123" / "input.mp4",
        )

    def test_resolve_storage_path_rejects_traversal(self) -> None:
        with self.assertRaises(media_service.MediaServiceError) as context:
            media_service.resolve_storage_path("../../outside-root.mp4", "sourcePath", self.storage_root)

        self.assertEqual(context.exception.status_code, 400)

    def test_process_extract_audio_request_rejects_missing_source(self) -> None:
        payload = media_service.ExtractAudioRequest(
            sourcePath=".system/media-extract/abc123/input.mp4",
            outputPath=".system/media-extract/abc123/output.mp3",
        )

        with self.assertRaises(media_service.MediaServiceError) as context:
            media_service.process_extract_audio_request(payload, storage_root=self.storage_root)

        self.assertEqual(context.exception.status_code, 404)

    def test_process_extract_audio_request_writes_output_successfully(self) -> None:
        source_path = self.storage_root / ".system" / "media-extract" / "abc123" / "input.mp4"
        source_path.parent.mkdir(parents=True, exist_ok=True)
        source_path.write_bytes(b"video")

        payload = media_service.ExtractAudioRequest(
            sourcePath=".system/media-extract/abc123/input.mp4",
            outputPath=".system/media-extract/abc123/output.mp3",
        )

        def fake_run(command, capture_output, text, check):  # noqa: ANN001
            output_path = Path(command[-1])
            output_path.write_bytes(b"mp3-data")
            return subprocess.CompletedProcess(command, 0, "", "")

        with patch.object(media_service.subprocess, "run", side_effect=fake_run):
            result = media_service.process_extract_audio_request(payload, storage_root=self.storage_root)

        self.assertEqual(result.outputPath, ".system/media-extract/abc123/output.mp3")
        self.assertEqual(result.contentType, "audio/mpeg")
        self.assertEqual(result.fileSize, len(b"mp3-data"))

    def test_process_extract_audio_request_rejects_existing_output_when_overwrite_disabled(self) -> None:
        source_path = self.storage_root / ".system" / "media-extract" / "abc123" / "input.mp4"
        output_path = self.storage_root / ".system" / "media-extract" / "abc123" / "output.mp3"
        output_path.parent.mkdir(parents=True, exist_ok=True)
        source_path.write_bytes(b"video")
        output_path.write_bytes(b"existing")

        payload = media_service.ExtractAudioRequest(
            sourcePath=".system/media-extract/abc123/input.mp4",
            outputPath=".system/media-extract/abc123/output.mp3",
            overwrite=False,
        )

        with self.assertRaises(media_service.MediaServiceError) as context:
            media_service.process_extract_audio_request(payload, storage_root=self.storage_root)

        self.assertEqual(context.exception.status_code, 409)


if __name__ == "__main__":
    unittest.main()
