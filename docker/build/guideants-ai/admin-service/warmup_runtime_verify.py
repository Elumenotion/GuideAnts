"""
API-owned verification that local engine HTTP matches a submitted lifecycle plan.

GuideAntsApi must NOT trust ga-admin revision/noop alone — engines can outlive
executor status files. This verifier probes engine admin HTTP on each stack host.
"""

from __future__ import annotations

import json
import os
import urllib.error
import urllib.request
from dataclasses import dataclass
from typing import Any

from warmup_plan import (
    WARMUP_SERVICE_SECTIONS,
    WarmupPlanDocument,
    section_execution_ref,
    section_should_load,
)


@dataclass(frozen=True)
class LocalAiRuntimeAlignmentMismatch:
    service_id: str
    detail: str


def _get_json(url: str, timeout: float = 5.0) -> tuple[int, Any]:
    request = urllib.request.Request(url=url, method="GET", headers={"Accept": "application/json"})
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            text = response.read().decode("utf-8", errors="replace")
            if not text.strip():
                return int(response.status), None
            return int(response.status), json.loads(text)
    except urllib.error.HTTPError as exc:
        body = exc.read().decode("utf-8", errors="replace")
        if not body.strip():
            return int(exc.code), None
        try:
            return int(exc.code), json.loads(body)
        except json.JSONDecodeError:
            return int(exc.code), body


def _engine_base(service_id: str) -> str | None:
    from warmup_engine_client import SERVICE_ENGINE_BASE_URLS

    return SERVICE_ENGINE_BASE_URLS.get(service_id)


def _probe_aux_loaded(service_id: str) -> tuple[bool, str | None]:
    base = _engine_base(service_id)
    if not base:
        return False, None
    if service_id == "ImageGeneration":
        status, payload = _get_json(f"{base.rstrip('/')}/health")
        if status != 200 or not isinstance(payload, dict):
            return False, None
        engine = payload.get("engine") or {}
        if engine.get("processAlive") is not True:
            return False, None
        bundle = str(engine.get("loadedBundleId") or payload.get("loadedBundleId") or "").strip()
        return True, bundle or None
    status, payload = _get_json(f"{base.rstrip('/')}/ready")
    if status != 200 or not isinstance(payload, dict) or payload.get("loaded") is not True:
        return False, None
    for key in ("modelRef", "model_ref", "catalogEntryId", "catalog_entry_id", "bundleId", "bundle_id"):
        value = str(payload.get(key) or "").strip()
        if value:
            return True, value
    return True, None


def find_runtime_mismatches(plan: WarmupPlanDocument) -> list[LocalAiRuntimeAlignmentMismatch]:
    """Return mechanical mismatches between plan and engine HTTP (empty if aligned)."""
    mismatches: list[LocalAiRuntimeAlignmentMismatch] = []
    for section_name in WARMUP_SERVICE_SECTIONS:
        section = plan.services.get(section_name)
        should_load = section_should_load(section_name, section)
        plan_ref = section_execution_ref(section_name, section) if section is not None else None

        loaded, loaded_ref = _probe_aux_loaded(section_name)
        if should_load:
            if not plan_ref:
                mismatches.append(
                    LocalAiRuntimeAlignmentMismatch(section_name, "plan enabled but missing execution ref")
                )
                continue
            if not loaded:
                mismatches.append(
                    LocalAiRuntimeAlignmentMismatch(section_name, f"plan warm but engine not loaded")
                )
                continue
            if loaded_ref and loaded_ref != plan_ref.strip():
                mismatches.append(
                    LocalAiRuntimeAlignmentMismatch(
                        section_name,
                        f"plan ref '{plan_ref}' but engine reports '{loaded_ref}'",
                    )
                )
        elif loaded:
            mismatches.append(
                LocalAiRuntimeAlignmentMismatch(
                    section_name,
                    f"plan idle but engine still loaded (ref={loaded_ref!r})",
                )
            )

    return mismatches
