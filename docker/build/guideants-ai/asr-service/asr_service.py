import gc
import json
import logging
import os
import tempfile
import threading
import time
import uuid
from datetime import datetime, timezone
from typing import Any

import soundfile as sf
import torch
import uvicorn
from fastapi import FastAPI, File, Form, HTTPException, Request, UploadFile
from fastapi.responses import JSONResponse
from pydantic import BaseModel


def utc_now_iso() -> str:
    return datetime.now(timezone.utc).isoformat()


def env_flag(name: str, default: bool = False) -> bool:
    raw = os.getenv(name)
    if raw is None:
        return default
    return raw.strip().lower() in {"1", "true", "yes", "on"}


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


def resolve_dtype(dtype_name: str | None) -> torch.dtype:
    normalized = (dtype_name or "").strip().lower()
    mapping = {
        "float16": torch.float16,
        "fp16": torch.float16,
        "bfloat16": torch.bfloat16,
        "bf16": torch.bfloat16,
        "float32": torch.float32,
        "fp32": torch.float32,
    }
    if normalized in mapping:
        return mapping[normalized]
    return torch.bfloat16 if torch.cuda.is_available() else torch.float32


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
        return 0.0
    return 0.0


def sanitize_for_log(value: str, max_chars: int) -> tuple[str, bool]:
    normalized = " ".join(value.split())
    if max_chars <= 0:
        max_chars = 320
    if len(normalized) <= max_chars:
        return normalized, False
    return normalized[:max_chars], True


def extract_transcription_fields(results: Any) -> tuple[str, str | None]:
    if not results:
        return "", None

    first = results[0]
    text = getattr(first, "text", "") or ""
    language = getattr(first, "language", None)

    if not text and isinstance(first, dict):
        text = str(first.get("text") or "")
        language = first.get("language")

    if language is not None:
        language = str(language)

    return str(text), language


class LoadModelRequest(BaseModel):
    model_id: str | None = None
    model_path: str | None = None
    dtype: str | None = None
    device_map: str | None = None
    max_inference_batch_size: int | None = None
    max_new_tokens: int | None = None
    # Single, server-resolved Hugging Face token stamped in by the .NET web
    # layer. Used when `model_id` triggers an implicit HF download. Not read
    # from env — the web API is the only source.
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


class AsrRuntimeState:
    def __init__(self) -> None:
        self.lock = threading.RLock()
        self.model: Any = None
        self.model_ref: str | None = None
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

    def is_loaded(self) -> bool:
        with self.lock:
            return self.model is not None

    def snapshot(self) -> dict[str, Any]:
        with self.lock:
            return {
                "loaded": self.model is not None,
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
            }


STATE = AsrRuntimeState()
APP = FastAPI(title="GuideAnts ASR Service", version="1.0.0")


def unload_model() -> dict[str, Any]:
    """
    Drop the loaded model reference, clear the runtime snapshot fields, and ask
    the allocator to release CUDA memory. Caller must hold ENGINE_LOCK. Safe to
    call when the model is already unloaded.

    An in-flight /transcribe request holding its own reference to the model
    object will continue to work against that reference until it finishes, but
    any request that starts after unload returns will see STATE.model is None
    and get a 503 model_not_loaded response.
    """
    with STATE.lock:
        had_model = STATE.model is not None
        previous_ref = STATE.model_ref
        STATE.model = None
        STATE.model_ref = None
        STATE.loaded_at_utc = None
        STATE.load_error = None
        STATE.warmup_ran = False
        STATE.warmup_succeeded = False
        STATE.warmup_latency_ms = 0
        STATE.warmup_error = None
        STATE.warmup_audio_path = None
        STATE.warmup_completed_at_utc = None
    gc.collect()
    try:
        if torch.cuda.is_available():
            torch.cuda.empty_cache()
    except Exception:
        pass
    return {"wasLoaded": had_model, "previousModelRef": previous_ref}
MODEL_LOAD_LOCK = threading.Lock()
MODEL_OPS_LOCK = threading.Lock()
MODEL_DOWNLOAD_OPERATIONS: dict[str, dict[str, Any]] = {}


def resolve_model_target(request: LoadModelRequest) -> str:
    model_dir = os.getenv("GA_ASR_MODEL_DIR", "/models-local/asr")
    default_model_path = os.getenv("GA_ASR_DEFAULT_MODEL_PATH", "").strip()
    default_model_id = os.getenv("GA_ASR_DEFAULT_MODEL_ID", "Qwen/Qwen3-ASR-0.6B").strip()

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


def get_model_dir() -> str:
    return os.getenv("GA_ASR_MODEL_DIR", "/models-local/asr")


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


