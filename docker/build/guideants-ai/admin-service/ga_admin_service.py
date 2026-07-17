"""
GuideAnts consolidated control-plane service (Phase 4).

Hosts llama-admin routes and warmup orchestration only. Every inference engine
(ASR, SD, TTS, embeddings, llama-cpp) runs in its own process; nginx routes
traffic directly to each engine port so no inference workload can block this
control plane or any other engine.
"""

from __future__ import annotations

import os
import sys

import uvicorn
from fastapi import FastAPI
from fastapi.routing import APIRoute

_ADMIN_DIR = os.path.dirname(os.path.abspath(__file__))
_APP_ROOT = os.path.dirname(_ADMIN_DIR)
if _ADMIN_DIR not in sys.path:
    sys.path.insert(0, _ADMIN_DIR)
_llama_admin_dir = os.path.join(_APP_ROOT, "llama-admin-service")
if _llama_admin_dir not in sys.path:
    sys.path.insert(0, _llama_admin_dir)

import llama_admin_service  # noqa: E402

from warmup_orchestrator import apply_warmup_on_startup, configure_warmup_orchestrator  # noqa: E402
from warmup_routes import ROUTER as WARMUP_ROUTER  # noqa: E402


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
_include_flat_routes(APP, WARMUP_ROUTER)


@APP.on_event("startup")
async def on_startup() -> None:
    configure_warmup_orchestrator(log_event=lambda event, **fields: print({"event": event, **fields}, flush=True))
    apply_warmup_on_startup()


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
