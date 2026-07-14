"""Router INI parsing and atomic preset writes (no FastAPI dependency)."""

from __future__ import annotations

import hashlib
import os
import tempfile
import threading
from dataclasses import dataclass
from typing import Any, Callable

from guideants_hf.preset_validation import (
    PresetValidationError,
    apply_preset_mode,
    normalize_alias,
    normalize_preset_map,
)
from guideants_hf.vision_token_preset import apply_alias_vision_token_preset

ROUTER_CONFIG_PATH = "/models-local/router-models.ini"
ROUTER_FILE_LOCK = threading.Lock()

_CTX_KEYS = ("ctx-size", "c", "ctx_size", "LLAMA_ARG_CTX_SIZE")
_CACHE_KEYS = ("cache-ram", "cache_ram", "LLAMA_ARG_CACHE_RAM")


@dataclass
class RouterSection:
    model: str
    mmproj: str
    extras: dict[str, str]


@dataclass
class RuntimeApplyResult:
    applied: bool
    ini_sha256: str
    remediation: str | None = None


def parse_router_ini(text: str) -> dict[str, RouterSection]:
    entries: dict[str, RouterSection] = {}
    current_alias: str | None = None
    model_path: str = ""
    mmproj_path: str = ""
    extras: dict[str, str] = {}

    def flush_current() -> None:
        nonlocal current_alias, model_path, mmproj_path, extras
        if current_alias and (model_path or mmproj_path or extras):
            entries[current_alias] = RouterSection(
                model=model_path,
                mmproj=mmproj_path,
                extras=dict(extras),
            )
        model_path = ""
        mmproj_path = ""
        extras = {}

    for raw_line in text.splitlines():
        line = raw_line.strip()
        if not line or line.startswith("#") or line.startswith(";"):
            continue
        if line.startswith("[") and line.endswith("]"):
            flush_current()
            current_alias = line[1:-1].strip()
            continue
        if "=" not in line:
            continue
        key, value = line.split("=", 1)
        key_raw = key.strip()
        key_lower = key_raw.lower()
        value = value.strip()
        if key_lower == "version":
            continue
        if key_lower == "model":
            model_path = value
        elif key_lower == "mmproj":
            mmproj_path = value
        else:
            extras[key_raw] = value

    flush_current()
    return entries


def serialize_router_ini(entries: dict[str, RouterSection]) -> str:
    lines: list[str] = ["version = 1", ""]
    for alias in sorted(entries.keys()):
        section = entries[alias]
        lines.append(f"[{alias}]")
        lines.append(f"model = {section.model}")
        lines.append(f"mmproj = {section.mmproj}")
        for extra_key in sorted(section.extras.keys()):
            lines.append(f"{extra_key} = {section.extras[extra_key]}")
        lines.append("")
    return "\n".join(lines)


def try_int_from_extras(extras: dict[str, str], keys: tuple[str, ...]) -> int | None:
    for k in keys:
        for ek, ev in extras.items():
            if ek.lower() == k.lower():
                try:
                    return int(ev.strip())
                except ValueError:
                    return None
    return None


def _strip_extras_matching_incoming(extras: dict[str, str], allowed_lower: set[str]) -> None:
    for k in list(extras.keys()):
        if k.lower() in allowed_lower:
            del extras[k]


def commit_router_ini_file(temp_path: str, destination: str, payload: str, *, log_event: Callable[..., None] | None = None) -> None:
    try:
        os.replace(temp_path, destination)
        return
    except OSError as replace_err:
        with open(destination, "w", encoding="utf-8") as handle:
            handle.write(payload)
            handle.flush()
            os.fsync(handle.fileno())
        if log_event is not None:
            log_event(
                "router_ini_commit_fallback",
                reason="os_replace_failed",
                destination=destination,
                replaceError=str(replace_err),
            )
        if os.path.exists(temp_path):
            try:
                os.remove(temp_path)
            except OSError:
                pass


