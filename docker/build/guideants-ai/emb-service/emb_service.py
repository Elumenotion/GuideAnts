import json
import logging
import os
import shutil
import subprocess
import threading
import time
import urllib.error
import urllib.request
import uuid
from dataclasses import dataclass
from datetime import datetime, timezone
from typing import Any

import uvicorn
from fastapi import FastAPI, HTTPException, Request
from fastapi.responses import JSONResponse
from pydantic import BaseModel, Field

MAX_PRODUCED_DIMENSION = 1536
CATALOG_PATH = os.path.join(os.path.dirname(__file__), "catalog", "manifest.json")


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
    payload = {"event": event, "ts": utc_now_iso()}
    payload.update(fields)
    print(json.dumps(payload, ensure_ascii=True, sort_keys=True), flush=True)


def load_catalog() -> dict[str, Any]:
    with open(CATALOG_PATH, encoding="utf-8") as handle:
        data = json.load(handle)
    entries = {entry["id"]: entry for entry in data.get("entries", []) if entry.get("task") == "emb"}
    return {"version": data.get("version", 1), "entries": entries}


CATALOG = load_catalog()


class LoadModelRequest(BaseModel):
    model_id: str | None = None
    model_path: str | None = None
    hf_token: str | None = None


class DownloadModelRequest(BaseModel):
    model_id: str
    revision: str | None = None
    hf_token: str | None = None


class EmbedRequest(BaseModel):
    inputs: list[str] = Field(default_factory=list)
    purpose: str = "document"


@dataclass
class EmbRuntimeConfig:
    server_path: str
    model_dir: str
    gguf_path: str
    model_ref: str
    catalog_entry_id: str
    produced_dimension: int
    engine_host: str
    engine_port: int
    engine_base_url: str
    engine_ready_timeout_seconds: int
    request_timeout_seconds: int
    n_gpu_layers: int
    pooling: str


class EmbRuntimeState:
    def __init__(self) -> None:
        self.lock = threading.RLock()
        self.config: EmbRuntimeConfig | None = None
        self.engine_process: subprocess.Popen[Any] | None = None
        self.engine_started_at_utc: str | None = None
        self.model_ref: str | None = None
        self.catalog_entry_id: str | None = None
        self.dimension: int = 0
        self.device: str = "cpu"
        self.loaded_at_utc: str | None = None
        self.loading: bool = False
        self.load_error: str | None = None
        self.autoload_enabled: bool = False
        self.warmup_enabled: bool = env_flag("GA_EMB_WARMUP_ON_LOAD", default=True)
        self.warmup_ran: bool = False
        self.warmup_succeeded: bool = False
        self.warmup_latency_ms: int = 0
        self.warmup_error: str | None = None
        self.warmup_completed_at_utc: str | None = None

    def snapshot(self) -> dict[str, Any]:
        with self.lock:
            loaded = self.config is not None and is_engine_process_alive()
            return {
                "loaded": loaded,
                "loading": self.loading,
                "modelRef": self.model_ref,
                "catalogEntryId": self.catalog_entry_id,
                "device": self.device,
                "dimensions": self.dimension,
                "loadedAtUtc": self.loaded_at_utc,
                "loadError": self.load_error,
                "autoloadEnabled": self.autoload_enabled,
                "warmupEnabled": self.warmup_enabled,
                "warmupRan": self.warmup_ran,
                "warmupSucceeded": self.warmup_succeeded,
                "warmupLatencyMs": self.warmup_latency_ms,
                "warmupError": self.warmup_error,
                "warmupCompletedAtUtc": self.warmup_completed_at_utc,
                "engineAlive": is_engine_process_alive(),
            }


STATE = EmbRuntimeState()
APP = FastAPI(title="GuideAnts Embeddings Service", version="2.0.0")
ENGINE_LOCK = threading.Lock()
MODEL_OPS_LOCK = threading.Lock()
MODEL_DOWNLOAD_OPERATIONS: dict[str, dict[str, Any]] = {}


def get_model_dir() -> str:
    return os.getenv("GA_EMB_MODEL_DIR", "/models-local/emb")


