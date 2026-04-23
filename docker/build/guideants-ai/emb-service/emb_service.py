import gc
import json
import logging
import os
import threading
import time
import uuid
from datetime import datetime, timezone
from typing import Any

import uvicorn
from fastapi import FastAPI, Request
from fastapi.responses import JSONResponse
from pydantic import BaseModel, Field
from sentence_transformers import SentenceTransformer

try:
    import torch
except Exception:  # pragma: no cover - torch is an indirect dep; guard anyway.
    torch = None  # type: ignore[assignment]


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


class LoadModelRequest(BaseModel):
    model_id: str | None = None
    model_path: str | None = None
    # Single, server-resolved Hugging Face token stamped in by the .NET web
    # layer. Used when `model_id` triggers an implicit HF download. Not read
    # from env — the web API is the only source.
    hf_token: str | None = None


class EmbedRequest(BaseModel):
    inputs: list[str] = Field(default_factory=list)
    purpose: str = "document"


class EmbRuntimeState:
    def __init__(self) -> None:
        self.lock = threading.RLock()
        self.model: SentenceTransformer | None = None
        self.multi_process_pool: dict[str, Any] | None = None
        self.model_ref: str | None = None
        self.device: str = "cpu"
        self.target_devices: list[str] = []
        self.dimension: int = 0
        self.loaded_at_utc: str | None = None
        self.loading: bool = False
        self.load_error: str | None = None
        self.autoload_enabled: bool = env_flag("GA_EMB_AUTO_LOAD_ON_STARTUP", default=False)
        self.warmup_enabled: bool = env_flag("GA_EMB_WARMUP_ON_LOAD", default=True)
        self.warmup_ran: bool = False
        self.warmup_succeeded: bool = False
        self.warmup_latency_ms: int = 0
        self.warmup_error: str | None = None
        self.warmup_completed_at_utc: str | None = None

    def snapshot(self) -> dict[str, Any]:
        with self.lock:
            return {
                "loaded": self.model is not None,
                "loading": self.loading,
                "modelRef": self.model_ref,
                "device": self.device,
                "targetDevices": self.target_devices,
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
            }


STATE = EmbRuntimeState()
APP = FastAPI(title="GuideAnts Embeddings Service", version="1.0.0")
MODEL_LOAD_LOCK = threading.Lock()


def unload_model() -> dict[str, Any]:
    """
    Drop the loaded sentence-transformer, stop any multi-process pool, and
    release CUDA memory. Caller must hold MODEL_LOAD_LOCK. Safe to call when
    nothing is loaded.
    """
    with STATE.lock:
        had_model = STATE.model is not None
        previous_ref = STATE.model_ref
        model = STATE.model
        pool = STATE.multi_process_pool
        STATE.model = None
        STATE.multi_process_pool = None
        STATE.model_ref = None
        STATE.device = "cpu"
        STATE.target_devices = []
        STATE.dimension = 0
        STATE.loaded_at_utc = None
        STATE.load_error = None
        STATE.warmup_ran = False
        STATE.warmup_succeeded = False
        STATE.warmup_latency_ms = 0
        STATE.warmup_error = None
        STATE.warmup_completed_at_utc = None
    if model is not None and pool is not None:
        try:
            model.stop_multi_process_pool(pool)
        except Exception:
            pass
    del model
    del pool
    gc.collect()
    try:
        if torch is not None and torch.cuda.is_available():
            torch.cuda.empty_cache()
    except Exception:
        pass
    return {"wasLoaded": had_model, "previousModelRef": previous_ref}


def resolve_model_target(request: LoadModelRequest) -> str:
    model_dir = os.getenv("GA_EMB_MODEL_DIR", "/models-local/emb")
    default_model_path = (os.getenv("GA_EMB_DEFAULT_MODEL_PATH") or "harrier-oss-v1-0.6b").strip()
    default_model_id = (os.getenv("GA_EMB_DEFAULT_MODEL_ID") or "microsoft/harrier-oss-v1-0.6b").strip()

    if request.model_path:
        candidate = request.model_path.strip()
        if not os.path.isabs(candidate):
            candidate = os.path.join(model_dir, candidate)
        if os.path.exists(candidate):
            return candidate
        return request.model_path.strip()

    if request.model_id:
        return request.model_id.strip()

    if default_model_path:
        candidate = default_model_path
        if not os.path.isabs(candidate):
            candidate = os.path.join(model_dir, candidate)
        if os.path.exists(candidate):
            return candidate

    return default_model_id


