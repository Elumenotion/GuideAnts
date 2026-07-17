import base64
import hashlib
import json
import logging
import os
import re
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

from guideants_hf.catalog_download import lookup_hf_file_size
from guideants_hf.transport import download_hf_file
from guideants_hf.operations import find_in_flight_operation

import uvicorn
from fastapi import FastAPI, File, Form, HTTPException, Request, UploadFile
from fastapi.responses import JSONResponse
from pydantic import BaseModel, field_validator


def utc_now_iso() -> str:
    return datetime.now(timezone.utc).isoformat()


def log_event(event: str, **fields: Any) -> None:
    payload = {"event": event, "ts": utc_now_iso()}
    payload.update(fields)
    print(json.dumps(payload, ensure_ascii=True, sort_keys=True), flush=True)


def truncate_text(value: str | None, max_chars: int = 512) -> str:
    text = (value or "").strip()
    if len(text) <= max_chars:
        return text
    return f"{text[:max_chars]}...<truncated:{len(text) - max_chars}>"


def prompt_metadata(prompt: str) -> dict[str, Any]:
    normalized = (prompt or "").strip()
    return {
        "promptChars": len(normalized),
        "promptHash": hashlib.sha256(normalized.encode("utf-8")).hexdigest(),
    }


def request_context(request: Request, request_id: str) -> dict[str, Any]:
    client_host = request.client.host if request.client else None
    return {
        "requestId": request_id,
        "traceparent": request.headers.get("traceparent"),
        "tracestate": request.headers.get("tracestate"),
        "method": request.method,
        "path": request.url.path,
        "clientIp": client_host,
    }


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


def parse_positive_int(value: str | None, default: int) -> int:
    if value is None:
        return default
    try:
        parsed = int(value)
    except ValueError:
        return default
    return parsed if parsed > 0 else default


def parse_positive_float(value: str | None, default: float) -> float:
    if value is None:
        return default
    try:
        parsed = float(value)
    except ValueError:
        return default
    return parsed if parsed > 0 else default


def optional_env_value(name: str) -> str | None:
    raw = os.getenv(name)
    if raw is None:
        return None
    value = raw.strip()
    return value or None


def _normalize_sampling(payload: Any) -> dict[str, Any] | None:
    """
    Require a complete sampling block: steps, cfgScale, samplingMethod.
    There is no legitimate global sampling default — the bundle owns these.
    """
    if not isinstance(payload, dict):
        return None
    try:
        steps = int(payload.get("steps"))
        cfg_scale = float(payload.get("cfgScale"))
    except (TypeError, ValueError):
        return None
    method = str(payload.get("samplingMethod") or "").strip()
    if steps <= 0 or cfg_scale <= 0 or not method:
        return None
    return {
        "steps": steps,
        "cfgScale": cfg_scale,
        "samplingMethod": method,
    }


def parse_size(size: str) -> tuple[int, int]:
    value = (size or "").strip().lower()
    if "x" not in value:
        raise ValueError(f"Invalid size '{size}'. Expected format WIDTHxHEIGHT.")
    parts = value.split("x", 1)
    if len(parts) != 2:
        raise ValueError(f"Invalid size '{size}'. Expected format WIDTHxHEIGHT.")
    width = int(parts[0])
    height = int(parts[1])
    if width <= 0 or height <= 0:
        raise ValueError("Image size must be positive.")
    return width, height


def normalize_output_format(raw: str | None, fallback: str) -> str:
    candidate = (raw or fallback or "png").strip().lower()
    if candidate == "jpg":
        candidate = "jpeg"
    if candidate not in VALID_OUTPUT_FORMATS:
        raise ValueError(
            f"Unsupported output format '{candidate}'. Supported formats: {', '.join(sorted(VALID_OUTPUT_FORMATS))}."
        )
    return candidate


def decode_json_bytes(payload: bytes, context: str) -> dict[str, Any]:
    text = payload.decode("utf-8", errors="replace")
    try:
        parsed = json.loads(text)
    except json.JSONDecodeError as exc:
        raise RuntimeError(f"{context} returned invalid JSON: {truncate_text(text, 2048)}") from exc
    if not isinstance(parsed, dict):
        raise RuntimeError(f"{context} returned non-object JSON payload.")
    return parsed


def build_multipart_form_data(fields: dict[str, str], files: list[tuple[str, str, bytes, str]]) -> tuple[bytes, str]:
    boundary = f"----guideants-sd-{uuid.uuid4().hex}"
    chunks: list[bytes] = []

    for name, value in fields.items():
        chunks.append(f"--{boundary}\r\n".encode("ascii"))
        chunks.append(f'Content-Disposition: form-data; name="{name}"\r\n\r\n'.encode("ascii"))
        chunks.append(value.encode("utf-8"))
        chunks.append(b"\r\n")

    for field_name, file_name, file_bytes, content_type in files:
        chunks.append(f"--{boundary}\r\n".encode("ascii"))
        chunks.append(
            f'Content-Disposition: form-data; name="{field_name}"; filename="{file_name}"\r\n'.encode("ascii")
        )
        chunks.append(f"Content-Type: {content_type}\r\n\r\n".encode("ascii"))
        chunks.append(file_bytes)
        chunks.append(b"\r\n")

    chunks.append(f"--{boundary}--\r\n".encode("ascii"))
    return b"".join(chunks), f"multipart/form-data; boundary={boundary}"


@dataclass(frozen=True)
class SdRuntimeConfig:
    server_path: str
    model_dir: str
    diffusion_model_path: str
    vae_path: str
    llm_path: str
    engine_host: str
    engine_port: int
    engine_ready_timeout_seconds: int
    timeout_seconds: int
    engine_request_timeout_seconds: int
    warmup_request_timeout_seconds: int
    poll_interval_seconds: float
    steps: int
    cfg_scale: float
    strength: float
    sampling_method: str
    offload_to_cpu: bool
    vae_on_cpu: bool
    backend: str | None
    params_backend: str | None
    split_mode: str | None
    max_vram: str | None
    auto_fit: bool
    diffusion_fa: bool
    vulkan_visible_devices: str | None
    default_output_format: str
    startup_warmup_fail_open: bool

    @property
    def engine_base_url(self) -> str:
        return f"http://{self.engine_host}:{self.engine_port}"


class Txt2ImgRequest(BaseModel):
    prompt: str
    size: str = "1024x1024"
    n: int = 1
    outputFormat: str = "png"


class WarmupRequest(BaseModel):
    prompt: str | None = None
    size: str | None = None
    outputFormat: str | None = None
    steps: int | None = None


class DownloadBundleRequest(BaseModel):
    """
    Bundle download request. Each role (diffusion / VAE / text encoder) is
    downloaded as **exactly one file** from its Hugging Face repo. All three
    (repo + file) pairs and the bundle id are required; there are no defaults
    and no implicit fallbacks. Empty values, whitespace-only values, and
    filenames containing glob metacharacters (``*`` or ``?``) are rejected so
    the caller cannot accidentally pull an entire multi-quantization repo.

    ``hf_token`` is the single server-resolved Hugging Face token stamped in
    by the .NET web layer from the top-level ``HuggingFace:Token``
    application setting. This service does not read ``HF_TOKEN`` from env or
    from anywhere else; whatever the web API passes in is the one token
    that will be used for every HF call during this operation.
    """
    bundle_id: str
    diffusion_repo: str
    diffusion_file: str
    vae_repo: str
    vae_file: str
    text_encoder_repo: str
    text_encoder_file: str
    sampling_steps: int
    sampling_cfg_scale: float
    sampling_method: str
    revision: str | None = None
    hf_token: str | None = None
    force_redownload: bool = False

    @field_validator(
        "bundle_id",
        "diffusion_repo",
        "diffusion_file",
        "vae_repo",
        "vae_file",
        "text_encoder_repo",
        "text_encoder_file",
        "sampling_method",
    )
    @classmethod
    def _require_non_empty(cls, value: str) -> str:
        trimmed = (value or "").strip()
        if not trimmed:
            raise ValueError("must be a non-empty string")
        return trimmed

    @field_validator("bundle_id")
    @classmethod
    def _validate_bundle_id(cls, value: str) -> str:
        return validate_bundle_id(value)

    @field_validator("diffusion_file", "vae_file", "text_encoder_file")
    @classmethod
    def _validate_bundle_filename(cls, value: str) -> str:
        return validate_bundle_filename(value)

    @field_validator("sampling_steps")
    @classmethod
    def _validate_sampling_steps(cls, value: int) -> int:
        if value <= 0:
            raise ValueError("sampling_steps must be > 0")
        return value

    @field_validator("sampling_cfg_scale")
    @classmethod
    def _validate_sampling_cfg_scale(cls, value: float) -> float:
        if value <= 0:
            raise ValueError("sampling_cfg_scale must be > 0")
        return value


class UpsertBundleDefinitionRequest(BaseModel):
    revision: str | None = None
    roles: dict[str, Any]
    sampling: dict[str, Any]


class SdRuntimeState:
    def __init__(self) -> None:
        # Control-plane model dir: resolved unconditionally at startup from
        # GA_SD_MODEL_DIR (default /models-local/sd). Always present even when no
        # bundle is active, because bundle-download/list admin routes must
        # work in order to create a bundle in the first place.
        self.model_dir: str | None = None
        # Runtime config is only populated when an active bundle exists and
        # all three role files are present. Inference endpoints gate on this.
        self.config: SdRuntimeConfig | None = None
        # Reason why the runtime config could not be resolved at startup, for
        # operator-visible diagnostics (surfaced on /health when config is
        # None). Not used as control flow.
        self.config_error: str | None = None
        self.loaded_at_utc: str | None = None
        self.startup_warmup_enabled: bool = False
        self.startup_warmup_completed_at_utc: str | None = None
        self.startup_warmup_last_attempt_at_utc: str | None = None
        self.startup_warmup_last_error: str | None = None
        self.startup_warmup_running: bool = False
        self.engine_process: Any = None
        self.engine_started_at_utc: str | None = None
        # id of the bundle whose files are currently loaded into the running
        # sd-server. None when the engine is not running. This is intentionally
        # distinct from `active_bundle.json` on disk (the "next bundle to load
        # from") so the UI can reason about "active marker vs. loaded engine"
        # during hot-swap failures.
        self.loaded_bundle_id: str | None = None


STATE = SdRuntimeState()
APP = FastAPI(title="GuideAnts Stable Diffusion Service", version="1.1.0")
VALID_OUTPUT_FORMATS = {"png", "jpeg", "webp"}
BUNDLE_ID_RE = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")

# Canonical bundle ids may still have on-disk folders under legacy names until
# operators rename or re-download into the canonical directory.
LEGACY_BUNDLE_DIR_NAMES: dict[str, str] = {
    "flux2-klein-4b": "flux2-klein-4b-q4ks",
    "flux2-klein-9b": "flux2-klein-9b-q5",
    "FLUX.2-dev": "FLUX.2-dev-GGUF-Q5_K_M",
}


