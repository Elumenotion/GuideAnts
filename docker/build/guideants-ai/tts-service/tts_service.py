import gc
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

MODEL_PATH_RE = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")
KOKORO_VOICE_RE = re.compile(r"^[a-z]{2}_[a-z0-9_]{1,64}$")


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

    return re.sub(r"\s+", " ", stripped)


class LoadModelRequest(BaseModel):
    model_id: str | None = None
    model_path: str | None = None
    tokenizer_id: str | None = None
    tokenizer_path: str | None = None
    dtype: str | None = None
    device_map: str | None = None
    # Single, server-resolved Hugging Face token stamped in by the .NET web
    # layer. Used when `model_id` triggers implicit HF downloads. Not read
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
    tokenizer_id: str | None = None
    revision: str | None = None
    hf_token: str | None = None


class SynthesizeRequest(BaseModel):
    text: str = Field(min_length=1)
    voice: str | None = None
    lang_code: str | None = None
    speed: float | None = None


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
        self.sampling_rate: int = 24_000
        self.voice: str = "af_heart"
        self.lang_code: str = "a"
        self.speed: float = 1.0
        self.local_model_dir: str | None = None

    def is_loaded(self) -> bool:
        with self.lock:
            return self.model is not None

    def snapshot(self) -> dict[str, Any]:
        with self.lock:
            return {
                # Kokoro stores the pipeline on STATE.model; processor stays None.
                "loaded": self.model is not None,
                "modelRef": self.model_ref,
                "tokenizerRef": self.tokenizer_ref,
                "loadedAtUtc": self.loaded_at_utc,
                "dtype": self.dtype,
                "deviceMap": self.device_map,
                "pipelineTask": self.pipeline_task,
                "device": self.device,
                "maxNewTokens": self.max_new_tokens,
                "voice": self.voice,
                "langCode": self.lang_code,
                "speed": self.speed,
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
        STATE.voice = "af_heart"
        STATE.lang_code = "a"
        STATE.speed = 1.0
        STATE.local_model_dir = None
    gc.collect()
    try:
        if torch.cuda.is_available():
            torch.cuda.empty_cache()
    except Exception:
        pass
    return {"wasLoaded": had_model, "previousModelRef": previous_ref}


def resolve_local_model_path(model_path: str) -> str:
    model_dir = os.getenv("GA_TTS_MODEL_DIR", "/models-local/tts")
    requested = model_path.strip()
    if not MODEL_PATH_RE.fullmatch(requested):
        raise ValueError("model_path must be a simple local model name (letters, digits, dot, underscore, hyphen).")

    base_real = os.path.realpath(model_dir)
    candidate = os.path.realpath(os.path.join(base_real, requested))
    if not candidate.startswith(base_real + os.sep):
        raise ValueError("resolved model_path escapes the permitted model directory.")
    if os.path.exists(candidate):
        return candidate
    raise FileNotFoundError(
        f"Configured model_path '{requested}' does not exist under GA_TTS_MODEL_DIR."
    )


def resolve_configured_local_model_path(model_path: str) -> str | None:
    model_dir = os.getenv("GA_TTS_MODEL_DIR", "/models-local/tts")
    requested = model_path.strip()
    if not requested:
        return None

    base_real = os.path.realpath(model_dir)
    if os.path.isabs(requested):
        candidate = os.path.realpath(requested)
        if not candidate.startswith(base_real + os.sep):
            return None
        return candidate if os.path.exists(candidate) else None

    if not MODEL_PATH_RE.fullmatch(requested):
        return None

    candidate = os.path.realpath(os.path.join(base_real, requested))
    if not candidate.startswith(base_real + os.sep):
        return None
    return candidate if os.path.exists(candidate) else None


def resolve_model_reference(request: LoadModelRequest) -> tuple[str, str | None]:
    default_model_path = os.getenv("GA_TTS_DEFAULT_MODEL_PATH", "").strip()
    default_model_id = os.getenv("GA_TTS_DEFAULT_MODEL_ID", "hexgrad/Kokoro-82M").strip()

    if request.model_path:
        local_path = resolve_local_model_path(request.model_path)
        return local_path, local_path

    if request.model_id:
        return request.model_id.strip(), None

    if default_model_path:
        local_path = resolve_configured_local_model_path(default_model_path)
        if local_path:
            return local_path, local_path

    return default_model_id, None


def resolve_model_target(request: LoadModelRequest) -> str:
    target, _ = resolve_model_reference(request)
    return target


def resolve_tokenizer_target(request: LoadModelRequest) -> str | None:
    model_dir = os.getenv("GA_TTS_MODEL_DIR", "/models-local/tts")
    default_tokenizer_path = os.getenv("GA_TTS_TOKENIZER_PATH", "").strip()
    default_tokenizer_id = os.getenv("GA_TTS_TOKENIZER_ID", "").strip()

    tokenizer_path = request.tokenizer_path.strip() if request.tokenizer_path else ""
    if tokenizer_path:
        if not MODEL_PATH_RE.fullmatch(tokenizer_path):
            raise ValueError("tokenizer_path must be a simple local tokenizer name (letters, digits, dot, underscore, hyphen).")
        base_real = os.path.realpath(model_dir)
        candidate = os.path.realpath(os.path.join(base_real, tokenizer_path))
        if not candidate.startswith(base_real + os.sep):
            raise ValueError("resolved tokenizer_path escapes the permitted model directory.")
        if os.path.exists(candidate):
            return candidate
        raise FileNotFoundError(
            f"Configured tokenizer_path '{tokenizer_path}' does not exist under GA_TTS_MODEL_DIR."
        )

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


def canonical_model_folder_name(model_id: str) -> str:
    normalized = (model_id or "").strip().strip("/")
    if not normalized:
        raise ValueError("model_id is required")

    leaf = normalized.split("/")[-1].strip()
    if not leaf:
        raise ValueError("model_id must include a repository name")

    if any(sep in leaf for sep in ("/", "\\", "..")):
        raise ValueError("model_id resolved to an invalid local folder name")

    return leaf


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
        model_target_name = canonical_model_folder_name(request.model_id)
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
                tokenizer_target_name = canonical_model_folder_name(request.tokenizer_id)
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


def resolve_kokoro_lang_code(value: str | None, voice: str | None = None) -> str:
    raw = (value or "").strip().lower()
    aliases = {
        "american": "a",
        "american english": "a",
        "en-us": "a",
        "us": "a",
        "british": "b",
        "british english": "b",
        "en-gb": "b",
        "uk": "b",
        "spanish": "e",
        "es": "e",
        "french": "f",
        "fr": "f",
        "fr-fr": "f",
        "hindi": "h",
        "hi": "h",
        "italian": "i",
        "it": "i",
        "japanese": "j",
        "ja": "j",
        "portuguese": "p",
        "brazilian portuguese": "p",
        "pt-br": "p",
        "mandarin": "z",
        "chinese": "z",
        "zh": "z",
    }
    if raw in aliases:
        return aliases[raw]
    if raw in {"a", "b", "e", "f", "h", "i", "j", "p", "z"}:
        return raw

    voice_prefix = (voice or "").strip().lower()[:1]
    if voice_prefix in {"a", "b", "e", "f", "h", "i", "j", "p", "z"}:
        return voice_prefix

    return "a"


def resolve_kokoro_voice(value: str | None) -> str:
    voice = (value or os.getenv("GA_TTS_VOICE") or "af_heart").strip()
    voice = voice or "af_heart"
    if not KOKORO_VOICE_RE.fullmatch(voice):
        raise ValueError("voice must be a Kokoro voice id such as af_heart.")
    return voice


def resolve_kokoro_speed(value: float | str | None) -> float:
    if value is None:
        value = os.getenv("GA_TTS_SPEED")
    try:
        speed = float(value) if value is not None and str(value).strip() else 1.0
    except (TypeError, ValueError):
        return 1.0
    return min(4.0, max(0.25, speed))


def resolve_device(requested_device_map: str) -> str:
    requested = requested_device_map.strip().lower()
    if requested in {"cpu", "cuda", "mps"}:
        return requested
    if torch.cuda.is_available():
        return "cuda"
    if os.getenv("PYTORCH_ENABLE_MPS_FALLBACK") == "1" and torch.backends.mps.is_available():
        return "mps"
    return "cpu"


def find_local_kokoro_weight(model_dir: str) -> str:
    model_dir = os.path.realpath(model_dir)
    base_real = os.path.realpath(get_model_dir())
    if not model_dir.startswith(base_real + os.sep):
        raise ValueError("Kokoro model directory escapes the permitted model directory.")

    preferred = [
        "kokoro-v1_0.pth",
        "kokoro-v1_1-zh.pth",
        "model.pth",
    ]
    for name in preferred:
        candidate = os.path.realpath(os.path.join(model_dir, name))
        if not candidate.startswith(model_dir + os.sep):
            continue
        if os.path.isfile(candidate):
            return candidate

    for name in sorted(os.listdir(model_dir)):
        if MODEL_PATH_RE.fullmatch(name) and name.lower().endswith(".pth"):
            candidate = os.path.realpath(os.path.join(model_dir, name))
            if candidate.startswith(model_dir + os.sep) and os.path.isfile(candidate):
                return candidate

    raise FileNotFoundError(f"No Kokoro .pth model weight was found in '{model_dir}'.")


def resolve_local_voice_path(local_model_dir: str | None, voice: str) -> str:
    voice = resolve_kokoro_voice(voice)
    if local_model_dir:
        base_real = os.path.realpath(get_model_dir())
        local_model_dir = os.path.realpath(local_model_dir)
        if not local_model_dir.startswith(base_real + os.sep):
            raise ValueError("Kokoro model directory escapes the permitted model directory.")

        voices_dir = os.path.realpath(os.path.join(local_model_dir, "voices"))
        if not voices_dir.startswith(local_model_dir + os.sep):
            raise ValueError("Kokoro voice directory escapes the configured model directory.")

        candidate = os.path.realpath(os.path.join(voices_dir, f"{voice}.pt"))
        if not candidate.startswith(voices_dir + os.sep):
            raise ValueError("Kokoro voice path escapes the configured voice directory.")
        if os.path.isfile(candidate):
            return candidate
    return voice


def retarget_kokoro_pipeline_lang(pipeline: Any, lang_code: str) -> Any:
    from kokoro import KPipeline

    model = getattr(pipeline, "model", None)
    if model is None:
        raise RuntimeError("Loaded Kokoro pipeline does not expose a reusable model for language switching.")
    return KPipeline(lang_code=lang_code, repo_id="hexgrad/Kokoro-82M", model=model)


def load_model(request: LoadModelRequest) -> dict[str, Any]:
    from kokoro import KModel, KPipeline

    # transformers / huggingface_hub pick up the HF token from the process
    # environment. When the .NET layer resolved a token for this request we
    # install it here before from_pretrained triggers any implicit download,
    # so the single configured token is what gets used.
    hf_token = (request.hf_token or "").strip()
    if hf_token:
        os.environ["HF_TOKEN"] = hf_token

    target, local_model_dir = resolve_model_reference(request)
    tokenizer_target = resolve_tokenizer_target(request)
    dtype = resolve_dtype(request.dtype or os.getenv("GA_TTS_DTYPE"))
    requested_device_map = (request.device_map or os.getenv("GA_TTS_DEVICE_MAP") or "auto").strip() or "auto"
    max_new_tokens = parse_positive_int(os.getenv("GA_TTS_MAX_NEW_TOKENS"), 512)
    default_voice = resolve_kokoro_voice(None)
    lang_code = resolve_kokoro_lang_code(os.getenv("GA_TTS_LANG_CODE"), default_voice)
    speed = resolve_kokoro_speed(None)
    device = resolve_device(requested_device_map)
    sample_rate = parse_positive_int(os.getenv("GA_TTS_SAMPLE_RATE"), 24_000)

    started = time.perf_counter()

    if local_model_dir:
        config_path = os.path.realpath(os.path.join(local_model_dir, "config.json"))
        if not config_path.startswith(local_model_dir + os.sep):
            raise ValueError("Kokoro config path escapes the configured model directory.")
        if not os.path.isfile(config_path):
            raise FileNotFoundError(f"Configured Kokoro model path '{target}' is missing config.json.")
        weight_path = find_local_kokoro_weight(local_model_dir)
        model = KModel(repo_id="hexgrad/Kokoro-82M", config=config_path, model=weight_path)
        if dtype != torch.float32:
            model = model.to(device=device, dtype=dtype)
        else:
            model = model.to(device)
        model.eval()
        pipeline = KPipeline(lang_code=lang_code, repo_id="hexgrad/Kokoro-82M", model=model)
    else:
        pipeline = KPipeline(lang_code=lang_code, repo_id=target, device=device)
        model = getattr(pipeline, "model", None)
        if model is not None and dtype != torch.float32:
            model.to(dtype=dtype)

    # Warm the default voice during load so readiness reflects the selected
    # voice pack as well as the model weights.
    pipeline.load_voice(resolve_local_voice_path(local_model_dir, default_voice))

    load_latency_ms = int((time.perf_counter() - started) * 1000)

    with STATE.lock:
        STATE.model = pipeline
        STATE.processor = None
        STATE.model_ref = target
        STATE.tokenizer_ref = tokenizer_target
        STATE.loaded_at_utc = utc_now_iso()
        STATE.dtype = str(dtype)
        STATE.device_map = requested_device_map
        STATE.pipeline_task = "kokoro-generate"
        STATE.device = str(device)
        STATE.max_new_tokens = max_new_tokens
        STATE.sampling_rate = sample_rate
        STATE.voice = default_voice
        STATE.lang_code = lang_code
        STATE.speed = speed
        STATE.local_model_dir = local_model_dir

    if requested_device_map == "auto":
        log_event(
            "tts_device_map_auto_accepted",
            requestedDeviceMap=requested_device_map,
            effectiveDevice=str(device),
            note="Kokoro runtime currently executes on a single selected device.",
        )

    return {
        "modelRef": target,
        "tokenizerRef": tokenizer_target,
        "loadedAtUtc": STATE.loaded_at_utc,
        "loadLatencyMs": load_latency_ms,
        "dtype": str(dtype),
        "deviceMap": requested_device_map,
        "pipelineTask": "kokoro-generate",
        "device": str(device),
        "maxNewTokens": max_new_tokens,
        "sampleRate": sample_rate,
        "voice": default_voice,
        "langCode": lang_code,
        "speed": speed,
    }


def synthesize_wav_bytes(pipeline: Any, script_text: str, voice: str, speed: float, sample_rate: int, local_model_dir: str | None) -> tuple[bytes, float, int]:
    voice_ref = resolve_local_voice_path(local_model_dir, voice)
    chunks: list[np.ndarray] = []
    pause = np.zeros(max(1, int(sample_rate * 0.08)), dtype=np.float32)

    with torch.no_grad():
        for _, _, audio in pipeline(script_text, voice=voice_ref, speed=speed, split_pattern=r"\n+"):
            if audio is None:
                continue
            if isinstance(audio, torch.Tensor):
                waveform = audio.detach().float().cpu().numpy()
            else:
                waveform = np.asarray(audio)

            if waveform.ndim == 2:
                if waveform.shape[0] == 1:
                    waveform = waveform[0]
                elif waveform.shape[1] == 1:
                    waveform = waveform[:, 0]

            if waveform.ndim != 1:
                raise RuntimeError(f"Unexpected waveform rank {waveform.ndim}; expected mono output.")

            if waveform.size > 0:
                if chunks:
                    chunks.append(pause)
                chunks.append(waveform.astype(np.float32))

    if not chunks:
        raise RuntimeError("Kokoro generated no speech outputs.")

    clipped = np.clip(np.concatenate(chunks), -1.0, 1.0)
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
                    "error": "model_load_failed",
                    "message": "Model load failed. Check service logs for details.",
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
    if not MODEL_PATH_RE.fullmatch(model_ref):
        raise HTTPException(status_code=400, detail="invalid model_ref")

    snapshot = STATE.snapshot()
    active_model = snapshot.get("modelRef")
    active_tokenizer = snapshot.get("tokenizerRef")
    if active_model and (active_model == model_ref or str(active_model).endswith(model_ref)):
        raise HTTPException(status_code=409, detail="cannot delete active model")
    if active_tokenizer and (active_tokenizer == model_ref or str(active_tokenizer).endswith(model_ref)):
        raise HTTPException(status_code=409, detail="cannot delete active tokenizer")

    model_dir = os.path.realpath(get_model_dir())
    target = os.path.realpath(os.path.join(model_dir, model_ref))
    if not target.startswith(model_dir + os.sep):
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
        pipeline = STATE.model
        model_ref = STATE.model_ref
        sample_rate = STATE.sampling_rate
        voice = resolve_kokoro_voice(payload.voice or STATE.voice)
        lang_code = resolve_kokoro_lang_code(payload.lang_code, voice)
        speed = resolve_kokoro_speed(payload.speed if payload.speed is not None else STATE.speed)
        local_model_dir = STATE.local_model_dir

    if getattr(pipeline, "lang_code", None) != lang_code:
        try:
            with STATE.lock:
                pipeline = retarget_kokoro_pipeline_lang(pipeline, lang_code)
                STATE.model = pipeline
                STATE.lang_code = lang_code
                STATE.voice = voice
                STATE.speed = speed
        except Exception as exc:
            log_event(
                "tts_language_switch_failed",
                requestId=request_id,
                traceparent=traceparent,
                modelRef=model_ref,
                voice=voice,
                langCode=lang_code,
                errorType=type(exc).__name__,
                error=str(exc),
            )
            return JSONResponse(
                status_code=500,
                content={
                    "requestId": request_id,
                    "error": "language_switch_failed",
                    "message": "Speech synthesis language switch failed. Check service logs for details.",
                },
            )
    else:
        with STATE.lock:
            STATE.voice = voice
            STATE.speed = speed

    log_event(
        "tts_synthesize_start",
        requestId=request_id,
        traceparent=traceparent,
        textLength=len(script_text),
        modelRef=model_ref,
        voice=voice,
        langCode=lang_code,
        speed=speed,
    )

    try:
        with STATE.lock:
            wav_bytes, duration_seconds, sampling_rate = synthesize_wav_bytes(
                pipeline=pipeline,
                script_text=script_text,
                voice=voice,
                speed=speed,
                sample_rate=sample_rate,
                local_model_dir=local_model_dir,
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
            voice=voice,
            langCode=lang_code,
            speed=speed,
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
                "message": "Speech synthesis failed. Check service logs for details.",
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
