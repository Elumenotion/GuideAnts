import json
import hashlib
import os
import re
import shutil
import signal
import tempfile
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid
from dataclasses import dataclass, field
from datetime import datetime, timezone
from typing import Any, Callable

from guideants_hf.exact_download import (
    ExactDownloadError,
    activate_staged_files,
    artifact_is_installed,
    build_artifact_specs,
    build_immutable_input,
    stage_download_file,
    staged_artifact_path,
)
from guideants_hf.path_safety import PathSafetyError, delete_obsolete_repository_paths
from guideants_hf.operation_journal import OperationJournalError, OperationJournalStore
from guideants_hf.preset_validation import (
    PresetValidationError,
    apply_preset_mode,
    normalize_alias,
    normalize_preset_map,
)
from guideants_hf.router_mmproj import preset_disables_mmproj
from guideants_hf.vision_token_preset import apply_alias_vision_token_preset, strip_vision_token_extras
from guideants_hf.transport import (
    build_regex_from_include_pattern,
    download_hf_file,
    list_hf_repository_files,
)

import uvicorn
from fastapi import FastAPI, HTTPException, Request, Response
from pydantic import BaseModel

import llama_router_ini as router_ini
from fleet_projection import (
    confirm_fleet_restart,
    get_fleet_preset_response,
    put_fleet_preset,
)
from guideants_hf.repository import HuggingFaceAccessError
from llama_catalog import (
    CatalogDefinitionError,
    build_catalog_response,
    resolve_definition_quants,
)


def utc_now_iso() -> str:
    return datetime.now(timezone.utc).isoformat()


def log_event(event: str, **fields: Any) -> None:
    payload = {"event": event, "ts": utc_now_iso()}
    payload.update(fields)
    print(json.dumps(payload, ensure_ascii=True, sort_keys=True), flush=True)


def env_flag(name: str, default: bool = False) -> bool:
    raw = os.getenv(name)
    if raw is None:
        return default
    return raw.strip().lower() in {"1", "true", "yes", "on"}


def parse_int(value: str | None, default: int) -> int:
    if value is None:
        return default
    try:
        parsed = int(value)
        if parsed > 0:
            return parsed
    except ValueError:
        pass
    return default


def resolve_model_store_root() -> str:
    # Must match the GA_LLAMA_MODEL_DIR set in docker-compose.yml for the
    # guideants-ai service (/models-local/llama). The fallback here is
    # only for bare-metal dev runs where compose isn't setting it.
    root = (os.getenv("GA_LLAMA_MODEL_DIR") or "/models-local/llama").strip()
    if not root:
        root = "/models-local/llama"
    return os.path.abspath(root)


def resolve_router_config_path() -> str:
    explicit = (os.getenv("GA_LLAMA_ROUTER_CONFIG_PATH") or "").strip()
    if explicit:
        return os.path.abspath(explicit)
    preset = (os.getenv("GA_LLAMA_MODELS_PRESET") or "").strip()
    if preset:
        return os.path.abspath(preset)
    return "/models-local/router-models.ini"


MODEL_STORE_ROOT = resolve_model_store_root()
ROUTER_CONFIG_PATH = resolve_router_config_path()
router_ini.ROUTER_CONFIG_PATH = ROUTER_CONFIG_PATH
OPERATION_JOURNAL_ROOT = os.path.join(MODEL_STORE_ROOT, ".llama-operations")
STAGING_ROOT = os.path.join(MODEL_STORE_ROOT, ".staging")
OPERATION_JOURNAL = OperationJournalStore(OPERATION_JOURNAL_ROOT)


def ensure_inside_root(root_abs: str, candidate_abs: str) -> None:
    root_norm = os.path.normcase(os.path.abspath(root_abs))
    candidate_norm = os.path.normcase(os.path.abspath(candidate_abs))
    common = os.path.commonpath([root_norm, candidate_norm])
    if common != root_norm:
        raise ValueError("Target directory escapes the model store root.")


def resolve_local_path(container_or_relative_path: str) -> str:
    raw = container_or_relative_path.strip()
    if not raw:
        return ""
    normalized = raw.replace("\\", "/")
    # Any absolute container path (including /models-local/llama/...,
    # the new unified volume layout) is passed through unchanged; the
    # caller is asserting this path is already container-rooted.
    if os.path.isabs(normalized):
        return os.path.normpath(normalized)
    # Relative paths are resolved against the configured model store
    # root — /models-local/llama in the shipped compose.
    relative = normalized.lstrip("/")
    return os.path.normpath(os.path.join(MODEL_STORE_ROOT, relative))


def has_artifact(path_value: str | None) -> bool:
    if not path_value or not path_value.strip():
        return False
    local_path = resolve_local_path(path_value)
    return os.path.exists(local_path)


@dataclass
class _RouterSection:
    model: str
    mmproj: str
    extras: dict[str, str]