def canonical_bundle_id(bundle_id: str) -> str:
    for canonical, legacy in LEGACY_BUNDLE_DIR_NAMES.items():
        if bundle_id == legacy:
            return canonical
    return bundle_id
WARMUP_LOCK = threading.Lock()
BUNDLE_OPS_LOCK = threading.Lock()
BUNDLE_OPERATIONS: dict[str, dict[str, Any]] = {}
# Serializes engine lifecycle operations (start / stop / hot-swap). Held only
# by lifecycle callers; inference endpoints do not acquire this lock so a
# long-running generation does not block a later unload request.
ENGINE_LOCK = threading.Lock()


def validate_bundle_filename(value: str) -> str:
    filename = (value or "").strip()
    if not filename:
        raise ValueError("must be a non-empty string")
    if "*" in filename or "?" in filename:
        raise ValueError(
            "must be a single filename (no '*' or '?' glob metacharacters)"
        )
    if (
        filename in {".", ".."}
        or os.path.isabs(filename)
        or os.path.basename(filename) != filename
        or "/" in filename
        or "\\" in filename
    ):
        raise ValueError("must be a single filename with no path separators")
    return filename


def validate_bundle_id(value: str) -> str:
    candidate = (value or "").strip()
    if not BUNDLE_ID_RE.fullmatch(candidate):
        raise ValueError("bundle_id must match ^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")
    return candidate


def list_role_asset_files(role_path: str) -> list[str]:
    if not os.path.isdir(role_path):
        return []
    assets: list[str] = []
    try:
        for name in os.listdir(role_path):
            if name.startswith("."):
                continue
            if name.endswith(".tmp") or name.endswith(".guideants-meta.json"):
                continue
            file_path = os.path.join(role_path, name)
            if os.path.isfile(file_path):
                assets.append(name)
    except OSError:
        return []
    return sorted(assets)


def role_asset_file_count(bundle_path: str) -> int:
    total = 0
    for subdir in ("diffusion", "vae", "text-encoder"):
        total += len(list_role_asset_files(os.path.join(bundle_path, subdir)))
    return total


def resolve_bundle_dir(model_dir: str, bundle_id: str) -> str:
    safe_bundle_id = validate_bundle_id(bundle_id)
    root_real = os.path.realpath(bundle_root_dir(model_dir))
    root_prefix = root_real if root_real.endswith(os.sep) else root_real + os.sep

    candidates: list[str] = [os.path.join(root_real, safe_bundle_id)]
    legacy_name = LEGACY_BUNDLE_DIR_NAMES.get(safe_bundle_id)
    if legacy_name:
        candidates.append(os.path.join(root_real, legacy_name))

    best_path = os.path.join(root_real, safe_bundle_id)
    best_count = -1
    for candidate in candidates:
        candidate_real = os.path.realpath(candidate)
        if not candidate_real.startswith(root_prefix) or not os.path.isdir(candidate_real):
            continue
        count = role_asset_file_count(candidate_real)
        if count > best_count:
            best_count = count
            best_path = candidate_real

    if not best_path.startswith(root_prefix):
        raise ValueError("resolved bundle path escapes the permitted bundle directory")
    return best_path


def _bundle_dir_is_populated(bundle_path: str) -> bool:
    return role_asset_file_count(bundle_path) > 0


def resolve_runtime_config(*, bundle_id: str | None = None) -> SdRuntimeConfig:
    model_dir = os.getenv("GA_SD_MODEL_DIR", "/models-local/sd")

    server_path = (os.getenv("GA_SD_SERVER_PATH") or "").strip()
    if not server_path:
        server_path = shutil.which("sd-server") or "/usr/local/bin/sd-server"
    elif not os.path.isabs(server_path):
        server_path = shutil.which(server_path) or server_path

    selected_bundle = (bundle_id or "").strip()
    if not selected_bundle:
        raise RuntimeError(
            "bundle_id is required. Selection is owned by ServiceModes; "
            "warmup orchestration must call POST /admin/load with bundle_id."
        )

    bundle_paths = expected_bundle_paths(model_dir, selected_bundle)
    definition = read_bundle_definition(model_dir, selected_bundle)
    if definition is None:
        raise RuntimeError(
            f"Bundle '{selected_bundle}' has no readable bundle-definition.json."
        )

    def _required_role_file(path: str, role: str) -> str:
        spec = role_spec_from_definition(definition, role)
        if spec is None:
            raise RuntimeError(
                f"Bundle '{selected_bundle}' is missing role '{role}' in bundle-definition.json."
            )
        _repo, filename = spec
        if not bundle_role_ready(path, definition, role):
            raise RuntimeError(
                f"Bundle '{selected_bundle}' role '{role}' is incomplete at '{path}'. "
                f"Expected file '{filename}' is missing, truncated, or still downloading."
            )
        return resolve_role_file_path(path, filename)

    diffusion_model_path = _required_role_file(bundle_paths["diffusion"], "diffusion")
    vae_path = _required_role_file(bundle_paths["vae"], "vae")
    llm_path = _required_role_file(bundle_paths["textEncoder"], "textEncoder")

    engine_host = (os.getenv("GA_SD_ENGINE_HOST") or "127.0.0.1").strip() or "127.0.0.1"
    engine_port = parse_positive_int(os.getenv("GA_SD_ENGINE_PORT"), 18083)
    engine_ready_timeout_seconds = parse_positive_int(os.getenv("GA_SD_ENGINE_READY_TIMEOUT_SECONDS"), 1800)
    timeout_seconds = parse_positive_int(os.getenv("GA_SD_TIMEOUT_SECONDS"), 600)
    engine_request_timeout_seconds = parse_positive_int(os.getenv("GA_SD_ENGINE_REQUEST_TIMEOUT_SECONDS"), 120)
    warmup_request_timeout_seconds = parse_positive_int(
        os.getenv("GA_SD_WARMUP_REQUEST_TIMEOUT_SECONDS"),
        max(engine_request_timeout_seconds, 120),
    )
    poll_interval_seconds = parse_positive_float(os.getenv("GA_SD_POLL_INTERVAL_SECONDS"), 0.25)

    # Sampling comes only from the active bundle definition. There is no
    # GA_SD_STEPS / GA_SD_CFG_SCALE / GA_SD_SAMPLING_METHOD env path.
    bundle_sampling = require_bundle_sampling(model_dir, selected_bundle)
    steps = int(bundle_sampling["steps"])
    cfg_scale = float(bundle_sampling["cfgScale"])
    sampling_method = str(bundle_sampling["samplingMethod"])
    strength = parse_positive_float(os.getenv("GA_SD_STRENGTH"), 0.75)
    offload_to_cpu = env_flag("GA_SD_OFFLOAD_TO_CPU", False)
    vae_on_cpu = env_flag("GA_SD_VAE_ON_CPU", False)
    backend = optional_env_value("GA_SD_BACKEND")
    params_backend = optional_env_value("GA_SD_PARAMS_BACKEND")
    split_mode = optional_env_value("GA_SD_SPLIT_MODE")
    max_vram = optional_env_value("GA_SD_MAX_VRAM")
    auto_fit = env_flag("GA_SD_AUTO_FIT", False)
    diffusion_fa = env_flag("GA_SD_DIFFUSION_FA", True)
    vulkan_visible_devices = optional_env_value("GA_SD_VK_VISIBLE_DEVICES")
    default_output_format = normalize_output_format(os.getenv("GA_SD_DEFAULT_OUTPUT_FORMAT"), "png")
    startup_warmup_fail_open = env_flag("GA_SD_WARMUP_FAIL_OPEN_ON_STARTUP", True)

    engine_request_timeout_seconds = max(1, min(engine_request_timeout_seconds, timeout_seconds))
    warmup_request_timeout_seconds = max(1, min(warmup_request_timeout_seconds, timeout_seconds))

    # The bundle paths are already verified as files by _only_file above; the
    # only remaining unknown is the sd-server binary.
    if not os.path.exists(server_path):
        raise RuntimeError(
            f"Stable Diffusion sd-server binary not found at '{server_path}'. "
            f"Set GA_SD_SERVER_PATH or install sd-server on PATH."
        )

    return SdRuntimeConfig(
        server_path=server_path,
        model_dir=model_dir,
        diffusion_model_path=diffusion_model_path,
        vae_path=vae_path,
        llm_path=llm_path,
        engine_host=engine_host,
        engine_port=engine_port,
        engine_ready_timeout_seconds=engine_ready_timeout_seconds,
        timeout_seconds=timeout_seconds,
        engine_request_timeout_seconds=engine_request_timeout_seconds,
        warmup_request_timeout_seconds=warmup_request_timeout_seconds,
        poll_interval_seconds=poll_interval_seconds,
        steps=steps,
        cfg_scale=cfg_scale,
        strength=strength,
        sampling_method=sampling_method,
        offload_to_cpu=offload_to_cpu,
        vae_on_cpu=vae_on_cpu,
        backend=backend,
        params_backend=params_backend,
        split_mode=split_mode,
        max_vram=max_vram,
        auto_fit=auto_fit,
        diffusion_fa=diffusion_fa,
        vulkan_visible_devices=vulkan_visible_devices,
        default_output_format=default_output_format,
        startup_warmup_fail_open=startup_warmup_fail_open,
    )


def bundle_root_dir(model_dir: str) -> str:
    root = os.path.join(model_dir, "bundles")
    os.makedirs(root, exist_ok=True)
    return root


def bundle_operation_staging_dir(model_dir: str, operation_id: str, role: str) -> str:
    return os.path.join(model_dir, ".staging", operation_id, role)


def active_bundle_file(model_dir: str) -> str:
    return os.path.join(model_dir, "active_bundle.json")


def read_active_bundle(model_dir: str) -> str | None:
    marker = active_bundle_file(model_dir)
    if not os.path.exists(marker):
        return None
    try:
        with open(marker, "r", encoding="utf-8") as handle:
            payload = json.load(handle)
        bundle_id = str(payload.get("bundleId") or "").strip()
        return bundle_id or None
    except Exception:
        return None


def write_active_bundle_marker(model_dir: str, bundle_id: str) -> None:
    marker = active_bundle_file(model_dir)
    with open(marker, "w", encoding="utf-8") as handle:
        json.dump({"bundleId": bundle_id, "updatedAtUtc": utc_now_iso()}, handle)


def expected_bundle_paths(model_dir: str, bundle_id: str) -> dict[str, str]:
    base = resolve_bundle_dir(model_dir, bundle_id)
    return {
        "diffusion": os.path.join(base, "diffusion"),
        "vae": os.path.join(base, "vae"),
        "textEncoder": os.path.join(base, "text-encoder"),
    }


def canonical_bundle_dir(model_dir: str, bundle_id: str) -> str:
    safe_id = validate_bundle_id(canonical_bundle_id(bundle_id))
    return os.path.join(bundle_root_dir(model_dir), safe_id)