def resolve_device() -> str:
    requested = (os.getenv("GA_EMB_DEVICE", "cpu") or "cpu").strip().lower().replace("_", "-")
    if requested in {"multi-gpu", "multigpu"}:
        requested = "cuda-multi"
    if requested in {"cpu", "cuda", "mps", "cuda-multi"}:
        return requested
    return "cpu"


def resolve_target_devices(device: str) -> list[str]:
    if device != "cuda-multi":
        return []

    configured = os.getenv("GA_EMB_TARGET_DEVICES", "cuda:0,cuda:1") or "cuda:0,cuda:1"
    devices = [value.strip() for value in configured.split(",") if value.strip()]
    return devices or ["cuda:0", "cuda:1"]


def resolve_fix_mistral_regex() -> bool:
    return env_flag("GA_EMB_FIX_MISTRAL_REGEX", default=True)


def normalize_purpose(raw: str) -> str:
    purpose = (raw or "").strip().lower()
    if purpose in {"document", "query"}:
        return purpose
    raise ValueError("purpose must be either 'document' or 'query'.")


def embed_vectors(
    model: SentenceTransformer,
    inputs: list[str],
    purpose: str,
    pool: dict[str, Any] | None = None,
) -> list[list[float]]:
    if pool is not None:
        if purpose == "query":
            vectors = model.encode_multi_process(inputs, pool, prompt_name="web_search_query")
        else:
            vectors = model.encode_multi_process(inputs, pool)
    else:
        if purpose == "query":
            vectors = model.encode(inputs, prompt_name="web_search_query", convert_to_numpy=True)
        else:
            vectors = model.encode(inputs, convert_to_numpy=True)

    normalized: list[list[float]] = []
    if hasattr(vectors, "tolist"):
        raw_vectors = vectors.tolist()
    else:
        raw_vectors = vectors

    for row in raw_vectors:
        normalized.append([float(value) for value in row])
    return normalized


def run_model_warmup(
    model: SentenceTransformer,
    model_ref: str,
    pool: dict[str, Any] | None = None,
) -> dict[str, Any]:
    warmup_enabled = env_flag("GA_EMB_WARMUP_ON_LOAD", default=True)
    if not warmup_enabled:
        return {
            "warmupEnabled": False,
            "warmupRan": False,
            "warmupSucceeded": False,
            "warmupLatencyMs": 0,
            "dimensions": model.get_sentence_embedding_dimension() or 0,
        }

    started = time.perf_counter()
    query_text = "startup warmup query"
    document_text = "startup warmup document"

    log_event("emb_model_warmup_start", modelRef=model_ref)
    try:
        query_vectors = embed_vectors(model, [query_text], "query", pool=pool)
        document_vectors = embed_vectors(model, [document_text], "document", pool=pool)

        if len(query_vectors) != 1 or len(document_vectors) != 1:
            raise RuntimeError("Warmup did not return exactly one query and one document vector.")

        query_dim = len(query_vectors[0])
        document_dim = len(document_vectors[0])
        if query_dim <= 0 or query_dim != document_dim:
            raise RuntimeError(
                f"Warmup produced inconsistent vector dimensions query={query_dim}, document={document_dim}.")

        latency_ms = int((time.perf_counter() - started) * 1000)
        log_event(
            "emb_model_warmup_success",
            modelRef=model_ref,
            warmupLatencyMs=latency_ms,
            dimensions=query_dim,
            warmupCompletedAtUtc=utc_now_iso(),
        )
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
            modelRef=model_ref,
            warmupLatencyMs=latency_ms,
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


