"""
ga-admin warmup EXECUTOR (not orchestrator).

================================================================================
READ THIS BEFORE EDITING — FUTURE AGENTS AND HUMANS
================================================================================

GuideAntsApi is the ONLY lifecycle authority:
  - ServiceModes / routing decide enabled vs idle
  - LocalAiDesiredStateBuilder builds the complete JSON plan
  - LocalAiRuntimeAlignmentVerifier checks engine HTTP after apply

THIS FILE MUST NOT:
  - Infer policy from disk, env autoload flags, marker files, or .warmup-state.json
  - Return early "noop" because revision/fingerprint "already applied" while skipping
    engine unload/load work (that caused SD staying loaded under cloud routing)
  - Compare plan to executor memory to skip work ("compute_transitions noop")
  - Add "runtime drift" reconciliation — that belongs in GuideAntsApi
  - Bump routing decisions or choose models/bundles

THIS FILE MAY ONLY:
  - Accept a complete plan POSTed by GuideAntsApi
  - Derive mechanical commands from the plan body alone (on → load, off → unload)
  - Call local engine admin HTTP on loopback (warmup_engine_client)
  - Poll engines until ready/unloaded (mechanical waits)
  - Skip redundant LOAD calls when the ENGINE HTTP port already reports the plan ref
    (mechanical probe — never skip unload when plan says off)
  - Write .warmup-state.json as DIAGNOSTIC status for /warmup/status

If you think you need "smart" logic here, you are in the wrong file. Put it in
GuideAntsApi (C#) or delete the temptation and add a contract test instead.
================================================================================
"""

from __future__ import annotations

import os
import threading
from typing import Any, Callable

from warmup_plan import (
    WARMUP_SERVICE_SECTIONS,
    WarmupPlanDocument,
    WarmupServiceSection,
    aux_section_load_request,
    section_execution_ref,
    section_should_load,
)
from warmup_engine_client import (
    aux_engine_loaded_ref,
    aux_engine_reports_loaded,
    post_aux_load,
    post_aux_unload,
    wait_aux_ready,
    wait_aux_unloaded,
)
from warmup_state import (
    APPLY_STATUS_APPLIED,
    APPLY_STATUS_APPLYING,
    APPLY_STATUS_FAILED,
    APPLY_STATUS_IDLE,
    APPLY_STATUS_PENDING,
    atomic_write_warmup_state,
    build_warmup_state_document,
    mutate_warmup_state,
    read_warmup_state,
    sync_state_after_plan_submission,
)

# Frozen mechanical aux order: unload order then load order (single-GPU box physics, not routing).
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
_SERVICE_PHASE_FAILED = "failed"

_APPLY_THREAD_LOCK = threading.Lock()
_APPLY_THREAD: threading.Thread | None = None
_PLAN_LOCK = threading.Lock()
_LATEST_PLAN: WarmupPlanDocument | None = None
_LOG_EVENT: Callable[..., None] | None = None


def configure_warmup_orchestrator(*, log_event: Callable[..., None] | None = None) -> None:
    global _LOG_EVENT
    _LOG_EVENT = log_event


def _log(event: str, **fields: Any) -> None:
    if _LOG_EVENT is not None:
        _LOG_EVENT(event, **fields)


def derive_plan_commands(document: WarmupPlanDocument) -> dict[str, str]:
    """
    Mechanical commands derived ONLY from the submitted plan body.

    DO NOT read .warmup-state.json or prior revisions here. Every service is
    explicitly load or unload — no noop transitions.
    """
    commands: dict[str, str] = {}
    for section_name in WARMUP_SERVICE_SECTIONS:
        section = document.services.get(section_name)
        if section_should_load(section_name, section):
            commands[section_name] = "load"
        else:
            commands[section_name] = "unload"
    return commands


def _patch_state(mutator: Callable[[dict[str, Any]], None]) -> dict[str, Any]:
    return mutate_warmup_state(mutator)


def _set_service_phase(
    service: str,
    *,
    phase: str,
    error: str | None = None,
    model_id: str | None = None,
    bundle_id: str | None = None,
    clear_loaded_refs: bool = False,
) -> None:
    def mutate(state: dict[str, Any]) -> None:
        services = dict(state.get("services") or {})
        entry = dict(services.get(service) or {})
        entry["phase"] = phase
        if error is not None:
            entry["error"] = error
        else:
            entry.pop("error", None)
        if clear_loaded_refs:
            entry.pop("modelId", None)
            entry.pop("bundleId", None)
        if model_id is not None:
            entry["modelId"] = model_id
            entry.pop("bundleId", None)
        if bundle_id is not None:
            entry["bundleId"] = bundle_id
            entry.pop("modelId", None)
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


