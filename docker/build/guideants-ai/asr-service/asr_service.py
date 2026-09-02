import json
import logging
import os
import re
import shutil
import subprocess
import tempfile
import threading
import time
import urllib.error
import urllib.request
import uuid
import contextlib
from dataclasses import dataclass
from datetime import datetime, timezone
from typing import Any

from guideants_hf.catalog_completeness import catalog_entry_for_directory_name, directory_model_entry_is_complete
from guideants_hf.catalog_download import download_catalog_entry_files, verify_required_files
from guideants_hf.engine_process import format_engine_exit_error
from guideants_hf.operations import find_in_flight_operation
from ga_blocking import await_blocking
from guideants_http.engine_failure import (
    engine_rpc_exception_is_engine_failure,
    wrapper_http_status_for_engine_exception,
)
from guideants_http.request_timeout import clamp_request_timeout_seconds, resolve_request_timeout_seconds

import soundfile as sf
import uvicorn
from fastapi import FastAPI, File, Form, HTTPException, Request, UploadFile
from fastapi.responses import JSONResponse
from pydantic import BaseModel

MODEL_PATH_RE = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")
ENGINE_MODEL_ID = "qwen3-asr"
DEFAULT_CATALOG_PATH = "/app/asr-service/catalog/manifest.json"


def utc_now_iso() -> str:
    return datetime.now(timezone.utc).isoformat()


def env_flag(name: str, default: bool = False) -> bool:
    raw = os.getenv(name)
    if raw is None:
        return default
    return raw.strip().lower() in {"1", "true", "yes", "on"}


def parse_positive_int(value: str | None, default: int) -> int:
    if not value:
        return default
    try:
        parsed = int(value)
        if parsed > 0:
            return parsed
    except ValueError:
        pass
    return default


def configure_uvicorn_access_log_filters(ignore_health_requests: bool) -> None:
    if not ignore_health_requests:
        return

    class _HealthRequestFilter(logging.Filter):
        def filter(self, record: logging.LogRecord) -> bool:
            message = record.getMessage()
            return '"/health' not in message and '"/ready' not in message

    logging.getLogger("uvicorn.access").addFilter(_HealthRequestFilter())


def log_event(event: str, **fields: Any) -> None:
    payload = {
        "event": event,
        "ts": utc_now_iso(),
    }
    payload.update(fields)
    print(json.dumps(payload, ensure_ascii=True, sort_keys=True), flush=True)


def catalog_path() -> str:
    configured = (os.getenv("CATALOG_PATH") or "").strip()
    if configured:
        return configured
    return DEFAULT_CATALOG_PATH


def load_catalog() -> dict[str, Any]:
    path = catalog_path()
    with open(path, encoding="utf-8") as handle:
        data = json.load(handle)
    entries = {entry["id"]: entry for entry in data.get("entries", []) if entry.get("task") == "asr"}
    return {"version": data.get("version", 1), "entries": entries}


CATALOG = load_catalog()


def normalize_size_bucket(size_bytes: int) -> str:
    if size_bytes < 512_000:
        return "lt_512kb"
    if size_bytes < 2_000_000:
        return "512kb_to_2mb"
    if size_bytes < 10_000_000:
        return "2mb_to_10mb"
    if size_bytes < 50_000_000:
        return "10mb_to_50mb"
    return "gte_50mb"


def get_audio_duration_seconds(path: str) -> float:
    try:
        with sf.SoundFile(path) as audio_file:
            if audio_file.samplerate > 0:
                return float(len(audio_file)) / float(audio_file.samplerate)
    except Exception:
        pass
    try:
        result = subprocess.run(
            [
                "ffprobe",
                "-v",
                "error",
                "-show_entries",
                "format=duration",
                "-of",
                "default=noprint_wrappers=1:nokey=1",
                path,
            ],
            capture_output=True,
            text=True,
            check=True,
        )
        value = result.stdout.strip()
        if value:
            return float(value)
    except Exception:
        pass
    return 0.0


def decode_audio_to_wav_16k_mono(input_path: str) -> str:
    with tempfile.NamedTemporaryFile(delete=False, suffix=".wav") as temp_file:
        output_path = temp_file.name
    subprocess.run(
        [
            "ffmpeg",
            "-y",
            "-i",
            input_path,
            "-ac",
            "1",
            "-ar",
            "16000",
            output_path,
        ],
        check=True,
        capture_output=True,
    )
    return output_path


def sanitize_for_log(value: str, max_chars: int) -> tuple[str, bool]:
    normalized = " ".join(value.split())
    if max_chars <= 0:
        max_chars = 320
    if len(normalized) <= max_chars:
        return normalized, False
    return normalized[:max_chars], True


class LoadModelRequest(BaseModel):
    model_id: str | None = None
    model_path: str | None = None
    dtype: str | None = None
    device_map: str | None = None
    max_inference_batch_size: int | None = None
    max_new_tokens: int | None = None
    hf_token: str | None = None


class DownloadModelRequest(BaseModel):
    """
    Request body for an explicit admin model download.

    ``hf_token`` is the single server-resolved token from the top-level
    ``HuggingFace:Token`` application setting, stamped in by the .NET web
    layer. This service does not consult ``HF_TOKEN`` env directly; whatever
    the web API passes is the one token used for every HF call.
    """
    model_id: str
    revision: str | None = None
    hf_token: str | None = None


@dataclass
class AsrEngineConfig:
    server_path: str
    model_dir: str
    model_path: str
    model_ref: str
    catalog_entry_id: str | None
    engine_host: str
    engine_port: int
    engine_base_url: str
    engine_ready_timeout_seconds: int
    request_timeout_seconds: int
    weight_type: str | None
    config_path: str
    device: int
    threads: int


