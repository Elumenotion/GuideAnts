"""
GuideAnts consolidated control-plane service (Phase 4).

Absorbs llama-admin routes, SD admin + inference facade (including sd-server
child management), and proxies ASR/TTS/emb admin traffic to the data-plane
engines on localhost.
"""

from __future__ import annotations

import os
import sys
from typing import Any

import uvicorn
from fastapi import FastAPI, Request
from fastapi.routing import APIRoute

_ADMIN_DIR = os.path.dirname(os.path.abspath(__file__))
_APP_ROOT = os.path.dirname(_ADMIN_DIR)
if _ADMIN_DIR not in sys.path:
    sys.path.insert(0, _ADMIN_DIR)
for _service_dir in ("llama-admin-service", "sd-service"):
    _candidate = os.path.join(_APP_ROOT, _service_dir)
    if _candidate not in sys.path:
        sys.path.insert(0, _candidate)

import llama_admin_service  # noqa: E402
import sd_service  # noqa: E402

from engine_proxy import (  # noqa: E402
    ASR_ENGINE_BASE_URL,
    EMB_ENGINE_BASE_URL,
    TTS_ENGINE_BASE_URL,
    proxy_to_engine,
)


def env_flag(name: str, default: bool = False) -> bool:
    raw = os.getenv(name)
    if raw is None:
        return default
    return raw.strip().lower() in {"1", "true", "yes", "on"}


def parse_positive_int(value: str | None, default: int) -> int:
    if value is None:
        return default
    try:
        parsed = int(value)
    except ValueError:
        return default
    return parsed if parsed > 0 else default


def _include_flat_routes(parent: FastAPI, child: FastAPI, prefix: str = "") -> None:
    for route in child.routes:
        if not isinstance(route, APIRoute):
            continue
        parent.add_api_route(
            f"{prefix}{route.path}",
            route.endpoint,
            methods=sorted(route.methods),
            response_model=route.response_model,
            status_code=route.status_code,
            tags=route.tags,
            dependencies=route.dependencies,
            summary=route.summary,
            description=route.description,
            response_class=route.response_class,
            name=route.name,
            include_in_schema=route.include_in_schema,
        )


APP = FastAPI(title="GuideAnts Admin Service", version="1.0.0")

# llama-admin public paths are exposed at the ga-admin root (nginx strips /llama-admin/).
_include_flat_routes(APP, llama_admin_service.APP)

# SD admin + inference facade + sd-server lifecycle (nginx prefixes /sd/).
APP.mount("/sd", sd_service.APP)


@APP.on_event("startup")
async def on_startup() -> None:
    await sd_service.on_startup()


@APP.on_event("shutdown")
async def on_shutdown() -> None:
    await sd_service.on_shutdown()


@APP.api_route("/asr/admin", methods=["GET", "POST", "PUT", "PATCH", "DELETE", "OPTIONS", "HEAD"])
@APP.api_route(
    "/asr/admin/",
    methods=["GET", "POST", "PUT", "PATCH", "DELETE", "OPTIONS", "HEAD"],
)
@APP.api_route(
    "/asr/admin/{admin_path:path}",
    methods=["GET", "POST", "PUT", "PATCH", "DELETE", "OPTIONS", "HEAD"],
)
async def proxy_asr_admin(request: Request, admin_path: str = "") -> Any:
    return await proxy_to_engine(request, ASR_ENGINE_BASE_URL, admin_path)


@APP.api_route("/tts/admin", methods=["GET", "POST", "PUT", "PATCH", "DELETE", "OPTIONS", "HEAD"])
@APP.api_route(
    "/tts/admin/",
    methods=["GET", "POST", "PUT", "PATCH", "DELETE", "OPTIONS", "HEAD"],
)
@APP.api_route(
    "/tts/admin/{admin_path:path}",
    methods=["GET", "POST", "PUT", "PATCH", "DELETE", "OPTIONS", "HEAD"],
)
async def proxy_tts_admin(request: Request, admin_path: str = "") -> Any:
    return await proxy_to_engine(request, TTS_ENGINE_BASE_URL, admin_path)


@APP.api_route("/emb/admin", methods=["GET", "POST", "PUT", "PATCH", "DELETE", "OPTIONS", "HEAD"])
@APP.api_route(
    "/emb/admin/",
    methods=["GET", "POST", "PUT", "PATCH", "DELETE", "OPTIONS", "HEAD"],
)
@APP.api_route(
    "/emb/admin/{admin_path:path}",
    methods=["GET", "POST", "PUT", "PATCH", "DELETE", "OPTIONS", "HEAD"],
)
async def proxy_emb_admin(request: Request, admin_path: str = "") -> Any:
    return await proxy_to_engine(request, EMB_ENGINE_BASE_URL, admin_path)


if __name__ == "__main__":
    host = (
        (os.getenv("GA_ADMIN_HOST") or os.getenv("GA_LLAMA_ADMIN_HOST") or "127.0.0.1").strip()
        or "127.0.0.1"
    )
    port = parse_positive_int(
        os.getenv("GA_ADMIN_PORT") or os.getenv("GA_LLAMA_ADMIN_PORT"),
        8086,
    )
    log_level = (
        (os.getenv("GA_ADMIN_LOG_LEVEL") or os.getenv("GA_LLAMA_ADMIN_LOG_LEVEL") or "info")
        .strip()
        .lower()
    )
    access_log = env_flag("GA_ADMIN_UVICORN_ACCESS_LOG", default=False) or env_flag(
        "GA_LLAMA_ADMIN_UVICORN_ACCESS_LOG",
        default=False,
    )
    uvicorn.run(APP, host=host, port=port, log_level=log_level, access_log=access_log)