def parse_router_ini(text: str) -> dict[str, _RouterSection]:
    entries: dict[str, _RouterSection] = {}
    current_alias: str | None = None
    model_path: str = ""
    mmproj_path: str = ""
    extras: dict[str, str] = {}

    def flush_current() -> None:
        nonlocal current_alias, model_path, mmproj_path, extras
        if current_alias and (model_path or mmproj_path or extras):
            entries[current_alias] = _RouterSection(
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


def serialize_router_ini(entries: dict[str, _RouterSection]) -> str:
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


_CTX_KEYS = ("ctx-size", "c", "ctx_size", "LLAMA_ARG_CTX_SIZE")
_CACHE_KEYS = ("cache-ram", "cache_ram", "LLAMA_ARG_CACHE_RAM")


def _try_int_from_extras(extras: dict[str, str], keys: tuple[str, ...]) -> int | None:
    for k in keys:
        for ek, ev in extras.items():
            if ek.lower() == k.lower():
                try:
                    return int(ev.strip())
                except ValueError:
                    return None
    return None


def _pydantic_set_fields(m: BaseModel) -> set[str]:
    # Pydantic v2: model_fields_set; v1: __fields_set__
    fs = getattr(m, "model_fields_set", None) or getattr(m, "__fields_set__", None)
    return set(fs) if fs is not None else set()


def _section_summary(section: _RouterSection | None) -> dict[str, Any]:
    if section is None:
        return {
            "exists": False,
            "modelPath": None,
            "mmprojPath": None,
            "contextSize": None,
            "cacheRamMib": None,
            "extras": {},
        }
    return {
        "exists": True,
        "modelPath": section.model,
        "mmprojPath": section.mmproj,
        "contextSize": _try_int_from_extras(section.extras, _CTX_KEYS),
        "cacheRamMib": _try_int_from_extras(section.extras, _CACHE_KEYS),
        "extras": dict(section.extras),
    }


ROUTER_FILE_LOCK = threading.Lock()
LLAMA_PID_FILE = "/run/llama-server.pid"
# entrypoint.sh writes this on every detected llama-server exit (normal or crash); see
# docker/build/guideants-ai/entrypoint.sh. The record shape is:
#   {"pid": int, "exitedAt": iso, "exitCode": int|null, "signal": int|null,
#    "reason": "oom"|"crashed"|"signaled_reload"|"normal", "tail": [str,...]}
LLAMA_LAST_EXIT_FILE = "/run/llama-server.last-exit.json"
LLAMA_SERVER_HOST = (os.getenv("GA_LLAMA_HOST") or "127.0.0.1").strip() or "127.0.0.1"
LLAMA_SERVER_PORT = parse_int(os.getenv("GA_LLAMA_PORT"), 8080)
LLAMA_SERVER_BASE_URL = f"http://{LLAMA_SERVER_HOST}:{LLAMA_SERVER_PORT}"

# Upper bound on how long we'll wait for entrypoint.sh to respawn llama-server and /models to
# answer after an operator-initiated restart. 30s matches the chat UI's restart spinner timeout.
LLAMA_RESTART_TIMEOUT_SECONDS = parse_int(os.getenv("GA_LLAMA_ADMIN_RESTART_TIMEOUT_SECONDS"), 30)


def _llama_http_get_json(path: str, timeout: float) -> Any:
    url = f"{LLAMA_SERVER_BASE_URL}{path}"
    req = urllib.request.Request(url, method="GET", headers={"Accept": "application/json"})
    with urllib.request.urlopen(req, timeout=timeout) as resp:
        return json.loads(resp.read().decode("utf-8"))


def _llama_http_post_json(path: str, payload: dict[str, Any], timeout: float) -> tuple[int, str]:
    url = f"{LLAMA_SERVER_BASE_URL}{path}"
    body = json.dumps(payload).encode("utf-8")
    req = urllib.request.Request(
        url,
        data=body,
        method="POST",
        headers={"Content-Type": "application/json", "Accept": "application/json"},
    )
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            return resp.status, resp.read().decode("utf-8", errors="replace")
    except urllib.error.HTTPError as exc:
        return exc.code, exc.read().decode("utf-8", errors="replace")


def _list_loaded_aliases() -> list[str]:
    """Snapshot aliases whose status.value is 'loaded' so we can restore them after a router restart."""
    try:
        data = _llama_http_get_json("/models", timeout=2.0)
    except Exception as exc:  # noqa: BLE001 - best-effort snapshot; bail to no-op on any error
        log_event("llama_server_list_loaded_failed", reason=str(exc))
        return []
    if not isinstance(data, dict):
        return []
    entries = data.get("data") or []
    loaded: list[str] = []
    for entry in entries:
        if not isinstance(entry, dict):
            continue
        alias = entry.get("id")
        status = entry.get("status") or {}
        state = status.get("value") if isinstance(status, dict) else None
        if isinstance(alias, str) and state == "loaded":
            loaded.append(alias)
    return loaded


def _read_llama_server_pid() -> int | None:
    try:
        with open(LLAMA_PID_FILE, "r", encoding="utf-8") as handle:
            raw = handle.read().strip()
        return int(raw) if raw else None
    except (FileNotFoundError, ValueError, OSError):
        return None


def _wait_for_llama_server_restart(
    old_pid: int | None,
    boot_timeout_s: float,
) -> bool:
    """Wait for entrypoint.sh to respawn the router (new PID in pid file) and /models to respond."""
    deadline = time.monotonic() + boot_timeout_s
    while time.monotonic() < deadline:
        current = _read_llama_server_pid()
        if current is not None and current != old_pid:
            break
        time.sleep(0.25)
    else:
        return False
    while time.monotonic() < deadline:
        try:
            _llama_http_get_json("/models", timeout=1.5)
            return True
        except Exception:  # noqa: BLE001 - poll until alive or deadline
            time.sleep(0.25)
    return False


def _restore_loaded_aliases_async(
    old_pid: int | None,
    aliases: list[str],
    boot_timeout_s: float = 60.0,
    load_timeout_s: float = 600.0,
) -> None:
    if not aliases:
        return
    ready = _wait_for_llama_server_restart(old_pid, boot_timeout_s)
    if not ready:
        log_event(
            "llama_server_reload_wait_timeout",
            aliases=aliases,
            bootTimeoutSec=boot_timeout_s,
        )
        return
    for alias in aliases:
        try:
            code, body = _llama_http_post_json(
                "/models/load", {"model": alias}, timeout=load_timeout_s
            )
            log_event(
                "llama_server_reload_restore_load",
                alias=alias,
                statusCode=code,
                ok=200 <= code < 300,
                body=body[:200] if isinstance(body, str) else None,
            )
        except Exception as exc:  # noqa: BLE001 - log and continue
            log_event("llama_server_reload_restore_load_failed", alias=alias, reason=str(exc))


def signal_llama_server_reload(*, preserve_loaded: bool = True) -> None:
    """
    llama-server parses --models-preset only at startup; new aliases added to
    router-models.ini are invisible until the server is restarted. Send SIGTERM
    to the llama-server process; entrypoint.sh respawns it automatically so the
    new alias becomes live. When ``preserve_loaded`` is True (default) the set
    of aliases currently in status ``loaded`` is captured before SIGTERM and
    re-loaded in a background thread once the router answers on the new PID.
    """
    loaded_aliases = _list_loaded_aliases() if preserve_loaded else []
    try:
        if not os.path.exists(LLAMA_PID_FILE):
            return
        with open(LLAMA_PID_FILE, "r", encoding="utf-8") as handle:
            pid_raw = handle.read().strip()
        if not pid_raw:
            return
        pid = int(pid_raw)
        os.kill(pid, signal.SIGTERM)
        log_event(
            "llama_server_reload_signal",
            pid=pid,
            preservedAliases=loaded_aliases,
        )
    except (FileNotFoundError, ValueError, ProcessLookupError) as exc:
        log_event("llama_server_reload_skip", reason=str(exc))
        return
    except PermissionError as exc:
        log_event("llama_server_reload_permission_error", reason=str(exc))
        return

    if loaded_aliases:
        threading.Thread(
            target=_restore_loaded_aliases_async,
            args=(pid, loaded_aliases),
            daemon=True,
            name="llama-reload-restore",
        ).start()


def _commit_router_ini_file(temp_path: str, destination: str, payload: str) -> None:
    """
    Promote a temp router ini to the live path.

    ``os.replace`` is preferred (atomic same-filesystem replace). Some setups
    (e.g. Docker Desktop bind mounts from the host) can make replace fail with
    ``OSError`` (e.g. errno 16 EBUSY). In that case we fall back to rewriting
    the destination in place with fsync. Production compose uses a path on the
    named model volume to avoid host bind issues.
    """
    try:
        os.replace(temp_path, destination)
        return
    except OSError as replace_err:
        try:
            with open(destination, "w", encoding="utf-8") as handle:
                handle.write(payload)
                handle.flush()
                os.fsync(handle.fileno())
        except OSError as write_err:
            if os.path.exists(temp_path):
                try:
                    os.remove(temp_path)
                except OSError:
                    pass
            raise write_err from replace_err
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


def read_router_entries() -> dict[str, _RouterSection]:
    with ROUTER_FILE_LOCK:
        if not os.path.exists(ROUTER_CONFIG_PATH):
            return {}
        with open(ROUTER_CONFIG_PATH, "r", encoding="utf-8") as handle:
            content = handle.read()
        return parse_router_ini(content)


def remove_router_entry(alias: str) -> tuple[str, str] | None:
    """Remove a router alias from the ini file. Returns (modelPath, mmprojPath) if removed."""
    alias_trimmed = alias.strip()
    if not alias_trimmed:
        raise ValueError("Router alias is required.")

    with ROUTER_FILE_LOCK:
        entries: dict[str, tuple[str, str]] = {}
        if os.path.exists(ROUTER_CONFIG_PATH):
            with open(ROUTER_CONFIG_PATH, "r", encoding="utf-8") as handle:
                entries = parse_router_ini(handle.read())

        if alias_trimmed not in entries:
            return None

        removed_sec = entries.pop(alias_trimmed)
        removed = (removed_sec.model, removed_sec.mmproj)

        router_ini.write_router_config_text(serialize_router_ini(entries))

    if removed:
        signal_llama_server_reload()
    return removed


def delete_registered_artifacts(model_path: str, mmproj_path: str) -> None:
    """
    Delete on-disk files for a removed router entry. Paths are resolved under
    MODEL_STORE_ROOT; anything outside the store is skipped (logged).
    When both artifacts share a single parent directory, the whole directory is removed.
    """
    dirs: set[str] = set()
    file_paths: list[str] = []

    for raw in (model_path, mmproj_path):
        if not raw or not str(raw).strip():
            continue
        local = resolve_local_path(str(raw).strip())
        if not local:
            continue
        try:
            ensure_inside_root(MODEL_STORE_ROOT, local)
        except ValueError as exc:
            log_event("artifact_delete_skip_outside_root", path=raw, error=str(exc))
            continue

        if os.path.isfile(local):
            file_paths.append(local)
            dirs.add(os.path.dirname(local))
        elif os.path.isdir(local):
            shutil.rmtree(local)
            log_event("artifact_delete_rmtree", path=local)
            return

    if len(dirs) == 1:
        tree = dirs.pop()
        try:
            ensure_inside_root(MODEL_STORE_ROOT, tree)
        except ValueError as exc:
            log_event("artifact_delete_skip_dir", path=tree, error=str(exc))
            return
        root_abs = os.path.normcase(os.path.abspath(MODEL_STORE_ROOT))
        tree_abs = os.path.normcase(os.path.abspath(tree))
        if tree_abs == root_abs:
            # Never remove the entire model store root; delete files only.
            for fp in file_paths:
                if os.path.isfile(fp):
                    os.remove(fp)
                    log_event("artifact_delete_file", path=fp)
            return
        if os.path.isdir(tree):
            shutil.rmtree(tree)
            log_event("artifact_delete_rmtree", path=tree)
        return

    for fp in file_paths:
        if os.path.isfile(fp):
            os.remove(fp)
            log_event("artifact_delete_file", path=fp)


@dataclass
class RuntimeApplyResult:
    applied: bool
    ini_sha256: str
    remediation: str | None = None


def signal_llama_server_reload_with_result(*, preserve_loaded: bool = True) -> RuntimeApplyResult:
    ini_sha256 = ""
    if os.path.exists(ROUTER_CONFIG_PATH):
        with open(ROUTER_CONFIG_PATH, "rb") as handle:
            ini_sha256 = hashlib.sha256(handle.read()).hexdigest()
    try:
        signal_llama_server_reload(preserve_loaded=preserve_loaded)
        return RuntimeApplyResult(applied=True, ini_sha256=ini_sha256)
    except Exception as exc:  # noqa: BLE001 - surface reload failure without rewriting preset
        return RuntimeApplyResult(
            applied=False,
            ini_sha256=ini_sha256,
            remediation=f"INI committed but llama-server reload failed: {exc}",
        )


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
) -> tuple[str, RuntimeApplyResult | None]:
    """Write model/mmproj and preset extras atomically. Returns (ini_sha256, runtime_apply)."""
    alias_trimmed = normalize_alias(alias)
    if not model_path.strip():
        raise ValueError("modelPath is required.")
    _ctx_l = {x.lower() for x in _CTX_KEYS}
    _cache_l = {x.lower() for x in _CACHE_KEYS}

    incoming_preset = normalize_preset_map(preset or {})
    # Only normalize vision-token defaults when the caller explicitly supplied a
    # preset payload. Model-path/context sync calls intentionally omit preset and
    # must preserve existing extras exactly as-is.
    if preset is not None and not preset_disables_mmproj(incoming_preset):
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
        entries: dict[str, _RouterSection] = {}
        if os.path.exists(ROUTER_CONFIG_PATH):
            with open(ROUTER_CONFIG_PATH, "r", encoding="utf-8") as handle:
                entries = parse_router_ini(handle.read())

        payload_before = serialize_router_ini(entries)
        prior = entries.get(alias_trimmed)
        extras: dict[str, str] = dict(prior.extras) if prior else {}
        # Context/cache-only updates are patch operations over existing extras.
        # Force merge semantics when no explicit preset was sent so we don't
        # accidentally replace unrelated alias keys (for example no-mmproj/spec-*).
        effective_preset_mode = (
            "merge"
            if preset is None and (update_context or update_cache)
            else preset_mode
        )

        if preset is not None or incoming_preset:
            extras = apply_preset_mode(extras, incoming_preset, effective_preset_mode)

        if update_context and context_size is None:
            _strip_extras_matching_incoming(extras, _ctx_l)
        if update_cache and cache_ram_mib is None:
            _strip_extras_matching_incoming(extras, _cache_l)
        if preset_disables_mmproj(extras):
            strip_vision_token_extras(extras)

        entries[alias_trimmed] = _RouterSection(
            model=model_path.strip(),
            mmproj=mmproj_path.strip(),
            extras=extras,
        )

        payload = serialize_router_ini(entries)
        changed = payload != payload_before
        ini_sha256 = hashlib.sha256(payload.encode("utf-8")).hexdigest()
        router_ini.write_router_config_text(payload)

    before_summary = _section_summary(prior)
    after_summary = _section_summary(entries.get(alias_trimmed))
    log_event(
        "router_entry_upsert_applied",
        alias=alias_trimmed,
        request={
            "presetMode": preset_mode,
            "presetKeys": sorted(incoming_preset.keys()),
            "updateContext": update_context,
            "updateCache": update_cache,
            "contextSize": context_size,
            "cacheRamMib": cache_ram_mib,
            "modelPath": model_path.strip(),
            "mmprojPath": mmproj_path.strip(),
        },
        before=before_summary,
        after=after_summary,
        iniChanged=changed,
        iniSha256=ini_sha256,
    )

    runtime_apply: RuntimeApplyResult | None = None
    if trigger_reload and changed:
        runtime_apply = signal_llama_server_reload_with_result()
        log_event(
            "router_entry_upsert_reload_triggered",
            alias=alias_trimmed,
            iniChanged=changed,
            runtimeApplied=runtime_apply.applied,
        )
    return ini_sha256, runtime_apply