class AsrRuntimeState:
    def __init__(self) -> None:
        self.lock = threading.RLock()
        self.engine_process: subprocess.Popen[Any] | None = None
        self.config: AsrEngineConfig | None = None
        self.model_ref: str | None = None
        self.catalog_entry_id: str | None = None
        self.loaded_at_utc: str | None = None
        self.loading: bool = False
        self.load_error: str | None = None
        self.warmup_enabled: bool = env_flag("GA_ASR_WARMUP_ON_LOAD", default=True)
        self.warmup_ran: bool = False
        self.warmup_succeeded: bool = False
        self.warmup_latency_ms: int = 0
        self.warmup_error: str | None = None
        self.warmup_audio_path: str | None = None
        self.warmup_completed_at_utc: str | None = None
        self.inference_failed: bool = False
        self.inference_failed_reason: str | None = None
        self.inference_failed_at_utc: str | None = None

    def is_loaded(self) -> bool:
        with self.lock:
            return self.config is not None and is_engine_process_alive()

    def snapshot(self) -> dict[str, Any]:
        with self.lock:
            return {
                "loaded": self.config is not None and is_engine_process_alive(),
                "loading": self.loading,
                "modelRef": self.model_ref,
                "loadedAtUtc": self.loaded_at_utc,
                "loadError": self.load_error,
                "warmupEnabled": self.warmup_enabled,
                "warmupRan": self.warmup_ran,
                "warmupSucceeded": self.warmup_succeeded,
                "warmupLatencyMs": self.warmup_latency_ms,
                "warmupError": self.warmup_error,
                "warmupAudioPath": self.warmup_audio_path,
                "warmupCompletedAtUtc": self.warmup_completed_at_utc,
                "engineAlive": is_engine_process_alive(),
                "enginePid": self.engine_process.pid if self.engine_process is not None else None,
                "busy": TRANSCRIBE_IN_FLIGHT > 0 or self.loading or MODEL_LOAD_LOCK.locked(),
                "inFlightTranscriptions": TRANSCRIBE_IN_FLIGHT,
                "oldestInFlightAgeMs": oldest_in_flight_age_ms(),
                "failed": self.inference_failed,
                "failedReason": self.inference_failed_reason,
                "failedAtUtc": self.inference_failed_at_utc,
            }


STATE = AsrRuntimeState()
APP = FastAPI(title="GuideAnts ASR Service", version="2.0.0")
MODEL_LOAD_LOCK = threading.Lock()
MODEL_OPS_LOCK = threading.Lock()
MODEL_DOWNLOAD_OPERATIONS: dict[str, dict[str, Any]] = {}
TRANSCRIBE_IN_FLIGHT = 0
TRANSCRIBE_IN_FLIGHT_LOCK = threading.Lock()
TRANSCRIBE_IN_FLIGHT_STARTED: list[float] = []


def is_engine_process_alive() -> bool:
    process = STATE.engine_process
    return process is not None and process.poll() is None


def oldest_in_flight_age_ms() -> int | None:
    with TRANSCRIBE_IN_FLIGHT_LOCK:
        if not TRANSCRIBE_IN_FLIGHT_STARTED:
            return None
        oldest = min(TRANSCRIBE_IN_FLIGHT_STARTED)
    return int((time.monotonic() - oldest) * 1000)


def heartbeat_interval_seconds() -> float:
    raw = os.getenv("GA_ASR_HEARTBEAT_SECONDS")
    if not raw:
        return 10.0
    try:
        parsed = float(raw)
    except ValueError:
        return 10.0
    return parsed if parsed > 0 else 10.0


def run_with_sync_heartbeat(event: str, fields: dict[str, Any], work: Any) -> Any:
    started = time.perf_counter()
    interval = heartbeat_interval_seconds()
    stop = threading.Event()

    def _beat() -> None:
        while not stop.wait(interval):
            log_event(
                event,
                elapsedMs=int((time.perf_counter() - started) * 1000),
                inFlight=TRANSCRIBE_IN_FLIGHT,
                **fields,
            )

    thread = threading.Thread(target=_beat, name=f"{event}-heartbeat", daemon=True)
    thread.start()
    try:
        return work()
    finally:
        stop.set()
        thread.join(timeout=1.0)


def mark_inference_failed(reason: str) -> None:
    with STATE.lock:
        STATE.inference_failed = True
        STATE.inference_failed_reason = reason
        STATE.inference_failed_at_utc = utc_now_iso()
    log_event(
        "asr_engine_failed",
        reason=reason,
        modelRef=STATE.model_ref,
        enginePid=STATE.engine_process.pid if STATE.engine_process is not None else None,
        inFlightTranscriptions=TRANSCRIBE_IN_FLIGHT,
        oldestInFlightAgeMs=oldest_in_flight_age_ms(),
    )


def clear_inference_failed() -> None:
    with STATE.lock:
        STATE.inference_failed = False
        STATE.inference_failed_reason = None
        STATE.inference_failed_at_utc = None


@contextlib.contextmanager
def track_transcription() -> Any:
    global TRANSCRIBE_IN_FLIGHT
    started = time.monotonic()
    with TRANSCRIBE_IN_FLIGHT_LOCK:
        TRANSCRIBE_IN_FLIGHT += 1
        TRANSCRIBE_IN_FLIGHT_STARTED.append(started)
    try:
        yield
    finally:
        with TRANSCRIBE_IN_FLIGHT_LOCK:
            TRANSCRIBE_IN_FLIGHT = max(0, TRANSCRIBE_IN_FLIGHT - 1)
            try:
                TRANSCRIBE_IN_FLIGHT_STARTED.remove(started)
            except ValueError:
                pass


def get_model_dir() -> str:
    return os.getenv("GA_ASR_MODEL_DIR", "/models-local/asr")


def resolve_catalog_entry(model_id: str) -> dict[str, Any]:
    normalized = (model_id or "").strip()
    entry = CATALOG["entries"].get(normalized)
    if entry is not None:
        return entry
    for candidate in CATALOG["entries"].values():
        for source in candidate.get("sourceRepos", []):
            if source.get("repoId") == normalized:
                return candidate
    raise ValueError(
        f"model_id '{model_id}' is not in the curated ASR catalog. "
        "Only manifest entries are allowed."
    )


def catalog_target_path(entry: dict[str, Any]) -> str:
    model_dir = get_model_dir()
    target_directory = (entry.get("targetDirectory") or "").strip()
    if not target_directory:
        raise ValueError(f"Catalog entry '{entry.get('id')}' is missing targetDirectory.")
    return os.path.join(model_dir, target_directory)


def resolve_server_path() -> str:
    configured = (os.getenv("GA_ASR_SERVER_PATH") or "").strip()
    if configured:
        return configured if os.path.isabs(configured) else (shutil.which(configured) or configured)
    default_path = "/usr/local/bin/audiocpp_server"
    if os.path.isfile(default_path):
        return default_path
    discovered = shutil.which("audiocpp_server")
    if discovered:
        return discovered
    raise RuntimeError("audiocpp_server binary not found; set GA_ASR_SERVER_PATH.")


def resolve_weight_type() -> str | None:
    value = (os.getenv("GA_ASR_WEIGHT_TYPE") or "").strip()
    return value or None


