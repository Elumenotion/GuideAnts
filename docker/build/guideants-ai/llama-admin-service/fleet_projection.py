"""Revisioned fleet projection consumed by start-llama.sh on every router spawn."""

from __future__ import annotations

import json
import os
import tempfile
from datetime import datetime, timezone
from typing import Any

FLEET_PRESET_STATE: dict[str, Any] = {
    "desiredRevision": 0,
    "appliedRevision": 0,
    "applyStatus": "pending",
    "applyError": None,
    "preset": {},
}


def resolve_projection_dir() -> str:
    explicit = (os.getenv("GA_LLAMA_FLEET_PROJECTION_DIR") or "").strip()
    if explicit:
        return os.path.abspath(explicit)
    model_dir = (os.getenv("GA_LLAMA_MODEL_DIR") or "/models-local/llama").strip()
    return os.path.abspath(os.path.join(model_dir, "runtime", "fleet"))


def resolve_projection_path() -> str:
    return os.path.join(resolve_projection_dir(), "fleet-projection.json")


ALIAS_FORBIDDEN_KEYS = {
    "ctx-size",
    "cache-ram",
    "image-min-tokens",
    "image-max-tokens",
    "spec-type",
    "spec-draft-n-max",
    "model",
    "mmproj",
    "version",
}

FLEET_KEY_ENV = {
    "jinja": ("GA_LLAMA_JINJA", lambda value: "1" if bool(value) else "0"),
    "parallel": ("GA_LLAMA_PARALLEL", lambda value: str(int(value))),
    "threads": ("GA_LLAMA_THREADS", lambda value: str(int(value))),
    "kvUnified": ("GA_LLAMA_KV_UNIFIED", lambda value: "1" if bool(value) else "0"),
    "contBatching": ("GA_LLAMA_CONT_BATCH", lambda value: "1" if bool(value) else "0"),
    "flashAttn": ("GA_LLAMA_FLASH_ATTN", lambda value: str(value)),
    "modelsMax": ("GA_LLAMA_MODELS_MAX", lambda value: str(int(value))),
    "noAutoload": ("GA_LLAMA_NO_AUTOLOAD", lambda value: "1" if bool(value) else "0"),
    "gpuLayers": ("GA_LLAMA_GPU_LAYERS", lambda value: str(int(value))),
    "kvOffload": ("GA_LLAMA_KV_OFFLOAD", lambda value: str(value)),
    "noMmap": ("GA_LLAMA_NO_MMAP", lambda value: "1" if bool(value) else "0"),
    "cacheTypeK": ("GA_LLAMA_CACHE_TYPE_K", lambda value: str(value)),
    "cacheTypeV": ("GA_LLAMA_CACHE_TYPE_V", lambda value: str(value)),
    "tensorSplit": ("GA_LLAMA_TENSOR_SPLIT", lambda value: str(value)),
    "cudaVisibleDevices": ("GA_LLAMA_CUDA_VISIBLE_DEVICES", lambda value: str(value)),
}


def validate_preset(preset: dict[str, Any]) -> dict[str, Any]:
    normalized: dict[str, Any] = {}
    for key, value in preset.items():
        if key in ALIAS_FORBIDDEN_KEYS:
            raise ValueError(f"Alias-scoped fleet key rejected: {key}")
        if key not in FLEET_KEY_ENV:
            raise ValueError(f"Unknown fleet preset key: {key}")
        normalized[key] = value
    return normalized


def preset_to_fleet_env(preset: dict[str, Any]) -> dict[str, str]:
    env: dict[str, str] = {}
    for key, value in preset.items():
        env_key, formatter = FLEET_KEY_ENV[key]
        env[env_key] = formatter(value)
    return env


def build_projection_document(
    *,
    revision: int,
    desired_revision: int,
    applied_revision: int,
    apply_status: str,
    apply_error: str | None,
    preset: dict[str, Any],
) -> dict[str, Any]:
    return {
        "revision": revision,
        "desiredRevision": desired_revision,
        "appliedRevision": applied_revision,
        "applyStatus": apply_status,
        "applyError": apply_error,
        "writtenAt": datetime.now(timezone.utc).isoformat(),
        "fleetEnv": preset_to_fleet_env(preset),
    }


def atomic_write_projection(document: dict[str, Any]) -> str:
    projection_dir = resolve_projection_dir()
    os.makedirs(projection_dir, exist_ok=True)
    target = resolve_projection_path()
    tmp = f"{target}.tmp"
    with open(tmp, "w", encoding="utf-8") as handle:
        json.dump(document, handle, indent=2, sort_keys=True)
        handle.flush()
        os.fsync(handle.fileno())
    os.replace(tmp, target)
    return target


def get_fleet_preset_response() -> dict[str, Any]:
    return {
        "desiredRevision": FLEET_PRESET_STATE["desiredRevision"],
        "appliedRevision": FLEET_PRESET_STATE["appliedRevision"],
        "applyStatus": FLEET_PRESET_STATE["applyStatus"],
        "applyError": FLEET_PRESET_STATE["applyError"],
        "preset": dict(FLEET_PRESET_STATE.get("preset") or {}),
    }


def put_fleet_preset(expected_revision: int, preset: dict[str, Any]) -> dict[str, Any]:
    if expected_revision != FLEET_PRESET_STATE["desiredRevision"]:
        raise ValueError(
            f"Fleet preset revision mismatch. Expected {expected_revision}, "
            f"current desired revision is {FLEET_PRESET_STATE['desiredRevision']}."
        )

    normalized = validate_preset(preset)
    desired_revision = FLEET_PRESET_STATE["desiredRevision"] + 1
    document = build_projection_document(
        revision=desired_revision,
        desired_revision=desired_revision,
        applied_revision=FLEET_PRESET_STATE["appliedRevision"],
        apply_status="pending_restart",
        apply_error=None,
        preset=normalized,
    )
    atomic_write_projection(document)

    FLEET_PRESET_STATE["desiredRevision"] = desired_revision
    FLEET_PRESET_STATE["preset"] = normalized
    FLEET_PRESET_STATE["applyStatus"] = "pending_restart"
    FLEET_PRESET_STATE["applyError"] = None

    # Restart confirmation updates appliedRevision in a follow-up call from the API layer.
    return get_fleet_preset_response()


def confirm_fleet_restart(applied_revision: int) -> dict[str, Any]:
    FLEET_PRESET_STATE["appliedRevision"] = applied_revision
    FLEET_PRESET_STATE["applyStatus"] = "applied"
    FLEET_PRESET_STATE["applyError"] = None
    document = build_projection_document(
        revision=applied_revision,
        desired_revision=FLEET_PRESET_STATE["desiredRevision"],
        applied_revision=applied_revision,
        apply_status="applied",
        apply_error=None,
        preset=dict(FLEET_PRESET_STATE.get("preset") or {}),
    )
    atomic_write_projection(document)
    return get_fleet_preset_response()