def load_model(request: LoadModelRequest, force_warmup: bool = False) -> dict[str, Any]:
    # transformers / huggingface_hub pick up the HF token from the process
    # environment. When the .NET layer resolved a token for this request we
    # install it here before from_pretrained triggers any implicit download,
    # so the single configured token is what gets used.
    hf_token = (getattr(request, "hf_token", None) or "").strip()
    if hf_token:
        os.environ["HF_TOKEN"] = hf_token

    target = resolve_model_target(request)
    device = resolve_device()
    target_devices = resolve_target_devices(device)
    fix_mistral_regex = resolve_fix_mistral_regex()
    started = time.perf_counter()

    model_device = "cuda" if device == "cuda-multi" else device
    tokenizer_kwargs = {"fix_mistral_regex": True} if fix_mistral_regex else None
    model = SentenceTransformer(target, device=model_device, tokenizer_kwargs=tokenizer_kwargs)
    pool: dict[str, Any] | None = None
    try:
        if target_devices:
            log_event("emb_multi_gpu_pool_start", modelRef=target, targetDevices=target_devices)
            pool = model.start_multi_process_pool(target_devices=target_devices)
            log_event(
                "emb_multi_gpu_pool_started",
                modelRef=target,
                targetDevices=target_devices,
                workerCount=len(pool.get("processes", [])),
            )

        warmup_details = run_model_warmup(model, target, pool=pool)
    except Exception:
        if pool is not None:
            try:
                model.stop_multi_process_pool(pool)
            except Exception:
                pass
        raise

    if force_warmup and not warmup_details.get("warmupEnabled"):
        # Autoload must always perform warmup.
        with_warmup_env = os.environ.get("GA_EMB_WARMUP_ON_LOAD")
        os.environ["GA_EMB_WARMUP_ON_LOAD"] = "1"
        try:
            warmup_details = run_model_warmup(model, target, pool=pool)
        finally:
            if with_warmup_env is None:
                del os.environ["GA_EMB_WARMUP_ON_LOAD"]
            else:
                os.environ["GA_EMB_WARMUP_ON_LOAD"] = with_warmup_env

    if force_warmup and not warmup_details.get("warmupSucceeded", False):
        raise RuntimeError(str(warmup_details.get("warmupError") or "Embeddings warmup failed."))

    load_latency_ms = int((time.perf_counter() - started) * 1000)
    resolved_dimensions = int(warmup_details.get("dimensions") or model.get_sentence_embedding_dimension() or 0)

    old_model: SentenceTransformer | None = None
    old_pool: dict[str, Any] | None = None
    with STATE.lock:
        old_model = STATE.model
        old_pool = STATE.multi_process_pool
        STATE.model = model
        STATE.multi_process_pool = pool
        STATE.model_ref = target
        STATE.device = device
        STATE.target_devices = target_devices
        STATE.dimension = resolved_dimensions
        STATE.loaded_at_utc = utc_now_iso()
        STATE.autoload_enabled = env_flag("GA_EMB_AUTO_LOAD_ON_STARTUP", default=False)
        STATE.warmup_enabled = bool(warmup_details.get("warmupEnabled", False))
        STATE.warmup_ran = bool(warmup_details.get("warmupRan", False))
        STATE.warmup_succeeded = bool(warmup_details.get("warmupSucceeded", False))
        STATE.warmup_latency_ms = int(warmup_details.get("warmupLatencyMs", 0) or 0)
        STATE.warmup_error = warmup_details.get("warmupError")
        STATE.warmup_completed_at_utc = warmup_details.get("warmupCompletedAtUtc")

    if old_pool is not None and old_model is not None and old_pool is not pool:
        try:
            old_model.stop_multi_process_pool(old_pool)
        except Exception:
            pass

    return {
        "modelRef": target,
        "device": device,
        "targetDevices": target_devices,
        "dimensions": resolved_dimensions,
        "loadLatencyMs": load_latency_ms,
        "loadedAtUtc": STATE.loaded_at_utc,
        **warmup_details,
    }


