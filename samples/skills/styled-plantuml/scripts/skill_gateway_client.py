#!/usr/bin/env python3
"""Shared helpers for remote ComfyUI-video adapter access via GPU host gateway.

When QWEN_IMAGE_SKILL_BASE_URL is set, skills talk to the GPU host LAN raw gateway
instead of loopback adapter ports. The gateway is a transparent reverse proxy:

  {BASE}/v1/capabilities
  {BASE}/v1/image/jobs
  {BASE}/v1/image/generate/jobs
  {BASE}/files

Auth: X-Qwen-Image-Skill-Token. Stdlib-only.
"""
from __future__ import annotations

import json
import os
import sys
import urllib.error
import urllib.request
from typing import Any


def skill_base_url() -> str | None:
    raw = (os.environ.get("QWEN_IMAGE_SKILL_BASE_URL") or "").strip().rstrip("/")
    return raw or None


def skill_token() -> str | None:
    raw = (os.environ.get("QWEN_IMAGE_SKILL_TOKEN") or "").strip()
    return raw or None


def using_skill_gateway() -> bool:
    return skill_base_url() is not None


def gateway_env_missing_hint() -> str:
    return (
        "Ask the user to set QWEN_IMAGE_SKILL_BASE_URL and QWEN_IMAGE_SKILL_TOKEN "
        "in the guide's Environment variables. Do not scan the LAN, ping hosts, or "
        "guess the GPU host's IP — the operator supplies the URL. Do not call 127.0.0.1:8189 "
        "or :8190 from a PC sandbox."
    )


def gateway_env_missing_blocker() -> str:
    return (
        "skillGateway: QWEN_IMAGE_SKILL_BASE_URL is not set — "
        + gateway_env_missing_hint()
    )


def require_gateway() -> None:
    if not using_skill_gateway():
        sys.stderr.write(
            f"QWEN_IMAGE_SKILL_BASE_URL is not set. {gateway_env_missing_hint()}\n"
        )
        sys.exit(1)


def gateway_headers(extra: dict[str, str] | None = None) -> dict[str, str]:
    headers = dict(extra or {})
    token = skill_token()
    if not token:
        sys.stderr.write(
            "QWEN_IMAGE_SKILL_BASE_URL is set but QWEN_IMAGE_SKILL_TOKEN is missing. "
            "Ask the user to set QWEN_IMAGE_SKILL_TOKEN in the guide's Environment "
            "variables (same value as the GPU host GA_QWEN_IMAGE_SKILL_TOKEN).\n"
        )
        sys.exit(1)
    headers["X-Qwen-Image-Skill-Token"] = token
    return headers


def gateway_request(
    path: str,
    *,
    method: str = "GET",
    payload: dict[str, Any] | None = None,
    timeout: float = 300.0,
) -> bytes:
    base = skill_base_url()
    if not base:
        raise RuntimeError("QWEN_IMAGE_SKILL_BASE_URL is not set")
    url = f"{base}{path if path.startswith('/') else '/' + path}"
    data = json.dumps(payload).encode("utf-8") if payload is not None else None
    if payload is not None and method.upper() == "GET":
        method = "POST"
    headers = gateway_headers({"Content-Type": "application/json"} if payload is not None else None)
    request = urllib.request.Request(url, data=data, method=method, headers=headers)
    with urllib.request.urlopen(request, timeout=timeout) as response:
        return response.read()


def gateway_request_multipart(
    path: str,
    fields: dict[str, str],
    files: dict[str, tuple[str, bytes, str]],
    *,
    method: str = "POST",
    timeout: float = 3600.0,
) -> bytes:
    """Minimal multipart/form-data client (stdlib)."""
    base = skill_base_url()
    if not base:
        raise RuntimeError("QWEN_IMAGE_SKILL_BASE_URL is not set")
    boundary = "----guideantsQwenImageSkillBoundary7MA4YWxkTrZu0gW"
    body = bytearray()
    for name, value in fields.items():
        body.extend(f"--{boundary}\r\n".encode("utf-8"))
        body.extend(f'Content-Disposition: form-data; name="{name}"\r\n\r\n'.encode("utf-8"))
        body.extend(value.encode("utf-8"))
        body.extend(b"\r\n")
    for name, (filename, content, content_type) in files.items():
        body.extend(f"--{boundary}\r\n".encode("utf-8"))
        body.extend(
            (
                f'Content-Disposition: form-data; name="{name}"; filename="{filename}"\r\n'
                f"Content-Type: {content_type}\r\n\r\n"
            ).encode("utf-8")
        )
        body.extend(content)
        body.extend(b"\r\n")
    body.extend(f"--{boundary}--\r\n".encode("utf-8"))
    url = f"{base}{path if path.startswith('/') else '/' + path}"
    headers = gateway_headers({"Content-Type": f"multipart/form-data; boundary={boundary}"})
    request = urllib.request.Request(url, data=bytes(body), method=method, headers=headers)
    with urllib.request.urlopen(request, timeout=timeout) as response:
        return response.read()


def gateway_download(path: str, *, timeout: float = 3600.0) -> bytes:
    base = skill_base_url()
    if not base:
        raise RuntimeError("QWEN_IMAGE_SKILL_BASE_URL is not set")
    url = f"{base}{path if path.startswith('/') else '/' + path}"
    request = urllib.request.Request(url, method="GET", headers=gateway_headers())
    with urllib.request.urlopen(request, timeout=timeout) as response:
        return response.read()


def stage_file(local_path: str, *, timeout: float = 600.0) -> str:
    """Upload a local file to the GPU host staging; return absolute GPU host-side path."""
    from pathlib import Path

    path = Path(local_path)
    if not path.is_file():
        raise FileNotFoundError(local_path)
    with path.open("rb") as handle:
        content = handle.read()
    raw = gateway_request_multipart(
        "/files",
        {},
        {"file": (path.name, content, "application/octet-stream")},
        timeout=timeout,
    )
    parsed = json.loads(raw.decode("utf-8"))
    staged = parsed.get("path")
    if not staged:
        raise RuntimeError(f"/files response missing path: {parsed}")
    return str(staged)


def fail_http(exc: urllib.error.HTTPError, context: str) -> None:
    detail = exc.read().decode("utf-8", errors="replace")
    sys.stderr.write(f"{context} failed with HTTP {exc.code}: {detail}\n")
    sys.exit(1)


def probe_gateway(timeout: float = 5.0) -> dict[str, Any]:
    base = skill_base_url()
    if not base:
        return {"configured": False, "open": False}
    if not skill_token():
        return {"configured": True, "open": False, "error": "QWEN_IMAGE_SKILL_TOKEN missing"}
    try:
        body = gateway_request("/health", timeout=timeout)
        parsed = json.loads(body.decode("utf-8", errors="replace"))
        return {"configured": True, "open": True, "status": 200, "body": parsed}
    except urllib.error.HTTPError as exc:
        return {
            "configured": True,
            "open": False,
            "status": exc.code,
            "body": exc.read().decode("utf-8", errors="replace")[:500],
        }
    except Exception as exc:
        return {"configured": True, "open": False, "error": f"{type(exc).__name__}: {exc}"}


def fetch_capabilities(timeout: float = 30.0) -> dict[str, Any]:
    body = gateway_request("/v1/capabilities", timeout=timeout)
    parsed = json.loads(body.decode("utf-8"))
    if not isinstance(parsed, dict):
        raise RuntimeError("capabilities response was not a JSON object")
    return parsed