def canonical_bundle_definition_path(model_dir: str, bundle_id: str) -> str:
    return os.path.join(canonical_bundle_dir(model_dir, bundle_id), "bundle-definition.json")


def bundle_definition_file(model_dir: str, bundle_id: str) -> str:
    canonical_path = canonical_bundle_definition_path(model_dir, bundle_id)
    if os.path.isfile(canonical_path):
        return canonical_path
    return os.path.join(resolve_bundle_dir(model_dir, bundle_id), "bundle-definition.json")


def write_bundle_definition_payload(model_dir: str, bundle_id: str, payload: dict[str, Any]) -> None:
    bundle_path = canonical_bundle_dir(model_dir, bundle_id)
    os.makedirs(bundle_path, exist_ok=True)
    target = os.path.join(bundle_path, "bundle-definition.json")
    temp = f"{target}.{uuid.uuid4().hex}.tmp"
    with open(temp, "w", encoding="utf-8") as handle:
        json.dump(payload, handle, ensure_ascii=True, sort_keys=True)
    os.replace(temp, target)


def bundle_definition_payload(request: DownloadBundleRequest, bundle_id: str | None = None) -> dict[str, Any]:
    safe_bundle_id = validate_bundle_id(bundle_id or request.bundle_id)
    return {
        "bundleId": safe_bundle_id,
        "revision": request.revision,
        "updatedAtUtc": utc_now_iso(),
        "sampling": {
            "steps": request.sampling_steps,
            "cfgScale": request.sampling_cfg_scale,
            "samplingMethod": request.sampling_method,
        },
        "roles": {
            "diffusion": {"repo": request.diffusion_repo, "file": request.diffusion_file},
            "vae": {"repo": request.vae_repo, "file": request.vae_file},
            "textEncoder": {
                "repo": request.text_encoder_repo,
                "file": request.text_encoder_file,
            },
        },
    }


def write_bundle_definition(model_dir: str, request: DownloadBundleRequest, bundle_id: str | None = None) -> None:
    safe_bundle_id = validate_bundle_id(bundle_id or request.bundle_id)
    payload = bundle_definition_payload(request, safe_bundle_id)
    write_bundle_definition_payload(model_dir, safe_bundle_id, payload)


def _normalize_bundle_definition(payload: Any) -> dict[str, Any] | None:
    if not isinstance(payload, dict):
        return None

    roles = payload.get("roles")
    if not isinstance(roles, dict):
        return None

    normalized_roles: dict[str, dict[str, str]] = {}
    for role in ("diffusion", "vae", "textEncoder"):
        role_payload = roles.get(role)
        if not isinstance(role_payload, dict):
            return None
        repo = str(role_payload.get("repo") or "").strip()
        filename = str(role_payload.get("file") or "").strip()
        if not repo or not filename:
            return None
        normalized_roles[role] = {"repo": repo, "file": filename}

    revision_raw = payload.get("revision")
    revision = None
    if revision_raw is not None:
        revision_text = str(revision_raw).strip()
        if revision_text:
            revision = revision_text

    updated_raw = payload.get("updatedAtUtc")
    updated_at_utc = None
    if updated_raw is not None:
        updated_text = str(updated_raw).strip()
        if updated_text:
            updated_at_utc = updated_text

    sampling = None
    if "sampling" in payload:
        # Invalid sampling is treated as absent so listing still works; load fails
        # via require_bundle_sampling with an explicit error.
        sampling = _normalize_sampling(payload.get("sampling"))

    result: dict[str, Any] = {
        "revision": revision,
        "updatedAtUtc": updated_at_utc,
        "roles": normalized_roles,
    }
    if sampling is not None:
        result["sampling"] = sampling
    return result


def require_bundle_sampling(model_dir: str, bundle_id: str) -> dict[str, Any]:
    definition = read_bundle_definition(model_dir, bundle_id)
    if definition is None:
        raise RuntimeError(
            f"Bundle '{bundle_id}' has no readable bundle-definition.json. "
            f"Sampling parameters (steps/cfgScale/samplingMethod) are required "
            f"on the bundle; there is no global default."
        )
    sampling = definition.get("sampling")
    normalized = _normalize_sampling(sampling)
    if normalized is None:
        raise RuntimeError(
            f"Bundle '{bundle_id}' is missing a valid sampling block "
            f"(steps, cfgScale, samplingMethod). Sampling is per-bundle; "
            f"there is no legitimate global default."
        )
    return normalized


def _single_file_name(path: str) -> str | None:
    if not os.path.isdir(path):
        return None
    try:
        files = [
            name for name in sorted(os.listdir(path))
            if os.path.isfile(os.path.join(path, name))
        ]
    except Exception:
        return None
    if len(files) != 1:
        return None
    return files[0]


def read_bundle_definition(model_dir: str, bundle_id: str) -> dict[str, Any] | None:
    path = bundle_definition_file(model_dir, bundle_id)
    if not os.path.isfile(path):
        return None
    try:
        with open(path, "r", encoding="utf-8") as handle:
            payload = json.load(handle)
            normalized = _normalize_bundle_definition(payload)
    except Exception:
        return None

    if normalized is None:
        return None

    return {
        "revision": normalized.get("revision"),
        "updatedAtUtc": normalized.get("updatedAtUtc"),
        "roles": normalized.get("roles"),
        "sampling": normalized.get("sampling"),
    }


def upsert_bundle_definition(model_dir: str, bundle_id: str, request: UpsertBundleDefinitionRequest) -> dict[str, Any]:
    safe_bundle_id = validate_bundle_id(bundle_id)
    normalized = _normalize_bundle_definition(
        {
            "bundleId": safe_bundle_id,
            "revision": request.revision,
            "roles": request.roles,
            "sampling": request.sampling,
        }
    )
    if normalized is None:
        raise ValueError("bundle definition payload is invalid")
    sampling = _normalize_sampling(normalized.get("sampling"))
    if sampling is None:
        raise ValueError("bundle definition sampling block is invalid")

    payload = {
        "bundleId": safe_bundle_id,
        "revision": normalized.get("revision"),
        "updatedAtUtc": utc_now_iso(),
        "roles": normalized["roles"],
        "sampling": sampling,
    }
    write_bundle_definition_payload(model_dir, safe_bundle_id, payload)
    return {
        "revision": payload["revision"],
        "updatedAtUtc": payload["updatedAtUtc"],
        "roles": payload["roles"],
        "sampling": payload["sampling"],
    }


def role_directory_ready(path: str) -> bool:
    if not os.path.isdir(path):
        return False
    try:
        return any(
            os.path.isfile(os.path.join(path, name))
            for name in os.listdir(path)
        )
    except Exception:
        return False


def role_file_metadata_path(expected_file: str) -> str:
    return f"{expected_file}.guideants-meta.json"


def read_role_file_metadata(expected_file: str) -> dict[str, Any] | None:
    meta_path = role_file_metadata_path(expected_file)
    if not os.path.isfile(meta_path):
        return None
    try:
        with open(meta_path, "r", encoding="utf-8") as handle:
            payload = json.load(handle)
    except Exception:
        return None
    return payload if isinstance(payload, dict) else None


def write_role_file_metadata(expected_file: str, *, expected_size: int, repo: str, filename: str) -> None:
    meta_path = role_file_metadata_path(expected_file)
    payload = {
        "expectedSize": expected_size,
        "repo": repo,
        "file": filename,
        "updatedAtUtc": utc_now_iso(),
    }
    temp = f"{meta_path}.{uuid.uuid4().hex}.tmp"
    with open(temp, "w", encoding="utf-8") as handle:
        json.dump(payload, handle, ensure_ascii=True, sort_keys=True)
    os.replace(temp, meta_path)


def role_file_size_matches_metadata(expected_file: str) -> bool:
    metadata = read_role_file_metadata(expected_file)
    if metadata is None:
        return False
    expected_size = metadata.get("expectedSize")
    if not isinstance(expected_size, int) or expected_size <= 0:
        return False
    if not os.path.isfile(expected_file):
        return False
    return os.path.getsize(expected_file) == expected_size


def role_has_incomplete_download_artifact(target_path: str, filename: str) -> bool:
    """
    Detect legacy partial downloads written directly into the role folder before
    staged downloads. New downloads only create ``.tmp`` files under
    ``{model_dir}/.staging/{operationId}/``.
    """
    try:
        expected_file = resolve_role_file_path(target_path, filename)
    except ValueError:
        return True
    temp_path = expected_file + ".tmp"
    return os.path.isfile(temp_path)


def remove_legacy_role_download_artifacts(target_path: str, filename: str) -> None:
    try:
        expected_file = resolve_role_file_path(target_path, filename)
    except ValueError:
        return
    temp_path = expected_file + ".tmp"
    if os.path.isfile(temp_path):
        os.remove(temp_path)


def cleanup_bundle_operation_staging(model_dir: str, operation_id: str) -> None:
    staging_root = os.path.join(model_dir, ".staging", operation_id)
    if os.path.isdir(staging_root):
        shutil.rmtree(staging_root, ignore_errors=True)


def download_bundle_role_via_staging(
    *,
    model_dir: str,
    operation_id: str,
    role: str,
    repo: str,
    filename: str,
    target_path: str,
    hf_token: str | None,
    revision: str | None,
) -> None:
    safe_filename = validate_bundle_filename(filename)
    staging_dir = bundle_operation_staging_dir(model_dir, operation_id, role)
    os.makedirs(staging_dir, exist_ok=True)
    staged_file = os.path.join(staging_dir, safe_filename)
    resolved_revision = (revision or "main").strip() or "main"

    download_hf_file(
        repo,
        safe_filename,
        staged_file,
        hf_token,
        revision=resolved_revision,
    )
    if not os.path.isfile(staged_file):
        raise RuntimeError(
            f"Expected staged file '{safe_filename}' was not produced by "
            f"download of '{repo}' into '{staging_dir}'."
        )

    os.makedirs(target_path, exist_ok=True)
    remove_legacy_role_download_artifacts(target_path, safe_filename)
    expected_file = resolve_role_file_path(target_path, safe_filename)
    if os.path.isfile(expected_file):
        os.remove(expected_file)
    os.replace(staged_file, expected_file)
    verify_downloaded_role_file(
        repo=repo,
        filename=safe_filename,
        expected_file=expected_file,
        hf_token=hf_token,
        revision=resolved_revision,
    )


def role_spec_from_definition(definition: dict[str, Any] | None, role: str) -> tuple[str, str] | None:
    if definition is None:
        return None
    roles = definition.get("roles")
    if not isinstance(roles, dict):
        return None
    role_payload = roles.get(role)
    if not isinstance(role_payload, dict):
        return None
    repo = str(role_payload.get("repo") or "").strip()
    filename = str(role_payload.get("file") or "").strip()
    if not repo or not filename:
        return None
    return repo, filename