def load_model_serialized(request: LoadModelRequest, force_warmup: bool = False) -> dict[str, Any]:
    with MODEL_LOAD_LOCK:
        with STATE.lock:
            STATE.loading = True
            STATE.load_error = None
        try:
            return load_model(request, force_warmup=force_warmup)
        except Exception as exc:
            with STATE.lock:
                STATE.load_error = str(exc)
            raise
        finally:
            with STATE.lock:
                STATE.loading = False


@APP.on_event("startup")
async def on_startup() -> None:
    resolved_device = resolve_device()
    resolved_target_devices = resolve_target_devices(resolved_device)
    startup_details = {
        "host": os.getenv("GA_EMB_HOST", "127.0.0.1"),
        "port": parse_positive_int(os.getenv("GA_EMB_PORT"), 8085),
        "modelDir": os.getenv("GA_EMB_MODEL_DIR", "/models-local/emb"),
        "device": resolved_device,
        "targetDevices": resolved_target_devices,
    }
    log_event("emb_service_startup", **startup_details)

    if env_flag("GA_EMB_AUTO_LOAD_ON_STARTUP", default=False):
        startup_request = LoadModelRequest()
        startup_target = resolve_model_target(startup_request)
        log_event("emb_model_autoload_start", modelTarget=startup_target, **startup_details)

        def _autoload_worker() -> None:
            try:
                details = load_model_serialized(startup_request, force_warmup=True)
                log_event("emb_model_autoload_success", **details)
            except Exception as exc:
                log_event(
                    "emb_model_autoload_failed",
                    modelTarget=startup_target,
                    errorType=type(exc).__name__,
                    error=str(exc),
                )

        threading.Thread(target=_autoload_worker, name="emb-autoload", daemon=True).start()


@APP.on_event("shutdown")
async def on_shutdown() -> None:
    with STATE.lock:
        model = STATE.model
        pool = STATE.multi_process_pool
        STATE.model = None
        STATE.multi_process_pool = None
    if model is not None and pool is not None:
        try:
            model.stop_multi_process_pool(pool)
        except Exception:
            pass


@APP.get("/health")
async def health() -> dict[str, Any]:
    return {
        "status": "ok",
        **STATE.snapshot(),
    }


@APP.get("/ready")
async def ready() -> JSONResponse:
    snapshot = STATE.snapshot()
    if not snapshot["loaded"]:
        return JSONResponse(status_code=503, content={"ready": False, **snapshot})
    if snapshot["autoloadEnabled"] and not snapshot.get("warmupSucceeded"):
        return JSONResponse(
            status_code=503,
            content={
                "ready": False,
                "message": "Embeddings model loaded but warmup is incomplete.",
                **snapshot,
            },
        )
    return JSONResponse(status_code=200, content={"ready": True, **snapshot})


@APP.post("/admin/load")
async def admin_load(request: Request, payload: LoadModelRequest) -> JSONResponse:
    request_id = request.headers.get("x-request-id", str(uuid.uuid4()))
    log_event("emb_model_load_start", requestId=request_id, payload=payload.model_dump())
    try:
        details = load_model_serialized(
            payload,
            force_warmup=env_flag("GA_EMB_WARMUP_ON_LOAD", default=True))
        log_event("emb_model_load_success", requestId=request_id, **details)
        return JSONResponse(status_code=200, content={"requestId": request_id, "status": "loaded", **details})
    except Exception as exc:
        log_event("emb_model_load_failed", requestId=request_id, errorType=type(exc).__name__, error=str(exc))
        return JSONResponse(
            status_code=500,
            content={
                "requestId": request_id,
                "status": "failed",
                "errorType": type(exc).__name__,
                "error": str(exc),
            },
        )