def _strip_extras_matching_incoming(extras: dict[str, str], allowed_lower: set[str]) -> None:
    for k in list(extras.keys()):
        if k.lower() in allowed_lower:
            del extras[k]


class RouterEntryUpsertRequest(BaseModel):
    class Config:
        extra = "forbid"
    alias: str
    modelPath: str
    mmprojPath: str = ""
    preset: dict[str, str] | None = None
    presetMode: str = "replace"
    contextSize: int | None = None
    cacheRamMib: int | None = None


class RouterEntryDto(BaseModel):
    alias: str
    modelPath: str
    mmprojPath: str
    hasModelFile: bool
    hasMmprojFile: bool
    contextSize: int | None = None
    cacheRamMib: int | None = None
    preset: dict[str, str]


class RouterEntriesResponse(BaseModel):
    entries: list[RouterEntryDto]


class DeleteObsoleteArtifactsRequest(BaseModel):
    class Config:
        extra = "forbid"
    targetDirectory: str
    repositoryPaths: list[str]


class ArtifactMetadataDto(BaseModel):
    path: str
    size: int | None = None
    digest: str | None = None
    etag: str | None = None


class ExactDownloadRequest(BaseModel):
    operationId: str
    repository: str
    resolvedRevision: str
    modelFiles: list[str]
    mmprojFiles: list[str] = []
    companionFiles: list[str] = []
    alias: str
    targetDirectory: str
    preset: dict[str, str]
    presetMode: str = "replace"
    artifactMetadata: list[ArtifactMetadataDto] | None = None
    hfToken: str | None = None


class StartDownloadRequest(BaseModel):
    """
    Request body forwarded by the GuideAntsApi web layer.

    ``hfToken`` is the single, server-resolved Hugging Face token from the
    top-level ``HuggingFace:Token`` application setting. The web API
    overwrites whatever the client sent so this admin service only ever
    receives the one configured value. There is no per-request override at
    this layer.
    """

    repository: str
    quantIncludePattern: str
    mmprojIncludePattern: str | None = None
    routerModelId: str
    targetDirectory: str
    hfToken: str | None = None
    allowOverwrite: bool = False


@dataclass
class DownloadOperationState:
    operation_id: str
    router_model_id: str
    status: str = "queued"
    progress: float | None = 0.0
    error_message: str | None = None
    log_line: str | None = None
    created_at: str = field(default_factory=utc_now_iso)
    completed_at: str | None = None

    def to_dto(self) -> dict[str, Any]:
        return {
            "operationId": self.operation_id,
            "status": self.status,
            "routerModelId": self.router_model_id,
            "progress": self.progress,
            "errorMessage": self.error_message,
            "logLine": self.log_line,
        }


def _journal_record_to_download_dto(record) -> dict[str, Any]:
    return record.to_dto()


OPERATIONS_LOCK = threading.Lock()
OPERATIONS: dict[str, DownloadOperationState] = {}
DOWNLOAD_WORKERS: dict[str, threading.Thread] = {}
ALIAS_LOCKS_GUARD = threading.Lock()
ALIAS_LOCKS: dict[str, threading.Lock] = {}


def _download_worker_active(operation_id: str) -> bool:
    worker = DOWNLOAD_WORKERS.get(operation_id)
    return worker is not None and worker.is_alive()


def _start_download_worker(operation_id: str, target: Any, args: tuple[Any, ...]) -> None:
    worker = threading.Thread(target=target, args=args, daemon=True)
    DOWNLOAD_WORKERS[operation_id] = worker
    worker.start()


def get_alias_lock(alias: str) -> threading.Lock:
    with ALIAS_LOCKS_GUARD:
        lock = ALIAS_LOCKS.get(alias)
        if lock is None:
            lock = threading.Lock()
            ALIAS_LOCKS[alias] = lock
        return lock


def update_operation(operation_id: str, **fields: Any) -> None:
    with OPERATIONS_LOCK:
        state = OPERATIONS.get(operation_id)
        if state is None:
            return
        for key, value in fields.items():
            setattr(state, key, value)


