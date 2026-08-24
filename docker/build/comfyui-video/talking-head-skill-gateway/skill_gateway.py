#!/usr/bin/env python3
"""LAN-facing raw ComfyUI-video adapter access (not GuideAntsApi / ServiceModes).

External clients (PC sandboxes, agents) reach the video adapter HTTP surface via
transparent reverse proxy to the loopback adapter:

  /v1/capabilities
  /v1/talking-head/jobs
  /v1/talking-head/jobs/{id}
  /v1/talking-head/jobs/{id}/cancel
  /v1/talking-head/jobs/{id}/result

Plus gateway-owned helpers:

  POST /files   stage an upload on Max (parity with qwen-image / audiocpp gateways)
  GET  /health  gateway + upstream probe

Auth: X-Talking-Head-Skill-Token == GA_TALKING_HEAD_SKILL_TOKEN.
"""
from __future__ import annotations

import json
import logging
import os
import urllib.error
import urllib.request
import uuid
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

import uvicorn
from fastapi import Depends, FastAPI, Header, HTTPException, Request, UploadFile
from fastapi.responses import JSONResponse, Response

APP = FastAPI(title="GuideAnts talking-head raw gateway", version="1.0.0")

CHUNK_SIZE = 1024 * 1024
PROXY_METHODS = ["GET", "POST", "PUT", "PATCH", "DELETE", "OPTIONS", "HEAD"]


def utc_now_iso() -> str:
    return datetime.now(timezone.utc).isoformat()


def env_int(name: str, default: int) -> int:
    raw = (os.getenv(name) or "").strip()
    if not raw:
        return default
    try:
        value = int(raw)
        return value if value > 0 else default
    except ValueError:
        return default


def require_token() -> str:
    token = (os.getenv("GA_TALKING_HEAD_SKILL_TOKEN") or "").strip()
    if not token:
        raise HTTPException(
            status_code=503,
            detail="GA_TALKING_HEAD_SKILL_TOKEN is not configured on this host",
        )
    return token


def auth_dependency(
    x_talking_head_skill_token: str | None = Header(
        default=None, alias="X-Talking-Head-Skill-Token"
    ),
) -> None:
    expected = require_token()
    if not x_talking_head_skill_token or x_talking_head_skill_token != expected:
        raise HTTPException(
            status_code=401, detail="invalid or missing X-Talking-Head-Skill-Token"
        )


def staging_root() -> Path:
    root = Path(
        os.getenv(
            "GA_TALKING_HEAD_SKILL_STAGING_DIR",
            "/var/lib/guideants/talking-head-skill/staging",
        )
    )
    root.mkdir(parents=True, exist_ok=True)
    return root


def adapter_url() -> str:
    host = os.getenv("GA_TALKING_HEAD_ADAPTER_HOST", "127.0.0.1").strip() or "127.0.0.1"
    port = env_int("GA_TALKING_HEAD_ADAPTER_PORT", 8190)
    return f"http://{host}:{port}"


def probe_url(url: str, timeout: float = 5.0) -> dict[str, Any]:
    try:
        with urllib.request.urlopen(url, timeout=timeout) as response:
            body = response.read().decode("utf-8", errors="replace")
            try:
                parsed: Any = json.loads(body)
            except json.JSONDecodeError:
                parsed = body[:500]
            return {"reachable": True, "status": response.status, "body": parsed}
    except urllib.error.HTTPError as exc:
        return {
            "reachable": True,
            "status": exc.code,
            "body": exc.read().decode("utf-8", errors="replace")[:500],
        }
    except Exception as exc:
        return {"reachable": False, "error": f"{type(exc).__name__}: {exc}"}