@APP.post("/admin/unload")
async def admin_unload(request: Request) -> JSONResponse:
    """
    Drop the loaded embedding model so the container releases GPU/RAM without
    a restart. Serialized with /admin/load via MODEL_LOAD_LOCK; if a load is
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
        with STATE.lock:
            already_unloaded = STATE.model is None
        if already_unloaded:
            log_event("emb_model_unload_noop", requestId=request_id)
            return JSONResponse(
                status_code=200,
                content={
                    "requestId": request_id,
                    "ok": True,
                    "action": "noop-already-unloaded",
                    **STATE.snapshot(),
                },
            )
        log_event("emb_model_unload_start", requestId=request_id)
        result = unload_model()
        log_event(
            "emb_model_unload_success",
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


@APP.post("/embed")
async def embed(request: Request, payload: EmbedRequest) -> JSONResponse:
    request_id = request.headers.get("x-request-id", str(uuid.uuid4()))
    try:
        purpose = normalize_purpose(payload.purpose)
    except ValueError as exc:
        return JSONResponse(
            status_code=400,
            content={
                "requestId": request_id,
                "error": "invalid_purpose",
                "message": str(exc),
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
    if snapshot["autoloadEnabled"] and not snapshot.get("warmupSucceeded"):
        return JSONResponse(
            status_code=503,
            content={
                "requestId": request_id,
                "error": "model_not_ready",
                "message": "Embeddings warmup is incomplete. Retry after /ready is healthy.",
                "warmupError": snapshot.get("warmupError"),
            },
        )

    if len(payload.inputs) == 0:
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
    with STATE.lock:
        model = STATE.model
        pool = STATE.multi_process_pool
        model_ref = STATE.model_ref

    if model is None:
        return JSONResponse(
            status_code=503,
            content={
                "requestId": request_id,
                "error": "model_not_loaded",
                "message": "Load a model with /admin/load before requesting embeddings.",
            },
        )

    try:
        vectors = embed_vectors(model, payload.inputs, purpose, pool=pool)
        if len(vectors) != len(payload.inputs):
            raise RuntimeError(f"Model returned {len(vectors)} vectors for {len(payload.inputs)} inputs.")

        dimensions = len(vectors[0]) if vectors else int(snapshot.get("dimensions") or 0)
        for idx, vector in enumerate(vectors):
            if len(vector) != dimensions:
                raise RuntimeError(f"Embedding vector at index {idx} has inconsistent dimension {len(vector)}.")

        latency_ms = int((time.perf_counter() - started) * 1000)
        log_event(
            "emb_embed_success",
            requestId=request_id,
            purpose=purpose,
            inputCount=len(payload.inputs),
            dimensions=dimensions,
            modelRef=model_ref,
            latencyMs=latency_ms,
        )

        return JSONResponse(
            status_code=200,
            content={
                "requestId": request_id,
                "data": [{"index": idx, "embedding": vector} for idx, vector in enumerate(vectors)],
                "dimensions": dimensions,
                "modelRef": model_ref,
                "latencyMs": latency_ms,
            },
        )
    except Exception as exc:
        latency_ms = int((time.perf_counter() - started) * 1000)
        log_event(
            "emb_embed_failed",
            requestId=request_id,
            purpose=purpose,
            inputCount=len(payload.inputs),
            modelRef=model_ref,
            latencyMs=latency_ms,
            errorType=type(exc).__name__,
            error=str(exc),
        )
        return JSONResponse(
            status_code=500,
            content={
                "requestId": request_id,
                "error": "embedding_failed",
                "errorType": type(exc).__name__,
                "message": str(exc),
            },
        )


if __name__ == "__main__":
    host = os.getenv("GA_EMB_HOST", "127.0.0.1")
    port = parse_positive_int(os.getenv("GA_EMB_PORT"), 8085)
    log_level = os.getenv("GA_EMB_LOG_LEVEL", "info").lower()
    access_log_enabled = env_flag("GA_EMB_UVICORN_ACCESS_LOG", default=False)
    if access_log_enabled:
        configure_uvicorn_access_log_filters(
            ignore_health_requests=env_flag("GA_EMB_SUPPRESS_HEALTH_ACCESS_LOGS", default=True)
        )
    uvicorn.run(APP, host=host, port=port, log_level=log_level, access_log=access_log_enabled)