def fail_operation(operation_id: str, message: str) -> None:
    update_operation(
        operation_id,
        status="failed",
        error_message=message,
        log_line=message,
        completed_at=utc_now_iso(),
    )


def run_download_operation(operation_id: str, request: StartDownloadRequest) -> None:
    alias = request.routerModelId.strip()
    alias_lock = get_alias_lock(alias)
    alias_lock.acquire()
    try:
        update_operation(operation_id, status="resolvingFiles", log_line="Resolving files in Hugging Face repository.")

        # Single source: whatever the web API stamped in. No env fallback
        # here — if a token is needed and none is configured, the gated-repo
        # call will 401/403 and the failure is surfaced to the operator.
        token = (request.hfToken or "").strip() or None
        files = list_hf_repository_files(request.repository, token)

        quant_regex = build_regex_from_include_pattern(request.quantIncludePattern.strip())
        mmproj_pattern = (request.mmprojIncludePattern or "").strip()
        mmproj_regex = build_regex_from_include_pattern(mmproj_pattern) if mmproj_pattern else None

        quant_candidates = [
            f
            for f in files
            if f.get("type") == "file"
            and isinstance(f.get("path"), str)
            and f["path"].lower().endswith(".gguf")
            and quant_regex.match(f["path"]) is not None
        ]
        quant_candidates.sort(key=lambda f: f.get("size") or 0, reverse=True)

        mmproj_candidates: list[dict[str, Any]] = []
        if mmproj_regex is not None:
            mmproj_candidates = [
                f
                for f in files
                if f.get("type") == "file"
                and isinstance(f.get("path"), str)
                and "mmproj" in f["path"].lower()
                and mmproj_regex.match(f["path"]) is not None
            ]
            mmproj_candidates.sort(key=lambda f: f.get("size") or 0, reverse=True)

        if not quant_candidates:
            fail_operation(operation_id, "No GGUF file matched the quant include pattern.")
            return
        if mmproj_regex is not None and not mmproj_candidates:
            fail_operation(operation_id, "No mmproj file matched the mmproj include pattern.")
            return

        quant_file = quant_candidates[0]
        mmproj_file = mmproj_candidates[0] if mmproj_candidates else None
        quant_path = str(quant_file["path"])
        mmproj_path = str(mmproj_file["path"]) if mmproj_file is not None else None

        target_subdir = request.targetDirectory.strip().strip("/\\")
        if not target_subdir:
            fail_operation(operation_id, "Target directory is required.")
            return

        target_dir = os.path.abspath(os.path.join(MODEL_STORE_ROOT, target_subdir))
        try:
            ensure_inside_root(MODEL_STORE_ROOT, target_dir)
        except ValueError as exc:
            fail_operation(operation_id, str(exc))
            return
        os.makedirs(target_dir, exist_ok=True)

        quant_name = os.path.basename(quant_path.replace("\\", "/"))
        mmproj_name = os.path.basename(mmproj_path.replace("\\", "/")) if mmproj_path else None
        quant_dest = os.path.join(target_dir, quant_name)
        mmproj_dest = os.path.join(target_dir, mmproj_name) if mmproj_name else None

        has_existing_quant = os.path.exists(quant_dest)
        has_existing_mmproj = bool(mmproj_dest) and os.path.exists(mmproj_dest)
        if not request.allowOverwrite and (has_existing_quant or has_existing_mmproj):
            fail_operation(operation_id, "Target file(s) already exist. Enable AllowOverwrite or remove existing files.")
            return

        quant_size = quant_file.get("size")
        mmproj_size = mmproj_file.get("size") if mmproj_file is not None else None

        quant_tmp = quant_dest + ".tmp"
        quant_existing = os.path.getsize(quant_tmp) if os.path.exists(quant_tmp) else 0
        if quant_existing > 0 and isinstance(quant_size, int) and quant_size > 0:
            resume_pct = round(100 * quant_existing / quant_size)
            initial_progress = 0.05 + 0.45 * (quant_existing / float(quant_size))
            update_operation(operation_id, status="downloading", progress=min(initial_progress, 0.5),
                             log_line=f"Resuming {quant_path} ({resume_pct}% already downloaded)")
        else:
            update_operation(operation_id, status="downloading", progress=0.05, log_line=f"Downloading {quant_path}")

        def report_quant(bytes_read: int) -> None:
            if isinstance(quant_size, int) and quant_size > 0:
                progress = 0.05 + 0.45 * (bytes_read / float(quant_size))
            else:
                progress = 0.05
            update_operation(operation_id, progress=min(progress, 0.5))

        download_hf_file(
            request.repository.strip(),
            quant_path,
            quant_dest,
            token,
            progress_callback=report_quant,
        )
        if mmproj_path and mmproj_dest:
            def report_mmproj(bytes_read: int) -> None:
                if isinstance(mmproj_size, int) and mmproj_size > 0:
                    progress = 0.50 + 0.45 * (bytes_read / float(mmproj_size))
                else:
                    progress = 0.50
                update_operation(operation_id, progress=min(progress, 0.95))

            mmproj_tmp = mmproj_dest + ".tmp"
            mmproj_existing = os.path.getsize(mmproj_tmp) if os.path.exists(mmproj_tmp) else 0
            if mmproj_existing > 0 and isinstance(mmproj_size, int) and mmproj_size > 0:
                resume_pct = round(100 * mmproj_existing / mmproj_size)
                initial_progress = 0.50 + 0.45 * (mmproj_existing / float(mmproj_size))
                update_operation(operation_id, progress=min(initial_progress, 0.95),
                                 log_line=f"Resuming {mmproj_path} ({resume_pct}% already downloaded)")
            else:
                update_operation(operation_id, log_line=f"Downloading {mmproj_path}")
            download_hf_file(
                request.repository.strip(),
                mmproj_path,
                mmproj_dest,
                token,
                progress_callback=report_mmproj,
            )
        else:
            update_operation(
                operation_id,
                progress=0.95,
                log_line="No mmproj pattern provided; skipping vision projector download."
            )

        update_operation(operation_id, status="registeringAlias", progress=0.92, log_line="Registering router alias.")
        # Paths written into router-models.ini are container paths that
        # llama-server will open at runtime. Must match the
        # GA_LLAMA_MODEL_DIR mount in docker-compose.yml
        # (/models-local/llama).
        model_container_path = f"/models-local/llama/{target_subdir}/{quant_name}"
        mmproj_container_path = (
            f"/models-local/llama/{target_subdir}/{mmproj_name}"
            if mmproj_name
            else ""
        )
        upsert_router_entry(alias, model_container_path, mmproj_container_path)

        update_operation(
            operation_id,
            status="completed",
            progress=1.0,
            log_line="Completed.",
            completed_at=utc_now_iso(),
        )
    except Exception as exc:
        log_event("llama_admin_download_failed", operationId=operation_id, errorType=type(exc).__name__, error=str(exc))
        fail_operation(operation_id, str(exc))
    finally:
        alias_lock.release()


def _immutable_input_download_identity(immutable_input: dict[str, Any]) -> dict[str, Any]:
    return {
        "repository": str(immutable_input.get("repository") or "").strip(),
        "resolvedRevision": str(immutable_input.get("resolvedRevision") or "").strip(),
        "modelFiles": list(immutable_input.get("modelFiles") or []),
        "mmprojFiles": list(immutable_input.get("mmprojFiles") or []),
        "companionFiles": list(immutable_input.get("companionFiles") or []),
        "routerModelId": normalize_alias(str(immutable_input.get("routerModelId") or immutable_input.get("alias") or "")),
        "targetDirectory": str(immutable_input.get("targetDirectory") or "").strip().strip("/\\"),
        "presetMode": str(immutable_input.get("presetMode") or "replace"),
        "routerPreset": dict(immutable_input.get("routerPreset") or immutable_input.get("preset") or {}),
    }


def _exact_download_request_identity(request: ExactDownloadRequest) -> dict[str, Any]:
    return _immutable_input_download_identity(
        {
            "repository": request.repository,
            "resolvedRevision": request.resolvedRevision,
            "modelFiles": request.modelFiles,
            "mmprojFiles": request.mmprojFiles,
            "companionFiles": request.companionFiles,
            "routerModelId": request.alias,
            "targetDirectory": request.targetDirectory,
            "presetMode": request.presetMode,
            "routerPreset": request.preset,
        }
    )