def resolve_path_within_model_dir(model_dir: str, requested: str) -> str:
    if not MODEL_PATH_RE.fullmatch(requested):
        raise ValueError("model_path must be a simple local model name (letters, digits, dot, underscore, hyphen).")
    base_real = os.path.realpath(model_dir)
    candidate = os.path.realpath(os.path.join(base_real, requested))
    if not candidate.startswith(base_real + os.sep):
        raise ValueError("resolved model_path escapes the permitted model directory.")
    return candidate


def resolve_model_target(request: LoadModelRequest) -> tuple[str, str, dict[str, Any] | None]:
    model_dir = get_model_dir()
    os.makedirs(model_dir, exist_ok=True)

    if request.model_path:
        requested = request.model_path.strip()
        candidate = resolve_path_within_model_dir(model_dir, requested)
        if not os.path.exists(candidate):
            raise FileNotFoundError(
                f"Configured model_path '{requested}' does not exist under GA_ASR_MODEL_DIR."
            )
        for entry in CATALOG["entries"].values():
            target_directory = entry.get("targetDirectory")
            if target_directory and (
                requested == target_directory or os.path.basename(candidate) == target_directory
            ):
                return candidate, requested, entry
        return candidate, requested, None

    if request.model_id:
        model_id = request.model_id.strip()
        try:
            entry = resolve_catalog_entry(model_id)
            candidate = catalog_target_path(entry)
            if not os.path.exists(candidate):
                raise FileNotFoundError(
                    f"Catalog model '{entry['id']}' expects '{entry.get('targetDirectory')}' under {model_dir}. "
                    "Download it first."
                )
            return candidate, entry.get("targetDirectory") or os.path.basename(candidate), entry
        except ValueError:
            leaf = model_id.split("/")[-1].strip()
            if not leaf:
                raise
            candidate = resolve_path_within_model_dir(model_dir, leaf)
            if os.path.exists(candidate):
                return candidate, leaf, None
            raise

    default_model_path = os.getenv("GA_ASR_DEFAULT_MODEL_PATH", "").strip()
    if default_model_path:
        candidate = default_model_path
        if not os.path.isabs(candidate):
            candidate = os.path.join(model_dir, candidate)
        if os.path.exists(candidate):
            for entry in CATALOG["entries"].values():
                if entry.get("targetDirectory") == os.path.basename(candidate):
                    return os.path.realpath(candidate), os.path.basename(candidate), entry
            return os.path.realpath(candidate), os.path.basename(candidate), None

    default_model_id = os.getenv("GA_ASR_DEFAULT_MODEL_ID", "Qwen/Qwen3-ASR-0.6B").strip()
    try:
        entry = resolve_catalog_entry(default_model_id)
        candidate = catalog_target_path(entry)
        if os.path.exists(candidate):
            return candidate, entry.get("targetDirectory") or os.path.basename(candidate), entry
    except ValueError:
        pass

    leaf = default_model_id.split("/")[-1].strip()
    candidate = os.path.join(model_dir, leaf)
    if os.path.exists(candidate):
        return os.path.realpath(candidate), leaf, None

    raise FileNotFoundError(
        f"No ASR model found under {model_dir}. Download a catalog model or set GA_ASR_DEFAULT_MODEL_PATH."
    )


def resolve_engine_family(entry: dict[str, Any] | None) -> str:
    """The audiocpp_server loader family for the model being loaded.

    Authoritative source is the catalog entry's `family` (matches the loader
    names registered in audio.cpp, e.g. qwen3_asr / citrinet_asr). For a bare
    local directory with no catalog entry we honour GA_ASR_ENGINE_FAMILY and
    otherwise assume the default qwen3_asr family, logging that assumption so it
    is never silent.
    """
    if entry is not None:
        family = (entry.get("family") or "").strip()
        if not family:
            raise ValueError(f"ASR catalog entry '{entry.get('id')}' is missing family.")
        return family
    override = (os.getenv("GA_ASR_ENGINE_FAMILY") or "").strip()
    if override:
        return override
    log_event("asr_engine_family_assumed", family="qwen3_asr", reason="no_catalog_entry")
    return "qwen3_asr"


def build_server_config_json(
    model_path: str,
    engine_host: str,
    engine_port: int,
    entry: dict[str, Any] | None,
) -> dict[str, Any]:
    weight_type = resolve_weight_type()
    family = resolve_engine_family(entry)
    model_entry: dict[str, Any] = {
        "id": ENGINE_MODEL_ID,
        "family": family,
        "path": model_path,
        "task": "asr",
        "mode": "offline",
    }
    if weight_type:
        # Weight-type override is family-scoped in audio.cpp (e.g.
        # citrinet_asr.weight_type / qwen3_asr.weight_type).
        model_entry["session_options"] = {f"{family}.weight_type": weight_type}

    return {
        "host": engine_host,
        "port": engine_port,
        # audiocpp_server >= "Allow server backend selection" (2026-07-02) reads this
        # field to pick engine::core::BackendType; defaults to "cuda" to match the
        # binary's own default. Must match the ENGINE_ENABLE_* flags baked into this
        # image's audiocpp_server build (see Dockerfile.<flavor>), otherwise every
        # model load fails at runtime with a backend-mismatch error.
        "backend": (os.getenv("GA_ASR_BACKEND") or "cuda").strip() or "cuda",
        "device": parse_positive_int(os.getenv("GA_ASR_DEVICE"), 0),
        "threads": parse_positive_int(os.getenv("GA_ASR_THREADS"), 1),
        "lazy_load": False,
        "models": [model_entry],
    }


def write_engine_config_file(
    model_path: str,
    engine_host: str,
    engine_port: int,
    entry: dict[str, Any] | None,
) -> str:
    config_dir = os.path.join(get_model_dir(), ".engine-config")
    os.makedirs(config_dir, exist_ok=True)
    config_path = os.path.join(config_dir, "server.json")
    payload = build_server_config_json(model_path, engine_host, engine_port, entry)
    with open(config_path, "w", encoding="utf-8") as handle:
        json.dump(payload, handle, ensure_ascii=True, indent=2)
        handle.write("\n")
    return config_path


