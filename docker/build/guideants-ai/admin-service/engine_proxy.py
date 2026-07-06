"""Forward admin requests to ASR/TTS/emb engine processes on localhost."""

from __future__ import annotations

import asyncio
import os
import urllib.error
import urllib.request
from typing import Iterable

from fastapi import Request, Response

_HOP_BY_HOP = frozenset(
    {
        "connection",
        "keep-alive",
        "proxy-authenticate",
        "proxy-authorization",
        "te",
        "trailer",
        "transfer-encoding",
        "upgrade",
        "host",
        "content-length",
    }
)


def parse_positive_int(value: str | None, default: int) -> int:
    if value is None:
        return default
    try:
        parsed = int(value)
    except ValueError:
        return default
    return parsed if parsed > 0 else default


def _engine_base_url(host_env: str, port_env: str, default_port: int) -> str:
    host = (os.getenv(host_env) or "127.0.0.1").strip() or "127.0.0.1"
    port = parse_positive_int(os.getenv(port_env), default_port)
    return f"http://{host}:{port}"


ASR_ENGINE_BASE_URL = _engine_base_url("GA_ASR_HOST", "GA_ASR_PORT", 8082)
TTS_ENGINE_BASE_URL = _engine_base_url("GA_TTS_HOST", "GA_TTS_PORT", 8084)
EMB_ENGINE_BASE_URL = _engine_base_url("GA_EMB_HOST", "GA_EMB_PORT", 8085)

PROXY_TIMEOUT_SECONDS = parse_positive_int(os.getenv("GA_ADMIN_PROXY_TIMEOUT_SECONDS"), 1800)


def _filter_request_headers(headers: Iterable[tuple[str, str]]) -> dict[str, str]:
    filtered: dict[str, str] = {}
    for name, value in headers:
        if name.lower() in _HOP_BY_HOP:
            continue
        filtered[name] = value
    return filtered


def _filter_response_headers(headers: Iterable[tuple[str, str]]) -> dict[str, str]:
    filtered: dict[str, str] = {}
    for name, value in headers:
        lowered = name.lower()
        if lowered in _HOP_BY_HOP:
            continue
        filtered[name] = value
    return filtered


def _proxy_sync(
    method: str,
    url: str,
    headers: dict[str, str],
    body: bytes,
    timeout_seconds: int,
) -> tuple[int, dict[str, str], bytes]:
    request = urllib.request.Request(url=url, data=body or None, method=method, headers=headers)
    try:
        with urllib.request.urlopen(request, timeout=timeout_seconds) as response:
            return (
                int(response.status),
                _filter_response_headers(response.headers.items()),
                response.read(),
            )
    except urllib.error.HTTPError as exc:
        return (
            int(exc.code),
            _filter_response_headers(exc.headers.items()),
            exc.read(),
        )


async def proxy_to_engine(request: Request, engine_base_url: str, admin_path: str) -> Response:
    query = request.url.query
    url = f"{engine_base_url.rstrip('/')}/admin/{admin_path.lstrip('/')}"
    if query:
        url = f"{url}?{query}"

    body = await request.body()
    headers = _filter_request_headers(request.headers.items())
    if body and "Content-Type" not in headers and "content-type" not in headers:
        headers["Content-Type"] = request.headers.get("content-type", "application/json")

    status_code, response_headers, content = await asyncio.to_thread(
        _proxy_sync,
        request.method,
        url,
        headers,
        body,
        PROXY_TIMEOUT_SECONDS,
    )
    return Response(content=content, status_code=status_code, headers=response_headers)