def read_router_entries() -> dict[str, RouterSection]:
    with ROUTER_FILE_LOCK:
        if not os.path.exists(ROUTER_CONFIG_PATH):
            return {}
        with open(ROUTER_CONFIG_PATH, "r", encoding="utf-8") as handle:
            content = handle.read()
        return parse_router_ini(content)


def upsert_router_entry(
    alias: str,
    model_path: str,
    mmproj_path: str,
    *,
    preset: dict[str, str] | None = None,
    preset_mode: str = "replace",
    context_size: int | None = None,
    cache_ram_mib: int | None = None,
    update_context: bool = False,
    update_cache: bool = False,
    trigger_reload: bool = True,
    reload_callback: Callable[[], RuntimeApplyResult] | None = None,
    log_event: Callable[..., None] | None = None,
) -> tuple[str, RuntimeApplyResult | None]:
    alias_trimmed = normalize_alias(alias)
    if not model_path.strip():
        raise ValueError("modelPath is required.")
    _ctx_l = {x.lower() for x in _CTX_KEYS}
    _cache_l = {x.lower() for x in _CACHE_KEYS}

    incoming_preset = normalize_preset_map(preset or {})
    incoming_preset = apply_alias_vision_token_preset(alias_trimmed, incoming_preset)
    if update_context:
        _strip_extras_matching_incoming(incoming_preset, _ctx_l)
        if context_size is not None:
            incoming_preset["ctx-size"] = str(int(context_size))
    if update_cache:
        _strip_extras_matching_incoming(incoming_preset, _cache_l)
        if cache_ram_mib is not None:
            incoming_preset["cache-ram"] = str(int(cache_ram_mib))

    with ROUTER_FILE_LOCK:
        entries: dict[str, RouterSection] = {}
        if os.path.exists(ROUTER_CONFIG_PATH):
            with open(ROUTER_CONFIG_PATH, "r", encoding="utf-8") as handle:
                entries = parse_router_ini(handle.read())

        payload_before = serialize_router_ini(entries)
        prior = entries.get(alias_trimmed)
        extras: dict[str, str] = dict(prior.extras) if prior else {}
        if incoming_preset or preset is not None or update_context or update_cache:
            extras = apply_preset_mode(extras, incoming_preset, preset_mode)

        if update_context and context_size is None:
            _strip_extras_matching_incoming(extras, _ctx_l)
        if update_cache and cache_ram_mib is None:
            _strip_extras_matching_incoming(extras, _cache_l)

        entries[alias_trimmed] = RouterSection(
            model=model_path.strip(),
            mmproj=mmproj_path.strip(),
            extras=extras,
        )

        directory = os.path.dirname(ROUTER_CONFIG_PATH)
        if directory:
            os.makedirs(directory, exist_ok=True)
        payload = serialize_router_ini(entries)
        changed = payload != payload_before
        ini_sha256 = hashlib.sha256(payload.encode("utf-8")).hexdigest()

        temp_fd, temp_path = tempfile.mkstemp(
            dir=directory if directory else None,
            prefix="router-models-",
            suffix=".ini.tmp",
        )
        try:
            with os.fdopen(temp_fd, "w", encoding="utf-8") as handle:
                handle.write(payload)
                handle.flush()
                os.fsync(handle.fileno())
            commit_router_ini_file(temp_path, ROUTER_CONFIG_PATH, payload, log_event=log_event)
        finally:
            if os.path.exists(temp_path):
                try:
                    os.remove(temp_path)
                except OSError:
                    pass

    if log_event is not None:
        log_event(
            "router_entry_upsert_applied",
            alias=alias_trimmed,
            iniChanged=changed,
            iniSha256=ini_sha256,
        )

    runtime_apply: RuntimeApplyResult | None = None
    if trigger_reload and changed and reload_callback is not None:
        runtime_apply = reload_callback()
        if log_event is not None:
            log_event(
                "router_entry_upsert_reload_triggered",
                alias=alias_trimmed,
                iniChanged=changed,
                runtimeApplied=runtime_apply.applied,
            )
    return ini_sha256, runtime_apply
