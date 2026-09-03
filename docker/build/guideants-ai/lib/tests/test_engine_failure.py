import os
import sys
import unittest
import urllib.error

LIB_ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
if LIB_ROOT not in sys.path:
    sys.path.insert(0, LIB_ROOT)

from guideants_http.engine_failure import (
    engine_rpc_exception_is_engine_failure,
    wrapper_http_status_for_engine_exception,
)


class EngineRpcExceptionIsEngineFailureTests(unittest.TestCase):
    def test_timeout_is_engine_failure(self) -> None:
        self.assertTrue(engine_rpc_exception_is_engine_failure(TimeoutError("timed out")))

    def test_http_500_is_engine_failure(self) -> None:
        exc = RuntimeError("audiocpp_server /v1/audio/transcriptions returned HTTP 500.")
        self.assertTrue(engine_rpc_exception_is_engine_failure(exc))

    def test_http_503_is_engine_failure(self) -> None:
        exc = RuntimeError("audiocpp_server /v1/audio/speech returned HTTP 503.")
        self.assertTrue(engine_rpc_exception_is_engine_failure(exc))

    def test_http_400_is_not_engine_failure(self) -> None:
        exc = RuntimeError("audiocpp_server /v1/audio/transcriptions returned HTTP 400.")
        self.assertFalse(engine_rpc_exception_is_engine_failure(exc))

    def test_http_408_is_engine_failure(self) -> None:
        exc = RuntimeError("audiocpp_server /v1/audio/transcriptions returned HTTP 408.")
        self.assertTrue(engine_rpc_exception_is_engine_failure(exc))

    def test_urlerror_is_engine_failure(self) -> None:
        exc = urllib.error.URLError("connection refused")
        self.assertTrue(engine_rpc_exception_is_engine_failure(exc))

    def test_empty_wav_runtime_error_is_engine_failure(self) -> None:
        exc = RuntimeError("audiocpp_server returned empty WAV payload")
        self.assertTrue(engine_rpc_exception_is_engine_failure(exc))

    def test_http_422_is_not_engine_failure(self) -> None:
        exc = RuntimeError("audiocpp_server /v1/audio/transcriptions returned HTTP 422.")
        self.assertFalse(engine_rpc_exception_is_engine_failure(exc))

    def test_wrapper_passes_through_http_400(self) -> None:
        exc = RuntimeError("audiocpp_server /v1/audio/transcriptions returned HTTP 400.")
        self.assertEqual(wrapper_http_status_for_engine_exception(exc), 400)

    def test_wrapper_maps_timeout_to_500(self) -> None:
        self.assertEqual(wrapper_http_status_for_engine_exception(TimeoutError("timed out")), 500)

    def test_wrapper_maps_http_500_to_500(self) -> None:
        exc = RuntimeError("audiocpp_server /v1/audio/transcriptions returned HTTP 500.")
        self.assertEqual(wrapper_http_status_for_engine_exception(exc), 500)

    def test_wrapper_maps_http_408_to_500(self) -> None:
        exc = RuntimeError("audiocpp_server /v1/audio/transcriptions returned HTTP 408.")
        self.assertEqual(wrapper_http_status_for_engine_exception(exc), 500)


if __name__ == "__main__":
    unittest.main()