def _hf_token_from_env() -> str | None:
    for key in ("HF_TOKEN", "HUGGING_FACE_HUB_TOKEN", "GA_HF_TOKEN"):
        value = (os.getenv(key) or "").strip()
        if value:
            return value
    return None


def bundle_role_ready(
    role_path: str,
    definition: dict[str, Any] | None,
    role: str,
) -> bool:
    spec = role_spec_from_definition(definition, role)
    if spec is None:
        return False
    _repo, filename = spec
    if role_has_incomplete_download_artifact(role_path, filename):
        return False
    try:
        expected_file = resolve_role_file_path(role_path, filename)
    except ValueError:
        return False
    if not os.path.isfile(expected_file):
        return False
    if read_role_file_metadata(expected_file) is not None:
        return role_file_size_matches_metadata(expected_file)
    try:
        return os.path.getsize(expected_file) > 0
    except OSError:
        return False


def verify_downloaded_role_file(
    *,
    repo: str,
    filename: str,
    expected_file: str,
    hf_token: str | None,
    revision: str | None,
) -> None:
    actual_size = os.path.getsize(expected_file)
    expected_size = lookup_hf_file_size(repo, filename, hf_token, revision)
    if expected_size is None:
        raise RuntimeError(
            f"Could not resolve Hugging Face size metadata for '{filename}' in '{repo}'."
        )
    if actual_size != expected_size:
        raise RuntimeError(
            f"Downloaded '{filename}' size {actual_size} does not match Hugging Face "
            f"expected size {expected_size}. The file may be truncated or corrupt; "
            f"retry with force_redownload."
        )
    write_role_file_metadata(
        expected_file,
        expected_size=expected_size,
        repo=repo,
        filename=filename,
    )



def list_bundles(model_dir: str) -> list[dict[str, Any]]:
    root = bundle_root_dir(model_dir)
    bundles: list[dict[str, Any]] = []
    try:
        folder_ids = os.listdir(root)
    except OSError as exc:
        log_event(
            "sd_bundle_root_list_failed",
            errorType=type(exc).__name__,
            error=truncate_text(str(exc), 2048),
        )
        return bundles

    canonical_ids: set[str] = set()
    for folder_id in folder_ids:
        folder_path = os.path.join(root, folder_id)
        if not os.path.isdir(folder_path):
            continue
        try:
            canonical_ids.add(canonical_bundle_id(validate_bundle_id(folder_id)))
        except ValueError:
            continue

    for bundle_id in sorted(canonical_ids):
        bundle_path = os.path.join(root, bundle_id)
        if not os.path.isdir(bundle_path):
            # resolve_bundle_dir may point at a legacy folder name.
            bundle_path = resolve_bundle_dir(model_dir, bundle_id)
        try:
            roles = expected_bundle_paths(model_dir, bundle_id)
            definition = read_bundle_definition(model_dir, bundle_id)
            role_state = {
                role: {"path": path, "ready": bundle_role_ready(path, definition, role)}
                for role, path in roles.items()
            }
            bundle: dict[str, Any] = {
                "bundleId": bundle_id,
                "roles": role_state,
                "complete": all(role["ready"] for role in role_state.values()),
            }
            if definition is not None:
                bundle["definition"] = definition
            bundles.append(bundle)
        except Exception as exc:
            # HF snapshot_download / rmtree can race with directory scans; a
            # failed row must not take down GET /admin/bundles (settings UI).
            log_event(
                "sd_bundle_list_item_failed",
                bundleId=bundle_id,
                errorType=type(exc).__name__,
                error=truncate_text(str(exc), 2048),
            )
            try:
                roles = expected_bundle_paths(model_dir, bundle_id)
            except Exception:
                roles = {
                    "diffusion": os.path.join(bundle_path, "diffusion"),
                    "vae": os.path.join(bundle_path, "vae"),
                    "textEncoder": os.path.join(bundle_path, "text-encoder"),
                }
            bundles.append(
                {
                    "bundleId": bundle_id,
                    "roles": {
                        role: {"path": path, "ready": False}
                        for role, path in roles.items()
                    },
                    "complete": False,
                }
            )
    return bundles


def _normalize_bundle_revision(revision: str | None) -> str | None:
    text = (revision or "").strip()
    return text or None


def _previous_role_spec(
    previous_definition: dict[str, Any] | None, role: str
) -> tuple[str, str] | None:
    if previous_definition is None:
        return None
    roles = previous_definition.get("roles")
    if not isinstance(roles, dict):
        return None
    role_payload = roles.get(role)
    if not isinstance(role_payload, dict):
        return None
    repo = str(role_payload.get("repo") or "").strip()
    filename = str(role_payload.get("file") or "").strip()
    if not repo or not filename:
        return None
    return repo, filename


def _bundle_revision_unchanged(
    previous_definition: dict[str, Any] | None, request: DownloadBundleRequest
) -> bool:
    if previous_definition is None:
        return False
    previous = _normalize_bundle_revision(previous_definition.get("revision"))
    incoming = _normalize_bundle_revision(request.revision)
    return previous == incoming


def bundle_role_download_needed(
    previous_definition: dict[str, Any] | None,
    request: DownloadBundleRequest,
    role: str,
    repo: str,
    filename: str,
) -> bool:
    if previous_definition is None:
        return True
    if not _bundle_revision_unchanged(previous_definition, request):
        return True
    previous = _previous_role_spec(previous_definition, role)
    if previous is None:
        return True
    return previous != (repo, filename)


def resolve_role_file_path(target_path: str, filename: str) -> str:
    safe_filename = validate_bundle_filename(filename)
    role_dir = os.path.realpath(target_path)
    candidate = os.path.realpath(os.path.join(role_dir, safe_filename))
    role_prefix = role_dir if role_dir.endswith(os.sep) else role_dir + os.sep
    if not candidate.startswith(role_prefix):
        raise ValueError("resolved role file path escapes the permitted role directory")
    return candidate


def role_expected_file_ready(target_path: str, filename: str) -> bool:
    try:
        expected_file = resolve_role_file_path(target_path, filename)
    except ValueError:
        return False
    return os.path.isfile(expected_file)


def remove_role_expected_file(target_path: str, filename: str) -> None:
    try:
        expected_file = resolve_role_file_path(target_path, filename)
    except ValueError:
        return
    if os.path.isfile(expected_file):
        os.remove(expected_file)


def should_skip_hf_download(
    target_path: str,
    filename: str,
    force_redownload: bool,
    *,
    repo: str | None = None,
    hf_token: str | None = None,
    revision: str | None = None,
) -> bool:
    if force_redownload:
        return False
    if role_has_incomplete_download_artifact(target_path, filename):
        return False
    if not role_expected_file_ready(target_path, filename):
        return False
    expected_file = resolve_role_file_path(target_path, filename)
    if role_file_size_matches_metadata(expected_file):
        return True
    if read_role_file_metadata(expected_file) is not None:
        return False
    if repo:
        expected_size = lookup_hf_file_size(repo, filename, hf_token, revision)
        if expected_size is not None:
            if os.path.getsize(expected_file) == expected_size:
                ensure_role_file_metadata(
                    repo=repo,
                    filename=filename,
                    expected_file=expected_file,
                    hf_token=hf_token,
                    revision=revision,
                )
                return True
            return False
    return True


def ensure_role_file_metadata(
    *,
    repo: str,
    filename: str,
    expected_file: str,
    hf_token: str | None,
    revision: str | None,
) -> None:
    if role_file_size_matches_metadata(expected_file):
        return
    expected_size = lookup_hf_file_size(repo, filename, hf_token, revision)
    if expected_size is None:
        return
    if os.path.getsize(expected_file) != expected_size:
        return
    write_role_file_metadata(
        expected_file,
        expected_size=expected_size,
        repo=repo,
        filename=filename,
    )


def request_force_redownload(request: DownloadBundleRequest) -> bool:
    return bool(getattr(request, "force_redownload", False))


def clear_stale_role_files(target_path: str, filename: str) -> None:
    """Remove files left by a prior recipe (e.g. a renamed gguf)."""
    remove_legacy_role_download_artifacts(target_path, filename)
    if not os.path.isdir(target_path):
        return
    safe_filename = validate_bundle_filename(filename)
    try:
        for name in os.listdir(target_path):
            file_path = os.path.join(target_path, name)
            if os.path.isfile(file_path) and name != safe_filename:
                os.remove(file_path)
    except OSError:
        shutil.rmtree(target_path)


def resolve_initial_bundle_role_states(
    previous_definition: dict[str, Any] | None,
    request: DownloadBundleRequest,
    paths: dict[str, str],
) -> dict[str, str]:
    roles = {
        "diffusion": (request.diffusion_repo, request.diffusion_file),
        "vae": (request.vae_repo, request.vae_file),
        "textEncoder": (request.text_encoder_repo, request.text_encoder_file),
    }
    states: dict[str, str] = {}
    for role, (repo, filename) in roles.items():
        if should_skip_hf_download(
            paths[role],
            filename,
            request_force_redownload(request),
            repo=repo,
            hf_token=(request.hf_token or "").strip() or None,
            revision=request.revision,
        ):
            states[role] = "ready"
        else:
            states[role] = "queued"
    return states


def _status_is_terminal(status: str | None) -> bool:
    normalized = (status or "").strip().lower()
    return normalized in {"completed", "failed", "error", "cancelled", "canceled"}


