#!/usr/bin/env python3
"""Unit tests for qwen-image skill_gateway_client and notebook path scoping."""
from __future__ import annotations

import argparse
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
import preflight  # noqa: E402


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
        os.environ["QWEN_IMAGE_SKILL_BASE_URL"] = "http://127.0.0.1:8189/qwen-image-skill"
        os.environ.pop("QWEN_IMAGE_SKILL_TOKEN", None)
        with self.assertRaises(SystemExit):
            client.gateway_headers()

    @mock.patch("urllib.request.urlopen")
    def test_fetch_capabilities(self, urlopen: mock.Mock) -> None:
        os.environ["QWEN_IMAGE_SKILL_BASE_URL"] = "http://127.0.0.1:8189/qwen-image-skill"
        os.environ["QWEN_IMAGE_SKILL_TOKEN"] = "secret"
        response = mock.Mock()
        response.read.return_value = json.dumps(
            {"image_generate_ready": True, "precision": "bfloat16"}
        ).encode("utf-8")
        response.__enter__ = mock.Mock(return_value=response)
        response.__exit__ = mock.Mock(return_value=False)
        urlopen.return_value = response

        caps = client.fetch_capabilities()
        self.assertTrue(caps["image_generate_ready"])
        request = urlopen.call_args[0][0]
        self.assertEqual(request.get_method(), "GET")
        self.assertTrue(request.full_url.endswith("/v1/capabilities"))
        header_map = {k: v for k, v in request.header_items()}
        self.assertEqual(header_map.get("X-qwen-image-skill-token"), "secret")

    @mock.patch("urllib.request.urlopen")
    def test_probe_gateway_http_error(self, urlopen: mock.Mock) -> None:
        os.environ["QWEN_IMAGE_SKILL_BASE_URL"] = "http://127.0.0.1:8189/qwen-image-skill"
        os.environ["QWEN_IMAGE_SKILL_TOKEN"] = "secret"
        urlopen.side_effect = HTTPError(
            "http://127.0.0.1/health",
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

    def test_output_prefix_stripped_when_cwd_is_output(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / ".guideants").mkdir()
            (root / ".guideants" / "notebook.json").write_text("{}", encoding="utf-8")
            output = root / "Output"
            output.mkdir()
            resolved = image_tool.resolve_notebook_path(
                "Output/skynet.png",
                output,
                must_exist=False,
            )
            self.assertEqual(resolved, output / "skynet.png")

    def test_generate_quality_draft_profile(self) -> None:
        args = argparse.Namespace(quality="draft", canvas="square", seed=42)
        params = image_tool._default_generate_params(args)
        self.assertEqual(params["steps"], 4)
        self.assertEqual(params["cfg"], 1.0)
        self.assertEqual(params["lora_strength"], 1.0)
        self.assertEqual(params["width"], 1328)
        self.assertEqual(params["height"], 1328)

    def test_generate_quality_high_landscape(self) -> None:
        args = argparse.Namespace(
            quality="high",
            canvas="landscape",
            seed=42,
            workflow=image_tool.GENERATE_WORKFLOW,
        )
        params = image_tool._default_generate_params(args)
        self.assertEqual(params["steps"], 20)
        self.assertEqual(params["cfg"], 2.5)
        self.assertNotIn("lora_strength", params)
        self.assertEqual(params["width"], 1664)
        self.assertEqual(params["height"], 928)
        self.assertEqual(
            image_tool._generate_workflow(args),
            image_tool.GENERATE_WORKFLOW_HIGH,
        )

    def test_preflight_generate_high_uses_generate_20_flag(self) -> None:
        caps = {
            "image_generate_ready": False,
            "image_generate_20_ready": True,
            "workflow_versions": ["qwen-image-v1", "qwen-image-generate-20-v1"],
        }
        with mock.patch("preflight.fetch_capabilities", return_value=caps), mock.patch(
            "preflight.probe_gateway",
            return_value={"open": True, "status": 200},
        ), mock.patch("preflight.using_skill_gateway", return_value=True):
            report = preflight.run_preflight("generate", quality="high")
        self.assertTrue(report["open"])
        self.assertEqual(report["evidence"]["workflow"], "qwen-image-generate-20-v1")
        self.assertEqual(report["evidence"]["quality"], "high")

    def test_preflight_generate_draft_still_requires_lightning_flag(self) -> None:
        caps = {
            "image_generate_ready": False,
            "image_generate_20_ready": True,
        }
        with mock.patch("preflight.fetch_capabilities", return_value=caps), mock.patch(
            "preflight.probe_gateway",
            return_value={"open": True, "status": 200},
        ), mock.patch("preflight.using_skill_gateway", return_value=True):
            report = preflight.run_preflight("generate", quality="draft")
        self.assertFalse(report["open"])
        self.assertIn("image_generate_ready is false", report["blockers"][0])

    def test_poll_seconds_rejects_absurd_interval(self) -> None:
        with self.assertRaises(image_tool.ImageToolError, msg="poll-seconds"):
            image_tool._poll_job(
                "0" * 32,
                timeout_seconds=60,
                poll_seconds=900,
            )


if __name__ == "__main__":
    unittest.main()