def _reconcile_aux(service: str, section: WarmupServiceSection, action: str) -> bool:
    if action == "unload":
        # Plan says OFF — always call unload (idempotent). Never skip because state file says idle.
        if not aux_engine_reports_loaded(service):
            _set_service_phase(service, phase=SERVICE_PHASE_IDLE, error=None, clear_loaded_refs=True)
            return True
        _set_service_phase(service, phase=SERVICE_PHASE_UNLOADING)
        if not post_aux_unload(service):
            _set_service_phase(service, phase=_SERVICE_PHASE_FAILED, error="unload request failed")
            return False
        if not wait_aux_unloaded(service):
            _set_service_phase(service, phase=_SERVICE_PHASE_FAILED, error="unload timed out")
            return False
        _set_service_phase(service, phase=SERVICE_PHASE_IDLE, error=None, clear_loaded_refs=True)
        return True

    if action == "load":
        if service == "ImageGeneration":
            model_ref = section_execution_ref(service, section)
            load_field = "model_path"
            if not model_ref:
                _set_service_phase(
                    service,
                    phase=_SERVICE_PHASE_FAILED,
                    error="plan missing bundle_id",
                )
                return False
        else:
            model_ref, load_field = aux_section_load_request(section, service=service)
        if service != "ImageGeneration" and not model_ref:
            _set_service_phase(
                service,
                phase=_SERVICE_PHASE_FAILED,
                error="plan missing model_path",
            )
            return False

        # Mechanical fast-path: engine HTTP already matches plan ref.
        if model_ref and aux_engine_reports_loaded(service):
            loaded_ref = aux_engine_loaded_ref(service)
            if loaded_ref and loaded_ref == model_ref.strip():
                if service == "ImageGeneration":
                    _set_service_phase(
                        service,
                        phase=SERVICE_PHASE_READY,
                        error=None,
                        bundle_id=model_ref,
                    )
                else:
                    _set_service_phase(
                        service,
                        phase=SERVICE_PHASE_READY,
                        error=None,
                        model_id=model_ref,
                    )
                return True

        _set_service_phase(service, phase=SERVICE_PHASE_LOADING)
        if not post_aux_load(service, model_ref, load_field=load_field):
            _set_service_phase(service, phase=_SERVICE_PHASE_FAILED, error="load request failed")
            return False
        if not wait_aux_ready(service, expected_model_ref=model_ref):
            _set_service_phase(service, phase=_SERVICE_PHASE_FAILED, error="ready timed out or model mismatch")
            return False
        if service == "ImageGeneration":
            _set_service_phase(
                service,
                phase=SERVICE_PHASE_READY,
                error=None,
                bundle_id=model_ref,
            )
        else:
            _set_service_phase(
                service,
                phase=SERVICE_PHASE_READY,
                error=None,
                model_id=model_ref,
            )
        return True

    return True


def _execute_plan_commands(
    desired: WarmupPlanDocument,
    commands: dict[str, str],
) -> bool:
    ok = True

    for service in AUX_UNLOAD_ORDER:
        if commands.get(service) != "unload":
            continue
        section = desired.services.get(service)
        if section is None:
            continue
        if not _reconcile_aux(service, section, "unload"):
            ok = False

    for service in AUX_LOAD_ORDER:
        if commands.get(service) != "load":
            continue
        section = desired.services.get(service)
        if section is None:
            continue
        if not _reconcile_aux(service, section, "load"):
            ok = False

    return ok


def _get_latest_plan() -> WarmupPlanDocument | None:
    with _PLAN_LOCK:
        return _LATEST_PLAN


def _store_plan(plan: WarmupPlanDocument) -> tuple[WarmupPlanDocument, bool]:
    global _LATEST_PLAN
    fingerprint = plan.content_fingerprint()
    with _PLAN_LOCK:
        state = read_warmup_state()
        current_revision = int((state or {}).get("desiredRevision") or 0)
        current_fingerprint = str((state or {}).get("desiredSha256") or "")
        changed = current_revision == 0 or current_fingerprint != fingerprint
        revision = current_revision + 1 if changed else current_revision
        versioned = plan.with_revision(revision)
        _LATEST_PLAN = versioned
        sync_state_after_plan_submission(
            versioned,
            desired_sha256=fingerprint,
            changed=changed,
        )
        return versioned, changed