def start_bundle_download(request: DownloadBundleRequest, model_dir: str) -> dict[str, Any]:
    bundle_id = validate_bundle_id(request.bundle_id)
    with BUNDLE_OPS_LOCK:
        existing = find_in_flight_operation(BUNDLE_OPERATIONS, bundle_id=bundle_id)
        if existing is not None:
            raise HTTPException(
                status_code=409,
                detail={
                    "error": f"A download for bundle '{bundle_id}' is already in progress.",
                    **dict(existing),
                },
            )
    previous_definition = read_bundle_definition(model_dir, bundle_id)
    paths = expected_bundle_paths(model_dir, bundle_id)
    operation_id = uuid.uuid4().hex
    operation = {
        "operationId": operation_id,
        "bundleId": bundle_id,
        "status": "queued",
        "roles": resolve_initial_bundle_role_states(previous_definition, request, paths),
        "error": None,
        "cancelRequested": False,
        "completedAtUtc": None,
    }
    with BUNDLE_OPS_LOCK:
        BUNDLE_OPERATIONS[operation_id] = operation
    try:
        # Persist the declared bundle recipe up front so operators can read and
        # edit the definition even if a download fails mid-way.
        write_bundle_definition(model_dir, request, bundle_id)
    except Exception as exc:
        log_event(
            "sd_bundle_definition_write_failed",
            bundleId=bundle_id,
            error=truncate_text(str(exc), 2048),
        )

    def _cancel_requested() -> bool:
        with BUNDLE_OPS_LOCK:
            current = BUNDLE_OPERATIONS.get(operation_id)
            return bool(current and current.get("cancelRequested"))

    def _mark_cancelled() -> None:
        with BUNDLE_OPS_LOCK:
            current = BUNDLE_OPERATIONS.get(operation_id)
            if current is None:
                return
            current["status"] = "cancelled"
            current["error"] = "Cancelled by operator."
            current["completedAtUtc"] = utc_now_iso()

    def _run() -> None:
        try:
            if _cancel_requested():
                _mark_cancelled()
                return

            hf_token = (request.hf_token or "").strip() or None

            roles = {
                "diffusion": (request.diffusion_repo, request.diffusion_file),
                "vae": (request.vae_repo, request.vae_file),
                "textEncoder": (request.text_encoder_repo, request.text_encoder_file),
            }
            for role, (repo, filename) in roles.items():
                if _cancel_requested():
                    _mark_cancelled()
                    return

                target_path = paths[role]
                if should_skip_hf_download(
                    target_path,
                    filename,
                    request_force_redownload(request),
                    repo=repo,
                    hf_token=hf_token,
                    revision=request.revision,
                ):
                    expected_file = resolve_role_file_path(target_path, filename)
                    ensure_role_file_metadata(
                        repo=repo,
                        filename=filename,
                        expected_file=expected_file,
                        hf_token=hf_token,
                        revision=request.revision,
                    )
                    clear_stale_role_files(target_path, filename)
                    with BUNDLE_OPS_LOCK:
                        BUNDLE_OPERATIONS[operation_id]["roles"][role] = "ready"
                    continue

                with BUNDLE_OPS_LOCK:
                    BUNDLE_OPERATIONS[operation_id]["status"] = "running"
                    BUNDLE_OPERATIONS[operation_id]["roles"][role] = "downloading"
                clear_stale_role_files(target_path, filename)
                if request_force_redownload(request):
                    remove_role_expected_file(target_path, filename)
                download_bundle_role_via_staging(
                    model_dir=model_dir,
                    operation_id=operation_id,
                    role=role,
                    repo=repo,
                    filename=filename,
                    target_path=target_path,
                    hf_token=hf_token,
                    revision=request.revision,
                )
                with BUNDLE_OPS_LOCK:
                    BUNDLE_OPERATIONS[operation_id]["roles"][role] = "ready"

            if _cancel_requested():
                _mark_cancelled()
                return

            with BUNDLE_OPS_LOCK:
                BUNDLE_OPERATIONS[operation_id]["status"] = "completed"
                BUNDLE_OPERATIONS[operation_id]["completedAtUtc"] = utc_now_iso()
        except Exception as exc:
            with BUNDLE_OPS_LOCK:
                current = BUNDLE_OPERATIONS.get(operation_id)
                if current is None:
                    return
                if current.get("cancelRequested"):
                    current["status"] = "cancelled"
                    current["error"] = "Cancelled by operator."
                else:
                    current["status"] = "failed"
                    current["error"] = str(exc)
                current["completedAtUtc"] = utc_now_iso()
        finally:
            cleanup_bundle_operation_staging(model_dir, operation_id)

    threading.Thread(target=_run, daemon=True).start()
    return operation

def build_sd_server_command(config: SdRuntimeConfig) -> list[str]:
    command = [
        config.server_path,
        "--listen-ip",
        config.engine_host,
        "--listen-port",
        str(config.engine_port),
        "--diffusion-model",
        config.diffusion_model_path,
        "--vae",
        config.vae_path,
        "--llm",
        config.llm_path,
        "--steps",
        str(config.steps),
        "--cfg-scale",
        str(config.cfg_scale),
        "--sampling-method",
        config.sampling_method,
        "-s",
        "-1",
    ]

    if config.auto_fit:
        command.append("--auto-fit")
    else:
        if config.backend:
            command.extend(["--backend", config.backend])
        if config.params_backend:
            command.extend(["--params-backend", config.params_backend])
        elif config.offload_to_cpu:
            command.append("--offload-to-cpu")
        if not config.backend and config.vae_on_cpu:
            command.append("--vae-on-cpu")
    if config.split_mode:
        command.extend(["--split-mode", config.split_mode])
    if config.max_vram:
        command.extend(["--max-vram", config.max_vram])
    if config.diffusion_fa:
        command.append("--diffusion-fa")

    return command


def build_sd_server_environment(config: SdRuntimeConfig) -> dict[str, str]:
    env = os.environ.copy()
    if config.vulkan_visible_devices is not None:
        env["GGML_VK_VISIBLE_DEVICES"] = config.vulkan_visible_devices
    return env


def is_engine_process_alive() -> bool:
    process = STATE.engine_process
    return process is not None and process.poll() is None


def describe_engine_process_failure() -> str:
    process = STATE.engine_process
    if process is None:
        return "sd-server process is not running"
    exit_code = process.poll()
    if exit_code is None:
        return "sd-server connection failed while the process was still running"
    return f"sd-server exited unexpectedly (exit code {exit_code})"


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
        if not is_engine_process_alive():
            raise RuntimeError(
                f"Failed to reach sd-server at {url}: {describe_engine_process_failure()} "
                f"(connection error: {reason})"
            ) from exc
        raise RuntimeError(f"Failed to reach sd-server at {url}: {reason}") from exc


def sd_server_json_request(
    config: SdRuntimeConfig,
    method: str,
    path: str,
    timeout_seconds: int,
    request_id: str | None = None,
    traceparent: str | None = None,
    payload: dict[str, Any] | None = None,
) -> tuple[int, dict[str, Any]]:
    headers: dict[str, str] = {"Accept": "application/json"}
    if request_id:
        headers["x-request-id"] = request_id
    if traceparent:
        headers["traceparent"] = traceparent

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

    parsed = decode_json_bytes(response_body, f"sd-server {method} {path}")
    return status_code, parsed


def sd_server_multipart_request(
    config: SdRuntimeConfig,
    path: str,
    timeout_seconds: int,
    request_id: str | None,
    traceparent: str | None,
    fields: dict[str, str],
    files: list[tuple[str, str, bytes, str]],
) -> tuple[int, dict[str, Any]]:
    body, content_type = build_multipart_form_data(fields, files)
    headers = {
        "Accept": "application/json",
        "Content-Type": content_type,
    }
    if request_id:
        headers["x-request-id"] = request_id
    if traceparent:
        headers["traceparent"] = traceparent

    status_code, response_body = perform_http_request(
        method="POST",
        url=f"{config.engine_base_url}{path}",
        timeout_seconds=timeout_seconds,
        headers=headers,
        body=body,
    )
    parsed = decode_json_bytes(response_body, f"sd-server POST {path}")
    return status_code, parsed


def wait_for_engine_ready(config: SdRuntimeConfig) -> None:
    deadline = time.monotonic() + config.engine_ready_timeout_seconds
    probe_timeout = min(5, config.timeout_seconds)
    while time.monotonic() < deadline:
        if not is_engine_process_alive():
            process = STATE.engine_process
            exit_code = process.poll() if process is not None else None
            raise RuntimeError(f"sd-server exited before readiness check completed (exit code: {exit_code}).")
        try:
            status_code, _ = sd_server_json_request(
                config,
                method="GET",
                path="/v1/models",
                timeout_seconds=probe_timeout,
            )
            if status_code == 200:
                return
        except Exception:
            pass
        time.sleep(1.0)

    raise RuntimeError(f"Timed out waiting for sd-server readiness after {config.engine_ready_timeout_seconds} seconds.")


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
    """
    Stop the sd-server subprocess and clear the runtime state that depends on
    it. Caller must hold ENGINE_LOCK. Safe to call when the engine is already
    stopped.
    """
    stop_engine_process()
    STATE.config = None
    STATE.loaded_bundle_id = None
    STATE.loaded_at_utc = None
    STATE.engine_started_at_utc = None


def start_engine(*, bundle_id: str) -> tuple[bool, str | None]:
    """
    Load ``bundle_id`` into sd-server. Caller must hold ENGINE_LOCK.

    Returns ``(ok, error)``. On success writes active_bundle.json as a derived
    record of the last bundle that was loaded successfully.
    """
    if is_engine_process_alive():
        if STATE.loaded_bundle_id == bundle_id:
            return True, None
        stop_engine()

    try:
        config = resolve_runtime_config(bundle_id=bundle_id)
    except RuntimeError as exc:
        config_error = truncate_text(str(exc), 2048)
        STATE.config = None
        STATE.loaded_bundle_id = None
        STATE.loaded_at_utc = None
        STATE.engine_started_at_utc = None
        STATE.config_error = "engine_config_invalid"
        log_event("sd_engine_start_config_error", reason=config_error)
        return False, STATE.config_error

    STATE.config = config
    STATE.loaded_bundle_id = bundle_id
    command = build_sd_server_command(config)
    log_event(
        "sd_engine_start",
        serverPath=config.server_path,
        engineHost=config.engine_host,
        enginePort=config.engine_port,
        modelDir=config.model_dir,
        diffusionModelPath=config.diffusion_model_path,
        vaePath=config.vae_path,
        llmPath=config.llm_path,
        bundleId=STATE.loaded_bundle_id,
        vulkanVisibleDevices=config.vulkan_visible_devices,
        command=command,
    )

    try:
        STATE.engine_process = subprocess.Popen(command, env=build_sd_server_environment(config))
        STATE.engine_started_at_utc = utc_now_iso()
        wait_for_engine_ready(config)
        STATE.loaded_at_utc = utc_now_iso()
        STATE.config_error = None
        write_active_bundle_marker(config.model_dir, bundle_id)
        log_event(
            "sd_engine_ready",
            bundleId=STATE.loaded_bundle_id,
            pid=STATE.engine_process.pid,
        )
        return True, None
    except Exception as exc:
        error_msg = str(exc)
        stop_engine_process()
        STATE.config = None
        STATE.loaded_bundle_id = None
        STATE.loaded_at_utc = None
        STATE.engine_started_at_utc = None
        STATE.config_error = "engine_start_failed"
        log_event(
            "sd_engine_start_failed",
            errorType=type(exc).__name__,
            error=truncate_text(error_msg, 2048),
        )
        return False, STATE.config_error


def engine_state_dict() -> dict[str, Any]:
    """
    Snapshot of the engine subprocess state for UI / health consumers. Safe to
    call without holding ENGINE_LOCK.
    """
    alive = is_engine_process_alive()
    config = STATE.config
    process = STATE.engine_process
    state: dict[str, Any] = {
        "processAlive": alive,
        "loadedBundleId": STATE.loaded_bundle_id if alive else None,
        "loadedAtUtc": STATE.loaded_at_utc if alive else None,
        "startedAtUtc": STATE.engine_started_at_utc if alive else None,
        "pid": process.pid if process is not None else None,
        "exitCode": None if process is None or alive else process.poll(),
        "lastError": STATE.config_error,
    }
    if alive and config is not None:
        state["config"] = {
            "diffusionModelPath": config.diffusion_model_path,
            "vaePath": config.vae_path,
            "llmPath": config.llm_path,
            "engineBaseUrl": config.engine_base_url,
        }
    else:
        state["config"] = None
    return state


