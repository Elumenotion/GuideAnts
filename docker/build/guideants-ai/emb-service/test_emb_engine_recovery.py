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

import emb_service


class EmbeddingErrorRecoverableTests(unittest.TestCase):
    def test_timeout_runtime_error_is_recoverable(self) -> None:
        exc = RuntimeError("Failed to reach llama-server at http://127.0.0.1:18085/v1/embeddings: timed out")
        self.assertTrue(emb_service.embedding_error_is_recoverable(exc))

    def test_connection_refused_runtime_error_is_recoverable(self) -> None:
        exc = RuntimeError("Failed to reach llama-server at http://127.0.0.1:18085/health: [Errno 111] Connection refused")
        self.assertTrue(emb_service.embedding_error_is_recoverable(exc))

    def test_http_500_from_engine_is_not_recoverable(self) -> None:
        exc = RuntimeError("llama-server /v1/embeddings returned HTTP 500: boom")
        self.assertFalse(emb_service.embedding_error_is_recoverable(exc))


class EmbedViaEngineRecoveryTests(unittest.TestCase):
    def setUp(self) -> None:
        self.config = MagicMock()
        self.config.model_ref = "Qwen3-Embedding-0.6B-Q8_0.gguf"

    @patch.object(emb_service, "embed_via_engine", side_effect=[RuntimeError("timed out"), [[0.1, 0.2]]])
    @patch.object(emb_service, "_restart_engine_process_locked")
    @patch.object(emb_service, "STATE")
    def test_restarts_once_and_retries(
        self,
        state_mock: MagicMock,
        restart_mock: MagicMock,
        embed_mock: MagicMock,
    ) -> None:
        state_mock.config = self.config
        result = emb_service.embed_via_engine_with_recovery(["hello"], "document")
        self.assertEqual(result, [[0.1, 0.2]])
        self.assertEqual(embed_mock.call_count, 2)
        restart_mock.assert_called_once()

    @patch.object(emb_service, "embed_via_engine", side_effect=RuntimeError("llama-server /v1/embeddings returned HTTP 500: nope"))
    @patch.object(emb_service, "_restart_engine_process_locked")
    @patch.object(emb_service, "STATE")
    def test_does_not_restart_on_non_recoverable_error(
        self,
        state_mock: MagicMock,
        restart_mock: MagicMock,
        _embed_mock: MagicMock,
    ) -> None:
        state_mock.config = self.config
        with self.assertRaises(RuntimeError):
            emb_service.embed_via_engine_with_recovery(["hello"], "document")
        restart_mock.assert_not_called()

    @patch.dict(emb_service.os.environ, {"GA_EMB_RESTART_ON_FAILURE": "0"})
    @patch.object(emb_service, "embed_via_engine", side_effect=RuntimeError("timed out"))
    @patch.object(emb_service, "_restart_engine_process_locked")
    @patch.object(emb_service, "STATE")
    def test_restart_can_be_disabled(
        self,
        state_mock: MagicMock,
        restart_mock: MagicMock,
        _embed_mock: MagicMock,
    ) -> None:
        state_mock.config = self.config
        with self.assertRaises(RuntimeError):
            emb_service.embed_via_engine_with_recovery(["hello"], "document")
        restart_mock.assert_not_called()


if __name__ == "__main__":
    unittest.main()