def build_engine_config(model_path: str, model_ref: str, entry: dict[str, Any] | None) -> AsrEngineConfig:
    engine_host = os.getenv("GA_ASR_ENGINE_HOST", "127.0.0.1")
    engine_port = parse_positive_int(os.getenv("GA_ASR_ENGINE_PORT"), 18082)
    config_path = write_engine_config_file(model_path, engine_host, engine_port, entry)
    return AsrEngineConfig(
        server_path=resolve_server_path(),
        model_dir=get_model_dir(),
        model_path=model_path,
        model_ref=model_ref,
        catalog_entry_id=entry.get("id") if entry else None,
        engine_host=engine_host,
        engine_port=engine_port,
        engine_base_url=f"http://{engine_host}:{engine_port}",
        engine_ready_timeout_seconds=parse_positive_int(
            os.getenv("GA_ASR_ENGINE_READY_TIMEOUT_SECONDS"),
            parse_positive_int(os.getenv("GA_ASR_READY_TIMEOUT_SECONDS"), 1800),
        ),
        request_timeout_seconds=parse_positive_int(os.getenv("GA_ASR_TIMEOUT_SECONDS"), 300),
        weight_type=resolve_weight_type(),
        config_path=config_path,
        device=parse_positive_int(os.getenv("GA_ASR_DEVICE"), 0),
        threads=parse_positive_int(os.getenv("GA_ASR_THREADS"), 1),
    )


def perform_http_request(
    method: str,
    url: str,
    timeout_seconds: int,
    headers: dict[str, str] | None = None,
    body: bytes | None = None,
) -> tuple[int, bytes]:
    request = urllib.request.Request(url=url, data=body, method=method)
    for name, value in (headers or {}).items():
        request.add_header(name, value)
    try:
        with urllib.request.urlopen(request, timeout=timeout_seconds) as response:
            return int(response.status), response.read()
    except urllib.error.HTTPError as exc:
        return int(exc.code), exc.read()
    except urllib.error.URLError as exc:
        reason = getattr(exc, "reason", exc)
        raise RuntimeError(f"Failed to reach audiocpp_server at {url}: {reason}") from exc


def engine_json_request(
    config: AsrEngineConfig,
    method: str,
    path: str,
    timeout_seconds: int,
    payload: dict[str, Any] | None = None,
) -> tuple[int, dict[str, Any]]:
    headers: dict[str, str] = {"Accept": "application/json"}
    body: bytes | None = None
    if payload is not None:
        headers["Content-Type"] = "application/json"
        body = json.dumps(payload, ensure_ascii=True, separators=(",", ":")).encode("utf-8")
    status_code, response_body = perform_http_request(
        method=method,
        url=f"{config.engine_base_url}{path}",
        timeout_seconds=timeout_seconds,
        headers=headers,
        body=body,
    )
    if not response_body:
        return status_code, {}
    parsed = json.loads(response_body.decode("utf-8"))
    if not isinstance(parsed, dict):
        raise RuntimeError(f"Unexpected JSON from audiocpp_server {method} {path}")
    return status_code, parsed


def wait_for_engine_ready(config: AsrEngineConfig) -> None:
    deadline = time.monotonic() + config.engine_ready_timeout_seconds
    while time.monotonic() < deadline:
        if not is_engine_process_alive():
            process = STATE.engine_process
            exit_code = process.poll() if process is not None else None
            raise RuntimeError(format_engine_exit_error(process, exit_code))
        try:
            status_code, _ = engine_json_request(
                config, "GET", "/health", min(5, config.request_timeout_seconds)
            )
            if status_code == 200:
                return
        except Exception:
            pass
        time.sleep(0.5)
    raise RuntimeError(
        f"Timed out waiting for audiocpp_server readiness after {config.engine_ready_timeout_seconds}s."
    )


def stop_engine_process() -> None:
    process = STATE.engine_process
    STATE.engine_process = None
    if process is None:
        return
    if process.poll() is not None:
        return
    process.terminate()
    try:
        process.wait(timeout=15)
    except subprocess.TimeoutExpired:
        process.kill()
        process.wait(timeout=5)


def stop_engine() -> None:
    stop_engine_process()
    STATE.config = None
    STATE.model_ref = None
    STATE.catalog_entry_id = None
    STATE.loaded_at_utc = None
    STATE.load_error = None
    STATE.warmup_ran = False
    STATE.warmup_succeeded = False
    STATE.warmup_latency_ms = 0
    STATE.warmup_error = None
    STATE.warmup_audio_path = None
    STATE.warmup_completed_at_utc = None
    STATE.inference_failed = False
    STATE.inference_failed_reason = None
    STATE.inference_failed_at_utc = None


def restart_engine_on_failure_enabled() -> bool:
    return env_flag("GA_ASR_RESTART_ON_FAILURE", default=True)


def transcription_error_is_recoverable(exc: BaseException) -> bool:
    return engine_rpc_exception_is_engine_failure(exc)


def _restart_engine_process_locked(config: AsrEngineConfig, *, reason: str) -> None:
    """Kill and respawn audiocpp_server. Caller must hold MODEL_LOAD_LOCK."""
    previous = STATE.engine_process
    previous_pid = previous.pid if previous is not None else None
    log_event(
        "asr_engine_restart_start",
        modelRef=config.model_ref,
        reason=reason,
        previousPid=previous_pid,
    )
    stop_engine_process()
    command = [config.server_path, "--config", config.config_path]
    STATE.engine_process = subprocess.Popen(
        command,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.PIPE,
        env=os.environ.copy(),
    )
    wait_for_engine_ready(config)
    clear_inference_failed()
    log_event(
        "asr_engine_restart_success",
        modelRef=config.model_ref,
        reason=reason,
        previousPid=previous_pid,
        enginePid=STATE.engine_process.pid if STATE.engine_process is not None else None,
    )


def restart_engine_process(config: AsrEngineConfig, *, reason: str) -> None:
    with MODEL_LOAD_LOCK:
        _restart_engine_process_locked(config, reason=reason)


def transcribe_via_engine_with_recovery(
    config: AsrEngineConfig,
    audio_path: str,
    language: str | None = None,
    request_timeout_seconds: int | None = None,
) -> tuple[str, str | None]:
    try:
        return transcribe_via_engine(
            config,
            audio_path,
            language,
            request_timeout_seconds=request_timeout_seconds,
        )
    except Exception as exc:
        if not engine_rpc_exception_is_engine_failure(exc):
            raise
        mark_inference_failed(f"{type(exc).__name__}: {exc}")
        if not restart_engine_on_failure_enabled():
            raise
        log_event(
            "asr_transcribe_recovering",
            modelRef=config.model_ref,
            errorType=type(exc).__name__,
            error=str(exc),
        )
        with MODEL_LOAD_LOCK:
            _restart_engine_process_locked(
                config,
                reason=f"transcription_failure:{type(exc).__name__}",
            )
        return transcribe_via_engine(
            config,
            audio_path,
            language,
            request_timeout_seconds=request_timeout_seconds,
        )


