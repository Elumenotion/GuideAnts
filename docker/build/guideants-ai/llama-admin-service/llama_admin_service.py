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

import uvicorn
from fastapi import FastAPI, HTTPException, Response
from pydantic import BaseModel


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
HF_TIMEOUT_SECONDS = parse_int(os.getenv("GA_LLAMA_ADMIN_HF_TIMEOUT_SECONDS"), 1800)
HTTP_USER_AGENT = "GuideAnts-LlamaAdmin/1.0"


def build_regex_from_include_pattern(pattern: str) -> re.Pattern[str]:
    parts = pattern.split("*")
    expression = "^" + ".*".join(re.escape(part) for part in parts) + "$"
    return re.compile(expression, re.IGNORECASE)


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

        directory = os.path.dirname(ROUTER_CONFIG_PATH)
        if directory:
            os.makedirs(directory, exist_ok=True)
        payload = serialize_router_ini(entries)

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
            _commit_router_ini_file(temp_path, ROUTER_CONFIG_PATH, payload)
        finally:
            if os.path.exists(temp_path):
                try:
                    os.remove(temp_path)
                except OSError:
                    pass

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


def _strip_extras_matching(
    extras: dict[str, str], allowed_lower: set[str]
) -> None:
    for k in list(extras.keys()):
        if k.lower() in allowed_lower:
            del extras[k]


