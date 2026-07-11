"""Alias router preset validation per D5."""

from __future__ import annotations

import re
from typing import Any

INFRASTRUCTURE_KEYS = frozenset({"model", "mmproj", "version"})

FLEET_SCOPED_KEYS = frozenset(
    {
        "models-preset",
        "models-max",
        "no-models-autoload",
        "no-autoload",
        "threads",
        "parallel",
        "n-gpu-layers",
        "gpu-layers",
        "kv-offload",
        "no-kv-offload",
        "kv-unified",
        "jinja",
        "cont-batching",
        "no-mmap",
        "flash-attn",
        "cache-type-k",
        "cache-type-v",
        "tensor-split",
        "cuda-visible-devices",
    }
)

_CONTROL_CHAR_RE = re.compile(r"[\x00-\x08\x0b\x0c\x0e-\x1f\x7f]")
_SHELL_FRAGMENT_RE = re.compile(r"[;&|`$<>]|\$\(|\${")


class PresetValidationError(ValueError):
    def __init__(self, code: str, message: str) -> None:
        super().__init__(message)
        self.code = code


def normalize_preset_key(key: str) -> str:
    return key.strip()


def normalize_alias(alias: str) -> str:
    trimmed = alias.strip()
    if not trimmed:
        raise PresetValidationError("ALIAS_REQUIRED", "Router alias is required.")
    if _CONTROL_CHAR_RE.search(trimmed):
        raise PresetValidationError("ALIAS_INVALID", "Router alias contains control characters.")
    return trimmed


def validate_preset_key(key: str) -> str:
    normalized = normalize_preset_key(key)
    if not normalized:
        raise PresetValidationError("PRESET_KEY_BLANK", "Preset keys cannot be blank.")
    if _CONTROL_CHAR_RE.search(normalized):
        raise PresetValidationError("PRESET_KEY_INVALID", f"Preset key '{normalized}' contains control characters.")
    lower = normalized.lower()
    if lower in INFRASTRUCTURE_KEYS:
        raise PresetValidationError(
            "PRESET_INFRASTRUCTURE_KEY",
            f"Preset cannot include infrastructure key '{normalized}'.",
        )
    if lower in FLEET_SCOPED_KEYS:
        raise PresetValidationError(
            "PRESET_FLEET_KEY",
            f"Preset key '{normalized}' is fleet-scoped. Use the Fleet llama server editor.",
        )
    return normalized


def validate_preset_value(value: Any) -> str:
    if not isinstance(value, str):
        raise PresetValidationError("PRESET_VALUE_TYPE", "Preset values must be strings.")
    trimmed = value.strip()
    if _CONTROL_CHAR_RE.search(trimmed) or "\n" in value or "\r" in value:
        raise PresetValidationError("PRESET_VALUE_INVALID", "Preset values cannot contain control characters or newlines.")
    if _SHELL_FRAGMENT_RE.search(trimmed):
        raise PresetValidationError("PRESET_VALUE_SHELL", "Preset values cannot contain shell metacharacters.")
    return trimmed


def normalize_preset_map(preset: dict[str, Any] | None) -> dict[str, str]:
    if preset is None:
        return {}
    if not isinstance(preset, dict):
        raise PresetValidationError("PRESET_TYPE", "Preset must be an object map.")

    normalized: dict[str, str] = {}
    seen_lower: dict[str, str] = {}
    for raw_key, raw_value in preset.items():
        key = validate_preset_key(str(raw_key))
        value = validate_preset_value(raw_value)
        lower = key.lower()
        if lower in seen_lower and seen_lower[lower] != key:
            raise PresetValidationError(
                "PRESET_DUPLICATE_KEY",
                f"Duplicate preset keys under case normalization: '{seen_lower[lower]}' and '{key}'.",
            )
        seen_lower[lower] = key
        normalized[key] = value
    return normalized


def apply_preset_mode(
    existing_extras: dict[str, str],
    incoming_preset: dict[str, str],
    preset_mode: str,
) -> dict[str, str]:
    mode = (preset_mode or "replace").strip().lower()
    if mode not in {"replace", "merge"}:
        raise PresetValidationError("PRESET_MODE_INVALID", "presetMode must be 'replace' or 'merge'.")
    if mode == "replace":
        return dict(incoming_preset)
    merged = dict(existing_extras)
    merged.update(incoming_preset)
    return merged
