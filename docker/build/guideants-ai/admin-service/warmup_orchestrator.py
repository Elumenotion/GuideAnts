"""INI-driven warmup reconciler (single executor inside ga-admin)."""

from __future__ import annotations

import threading
from dataclasses import dataclass
from typing import Any, Callable

from warmup_desired_ini import (
    SERVICE_LLAMA,
    AUX_SERVICES,
    WarmupDesiredDocument,
    WarmupServiceSection,
    read_warmup_desired,
)
from warmup_engine_client import (
    list_llama_models,
    post_aux_load,
    post_aux_unload,
    post_llama_load,
    post_llama_unload,
    wait_aux_ready,
    wait_aux_unloaded,
    wait_llama_loaded,
    wait_llama_unloaded,
)
from warmup_state import (
    APPLY_STATUS_APPLIED,
    APPLY_STATUS_APPLYING,
    APPLY_STATUS_FAILED,
    APPLY_STATUS_IDLE,
    APPLY_STATUS_PENDING,
    atomic_write_warmup_state,
    build_initial_state_from_desired,
    mutate_warmup_state,
    read_warmup_state,
)

AUX_UNLOAD_ORDER = (
    "ImageGeneration",
    "SpeechSynthesis",
    "Embeddings",
    "SpeechTranscription",
)
AUX_LOAD_ORDER = (
    "SpeechTranscription",
    "Embeddings",
    "SpeechSynthesis",
    "ImageGeneration",
)

SERVICE_PHASE_LOADING = "loading"
SERVICE_PHASE_UNLOADING = "unloading"
SERVICE_PHASE_IDLE = "idle"
SERVICE_PHASE_READY = "ready"
SERVICE_PHASE_FAILED = "failed"

_APPLY_THREAD_LOCK = threading.Lock()
_APPLY_THREAD: threading.Thread | None = None
_LOG_EVENT: Callable[..., None] | None = None


def configure_warmup_orchestrator(*, log_event: Callable[..., None] | None = None) -> None:
    global _LOG_EVENT
    _LOG_EVENT = log_event


def _log(event: str, **fields: Any) -> None:
    if _LOG_EVENT is not None:
        _LOG_EVENT(event, **fields)


@dataclass(frozen=True)
class ServiceTransition:
    service: str
    action: str  # load | unload | noop


def _service_model_ref(section_name: str, section: WarmupServiceSection) -> str | None:
    if section_name == SERVICE_LLAMA:
        return section.router_alias
    if section_name == "ImageGeneration":
        return section.bundle_id
    return section.model_id


def _applied_model_ref(service_state: dict[str, Any]) -> str | None:
    if service_state.get("routerAlias"):
        return str(service_state["routerAlias"])
    if service_state.get("bundleId"):
        return str(service_state["bundleId"])
    if service_state.get("modelId"):
        return str(service_state["modelId"])
    return None


def _needs_transition(
    section_name: str,
    desired_section: WarmupServiceSection,
    service_state: dict[str, Any] | None,
) -> ServiceTransition:
    prior = service_state or {}
    applied = str(prior.get("applied", "idle")).lower()
    desired = desired_section.desired
    desired_ref = _service_model_ref(section_name, desired_section)
    applied_ref = _applied_model_ref(prior)

    if desired == "idle":
        if applied == "warm":
            return ServiceTransition(section_name, "unload")
        return ServiceTransition(section_name, "noop")

    # desired warm
    if applied != "warm":
        return ServiceTransition(section_name, "load")
    if desired_ref and applied_ref and desired_ref != applied_ref:
        return ServiceTransition(section_name, "load")
    if desired_ref and not applied_ref:
        return ServiceTransition(section_name, "load")
    return ServiceTransition(section_name, "noop")


def compute_transitions(
    document: WarmupDesiredDocument,
    state: dict[str, Any],
) -> list[ServiceTransition]:
    services = state.get("services") or {}
    transitions: list[ServiceTransition] = []

    llama_section = document.sections.get(SERVICE_LLAMA)
    if llama_section is not None:
        transitions.append(
            _needs_transition(SERVICE_LLAMA, llama_section, services.get(SERVICE_LLAMA))
        )

    for service in AUX_SERVICES:
        section = document.sections.get(service)
        if section is None:
            continue
        transitions.append(_needs_transition(service, section, services.get(service)))

    return transitions


def _transition_map(transitions: list[ServiceTransition]) -> dict[str, str]:
    return {item.service: item.action for item in transitions if item.action != "noop"}


def _llama_needs_gpu_drain(transitions: dict[str, str], document: WarmupDesiredDocument) -> bool:
    llama_action = transitions.get(SERVICE_LLAMA)
    if llama_action in {"load", "unload"}:
        return True
    llama_section = document.sections.get(SERVICE_LLAMA)
    if llama_section is None or llama_section.desired != "warm":
        return False
    # Loading aux while llama is not warm may still be fine; only drain when llama itself changes.
    return False


