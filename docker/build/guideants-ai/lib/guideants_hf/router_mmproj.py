"""Per-alias mmproj override and env-default materialization for llama.cpp router spawn."""

from __future__ import annotations

from guideants_hf.env_default_preset import apply_env_defaults_to_extras

# CLI spellings llama.cpp's INI parser does not accept (hyphens are stripped to
# `nommproj`, which is not a registered preset option). Canonicalize to mmproj-auto=false.
NO_MMPROJ_KEYS = frozenset({"no-mmproj", "no_mmproj", "nommproj", "no-mmproj-auto", "nommprojauto"})
MMPROJ_AUTO_KEY = "mmproj-auto"
MMPROJ_AUTO_DISABLED_VALUE = "false"
_FALSEY_VALUES = frozenset({"", "0", "false", "no", "off"})


def llama_option_token(key: str) -> str:
    """Match llama.cpp INI option names after hyphen/underscore stripping."""
    return "".join(ch for ch in key.strip().lower() if ch not in "-_")


def _is_falsey(value: str) -> bool:
    return value.strip().lower() in _FALSEY_VALUES


def preset_disables_mmproj(extras: dict[str, str]) -> bool:
    """True when the alias preset explicitly disables vision projector loading."""
    for key, value in extras.items():
        token = llama_option_token(key)
        if token in {"nommproj", "nommprojauto"}:
            return True
        if token == "mmprojauto" and _is_falsey(value):
            return True
    return False


def canonicalize_mmproj_disable_keys(preset: dict[str, str]) -> dict[str, str]:
    """Rewrite CLI-shaped mmproj disable flags to mmproj-auto=false."""
    if not preset:
        return preset
    disable = False
    rewritten: dict[str, str] = {}
    for key, value in preset.items():
        token = llama_option_token(key)
        if token in {"nommproj", "nommprojauto"}:
            disable = True
            continue
        if token == "mmprojauto":
            if _is_falsey(value):
                disable = True
            else:
                rewritten[MMPROJ_AUTO_KEY] = value
            continue
        rewritten[key] = value
    if disable:
        rewritten[MMPROJ_AUTO_KEY] = MMPROJ_AUTO_DISABLED_VALUE
    return rewritten


def resolve_router_runtime_config_path(canonical_path: str) -> str:
    trimmed = canonical_path.strip()
    if trimmed.lower().endswith(".ini"):
        return trimmed[:-4] + ".runtime.ini"
    return trimmed + ".runtime.ini"


def materialize_router_extras_for_runtime(extras: dict[str, str]) -> dict[str, str]:
    """Build effective alias extras for child spawn (mmproj translation + env defaults)."""
    effective = apply_env_defaults_to_extras(extras)
    if not preset_disables_mmproj(effective):
        return effective
    runtime_extras = {
        key: value
        for key, value in effective.items()
        if llama_option_token(key) not in {"nommproj", "nommprojauto"}
    }
    runtime_extras[MMPROJ_AUTO_KEY] = MMPROJ_AUTO_DISABLED_VALUE
    return runtime_extras


def materialize_router_ini_text(
    canonical_text: str,
    *,
    parse_router_ini,
    serialize_router_ini_for_runtime=None,
    serialize_router_ini=None,
) -> str:
    """Build router-facing INI from canonical storage while preserving base mmproj definitions."""
    serializer = serialize_router_ini_for_runtime or serialize_router_ini
    if serializer is None:
        raise TypeError("serialize_router_ini_for_runtime is required")
    entries = parse_router_ini(canonical_text)
    return serializer(entries)
