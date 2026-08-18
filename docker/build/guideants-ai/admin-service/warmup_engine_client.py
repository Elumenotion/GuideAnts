"""HTTP helpers for ga-admin warmup execution (load/unload + engine probes).

DO NOT add routing policy, ServiceModes logic, revision reconciliation, or
"runtime drift" detection here. GuideAntsApi owns policy. This module only
talks to local engine admin ports on loopback.
"""

from __future__ import annotations

import json
import os
import time
import urllib.error
import urllib.parse
import urllib.request
from typing import Any

POLL_INTERVAL_SECONDS = 2.0


def _parse_positive_int(value: str | None, default: int) -> int:
    if value is None:
        return default
    try:
        parsed = int(value)
    except ValueError:
        return default
    return parsed if parsed > 0 else default


def _engine_base_url(host_env: str, port_env: str, default_port: int) -> str:
    host = (os.getenv(host_env) or "127.0.0.1").strip() or "127.0.0.1"
    port = _parse_positive_int(os.getenv(port_env), default_port)
    return f"http://{host}:{port}"


ASR_ENGINE_BASE_URL = _engine_base_url("GA_ASR_HOST", "GA_ASR_PORT", 8082)
TTS_ENGINE_BASE_URL = _engine_base_url("GA_TTS_HOST", "GA_TTS_PORT", 8084)
EMB_ENGINE_BASE_URL = _engine_base_url("GA_EMB_HOST", "GA_EMB_PORT", 8085)

LLAMA_HOST = (os.getenv("GA_LLAMA_HOST") or "127.0.0.1").strip() or "127.0.0.1"
LLAMA_PORT = _parse_positive_int(os.getenv("GA_LLAMA_PORT"), 8080)
LLAMA_BASE_URL = f"http://{LLAMA_HOST}:{LLAMA_PORT}"

SD_ADMIN_BASE_URL = _engine_base_url("GA_SD_HOST", "GA_SD_PORT", 8083)

SERVICE_ENGINE_BASE_URLS: dict[str, str] = {
    "SpeechTranscription": ASR_ENGINE_BASE_URL,
    "Embeddings": EMB_ENGINE_BASE_URL,
    "SpeechSynthesis": TTS_ENGINE_BASE_URL,
    "ImageGeneration": SD_ADMIN_BASE_URL,
}

READY_TIMEOUT_SECONDS: dict[str, int] = {
    "SpeechTranscription": _parse_positive_int(os.getenv("GA_ASR_READY_TIMEOUT_SECONDS"), 900),
    "SpeechSynthesis": _parse_positive_int(os.getenv("GA_TTS_READY_TIMEOUT_SECONDS"), 900),
    "Embeddings": _parse_positive_int(os.getenv("GA_EMB_READY_TIMEOUT_SECONDS"), 900),
    "ImageGeneration": _parse_positive_int(os.getenv("GA_SD_READY_TIMEOUT_SECONDS"), 900),
    "llama": _parse_positive_int(os.getenv("GA_LLAMA_LOAD_TIMEOUT_SECONDS"), 600),
}


def _http_request(
    method: str,
    url: str,
    *,
    body: bytes | None = None,
    headers: dict[str, str] | None = None,
    timeout: float,
) -> tuple[int, bytes]:
    request_headers = {"Accept": "application/json"}
    if headers:
        request_headers.update(headers)
    request = urllib.request.Request(url=url, data=body, method=method, headers=request_headers)
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            return int(response.status), response.read()
    except urllib.error.HTTPError as exc:
        return int(exc.code), exc.read()


def _post_json(url: str, payload: dict[str, Any] | None, timeout: float) -> tuple[int, str]:
    body = None if payload is None else json.dumps(payload).encode("utf-8")
    headers = {"Content-Type": "application/json"} if body is not None else None
    status, raw = _http_request("POST", url, body=body, headers=headers, timeout=timeout)
    return status, raw.decode("utf-8", errors="replace")


def _get_json(url: str, timeout: float) -> tuple[int, Any]:
    status, raw = _http_request("GET", url, timeout=timeout)
    text = raw.decode("utf-8", errors="replace")
    if not text.strip():
        return status, None
    try:
        return status, json.loads(text)
    except json.JSONDecodeError:
        return status, text


def _aux_load_body(model_ref: str | None, *, load_field: str = "model_path") -> dict[str, str] | None:
    if not model_ref or not model_ref.strip():
        return None
    trimmed = model_ref.strip()
    if trimmed.lower().endswith(".gguf"):
        return {"model_path": trimmed}
    if load_field == "model_id":
        return {"model_id": trimmed}
    return {"model_path": trimmed}