def start_download_operation(request: DownloadModelRequest) -> dict[str, Any]:
    operation_id = uuid.uuid4().hex
    operation = {
        "operationId": operation_id,
        "status": "queued",
        "modelId": request.model_id,
        "error": None,
        "modelRef": None,
        "startedAtUtc": utc_now_iso(),
        "completedAtUtc": None,
    }
    with MODEL_OPS_LOCK:
        MODEL_DOWNLOAD_OPERATIONS[operation_id] = operation

    def _run() -> None:
        with MODEL_OPS_LOCK:
            MODEL_DOWNLOAD_OPERATIONS[operation_id]["status"] = "running"
        model_dir = get_model_dir()
        os.makedirs(model_dir, exist_ok=True)
        target_name = request.model_id.replace("/", "--")
        target_path = os.path.join(model_dir, target_name)
        try:
            from huggingface_hub import snapshot_download

            hf_token = (request.hf_token or "").strip() or None
            snapshot_download(
                repo_id=request.model_id,
                revision=request.revision,
                local_dir=target_path,
                local_dir_use_symlinks=False,
                resume_download=True,
                token=hf_token,
            )
            with MODEL_OPS_LOCK:
                MODEL_DOWNLOAD_OPERATIONS[operation_id]["status"] = "completed"
                MODEL_DOWNLOAD_OPERATIONS[operation_id]["modelRef"] = target_name
                MODEL_DOWNLOAD_OPERATIONS[operation_id]["completedAtUtc"] = utc_now_iso()
        except Exception as exc:
            with MODEL_OPS_LOCK:
                MODEL_DOWNLOAD_OPERATIONS[operation_id]["status"] = "failed"
                MODEL_DOWNLOAD_OPERATIONS[operation_id]["error"] = str(exc)
                MODEL_DOWNLOAD_OPERATIONS[operation_id]["completedAtUtc"] = utc_now_iso()

    threading.Thread(target=_run, daemon=True).start()
    return operation


def load_model(request: LoadModelRequest) -> dict[str, Any]:
    from qwen_asr import Qwen3ASRModel

    # transformers / huggingface_hub pick up the HF token from the process
    # environment. When the .NET layer resolved a token for this request we
    # install it here before from_pretrained triggers any implicit download,
    # so the single configured token is what gets used.
    hf_token = (request.hf_token or "").strip()
    if hf_token:
        os.environ["HF_TOKEN"] = hf_token

    target = resolve_model_target(request)
    dtype = resolve_dtype(request.dtype or os.getenv("GA_ASR_DTYPE"))

    default_device_map = os.getenv("GA_ASR_DEVICE_MAP")
    if not default_device_map:
        default_device_map = "auto"
    device_map = request.device_map or default_device_map

    max_batch_size = request.max_inference_batch_size
    if max_batch_size is None:
        max_batch_size = int(os.getenv("GA_ASR_MAX_INFERENCE_BATCH_SIZE", "8"))

    max_new_tokens = request.max_new_tokens
    if max_new_tokens is None:
        max_new_tokens = int(os.getenv("GA_ASR_MAX_NEW_TOKENS", "512"))

    init_started = time.perf_counter()
    model = Qwen3ASRModel.from_pretrained(
        target,
        dtype=dtype,
        device_map=device_map,
        max_inference_batch_size=max_batch_size,
        max_new_tokens=max_new_tokens,
    )
    warmup_details = run_model_warmup(model, target)
    if warmup_details["warmupEnabled"] and not warmup_details["warmupSucceeded"]:
        warmup_error = warmup_details.get("warmupError", "ASR warmup failed.")
        raise RuntimeError(str(warmup_error))
    init_latency_ms = int((time.perf_counter() - init_started) * 1000)

    with STATE.lock:
        STATE.model = model
        STATE.model_ref = target
        STATE.loaded_at_utc = utc_now_iso()
        STATE.warmup_enabled = bool(warmup_details.get("warmupEnabled", False))
        STATE.warmup_ran = bool(warmup_details.get("warmupRan", False))
        STATE.warmup_succeeded = bool(warmup_details.get("warmupSucceeded", False))
        STATE.warmup_latency_ms = int(warmup_details.get("warmupLatencyMs", 0) or 0)
        STATE.warmup_error = warmup_details.get("warmupError")
        STATE.warmup_audio_path = warmup_details.get("warmupAudioPath")
        STATE.warmup_completed_at_utc = warmup_details.get("warmupCompletedAtUtc")

    return {
        "modelRef": target,
        "loadedAtUtc": STATE.loaded_at_utc,
        "loadLatencyMs": init_latency_ms,
        "dtype": str(dtype),
        "deviceMap": device_map,
        "maxInferenceBatchSize": max_batch_size,
        "maxNewTokens": max_new_tokens,
        **warmup_details,
    }


