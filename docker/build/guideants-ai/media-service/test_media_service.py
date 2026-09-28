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

        def fake_run(command, capture_output, text, check, timeout, **kwargs):  # noqa: ANN001
            if command[0] == "ffprobe":
                return subprocess.CompletedProcess(command, 0, "0\n", "")
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

    def test_process_extract_audio_request_rejects_invalid_audio_quality(self) -> None:
        source_path = self.storage_root / ".system" / "media-extract" / "abc123" / "input.mp4"
        source_path.parent.mkdir(parents=True, exist_ok=True)
        source_path.write_bytes(b"video")

        payload = media_service.ExtractAudioRequest(
            sourcePath=".system/media-extract/abc123/input.mp4",
            outputPath=".system/media-extract/abc123/output.mp3",
            audioQuality="10",
        )

        with self.assertRaises(media_service.MediaServiceError) as context:
            media_service.process_extract_audio_request(payload, storage_root=self.storage_root)

        self.assertEqual(context.exception.status_code, 400)
        self.assertIn("audioQuality must be an integer between 0 and 9.", context.exception.detail)

    def test_run_ffmpeg_raises_error_when_timeout_expires(self) -> None:
        source_path = self.storage_root / ".system" / "media-extract" / "abc123" / "input.mp4"
        output_path = self.storage_root / ".system" / "media-extract" / "abc123" / "output.mp3"
        output_path.parent.mkdir(parents=True, exist_ok=True)
        source_path.write_bytes(b"video")

        with patch.object(
            media_service.subprocess,
            "run",
            side_effect=subprocess.TimeoutExpired(cmd=["ffmpeg"], timeout=1),
        ):
            with self.assertRaises(media_service.MediaServiceError) as context:
                media_service.run_ffmpeg(
                    source_path=source_path,
                    output_path=output_path,
                    codec="libmp3lame",
                    audio_quality="2",
                    overwrite=True,
                    timeout_seconds=1,
                )

        self.assertEqual(context.exception.status_code, 500)
        self.assertIn("ffmpeg timed out after 1 seconds.", context.exception.detail)


    def test_process_extract_audio_request_rejects_source_without_audio_stream(self):
        source_path = self.storage_root / "system" / "media-extract" / "abc123" / "input.mp4"
        source_path.parent.mkdir(parents=True, exist_ok=True)
        source_path.write_bytes(b"video-only")

        payload = media_service.ExtractAudioRequest(
            sourcePath="system/media-extract/abc123/input.mp4",
            outputPath="system/media-extract/abc123/output.mp3",
        )

        def fake_run(command, capture_output, text, check, timeout, **kwargs):  # noqa: ANN001
            self.assertEqual(command[0], "ffprobe")
            return subprocess.CompletedProcess(command, 0, "", "")

        with patch.object(media_service.subprocess, "run", side_effect=fake_run):
            with self.assertRaises(media_service.MediaServiceError) as context:
                media_service.process_extract_audio_request(payload, storage_root=self.storage_root)

        self.assertEqual(context.exception.status_code, 422)
        self.assertEqual(context.exception.error_type, "NO_AUDIO_STREAM")
        self.assertIn("contains no audio stream", context.exception.detail)

    def test_process_extract_audio_request_rejects_unprobeable_source(self):
        source_path = self.storage_root / "system" / "media-extract" / "abc123" / "input.mp4"
        source_path.parent.mkdir(parents=True, exist_ok=True)
        source_path.write_bytes(b"not-a-media-file")

        payload = media_service.ExtractAudioRequest(
            sourcePath="system/media-extract/abc123/input.mp4",
            outputPath="system/media-extract/abc123/output.mp3",
        )

        def fake_run(command, capture_output, text, check, timeout, **kwargs):  # noqa: ANN001
            self.assertEqual(command[0], "ffprobe")
            return subprocess.CompletedProcess(
                command, 1, "", "input.mp4: Invalid data found when processing input"
            )

        with patch.object(media_service.subprocess, "run", side_effect=fake_run):
            with self.assertRaises(media_service.MediaServiceError) as context:
                media_service.process_extract_audio_request(payload, storage_root=self.storage_root)

        self.assertEqual(context.exception.status_code, 422)
        self.assertEqual(context.exception.error_type, "UNREADABLE_MEDIA")

    def test_process_extract_audio_request_ffprobe_timeout_is_retryable(self):
        source_path = self.storage_root / "system" / "media-extract" / "abc123" / "input.mp4"
        source_path.parent.mkdir(parents=True, exist_ok=True)
        source_path.write_bytes(b"video")

        payload = media_service.ExtractAudioRequest(
            sourcePath="system/media-extract/abc123/input.mp4",
            outputPath="system/media-extract/abc123/output.mp3",
        )

        with patch.object(
            media_service.subprocess,
            "run",
            side_effect=subprocess.TimeoutExpired(cmd=["ffprobe"], timeout=1),
        ):
            with self.assertRaises(media_service.MediaServiceError) as context:
                media_service.process_extract_audio_request(payload, storage_root=self.storage_root, timeout_seconds=1)

        self.assertEqual(context.exception.status_code, 500)
        self.assertEqual(context.exception.error_type, "UNREADABLE_MEDIA")
        self.assertIn("ffprobe timed out after 1 seconds.", context.exception.detail)

    def test_count_audio_streams_uses_valid_ffprobe_cli(self):
        source_path = self.storage_root / "input.mp4"
        source_path.write_bytes(b"video")

        captured = {}

        def fake_run(command, capture_output, text, check, timeout, **kwargs):  # noqa: ANN001
            captured["command"] = command
            captured["stdin"] = kwargs.get("stdin")
            return subprocess.CompletedProcess(command, 0, "0\n", "")

        with patch.object(media_service.subprocess, "run", side_effect=fake_run):
            count = media_service.count_audio_streams(source_path, 600)

        self.assertEqual(count, 1)
        self.assertEqual(
            captured["command"],
            [
                "ffprobe",
                "-hide_banner",
                "-loglevel",
                "error",
                "-select_streams",
                "a",
                "-show_entries",
                "stream=index",
                "-of",
                "csv=p=0",
                str(source_path),
            ],
        )
        # ffprobe has no -nostdin (ffmpeg-only); stdin must be detached by the caller.
        self.assertNotIn("-nostdin", captured["command"])
        self.assertEqual(captured["stdin"], subprocess.DEVNULL)

    def test_count_audio_streams_counts_listed_streams(self):
        source_path = self.storage_root / "input.mp4"
        source_path.write_bytes(b"video")

        def fake_run(command, capture_output, text, check, timeout, **kwargs):  # noqa: ANN001
            return subprocess.CompletedProcess(command, 0, "0\n1\n", "")

        with patch.object(media_service.subprocess, "run", side_effect=fake_run):
            self.assertEqual(media_service.count_audio_streams(source_path, 600), 2)


if __name__ == "__main__":
    unittest.main()
