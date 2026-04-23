import gc
import inspect
import io
import json
import logging
import os
import re
import threading
import time
import uuid
from datetime import datetime, timezone
from typing import Any

import numpy as np
import soundfile as sf
import torch
import uvicorn
from fastapi import FastAPI, HTTPException, Request
from fastapi.responses import JSONResponse, Response
from pydantic import BaseModel, Field


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


def parse_positive_float(value: str | None, default: float) -> float:
    if not value:
        return default
    try:
        parsed = float(value)
        if parsed > 0:
            return parsed
    except ValueError:
        pass
    return default


def normalize_script_input(text: str) -> str:
    stripped = text.strip()
    if not stripped:
        return ""

    lines = [line.strip() for line in stripped.splitlines() if line.strip()]
    speaker_pattern = re.compile(r"^speaker\s+\d+\s*:", re.IGNORECASE)
    if lines and all(speaker_pattern.match(line) for line in lines):
        return "\n".join(lines)

    compact = re.sub(r"\s+", " ", stripped)
    return f"Speaker 1: {compact}"


def build_default_voice_sample() -> tuple[np.ndarray, int]:
    sample_rate = parse_positive_int(os.getenv("GA_TTS_SAMPLE_RATE"), 24_000)
    seconds = parse_positive_float(os.getenv("GA_TTS_DEFAULT_VOICE_SECONDS"), 1.0)
    frame_count = max(1, int(sample_rate * seconds))
    return np.zeros(frame_count, dtype=np.float32), sample_rate


class LoadModelRequest(BaseModel):
    model_id: str | None = None
    model_path: str | None = None
    tokenizer_id: str | None = None
    tokenizer_path: str | None = None
    dtype: str | None = None
    device_map: str | None = None
    # Single, server-resolved Hugging Face token stamped in by the .NET web
    # layer. Used when `model_id` / `tokenizer_id` trigger implicit HF
    # downloads. Not read from env — the web API is the only source.
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
    tokenizer_id: str | None = None
    revision: str | None = None
    hf_token: str | None = None


class SynthesizeRequest(BaseModel):
    text: str = Field(min_length=1)


class TtsRuntimeState:
    def __init__(self) -> None:
        self.lock = threading.RLock()
        self.model: Any = None
        self.processor: Any = None
        self.model_ref: str | None = None
        self.tokenizer_ref: str | None = None
        self.loaded_at_utc: str | None = None
        self.dtype: str | None = None
        self.device_map: str | None = None
        self.pipeline_task: str | None = None
        self.device: str | None = None
        self.max_new_tokens: int = 512
        self.default_voice_sample: np.ndarray | None = None
        self.sampling_rate: int = 24_000

    def is_loaded(self) -> bool:
        with self.lock:
            return self.model is not None and self.processor is not None

    def snapshot(self) -> dict[str, Any]:
        with self.lock:
            return {
                "loaded": self.model is not None and self.processor is not None,
                "modelRef": self.model_ref,
                "tokenizerRef": self.tokenizer_ref,
                "loadedAtUtc": self.loaded_at_utc,
                "dtype": self.dtype,
                "deviceMap": self.device_map,
                "pipelineTask": self.pipeline_task,
                "device": self.device,
                "maxNewTokens": self.max_new_tokens,
            }


STATE = TtsRuntimeState()
APP = FastAPI(title="GuideAnts TTS Service", version="1.0.0")
MODEL_OPS_LOCK = threading.Lock()
MODEL_LOAD_LOCK = threading.Lock()
MODEL_DOWNLOAD_OPERATIONS: dict[str, dict[str, Any]] = {}