def resolve_catalog_entry(model_id: str) -> dict[str, Any]:
    entry = CATALOG["entries"].get(model_id.strip())
    if entry is None:
        raise ValueError(
            f"model_id '{model_id}' is not in the curated embeddings catalog. "
            "Only manifest entries are allowed."
        )
    dim = int(entry.get("producedDimension", 0))
    if dim <= 0 or dim > MAX_PRODUCED_DIMENSION:
        raise ValueError(
            f"Catalog entry '{model_id}' has invalid producedDimension {dim}; "
            f"must be > 0 and <= {MAX_PRODUCED_DIMENSION}."
        )
    return entry


def resolve_server_path() -> str:
    configured = (os.getenv("GA_EMB_SERVER_PATH") or "").strip()
    if configured:
        return configured if os.path.isabs(configured) else (shutil.which(configured) or configured)
    for candidate in ("/app/llama-server", shutil.which("llama-server")):
        if candidate and os.path.isfile(candidate):
            return candidate
    raise RuntimeError("llama-server binary not found; set GA_EMB_SERVER_PATH.")


def resolve_n_gpu_layers() -> tuple[str, int]:
    device = (os.getenv("GA_EMB_DEVICE") or "cpu").strip().lower()
    if device in {"cuda-multi"}:
        device = "cuda"
    if device in {"hip", "rocm", "amd", "hip-multi", "rocm-multi", "amd-multi"}:
        device = "cuda"
    if device == "cpu":
        return device, 0
    override = os.getenv("GA_EMB_NGL")
    if override is not None and override.strip() != "":
        try:
            return device, int(override.strip())
        except ValueError as exc:
            raise ValueError("GA_EMB_NGL must be an integer.") from exc
    return device, -1


def resolve_permitted_gguf_path(model_dir: str, catalog_filename: str) -> str:
    """Resolve a catalog-listed GGUF filename under model_dir.

    ``catalog_filename`` must come from the baked manifest (see
    ``catalog_entry_for_gguf_filename``), not from raw request input.
    """
    base_real = os.path.realpath(model_dir)
    candidate = os.path.realpath(os.path.join(base_real, catalog_filename))
    if not candidate.startswith(base_real + os.sep):
        raise ValueError("resolved model_path escapes the permitted model directory.")
    if not os.path.isfile(candidate):
        raise FileNotFoundError(f"GGUF file '{catalog_filename}' does not exist.")
    return candidate


def catalog_entry_for_gguf_filename(requested: str) -> tuple[str, dict[str, Any]] | None:
    requested = requested.strip()
    if not requested or "/" in requested or "\\" in requested or requested in {".", ".."}:
        return None
    for entry in CATALOG["entries"].values():
        if int(entry.get("producedDimension", 0)) == 0:
            continue
        for src in entry.get("sourceRepos", []):
            catalog_filename = str(src.get("filename") or "").strip()
            if catalog_filename and catalog_filename == requested:
                return catalog_filename, entry
    return None


def resolve_gguf_target(request: LoadModelRequest) -> tuple[str, str, dict[str, Any]]:
    model_dir = get_model_dir()
    os.makedirs(model_dir, exist_ok=True)

    if request.model_path:
        requested = request.model_path.strip()
        match = catalog_entry_for_gguf_filename(requested)
        if match is None:
            raise ValueError(f"Local GGUF '{requested}' is not a catalog-listed artifact.")
        catalog_filename, entry = match
        candidate = resolve_permitted_gguf_path(model_dir, catalog_filename)
        return candidate, catalog_filename, entry

    catalog_id = (request.model_id or os.getenv("GA_EMB_DEFAULT_MODEL_PATH") or "").strip()
    if not catalog_id:
        raise ValueError("model_id or model_path is required, or set GA_EMB_DEFAULT_MODEL_PATH.")

    entry = resolve_catalog_entry(catalog_id)
    source = entry["sourceRepos"][0]
    catalog_filename = str(source["filename"]).strip()
    try:
        gguf_path = resolve_permitted_gguf_path(model_dir, catalog_filename)
    except FileNotFoundError as exc:
        raise FileNotFoundError(
            f"Catalog model '{catalog_id}' expects '{catalog_filename}'. Download it first."
        ) from exc
    return gguf_path, catalog_filename, entry