def _journal_has_download_step(record, repository_path: str) -> bool:
    return any(
        step.step == "downloadModelFile" and step.path == repository_path
        for step in record.journal
    )


def _ensure_journal_download_step(operation_id: str, repository_path: str) -> None:
    record = OPERATION_JOURNAL.get(operation_id)
    if record is None or _journal_has_download_step(record, repository_path):
        return
    OPERATION_JOURNAL.append_step(operation_id, "downloadModelFile", repository_path)


def _exact_download_request_from_journal(journal_record) -> ExactDownloadRequest:
    immutable_input = journal_record.immutable_input
    artifact_metadata: list[ArtifactMetadataDto] | None = None
    raw_metadata = immutable_input.get("artifactMetadata")
    if isinstance(raw_metadata, list):
        artifact_metadata = [
            ArtifactMetadataDto.model_validate(item)
            for item in raw_metadata
            if isinstance(item, dict)
        ]
    return ExactDownloadRequest(
        operationId=journal_record.operation_id,
        repository=str(immutable_input.get("repository") or ""),
        resolvedRevision=str(immutable_input.get("resolvedRevision") or ""),
        modelFiles=[str(path) for path in (immutable_input.get("modelFiles") or [])],
        mmprojFiles=[str(path) for path in (immutable_input.get("mmprojFiles") or [])],
        companionFiles=[str(path) for path in (immutable_input.get("companionFiles") or [])],
        alias=normalize_alias(
            str(immutable_input.get("routerModelId") or immutable_input.get("alias") or journal_record.alias)
        ),
        targetDirectory=str(immutable_input.get("targetDirectory") or ""),
        preset=dict(immutable_input.get("routerPreset") or immutable_input.get("preset") or {}),
        presetMode=str(immutable_input.get("presetMode") or "replace"),
        artifactMetadata=artifact_metadata,
        hfToken=None,
    )


def _exact_download_staging_dir(target_directory: str) -> str:
    target_subdir = target_directory.strip().strip("/\\")
    if not target_subdir:
        raise ValueError("targetDirectory is required.")
    staging_dir = os.path.abspath(os.path.join(STAGING_ROOT, target_subdir))
    ensure_inside_root(STAGING_ROOT, staging_dir)
    return staging_dir


def _maybe_resume_exact_download_from_journal(journal_record) -> None:
    if journal_record.status not in {"queued", "resolvingFiles", "downloading", "validating", "registeringAlias"}:
        return
    if _download_worker_active(journal_record.operation_id):
        return
    request = _exact_download_request_from_journal(journal_record)
    with OPERATIONS_LOCK:
        state = OPERATIONS.get(journal_record.operation_id)
        if state is None:
            OPERATIONS[journal_record.operation_id] = DownloadOperationState(
                operation_id=journal_record.operation_id,
                router_model_id=journal_record.alias,
                status=journal_record.status,
                progress=journal_record.progress,
                log_line=journal_record.log_line,
            )
    _start_download_worker(
        journal_record.operation_id,
        run_exact_download_operation,
        (journal_record.operation_id, request),
    )


def run_exact_download_operation(operation_id: str, request: ExactDownloadRequest) -> None:
    alias = normalize_alias(request.alias)
    alias_lock = get_alias_lock(alias)
    alias_lock.acquire()
    staging_dir = _exact_download_staging_dir(request.targetDirectory)
    completed_successfully = False
    try:
        preset = normalize_preset_map(request.preset)
        immutable_input = build_immutable_input(
            repository=request.repository,
            resolved_revision=request.resolvedRevision,
            model_files=request.modelFiles,
            mmproj_files=request.mmprojFiles,
            companion_files=request.companionFiles,
            alias=alias,
            target_directory=request.targetDirectory,
            preset=preset,
            preset_mode=request.presetMode,
            artifact_metadata=[
                item.model_dump(exclude_none=True) for item in (request.artifactMetadata or [])
            ],
        )
        try:
            OPERATION_JOURNAL.create(operation_id=operation_id, immutable_input=immutable_input, alias=alias)
        except OperationJournalError:
            existing = OPERATION_JOURNAL.get(operation_id)
            if existing is None or _immutable_input_download_identity(existing.immutable_input) != _exact_download_request_identity(request):
                fail_operation(operation_id, "Operation id conflicts with a different immutable input.")
                OPERATION_JOURNAL.update(
                    operation_id,
                    status="failed",
                    error_message="Operation id conflicts with a different immutable input.",
                    completed_at=utc_now_iso(),
                )
                return
            OPERATION_JOURNAL.update(
                operation_id,
                status="downloading",
                error_message=None,
                completed_at=None,
                log_line="Resuming staged download.",
            )

        OPERATION_JOURNAL.update(operation_id, status="downloading", progress=0.05, log_line="Preparing staged download.")
        update_operation(operation_id, status="downloading", progress=0.05, log_line="Preparing staged download.")

        metadata_payload = [
            item.model_dump(exclude_none=True) for item in (request.artifactMetadata or [])
        ]
        target_dir, model_specs, mmproj_specs, companion_specs = build_artifact_specs(
            model_files=request.modelFiles,
            mmproj_files=request.mmprojFiles,
            companion_files=request.companionFiles,
            store_root=MODEL_STORE_ROOT,
            target_subdir=request.targetDirectory,
            artifact_metadata=metadata_payload,
        )
        os.makedirs(staging_dir, exist_ok=True)
        token = (request.hfToken or "").strip() or None
        all_specs = model_specs + mmproj_specs + companion_specs
        total = len(all_specs)

        for idx, spec in enumerate(all_specs):
            if artifact_is_installed(spec) or staged_artifact_path(staging_dir, spec) is not None:
                _ensure_journal_download_step(operation_id, spec.repository_path)
                continue

            def report(bytes_read: int, *, file_idx: int = idx) -> None:
                if spec.expected_size and spec.expected_size > 0:
                    file_fraction = min(bytes_read / float(spec.expected_size), 1.0)
                else:
                    file_fraction = 0.0
                progress = 0.05 + 0.80 * ((file_idx + file_fraction) / max(total, 1))
                OPERATION_JOURNAL.update(operation_id, status="downloading", progress=progress, log_line=f"Downloading {spec.repository_path}")
                update_operation(operation_id, status="downloading", progress=progress, log_line=f"Downloading {spec.repository_path}")

            stage_download_file(
                repository=request.repository.strip(),
                resolved_revision=request.resolvedRevision.strip(),
                spec=spec,
                staging_dir=staging_dir,
                token=token,
                operation_id=operation_id,
                progress_callback=report,
            )
            OPERATION_JOURNAL.append_step(operation_id, "downloadModelFile", spec.repository_path)

        OPERATION_JOURNAL.update(operation_id, status="validating", progress=0.88, log_line="Validating staged artifact set.")
        update_operation(operation_id, status="validating", progress=0.88, log_line="Validating staged artifact set.")
        activate_staged_files(
            staging_dir=staging_dir,
            target_dir=target_dir,
            store_root=MODEL_STORE_ROOT,
            specs=all_specs,
        )
        OPERATION_JOURNAL.mark_side_effect(operation_id, "artifactsActivated")

        update_operation(operation_id, status="registeringAlias", progress=0.92, log_line="Registering router alias.")
        OPERATION_JOURNAL.update(operation_id, status="registeringAlias", progress=0.92, log_line="Registering router alias.")

        target_subdir = request.targetDirectory.strip().strip("/\\")
        first_model_name = os.path.basename(model_specs[0].repository_path.replace("\\", "/"))
        model_container_path = f"/models-local/llama/{target_subdir}/{first_model_name}"
        mmproj_container_path = ""
        if mmproj_specs:
            mmproj_name = os.path.basename(mmproj_specs[0].repository_path.replace("\\", "/"))
            mmproj_container_path = f"/models-local/llama/{target_subdir}/{mmproj_name}"

        ini_sha256, runtime_apply = upsert_router_entry(
            alias,
            model_container_path,
            mmproj_container_path,
            preset=preset,
            preset_mode=request.presetMode,
        )
        OPERATION_JOURNAL.mark_side_effect(operation_id, "routerIniCommitted")
        OPERATION_JOURNAL.update(operation_id, ini_sha256=ini_sha256)

        if runtime_apply is not None and not runtime_apply.applied:
            message = runtime_apply.remediation or "INI committed but llama-server reload failed."
            OPERATION_JOURNAL.update(
                operation_id,
                status="failed",
                progress=0.98,
                error_message=message,
                log_line=message,
                completed_at=utc_now_iso(),
            )
            update_operation(
                operation_id,
                status="failed",
                progress=0.98,
                error_message=message,
                log_line=message,
                completed_at=utc_now_iso(),
            )
            return

        OPERATION_JOURNAL.update(
            operation_id,
            status="completed",
            progress=1.0,
            error_message=None,
            log_line="Completed.",
            completed_at=utc_now_iso(),
        )
        update_operation(
            operation_id,
            status="completed",
            progress=1.0,
            log_line="Completed.",
            completed_at=utc_now_iso(),
        )
        completed_successfully = True
    except (ExactDownloadError, PresetValidationError, PathSafetyError, OperationJournalError, ValueError) as exc:
        code = getattr(exc, "code", type(exc).__name__)
        message = str(exc)
        log_event("llama_admin_exact_download_failed", operationId=operation_id, errorType=code, error=message)
        if isinstance(exc, ExactDownloadError) and code == "DOWNLOAD_TRUNCATED":
            journal_record = OPERATION_JOURNAL.get(operation_id)
            progress = journal_record.progress if journal_record is not None else None
            OPERATION_JOURNAL.update(
                operation_id,
                status="downloading",
                error_message=message,
                log_line=message,
                completed_at=None,
            )
            update_operation(
                operation_id,
                status="downloading",
                progress=progress,
                error_message=message,
                log_line=message,
                completed_at=None,
            )
        else:
            fail_operation(operation_id, message)
            if OPERATION_JOURNAL.get(operation_id) is not None:
                OPERATION_JOURNAL.update(
                    operation_id,
                    status="failed",
                    error_message=message,
                    log_line=message,
                    completed_at=utc_now_iso(),
                )
    except Exception as exc:
        log_event("llama_admin_exact_download_failed", operationId=operation_id, errorType=type(exc).__name__, error=str(exc))
        fail_operation(operation_id, str(exc))
        if OPERATION_JOURNAL.get(operation_id) is not None:
            OPERATION_JOURNAL.update(
                operation_id,
                status="failed",
                error_message=str(exc),
                log_line=str(exc),
                completed_at=utc_now_iso(),
            )
    finally:
        alias_lock.release()
        DOWNLOAD_WORKERS.pop(operation_id, None)
        if completed_successfully and os.path.isdir(staging_dir):
            shutil.rmtree(staging_dir, ignore_errors=True)