def _aux_services_to_drain_before_llama(
    document: WarmupDesiredDocument,
    state: dict[str, Any],
) -> set[str]:
    """D11 GPU drain: unload every aux that should stay warm but is currently applied warm."""
    services = state.get("services") or {}
    to_drain: set[str] = set()
    for service in AUX_UNLOAD_ORDER:
        section = document.sections.get(service)
        if section is None or section.desired != "warm":
            continue
        entry = services.get(service) or {}
        applied = str(entry.get("applied", "idle")).lower()
        if applied == "warm":
            to_drain.add(service)
    return to_drain


def _patch_state(
    mutator: Callable[[dict[str, Any]], None],
) -> dict[str, Any]:
    return mutate_warmup_state(mutator)


def _set_service_phase(
    service: str,
    *,
    phase: str,
    applied: str | None = None,
    error: str | None = None,
) -> None:
    def mutate(state: dict[str, Any]) -> None:
        services = dict(state.get("services") or {})
        entry = dict(services.get(service) or {})
        entry["phase"] = phase
        if applied is not None:
            entry["applied"] = applied
        if error is not None:
            entry["error"] = error
        services[service] = entry
        state["services"] = services

    _patch_state(mutate)


def _set_apply_meta(
    *,
    apply_status: str,
    apply_error: str | None = None,
    applied_revision: int | None = None,
    in_progress_revision: int | None = None,
) -> None:
    def mutate(state: dict[str, Any]) -> None:
        state["applyStatus"] = apply_status
        state["applyError"] = apply_error
        if applied_revision is not None:
            state["appliedRevision"] = applied_revision
        if in_progress_revision is not None:
            state["inProgressRevision"] = in_progress_revision

    _patch_state(mutate)


def _unload_llama_to_idle() -> bool:
    loaded_aliases = [
        str(entry.get("id"))
        for entry in list_llama_models()
        if entry.get("id") and _is_llama_loaded_entry(entry)
    ]
    ok = True
    for alias in loaded_aliases:
        _set_service_phase(SERVICE_LLAMA, phase=SERVICE_PHASE_UNLOADING)
        if not post_llama_unload(alias):
            ok = False
        elif not wait_llama_unloaded(alias):
            ok = False
    if ok:
        _set_service_phase(SERVICE_LLAMA, phase=SERVICE_PHASE_IDLE, applied="idle", error=None)
    else:
        _set_service_phase(SERVICE_LLAMA, phase=SERVICE_PHASE_FAILED, error="llama unload timed out")
    return ok


def _is_llama_loaded_entry(entry: dict[str, Any]) -> bool:
    status = entry.get("status")
    if isinstance(status, dict):
        value = status.get("value")
        if isinstance(value, str) and value.lower() == "loaded":
            return True
    state = entry.get("state")
    return isinstance(state, str) and state.lower() == "loaded"


def _load_llama_alias(alias: str) -> bool:
    loaded_aliases = [
        str(entry.get("id"))
        for entry in list_llama_models()
        if entry.get("id") and _is_llama_loaded_entry(entry)
    ]
    ok = True
    for loaded in loaded_aliases:
        if loaded == alias:
            continue
        if not post_llama_unload(loaded):
            ok = False
        elif not wait_llama_unloaded(loaded):
            ok = False
    _set_service_phase(SERVICE_LLAMA, phase=SERVICE_PHASE_LOADING)
    if not post_llama_load(alias):
        _set_service_phase(SERVICE_LLAMA, phase=SERVICE_PHASE_FAILED, error="llama load request failed")
        return False
    if not wait_llama_loaded(alias):
        _set_service_phase(SERVICE_LLAMA, phase=SERVICE_PHASE_FAILED, error="llama load timed out")
        return False
    _set_service_phase(SERVICE_LLAMA, phase=SERVICE_PHASE_READY, applied="warm", error=None)
    return ok


def _reconcile_llama(section: WarmupServiceSection, action: str) -> bool:
    if action == "unload":
        return _unload_llama_to_idle()
    if action == "load":
        alias = (section.router_alias or "").strip()
        if not alias:
            _set_service_phase(
                SERVICE_LLAMA,
                phase=SERVICE_PHASE_FAILED,
                error="llama desired warm but router_alias is missing",
            )
            return False
        return _load_llama_alias(alias)
    return True


def _model_ref_for_aux(service: str, section: WarmupServiceSection) -> str | None:
    if service == "ImageGeneration":
        return section.bundle_id
    return section.model_id