def run_model_warmup(model: Any, model_ref: str) -> dict[str, Any]:
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

        warmup_log_max_chars = int(os.getenv("GA_ASR_WARMUP_LOG_TEXT_MAX_CHARS", "320"))
        results = model.transcribe(audio=warmup_audio_path, language=language)
        warmup_text_raw, detected_language = extract_transcription_fields(results)
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


def load_model_serialized(request: LoadModelRequest) -> dict[str, Any]:
    with MODEL_LOAD_LOCK:
        with STATE.lock:
            STATE.loading = True
            STATE.load_error = None
        try:
            return load_model(request)
        except Exception as exc:
            with STATE.lock:
                STATE.load_error = str(exc)
            raise
        finally:
            with STATE.lock:
                STATE.loading = False


@APP.on_event("startup")
async def on_startup() -> None:
    startup_details = {
        "host": os.getenv("GA_ASR_HOST", "127.0.0.1"),
        "port": int(os.getenv("GA_ASR_PORT", "8082")),
        "modelDir": os.getenv("GA_ASR_MODEL_DIR", "/models-local/asr"),
    }
    log_event(
        "asr_service_startup",
        **startup_details,
    )

    if env_flag("GA_ASR_AUTO_LOAD_ON_STARTUP", default=False):
        startup_request = LoadModelRequest()
        startup_target = resolve_model_target(startup_request)
        log_event("asr_model_autoload_start", modelTarget=startup_target, **startup_details)

        def _autoload_worker() -> None:
            try:
                details = load_model_serialized(startup_request)
                log_event("asr_model_autoload_success", **details)
            except Exception as exc:
                log_event(
                    "asr_model_autoload_failed",
                    modelTarget=startup_target,
                    errorType=type(exc).__name__,
                    error=str(exc),
                )

        threading.Thread(target=_autoload_worker, name="asr-autoload", daemon=True).start()


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
    return JSONResponse(status_code=200, content={"ready": True, **snapshot})


@APP.post("/admin/load")
async def admin_load(request: Request, payload: LoadModelRequest) -> JSONResponse:
    request_id = request.headers.get("x-request-id", str(uuid.uuid4()))
    log_event("asr_model_load_start", requestId=request_id, payload=payload.model_dump())
    try:
        details = load_model_serialized(payload)
        log_event("asr_model_load_success", requestId=request_id, **details)
        return JSONResponse(status_code=200, content={"requestId": request_id, "status": "loaded", **details})
    except Exception as exc:
        log_event("asr_model_load_failed", requestId=request_id, errorType=type(exc).__name__, error=str(exc))
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
    Drop the loaded ASR model so the container releases GPU/RAM without a
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
    operation = start_download_operation(payload)
    return JSONResponse(status_code=202, content=operation)


@APP.get("/admin/models/{operation_id}")
async def admin_download_status(operation_id: str) -> JSONResponse:
    with MODEL_OPS_LOCK:
        operation = MODEL_DOWNLOAD_OPERATIONS.get(operation_id)
        if operation is None:
            raise HTTPException(status_code=404, detail="operation not found")
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
        import shutil

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

        duration_seconds = get_audio_duration_seconds(temp_path)

        with STATE.lock:
            model = STATE.model
            model_ref = STATE.model_ref

        results = model.transcribe(
            audio=temp_path,
            language=language if language else None,
        )
        first = results[0] if results else None
        text = getattr(first, "text", "") if first is not None else ""
        detected_language = getattr(first, "language", None) if first is not None else None

        latency_ms = int((time.perf_counter() - started) * 1000)
        log_event(
            "asr_transcribe_success",
            requestId=request_id,
            modelRef=model_ref,
            latencyMs=latency_ms,
            payloadSizeBucket=size_bucket,
            durationSeconds=duration_seconds,
            detectedLanguage=detected_language,
            textLength=len(text),
        )

        return JSONResponse(
            status_code=200,
            content={
                "requestId": request_id,
                "text": text,
                "language": detected_language,
                "durationSeconds": int(round(duration_seconds)),
                "modelRef": model_ref,
                "latencyMs": latency_ms,
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
        return JSONResponse(
            status_code=500,
            content={
                "requestId": request_id,
                "error": "transcription_failed",
                "errorType": type(exc).__name__,
                "message": str(exc),
            },
        )
    finally:
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
