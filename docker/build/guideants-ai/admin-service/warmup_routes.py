"""FastAPI routes for warmup desired state orchestration."""

from __future__ import annotations

from typing import Any

from fastapi import APIRouter, Body, Header, HTTPException, Query

from warmup_desired_ini import (
    WarmupDesiredValidationError,
    put_warmup_desired_text,
)
from warmup_orchestrator import request_warmup_apply
from warmup_state import get_warmup_status_response, sync_state_after_desired_write

ROUTER = APIRouter(tags=["warmup"])


@ROUTER.put("/warmup/desired")
async def put_warmup_desired(
    body: str = Body(..., media_type="text/plain"),
    expected_revision: int | None = Query(default=None),
    if_match_revision: int | None = Header(default=None, alias="If-Match-Revision"),
) -> dict[str, Any]:
    revision_guard = if_match_revision if if_match_revision is not None else expected_revision
    try:
        document, result = put_warmup_desired_text(body, expected_revision=revision_guard)
    except WarmupDesiredValidationError as exc:
        raise HTTPException(status_code=400, detail=str(exc)) from exc
    except ValueError as exc:
        if "revision conflict" in str(exc).lower():
            raise HTTPException(status_code=409, detail=str(exc)) from exc
        raise HTTPException(status_code=400, detail=str(exc)) from exc

    state = sync_state_after_desired_write(
        document,
        desired_sha256=result.sha256,
        changed=result.changed,
    )
    return {
        "ok": True,
        "revision": result.revision,
        "sha256": result.sha256,
        "changed": result.changed,
        "state": state,
    }


@ROUTER.post("/warmup/apply")
async def post_warmup_apply() -> dict[str, Any]:
    return request_warmup_apply()


@ROUTER.get("/warmup/status")
async def get_warmup_status() -> dict[str, Any]:
    return get_warmup_status_response()