APP = FastAPI(title="GuideAnts Llama Admin Service", version="1.0.0")


def _catalog_error_status(code: str) -> int:
    if code in {"CATALOG_DEFINITION_NOT_FOUND", "REPOSITORY_NOT_FOUND"}:
        return 404
    if code in {"CATALOG_VERSION_MISMATCH"}:
        return 409
    if code in {"HUGGINGFACE_TOKEN_MISSING", "REPO_TOKEN_INSUFFICIENT"}:
        return 403
    if code in {"PROJECTOR_NOT_FOUND", "INCOMPLETE_QUANT_GROUP"}:
        return 422
    return 400


def _resolve_hf_token(request: Request) -> str | None:
    header_token = (request.headers.get("X-HF-Token") or "").strip()
    return header_token or None


@APP.get("/admin/catalog")
def get_admin_catalog() -> dict[str, Any]:
    return build_catalog_response()


@APP.get("/admin/catalog/{catalog_id}/quants")
def get_admin_catalog_quants(catalog_id: str, request: Request) -> dict[str, Any]:
    catalog_version = (request.query_params.get("catalogVersion") or "").strip() or None
    resolved_revision = (request.query_params.get("resolvedRevision") or "").strip() or None
    hf_token = _resolve_hf_token(request)
    try:
        return resolve_definition_quants(
            catalog_id.strip(),
            hf_token,
            catalog_version=catalog_version,
            resolved_revision=resolved_revision,
        )
    except CatalogDefinitionError as exc:
        raise HTTPException(
            status_code=_catalog_error_status(exc.code),
            detail={"code": exc.code, "message": str(exc)},
        ) from exc
    except HuggingFaceAccessError as exc:
        raise HTTPException(
            status_code=_catalog_error_status(exc.code),
            detail={"code": exc.code, "message": str(exc)},
        ) from exc


@APP.get("/health")
def health() -> dict[str, Any]:
    return {
        "status": "ok",
        "routerConfigPath": ROUTER_CONFIG_PATH,
        "modelStoreRoot": MODEL_STORE_ROOT,
    }


class FleetPresetPutRequest(BaseModel):
    expectedRevision: int
    preset: dict[str, Any]


@APP.get("/runtime/fleet-preset")
def get_runtime_fleet_preset() -> dict[str, Any]:
    return get_fleet_preset_response()


@APP.put("/runtime/fleet-preset")
def put_runtime_fleet_preset(request: FleetPresetPutRequest) -> dict[str, Any]:
    try:
        response = put_fleet_preset(request.expectedRevision, request.preset)
        restart_result = restart_llama_server()
        if not restart_result.get("restarted"):
            response["applyStatus"] = "error"
            response["applyError"] = "llama-server restart did not complete."
            return response
        return confirm_fleet_restart(response["desiredRevision"])
    except ValueError as exc:
        raise HTTPException(status_code=409, detail=str(exc)) from exc


@APP.get("/router/entries")
def get_router_entries() -> RouterEntriesResponse:
    """Return alias presets as stored in canonical router-models.ini."""
    with ROUTER_FILE_LOCK:
        if not os.path.exists(ROUTER_CONFIG_PATH):
            entries: dict[str, _RouterSection] = {}
        else:
            with open(ROUTER_CONFIG_PATH, "r", encoding="utf-8") as handle:
                entries = parse_router_ini(handle.read())

    result: list[RouterEntryDto] = []
    for alias in sorted(entries.keys()):
        sec = entries[alias]
        ex = sec.extras
        ctx = _try_int_from_extras(ex, _CTX_KEYS)
        c_mib = _try_int_from_extras(ex, _CACHE_KEYS)
        result.append(
            RouterEntryDto(
                alias=alias,
                modelPath=sec.model,
                mmprojPath=sec.mmproj,
                hasModelFile=has_artifact(sec.model),
                hasMmprojFile=has_artifact(sec.mmproj),
                contextSize=ctx,
                cacheRamMib=c_mib,
                preset=dict(ex),
            )
        )
    return RouterEntriesResponse(entries=result)


@APP.post("/admin/artifacts/delete-obsolete")
def delete_obsolete_artifacts(request: DeleteObsoleteArtifactsRequest) -> dict[str, Any]:
    if not request.targetDirectory.strip():
        raise HTTPException(status_code=400, detail="targetDirectory is required")
    if not request.repositoryPaths:
        return {"removed": []}
    try:
        removed = delete_obsolete_repository_paths(
            store_root=MODEL_STORE_ROOT,
            target_subdir=request.targetDirectory,
            repository_paths=request.repositoryPaths,
        )
    except PathSafetyError as exc:
        raise HTTPException(status_code=400, detail=str(exc)) from exc
    return {"removed": removed}


@APP.post("/router/entries")
def post_router_entry(request: RouterEntryUpsertRequest) -> dict[str, Any]:
    if not request.alias.strip():
        raise HTTPException(status_code=400, detail="alias is required")
    if not request.modelPath.strip():
        raise HTTPException(status_code=400, detail="modelPath is required")

    set_fields = _pydantic_set_fields(request)
    update_context = "contextSize" in set_fields
    update_cache = "cacheRamMib" in set_fields
    # Explicit preset maps are WYSIWYG (catalog editor Save). Path-only syncs omit
    # preset and keep merge for context/cache patches. Honoring client "merge" when
    # a preset body is present makes removed keys appear to resurrect after reload.
    effective_preset_mode = "replace" if request.preset is not None else request.presetMode
    log_event(
        "router_entry_upsert_requested",
        alias=request.alias.strip(),
        request={
            "setFields": sorted(list(set_fields)),
            "presetMode": request.presetMode,
            "effectivePresetMode": effective_preset_mode,
            "contextSize": request.contextSize,
            "cacheRamMib": request.cacheRamMib,
            "modelPath": request.modelPath.strip(),
            "mmprojPath": request.mmprojPath.strip(),
            "updateContext": update_context,
            "updateCache": update_cache,
        },
    )

    try:
        ini_sha256, runtime_apply = upsert_router_entry(
            request.alias,
            request.modelPath,
            request.mmprojPath,
            preset=request.preset,
            preset_mode=effective_preset_mode,
            context_size=request.contextSize,
            cache_ram_mib=request.cacheRamMib,
            update_context=update_context,
            update_cache=update_cache,
        )
    except PresetValidationError as exc:
        raise HTTPException(status_code=400, detail={"code": exc.code, "message": str(exc)}) from exc
    except ValueError as exc:
        raise HTTPException(status_code=400, detail=str(exc)) from exc
    except Exception as exc:
        raise HTTPException(status_code=500, detail=str(exc)) from exc

    response: dict[str, Any] = {"ok": True, "iniSha256": ini_sha256}
    if runtime_apply is not None:
        response["runtimeApply"] = {
            "applied": runtime_apply.applied,
            "iniSha256": runtime_apply.ini_sha256,
            "remediation": runtime_apply.remediation,
        }
        if not runtime_apply.applied:
            raise HTTPException(status_code=502, detail=response)
    return response


