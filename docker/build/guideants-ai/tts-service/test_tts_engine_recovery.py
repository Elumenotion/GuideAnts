import sys
import unittest
from pathlib import Path
from unittest.mock import MagicMock, patch

_SERVICE_ROOT = Path(__file__).resolve().parent
_LIB_ROOT = _SERVICE_ROOT.parent / "lib"
if str(_SERVICE_ROOT) not in sys.path:
    sys.path.insert(0, str(_SERVICE_ROOT))
if str(_LIB_ROOT) not in sys.path:
    sys.path.insert(0, str(_LIB_ROOT))

for _optional in ("uvicorn", "fastapi", "fastapi.responses", "pydantic"):
    if _optional not in sys.modules:
        sys.modules[_optional] = MagicMock()

import tts_service


class SynthesisErrorRecoverableTests(unittest.TestCase):
    def test_timeout_runtime_error_is_recoverable(self) -> None:
        exc = RuntimeError("Failed to reach audiocpp_server at http://127.0.0.1:18084/v1/audio/speech: timed out")
        self.assertTrue(tts_service.synthesis_error_is_recoverable(exc))

    def test_connection_refused_runtime_error_is_recoverable(self) -> None:
        exc = RuntimeError("Failed to reach audiocpp_server at http://127.0.0.1:18084/health: [Errno 111] Connection refused")
        self.assertTrue(tts_service.synthesis_error_is_recoverable(exc))

    def test_http_500_from_engine_is_not_recoverable(self) -> None:
        exc = RuntimeError("audiocpp_server /v1/audio/speech returned HTTP 500: boom")
        self.assertFalse(tts_service.synthesis_error_is_recoverable(exc))


class SynthesizeWithEngineRecoveryTests(unittest.TestCase):
    def setUp(self) -> None:
        self.config = MagicMock()
        self.config.model_ref = "chatterbox"

    @patch.object(tts_service, "synthesize_via_engine", side_effect=[RuntimeError("timed out"), b"wav"])
    @patch.object(tts_service, "_restart_engine_process_locked")
    def test_restarts_once_and_retries(self, restart_mock: MagicMock, synthesize_mock: MagicMock) -> None:
        result = tts_service.synthesize_with_engine_recovery(self.config, "hello", {}, 1)
        self.assertEqual(result, b"wav")
        self.assertEqual(synthesize_mock.call_count, 2)
        restart_mock.assert_called_once()

    @patch.object(tts_service, "synthesize_via_engine", side_effect=RuntimeError("audiocpp_server /v1/audio/speech returned HTTP 500: nope"))
    @patch.object(tts_service, "_restart_engine_process_locked")
    def test_does_not_restart_on_non_recoverable_error(self, restart_mock: MagicMock, _synthesize_mock: MagicMock) -> None:
        with self.assertRaises(RuntimeError):
            tts_service.synthesize_with_engine_recovery(self.config, "hello", {}, 1)
        restart_mock.assert_not_called()

    @patch.dict(tts_service.os.environ, {"GA_TTS_RESTART_ON_FAILURE": "0"})
    @patch.object(tts_service, "synthesize_via_engine", side_effect=RuntimeError("timed out"))
    @patch.object(tts_service, "_restart_engine_process_locked")
    def test_restart_can_be_disabled(self, restart_mock: MagicMock, _synthesize_mock: MagicMock) -> None:
        with self.assertRaises(RuntimeError):
            tts_service.synthesize_with_engine_recovery(self.config, "hello", {}, 1)
        restart_mock.assert_not_called()


if __name__ == "__main__":
    unittest.main()