def upsert_router_entry(
    alias: str,
    model_path: str,
    mmproj_path: str,
    *,
    context_size: int | None = None,
    cache_ram_mib: int | None = None,
    update_context: bool = False,
    update_cache: bool = False,
) -> None:
    """Write model/mmproj. Optional ctx/cache updates are applied only when the flag is True; null then clears
    the corresponding keys. When False, existing per-alias extra keys in those families are left unchanged
    (paths-only registration)."""
    alias_trimmed = alias.strip()
    if not alias_trimmed:
        raise ValueError("Router alias is required.")
    _ctx_l = {x.lower() for x in _CTX_KEYS}
    _cache_l = {x.lower() for x in _CACHE_KEYS}

    with ROUTER_FILE_LOCK:
        entries: dict[str, _RouterSection] = {}
        if os.path.exists(ROUTER_CONFIG_PATH):
            with open(ROUTER_CONFIG_PATH, "r", encoding="utf-8") as handle:
                entries = parse_router_ini(handle.read())

        payload_before = serialize_router_ini(entries)
        prior = entries.get(alias_trimmed)
        extras: dict[str, str] = dict(prior.extras) if prior else {}

        if update_context:
            _strip_extras_matching(extras, _ctx_l)
            if context_size is not None:
                # Canonical preset key for router per-model configuration.
                extras["ctx-size"] = str(int(context_size))
        if update_cache:
            _strip_extras_matching(extras, _cache_l)
            if cache_ram_mib is not None:
                # Canonical preset key for router per-model configuration.
                extras["cache-ram"] = str(int(cache_ram_mib))

        entries[alias_trimmed] = _RouterSection(
            model=model_path.strip(),
            mmproj=mmproj_path.strip(),
            extras=extras,
        )

        directory = os.path.dirname(ROUTER_CONFIG_PATH)
        if directory:
            os.makedirs(directory, exist_ok=True)
        payload = serialize_router_ini(entries)
        changed = payload != payload_before

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
            _commit_router_ini_file(temp_path, ROUTER_CONFIG_PATH, payload)
        finally:
            if os.path.exists(temp_path):
                try:
                    os.remove(temp_path)
                except OSError:
                    pass

    before_summary = _section_summary(prior)
    after_summary = _section_summary(entries.get(alias_trimmed))
    log_event(
        "router_entry_upsert_applied",
        alias=alias_trimmed,
        request={
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
        iniSha256=hashlib.sha256(payload.encode("utf-8")).hexdigest(),
    )

    signal_llama_server_reload()
    log_event("router_entry_upsert_reload_triggered", alias=alias_trimmed, iniChanged=changed)


def list_hf_repository_files(repository: str, token: str | None) -> list[dict[str, Any]]:
    repo_normalized = repository.strip().strip("/")
    if not repo_normalized:
        raise ValueError("Repository is required.")

    url = f"https://huggingface.co/api/models/{repo_normalized}/tree/main?recursive=true"
    request = urllib.request.Request(
        url,
        method="GET",
        headers={
            "User-Agent": HTTP_USER_AGENT,
            **({"Authorization": f"Bearer {token}"} if token else {}),
        },
    )
    try:
        with urllib.request.urlopen(request, timeout=HF_TIMEOUT_SECONDS) as response:
            body = response.read().decode("utf-8", errors="replace")
    except urllib.error.HTTPError as exc:
        detail = exc.read().decode("utf-8", errors="replace")
        raise RuntimeError(f"Failed to list repository files ({exc.code}): {detail}") from exc
    except urllib.error.URLError as exc:
        raise RuntimeError(f"Failed to list repository files: {exc.reason}") from exc

    try:
        parsed = json.loads(body)
    except json.JSONDecodeError as exc:
        raise RuntimeError("Unexpected Hugging Face tree response (invalid JSON).") from exc

    if not isinstance(parsed, list):
        raise RuntimeError("Unexpected Hugging Face tree response (expected array).")

    out: list[dict[str, Any]] = []
    for item in parsed:
        if not isinstance(item, dict):
            continue
        file_type = item.get("type")
        path = item.get("path")
        size = item.get("size")
        if not isinstance(file_type, str) or not isinstance(path, str):
            continue
        out.append({"type": file_type, "path": path, "size": size if isinstance(size, int) else None})
    return out


def download_hf_file(
    repository: str,
    relative_path: str,
    destination_path: str,
    token: str | None,
    progress_callback: Callable[[int], None] | None = None,
) -> None:
    repo_path = "/".join(p.strip() for p in repository.split("/") if p.strip())
    encoded_segments = [urllib.parse.quote(segment) for segment in relative_path.split("/") if segment]
    encoded_path = "/".join(encoded_segments)
    url = f"https://huggingface.co/{repo_path}/resolve/main/{encoded_path}"

    request = urllib.request.Request(
        url,
        method="GET",
        headers={
            "User-Agent": HTTP_USER_AGENT,
            **({"Authorization": f"Bearer {token}"} if token else {}),
        },
    )

    temp_path = destination_path + ".tmp"
    try:
        with urllib.request.urlopen(request, timeout=HF_TIMEOUT_SECONDS) as response:
            with open(temp_path, "wb") as target:
                total = 0
                while True:
                    chunk = response.read(81920)
                    if not chunk:
                        break
                    target.write(chunk)
                    total += len(chunk)
                    if progress_callback:
                        progress_callback(total)
                target.flush()
                os.fsync(target.fileno())
    except urllib.error.HTTPError as exc:
        detail = exc.read().decode("utf-8", errors="replace")
        if os.path.exists(temp_path):
            os.remove(temp_path)
        raise RuntimeError(f"Download failed ({exc.code}): {detail}") from exc
    except Exception:
        if os.path.exists(temp_path):
            os.remove(temp_path)
        raise

    if os.path.exists(destination_path):
        os.remove(destination_path)
    os.replace(temp_path, destination_path)


class RouterEntryUpsertRequest(BaseModel):
    class Config:
        extra = "forbid"
    alias: str
    modelPath: str
    mmprojPath: str
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
    mmprojIncludePattern: str
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


OPERATIONS_LOCK = threading.Lock()
OPERATIONS: dict[str, DownloadOperationState] = {}
ALIAS_LOCKS_GUARD = threading.Lock()
ALIAS_LOCKS: dict[str, threading.Lock] = {}


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
        mmproj_regex = build_regex_from_include_pattern(request.mmprojIncludePattern.strip())

        quant_candidates = [
            f
            for f in files
            if f.get("type") == "file"
            and isinstance(f.get("path"), str)
            and f["path"].lower().endswith(".gguf")
            and quant_regex.match(f["path"]) is not None
        ]
        quant_candidates.sort(key=lambda f: f.get("size") or 0, reverse=True)

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
        if not mmproj_candidates:
            fail_operation(operation_id, "No mmproj file matched the mmproj include pattern.")
            return

        quant_file = quant_candidates[0]
        mmproj_file = mmproj_candidates[0]
        quant_path = str(quant_file["path"])
        mmproj_path = str(mmproj_file["path"])

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
        mmproj_name = os.path.basename(mmproj_path.replace("\\", "/"))
        quant_dest = os.path.join(target_dir, quant_name)
        mmproj_dest = os.path.join(target_dir, mmproj_name)

        if not request.allowOverwrite and (os.path.exists(quant_dest) or os.path.exists(mmproj_dest)):
            fail_operation(operation_id, "Target file(s) already exist. Enable AllowOverwrite or remove existing files.")
            return

        update_operation(operation_id, status="downloading", progress=0.05, log_line=f"Downloading {quant_path}")

        quant_size = quant_file.get("size")
        mmproj_size = mmproj_file.get("size")

        def report_quant(bytes_read: int) -> None:
            if isinstance(quant_size, int) and quant_size > 0:
                progress = 0.05 + 0.45 * (bytes_read / float(quant_size))
            else:
                progress = 0.05
            update_operation(operation_id, progress=min(progress, 0.5))

        def report_mmproj(bytes_read: int) -> None:
            if isinstance(mmproj_size, int) and mmproj_size > 0:
                progress = 0.50 + 0.45 * (bytes_read / float(mmproj_size))
            else:
                progress = 0.50
            update_operation(operation_id, progress=min(progress, 0.95))

        download_hf_file(
            request.repository.strip(),
            quant_path,
            quant_dest,
            token,
            progress_callback=report_quant,
        )
        update_operation(operation_id, log_line=f"Downloading {mmproj_path}")
        download_hf_file(
            request.repository.strip(),
            mmproj_path,
            mmproj_dest,
            token,
            progress_callback=report_mmproj,
        )

        update_operation(operation_id, status="registeringAlias", progress=0.92, log_line="Registering router alias.")
        # Paths written into router-models.ini are container paths that
        # llama-server will open at runtime. Must match the
        # GA_LLAMA_MODEL_DIR mount in docker-compose.yml
        # (/models-local/llama).
        model_container_path = f"/models-local/llama/{target_subdir}/{quant_name}"
        mmproj_container_path = f"/models-local/llama/{target_subdir}/{mmproj_name}"
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


APP = FastAPI(title="GuideAnts Llama Admin Service", version="1.0.0")


@APP.get("/health")
def health() -> dict[str, Any]:
    return {
        "status": "ok",
        "routerConfigPath": ROUTER_CONFIG_PATH,
        "modelStoreRoot": MODEL_STORE_ROOT,
    }


@APP.get("/router/entries")
def get_router_entries() -> list[RouterEntryDto]:
    entries = read_router_entries()
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
            )
        )
    return result