def build_runtime_config(gguf_path: str, model_ref: str, entry: dict[str, Any]) -> EmbRuntimeConfig:
    device, n_gpu_layers = resolve_n_gpu_layers()
    engine_host = os.getenv("GA_EMB_ENGINE_HOST", "127.0.0.1")
    engine_port = parse_positive_int(os.getenv("GA_EMB_ENGINE_PORT"), 18085)
    return EmbRuntimeConfig(
        server_path=resolve_server_path(),
        model_dir=get_model_dir(),
        gguf_path=gguf_path,
        model_ref=model_ref,
        catalog_entry_id=entry["id"],
        produced_dimension=int(entry["producedDimension"]),
        engine_host=engine_host,
        engine_port=engine_port,
        engine_base_url=f"http://{engine_host}:{engine_port}",
        engine_ready_timeout_seconds=parse_positive_int(
            os.getenv("GA_EMB_ENGINE_READY_TIMEOUT_SECONDS"),
            parse_positive_int(os.getenv("GA_EMB_READY_TIMEOUT_SECONDS"), 1800),
        ),
        request_timeout_seconds=parse_positive_int(os.getenv("GA_EMB_ENGINE_REQUEST_TIMEOUT_SECONDS"), 120),
        n_gpu_layers=n_gpu_layers,
        pooling=str(entry.get("pooling") or "last"),
    )


def build_llama_server_command(config: EmbRuntimeConfig) -> list[str]:
    return [
        config.server_path,
        "--embeddings",
        "--pooling",
        config.pooling,
        "-m",
        config.gguf_path,
        "--host",
        config.engine_host,
        "--port",
        str(config.engine_port),
        "-ngl",
        str(config.n_gpu_layers),
    ]


def is_engine_process_alive() -> bool:
    process = STATE.engine_process
    return process is not None and process.poll() is None


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
        raise RuntimeError(f"Failed to reach llama-server at {url}: {reason}") from exc


