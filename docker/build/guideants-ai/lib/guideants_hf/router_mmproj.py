"""Per-alias mmproj override materialization for llama.cpp router spawn."""

from __future__ import annotations

NO_MMPROJ_KEYS = frozenset({"no-mmproj", "no_mmproj"})
MMPROJ_AUTO_KEY = "mmproj-auto"
MMPROJ_AUTO_DISABLED_VALUE = "false"


def preset_disables_mmproj(extras: dict[str, str]) -> bool:
    """True when the alias preset explicitly disables vision projector loading."""
    return any(key.strip().lower() in NO_MMPROJ_KEYS for key in extras)


def resolve_router_runtime_config_path(canonical_path: str) -> str:
    trimmed = canonical_path.strip()
    if trimmed.lower().endswith(".ini"):
        return trimmed[:-4] + ".runtime.ini"
    return trimmed + ".runtime.ini"


def materialize_router_extras_for_runtime(extras: dict[str, str]) -> dict[str, str]:
    """Translate user-facing no-mmproj override into llama.cpp preset keys."""
    if not preset_disables_mmproj(extras):
        return dict(extras)
    runtime_extras = {
        key: value
        for key, value in extras.items()
        if key.strip().lower() not in NO_MMPROJ_KEYS
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