@APP.delete("/router/entries/{alias}")
def delete_router_entry_route(alias: str) -> Response:
    alias_trimmed = alias.strip()
    if not alias_trimmed:
        raise HTTPException(status_code=400, detail="alias is required")

    alias_lock = get_alias_lock(alias_trimmed)
    alias_lock.acquire()
    try:
        removed = remove_router_entry(alias_trimmed)
        if removed is None:
            raise HTTPException(status_code=404, detail="router alias not found")
        model_path, mmproj_path = removed
        try:
            delete_registered_artifacts(model_path, mmproj_path)
        except OSError as exc:
            log_event("artifact_delete_failed", alias=alias_trimmed, error=str(exc))
            raise HTTPException(
                status_code=500,
                detail=f"Removed router entry but failed to delete files: {exc}",
            ) from exc
        return Response(status_code=204)
    finally:
        alias_lock.release()


@APP.post("/downloads")
async def start_download(http_request: Request) -> dict[str, Any]:
    body = await http_request.json()
    if not isinstance(body, dict):
        raise HTTPException(status_code=400, detail="Request body must be a JSON object.")

    if "modelFiles" in body:
        request = ExactDownloadRequest.model_validate(body)
        return _start_exact_download(request)

    request = StartDownloadRequest.model_validate(body)
    return _start_legacy_download(request)


def _start_exact_download(request: ExactDownloadRequest) -> dict[str, Any]:
    if not request.operationId.strip():
        raise HTTPException(status_code=400, detail="operationId is required")
    if not request.repository.strip():
        raise HTTPException(status_code=400, detail="repository is required")
    if not request.resolvedRevision.strip():
        raise HTTPException(status_code=400, detail="resolvedRevision is required")
    if not request.modelFiles:
        raise HTTPException(status_code=400, detail="modelFiles is required")
    if not request.alias.strip():
        raise HTTPException(status_code=400, detail="alias is required")
    if not request.targetDirectory.strip():
        raise HTTPException(status_code=400, detail="targetDirectory is required")

    alias = normalize_alias(request.alias)
    in_flight_statuses = {"queued", "resolvingFiles", "downloading", "validating", "registeringAlias"}
    restartable_statuses = in_flight_statuses | {"failed"}
    operation_id = request.operationId.strip()

    existing_journal = OPERATION_JOURNAL.get(operation_id)
    if existing_journal is not None and existing_journal.status in restartable_statuses:
        if _immutable_input_download_identity(existing_journal.immutable_input) != _exact_download_request_identity(request):
            raise HTTPException(
                status_code=409,
                detail={
                    "error": f"Operation '{operation_id}' conflicts with a different download request.",
                    "operationId": operation_id,
                    "status": existing_journal.status,
                    "routerModelId": existing_journal.alias,
                    "progress": existing_journal.progress,
                },
            )
        if _download_worker_active(operation_id):
            return existing_journal.to_dto()
        if existing_journal.status != "failed":
            return existing_journal.to_dto()
        OPERATION_JOURNAL.update(
            operation_id,
            status="queued",
            error_message=None,
            completed_at=None,
            log_line="Retrying interrupted download.",
        )

    with OPERATIONS_LOCK:
        existing = next(
            (
                op
                for op in OPERATIONS.values()
                if op.router_model_id == alias and op.status in in_flight_statuses
            ),
            None,
        )
        if existing is not None and existing.operation_id != request.operationId.strip():
            raise HTTPException(
                status_code=409,
                detail={
                    "error": f"A download for alias '{alias}' is already in progress.",
                    "operationId": existing.operation_id,
                    "status": existing.status,
                    "routerModelId": existing.router_model_id,
                    "progress": existing.progress,
                },
            )

        operation_id = request.operationId.strip()
        if existing_journal is not None and existing_journal.status in restartable_statuses:
            state = DownloadOperationState(
                operation_id=operation_id,
                router_model_id=alias,
                status="queued" if existing_journal.status == "failed" else existing_journal.status,
                progress=existing_journal.progress,
                log_line=existing_journal.log_line,
            )
        else:
            state = DownloadOperationState(
                operation_id=operation_id,
                router_model_id=alias,
                status="queued",
                progress=0.0,
            )
        OPERATIONS[operation_id] = state

    _start_download_worker(operation_id, run_exact_download_operation, (operation_id, request))
    journal = OPERATION_JOURNAL.get(operation_id)
    if journal is not None:
        return journal.to_dto()
    with OPERATIONS_LOCK:
        return OPERATIONS[operation_id].to_dto()


def _start_legacy_download(request: StartDownloadRequest) -> dict[str, Any]:
    if not request.repository.strip():
        raise HTTPException(status_code=400, detail="repository is required")
    if not request.quantIncludePattern.strip():
        raise HTTPException(status_code=400, detail="quantIncludePattern is required")
    if not request.routerModelId.strip():
        raise HTTPException(status_code=400, detail="routerModelId is required")
    if not request.targetDirectory.strip():
        raise HTTPException(status_code=400, detail="targetDirectory is required")

    alias = request.routerModelId.strip()
    in_flight_statuses = {"queued", "resolvingFiles", "downloading", "registeringAlias"}

    with OPERATIONS_LOCK:
        existing = next(
            (
                op
                for op in OPERATIONS.values()
                if op.router_model_id == alias and op.status in in_flight_statuses
            ),
            None,
        )
        if existing is not None:
            raise HTTPException(
                status_code=409,
                detail={
                    "error": f"A download for alias '{alias}' is already in progress.",
                    "operationId": existing.operation_id,
                    "status": existing.status,
                    "routerModelId": existing.router_model_id,
                    "progress": existing.progress,
                },
            )

        operation_id = uuid.uuid4().hex
        state = DownloadOperationState(
            operation_id=operation_id,
            router_model_id=alias,
            status="queued",
            progress=0.0,
        )
        OPERATIONS[operation_id] = state

    worker = threading.Thread(
        target=run_download_operation,
        args=(operation_id, request),
        daemon=True,
    )
    worker.start()
    return state.to_dto()


@APP.get("/downloads/{operation_id}")
def get_download_status(operation_id: str) -> dict[str, Any]:
    journal_record = OPERATION_JOURNAL.get(operation_id)
    if journal_record is not None:
        _maybe_resume_exact_download_from_journal(journal_record)
        journal_record = OPERATION_JOURNAL.get(operation_id) or journal_record
        return journal_record.to_dto()
    with OPERATIONS_LOCK:
        state = OPERATIONS.get(operation_id)
        if state is None:
            raise HTTPException(status_code=404, detail="operation not found")
        return state.to_dto()


def _pid_alive(pid: int | None) -> bool:
    if pid is None or pid <= 0:
        return False
    try:
        os.kill(pid, 0)
        return True
    except ProcessLookupError:
        return False
    except PermissionError:
        # Process exists in another uid namespace; treat as alive.
        return True
    except OSError:
        return False