def llama_json_request(
    config: EmbRuntimeConfig,
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
        raise RuntimeError(f"Unexpected JSON from llama-server {method} {path}")
    return status_code, parsed


def wait_for_engine_ready(config: EmbRuntimeConfig) -> None:
    deadline = time.monotonic() + config.engine_ready_timeout_seconds
    while time.monotonic() < deadline:
        if not is_engine_process_alive():
            process = STATE.engine_process
            exit_code = process.poll() if process is not None else None
            raise RuntimeError(f"llama-server exited before readiness (exit code: {exit_code}).")
        try:
            status_code, _ = llama_json_request(
                config, "GET", "/health", min(5, config.request_timeout_seconds)
            )
            if status_code == 200:
                return
        except Exception:
            pass
        time.sleep(0.5)
    raise RuntimeError(
        f"Timed out waiting for llama-server readiness after {config.engine_ready_timeout_seconds}s."
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
    STATE.dimension = 0
    STATE.loaded_at_utc = None
    STATE.warmup_ran = False
    STATE.warmup_succeeded = False
    STATE.warmup_latency_ms = 0
    STATE.warmup_error = None
    STATE.warmup_completed_at_utc = None


def probe_embedding_dimension(config: EmbRuntimeConfig) -> int:
    status_code, parsed = llama_json_request(
        config,
        "POST",
        "/v1/embeddings",
        config.request_timeout_seconds,
        payload={"input": ["dimension probe"]},
    )
    if status_code != 200:
        raise RuntimeError(f"Dimension probe failed with HTTP {status_code}.")
    data = parsed.get("data")
    if not isinstance(data, list) or not data:
        raise RuntimeError("Dimension probe returned no embedding data.")
    embedding = data[0].get("embedding")
    if not isinstance(embedding, list) or not embedding:
        raise RuntimeError("Dimension probe returned an empty embedding.")
    return len(embedding)


def start_engine(request: LoadModelRequest) -> dict[str, Any]:
    gguf_path, model_ref, entry = resolve_gguf_target(request)
    config = build_runtime_config(gguf_path, model_ref, entry)
    device, _ = resolve_n_gpu_layers()

    if STATE.config and STATE.config.gguf_path == gguf_path and is_engine_process_alive():
        return {
            "modelRef": STATE.model_ref,
            "catalogEntryId": STATE.catalog_entry_id,
            "dimensions": STATE.dimension,
            "action": "noop-already-loaded",
        }

    stop_engine()
    command = build_llama_server_command(config)
    log_event("emb_engine_start", command=command, modelRef=model_ref)
    STATE.engine_process = subprocess.Popen(
        command,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.PIPE,
        env=os.environ.copy(),
    )
    STATE.engine_started_at_utc = utc_now_iso()
    wait_for_engine_ready(config)
    actual_dim = probe_embedding_dimension(config)
    expected_dim = config.produced_dimension
    if actual_dim != expected_dim:
        stop_engine()
        raise RuntimeError(
            f"GGUF produced dimension {actual_dim} != catalog declared {expected_dim}."
        )

    STATE.config = config
    STATE.model_ref = model_ref
    STATE.catalog_entry_id = entry["id"]
    STATE.dimension = actual_dim
    STATE.device = device
    STATE.loaded_at_utc = utc_now_iso()
    return {"modelRef": model_ref, "catalogEntryId": entry["id"], "dimensions": actual_dim}


def normalize_purpose(raw: str) -> str:
    purpose = (raw or "").strip().lower()
    if purpose in {"document", "query"}:
        return purpose
    raise ValueError("purpose must be either 'document' or 'query'.")


def apply_input_prefix(text: str, purpose: str, entry_id: str | None) -> str:
    if not entry_id:
        return text
    entry = CATALOG["entries"].get(entry_id)
    if entry is None:
        return text
    template_key = "queryPrefixTemplate" if purpose == "query" else "documentPrefixTemplate"
    template = entry.get(template_key)
    if not template:
        return text
    return str(template).replace("{text}", text)


def embed_via_engine(inputs: list[str], purpose: str) -> list[list[float]]:
    config = STATE.config
    if config is None:
        raise RuntimeError("Embeddings engine is not loaded.")
    prefixed = [apply_input_prefix(value, purpose, STATE.catalog_entry_id) for value in inputs]
    status_code, parsed = llama_json_request(
        config,
        "POST",
        "/v1/embeddings",
        config.request_timeout_seconds,
        payload={"input": prefixed},
    )
    if status_code != 200:
        raise RuntimeError(f"llama-server /v1/embeddings returned HTTP {status_code}.")
    data = parsed.get("data")
    if not isinstance(data, list):
        raise RuntimeError("llama-server returned no embedding data.")
    vectors: list[list[float]] = []
    for item in sorted(data, key=lambda row: int(row.get("index", 0))):
        embedding = item.get("embedding")
        if not isinstance(embedding, list):
            raise RuntimeError("Malformed embedding row from llama-server.")
        vectors.append([float(value) for value in embedding])
    if len(vectors) != len(inputs):
        raise RuntimeError(f"Expected {len(inputs)} vectors, got {len(vectors)}.")
    return vectors


def run_model_warmup(force: bool = False) -> dict[str, Any]:
    warmup_enabled = env_flag("GA_EMB_WARMUP_ON_LOAD", default=True)
    if not warmup_enabled and not force:
        return {
            "warmupEnabled": False,
            "warmupRan": False,
            "warmupSucceeded": False,
            "warmupLatencyMs": 0,
            "dimensions": STATE.dimension,
        }

    started = time.perf_counter()
    log_event("emb_model_warmup_start", modelRef=STATE.model_ref)
    try:
        query_vectors = embed_via_engine(["startup warmup query"], "query")
        document_vectors = embed_via_engine(["startup warmup document"], "document")
        query_dim = len(query_vectors[0])
        document_dim = len(document_vectors[0])
        if query_dim <= 0 or query_dim != document_dim:
            raise RuntimeError(
                f"Warmup produced inconsistent dimensions query={query_dim}, document={document_dim}."
            )
        latency_ms = int((time.perf_counter() - started) * 1000)
        return {
            "warmupEnabled": True,
            "warmupRan": True,
            "warmupSucceeded": True,
            "warmupLatencyMs": latency_ms,
            "dimensions": query_dim,
            "warmupCompletedAtUtc": utc_now_iso(),
        }
    except Exception as exc:
        latency_ms = int((time.perf_counter() - started) * 1000)
        log_event(
            "emb_model_warmup_failed",
            modelRef=STATE.model_ref,
            errorType=type(exc).__name__,
            error=str(exc),
        )
        return {
            "warmupEnabled": True,
            "warmupRan": True,
            "warmupSucceeded": False,
            "warmupLatencyMs": latency_ms,
            "warmupError": str(exc),
            "dimensions": 0,
        }


def load_model_serialized(request: LoadModelRequest, force_warmup: bool = False) -> dict[str, Any]:
    if not ENGINE_LOCK.acquire(blocking=False):
        raise RuntimeError("model lifecycle operation already in progress")
    try:
        with STATE.lock:
            STATE.loading = True
            STATE.load_error = None
        try:
            hf_token = (request.hf_token or "").strip()
            if hf_token:
                os.environ["HF_TOKEN"] = hf_token
            details = start_engine(request)
            warmup = run_model_warmup(force=force_warmup)
            if force_warmup and not warmup.get("warmupSucceeded", False):
                raise RuntimeError(str(warmup.get("warmupError") or "Embeddings warmup failed."))
            with STATE.lock:
                STATE.warmup_ran = bool(warmup.get("warmupRan"))
                STATE.warmup_succeeded = bool(warmup.get("warmupSucceeded"))
                STATE.warmup_latency_ms = int(warmup.get("warmupLatencyMs") or 0)
                STATE.warmup_error = warmup.get("warmupError")
                STATE.warmup_completed_at_utc = warmup.get("warmupCompletedAtUtc")
            return {**details, **warmup}
        except Exception as exc:
            with STATE.lock:
                STATE.load_error = str(exc)
            raise
        finally:
            with STATE.lock:
                STATE.loading = False
    finally:
        ENGINE_LOCK.release()


def unload_model() -> dict[str, Any]:
    with STATE.lock:
        previous_ref = STATE.model_ref
        had_model = STATE.config is not None
    stop_engine()
    return {"wasLoaded": had_model, "previousModelRef": previous_ref}


def list_model_entries() -> list[dict[str, Any]]:
    model_dir = get_model_dir()
    os.makedirs(model_dir, exist_ok=True)
    active_ref = STATE.snapshot().get("modelRef")
    items: list[dict[str, Any]] = []
    for name in sorted(os.listdir(model_dir)):
        full_path = os.path.join(model_dir, name)
        try:
            size_bytes = os.path.getsize(full_path) if os.path.isfile(full_path) else 0
        except OSError:
            size_bytes = 0
        items.append(
            {
                "modelRef": name,
                "path": full_path,
                "isDirectory": os.path.isdir(full_path),
                "sizeBytes": size_bytes,
                "active": bool(active_ref and (active_ref == name or active_ref == full_path)),
            }
        )
    return items


def _status_is_terminal(status: str | None) -> bool:
    normalized = (status or "").strip().lower()
    return normalized in {"completed", "failed", "error", "cancelled", "canceled"}


def start_download_operation(request: DownloadModelRequest) -> dict[str, Any]:
    entry = resolve_catalog_entry(request.model_id)
    source = entry["sourceRepos"][0]
    repo_id = source["repoId"]
    filename = source["filename"]
    revision = request.revision or source.get("revision")

    operation_id = uuid.uuid4().hex
    operation = {
        "operationId": operation_id,
        "status": "queued",
        "modelId": request.model_id,
        "error": None,
        "modelRef": filename,
        "cancelRequested": False,
        "startedAtUtc": utc_now_iso(),
        "completedAtUtc": None,
    }
    with MODEL_OPS_LOCK:
        MODEL_DOWNLOAD_OPERATIONS[operation_id] = operation

    def _run() -> None:
        model_dir = get_model_dir()
        os.makedirs(model_dir, exist_ok=True)
        target_path = os.path.join(model_dir, filename)
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
            from huggingface_hub import hf_hub_download

            hf_token = (request.hf_token or "").strip() or None
            downloaded = hf_hub_download(
                repo_id=repo_id,
                filename=filename,
                revision=revision,
                local_dir=model_dir,
                local_dir_use_symlinks=False,
                token=hf_token,
            )
            if not os.path.isfile(downloaded):
                raise RuntimeError(f"Expected GGUF file was not produced: {filename}")
            with MODEL_OPS_LOCK:
                current = MODEL_DOWNLOAD_OPERATIONS.get(operation_id)
                if current is None:
                    return
                if current.get("cancelRequested"):
                    if os.path.exists(target_path):
                        os.remove(target_path)
                    current["status"] = "cancelled"
                    current["error"] = "Cancelled by operator."
                    current["completedAtUtc"] = utc_now_iso()
                    return
                current["status"] = "completed"
                current["modelRef"] = filename
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
    fix_mistral = os.getenv("GA_EMB_FIX_MISTRAL_REGEX")
    if fix_mistral is not None and fix_mistral.strip():
        log_event(
            "emb_deprecated_env",
            name="GA_EMB_FIX_MISTRAL_REGEX",
            message="Retired under llama-server embeddings; value ignored.",
        )

    device, ngl = resolve_n_gpu_layers()
    startup_details = {
        "host": os.getenv("GA_EMB_HOST", "127.0.0.1"),
        "port": os.getenv("GA_EMB_PORT", "8085"),
        "modelDir": get_model_dir(),
        "defaultModelPath": os.getenv("GA_EMB_DEFAULT_MODEL_PATH"),
        "device": device,
        "nGpuLayers": ngl,
        "catalogVersion": CATALOG.get("version"),
    }
    log_event("emb_service_startup", **startup_details)


@APP.on_event("shutdown")
async def on_shutdown() -> None:
    with ENGINE_LOCK:
        stop_engine()


@APP.get("/health")
async def health() -> dict[str, Any]:
    return {"status": "ok", **STATE.snapshot()}


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
                "message": "Embeddings model loaded but warmup is incomplete.",
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
            "producedDimension": entry.get("producedDimension"),
            "default": entry.get("default", False),
        }
        for entry in CATALOG["entries"].values()
    ]
    return JSONResponse(status_code=200, content={"version": CATALOG["version"], "entries": entries})


