import re
import unittest
from pathlib import Path

_REPO_ROOT = Path(__file__).resolve().parents[2]
_NGINX_TEMPLATE = (_REPO_ROOT / "nginx.conf.template").read_text(encoding="utf-8")
_NGINX_TEMPLATE_PATH = _REPO_ROOT / "nginx.conf.template"
_DEFAULT_ASR_BODY_LIMIT = "300m"
_ASR_BODY_PLACEHOLDER = "__GA_NGINX_ASR_CLIENT_MAX_BODY_SIZE__"
_MIN_ASR_UPLOAD_MB = 128


def render_nginx_template(asr_body_limit: str = _DEFAULT_ASR_BODY_LIMIT) -> str:
    return _NGINX_TEMPLATE.replace(_ASR_BODY_PLACEHOLDER, asr_body_limit)


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


def _proxy_port(nginx_conf: str, location: str) -> int | None:
    pattern = rf'location\s+{re.escape(location)}\s*\{{[^}}]*?proxy_pass\s+http://127\.0\.0\.1:(\d+)/'
    match = re.search(pattern, nginx_conf, flags=re.DOTALL)
    if not match:
        return None
    return int(match.group(1))


def _location_block(nginx_conf: str, location: str) -> str | None:
    pattern = rf'location\s+{re.escape(location)}\s*\{{(.*?)\n\s*\}}'
    match = re.search(pattern, nginx_conf, flags=re.DOTALL)
    if not match:
        return None
    return match.group(1)


def _client_max_body_size_megabytes(nginx_conf: str, location: str) -> int | None:
    block = _location_block(nginx_conf, location)
    if block is None:
        return None
    match = re.search(r"client_max_body_size\s+(\d+)m\s*;", block)
    if not match:
        return None
    return int(match.group(1))


class InferenceServiceIsolationTests(unittest.TestCase):
    def setUp(self) -> None:
        self.nginx_conf = render_nginx_template()

    def test_each_inference_route_has_dedicated_upstream_port(self) -> None:
        for location, expected_port in _INFERENCE_ROUTES.items():
            actual_port = _proxy_port(self.nginx_conf, location)
            self.assertIsNotNone(actual_port, f"missing nginx location for {location}")
            self.assertEqual(
                actual_port,
                expected_port,
                f"{location} must proxy to dedicated engine port {expected_port}, got {actual_port}",
            )

        self.assertEqual(set(_INFERENCE_ROUTES.values()), {8080, 8082, 8083, 8084, 8085, 8087})

    def test_no_inference_route_targets_ga_admin_port(self) -> None:
        for location in _INFERENCE_ROUTES:
            actual_port = _proxy_port(self.nginx_conf, location)
            self.assertIsNotNone(actual_port)
            self.assertNotEqual(
                actual_port,
                _CONTROL_PLANE_PORT,
                f"{location} must not proxy inference/admin traffic through ga-admin:{_CONTROL_PLANE_PORT}",
            )

    def test_llama_admin_is_control_plane_only(self) -> None:
        self.assertEqual(_proxy_port(self.nginx_conf, "/llama-admin/"), _CONTROL_PLANE_PORT)

    def test_asr_template_declares_configurable_upload_limit(self) -> None:
        self.assertIn(_ASR_BODY_PLACEHOLDER, _NGINX_TEMPLATE)

    def test_default_asr_upload_limit_accepts_large_narration_wav(self) -> None:
        # webapi forwards up to 300 MB; observed failing narration.wav payloads were ~128 MB.
        limit_mb = _client_max_body_size_megabytes(self.nginx_conf, "/asr/")
        self.assertIsNotNone(limit_mb, "missing /asr/ client_max_body_size override")
        self.assertGreaterEqual(limit_mb, _MIN_ASR_UPLOAD_MB)


if __name__ == "__main__":
    unittest.main()