def transcribe_via_engine(
    config: AsrEngineConfig,
    audio_path: str,
    language: str | None = None,
    request_timeout_seconds: int | None = None,
) -> tuple[str, str | None]:
    payload: dict[str, Any] = {
        "model": ENGINE_MODEL_ID,
        "audio": os.path.abspath(audio_path),
    }
    if language:
        payload["language"] = language
    effective_timeout = clamp_request_timeout_seconds(
        request_timeout_seconds or config.request_timeout_seconds
    )
    status_code, parsed = engine_json_request(
        config,
        "POST",
        "/v1/audio/transcriptions",
        effective_timeout,
        payload=payload,
    )
    if status_code != 200:
        detail = parsed.get("error") if isinstance(parsed.get("error"), str) else None
        if detail is None and isinstance(parsed.get("error"), dict):
            nested = parsed["error"].get("message")
            detail = str(nested) if nested is not None else None
        suffix = f": {detail}" if detail else ""
        raise RuntimeError(
            f"audiocpp_server /v1/audio/transcriptions returned HTTP {status_code}{suffix}."
        )
    text = str(parsed.get("text") or "")
    detected_language = parsed.get("language")
    if detected_language is not None:
        detected_language = str(detected_language)
    return text, detected_language


def run_model_warmup(config: AsrEngineConfig, model_ref: str) -> dict[str, Any]:
    if not env_flag("GA_ASR_WARMUP_ON_LOAD", default=True):
        return {
            "warmupEnabled": False,
            "warmupRan": False,
            "warmupSucceeded": False,
            "warmupLatencyMs": 0,
        }

    warmup_audio_path = os.getenv("GA_ASR_WARMUP_AUDIO_PATH", "/app/asr-service/warmup.webm").strip()
    language = (os.getenv("GA_ASR_WARMUP_LANGUAGE") or "").strip() or None
    if not warmup_audio_path:
        warmup_audio_path = "/app/asr-service/warmup.webm"
    started = time.perf_counter()
    decoded_path = ""

    log_event(
        "asr_model_warmup_start",
        modelRef=model_ref,
        warmupAudioPath=warmup_audio_path,
        languageHint=language,
    )

    try:
        if not os.path.exists(warmup_audio_path):
            fallback_path = "/app/asr-service/warmup.wav"
            if warmup_audio_path != fallback_path and os.path.exists(fallback_path):
                log_event(
                    "asr_model_warmup_audio_fallback",
                    configuredWarmupAudioPath=warmup_audio_path,
                    fallbackWarmupAudioPath=fallback_path,
                )
                warmup_audio_path = fallback_path
            else:
                raise FileNotFoundError(f"Warmup audio file not found: {warmup_audio_path}")

        decoded_path = decode_audio_to_wav_16k_mono(warmup_audio_path)
        warmup_log_max_chars = int(os.getenv("GA_ASR_WARMUP_LOG_TEXT_MAX_CHARS", "320"))
        warmup_text_raw, detected_language = transcribe_via_engine(config, decoded_path, language)
        warmup_text, warmup_text_truncated = sanitize_for_log(warmup_text_raw, warmup_log_max_chars)
        latency_ms = int((time.perf_counter() - started) * 1000)

        log_event(
            "asr_model_warmup_success",
            modelRef=model_ref,
            warmupLatencyMs=latency_ms,
            warmupAudioPath=warmup_audio_path,
            warmupText=warmup_text,
            warmupTextLength=len(warmup_text_raw),
            warmupTextTruncated=warmup_text_truncated,
            detectedLanguage=detected_language,
            warmupCompletedAtUtc=utc_now_iso(),
        )
        return {
            "warmupEnabled": True,
            "warmupRan": True,
            "warmupSucceeded": True,
            "warmupLatencyMs": latency_ms,
            "warmupAudioPath": warmup_audio_path,
            "warmupText": warmup_text,
            "warmupTextLength": len(warmup_text_raw),
            "warmupTextTruncated": warmup_text_truncated,
            "detectedLanguage": detected_language,
            "warmupCompletedAtUtc": utc_now_iso(),
        }
    except Exception as exc:
        latency_ms = int((time.perf_counter() - started) * 1000)
        log_event(
            "asr_model_warmup_failed",
            modelRef=model_ref,
            warmupLatencyMs=latency_ms,
            warmupAudioPath=warmup_audio_path,
            errorType=type(exc).__name__,
            error=str(exc),
        )
        return {
            "warmupEnabled": True,
            "warmupRan": True,
            "warmupSucceeded": False,
            "warmupLatencyMs": latency_ms,
            "warmupError": str(exc),
            "warmupAudioPath": warmup_audio_path,
        }
    finally:
        if decoded_path and os.path.exists(decoded_path):
            os.remove(decoded_path)


def unload_model() -> dict[str, Any]:
    """
    Stop the upstream engine process and clear the runtime snapshot fields.
    Caller must hold MODEL_LOAD_LOCK. Safe to call when the engine is already unloaded.
    """
    with STATE.lock:
        had_model = STATE.config is not None
        previous_ref = STATE.model_ref
    stop_engine()
    return {"wasLoaded": had_model, "previousModelRef": previous_ref}


def start_engine(request: LoadModelRequest) -> dict[str, Any]:
    model_path, model_ref, entry = resolve_model_target(request)
    config = build_engine_config(model_path, model_ref, entry)

    if (
        STATE.config
        and STATE.config.model_path == model_path
        and is_engine_process_alive()
        and not STATE.inference_failed
    ):
        return {
            "modelRef": STATE.model_ref,
            "loadedAtUtc": STATE.loaded_at_utc,
            "action": "noop-already-loaded",
        }

    stop_engine()
    command = [config.server_path, "--config", config.config_path]
    log_event("asr_engine_start", command=command, modelRef=model_ref, configPath=config.config_path)
    init_started = time.perf_counter()
    STATE.engine_process = subprocess.Popen(
        command,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.PIPE,
        env=os.environ.copy(),
    )
    wait_for_engine_ready(config)
    warmup_details = run_model_warmup(config, model_ref)
    if warmup_details["warmupEnabled"] and not warmup_details["warmupSucceeded"]:
        stop_engine()
        warmup_error = warmup_details.get("warmupError", "ASR warmup failed.")
        raise RuntimeError(str(warmup_error))
    init_latency_ms = int((time.perf_counter() - init_started) * 1000)

    with STATE.lock:
        STATE.config = config
        STATE.model_ref = model_ref
        STATE.catalog_entry_id = entry.get("id") if entry else None
        STATE.loaded_at_utc = utc_now_iso()
        STATE.warmup_enabled = bool(warmup_details.get("warmupEnabled", False))
        STATE.warmup_ran = bool(warmup_details.get("warmupRan", False))
        STATE.warmup_succeeded = bool(warmup_details.get("warmupSucceeded", False))
        STATE.warmup_latency_ms = int(warmup_details.get("warmupLatencyMs", 0) or 0)
        STATE.warmup_error = warmup_details.get("warmupError")
        STATE.warmup_audio_path = warmup_details.get("warmupAudioPath")
        STATE.warmup_completed_at_utc = warmup_details.get("warmupCompletedAtUtc")
        STATE.inference_failed = False
        STATE.inference_failed_reason = None
        STATE.inference_failed_at_utc = None

    return {
        "modelRef": model_ref,
        "loadedAtUtc": STATE.loaded_at_utc,
        "loadLatencyMs": init_latency_ms,
        **warmup_details,
    }


