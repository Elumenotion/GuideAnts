"""Persisted status emitted by the API-commanded warmup executor."""

from __future__ import annotations

import json
import os
import threading
from datetime import datetime, timezone
from typing import Any, Callable

from warmup_plan import (
    WARMUP_SERVICE_SECTIONS,
    WarmupPlanDocument,
    WarmupServiceSection,
    section_execution_ref,
)

WARMUP_STATE_PATH = "/models-local/.warmup-state.json"
WARMUP_STATE_LOCK = threading.Lock()

APPLY_STATUS_IDLE = "idle"
APPLY_STATUS_PENDING = "pending"
APPLY_STATUS_APPLYING = "applying"
APPLY_STATUS_APPLIED = "applied"
APPLY_STATUS_FAILED = "failed"

SERVICE_PHASE_IDLE = "idle"


def resolve_warmup_state_path() -> str:
    explicit = (os.getenv("GA_WARMUP_STATE_PATH") or "").strip()
    return explicit or WARMUP_STATE_PATH


def _utc_now_iso() -> str:
    return datetime.now(timezone.utc).replace(microsecond=0).isoformat().replace("+00:00", "Z")


def _loaded_ref_from_service_state(service_state: dict[str, Any]) -> str | None:
    if service_state.get("routerAlias"):
        return str(service_state["routerAlias"])
    if service_state.get("bundleId"):
        return str(service_state["bundleId"])
    if service_state.get("modelId"):
        return str(service_state["modelId"])
    return None


def _service_state_from_section(section_name: str, section: WarmupServiceSection) -> dict[str, Any]:
    """Fresh service state from the latest explicit API command."""
    plan_ref = section_execution_ref(section_name, section)
    state: dict[str, Any] = {
        "phase": SERVICE_PHASE_IDLE,
        "error": None,
    }
    if plan_ref:
        state["planRef"] = plan_ref
    return state


def build_service_states_from_desired(document: WarmupPlanDocument) -> dict[str, dict[str, Any]]:
    services: dict[str, dict[str, Any]] = {}
    for section_name in WARMUP_SERVICE_SECTIONS:
        section = document.services.get(section_name)
        if section is None:
            continue
        services[section_name] = _service_state_from_section(section_name, section)
    return services


def build_warmup_state_document(
    *,
    desired_revision: int,
    applied_revision: int,
    apply_status: str,
    apply_error: str | None,
    desired_sha256: str,
    services: dict[str, dict[str, Any]],
    in_progress_revision: int | None = None,
    written_at: str | None = None,
) -> dict[str, Any]:
    return {
        "schemaVersion": 2,
        "desiredRevision": desired_revision,
        "appliedRevision": applied_revision,
        "inProgressRevision": in_progress_revision,
        "applyStatus": apply_status,
        "applyError": apply_error,
        "desiredSha256": desired_sha256,
        "writtenAt": written_at or _utc_now_iso(),
        "services": services,
    }


def build_initial_state_from_plan(
    document: WarmupPlanDocument,
    *,
    desired_sha256: str,
) -> dict[str, Any]:
    return build_warmup_state_document(
        desired_revision=document.revision,
        applied_revision=0,
        apply_status=APPLY_STATUS_PENDING,
        apply_error=None,
        desired_sha256=desired_sha256,
        services=build_service_states_from_desired(document),
        in_progress_revision=None,
    )


def atomic_write_warmup_state(document: dict[str, Any]) -> str:
    path = resolve_warmup_state_path()
    directory = os.path.dirname(path)
    if directory:
        os.makedirs(directory, exist_ok=True)
    temp_path = f"{path}.tmp"
    with open(temp_path, "w", encoding="utf-8") as handle:
        json.dump(document, handle, indent=2, sort_keys=True)
        handle.write("\n")
        handle.flush()
        os.fsync(handle.fileno())
    os.replace(temp_path, path)
    return path


def _read_warmup_state_unlocked() -> dict[str, Any] | None:
    path = resolve_warmup_state_path()
    if not os.path.exists(path):
        return None
    with open(path, "r", encoding="utf-8") as handle:
        return json.load(handle)


def read_warmup_state() -> dict[str, Any] | None:
    with WARMUP_STATE_LOCK:
        return _read_warmup_state_unlocked()


def sync_state_after_plan_submission(
    document: WarmupPlanDocument,
    *,
    desired_sha256: str,
    changed: bool,
) -> dict[str, Any]:
    """Update executor status after GuideAntsApi submits a complete plan."""
    with WARMUP_STATE_LOCK:
        current = _read_warmup_state_unlocked()
        if current is None:
            state = build_initial_state_from_plan(document, desired_sha256=desired_sha256)
            atomic_write_warmup_state(state)
            return state

        next_state = dict(current)
        next_state["schemaVersion"] = 2
        next_state["desiredRevision"] = document.revision
        next_state["desiredSha256"] = desired_sha256
        next_state["writtenAt"] = _utc_now_iso()

        services = dict(next_state.get("services") or {})
        for section_name in WARMUP_SERVICE_SECTIONS:
            section = document.services.get(section_name)
            if section is None:
                prior = services.get(section_name)
                if prior and _loaded_ref_from_service_state(prior):
                    services[section_name] = {
                        "phase": prior.get("phase", SERVICE_PHASE_IDLE),
                        "error": prior.get("error"),
                        **_loaded_ref_fields(prior),
                    }
                else:
                    services.pop(section_name, None)
                continue

            prior = dict(services.get(section_name) or {})
            plan_ref = section_execution_ref(section_name, section)
            updated: dict[str, Any] = {
                "phase": prior.get("phase", SERVICE_PHASE_IDLE),
                "error": prior.get("error"),
            }
            if plan_ref:
                updated["planRef"] = plan_ref
            else:
                updated.pop("planRef", None)

            for key in ("modelId", "bundleId", "routerAlias"):
                if key in prior:
                    updated[key] = prior[key]

            loaded_ref = _loaded_ref_from_service_state(prior)
            if plan_ref and loaded_ref and plan_ref != loaded_ref:
                updated["phase"] = SERVICE_PHASE_IDLE
                updated["error"] = None

            services[section_name] = updated
        next_state["services"] = services

        if changed and next_state.get("appliedRevision", 0) < document.revision:
            next_state["applyStatus"] = APPLY_STATUS_PENDING
            next_state["inProgressRevision"] = None

        atomic_write_warmup_state(next_state)
        return next_state


def _loaded_ref_fields(prior: dict[str, Any]) -> dict[str, Any]:
    fields: dict[str, Any] = {}
    for key in ("modelId", "bundleId", "routerAlias"):
        if key in prior:
            fields[key] = prior[key]
    return fields


def mutate_warmup_state(mutator: Callable[[dict[str, Any]], None]) -> dict[str, Any]:
    """Read-modify-write warmup state under lock."""
    with WARMUP_STATE_LOCK:
        state = _read_warmup_state_unlocked()
        if state is None:
            raise RuntimeError("warmup state is missing")
        mutator(state)
        atomic_write_warmup_state(state)
        return dict(state)


def get_warmup_status_response() -> dict[str, Any]:
    state = read_warmup_state()
    if state is None:
        return build_warmup_state_document(
            desired_revision=0,
            applied_revision=0,
            apply_status=APPLY_STATUS_IDLE,
            apply_error=None,
            desired_sha256="",
            services={},
        )
    return state
