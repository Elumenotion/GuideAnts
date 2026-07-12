"""Persisted warmup orchestrator state (.warmup-state.json)."""

from __future__ import annotations

import json
import os
import threading
from datetime import datetime, timezone
from typing import Any

from warmup_desired_ini import (
    SERVICE_LLAMA,
    WARMUP_SERVICE_SECTIONS,
    WarmupDesiredDocument,
    WarmupServiceSection,
    read_warmup_desired,
    resolve_warmup_desired_path,
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
    if explicit:
        return explicit
    desired_path = resolve_warmup_desired_path()
    return os.path.join(os.path.dirname(desired_path), ".warmup-state.json")


def _utc_now_iso() -> str:
    return datetime.now(timezone.utc).replace(microsecond=0).isoformat().replace("+00:00", "Z")


def _service_state_from_section(section_name: str, section: WarmupServiceSection) -> dict[str, Any]:
    applied = "idle"
    state: dict[str, Any] = {
        "desired": section.desired,
        "applied": applied,
        "phase": SERVICE_PHASE_IDLE,
        "error": None,
    }
    if section_name == SERVICE_LLAMA and section.router_alias:
        state["routerAlias"] = section.router_alias
    if section.model_id:
        state["modelId"] = section.model_id
    if section.bundle_id:
        state["bundleId"] = section.bundle_id
    return state


def build_service_states_from_desired(document: WarmupDesiredDocument) -> dict[str, dict[str, Any]]:
    services: dict[str, dict[str, Any]] = {}
    for section_name in WARMUP_SERVICE_SECTIONS:
        section = document.sections.get(section_name)
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
        "schemaVersion": 1,
        "desiredRevision": desired_revision,
        "appliedRevision": applied_revision,
        "inProgressRevision": in_progress_revision,
        "applyStatus": apply_status,
        "applyError": apply_error,
        "desiredSha256": desired_sha256,
        "writtenAt": written_at or _utc_now_iso(),
        "services": services,
    }


def build_initial_state_from_desired(
    document: WarmupDesiredDocument,
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


def sync_state_after_desired_write(
    document: WarmupDesiredDocument,
    *,
    desired_sha256: str,
    changed: bool,
) -> dict[str, Any]:
    """Update warmup state after a desired INI write."""
    with WARMUP_STATE_LOCK:
        current = _read_warmup_state_unlocked()
        if current is None:
            state = build_initial_state_from_desired(document, desired_sha256=desired_sha256)
            atomic_write_warmup_state(state)
            return state

        next_state = dict(current)
        next_state["desiredRevision"] = document.revision
        next_state["desiredSha256"] = desired_sha256
        next_state["writtenAt"] = _utc_now_iso()

        services = dict(next_state.get("services") or {})
        for section_name in WARMUP_SERVICE_SECTIONS:
            section = document.sections.get(section_name)
            if section is None:
                services.pop(section_name, None)
                continue
            prior = services.get(section_name) or {}
            updated = _service_state_from_section(section_name, section)
            updated["applied"] = prior.get("applied", "idle")
            updated["phase"] = prior.get("phase", SERVICE_PHASE_IDLE)
            updated["error"] = prior.get("error")
            services[section_name] = updated
        next_state["services"] = services

        if changed and next_state.get("appliedRevision", 0) < document.revision:
            next_state["applyStatus"] = APPLY_STATUS_PENDING
            next_state["inProgressRevision"] = None

        atomic_write_warmup_state(next_state)
        return next_state


def get_warmup_status_response() -> dict[str, Any]:
    desired = read_warmup_desired()
    state = read_warmup_state()
    if state is None and desired is not None:
        state = build_initial_state_from_desired(
            desired,
            desired_sha256=desired.content_fingerprint(),
        )
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