def extract_first_image_bytes(payload: dict[str, Any], context: str) -> bytes:
    data = payload.get("data")
    if not isinstance(data, list) or len(data) == 0:
        raise RuntimeError(f"{context} did not return any images.")
    first = data[0]
    if not isinstance(first, dict):
        raise RuntimeError(f"{context} returned malformed image payload.")
    encoded = first.get("b64_json")
    if not isinstance(encoded, str) or not encoded:
        raise RuntimeError(f"{context} returned empty image data.")

    try:
        return base64.b64decode(encoded, validate=True)
    except Exception as exc:
        raise RuntimeError(f"{context} returned invalid base64 image data.") from exc

def build_native_sdcpp_payload(
    prompt: str,
    width: int,
    height: int,
    output_format: str,
    steps: int,
    init_image_b64: str | None,
    strength: float,
    cfg_scale: float,
    sampling_method: str,
) -> dict[str, Any]:
    payload: dict[str, Any] = {
        "prompt": prompt,
        "width": width,
        "height": height,
        "seed": -1,
        "batch_count": 1,
        "output_format": output_format,
        "sample_params": {
            "sample_steps": steps,
            "sample_method": sampling_method,
            "guidance": {"txt_cfg": cfg_scale},
        },
    }

    if init_image_b64 is not None:
        payload["init_image"] = init_image_b64
        payload["strength"] = strength

    return payload


def submit_and_wait_for_job_result(
    config: SdRuntimeConfig,
    request_id: str,
    traceparent: str | None,
    payload: dict[str, Any],
    request_timeout_seconds: int,
) -> dict[str, Any]:
    effective_request_timeout_seconds = max(1, min(request_timeout_seconds, config.timeout_seconds))

    submit_status, submit_body = sd_server_json_request(
        config,
        method="POST",
        path="/sdcpp/v1/img_gen",
        timeout_seconds=effective_request_timeout_seconds,
        request_id=request_id,
        traceparent=traceparent,
        payload=payload,
    )

    if submit_status not in {200, 202}:
        raise RuntimeError(
            f"sd-server job submission failed ({submit_status}): {truncate_text(json.dumps(submit_body), 2048)}"
        )

    job_id = submit_body.get("id")
    poll_url = submit_body.get("poll_url")
    if not isinstance(job_id, str) or not job_id:
        raise RuntimeError("sd-server job submission response did not include a valid job id.")
    if not isinstance(poll_url, str) or not poll_url:
        poll_url = f"/sdcpp/v1/jobs/{job_id}"

    deadline = time.monotonic() + config.timeout_seconds
    last_status = submit_body.get("status")
    while time.monotonic() < deadline:
        if not is_engine_process_alive():
            process = STATE.engine_process
            exit_code = process.poll() if process is not None else None
            raise RuntimeError(f"sd-server exited while waiting for job {job_id} (exit code: {exit_code}).")

        poll_status, poll_body = sd_server_json_request(
            config,
            method="GET",
            path=poll_url,
            timeout_seconds=effective_request_timeout_seconds,
            request_id=request_id,
            traceparent=traceparent,
        )
        if poll_status != 200:
            raise RuntimeError(
                f"sd-server job poll failed ({poll_status}) for {job_id}: {truncate_text(json.dumps(poll_body), 2048)}"
            )

        job_status = poll_body.get("status")
        if isinstance(job_status, str):
            last_status = job_status
        if job_status == "completed":
            result = poll_body.get("result")
            if not isinstance(result, dict):
                raise RuntimeError(f"sd-server job {job_id} completed without result payload.")
            return result
        if job_status in {"failed", "cancelled"}:
            error_payload = poll_body.get("error")
            if isinstance(error_payload, dict):
                message = str(error_payload.get("message") or error_payload)
            else:
                message = str(error_payload or f"Job status {job_status}")
            raise RuntimeError(f"sd-server job {job_id} {job_status}: {message}")

        time.sleep(config.poll_interval_seconds)

    try:
        sd_server_json_request(
            config,
            method="POST",
            path=f"/sdcpp/v1/jobs/{job_id}/cancel",
            timeout_seconds=5,
            request_id=request_id,
            traceparent=traceparent,
            payload={},
        )
    except Exception:
        pass

    raise RuntimeError(
        f"sd-server job {job_id} timed out after {config.timeout_seconds} seconds (last status: {last_status})."
    )


def run_sd_generation_via_engine(
    config: SdRuntimeConfig,
    request_id: str,
    traceparent: str | None,
    prompt: str,
    size: str,
    output_format: str,
    n: int,
    init_image_bytes: bytes | None = None,
    init_image_name: str | None = None,
    steps_override: int | None = None,
    request_timeout_seconds: int | None = None,
) -> bytes:
    width, height = parse_size(size)
    if not prompt or not prompt.strip():
        raise ValueError("Prompt cannot be empty.")
    if n < 1:
        raise ValueError("n must be >= 1.")
    if not is_engine_process_alive():
        raise RuntimeError("sd-server process is not running.")

    steps = steps_override if steps_override is not None else config.steps
    effective_request_timeout_seconds = request_timeout_seconds or config.engine_request_timeout_seconds
    init_image_b64 = base64.b64encode(init_image_bytes).decode("ascii") if init_image_bytes is not None else None

    payload = build_native_sdcpp_payload(
        prompt=prompt,
        width=width,
        height=height,
        output_format=output_format,
        steps=steps,
        init_image_b64=init_image_b64,
        strength=config.strength,
        cfg_scale=config.cfg_scale,
        sampling_method=config.sampling_method,
    )

    started = time.perf_counter()
    log_event(
        "sd_cli_start",
        requestId=request_id,
        traceparent=traceparent,
        latencyMs=0,
        width=width,
        height=height,
        outputFormat=output_format,
        initImage=init_image_bytes is not None,
        initImageFileName=init_image_name,
        steps=steps,
        cfgScale=config.cfg_scale,
        strength=config.strength,
        samplingMethod=config.sampling_method,
        offloadToCpu=config.offload_to_cpu,
        vaeOnCpu=config.vae_on_cpu,
        diffusionFa=config.diffusion_fa,
        timeoutSeconds=config.timeout_seconds,
        engineMode="sd-server",
        **prompt_metadata(prompt),
    )

    try:
        result_payload = submit_and_wait_for_job_result(
            config=config,
            request_id=request_id,
            traceparent=traceparent,
            payload=payload,
            request_timeout_seconds=effective_request_timeout_seconds,
        )
        image_bytes = extract_first_image_bytes(
            {"data": result_payload.get("images") or []},
            context="sd-server img_gen result",
        )
        latency_ms = int((time.perf_counter() - started) * 1000)
        log_event(
            "sd_cli_success",
            requestId=request_id,
            traceparent=traceparent,
            latencyMs=latency_ms,
            width=width,
            height=height,
            outputBytes=len(image_bytes),
            outputFormat=output_format,
            initImage=init_image_bytes is not None,
            engineMode="sd-server",
            **prompt_metadata(prompt),
        )
        return image_bytes
    except Exception as exc:
        latency_ms = int((time.perf_counter() - started) * 1000)
        log_event(
            "sd_cli_failed",
            requestId=request_id,
            traceparent=traceparent,
            latencyMs=latency_ms,
            width=width,
            height=height,
            initImage=init_image_bytes is not None,
            errorType=type(exc).__name__,
            error=truncate_text(str(exc), 2048),
            engineMode="sd-server",
            **prompt_metadata(prompt),
        )
        raise


def run_sd_edit_via_openai_endpoint(
    config: SdRuntimeConfig,
    request_id: str,
    traceparent: str | None,
    prompt: str,
    size: str,
    output_format: str,
    n: int,
    image_bytes: bytes,
    image_file_name: str,
    image_content_type: str,
) -> bytes:
    # Native API currently supports webp; OpenAI edits endpoint does not.
    # Keep webp requests on native API path so behavior remains compatible.
    if output_format == "webp":
        return run_sd_generation_via_engine(
            config=config,
            request_id=request_id,
            traceparent=traceparent,
            prompt=prompt,
            size=size,
            output_format=output_format,
            n=n,
            init_image_bytes=image_bytes,
            init_image_name=image_file_name,
        )

    if not is_engine_process_alive():
        raise RuntimeError("sd-server process is not running.")

    width, height = parse_size(size)
    if not prompt or not prompt.strip():
        raise ValueError("Prompt cannot be empty.")
    if n < 1:
        raise ValueError("n must be >= 1.")

    started = time.perf_counter()
    log_event(
        "sd_cli_start",
        requestId=request_id,
        traceparent=traceparent,
        latencyMs=0,
        width=width,
        height=height,
        outputFormat=output_format,
        initImage=True,
        initImageFileName=image_file_name,
        steps=config.steps,
        cfgScale=config.cfg_scale,
        strength=config.strength,
        samplingMethod=config.sampling_method,
        offloadToCpu=config.offload_to_cpu,
        vaeOnCpu=config.vae_on_cpu,
        diffusionFa=config.diffusion_fa,
        timeoutSeconds=config.timeout_seconds,
        engineMode="sd-server-openai-edits",
        **prompt_metadata(prompt),
    )

    form_fields = {
        "prompt": prompt,
        "n": "1",
        "size": size,
        "output_format": output_format,
    }
    files = [("image", image_file_name or "input.png", image_bytes, image_content_type or "application/octet-stream")]

    status_code, response_body = sd_server_multipart_request(
        config=config,
        path="/v1/images/edits",
        timeout_seconds=config.timeout_seconds,
        request_id=request_id,
        traceparent=traceparent,
        fields=form_fields,
        files=files,
    )
    if status_code != 200:
        raise RuntimeError(
            f"sd-server OpenAI edits call failed ({status_code}): {truncate_text(json.dumps(response_body), 2048)}"
        )
    image_result = extract_first_image_bytes(response_body, "sd-server /v1/images/edits")
    latency_ms = int((time.perf_counter() - started) * 1000)
    log_event(
        "sd_cli_success",
        requestId=request_id,
        traceparent=traceparent,
        latencyMs=latency_ms,
        width=width,
        height=height,
        outputBytes=len(image_result),
        outputFormat=output_format,
        initImage=True,
        engineMode="sd-server-openai-edits",
        **prompt_metadata(prompt),
    )
    return image_result


def success_payload(image_bytes: bytes, request_id: str) -> dict[str, Any]:
    return {
        "requestId": request_id,
        "data": [{"b64_json": base64.b64encode(image_bytes).decode("ascii")}],
    }


def error_payload(request_id: str, _exc: Exception) -> dict[str, Any]:
    return {
        "requestId": request_id,
        "error": {
            "code": "sd_generation_failed",
            "message": "Image generation failed. Check service logs for details.",
        },
    }