async def proxy_adapter(path: str, request: Request) -> Response:
    """Transparent reverse proxy: method, query, headers, body → adapter."""
    query = request.url.query
    target = f"{adapter_url().rstrip('/')}/{path.lstrip('/')}"
    if query:
        target = f"{target}?{query}"

    body = await request.body()
    hop_by_hop = {
        "connection",
        "keep-alive",
        "proxy-authenticate",
        "proxy-authorization",
        "te",
        "trailers",
        "transfer-encoding",
        "upgrade",
        "host",
        "content-length",
        "x-talking-head-skill-token",
    }
    headers: dict[str, str] = {}
    for key, value in request.headers.items():
        if key.lower() in hop_by_hop:
            continue
        headers[key] = value

    req = urllib.request.Request(
        target, data=body if body else None, method=request.method, headers=headers
    )
    timeout = float(
        os.getenv("GA_TALKING_HEAD_SKILL_PROXY_TIMEOUT_SECONDS", "3600") or "3600"
    )
    try:
        with urllib.request.urlopen(req, timeout=timeout) as upstream:
            content_type = upstream.headers.get("Content-Type", "application/octet-stream")
            payload = upstream.read()
            response_headers = {}
            for name in ("Content-Disposition", "X-Request-Id"):
                if upstream.headers.get(name):
                    response_headers[name] = upstream.headers.get(name)
            return Response(
                content=payload,
                status_code=upstream.status,
                media_type=content_type,
                headers=response_headers,
            )
    except urllib.error.HTTPError as exc:
        detail = exc.read()
        content_type = (
            exc.headers.get("Content-Type", "application/octet-stream")
            if exc.headers
            else "application/octet-stream"
        )
        return Response(content=detail, status_code=exc.code, media_type=content_type)
    except Exception as exc:
        raise HTTPException(status_code=502, detail=f"upstream error: {exc}") from exc


@APP.get("/health")
def health(_: None = Depends(auth_dependency)) -> dict[str, Any]:
    upstream = adapter_url()
    caps = probe_url(f"{upstream}/v1/capabilities")
    return {
        "status": "ok",
        "service": "talking-head-raw-gateway",
        "api_version": "1",
        "ts": utc_now_iso(),
        "adapter": {"upstream": upstream, **probe_url(f"{upstream}/health")},
        "capabilities": caps,
        "stagingRoot": str(staging_root()),
        "note": "All /v1/* paths are transparent proxies to the ComfyUI-video adapter.",
    }


@APP.post("/files")
async def stage_file(
    file: UploadFile,
    _: None = Depends(auth_dependency),
) -> dict[str, Any]:
    """Stage an uploaded file on Max; return an absolute path for adapter JSON fields."""
    staging = staging_root() / str(uuid.uuid4())
    staging.mkdir(parents=True, exist_ok=True)
    name = Path(file.filename or "upload.bin").name
    dest = staging / name
    with dest.open("wb") as handle:
        while True:
            chunk = await file.read(CHUNK_SIZE)
            if not chunk:
                break
            handle.write(chunk)
    return {"path": str(dest.resolve()), "bytes": dest.stat().st_size, "filename": name}


@APP.api_route("/v1", methods=PROXY_METHODS)
@APP.api_route("/v1/{path:path}", methods=PROXY_METHODS)
async def proxy_v1(
    request: Request, path: str = "", _: None = Depends(auth_dependency)
) -> Response:
    return await proxy_adapter(f"v1/{path}" if path else "v1", request)


@APP.exception_handler(HTTPException)
async def http_exception_handler(_: Request, exc: HTTPException) -> JSONResponse:
    return JSONResponse(status_code=exc.status_code, content={"detail": exc.detail})


def main() -> None:
    host = os.getenv("GA_TALKING_HEAD_SKILL_HOST", "127.0.0.1").strip() or "127.0.0.1"
    port = env_int("GA_TALKING_HEAD_SKILL_PORT", 8098)
    log_level = (os.getenv("GA_TALKING_HEAD_SKILL_LOG_LEVEL") or "warning").strip() or "warning"
    staging_root()
    logging.getLogger("uvicorn.access").setLevel(logging.WARNING)
    uvicorn.run(APP, host=host, port=port, log_level=log_level, access_log=False)


if __name__ == "__main__":
    main()
