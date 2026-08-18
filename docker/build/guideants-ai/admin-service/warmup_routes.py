"""HTTP boundary for API-owned local AI lifecycle plans."""

from __future__ import annotations

from typing import Any

from fastapi import APIRouter, Body, HTTPException

from warmup_plan import WarmupPlanValidationError, parse_warmup_plan
from warmup_orchestrator import request_warmup_apply
from warmup_state import get_warmup_status_response

ROUTER = APIRouter(tags=["warmup"])


@ROUTER.post("/warmup/apply")
async def post_warmup_apply(
    body: dict[str, Any] = Body(..., media_type="application/json"),
) -> dict[str, Any]:
    try:
        plan = parse_warmup_plan(body)
    except WarmupPlanValidationError as exc:
        raise HTTPException(status_code=400, detail=str(exc)) from exc
    return request_warmup_apply(plan)


@ROUTER.get("/warmup/status")
async def get_warmup_status() -> dict[str, Any]:
    return get_warmup_status_response()