def _find_llama_server_pids() -> list[int]:
    """
    Authoritative enumeration of currently-running llama-server processes inside the container.
    We scan /proc/*/comm rather than trusting /run/llama-server.pid because the whole point of
    the restart endpoint is to make no assumptions about who last wrote the PID file — if the
    entrypoint watchdog bug ever lets two llama-servers run at once, we still want this endpoint
    to converge the system back to "exactly one".
    """
    pids: list[int] = []
    try:
        entries = os.listdir("/proc")
    except OSError:
        return pids
    for entry in entries:
        if not entry.isdigit():
            continue
        try:
            with open(f"/proc/{entry}/comm", "r", encoding="utf-8") as handle:
                comm = handle.read().strip()
        except (FileNotFoundError, PermissionError, OSError):
            continue
        # Linux truncates /proc/<pid>/comm at 15 chars; "llama-server" fits, but guard against
        # future renames by accepting the common startswith as well.
        if comm == "llama-server" or comm.startswith("llama-server"):
            try:
                pids.append(int(entry))
            except ValueError:
                continue
    return pids


def _read_last_exit_record() -> dict[str, Any] | None:
    try:
        with open(LLAMA_LAST_EXIT_FILE, "r", encoding="utf-8") as handle:
            raw = handle.read().strip()
    except (FileNotFoundError, OSError):
        return None
    if not raw:
        return None
    try:
        parsed = json.loads(raw)
    except json.JSONDecodeError:
        return None
    return parsed if isinstance(parsed, dict) else None


def _seconds_since_iso(iso_timestamp: str | None) -> float | None:
    if not isinstance(iso_timestamp, str) or not iso_timestamp:
        return None
    try:
        parsed = datetime.fromisoformat(iso_timestamp.replace("Z", "+00:00"))
    except ValueError:
        return None
    if parsed.tzinfo is None:
        parsed = parsed.replace(tzinfo=timezone.utc)
    return max(0.0, (datetime.now(timezone.utc) - parsed).total_seconds())


@APP.get("/llama/last-exit")
def get_last_exit() -> dict[str, Any]:
    """
    Diagnostic endpoint used by the GuideAntsApi admin surface and (optionally) the chat-side
    crash classifier to correlate an HTTP 5xx from llama-server with a concrete kernel exit.

    ``serverAlive`` is the authoritative signal for "do we need to restart" — if the process is
    alive the restart endpoint will SIGTERM it regardless, since a CUDA-context-poisoned
    llama-server is effectively unusable even while its PID is still valid.
    """
    record = _read_last_exit_record()
    current_pid = _read_llama_server_pid()
    server_alive = _pid_alive(current_pid)
    seconds_since_exit = (
        _seconds_since_iso(record.get("exitedAt")) if record else None
    )
    return {
        "serverAlive": server_alive,
        "currentPid": current_pid,
        "lastExit": record,
        "secondsSinceExit": seconds_since_exit,
    }


def _signal_all(pids: list[int], sig: int) -> list[int]:
    """Best-effort signal of each pid; returns pids that were still alive at signal time."""
    signaled: list[int] = []
    for pid in pids:
        try:
            os.kill(pid, sig)
            signaled.append(pid)
        except ProcessLookupError:
            continue
        except PermissionError as exc:
            log_event("llama_server_signal_permission_error", pid=pid, sig=sig, reason=str(exc))
            raise HTTPException(
                status_code=500,
                detail=f"Not permitted to signal PID {pid} with signal {sig}: {exc}",
            ) from exc
    return signaled


def _wait_until(predicate: Callable[[], bool], deadline_monotonic: float, poll_seconds: float = 0.25) -> bool:
    while time.monotonic() < deadline_monotonic:
        if predicate():
            return True
        time.sleep(poll_seconds)
    return predicate()


@APP.post("/llama/restart")
def restart_llama_server() -> dict[str, Any]:
    """
    Converge the container to "exactly one fresh llama-server".

    The contract is deliberately brutal and assumption-free:

      1. Enumerate every ``llama-server`` process in the container (via ``/proc/*/comm``);
         do NOT trust ``/run/llama-server.pid`` — it is a hint, not the ground truth.
      2. SIGTERM all of them. Grace-wait up to 5s for clean exit (llama.cpp flushes CUDA
         handles and releases VRAM on SIGTERM).
      3. Any survivors get SIGKILL. If SIGKILL fails, we fail the request — something is very
         wrong and the operator needs to see it.
      4. The container's ``entrypoint.sh`` watchdog observes the dead PID and respawns one
         fresh llama-server; we poll ``/proc`` until exactly one exists, then poll ``/models``
         until it answers. No automatic model reload — the crash-recovery flow deliberately
         returns the user to the model-load dialog so they can pick consciously (OOMs tend
         to recur with the same config, so re-loading the same alias is usually wrong).
    """
    initial_pids = _find_llama_server_pids()
    log_event("llama_server_restart_start", initialPids=initial_pids)

    termed = _signal_all(initial_pids, signal.SIGTERM)

    grace_deadline = time.monotonic() + 5.0
    all_dead = _wait_until(
        lambda: not any(_pid_alive(p) for p in initial_pids),
        grace_deadline,
    )

    killed: list[int] = []
    if not all_dead:
        stragglers = [p for p in initial_pids if _pid_alive(p)]
        log_event("llama_server_restart_sigkill", pids=stragglers)
        killed = _signal_all(stragglers, signal.SIGKILL)
        kill_deadline = time.monotonic() + 2.0
        if not _wait_until(
            lambda: not any(_pid_alive(p) for p in initial_pids),
            kill_deadline,
        ):
            remaining = [p for p in initial_pids if _pid_alive(p)]
            log_event("llama_server_restart_sigkill_failed", pids=remaining)
            raise HTTPException(
                status_code=500,
                detail=f"Could not terminate llama-server pids={remaining}.",
            )

    # By contract the entrypoint watchdog should respawn ONE llama-server. We cap waiting to
    # LLAMA_RESTART_TIMEOUT_SECONDS for the whole respawn + readiness window.
    respawn_deadline = time.monotonic() + LLAMA_RESTART_TIMEOUT_SECONDS
    new_pid: int | None = None
    while time.monotonic() < respawn_deadline:
        current = _find_llama_server_pids()
        alive = [p for p in current if _pid_alive(p)]
        if len(alive) >= 1:
            # If more than one, the watchdog raced somehow. We still accept the youngest and
            # kill the rest — the invariant is *exactly one* after this call returns.
            if len(alive) > 1:
                log_event("llama_server_restart_unexpected_duplicates", pids=alive)
                # Keep the largest PID (most recently spawned) and terminate the rest.
                youngest = max(alive)
                for p in alive:
                    if p != youngest:
                        try:
                            os.kill(p, signal.SIGTERM)
                        except OSError:
                            pass
                new_pid = youngest
            else:
                new_pid = alive[0]
            break
        time.sleep(0.25)

    if new_pid is None:
        log_event(
            "llama_server_restart_no_new_pid",
            initialPids=initial_pids,
            termed=termed,
            killed=killed,
        )
        raise HTTPException(
            status_code=504,
            detail=(
                f"llama-server did not respawn within {LLAMA_RESTART_TIMEOUT_SECONDS}s "
                f"(killed pids={initial_pids})."
            ),
        )

    while time.monotonic() < respawn_deadline:
        try:
            _llama_http_get_json("/models", timeout=1.5)
            log_event(
                "llama_server_restart_ready",
                initialPids=initial_pids,
                newPid=new_pid,
                termed=termed,
                killed=killed,
            )
            return {
                "restarted": True,
                "termed": bool(termed),
                "oldPid": initial_pids[0] if initial_pids else None,
                "newPid": new_pid,
            }
        except Exception:  # noqa: BLE001 - poll until reachable or deadline
            time.sleep(0.25)

    log_event(
        "llama_server_restart_models_timeout",
        initialPids=initial_pids,
        newPid=new_pid,
    )
    raise HTTPException(
        status_code=504,
        detail=(
            f"llama-server (PID {new_pid}) respawned but did not answer /models within "
            f"{LLAMA_RESTART_TIMEOUT_SECONDS}s."
        ),
    )


if __name__ == "__main__":
    host = os.getenv("GA_LLAMA_ADMIN_HOST", "127.0.0.1")
    port = parse_int(os.getenv("GA_LLAMA_ADMIN_PORT"), 8086)
    log_level = (os.getenv("GA_LLAMA_ADMIN_LOG_LEVEL") or "info").strip().lower()
    access_log = env_flag("GA_LLAMA_ADMIN_UVICORN_ACCESS_LOG", default=False)
    uvicorn.run(APP, host=host, port=port, log_level=log_level, access_log=access_log)