def run_startup_warmup(
    config: SdRuntimeConfig,
    request_id_prefix: str = "sd-startup",
    prompt: str | None = None,
    size: str | None = None,
    output_format: str | None = None,
    steps_override: int | None = None,
) -> dict[str, Any]:
    warmup_prompt = (prompt if prompt is not None else os.getenv("GA_SD_WARMUP_PROMPT") or "startup warmup").strip()
    warmup_size = (size if size is not None else os.getenv("GA_SD_WARMUP_SIZE") or "512x512").strip()
    warmup_steps = steps_override if steps_override is not None else parse_positive_int(os.getenv("GA_SD_WARMUP_STEPS"), 1)
    warmup_output_format = normalize_output_format(
        output_format if output_format is not None else os.getenv("GA_SD_WARMUP_OUTPUT_FORMAT"),
        config.default_output_format,
    )
    warmup_request_id = f"{request_id_prefix}-{uuid.uuid4().hex}"
    warmup_started = time.perf_counter()

    STATE.startup_warmup_last_attempt_at_utc = utc_now_iso()
    STATE.startup_warmup_running = True

    log_event(
        "sd_startup_warmup_start",
        requestId=warmup_request_id,
        size=warmup_size,
        outputFormat=warmup_output_format,
        steps=warmup_steps,
        requestTimeoutSeconds=config.warmup_request_timeout_seconds,
        **prompt_metadata(warmup_prompt),
    )

    try:
        image = run_sd_generation_via_engine(
            config=config,
            request_id=warmup_request_id,
            traceparent=None,
            prompt=warmup_prompt,
            size=warmup_size,
            output_format=warmup_output_format,
            n=1,
            steps_override=warmup_steps,
            request_timeout_seconds=config.warmup_request_timeout_seconds,
        )
        STATE.startup_warmup_completed_at_utc = utc_now_iso()
        STATE.startup_warmup_last_error = None
        latency_ms = int((time.perf_counter() - warmup_started) * 1000)
        log_event(
            "sd_startup_warmup_success",
            requestId=warmup_request_id,
            latencyMs=latency_ms,
            outputBytes=len(image),
            completedAtUtc=STATE.startup_warmup_completed_at_utc,
        )
        return {
            "ok": True,
            "requestId": warmup_request_id,
            "latencyMs": latency_ms,
            "outputBytes": len(image),
            "completedAtUtc": STATE.startup_warmup_completed_at_utc,
            "requestTimeoutSeconds": config.warmup_request_timeout_seconds,
        }
    except Exception as exc:
        warmup_error = truncate_text(str(exc), 2048)
        STATE.startup_warmup_last_error = "startup_warmup_failed"
        latency_ms = int((time.perf_counter() - warmup_started) * 1000)
        log_event(
            "sd_startup_warmup_failed",
            requestId=warmup_request_id,
            latencyMs=latency_ms,
            errorType=type(exc).__name__,
            error=warmup_error,
        )
        return {
            "ok": False,
            "requestId": warmup_request_id,
            "latencyMs": latency_ms,
            "error": "startup_warmup_failed",
            "requestTimeoutSeconds": config.warmup_request_timeout_seconds,
        }
    finally:
        STATE.startup_warmup_running = False


@APP.on_event("startup")
async def on_startup() -> None:
    # Control plane is always up. The admin bundle / engine-lifecycle
    # endpoints only need model_dir to work, and they must work even when
    # there is no bundle yet (otherwise there is no way to create the first
    # one) or when the last load failed.
    STATE.model_dir = os.getenv("GA_SD_MODEL_DIR", "/models-local/sd")
    STATE.startup_warmup_enabled = False
    STATE.startup_warmup_completed_at_utc = None
    STATE.startup_warmup_last_attempt_at_utc = None
    STATE.startup_warmup_last_error = None
    STATE.startup_warmup_running = False
    STATE.engine_started_at_utc = None
    STATE.loaded_bundle_id = None


@APP.on_event("shutdown")
async def on_shutdown() -> None:
    with ENGINE_LOCK:
        stop_engine()


@APP.get("/health")
async def health() -> dict[str, Any]:
    config = STATE.config
    if config is None:
        # Unloaded: the control plane is up but no sd-server subprocess is
        # running. Either no active bundle has been selected yet, the last
        # load failed, or an operator issued POST /admin/unload. The UI can
        # still use /admin/bundles and /admin/load. Inference endpoints will
        # return 503 until the engine is loaded.
        return {
            "status": "unloaded",
            "loadedAtUtc": None,
            "loadedBundleId": None,
            "modelDir": STATE.model_dir,
            "configError": STATE.config_error,
            "engine": engine_state_dict(),
            "startupWarmup": {
                "enabled": STATE.startup_warmup_enabled,
                "running": STATE.startup_warmup_running,
                "completedAtUtc": STATE.startup_warmup_completed_at_utc,
                "lastAttemptAtUtc": STATE.startup_warmup_last_attempt_at_utc,
                "lastError": STATE.startup_warmup_last_error,
            },
        }

    engine_alive = is_engine_process_alive()
    engine_healthy = False
    if engine_alive:
        try:
            status_code, _ = sd_server_json_request(
                config,
                method="GET",
                path="/v1/models",
                timeout_seconds=min(5, config.timeout_seconds),
            )
            engine_healthy = status_code == 200
        except Exception:
            engine_healthy = False

    status = "ok" if (engine_alive and engine_healthy) else "degraded"
    process = STATE.engine_process
    return {
        "status": status,
        "loadedAtUtc": STATE.loaded_at_utc,
        "loadedBundleId": STATE.loaded_bundle_id,
        "config": {
            "serverPath": config.server_path,
            "engineHost": config.engine_host,
            "enginePort": config.engine_port,
            "modelDir": config.model_dir,
            "diffusionModelPath": config.diffusion_model_path,
            "vaePath": config.vae_path,
            "llmPath": config.llm_path,
            "timeoutSeconds": config.timeout_seconds,
            "engineRequestTimeoutSeconds": config.engine_request_timeout_seconds,
            "warmupRequestTimeoutSeconds": config.warmup_request_timeout_seconds,
            "steps": config.steps,
            "cfgScale": config.cfg_scale,
            "strength": config.strength,
            "samplingMethod": config.sampling_method,
            "offloadToCpu": config.offload_to_cpu,
            "vaeOnCpu": config.vae_on_cpu,
            "backend": config.backend,
            "paramsBackend": config.params_backend,
            "splitMode": config.split_mode,
            "maxVram": config.max_vram,
            "autoFit": config.auto_fit,
            "diffusionFa": config.diffusion_fa,
            "vulkanVisibleDevices": config.vulkan_visible_devices,
        },
        "engine": {
            "startedAtUtc": STATE.engine_started_at_utc,
            "processAlive": engine_alive,
            "healthy": engine_healthy,
            "pid": process.pid if process is not None else None,
            "exitCode": None if process is None or engine_alive else process.poll(),
        },
        "startupWarmup": {
            "enabled": STATE.startup_warmup_enabled,
            "running": STATE.startup_warmup_running,
            "completedAtUtc": STATE.startup_warmup_completed_at_utc,
            "lastAttemptAtUtc": STATE.startup_warmup_last_attempt_at_utc,
            "lastError": STATE.startup_warmup_last_error,
            "failOpenOnStartup": config.startup_warmup_fail_open,
        },
    }


@APP.post("/admin/warmup")
async def admin_warmup(request: WarmupRequest | None = None) -> JSONResponse:
    config = STATE.config
    if config is None:
        return JSONResponse(
            status_code=503,
            content={"ok": False, "error": "SD service is not ready."},
        )

    if not WARMUP_LOCK.acquire(blocking=False):
        return JSONResponse(
            status_code=409,
            content={"ok": False, "error": "Warmup is already in progress."},
        )

    try:
        payload = request or WarmupRequest()
        result = run_startup_warmup(
            config=config,
            request_id_prefix="sd-admin-warmup",
            prompt=payload.prompt,
            size=payload.size,
            output_format=payload.outputFormat,
            steps_override=payload.steps,
        )
        return JSONResponse(status_code=200 if result.get("ok") else 500, content=result)
    finally:
        WARMUP_LOCK.release()


def _require_model_dir() -> str:
    """
    Admin bundle routes only need the model_dir control-plane value, which is
    populated unconditionally at startup. If it has somehow not been set
    (should be impossible outside of tests that bypass on_startup), raise a
    500 rather than silently failing.
    """
    model_dir = STATE.model_dir
    if not model_dir:
        raise HTTPException(
            status_code=500,
            detail="SD control plane is not initialized (STATE.model_dir unset).",
        )
    return model_dir


def require_valid_bundle_id(bundle_id: str) -> str:
    try:
        return validate_bundle_id(bundle_id)
    except ValueError:
        raise HTTPException(status_code=400, detail="invalid bundle_id")


@APP.get("/admin/bundles")
async def admin_list_bundles() -> JSONResponse:
    model_dir = _require_model_dir()
    items = list_bundles(model_dir)
    engine_alive = is_engine_process_alive()
    loaded_id = STATE.loaded_bundle_id if engine_alive else None
    for item in items:
        # `active` == marked in active_bundle.json, `loaded` == present in the
        # running engine. They differ briefly when a hot-swap is in flight or
        # the engine has been unloaded while a marker is still set.
        item["loaded"] = item["bundleId"] == loaded_id
    return JSONResponse(
        status_code=200,
        content={
            "modelDir": model_dir,
            "legacyMarkerBundleId": read_active_bundle(model_dir),
            "loadedBundleId": loaded_id,
            "engine": engine_state_dict(),
            "items": items,
        },
    )


@APP.get("/admin/bundles/{bundle_id}")
async def admin_get_bundle(bundle_id: str) -> JSONResponse:
    model_dir = _require_model_dir()
    bundle_id = require_valid_bundle_id(bundle_id)
    bundle = next((item for item in list_bundles(model_dir) if item["bundleId"] == bundle_id), None)
    if bundle is None:
        raise HTTPException(status_code=404, detail="bundle not found")
    engine_alive = is_engine_process_alive()
    loaded_id = STATE.loaded_bundle_id if engine_alive else None
    bundle["loaded"] = bundle["bundleId"] == loaded_id
    return JSONResponse(status_code=200, content=bundle)


@APP.put("/admin/bundles/{bundle_id}/definition")
async def admin_upsert_bundle_definition(bundle_id: str, payload: UpsertBundleDefinitionRequest) -> JSONResponse:
    model_dir = _require_model_dir()
    bundle_id = require_valid_bundle_id(bundle_id)
    try:
        definition = upsert_bundle_definition(model_dir, bundle_id, payload)
    except ValueError as exc:
        raise HTTPException(status_code=400, detail=str(exc)) from exc
    return JSONResponse(status_code=200, content={"bundleId": bundle_id, "definition": definition})