def load_model_serialized(request: LoadModelRequest) -> dict[str, Any]:
    with MODEL_LOAD_LOCK:
        with STATE.lock:
            STATE.loading = True
            STATE.load_error = None
        try:
            hf_token = (request.hf_token or "").strip()
            if hf_token:
                os.environ["HF_TOKEN"] = hf_token
            return start_engine(request)
        except Exception as exc:
            with STATE.lock:
                STATE.load_error = str(exc)
            raise
        finally:
            with STATE.lock:
                STATE.loading = False


def list_model_entries() -> list[dict[str, Any]]:
    model_dir = get_model_dir()
    os.makedirs(model_dir, exist_ok=True)
    active_ref = STATE.snapshot().get("modelRef")
    items: list[dict[str, Any]] = []
    for name in sorted(os.listdir(model_dir)):
        if name.startswith("."):
            continue
        full_path = os.path.join(model_dir, name)
        try:
            size_bytes = os.path.getsize(full_path) if os.path.isfile(full_path) else 0
        except OSError:
            size_bytes = 0
        catalog_entry = catalog_entry_for_directory_name(name, CATALOG["entries"])
        items.append(
            {
                "modelRef": name,
                "path": full_path,
                "isDirectory": os.path.isdir(full_path),
                "sizeBytes": size_bytes,
                "complete": directory_model_entry_is_complete(full_path, catalog_entry),
                "active": bool(active_ref and (active_ref == name or active_ref == full_path)),
            }
        )
    return items


def canonical_model_folder_name(model_id: str) -> str:
    entry = resolve_catalog_entry(model_id)
    target_directory = (entry.get("targetDirectory") or "").strip()
    if target_directory:
        return target_directory
    normalized = (model_id or "").strip().strip("/")
    leaf = normalized.split("/")[-1].strip()
    if not leaf:
        raise ValueError("model_id must include a repository name")
    return leaf


def _status_is_terminal(status: str | None) -> bool:
    normalized = (status or "").strip().lower()
    return normalized in {"completed", "failed", "error", "cancelled", "canceled"}


def start_download_operation(request: DownloadModelRequest) -> dict[str, Any]:
    entry = resolve_catalog_entry(request.model_id)
    target_name = canonical_model_folder_name(request.model_id)

    operation_id = uuid.uuid4().hex
    operation = {
        "operationId": operation_id,
        "status": "queued",
        "modelId": request.model_id,
        "error": None,
        "modelRef": target_name,
        "cancelRequested": False,
        "startedAtUtc": utc_now_iso(),
        "completedAtUtc": None,
    }
    with MODEL_OPS_LOCK:
        MODEL_DOWNLOAD_OPERATIONS[operation_id] = operation

    def _run() -> None:
        model_dir = get_model_dir()
        os.makedirs(model_dir, exist_ok=True)
        target_path = os.path.join(model_dir, target_name)
        with MODEL_OPS_LOCK:
            current = MODEL_DOWNLOAD_OPERATIONS.get(operation_id)
            if current is None:
                return
            if current.get("cancelRequested"):
                current["status"] = "cancelled"
                current["error"] = "Cancelled by operator."
                current["completedAtUtc"] = utc_now_iso()
                return
            current["status"] = "running"
        try:
            hf_token = (request.hf_token or "").strip() or None
            download_catalog_entry_files(
                entry,
                target_path,
                hf_token,
                revision_override=request.revision,
            )
            verify_required_files(target_path, entry)
            with MODEL_OPS_LOCK:
                current = MODEL_DOWNLOAD_OPERATIONS.get(operation_id)
                if current is None:
                    return
                if current.get("cancelRequested"):
                    if os.path.isdir(target_path):
                        shutil.rmtree(target_path, ignore_errors=True)
                    current["status"] = "cancelled"
                    current["error"] = "Cancelled by operator."
                    current["completedAtUtc"] = utc_now_iso()
                    return
                current["status"] = "completed"
                current["modelRef"] = target_name
                current["completedAtUtc"] = utc_now_iso()
        except Exception as exc:
            with MODEL_OPS_LOCK:
                current = MODEL_DOWNLOAD_OPERATIONS.get(operation_id)
                if current is None:
                    return
                if current.get("cancelRequested"):
                    current["status"] = "cancelled"
                    current["error"] = "Cancelled by operator."
                else:
                    current["status"] = "failed"
                    current["error"] = str(exc)
                current["completedAtUtc"] = utc_now_iso()

    threading.Thread(target=_run, daemon=True).start()
    return operation


@APP.on_event("startup")
async def on_startup() -> None:
    startup_details = {
        "host": os.getenv("GA_ASR_HOST", "127.0.0.1"),
        "port": int(os.getenv("GA_ASR_PORT", "8082")),
        "modelDir": get_model_dir(),
        "engineHost": os.getenv("GA_ASR_ENGINE_HOST", "127.0.0.1"),
        "enginePort": parse_positive_int(os.getenv("GA_ASR_ENGINE_PORT"), 18082),
        "catalogVersion": CATALOG.get("version"),
    }
    log_event("asr_service_startup", **startup_details)


@APP.on_event("shutdown")
async def on_shutdown() -> None:
    with MODEL_LOAD_LOCK:
        stop_engine()


@APP.get("/health")
async def health() -> dict[str, Any]:
    snapshot = STATE.snapshot()
    return {"status": "ok", **snapshot}


@APP.get("/ready")
async def ready() -> JSONResponse:
    snapshot = STATE.snapshot()
    if not snapshot["loaded"]:
        return JSONResponse(status_code=503, content={"ready": False, **snapshot})
    if snapshot.get("warmupEnabled") and not snapshot.get("warmupSucceeded"):
        return JSONResponse(
            status_code=503,
            content={
                "ready": False,
                "message": "ASR model loaded but representative warmup is incomplete.",
                **snapshot,
            },
        )
    if snapshot.get("failed"):
        return JSONResponse(
            status_code=503,
            content={
                "ready": False,
                "message": "ASR engine is in a failed state and must be recycled.",
                **snapshot,
            },
        )
    return JSONResponse(status_code=200, content={"ready": True, **snapshot})


