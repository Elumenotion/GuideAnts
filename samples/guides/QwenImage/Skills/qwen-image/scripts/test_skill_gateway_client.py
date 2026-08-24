#!/usr/bin/env python3
"""Unit tests for qwen-image skill_gateway_client and notebook path scoping."""
from __future__ import annotations

import json
import os
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock
from urllib.error import HTTPError

import skill_gateway_client as client

# image_tool lives in sibling skill folders; import from generate copy
sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "qwen-image-generate" / "scripts"))
import image_tool  # noqa: E402


class SkillGatewayClientTests(unittest.TestCase):
    def setUp(self) -> None:
        self._env = os.environ.copy()

    def tearDown(self) -> None:
        os.environ.clear()
        os.environ.update(self._env)

    def test_using_skill_gateway_requires_base_url(self) -> None:
        os.environ.pop("QWEN_IMAGE_SKILL_BASE_URL", None)
        self.assertFalse(client.using_skill_gateway())

    def test_gateway_headers_requires_token(self) -> None:
        os.environ["QWEN_IMAGE_SKILL_BASE_URL"] = "http://max:8189/qwen-image-skill"
        os.environ.pop("QWEN_IMAGE_SKILL_TOKEN", None)
        with self.assertRaises(SystemExit):
            client.gateway_headers()

    @mock.patch("urllib.request.urlopen")
    def test_fetch_capabilities(self, urlopen: mock.Mock) -> None:
        os.environ["QWEN_IMAGE_SKILL_BASE_URL"] = "http://max:8189/qwen-image-skill"
        os.environ["QWEN_IMAGE_SKILL_TOKEN"] = "secret"
        response = mock.Mock()
        response.read.return_value = json.dumps(
            {"image_generate_bf16_ready": True}
        ).encode("utf-8")
        response.__enter__ = mock.Mock(return_value=response)
        response.__exit__ = mock.Mock(return_value=False)
        urlopen.return_value = response

        caps = client.fetch_capabilities()
        self.assertTrue(caps["image_generate_bf16_ready"])
        request = urlopen.call_args[0][0]
        self.assertEqual(request.get_method(), "GET")
        self.assertTrue(request.full_url.endswith("/v1/capabilities"))
        header_map = {k: v for k, v in request.header_items()}
        self.assertEqual(header_map.get("X-qwen-image-skill-token"), "secret")

    @mock.patch("urllib.request.urlopen")
    def test_probe_gateway_http_error(self, urlopen: mock.Mock) -> None:
        os.environ["QWEN_IMAGE_SKILL_BASE_URL"] = "http://max:8189/qwen-image-skill"
        os.environ["QWEN_IMAGE_SKILL_TOKEN"] = "secret"
        urlopen.side_effect = HTTPError(
            "http://max/health",
            401,
            "unauthorized",
            hdrs=None,
            fp=mock.Mock(read=mock.Mock(return_value=b"no")),
        )
        report = client.probe_gateway()
        self.assertFalse(report["open"])
        self.assertEqual(report["status"], 401)


class NotebookPathTests(unittest.TestCase):
    def test_path_escape_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / ".guideants").mkdir()
            (root / ".guideants" / "notebook.json").write_text("{}", encoding="utf-8")
            (root / "Output").mkdir()
            with self.assertRaises(image_tool.ImageToolError, msg="path escapes"):
                image_tool.resolve_notebook_path("/etc/passwd", root / "Output", must_exist=False)


if __name__ == "__main__":
    unittest.main()