@APP.post("/admin/bundles/download")
async def admin_download_bundle(payload: DownloadBundleRequest) -> JSONResponse:
    model_dir = _require_model_dir()
    # Field-level validation is enforced by DownloadBundleRequest validators.
    operation = start_bundle_download(payload, model_dir)
    return JSONResponse(status_code=202, content=operation)


@APP.get("/admin/bundles/operations/{operation_id}")
async def admin_bundle_operation(operation_id: str) -> JSONResponse:
    with BUNDLE_OPS_LOCK:
        operation = BUNDLE_OPERATIONS.get(operation_id)
        if operation is None:
            raise HTTPException(status_code=404, detail="operation not found")
        return JSONResponse(status_code=200, content=dict(operation))


@APP.post("/admin/bundles/operations/{operation_id}/cancel")
async def admin_cancel_bundle_operation(operation_id: str) -> JSONResponse:
    with BUNDLE_OPS_LOCK:
        operation = BUNDLE_OPERATIONS.get(operation_id)
        if operation is None:
            raise HTTPException(status_code=404, detail="operation not found")
        if _status_is_terminal(operation.get("status")):
            return JSONResponse(status_code=200, content=dict(operation))
        operation["cancelRequested"] = True
        if operation.get("status") == "queued":
            operation["status"] = "cancelled"
            operation["error"] = "Cancelled by operator."
            operation["completedAtUtc"] = utc_now_iso()
        elif operation.get("status") in {"running", "downloading"}:
            operation["status"] = "cancelling"
        return JSONResponse(status_code=200, content=dict(operation))


@APP.post("/admin/bundles/{bundle_id}/select-active")
async def admin_select_active_bundle(bundle_id: str) -> JSONResponse:
    raise HTTPException(
        status_code=410,
        detail=(
            "Bundle selection is owned by ServiceModes. Use the GuideAnts "
            "select-active API, which writes warmup-desired.ini and calls "
            "POST /admin/load with bundle_id."
        ),
    )


class AdminLoadRequest(BaseModel):
    bundle_id: str | None = None


@APP.post("/admin/load")
async def admin_load(payload: AdminLoadRequest | None = None) -> JSONResponse:
    """
    Load ``bundle_id`` into sd-server. Selection authority is upstream
    (ServiceModes → warmup INI → orchestrator); this endpoint only executes
    the load request it is given.
    """
    bundle_id = require_valid_bundle_id((payload.bundle_id if payload else "") or "")
    model_dir = _require_model_dir()
    bundle = next((item for item in list_bundles(model_dir) if item["bundleId"] == bundle_id), None)
    if bundle is None:
        return JSONResponse(status_code=404, content={"ok": False, "error": "bundle not found"})
    if not bundle.get("complete"):
        missing = [name for name, role in bundle.get("roles", {}).items() if not role.get("ready")]
        return JSONResponse(
            status_code=409,
            content={
                "ok": False,
                "error": "bundle_incomplete",
                "missingRoles": missing,
            },
        )

    if not ENGINE_LOCK.acquire(blocking=False):
        return JSONResponse(
            status_code=409,
            content={"ok": False, "error": "engine lifecycle operation already in progress"},
        )
    try:
        if is_engine_process_alive() and STATE.loaded_bundle_id == bundle_id:
            return JSONResponse(
                status_code=200,
                content={
                    "ok": True,
                    "action": "noop-already-loaded",
                    "bundleId": bundle_id,
                    "engine": engine_state_dict(),
                },
            )
        ok, err = start_engine(bundle_id=bundle_id)
        if ok:
            return JSONResponse(
                status_code=200,
                content={
                    "ok": True,
                    "action": "loaded",
                    "bundleId": bundle_id,
                    "engine": engine_state_dict(),
                },
            )
        return JSONResponse(
            status_code=503,
            content={
                "ok": False,
                "action": "load-failed",
                "error": err or "engine_start_failed",
                "bundleId": bundle_id,
                "engine": engine_state_dict(),
            },
        )
    finally:
        ENGINE_LOCK.release()


@APP.post("/admin/unload")
async def admin_unload() -> JSONResponse:
    """
    Stop the sd-server subprocess. No-op when already unloaded. Any inference
    request already in flight against sd-server will fail with a connection
    error once the subprocess exits — this is by design so operators can force
    a release of GPU / RAM without waiting for in-flight jobs.
    """
    if not ENGINE_LOCK.acquire(blocking=False):
        return JSONResponse(
            status_code=409,
            content={"ok": False, "error": "engine lifecycle operation already in progress"},
        )
    try:
        if not is_engine_process_alive():
            log_event(
                "sd_engine_unload",
                action="noop-already-unloaded",
                bundleId=STATE.loaded_bundle_id,
            )
            return JSONResponse(
                status_code=200,
                content={
                    "ok": True,
                    "action": "noop-already-unloaded",
                    "engine": engine_state_dict(),
                },
            )
        unloaded_bundle_id = STATE.loaded_bundle_id
        stop_engine()
        log_event(
            "sd_engine_unload",
            action="unloaded",
            bundleId=unloaded_bundle_id,
        )
        return JSONResponse(
            status_code=200,
            content={
                "ok": True,
                "action": "unloaded",
                "engine": engine_state_dict(),
            },
        )
    finally:
        ENGINE_LOCK.release()


@APP.delete("/admin/bundles/{bundle_id}")
async def admin_delete_bundle(bundle_id: str) -> JSONResponse:
    model_dir = _require_model_dir()
    bundle_id = require_valid_bundle_id(bundle_id)
    if STATE.loaded_bundle_id == bundle_id:
        raise HTTPException(status_code=409, detail="cannot remove loaded bundle")

    try:
        target = resolve_bundle_dir(model_dir, bundle_id)
    except ValueError:
        raise HTTPException(status_code=400, detail="invalid bundle_id")
    if not os.path.exists(target):
        raise HTTPException(status_code=404, detail="bundle not found")
    shutil.rmtree(target)
    return JSONResponse(status_code=200, content={"deleted": True, "bundleId": bundle_id})


@APP.post("/txt2img")
async def txt2img(request: Request, payload: Txt2ImgRequest) -> JSONResponse:
    request_id = request.headers.get("x-request-id", str(uuid.uuid4()))
    traceparent = request.headers.get("traceparent")
    started = time.perf_counter()
    context = request_context(request, request_id)
    log_event(
        "sd_txt2img_request",
        size=payload.size,
        n=payload.n,
        outputFormat=payload.outputFormat,
        **prompt_metadata(payload.prompt),
        **context,
    )
    config = STATE.config
    if config is None:
        response = JSONResponse(
            status_code=503,
            content={"requestId": request_id, "error": {"code": "service_not_ready", "message": "SD service is not ready."}},
        )
        response.headers["x-request-id"] = request_id
        log_event("sd_txt2img_not_ready", latencyMs=int((time.perf_counter() - started) * 1000), **context)
        return response

    try:
        output_format = normalize_output_format(payload.outputFormat, config.default_output_format)
        image = run_sd_generation_via_engine(
            config=config,
            request_id=request_id,
            traceparent=traceparent,
            prompt=payload.prompt,
            size=payload.size,
            output_format=output_format,
            n=payload.n,
        )
        response = JSONResponse(status_code=200, content=success_payload(image, request_id))
        response.headers["x-request-id"] = request_id
        log_event(
            "sd_txt2img_response",
            latencyMs=int((time.perf_counter() - started) * 1000),
            statusCode=200,
            outputBytes=len(image),
            **context,
        )
        return response
    except Exception as exc:
        log_event(
            "sd_txt2img_failed",
            latencyMs=int((time.perf_counter() - started) * 1000),
            errorType=type(exc).__name__,
            error=truncate_text(str(exc), 2048),
            **context,
        )
        response = JSONResponse(status_code=500, content=error_payload(request_id, exc))
        response.headers["x-request-id"] = request_id
        return response


@APP.post("/img2img")
async def img2img(
    request: Request,
    prompt: str = Form(...),
    size: str = Form("1024x1024"),
    n: int = Form(1),
    outputFormat: str = Form("png"),
    image: UploadFile = File(...),
) -> JSONResponse:
    request_id = request.headers.get("x-request-id", str(uuid.uuid4()))
    traceparent = request.headers.get("traceparent")
    started = time.perf_counter()
    context = request_context(request, request_id)
    _ = n  # accepted for API compatibility; current runtime returns one image.
    config = STATE.config
    if config is None:
        response = JSONResponse(
            status_code=503,
            content={"requestId": request_id, "error": {"code": "service_not_ready", "message": "SD service is not ready."}},
        )
        response.headers["x-request-id"] = request_id
        log_event("sd_img2img_not_ready", latencyMs=int((time.perf_counter() - started) * 1000), **context)
        return response

    try:
        output_format = normalize_output_format(outputFormat, config.default_output_format)
        image_bytes = await image.read()
        image_bytes_len = len(image_bytes)
        log_event(
            "sd_img2img_request",
            size=size,
            n=n,
            outputFormat=outputFormat,
            imageBytes=image_bytes_len,
            imageContentType=image.content_type,
            imageFileName=image.filename,
            **prompt_metadata(prompt),
            **context,
        )

        output_image = run_sd_edit_via_openai_endpoint(
            config=config,
            request_id=request_id,
            traceparent=traceparent,
            prompt=prompt,
            size=size,
            output_format=output_format,
            n=n,
            image_bytes=image_bytes,
            image_file_name=image.filename or "input.png",
            image_content_type=image.content_type or "application/octet-stream",
        )
        response = JSONResponse(status_code=200, content=success_payload(output_image, request_id))
        response.headers["x-request-id"] = request_id
        log_event(
            "sd_img2img_response",
            latencyMs=int((time.perf_counter() - started) * 1000),
            statusCode=200,
            outputBytes=len(output_image),
            **context,
        )
        return response
    except Exception as exc:
        log_event(
            "sd_img2img_failed",
            latencyMs=int((time.perf_counter() - started) * 1000),
            errorType=type(exc).__name__,
            error=truncate_text(str(exc), 2048),
            **context,
        )
        response = JSONResponse(status_code=500, content=error_payload(request_id, exc))
        response.headers["x-request-id"] = request_id
        return response


if __name__ == "__main__":
    host = os.getenv("GA_SD_HOST", "127.0.0.1")
    port = parse_positive_int(os.getenv("GA_SD_PORT"), 8083)
    log_level = (os.getenv("GA_SD_LOG_LEVEL") or "info").strip().lower()
    access_log_enabled = env_flag("GA_SD_UVICORN_ACCESS_LOG", default=False)
    if access_log_enabled:
        configure_uvicorn_access_log_filters(
            ignore_health_requests=env_flag("GA_SD_SUPPRESS_HEALTH_ACCESS_LOGS", default=True)
        )
    uvicorn.run(APP, host=host, port=port, log_level=log_level, access_log=access_log_enabled)