@APP.get("/admin/catalog")
async def admin_catalog() -> JSONResponse:
    entries = [
        {
            "id": entry["id"],
            "displayName": entry.get("displayName"),
            "license": entry.get("license"),
            "multilingual": entry.get("multilingual"),
            "default": entry.get("default", False),
        }
        for entry in CATALOG["entries"].values()
    ]
    return JSONResponse(status_code=200, content={"version": CATALOG["version"], "entries": entries})


@APP.post("/admin/load")
async def admin_load(request: Request, payload: LoadModelRequest) -> JSONResponse:
    request_id = request.headers.get("x-request-id", str(uuid.uuid4()))
    log_event("asr_model_load_start", requestId=request_id, payload=payload.model_dump())
    try:
        details = await await_blocking(load_model_serialized, payload)
        log_event("asr_model_load_success", requestId=request_id, **details)
        return JSONResponse(status_code=200, content={"requestId": request_id, "status": "loaded", **details})
    except Exception as exc:
        log_event("asr_model_load_failed", requestId=request_id, errorType=type(exc).__name__, error=str(exc))
        return JSONResponse(
            status_code=500,
            content={
                "requestId": request_id,
                "status": "failed",
                "error": "model_load_failed",
                "message": "Model load failed. Check service logs for details.",
            },
        )


@APP.post("/admin/unload")
async def admin_unload(request: Request) -> JSONResponse:
    """
    Stop the upstream ASR engine so the container releases GPU/RAM without a
    restart. Serialized with /admin/load via MODEL_LOAD_LOCK. If a load is
    already in flight, this returns 409 rather than blocking a worker.
    """
    request_id = request.headers.get("x-request-id", str(uuid.uuid4()))
    if not MODEL_LOAD_LOCK.acquire(blocking=False):
        return JSONResponse(
            status_code=409,
            content={
                "requestId": request_id,
                "ok": False,
                "error": "model lifecycle operation already in progress",
                **STATE.snapshot(),
            },
        )
    try:
        if not STATE.is_loaded():
            log_event("asr_model_unload_noop", requestId=request_id)
            return JSONResponse(
                status_code=200,
                content={
                    "requestId": request_id,
                    "ok": True,
                    "action": "noop-already-unloaded",
                    **STATE.snapshot(),
                },
            )
        log_event("asr_model_unload_start", requestId=request_id)
        result = unload_model()
        log_event(
            "asr_model_unload_success",
            requestId=request_id,
            previousModelRef=result.get("previousModelRef"),
        )
        return JSONResponse(
            status_code=200,
            content={
                "requestId": request_id,
                "ok": True,
                "action": "unloaded",
                "previousModelRef": result.get("previousModelRef"),
                **STATE.snapshot(),
            },
        )
    finally:
        MODEL_LOAD_LOCK.release()


class ReportEngineFailureRequest(BaseModel):
    reason: str
    requestId: str | None = None


@APP.post("/admin/report-engine-failure")
async def admin_report_engine_failure(
    request: Request,
    payload: ReportEngineFailureRequest,
) -> JSONResponse:
    """Mark the engine failed and recycle the process. GPU work does not stop on HTTP timeout."""
    request_id = payload.requestId or request.headers.get("x-request-id", str(uuid.uuid4()))
    reason = (payload.reason or "").strip() or "reported_engine_failure"
    mark_inference_failed(reason)
    config = STATE.config
    if config is None or not restart_engine_on_failure_enabled():
        return JSONResponse(
            status_code=200,
            content={"requestId": request_id, "ok": True, "action": "marked_failed", **STATE.snapshot()},
        )
    try:
        restart_engine_process(config, reason=reason)
        return JSONResponse(
            status_code=200,
            content={"requestId": request_id, "ok": True, "action": "restarted", **STATE.snapshot()},
        )
    except Exception as exc:
        log_event(
            "asr_engine_restart_failed",
            requestId=request_id,
            reason=reason,
            errorType=type(exc).__name__,
            error=str(exc),
        )
        return JSONResponse(
            status_code=500,
            content={
                "requestId": request_id,
                "ok": False,
                "error": "engine_restart_failed",
                "message": str(exc),
                **STATE.snapshot(),
            },
        )


@APP.get("/admin/models")
async def admin_list_models() -> JSONResponse:
    return JSONResponse(
        status_code=200,
        content={
            "modelDir": get_model_dir(),
            "items": list_model_entries(),
        },
    )


@APP.post("/admin/models/download")
async def admin_download_model(payload: DownloadModelRequest) -> JSONResponse:
    if not payload.model_id.strip():
        raise HTTPException(status_code=400, detail="model_id is required")
    try:
        resolve_catalog_entry(payload.model_id)
    except ValueError as exc:
        raise HTTPException(status_code=400, detail=str(exc)) from exc
    with MODEL_OPS_LOCK:
        existing = find_in_flight_operation(MODEL_DOWNLOAD_OPERATIONS, model_id=payload.model_id.strip())
        if existing is not None:
            return JSONResponse(status_code=409, content=dict(existing))
    operation = start_download_operation(payload)
    return JSONResponse(status_code=202, content=operation)


@APP.get("/admin/models/{operation_id}")
async def admin_download_status(operation_id: str) -> JSONResponse:
    with MODEL_OPS_LOCK:
        operation = MODEL_DOWNLOAD_OPERATIONS.get(operation_id)
        if operation is None:
            raise HTTPException(status_code=404, detail="operation not found")
        return JSONResponse(status_code=200, content=dict(operation))


@APP.post("/admin/models/{operation_id}/cancel")
async def admin_cancel_download(operation_id: str) -> JSONResponse:
    with MODEL_OPS_LOCK:
        operation = MODEL_DOWNLOAD_OPERATIONS.get(operation_id)
        if operation is None:
            raise HTTPException(status_code=404, detail="operation not found")
        if _status_is_terminal(operation.get("status")):
            return JSONResponse(status_code=200, content=dict(operation))
        operation["cancelRequested"] = True
        if operation.get("status") == "queued":
            operation["status"] = "cancelled"
            operation["error"] = "Cancelled by operator."
            operation["completedAtUtc"] = utc_now_iso()
        elif operation.get("status") == "running":
            operation["status"] = "cancelling"
        return JSONResponse(status_code=200, content=dict(operation))


