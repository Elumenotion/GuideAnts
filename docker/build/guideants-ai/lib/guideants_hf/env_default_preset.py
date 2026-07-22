"""Env-default llama preset keys filled into runtime alias sections at spawn."""

from __future__ import annotations

import os
from collections.abc import Callable
from typing import Any

# Knobs defaulted from GA_LLAMA_* when absent on the alias preset.
# Users may override any of these on the alias; the UI does not auto-show env values.
ENV_DEFAULT_PRESET_KEYS = frozenset(
    {
        "n-gpu-layers",
        "no-mmap",
        "threads",
        "parallel",
        "kv-unified",
        "kv-offload",
        "no-kv-offload",
        "jinja",
        "cont-batching",
        "flash-attn",
        "cache-type-k",
        "cache-type-v",
        "tensor-split",
    }
)

EnvResolver = Callable[[str | None], str | None]


def _truthy_env(value: str | None) -> bool:
    return bool(value) and value.strip().lower() not in {"0", "false", "no", "off"}


def _flag_true(value: str | None) -> str | None:
    return "true" if _truthy_env(value) else None


def _passthrough(value: str | None) -> str | None:
    if value is None:
        return None
    trimmed = value.strip()
    return trimmed if trimmed else None


def _kv_offload_entries(value: str | None) -> dict[str, str]:
    if value is None or not value.strip():
        return {}
    normalized = value.strip()
    if normalized == "0":
        return {"no-kv-offload": "true"}
    if normalized == "1":
        return {"kv-offload": "true"}
    return {}


# (ini_key, env_var, resolver) — resolver returns INI value or None to skip.
_ENV_DEFAULT_SOURCES: tuple[tuple[str, str, EnvResolver | None], ...] = (
    ("n-gpu-layers", "GA_LLAMA_GPU_LAYERS", _passthrough),
    ("no-mmap", "GA_LLAMA_NO_MMAP", lambda v: "true" if v == "1" else None),
    ("threads", "GA_LLAMA_THREADS", _passthrough),
    ("parallel", "GA_LLAMA_PARALLEL", _passthrough),
    ("kv-unified", "GA_LLAMA_KV_UNIFIED", lambda v: "true" if v == "1" else None),
    ("jinja", "GA_LLAMA_JINJA", _flag_true),
    ("cont-batching", "GA_LLAMA_CONT_BATCH", lambda v: "true" if v == "1" else None),
    ("flash-attn", "GA_LLAMA_FLASH_ATTN", _passthrough),
    ("cache-type-k", "GA_LLAMA_CACHE_TYPE_K", _passthrough),
    ("cache-type-v", "GA_LLAMA_CACHE_TYPE_V", _passthrough),
    ("tensor-split", "GA_LLAMA_TENSOR_SPLIT", _passthrough),
)


def _extras_lower_keys(extras: dict[str, str]) -> set[str]:
    return {key.strip().lower() for key in extras}


def resolve_env_default_extras(
    extras: dict[str, str],
    *,
    environ: dict[str, str] | None = None,
) -> dict[str, str]:
    """Return env-derived preset entries for keys missing from alias extras."""
    env = environ if environ is not None else os.environ
    present = _extras_lower_keys(extras)
    resolved: dict[str, str] = {}

    for ini_key, env_var, resolver in _ENV_DEFAULT_SOURCES:
        if ini_key in present:
            continue
        if resolver is None:
            continue
        raw = env.get(env_var)
        value = resolver(raw)
        if value is not None:
            resolved[ini_key] = value

    kv_raw = env.get("GA_LLAMA_KV_OFFLOAD")
    for kv_key, kv_value in _kv_offload_entries(kv_raw).items():
        if kv_key not in present and kv_key not in {k.lower() for k in resolved}:
            resolved[kv_key] = kv_value

    return resolved


def apply_env_defaults_to_extras(
    extras: dict[str, str],
    *,
    environ: dict[str, str] | None = None,
) -> dict[str, str]:
    """Build effective alias extras: saved keys plus env fill for missing defaults."""
    merged = dict(extras)
    merged.update(resolve_env_default_extras(extras, environ=environ))
    return merged
