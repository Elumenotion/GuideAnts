import os
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
os.environ.setdefault("CATALOG_PATH", str(_SERVICE_ROOT / "catalog" / "manifest.json"))

for _optional in ("uvicorn", "fastapi", "fastapi.responses", "pydantic", "soundfile"):
    if _optional not in sys.modules:
        sys.modules[_optional] = MagicMock()

import asr_service


class TranscriptionErrorRecoverableTests(unittest.TestCase):
    def test_timeout_runtime_error_is_recoverable(self) -> None:
        exc = RuntimeError(
            "Failed to reach audiocpp_server at http://127.0.0.1:18082/v1/audio/transcriptions: timed out"
        )
        self.assertTrue(asr_service.transcription_error_is_recoverable(exc))

    def test_http_503_from_engine_is_recoverable(self) -> None:
        exc = RuntimeError("audiocpp_server /v1/audio/transcriptions returned HTTP 503.")
        self.assertTrue(asr_service.transcription_error_is_recoverable(exc))

    def test_http_500_from_engine_is_recoverable(self) -> None:
        exc = RuntimeError("audiocpp_server /v1/audio/transcriptions returned HTTP 500.")
        self.assertTrue(asr_service.transcription_error_is_recoverable(exc))

    def test_http_400_from_engine_is_not_recoverable(self) -> None:
        exc = RuntimeError("audiocpp_server /v1/audio/transcriptions returned HTTP 400.")
        self.assertFalse(asr_service.transcription_error_is_recoverable(exc))


class TranscribeWithEngineRecoveryTests(unittest.TestCase):
    def setUp(self) -> None:
        self.config = MagicMock()
        self.config.model_ref = "Qwen3-ASR-0.6B"

    def tearDown(self) -> None:
        asr_service.clear_inference_failed()

    @patch.object(asr_service, "transcribe_via_engine", side_effect=[RuntimeError("timed out"), ("hello", None)])
    @patch.object(asr_service, "_restart_engine_process_locked")
    def test_restarts_once_and_retries(self, restart_mock: MagicMock, transcribe_mock: MagicMock) -> None:
        result = asr_service.transcribe_via_engine_with_recovery(self.config, "/tmp/a.wav")
        self.assertEqual(result, ("hello", None))
        self.assertEqual(transcribe_mock.call_count, 2)
        restart_mock.assert_called_once()

    @patch.object(
        asr_service,
        "transcribe_via_engine",
        side_effect=[
            RuntimeError("audiocpp_server /v1/audio/transcriptions returned HTTP 500."),
            ("hello", None),
        ],
    )
    @patch.object(asr_service, "_restart_engine_process_locked")
    def test_restarts_once_on_http_500(self, restart_mock: MagicMock, transcribe_mock: MagicMock) -> None:
        result = asr_service.transcribe_via_engine_with_recovery(self.config, "/tmp/a.wav")
        self.assertEqual(result, ("hello", None))
        self.assertEqual(transcribe_mock.call_count, 2)
        restart_mock.assert_called_once()

    @patch.object(
        asr_service,
        "transcribe_via_engine",
        return_value=("", None),
    )
    @patch.object(asr_service, "_restart_engine_process_locked")
    def test_empty_transcript_is_success_not_a_restart(self, restart_mock: MagicMock, transcribe_mock: MagicMock) -> None:
        result = asr_service.transcribe_via_engine_with_recovery(self.config, "/tmp/a.wav")
        self.assertEqual(result, ("", None))
        transcribe_mock.assert_called_once()
        restart_mock.assert_not_called()

    @patch.object(
        asr_service,
        "transcribe_via_engine",
        side_effect=RuntimeError("audiocpp_server /v1/audio/transcriptions returned HTTP 400."),
    )
    @patch.object(asr_service, "_restart_engine_process_locked")
    def test_does_not_restart_on_http_400(
        self, restart_mock: MagicMock, _transcribe_mock: MagicMock
    ) -> None:
        with self.assertRaises(RuntimeError):
            asr_service.transcribe_via_engine_with_recovery(self.config, "/tmp/a.wav")
        restart_mock.assert_not_called()
        self.assertFalse(asr_service.STATE.inference_failed)

    @patch.dict(asr_service.os.environ, {"GA_ASR_RESTART_ON_FAILURE": "0"})
    @patch.object(asr_service, "transcribe_via_engine", side_effect=RuntimeError("timed out"))
    @patch.object(asr_service, "_restart_engine_process_locked")
    def test_restart_can_be_disabled(self, restart_mock: MagicMock, _transcribe_mock: MagicMock) -> None:
        with self.assertRaises(RuntimeError):
            asr_service.transcribe_via_engine_with_recovery(self.config, "/tmp/a.wav")
        restart_mock.assert_not_called()
        self.assertTrue(asr_service.STATE.inference_failed)


class StartEngineFailedStateTests(unittest.TestCase):
    def test_noop_skipped_when_inference_failed(self) -> None:
        asr_service.STATE.inference_failed = True
        asr_service.STATE.config = MagicMock()
        asr_service.STATE.config.model_path = "Qwen3-ASR-0.6B"
        asr_service.STATE.model_ref = "Qwen3-ASR-0.6B"
        asr_service.STATE.engine_process = MagicMock()
        asr_service.STATE.engine_process.poll.return_value = None

        with patch.object(asr_service, "resolve_model_target", return_value=("Qwen3-ASR-0.6B", "Qwen3-ASR-0.6B", {})):
            with patch.object(asr_service, "build_engine_config") as build_mock:
                with patch.object(asr_service, "stop_engine"):
                    with patch.object(asr_service, "wait_for_engine_ready"):
                        with patch.object(asr_service, "run_model_warmup", return_value={"warmupEnabled": False}):
                            with patch.object(asr_service.subprocess, "Popen") as popen_mock:
                                popen_mock.return_value = MagicMock()
                                build_mock.return_value = MagicMock(
                                    server_path="/usr/local/bin/audiocpp_server",
                                    config_path="/tmp/server.json",
                                    model_path="Qwen3-ASR-0.6B",
                                    model_ref="Qwen3-ASR-0.6B",
                                )
                                result = asr_service.start_engine(asr_service.LoadModelRequest(model_path="Qwen3-ASR-0.6B"))

        self.assertNotEqual(result.get("action"), "noop-already-loaded")
        asr_service.clear_inference_failed()
        asr_service.STATE.config = None
        asr_service.STATE.engine_process = None


if __name__ == "__main__":
    unittest.main()