def _run_apply_loop() -> None:
    """
    Background worker: run mechanical engine commands for the latest API plan.

    NEVER return early because appliedRevision caught up — engines may still be wrong.
    """
    try:
        while True:
            desired = _get_latest_plan()
            state = read_warmup_state()
            if desired is None or state is None:
                _set_apply_meta(apply_status=APPLY_STATUS_IDLE, apply_error=None)
                return

            target_revision = desired.revision
            commands = derive_plan_commands(desired)

            _set_apply_meta(
                apply_status=APPLY_STATUS_APPLYING,
                apply_error=None,
                in_progress_revision=target_revision,
            )

            ok = _execute_plan_commands(desired, commands)

            latest = _get_latest_plan()
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
                    apply_error="one or more services failed mechanical apply",
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
    return _start_worker(_run_apply_loop, thread_name="warmup-executor")


def request_warmup_apply(plan: WarmupPlanDocument) -> dict[str, Any]:
    """
    Accept one complete API-owned plan and run mechanical engine commands.

    DO NOT add revision/fingerprint "noop" that skips the apply worker. GuideAntsApi
    verifies engine HTTP after apply; ga-admin must still execute unload when the
    plan says off even if appliedRevision already matches.
    """
    desired, changed = _store_plan(plan)
    state = read_warmup_state()
    if state is None:
        raise RuntimeError("warmup state missing after plan submission")

    desired_revision = desired.revision
    applied_revision = int(state.get("appliedRevision") or 0)
    desired_sha = desired.content_fingerprint()
    stored_sha = str(state.get("desiredSha256") or "")
    in_progress = state.get("inProgressRevision")
    apply_status = str(state.get("applyStatus") or APPLY_STATUS_IDLE)

    # Only noop when the SAME plan is already being executed right now.
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
            "changed": changed,
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
        "changed": changed,
    }


def _legacy_policy_ini_basenames() -> tuple[str, str]:
    legacy = "warmup" + "-" + "desired" + ".ini"
    return legacy, "ai-" + legacy


def _purge_retired_legacy_policy_ini() -> None:
    """
    Delete retired INI policy files if they still exist on the models volume.

    Old ga-admin read these on boot and loaded SD/aux without GuideAntsApi. If this
    file survives a deploy, you get exactly the failure you are debugging.
    """
    legacy_basename, ai_legacy_basename = _legacy_policy_ini_basenames()
    candidates = [
        (os.getenv("GA_WARMUP_DESIRED_PATH") or "").strip(),
        "/models-local/" + legacy_basename,
        "/models-local/" + ai_legacy_basename,
    ]
    for path in candidates:
        if not path:
            continue
        if os.path.isfile(path):
            os.remove(path)
            _log("warmup_legacy_ini_purged", path=path)


def initialize_warmup_executor_on_startup() -> None:
    """Start idle with no plan. Only a later GuideAntsApi request may load engines."""
    global _LATEST_PLAN
    _purge_retired_legacy_policy_ini()
    with _PLAN_LOCK:
        _LATEST_PLAN = None
    atomic_write_warmup_state(
        build_warmup_state_document(
            desired_revision=0,
            applied_revision=0,
            apply_status=APPLY_STATUS_IDLE,
            apply_error=None,
            desired_sha256="",
            services={},
        )
    )
    _log(
        "warmup_startup_idle",
        waitingForApiCommand=True,
    )


# Back-compat for tests that imported the old reconcile loop name.
_run_reconcile_loop = _run_apply_loop

# Back-compat for tests that imported compute_transitions.
def compute_transitions(document: WarmupPlanDocument, state: dict[str, Any]) -> list:
    """Deprecated test helper: maps derive_plan_commands to legacy transition objects."""
    from dataclasses import dataclass

    @dataclass(frozen=True)
    class ServiceTransition:
        service: str
        action: str

    commands = derive_plan_commands(document)
    return [
        ServiceTransition(service=service, action=commands[service])
        for service in WARMUP_SERVICE_SECTIONS
    ]


# Back-compat for tests that imported the old reconcile loop name.
_run_reconcile_loop = _run_apply_loop

# Back-compat for tests that imported compute_transitions.
def compute_transitions(document: WarmupPlanDocument, state: dict[str, Any]) -> list:
    """Deprecated test helper: maps derive_plan_commands to legacy transition objects."""
    from dataclasses import dataclass

    @dataclass(frozen=True)
    class ServiceTransition:
        service: str
        action: str

    commands = derive_plan_commands(document)
    return [
        ServiceTransition(service=service, action=commands[service])
        for service in WARMUP_SERVICE_SECTIONS
    ]