@APP.post("/router/entries")
def post_router_entry(request: RouterEntryUpsertRequest) -> dict[str, Any]:
    if not request.alias.strip():
        raise HTTPException(status_code=400, detail="alias is required")
    if not request.modelPath.strip():
        raise HTTPException(status_code=400, detail="modelPath is required")
    if not request.mmprojPath.strip():
        raise HTTPException(status_code=400, detail="mmprojPath is required")

    set_fields = _pydantic_set_fields(request)
    update_context = "contextSize" in set_fields
    update_cache = "cacheRamMib" in set_fields
    log_event(
        "router_entry_upsert_requested",
        alias=request.alias.strip(),
        request={
            "setFields": sorted(list(set_fields)),
            "contextSize": request.contextSize,
            "cacheRamMib": request.cacheRamMib,
            "modelPath": request.modelPath.strip(),
            "mmprojPath": request.mmprojPath.strip(),
            "updateContext": update_context,
            "updateCache": update_cache,
        },
    )

    try:
        upsert_router_entry(
            request.alias,
            request.modelPath,
            request.mmprojPath,
            context_size=request.contextSize,
            cache_ram_mib=request.cacheRamMib,
            update_context=update_context,
            update_cache=update_cache,
        )
    except ValueError as exc:
        raise HTTPException(status_code=400, detail=str(exc)) from exc
    except Exception as exc:
        raise HTTPException(status_code=500, detail=str(exc)) from exc

    return {"ok": True}


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
def start_download(request: StartDownloadRequest) -> dict[str, Any]:
    if not request.repository.strip():
        raise HTTPException(status_code=400, detail="repository is required")
    if not request.quantIncludePattern.strip():
        raise HTTPException(status_code=400, detail="quantIncludePattern is required")
    if not request.mmprojIncludePattern.strip():
        raise HTTPException(status_code=400, detail="mmprojIncludePattern is required")
    if not request.routerModelId.strip():
        raise HTTPException(status_code=400, detail="routerModelId is required")
    if not request.targetDirectory.strip():
        raise HTTPException(status_code=400, detail="targetDirectory is required")

    operation_id = uuid.uuid4().hex
    state = DownloadOperationState(
        operation_id=operation_id,
        router_model_id=request.routerModelId.strip(),
        status="queued",
        progress=0.0,
    )
    with OPERATIONS_LOCK:
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
