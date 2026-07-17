import os
import sys
import unittest

LIB_ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
if LIB_ROOT not in sys.path:
    sys.path.insert(0, LIB_ROOT)

from guideants_http.request_timeout import (  # noqa: E402
    REQUEST_TIMEOUT_HEADER,
    absolute_max_request_timeout_seconds,
    clamp_request_timeout_seconds,
    resolve_request_timeout_seconds,
)


class RequestTimeoutTests(unittest.TestCase):
    def test_uses_env_default_when_header_missing(self) -> None:
        self.assertEqual(resolve_request_timeout_seconds({}, 300), 300)

    def test_uses_header_when_present(self) -> None:
        headers = {REQUEST_TIMEOUT_HEADER: "600"}
        self.assertEqual(resolve_request_timeout_seconds(headers, 300), 600)

    def test_header_is_case_insensitive(self) -> None:
        headers = {"X-Ga-Request-Timeout-Seconds": "450"}
        self.assertEqual(resolve_request_timeout_seconds(headers, 300), 450)

    def test_invalid_header_falls_back_to_env_default(self) -> None:
        headers = {REQUEST_TIMEOUT_HEADER: "not-a-number"}
        self.assertEqual(resolve_request_timeout_seconds(headers, 400), 400)

    def test_clamps_to_absolute_max(self) -> None:
        headers = {REQUEST_TIMEOUT_HEADER: "999999999"}
        self.assertEqual(
            resolve_request_timeout_seconds(headers, 300),
            absolute_max_request_timeout_seconds(),
        )

    def test_clamps_to_explicit_max(self) -> None:
        headers = {REQUEST_TIMEOUT_HEADER: "900"}
        self.assertEqual(resolve_request_timeout_seconds(headers, 300, max_timeout_seconds=600), 600)

    def test_clamp_request_timeout_seconds_never_returns_zero(self) -> None:
        self.assertEqual(clamp_request_timeout_seconds(0), 1)
        self.assertEqual(clamp_request_timeout_seconds(-5), 1)


if __name__ == "__main__":
    unittest.main()