def unload_model() -> dict[str, Any]:
    """
    Drop the loaded TTS model + processor and release CUDA memory. Caller must
    hold MODEL_LOAD_LOCK. Safe to call when nothing is loaded.

    An in-flight /synthesize call that has already taken a reference to the
    model will keep running against that reference; any request that starts
    after unload returns will see STATE.model is None and fail fast.
    """
    with STATE.lock:
        had_model = STATE.model is not None or STATE.processor is not None
        previous_ref = STATE.model_ref
        STATE.model = None
        STATE.processor = None
        STATE.model_ref = None
        STATE.tokenizer_ref = None
        STATE.loaded_at_utc = None
        STATE.dtype = None
        STATE.device_map = None
        STATE.pipeline_task = None
        STATE.device = None
        STATE.default_voice_sample = None
    gc.collect()
    try:
        if torch.cuda.is_available():
            torch.cuda.empty_cache()
    except Exception:
        pass
    return {"wasLoaded": had_model, "previousModelRef": previous_ref}


def resolve_model_target(request: LoadModelRequest) -> str:
    model_dir = os.getenv("GA_TTS_MODEL_DIR", "/models-local/tts")
    default_model_path = os.getenv("GA_TTS_DEFAULT_MODEL_PATH", "").strip()
    default_model_id = os.getenv("GA_TTS_DEFAULT_MODEL_ID", "microsoft/VibeVoice-1.5B").strip()

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


def resolve_tokenizer_target(request: LoadModelRequest) -> str | None:
    model_dir = os.getenv("GA_TTS_MODEL_DIR", "/models-local/tts")
    default_tokenizer_path = os.getenv("GA_TTS_TOKENIZER_PATH", "").strip()
    default_tokenizer_id = os.getenv("GA_TTS_TOKENIZER_ID", "Qwen/Qwen2.5-1.5B").strip()

    tokenizer_path = request.tokenizer_path.strip() if request.tokenizer_path else ""
    if tokenizer_path:
        candidate = tokenizer_path
        if not os.path.isabs(candidate):
            candidate = os.path.join(model_dir, candidate)
        if os.path.exists(candidate):
            return candidate
        return tokenizer_path

    if request.tokenizer_id:
        return request.tokenizer_id.strip()

    if default_tokenizer_path:
        candidate = default_tokenizer_path
        if not os.path.isabs(candidate):
            candidate = os.path.join(model_dir, candidate)
        if os.path.exists(candidate):
            return candidate

    return default_tokenizer_id if default_tokenizer_id else None


def get_model_dir() -> str:
    return os.getenv("GA_TTS_MODEL_DIR", "/models-local/tts")


def list_model_entries() -> list[dict[str, Any]]:
    model_dir = get_model_dir()
    os.makedirs(model_dir, exist_ok=True)
    snapshot = STATE.snapshot()
    active_model = snapshot.get("modelRef")
    active_tokenizer = snapshot.get("tokenizerRef")
    items: list[dict[str, Any]] = []
    for name in sorted(os.listdir(model_dir)):
        full_path = os.path.join(model_dir, name)
        items.append(
            {
                "modelRef": name,
                "path": full_path,
                "isDirectory": os.path.isdir(full_path),
                "activeModel": bool(active_model and (active_model == name or active_model == full_path)),
                "activeTokenizer": bool(active_tokenizer and (active_tokenizer == name or active_tokenizer == full_path)),
            }
        )
    return items


