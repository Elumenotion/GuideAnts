import sys
import unittest
from pathlib import Path
from typing import Any
from unittest.mock import patch

_GATEWAY = Path(__file__).resolve().parents[1] / "audiocpp-skill-gateway"
if str(_GATEWAY) not in sys.path:
    sys.path.insert(0, str(_GATEWAY))

import skill_gateway


class ProbeUpstreamBusyTests(unittest.TestCase):
    def test_listening_plus_timeout_is_busy_not_down(self) -> None:
        with patch.object(skill_gateway, "tcp_listening", return_value=True):
            with patch.object(
                skill_gateway,
                "probe_url",
                return_value={"reachable": False, "error": "TimeoutError: timed out"},
            ):
                result = skill_gateway.probe_upstream("http://127.0.0.1:18084/health", timeout=0.1)

        self.assertTrue(result["reachable"])
        self.assertEqual(result["state"], "busy")
        self.assertTrue(result["busy"])
        self.assertTrue(result["listening"])

    def test_tcp_down_is_down(self) -> None:
        with patch.object(skill_gateway, "tcp_listening", return_value=False):
            result = skill_gateway.probe_upstream("http://127.0.0.1:18084/health", timeout=0.1)

        self.assertFalse(result["reachable"])
        self.assertEqual(result["state"], "down")
        self.assertFalse(result["listening"])

    def test_health_endpoint_does_not_http_probe_engines(self) -> None:
        calls: list[str] = []

        def capture_probe(url: str, timeout: float = 0.5) -> dict:
            calls.append(url)
            return {"reachable": True, "status": 200, "body": {"status": "ok"}, "state": "up", "busy": False, "listening": True}

        with patch.object(skill_gateway, "require_token", return_value="tok"):
            with patch.object(skill_gateway, "tcp_listening", return_value=True):
                with patch.object(skill_gateway, "probe_many", side_effect=lambda targets, timeout=0.5: {
                    name: capture_probe(url, timeout) for name, url in targets.items()
                }):
                    body = skill_gateway.health()

        self.assertEqual(body["status"], "ok")
        self.assertEqual(body["engines"]["tts"]["state"], "listening")
        # Only wrappers are HTTP-probed on /health.
        self.assertTrue(all(":8084/health" in url or ":8082/health" in url for url in calls), calls)
        self.assertFalse(any(":18084" in url or ":18082" in url or ":18099" in url for url in calls), calls)


    def test_health_marks_busy_from_gateway_in_flight(self) -> None:
        with patch.object(skill_gateway, "require_token", return_value="tok"):
            with patch.object(skill_gateway, "tcp_listening", return_value=True):
                with patch.object(
                    skill_gateway,
                    "probe_many",
                    return_value={
                        "asr": {
                            "reachable": True,
                            "status": 200,
                            "body": {"status": "ok", "loaded": True, "busy": False},
                            "state": "up",
                            "busy": False,
                            "listening": True,
                        },
                        "tts": {
                            "reachable": True,
                            "status": 200,
                            "body": {"status": "ok", "loaded": True, "busy": False},
                            "state": "up",
                            "busy": False,
                            "listening": True,
                        },
                    },
                ):
                    skill_gateway.track_proxy_start(
                        "req-1",
                        route="tts",
                        method="POST",
                        work={"kind": "speech", "inputChars": 120, "model": "chatterbox"},
                    )
                    try:
                        body = skill_gateway.health()
                    finally:
                        skill_gateway.track_proxy_finish("req-1")

        self.assertEqual(body["engines"]["tts"]["state"], "busy")
        self.assertTrue(body["engines"]["tts"]["busy"])
        self.assertEqual(body["engines"]["tts"]["gatewayInFlight"], 1)
        self.assertTrue(body["wrappers"]["tts"]["busy"])
        self.assertTrue(body["wrappers"]["tts"]["body"]["busy"])

    def test_summarize_proxy_work_speech(self) -> None:
        work = skill_gateway.summarize_proxy_work(
            "v1/audio/speech",
            b'{"model":"chatterbox","input":"hello world","voice":"doug"}',
        )
        self.assertEqual(work["kind"], "speech")
        self.assertEqual(work["inputChars"], 11)
        self.assertEqual(work["inputWords"], 2)
        self.assertEqual(work["model"], "chatterbox")
        self.assertEqual(work["voice"], "doug")

    def test_mark_recovery_requested_is_once_per_request(self) -> None:
        skill_gateway.track_proxy_start("req-recover", route="tts", method="POST", work={})
        try:
            self.assertTrue(skill_gateway.mark_recovery_requested("req-recover"))
            self.assertFalse(skill_gateway.mark_recovery_requested("req-recover"))
        finally:
            skill_gateway.track_proxy_finish("req-recover")

    def test_stalled_in_flight_marks_failed(self) -> None:
        with patch.object(skill_gateway, "require_token", return_value="tok"):
            with patch.object(skill_gateway, "tcp_listening", return_value=True):
                with patch.object(
                    skill_gateway,
                    "probe_many",
                    return_value={
                        "asr": {
                            "reachable": True,
                            "status": 200,
                            "body": {"status": "ok", "loaded": True, "busy": False},
                            "state": "up",
                            "busy": False,
                            "listening": True,
                        },
                        "tts": {
                            "reachable": True,
                            "status": 200,
                            "body": {"status": "ok", "loaded": True, "busy": False},
                            "state": "up",
                            "busy": False,
                            "listening": True,
                        },
                    },
                ):
                    with patch.object(skill_gateway, "proxy_timeout_seconds", return_value=0):
                        skill_gateway.track_proxy_start(
                            "req-stall",
                            route="tts",
                            method="POST",
                            work={"kind": "speech"},
                        )
                        try:
                            body = skill_gateway.health()
                        finally:
                            skill_gateway.track_proxy_finish("req-stall")

        self.assertEqual(body["engines"]["tts"]["state"], "failed")
        self.assertTrue(body["engines"]["tts"]["failed"])
        self.assertTrue(body["wrappers"]["tts"]["failed"])

    def test_report_wrapper_engine_failure_posts_to_tts_admin(self) -> None:
        posted: dict[str, Any] = {}

        class FakeResponse:
            status = 200

            def read(self) -> bytes:
                return b'{"ok":true}'

            def __enter__(self) -> "FakeResponse":
                return self

            def __exit__(self, *args: object) -> None:
                return None

        def fake_urlopen(req: object, timeout: float = 30) -> FakeResponse:
            posted["url"] = req.full_url
            posted["body"] = req.data
            return FakeResponse()

        with patch.object(skill_gateway.urllib.request, "urlopen", side_effect=fake_urlopen):
            skill_gateway.report_wrapper_engine_failure(
                "tts",
                reason="proxy_timeout",
                request_id="abc",
            )

        self.assertEqual(posted["url"], "http://127.0.0.1:8084/admin/report-engine-failure")
        self.assertIn(b"proxy_timeout", posted["body"])


if __name__ == "__main__":
    unittest.main()
