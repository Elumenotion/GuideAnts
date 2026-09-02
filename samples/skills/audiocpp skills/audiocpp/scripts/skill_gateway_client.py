#!/usr/bin/env python3
"""Shared helpers for remote raw audiocpp_server access via GPU host gateway.

When AUDIOCPP_SKILL_BASE_URL is set, skills talk to the GPU host LAN raw gateway
instead of loopback engines. The gateway is a transparent reverse proxy:

  {BASE}/asr/...      -> audiocpp_server ASR
  {BASE}/tts/...      -> audiocpp_server TTS
  {BASE}/private/...  -> skill-spawned private engine
  {BASE}/files        -> stage upload; returns host-local path
  {BASE}/admin/...    -> fetch models / private start|stop

Auth: X-Audiocpp-Skill-Token. Stdlib-only.
"""
from __future__ import annotations

import json
import os
import sys
import urllib.error
import urllib.request
from pathlib import Path
from typing import Any


def normalize_sandbox_relative_path(value: str | os.PathLike[str]) -> str:
    """Strip redundant Output/ prefix when sandbox CWD is already Output/."""
    text = os.fspath(value).replace("\\", "/")
    cwd = Path.cwd().resolve()
    for candidate in (cwd, *cwd.parents):
        if (candidate / ".guideants" / "notebook.json").is_file():
            output_dir = candidate / "Output"
            try:
                cwd.relative_to(output_dir.resolve())
            except ValueError:
                return os.fspath(value)
            normalized = text.lstrip("./").lstrip("/")
            if normalized.lower().startswith("output/"):
                stripped = normalized[7:]
                print(
                    "warning: path starts with Output/ but sandbox CWD is already Output/; "
                    f"using {stripped!r} instead",
                    file=sys.stderr,
                )
                return stripped
            return os.fspath(value)
    return os.fspath(value)


def skill_base_url() -> str | None:
    raw = (os.environ.get("AUDIOCPP_SKILL_BASE_URL") or "").strip().rstrip("/")
    return raw or None


def skill_token() -> str | None:
    raw = (os.environ.get("AUDIOCPP_SKILL_TOKEN") or "").strip()
    return raw or None


def using_skill_gateway() -> bool:
    return skill_base_url() is not None


def gateway_headers(extra: dict[str, str] | None = None) -> dict[str, str]:
    headers = dict(extra or {})
    token = skill_token()
    if not token:
        sys.stderr.write(
            "AUDIOCPP_SKILL_BASE_URL is set but AUDIOCPP_SKILL_TOKEN is missing.\n"
        )
        sys.exit(1)
    headers["X-Audiocpp-Skill-Token"] = token
    return headers


def gateway_engine_prefix(engine_url: str) -> str:
    """Map a local-style engine URL / label to the gateway proxy prefix."""
    url = (engine_url or "").rstrip("/").lower()
    if url.endswith(":18099") or "private" in url or url.endswith("/private"):
        return "/private"
    if url.endswith(":18082") or "asr" in url or url.endswith("/asr"):
        return "/asr"
    return "/tts"


def gateway_request(
    path: str,
    *,
    method: str = "GET",
    payload: dict[str, Any] | None = None,
    timeout: float = 300.0,
) -> bytes:
    base = skill_base_url()
    if not base:
        raise RuntimeError("AUDIOCPP_SKILL_BASE_URL is not set")
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
    """Minimal multipart/form-data client (stdlib). Used for /files staging."""
    base = skill_base_url()
    if not base:
        raise RuntimeError("AUDIOCPP_SKILL_BASE_URL is not set")
    boundary = "----guideantsAudiocppSkillBoundary7MA4YWxkTrZu0gW"
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


def stage_file(local_path: str, *, timeout: float = 600.0) -> str:
    """Upload a local file to the GPU host staging; return absolute GPU host-side path for engine JSON."""
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
        return {"configured": True, "open": False, "error": "AUDIOCPP_SKILL_TOKEN missing"}
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