def start_download_operation(request: DownloadModelRequest) -> dict[str, Any]:
    operation_id = uuid.uuid4().hex
    operation = {
        "operationId": operation_id,
        "status": "queued",
        "modelId": request.model_id,
        "tokenizerId": request.tokenizer_id,
        "error": None,
        "modelRef": None,
        "tokenizerRef": None,
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
        model_target_name = request.model_id.replace("/", "--")
        model_target_path = os.path.join(model_dir, model_target_name)
        tokenizer_target_name = None
        try:
            from huggingface_hub import snapshot_download

            hf_token = (request.hf_token or "").strip() or None
            snapshot_download(
                repo_id=request.model_id,
                revision=request.revision,
                local_dir=model_target_path,
                local_dir_use_symlinks=False,
                resume_download=True,
                token=hf_token,
            )
            if request.tokenizer_id:
                tokenizer_target_name = request.tokenizer_id.replace("/", "--")
                tokenizer_target_path = os.path.join(model_dir, tokenizer_target_name)
                snapshot_download(
                    repo_id=request.tokenizer_id,
                    revision=request.revision,
                    local_dir=tokenizer_target_path,
                    local_dir_use_symlinks=False,
                    resume_download=True,
                    token=hf_token,
                )

            with MODEL_OPS_LOCK:
                MODEL_DOWNLOAD_OPERATIONS[operation_id]["status"] = "completed"
                MODEL_DOWNLOAD_OPERATIONS[operation_id]["modelRef"] = model_target_name
                MODEL_DOWNLOAD_OPERATIONS[operation_id]["tokenizerRef"] = tokenizer_target_name
                MODEL_DOWNLOAD_OPERATIONS[operation_id]["completedAtUtc"] = utc_now_iso()
        except Exception as exc:
            with MODEL_OPS_LOCK:
                MODEL_DOWNLOAD_OPERATIONS[operation_id]["status"] = "failed"
                MODEL_DOWNLOAD_OPERATIONS[operation_id]["error"] = str(exc)
                MODEL_DOWNLOAD_OPERATIONS[operation_id]["completedAtUtc"] = utc_now_iso()

    threading.Thread(target=_run, daemon=True).start()
    return operation


def ensure_vibevoice_registration() -> None:
    from transformers import AutoConfig
    from transformers.cache_utils import DynamicCache
    from transformers.generation.utils import GenerationMixin

    from vibevoice.modular.configuration_vibevoice import (
        VibeVoiceAcousticTokenizerConfig,
        VibeVoiceConfig,
        VibeVoiceDiffusionHeadConfig,
        VibeVoiceSemanticTokenizerConfig,
    )

    import vibevoice.modular.modular_vibevoice_diffusion_head  # noqa: F401
    import vibevoice.modular.modular_vibevoice_tokenizer  # noqa: F401
    import vibevoice.modular.modeling_vibevoice_inference  # noqa: F401

    for model_type, config_cls in [
        ("vibevoice", VibeVoiceConfig),
        ("vibevoice_acoustic_tokenizer", VibeVoiceAcousticTokenizerConfig),
        ("vibevoice_semantic_tokenizer", VibeVoiceSemanticTokenizerConfig),
        ("vibevoice_diffusion_head", VibeVoiceDiffusionHeadConfig),
    ]:
        try:
            AutoConfig.register(model_type, config_cls)
        except ValueError:
            pass

    if getattr(GenerationMixin, "_ga_vibevoice_cache_patch", False):
        return

    # vibevoice accesses legacy DynamicCache.key_cache/value_cache lists, while newer
    # transformers exposes cache tensors via cache layers.
    if not hasattr(DynamicCache, "key_cache"):
        DynamicCache.key_cache = property(lambda self: [layer.keys for layer in self.layers])  # type: ignore[attr-defined]
    if not hasattr(DynamicCache, "value_cache"):
        DynamicCache.value_cache = property(lambda self: [layer.values for layer in self.layers])  # type: ignore[attr-defined]

    original_prepare_cache = GenerationMixin._prepare_cache_for_generation
    signature = inspect.signature(original_prepare_cache)

    if len(signature.parameters) == 6:

        def _compat_prepare_cache(self: Any, generation_config: Any, model_kwargs: Any, assistant_model: Any, batch_size: int, max_cache_length: int, *_args: Any, **_kwargs: Any) -> Any:
            return original_prepare_cache(self, generation_config, model_kwargs, assistant_model, batch_size, max_cache_length)

        GenerationMixin._prepare_cache_for_generation = _compat_prepare_cache

    GenerationMixin._ga_vibevoice_cache_patch = True


def ensure_generation_compatibility(model: Any) -> None:
    config = model.config
    decoder_config = getattr(config, "decoder_config", None)
    if decoder_config is None:
        return

    for field in [
        "hidden_size",
        "num_hidden_layers",
        "num_attention_heads",
        "num_key_value_heads",
        "vocab_size",
        "max_position_embeddings",
    ]:
        if not hasattr(config, field) and hasattr(decoder_config, field):
            setattr(config, field, getattr(decoder_config, field))


def load_model(request: LoadModelRequest) -> dict[str, Any]:
    from transformers import AutoModelForCausalLM
    from vibevoice.processor.vibevoice_processor import VibeVoiceProcessor

    ensure_vibevoice_registration()

    # transformers / huggingface_hub pick up the HF token from the process
    # environment. When the .NET layer resolved a token for this request we
    # install it here before from_pretrained triggers any implicit download,
    # so the single configured token is what gets used.
    hf_token = (request.hf_token or "").strip()
    if hf_token:
        os.environ["HF_TOKEN"] = hf_token

    target = resolve_model_target(request)
    tokenizer_target = resolve_tokenizer_target(request)
    dtype = resolve_dtype(request.dtype or os.getenv("GA_TTS_DTYPE"))
    requested_device_map = (request.device_map or os.getenv("GA_TTS_DEVICE_MAP") or "auto").strip() or "auto"
    max_new_tokens = parse_positive_int(os.getenv("GA_TTS_MAX_NEW_TOKENS"), 512)

    started = time.perf_counter()

    model = AutoModelForCausalLM.from_pretrained(target, torch_dtype=dtype)
    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    model.to(device)
    model.eval()
    ensure_generation_compatibility(model)

    processor_kwargs: dict[str, Any] = {}
    if tokenizer_target:
        processor_kwargs["language_model_pretrained_name"] = tokenizer_target

    processor = VibeVoiceProcessor.from_pretrained(target, **processor_kwargs)
    default_voice_sample, sample_rate = build_default_voice_sample()

    load_latency_ms = int((time.perf_counter() - started) * 1000)

    with STATE.lock:
        STATE.model = model
        STATE.processor = processor
        STATE.model_ref = target
        STATE.tokenizer_ref = tokenizer_target
        STATE.loaded_at_utc = utc_now_iso()
        STATE.dtype = str(dtype)
        STATE.device_map = requested_device_map
        STATE.pipeline_task = "vibevoice-generate"
        STATE.device = str(device)
        STATE.max_new_tokens = max_new_tokens
        STATE.default_voice_sample = default_voice_sample
        STATE.sampling_rate = sample_rate

    if requested_device_map == "auto":
        log_event(
            "tts_device_map_auto_accepted",
            requestedDeviceMap=requested_device_map,
            effectiveDevice=str(device),
            note="VibeVoice runtime currently executes on a single selected device.",
        )

    return {
        "modelRef": target,
        "tokenizerRef": tokenizer_target,
        "loadedAtUtc": STATE.loaded_at_utc,
        "loadLatencyMs": load_latency_ms,
        "dtype": str(dtype),
        "deviceMap": requested_device_map,
        "pipelineTask": "vibevoice-generate",
        "device": str(device),
        "maxNewTokens": max_new_tokens,
        "sampleRate": sample_rate,
    }


def synthesize_wav_bytes(model: Any, processor: Any, script_text: str, default_voice_sample: np.ndarray, sample_rate: int, max_new_tokens: int) -> tuple[bytes, float, int]:
    device = next(model.parameters()).device

    inputs = processor(
        text=script_text,
        voice_samples=[default_voice_sample],
        return_tensors="pt",
    )

    for key, value in list(inputs.items()):
        if torch.is_tensor(value):
            inputs[key] = value.to(device)

    with torch.no_grad():
        generation_output = model.generate(
            **inputs,
            tokenizer=processor.tokenizer,
            max_new_tokens=max_new_tokens,
        )

    speech_outputs = getattr(generation_output, "speech_outputs", None)
    if not speech_outputs:
        raise RuntimeError("VibeVoice generate returned no speech outputs.")

    audio_tensor = speech_outputs[0]
    if audio_tensor is None:
        raise RuntimeError("VibeVoice generate produced an empty speech output for the first sample.")

    if isinstance(audio_tensor, torch.Tensor):
        waveform = audio_tensor.detach().float().cpu().numpy()
    else:
        waveform = np.asarray(audio_tensor)

    if waveform.ndim == 2:
        if waveform.shape[0] == 1:
            waveform = waveform[0]
        elif waveform.shape[1] == 1:
            waveform = waveform[:, 0]

    if waveform.ndim != 1:
        raise RuntimeError(f"Unexpected waveform rank {waveform.ndim}; expected mono output.")

    if waveform.size == 0:
        raise RuntimeError("Generated waveform is empty.")

    clipped = np.clip(waveform.astype(np.float32), -1.0, 1.0)
    duration_seconds = float(clipped.shape[0]) / float(sample_rate)

    buffer = io.BytesIO()
    sf.write(buffer, clipped, sample_rate, format="WAV")
    return buffer.getvalue(), duration_seconds, sample_rate


@APP.on_event("startup")
async def on_startup() -> None:
    startup_details = {
        "host": os.getenv("GA_TTS_HOST", "127.0.0.1"),
        "port": int(os.getenv("GA_TTS_PORT", "8084")),
        "modelDir": os.getenv("GA_TTS_MODEL_DIR", "/models-local/tts"),
    }
    log_event("tts_service_startup", **startup_details)

    if env_flag("GA_TTS_AUTO_LOAD_ON_STARTUP", default=False):
        startup_request = LoadModelRequest()
        startup_target = resolve_model_target(startup_request)
        log_event("tts_model_autoload_start", modelTarget=startup_target, **startup_details)
        try:
            with MODEL_LOAD_LOCK:
                details = load_model(startup_request)
            log_event("tts_model_autoload_success", **details)
        except Exception as exc:
            log_event(
                "tts_model_autoload_failed",
                modelTarget=startup_target,
                errorType=type(exc).__name__,
                error=str(exc),
            )


@APP.get("/health")
async def health() -> dict[str, Any]:
    return {
        "status": "ok",
        "cudaAvailable": torch.cuda.is_available(),
        **STATE.snapshot(),
    }


@APP.get("/ready")
async def ready() -> JSONResponse:
    snapshot = STATE.snapshot()
    if not snapshot["loaded"]:
        return JSONResponse(status_code=503, content={"ready": False, **snapshot})
    return JSONResponse(status_code=200, content={"ready": True, **snapshot})


@APP.post("/admin/load")
async def admin_load(request: Request, payload: LoadModelRequest) -> JSONResponse:
    request_id = request.headers.get("x-request-id", str(uuid.uuid4()))
    log_event("tts_model_load_start", requestId=request_id, payload=payload.model_dump())
    # Serialize loads against concurrent loads and against /admin/unload so the
    # two can't trample each other's STATE writes.
    with MODEL_LOAD_LOCK:
        try:
            details = load_model(payload)
            log_event("tts_model_load_success", requestId=request_id, **details)
            return JSONResponse(status_code=200, content={"requestId": request_id, "status": "loaded", **details})
        except Exception as exc:
            log_event("tts_model_load_failed", requestId=request_id, errorType=type(exc).__name__, error=str(exc))
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
    Drop the loaded TTS model + processor so the container releases GPU/RAM
    without a restart. Serialized with /admin/load via MODEL_LOAD_LOCK; if a
    load is in flight, this returns 409 rather than blocking a worker.
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
            log_event("tts_model_unload_noop", requestId=request_id)
            return JSONResponse(
                status_code=200,
                content={
                    "requestId": request_id,
                    "ok": True,
                    "action": "noop-already-unloaded",
                    **STATE.snapshot(),
                },
            )
        log_event("tts_model_unload_start", requestId=request_id)
        result = unload_model()
        log_event(
            "tts_model_unload_success",
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

    snapshot = STATE.snapshot()
    active_model = snapshot.get("modelRef")
    active_tokenizer = snapshot.get("tokenizerRef")
    if active_model and (active_model == model_ref or str(active_model).endswith(model_ref)):
        raise HTTPException(status_code=409, detail="cannot delete active model")
    if active_tokenizer and (active_tokenizer == model_ref or str(active_tokenizer).endswith(model_ref)):
        raise HTTPException(status_code=409, detail="cannot delete active tokenizer")

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


@APP.post("/synthesize")
async def synthesize(request: Request, payload: SynthesizeRequest) -> Response:
    request_id = request.headers.get("x-request-id", str(uuid.uuid4()))
    traceparent = request.headers.get("traceparent")
    text = payload.text.strip()
    if not text:
        return JSONResponse(
            status_code=400,
            content={"requestId": request_id, "error": "invalid_text", "message": "Text cannot be empty."},
        )

    script_text = normalize_script_input(text)
    if not script_text:
        return JSONResponse(
            status_code=400,
            content={"requestId": request_id, "error": "invalid_text", "message": "Text cannot be empty after normalization."},
        )

    if not STATE.is_loaded():
        return JSONResponse(
            status_code=503,
            content={
                "requestId": request_id,
                "error": "model_not_loaded",
                "message": "Load a model with /admin/load before synthesizing.",
            },
        )

    started = time.perf_counter()
    with STATE.lock:
        model = STATE.model
        processor = STATE.processor
        model_ref = STATE.model_ref
        max_new_tokens = STATE.max_new_tokens
        default_voice_sample = STATE.default_voice_sample
        sample_rate = STATE.sampling_rate

        if default_voice_sample is None:
            default_voice_sample, sample_rate = build_default_voice_sample()
            STATE.default_voice_sample = default_voice_sample
            STATE.sampling_rate = sample_rate

    log_event(
        "tts_synthesize_start",
        requestId=request_id,
        traceparent=traceparent,
        textLength=len(script_text),
        modelRef=model_ref,
        maxNewTokens=max_new_tokens,
    )

    try:
        with STATE.lock:
            wav_bytes, duration_seconds, sampling_rate = synthesize_wav_bytes(
                model=model,
                processor=processor,
                script_text=script_text,
                default_voice_sample=default_voice_sample,
                sample_rate=sample_rate,
                max_new_tokens=max_new_tokens,
            )

        latency_ms = int((time.perf_counter() - started) * 1000)

        response = Response(content=wav_bytes, media_type="audio/wav")
        response.headers["x-request-id"] = request_id
        response.headers["x-model-ref"] = model_ref or ""
        response.headers["x-audio-duration-seconds"] = f"{duration_seconds:.3f}"
        response.headers["x-sampling-rate"] = str(sampling_rate)

        log_event(
            "tts_synthesize_success",
            requestId=request_id,
            traceparent=traceparent,
            latencyMs=latency_ms,
            textLength=len(script_text),
            modelRef=model_ref,
            durationSeconds=duration_seconds,
            samplingRate=sampling_rate,
            outputBytes=len(wav_bytes),
        )
        return response
    except Exception as exc:
        latency_ms = int((time.perf_counter() - started) * 1000)
        log_event(
            "tts_synthesize_failed",
            requestId=request_id,
            traceparent=traceparent,
            latencyMs=latency_ms,
            textLength=len(script_text),
            modelRef=model_ref,
            errorType=type(exc).__name__,
            error=str(exc),
        )
        return JSONResponse(
            status_code=500,
            content={
                "requestId": request_id,
                "error": "synthesis_failed",
                "errorType": type(exc).__name__,
                "message": str(exc),
                "modelRef": model_ref,
            },
        )


if __name__ == "__main__":
    host = os.getenv("GA_TTS_HOST", "127.0.0.1")
    port = int(os.getenv("GA_TTS_PORT", "8084"))
    log_level = os.getenv("GA_TTS_LOG_LEVEL", "info").lower()
    access_log_enabled = env_flag("GA_TTS_UVICORN_ACCESS_LOG", default=False)
    if access_log_enabled:
        configure_uvicorn_access_log_filters(
            ignore_health_requests=env_flag("GA_TTS_SUPPRESS_HEALTH_ACCESS_LOGS", default=True)
        )
    uvicorn.run(APP, host=host, port=port, log_level=log_level, access_log=access_log_enabled)