@APP.delete("/admin/models/{model_ref}")
async def admin_delete_model(model_ref: str) -> JSONResponse:
    if not model_ref:
        raise HTTPException(status_code=400, detail="model_ref is required")

    active_ref = STATE.snapshot().get("modelRef")
    if active_ref and (active_ref == model_ref or str(active_ref).endswith(model_ref)):
        raise HTTPException(status_code=409, detail="cannot delete active model")

    model_dir = os.path.abspath(get_model_dir())
    target = os.path.abspath(os.path.join(model_dir, model_ref))
    if not target.startswith(model_dir):
        raise HTTPException(status_code=400, detail="invalid model_ref")

    if not os.path.exists(target):
        raise HTTPException(status_code=404, detail="model not found")

    if os.path.isdir(target):
        shutil.rmtree(target)
    else:
        os.remove(target)

    return JSONResponse(status_code=200, content={"deleted": True, "modelRef": model_ref})


@APP.post("/transcribe")
async def transcribe(
    request: Request,
    audio: UploadFile = File(...),
    language: str | None = Form(default=None),
) -> JSONResponse:
    request_id = request.headers.get("x-request-id", str(uuid.uuid4()))
    snapshot = STATE.snapshot()
    payload_size_header = request.headers.get("content-length")
    if not snapshot["loaded"]:
        log_event(
            "asr_transcribe_rejected",
            requestId=request_id,
            reason="model_not_loaded",
            modelLoaded=False,
            loading=snapshot.get("loading"),
            modelRef=snapshot.get("modelRef"),
            payloadSizeHeader=payload_size_header,
            languageHint=language,
        )
        return JSONResponse(
            status_code=503,
            content={
                "requestId": request_id,
                "error": "model_not_loaded",
                "message": "Load a model with /admin/load before transcribing.",
            },
        )
    if snapshot.get("warmupEnabled") and not snapshot.get("warmupSucceeded"):
        log_event(
            "asr_transcribe_rejected",
            requestId=request_id,
            reason="model_not_ready",
            modelLoaded=True,
            loading=snapshot.get("loading"),
            modelRef=snapshot.get("modelRef"),
            warmupError=snapshot.get("warmupError"),
            payloadSizeHeader=payload_size_header,
            languageHint=language,
        )
        return JSONResponse(
            status_code=503,
            content={
                "requestId": request_id,
                "error": "model_not_ready",
                "message": "ASR warmup is incomplete. Retry after /ready is healthy.",
                "warmupError": snapshot.get("warmupError"),
            },
        )

    started = time.perf_counter()
    payload = await audio.read()
    size_bytes = len(payload)
    size_bucket = normalize_size_bucket(size_bytes)
    temp_path = ""
    decoded_path = ""

    log_event(
        "asr_transcribe_start",
        requestId=request_id,
        filename=audio.filename,
        contentType=audio.content_type,
        payloadSizeBytes=size_bytes,
        payloadSizeBucket=size_bucket,
        languageHint=language,
        modelRef=snapshot.get("modelRef"),
    )

    try:
        suffix = ""
        if audio.filename and "." in audio.filename:
            suffix = "." + audio.filename.rsplit(".", 1)[1]

        with tempfile.NamedTemporaryFile(delete=False, suffix=suffix) as temp_file:
            temp_file.write(payload)
            temp_path = temp_file.name

        with STATE.lock:
            config_for_timeout = STATE.config
        request_timeout_seconds = resolve_request_timeout_seconds(
            request.headers,
            config_for_timeout.request_timeout_seconds if config_for_timeout is not None else 300,
        )

        def _run_transcribe() -> tuple[str, float, str | None, str]:
            def _work() -> tuple[str, float, str | None, str]:
                with track_transcription():
                    decoded_path_local = decode_audio_to_wav_16k_mono(temp_path)
                    duration_seconds_local = get_audio_duration_seconds(decoded_path_local)
                    with STATE.lock:
                        config_local = STATE.config
                        model_ref_local = STATE.model_ref
                    if config_local is None:
                        raise RuntimeError("ASR engine is not loaded.")
                    text_local, _detected_language_local = transcribe_via_engine_with_recovery(
                        config_local,
                        decoded_path_local,
                        language if language else None,
                        request_timeout_seconds=request_timeout_seconds,
                    )
                    return text_local, duration_seconds_local, model_ref_local, decoded_path_local

            return run_with_sync_heartbeat(
                "asr_transcribe_heartbeat",
                {
                    "requestId": request_id,
                    "modelRef": snapshot.get("modelRef"),
                    "payloadSizeBucket": size_bucket,
                },
                _work,
            )

        text, duration_seconds, model_ref, decoded_path = await await_blocking(_run_transcribe)

        latency_ms = int((time.perf_counter() - started) * 1000)
        log_event(
            "asr_transcribe_success",
            requestId=request_id,
            modelRef=model_ref,
            latencyMs=latency_ms,
            payloadSizeBucket=size_bucket,
            durationSeconds=duration_seconds,
            textLength=len(text),
        )

        return JSONResponse(
            status_code=200,
            content={
                "requestId": request_id,
                "text": text,
                "durationSeconds": int(round(duration_seconds)),
                "modelRef": model_ref,
            },
        )
    except Exception as exc:
        latency_ms = int((time.perf_counter() - started) * 1000)
        log_event(
            "asr_transcribe_failed",
            requestId=request_id,
            latencyMs=latency_ms,
            errorType=type(exc).__name__,
            error=str(exc),
        )
        status_code = wrapper_http_status_for_engine_exception(exc)
        return JSONResponse(
            status_code=status_code,
            content={
                "requestId": request_id,
                "error": "transcription_failed" if status_code >= 500 else "transcription_rejected",
                "message": "Transcription failed. Check service logs for details.",
            },
        )
    finally:
        if decoded_path and os.path.exists(decoded_path):
            os.remove(decoded_path)
        if temp_path and os.path.exists(temp_path):
            os.remove(temp_path)


if __name__ == "__main__":
    host = os.getenv("GA_ASR_HOST", "127.0.0.1")
    port = int(os.getenv("GA_ASR_PORT", "8082"))
    log_level = os.getenv("GA_ASR_LOG_LEVEL", "info").lower()
    access_log_enabled = env_flag("GA_ASR_UVICORN_ACCESS_LOG", default=False)
    if access_log_enabled:
        configure_uvicorn_access_log_filters(
            ignore_health_requests=env_flag("GA_ASR_SUPPRESS_HEALTH_ACCESS_LOGS", default=True)
        )
    uvicorn.run(APP, host=host, port=port, log_level=log_level, access_log=access_log_enabled)
