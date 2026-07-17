import re
import unittest
from pathlib import Path

_REPO_ROOT = Path(__file__).resolve().parents[2]
_NGINX_CONF = (_REPO_ROOT / "nginx.conf").read_text(encoding="utf-8")

_INFERENCE_ROUTES: dict[str, int] = {
    "/llama-cpp/": 8080,
    "/asr/": 8082,
    "/asr/admin/": 8082,
    "/sd/": 8083,
    "/sd/admin/": 8083,
    "/tts/": 8084,
    "/tts/admin/": 8084,
    "/emb/": 8085,
    "/emb/admin/": 8085,
    "/media/": 8087,
}

_CONTROL_PLANE_PORT = 8086


def _proxy_port(location: str) -> int | None:
    pattern = rf'location\s+{re.escape(location)}\s*\{{[^}}]*?proxy_pass\s+http://127\.0\.0\.1:(\d+)/'
    match = re.search(pattern, _NGINX_CONF, flags=re.DOTALL)
    if not match:
        return None
    return int(match.group(1))


class InferenceServiceIsolationTests(unittest.TestCase):
    def test_each_inference_route_has_dedicated_upstream_port(self) -> None:
        for location, expected_port in _INFERENCE_ROUTES.items():
            actual_port = _proxy_port(location)
            self.assertIsNotNone(actual_port, f"missing nginx location for {location}")
            self.assertEqual(
                actual_port,
                expected_port,
                f"{location} must proxy to dedicated engine port {expected_port}, got {actual_port}",
            )

        self.assertEqual(set(_INFERENCE_ROUTES.values()), {8080, 8082, 8083, 8084, 8085, 8087})

    def test_no_inference_route_targets_ga_admin_port(self) -> None:
        for location in _INFERENCE_ROUTES:
            actual_port = _proxy_port(location)
            self.assertIsNotNone(actual_port)
            self.assertNotEqual(
                actual_port,
                _CONTROL_PLANE_PORT,
                f"{location} must not proxy inference/admin traffic through ga-admin:{_CONTROL_PLANE_PORT}",
            )

    def test_llama_admin_is_control_plane_only(self) -> None:
        self.assertEqual(_proxy_port("/llama-admin/"), _CONTROL_PLANE_PORT)


if __name__ == "__main__":
    unittest.main()