@APP.post("/admin/load")
async def admin_load(request: Request, payload: LoadModelRequest) -> JSONResponse:
    request_id = request.headers.get("x-request-id", str(uuid.uuid4()))
    log_event("emb_model_load_start", requestId=request_id, payload=payload.model_dump())
    try:
        details = load_model_serialized(payload, force_warmup=env_flag("GA_EMB_WARMUP_ON_LOAD", default=True))
        log_event("emb_model_load_success", requestId=request_id, **details)
        return JSONResponse(
            status_code=200,
            content={"requestId": request_id, "status": "loaded", **details},
        )
    except (ValueError, FileNotFoundError) as exc:
        return JSONResponse(
            status_code=400,
            content={
                "requestId": request_id,
                "status": "failed",
                "error": "invalid_model_request",
                "message": str(exc),
            },
        )
    except Exception as exc:
        log_event("emb_model_load_failed", requestId=request_id, errorType=type(exc).__name__, error=str(exc))
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
    request_id = request.headers.get("x-request-id", str(uuid.uuid4()))
    if not ENGINE_LOCK.acquire(blocking=False):
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
        snapshot = STATE.snapshot()
        if not snapshot["loaded"]:
            return JSONResponse(
                status_code=200,
                content={"requestId": request_id, "ok": True, "action": "noop-already-unloaded", **snapshot},
            )
        result = unload_model()
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
        ENGINE_LOCK.release()


