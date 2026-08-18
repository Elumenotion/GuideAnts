import os
import tempfile
import unittest
from unittest import mock

from guideants_hf.transport import IncompleteDownloadError, download_hf_file


class TransportDownloadTests(unittest.TestCase):
    def setUp(self) -> None:
        self._tmpdir = tempfile.TemporaryDirectory()
        self._dest = os.path.join(self._tmpdir.name, "model.gguf")

    def tearDown(self) -> None:
        self._tmpdir.cleanup()

    def test_truncated_full_download_raises_and_preserves_tmp(self) -> None:
        payload = b"partial"

        class FakeResponse:
            status = 200
            _sent = False

            def getheader(self, name: str) -> str | None:
                if name == "Content-Length":
                    return "100"
                return None

            def read(self, size: int = -1) -> bytes:
                if self._sent:
                    return b""
                self._sent = True
                return payload

            def __enter__(self):
                return self

            def __exit__(self, *_args) -> None:
                return None

        with mock.patch("urllib.request.urlopen", return_value=FakeResponse()):
            with self.assertRaises(IncompleteDownloadError):
                download_hf_file(
                    "org/repo",
                    "model.gguf",
                    self._dest,
                    None,
                    expected_size=100,
                )

        temp_path = self._dest + ".tmp"
        self.assertTrue(os.path.isfile(temp_path))
        self.assertFalse(os.path.isfile(self._dest))
        self.assertEqual(len(payload), os.path.getsize(temp_path))

    def test_complete_download_replaces_tmp_with_destination(self) -> None:
        payload = b"complete-bytes"

        class FakeResponse:
            status = 200
            _sent = False

            def getheader(self, name: str) -> str | None:
                if name == "Content-Length":
                    return str(len(payload))
                return None

            def read(self, size: int = -1) -> bytes:
                if self._sent:
                    return b""
                self._sent = True
                return payload

            def __enter__(self):
                return self

            def __exit__(self, *_args) -> None:
                return None

        with mock.patch("urllib.request.urlopen", return_value=FakeResponse()):
            download_hf_file(
                "org/repo",
                "model.gguf",
                self._dest,
                None,
                expected_size=len(payload),
            )

        self.assertTrue(os.path.isfile(self._dest))
        self.assertFalse(os.path.isfile(self._dest + ".tmp"))
        with open(self._dest, "rb") as handle:
            self.assertEqual(payload, handle.read())


if __name__ == "__main__":
    unittest.main()