def _reconcile_aux(service: str, section: WarmupServiceSection, action: str) -> bool:
    if action == "unload":
        _set_service_phase(service, phase=SERVICE_PHASE_UNLOADING)
        if not post_aux_unload(service):
            _set_service_phase(service, phase=SERVICE_PHASE_FAILED, error="unload request failed")
            return False
        if not wait_aux_unloaded(service):
            _set_service_phase(service, phase=SERVICE_PHASE_FAILED, error="unload timed out")
            return False
        _set_service_phase(service, phase=SERVICE_PHASE_IDLE, applied="idle", error=None)
        return True

    if action == "load":
        model_ref = _model_ref_for_aux(service, section)
        if service != "ImageGeneration" and not model_ref:
            _set_service_phase(
                service,
                phase=SERVICE_PHASE_FAILED,
                error="desired warm but model_id is missing",
            )
            return False
        _set_service_phase(service, phase=SERVICE_PHASE_LOADING)
        if not post_aux_load(service, model_ref):
            _set_service_phase(service, phase=SERVICE_PHASE_FAILED, error="load request failed")
            return False
        if not wait_aux_ready(service):
            _set_service_phase(service, phase=SERVICE_PHASE_FAILED, error="ready timed out")
            return False
        _set_service_phase(service, phase=SERVICE_PHASE_READY, applied="warm", error=None)
        return True

    return True


def _cold_start_load_map(document: WarmupDesiredDocument) -> dict[str, str]:
    """Warm services to load on container cold start. Engines are empty — no unloads."""
    actions: dict[str, str] = {}
    for service in (SERVICE_LLAMA, *AUX_SERVICES):
        section = document.sections.get(service)
        if section is not None and section.desired == "warm":
            actions[service] = "load"
    return actions


def _execute_action_map(
    desired: WarmupDesiredDocument,
    state: dict[str, Any],
    action_map: dict[str, str],
) -> bool:
    ok = True

    llama_action = action_map.get(SERVICE_LLAMA)
    drain_aux = _llama_needs_gpu_drain(action_map, desired)
    gpu_reload_set = _aux_services_to_drain_before_llama(desired, state) if drain_aux else set()
    if drain_aux:
        to_drain = set(gpu_reload_set)
        for service in AUX_UNLOAD_ORDER:
            action = action_map.get(service)
            if action == "unload":
                to_drain.add(service)
        for service in AUX_UNLOAD_ORDER:
            if service not in to_drain:
                continue
            section = desired.sections.get(service)
            if section is None:
                continue
            if not _reconcile_aux(service, section, "unload"):
                ok = False

    if SERVICE_LLAMA in action_map:
        llama_section = desired.sections.get(SERVICE_LLAMA)
        if llama_section is not None:
            if not _reconcile_llama(llama_section, action_map[SERVICE_LLAMA]):
                ok = False

    for service in AUX_LOAD_ORDER:
        action = action_map.get(service)
        if action != "load" and service not in gpu_reload_set:
            continue
        section = desired.sections.get(service)
        if section is None:
            continue
        if not _reconcile_aux(service, section, "load"):
            ok = False

    for service in AUX_UNLOAD_ORDER:
        action = action_map.get(service)
        if action != "unload":
            continue
        section = desired.sections.get(service)
        if section is None:
            continue
        if drain_aux and llama_action in {"load", "unload"}:
            continue
        if not _reconcile_aux(service, section, "unload"):
            ok = False

    return ok


def _run_startup_apply() -> None:
    """Container cold start: load warm services from warmup-desired.ini."""
    try:
        desired = read_warmup_desired()
        if desired is None:
            return

        state = read_warmup_state()
        if state is None:
            return

        action_map = _cold_start_load_map(desired)
        if not action_map:
            _set_apply_meta(
                apply_status=APPLY_STATUS_APPLIED,
                apply_error=None,
                applied_revision=desired.revision,
                in_progress_revision=None,
            )
            return

        _set_apply_meta(
            apply_status=APPLY_STATUS_APPLYING,
            apply_error=None,
            in_progress_revision=desired.revision,
        )

        ok = _execute_action_map(desired, state, action_map)
        if ok:
            _set_apply_meta(
                apply_status=APPLY_STATUS_APPLIED,
                apply_error=None,
                applied_revision=desired.revision,
                in_progress_revision=None,
            )
        else:
            _set_apply_meta(
                apply_status=APPLY_STATUS_FAILED,
                apply_error="one or more services failed to load on startup",
                in_progress_revision=None,
            )
    except Exception as exc:  # noqa: BLE001 — background worker must not crash silently
        _log("warmup_startup_failed", reason=str(exc))
        _set_apply_meta(apply_status=APPLY_STATUS_FAILED, apply_error=str(exc), in_progress_revision=None)