def post_aux_load(service_id: str, model_ref: str | None, *, load_field: str = "model_path") -> bool:
    base = SERVICE_ENGINE_BASE_URLS.get(service_id)
    if not base:
        return False
    timeout = float(READY_TIMEOUT_SECONDS.get(service_id, 900))
    if service_id == "ImageGeneration":
        if not model_ref or not model_ref.strip():
            return False
        bundle_id = model_ref.strip()
        body = {"bundle_id": bundle_id}
        status, _ = _post_json(f"{base.rstrip('/')}/admin/load", body, timeout=timeout)
        return status in (200, 201, 202, 204, 409)
    body = _aux_load_body(model_ref, load_field=load_field)
    status, _ = _post_json(f"{base.rstrip('/')}/admin/load", body, timeout=timeout)
    return status in (200, 201, 202, 204, 409)


def _aux_ready_matches_expected(payload: dict[str, Any], expected_model_ref: str) -> bool:
    expected = expected_model_ref.strip()
    if not expected:
        return True
    candidates = {
        str(payload.get("modelRef") or payload.get("model_ref") or "").strip(),
        str(payload.get("catalogEntryId") or payload.get("catalog_entry_id") or "").strip(),
        str(payload.get("bundleId") or payload.get("bundle_id") or "").strip(),
    }
    candidates.discard("")
    return bool(candidates) and expected in candidates


def post_aux_unload(service_id: str) -> bool:
    base = SERVICE_ENGINE_BASE_URLS.get(service_id)
    if not base:
        return False
    timeout = float(READY_TIMEOUT_SECONDS.get(service_id, 900))
    status, _ = _post_json(f"{base.rstrip('/')}/admin/unload", None, timeout=timeout)
    return status in (200, 201, 202, 204, 409)


def wait_aux_ready(service_id: str, *, expected_model_ref: str | None = None) -> bool:
    base = SERVICE_ENGINE_BASE_URLS.get(service_id)
    if not base:
        return False
    timeout = float(READY_TIMEOUT_SECONDS.get(service_id, 900))
    deadline = time.monotonic() + timeout
    if service_id == "ImageGeneration":
        while time.monotonic() < deadline:
            status, payload = _get_json(f"{base.rstrip('/')}/health", timeout=5.0)
            if status == 200:
                if isinstance(payload, dict):
                    engine = payload.get("engine") or {}
                    process_alive = engine.get("processAlive")
                    healthy = engine.get("healthy")
                    if process_alive is True and healthy is True:
                        if expected_model_ref:
                            engine = payload.get("engine") or {}
                            loaded_bundle = str(
                                engine.get("loadedBundleId")
                                or payload.get("loadedBundleId")
                                or ""
                            ).strip()
                            if loaded_bundle and loaded_bundle != expected_model_ref.strip():
                                time.sleep(POLL_INTERVAL_SECONDS)
                                continue
                        return True
                    if process_alive is True and healthy is None:
                        return True
                    if payload.get("status") == "ok":
                        return True
                else:
                    return True
            time.sleep(POLL_INTERVAL_SECONDS)
        return False
    while time.monotonic() < deadline:
        status, payload = _get_json(f"{base.rstrip('/')}/ready", timeout=5.0)
        if status == 200:
            if expected_model_ref and isinstance(payload, dict):
                if not _aux_ready_matches_expected(payload, expected_model_ref):
                    time.sleep(POLL_INTERVAL_SECONDS)
                    continue
            return True
        time.sleep(POLL_INTERVAL_SECONDS)
    return False


def wait_aux_unloaded(service_id: str) -> bool:
    base = SERVICE_ENGINE_BASE_URLS.get(service_id)
    if not base:
        return False
    timeout = float(READY_TIMEOUT_SECONDS.get(service_id, 900))
    deadline = time.monotonic() + timeout
    if service_id == "ImageGeneration":
        while time.monotonic() < deadline:
            status, payload = _get_json(f"{base.rstrip('/')}/health", timeout=5.0)
            if status != 200:
                return True
            if isinstance(payload, dict):
                engine = payload.get("engine") or {}
                if engine.get("processAlive") is False:
                    return True
                if engine.get("healthy") is False and engine.get("processAlive") is not True:
                    return True
            time.sleep(POLL_INTERVAL_SECONDS)
        return False
    while time.monotonic() < deadline:
        status, _ = _get_json(f"{base.rstrip('/')}/ready", timeout=5.0)
        if status in (404, 503):
            return True
        time.sleep(POLL_INTERVAL_SECONDS)
    return False