@APP.get("/admin/models")
async def admin_list_models() -> JSONResponse:
    return JSONResponse(
        status_code=200,
        content={"modelDir": get_model_dir(), "items": list_model_entries()},
    )


@APP.post("/admin/models/download")
async def admin_download_model(payload: DownloadModelRequest) -> JSONResponse:
    if not payload.model_id.strip():
        raise HTTPException(status_code=400, detail="model_id is required")
    try:
        resolve_catalog_entry(payload.model_id)
    except ValueError as exc:
        raise HTTPException(status_code=400, detail=str(exc)) from exc
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
    if not target.startswith(model_dir + os.sep) and target != model_dir:
        raise HTTPException(status_code=400, detail="invalid model_ref")
    if not os.path.exists(target):
        raise HTTPException(status_code=404, detail="model not found")
    if os.path.isdir(target):
        shutil.rmtree(target, ignore_errors=False)
    else:
        os.remove(target)
    return JSONResponse(status_code=200, content={"deleted": True, "modelRef": model_ref})


@APP.post("/embed")
async def embed(request: Request, payload: EmbedRequest) -> JSONResponse:
    request_id = request.headers.get("x-request-id", str(uuid.uuid4()))
    try:
        purpose = normalize_purpose(payload.purpose)
    except ValueError:
        return JSONResponse(
            status_code=400,
            content={
                "requestId": request_id,
                "error": "invalid_purpose",
                "message": "Invalid purpose. Use one of: query, document.",
            },
        )

    snapshot = STATE.snapshot()
    if not snapshot["loaded"]:
        return JSONResponse(
            status_code=503,
            content={
                "requestId": request_id,
                "error": "model_not_loaded",
                "message": "Load a model with /admin/load before requesting embeddings.",
            },
        )
    if snapshot.get("warmupEnabled") and not snapshot.get("warmupSucceeded"):
        return JSONResponse(
            status_code=503,
            content={
                "requestId": request_id,
                "error": "model_not_ready",
                "message": "Embeddings warmup is incomplete.",
                "warmupError": snapshot.get("warmupError"),
            },
        )

    if not payload.inputs:
        return JSONResponse(
            status_code=200,
            content={
                "requestId": request_id,
                "data": [],
                "dimensions": int(snapshot.get("dimensions") or 0),
                "modelRef": snapshot.get("modelRef"),
            },
        )

    started = time.perf_counter()
    try:
        vectors = embed_via_engine(payload.inputs, purpose)
        dimensions = len(vectors[0]) if vectors else int(snapshot.get("dimensions") or 0)
        data = [{"index": idx, "embedding": vector} for idx, vector in enumerate(vectors)]
        latency_ms = int((time.perf_counter() - started) * 1000)
        return JSONResponse(
            status_code=200,
            content={
                "requestId": request_id,
                "data": data,
                "dimensions": dimensions,
                "modelRef": snapshot.get("modelRef"),
                "latencyMs": latency_ms,
            },
        )
    except Exception as exc:
        log_event(
            "emb_inference_failed",
            requestId=request_id,
            errorType=type(exc).__name__,
            error=str(exc),
        )
        return JSONResponse(
            status_code=500,
            content={
                "requestId": request_id,
                "error": "embedding_failed",
                "message": "Embedding inference failed. Check service logs for details.",
            },
        )


def main() -> None:
    host = os.getenv("GA_EMB_HOST", "127.0.0.1")
    port = parse_positive_int(os.getenv("GA_EMB_PORT"), 8085)
    log_level = (os.getenv("GA_EMB_LOG_LEVEL") or "info").lower()
    access_log = env_flag("GA_EMB_UVICORN_ACCESS_LOG", default=False)
    configure_uvicorn_access_log_filters(env_flag("GA_EMB_SUPPRESS_HEALTH_ACCESS_LOGS", default=True))
    uvicorn.run(APP, host=host, port=port, log_level=log_level, access_log=access_log)


if __name__ == "__main__":
    main()