def _run_reconcile_loop() -> None:
    try:
        while True:
            desired = read_warmup_desired()
            state = read_warmup_state()
            if desired is None or state is None:
                _set_apply_meta(apply_status=APPLY_STATUS_IDLE, apply_error=None)
                return

            target_revision = desired.revision
            applied_revision = int(state.get("appliedRevision") or 0)
            if target_revision <= applied_revision:
                _set_apply_meta(
                    apply_status=APPLY_STATUS_APPLIED,
                    apply_error=None,
                    applied_revision=applied_revision,
                    in_progress_revision=None,
                )
                return

            transitions = compute_transitions(desired, state)
            action_map = _transition_map(transitions)
            if not action_map:
                _set_apply_meta(
                    apply_status=APPLY_STATUS_APPLIED,
                    apply_error=None,
                    applied_revision=target_revision,
                    in_progress_revision=None,
                )
                return

            _set_apply_meta(
                apply_status=APPLY_STATUS_APPLYING,
                apply_error=None,
                in_progress_revision=target_revision,
            )

            ok = _execute_action_map(desired, state, action_map)

            # Re-read desired in case revision changed mid-apply.
            latest = read_warmup_desired()
            if latest is not None and latest.revision > target_revision:
                _log("warmup_apply_revision_advanced", fromRevision=target_revision, toRevision=latest.revision)
                continue

            if ok:
                _set_apply_meta(
                    apply_status=APPLY_STATUS_APPLIED,
                    apply_error=None,
                    applied_revision=target_revision,
                    in_progress_revision=None,
                )
            else:
                _set_apply_meta(
                    apply_status=APPLY_STATUS_FAILED,
                    apply_error="one or more services failed to reconcile",
                    in_progress_revision=None,
                )
            return
    except Exception as exc:  # noqa: BLE001 — background worker must not crash silently
        _log("warmup_apply_failed", reason=str(exc))
        _set_apply_meta(apply_status=APPLY_STATUS_FAILED, apply_error=str(exc), in_progress_revision=None)


def _start_worker(target: Callable[[], None], *, thread_name: str) -> bool:
    global _APPLY_THREAD
    with _APPLY_THREAD_LOCK:
        if _APPLY_THREAD is not None and _APPLY_THREAD.is_alive():
            return False
        _APPLY_THREAD = threading.Thread(target=target, name=thread_name, daemon=True)
        _APPLY_THREAD.start()
        return True


def _start_apply_worker_if_needed() -> bool:
    return _start_worker(_run_reconcile_loop, thread_name="warmup-orchestrator")


def _start_startup_worker_if_needed() -> bool:
    return _start_worker(_run_startup_apply, thread_name="warmup-startup")


def request_warmup_apply() -> dict[str, Any]:
    """Idempotent apply kick. Returns status summary for HTTP handlers."""
    desired = read_warmup_desired()
    state = read_warmup_state()
    if desired is None:
        return {"ok": True, "noop": True, "reason": "no_desired_ini"}

    if state is None:
        state = {
            "desiredRevision": desired.revision,
            "appliedRevision": 0,
            "applyStatus": APPLY_STATUS_PENDING,
            "desiredSha256": desired.content_fingerprint(),
            "services": {},
        }
        atomic_write_warmup_state(state)

    desired_revision = desired.revision
    applied_revision = int(state.get("appliedRevision") or 0)
    desired_sha = desired.content_fingerprint()
    stored_sha = str(state.get("desiredSha256") or "")
    in_progress = state.get("inProgressRevision")
    apply_status = str(state.get("applyStatus") or APPLY_STATUS_IDLE)

    if desired_revision == applied_revision:
        return {
            "ok": True,
            "noop": True,
            "desiredRevision": desired_revision,
            "appliedRevision": applied_revision,
            "applyStatus": APPLY_STATUS_APPLIED,
        }

    if (
        in_progress == desired_revision
        and stored_sha == desired_sha
        and apply_status == APPLY_STATUS_APPLYING
    ):
        return {
            "ok": True,
            "noop": True,
            "continue": True,
            "desiredRevision": desired_revision,
            "appliedRevision": applied_revision,
            "applyStatus": apply_status,
        }

    if apply_status != APPLY_STATUS_APPLYING:
        _set_apply_meta(apply_status=APPLY_STATUS_PENDING, apply_error=None)

    started = _start_apply_worker_if_needed()
    return {
        "ok": True,
        "noop": False,
        "started": started,
        "desiredRevision": desired_revision,
        "appliedRevision": applied_revision,
        "applyStatus": APPLY_STATUS_APPLYING if started else apply_status,
    }


def apply_warmup_on_startup() -> None:
    """Container startup: load warm services from warmup-desired.ini."""
    desired = read_warmup_desired()
    if desired is None:
        return

    # Fresh state for this process; volume state from a prior container is not authoritative.
    atomic_write_warmup_state(
        build_initial_state_from_desired(
            desired,
            desired_sha256=desired.content_fingerprint(),
        )
    )
    _log("warmup_startup", desiredRevision=desired.revision)
    _start_startup_worker_if_needed()