def list_llama_models() -> list[dict[str, Any]]:
    status, payload = _get_json(f"{LLAMA_BASE_URL}/models", timeout=10.0)
    if status != 200 or not isinstance(payload, dict):
        return []
    entries = payload.get("data")
    if not isinstance(entries, list):
        return []
    return [entry for entry in entries if isinstance(entry, dict)]


def _is_llama_loaded(entry: dict[str, Any]) -> bool:
    status = entry.get("status")
    if isinstance(status, dict):
        value = status.get("value")
        if isinstance(value, str) and value.lower() == "loaded":
            return True
    state = entry.get("state")
    return isinstance(state, str) and state.lower() == "loaded"


def llama_engine_loaded_aliases() -> list[str]:
    """Mechanical probe: which router aliases the llama engine reports as loaded."""
    return [
        str(entry.get("id"))
        for entry in list_llama_models()
        if entry.get("id") and _is_llama_loaded(entry)
    ]


def aux_engine_reports_loaded(service_id: str) -> bool:
    """Mechanical probe: whether a local aux engine process is currently loaded.

    This is NOT policy. It only answers what the engine HTTP port reports right now.
    GuideAntsApi decides what should be loaded; this function is used to skip redundant
    load calls and to perform GPU drain before llama changes.
    """
    base = SERVICE_ENGINE_BASE_URLS.get(service_id)
    if not base:
        return False
    if service_id == "ImageGeneration":
        status, payload = _get_json(f"{base.rstrip('/')}/health", timeout=5.0)
        if status != 200 or not isinstance(payload, dict):
            return False
        engine = payload.get("engine") or {}
        if engine.get("processAlive") is True:
            return True
        loaded_bundle = str(payload.get("loadedBundleId") or "").strip()
        return bool(loaded_bundle)
    status, payload = _get_json(f"{base.rstrip('/')}/ready", timeout=5.0)
    if status != 200 or not isinstance(payload, dict):
        return False
    return payload.get("loaded") is True


def aux_engine_loaded_ref(service_id: str) -> str | None:
    """Return the model/bundle ref the aux engine reports, if any."""
    base = SERVICE_ENGINE_BASE_URLS.get(service_id)
    if not base:
        return None
    if service_id == "ImageGeneration":
        status, payload = _get_json(f"{base.rstrip('/')}/health", timeout=5.0)
        if status != 200 or not isinstance(payload, dict):
            return None
        engine = payload.get("engine") or {}
        bundle = str(engine.get("loadedBundleId") or payload.get("loadedBundleId") or "").strip()
        return bundle or None
    status, payload = _get_json(f"{base.rstrip('/')}/ready", timeout=5.0)
    if status != 200 or not isinstance(payload, dict) or payload.get("loaded") is not True:
        return None
    for key in ("modelRef", "model_ref", "catalogEntryId", "catalog_entry_id", "bundleId", "bundle_id"):
        value = str(payload.get(key) or "").strip()
        if value:
            return value
    return None


def post_llama_load(alias: str) -> bool:
    timeout = float(READY_TIMEOUT_SECONDS["llama"])
    status, _ = _post_json(f"{LLAMA_BASE_URL}/models/load", {"model": alias}, timeout=timeout)
    return status in (200, 201, 202, 204, 409)


def post_llama_unload(alias: str) -> bool:
    timeout = float(READY_TIMEOUT_SECONDS["llama"])
    status, _ = _post_json(f"{LLAMA_BASE_URL}/models/unload", {"model": alias}, timeout=timeout)
    return status in (200, 201, 202, 204, 409)


def wait_llama_loaded(alias: str) -> bool:
    timeout = float(READY_TIMEOUT_SECONDS["llama"])
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        for entry in list_llama_models():
            entry_id = entry.get("id")
            if isinstance(entry_id, str) and entry_id == alias and _is_llama_loaded(entry):
                return True
        time.sleep(POLL_INTERVAL_SECONDS)
    return False


def wait_llama_unloaded(alias: str) -> bool:
    timeout = float(READY_TIMEOUT_SECONDS["llama"])
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        found_loaded = False
        for entry in list_llama_models():
            entry_id = entry.get("id")
            if isinstance(entry_id, str) and entry_id == alias:
                if _is_llama_loaded(entry):
                    found_loaded = True
                    break
                return True
        if not found_loaded:
            return True
        time.sleep(POLL_INTERVAL_SECONDS)
    return False
