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
    diffusion_fa: bool
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
    revision: str | None = None
    hf_token: str | None = None

    @field_validator(
        "bundle_id",
        "diffusion_repo",
        "diffusion_file",
        "vae_repo",
        "vae_file",
        "text_encoder_repo",
        "text_encoder_file",
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
        if not BUNDLE_ID_RE.fullmatch(value):
            raise ValueError("bundle_id must match ^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")
        return value

    @field_validator("diffusion_file", "vae_file", "text_encoder_file")
    @classmethod
    def _reject_globs(cls, value: str) -> str:
        if "*" in value or "?" in value:
            raise ValueError(
                "must be a single filename (no '*' or '?' glob metacharacters)"
            )
        return value


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
WARMUP_LOCK = threading.Lock()
BUNDLE_OPS_LOCK = threading.Lock()
BUNDLE_OPERATIONS: dict[str, dict[str, Any]] = {}
# Serializes engine lifecycle operations (start / stop / hot-swap). Held only
# by lifecycle callers; inference endpoints do not acquire this lock so a
# long-running generation does not block a later unload request.
ENGINE_LOCK = threading.Lock()


def resolve_runtime_config() -> SdRuntimeConfig:
    model_dir = os.getenv("GA_SD_MODEL_DIR", "/models-local/sd")

    server_path = (os.getenv("GA_SD_SERVER_PATH") or "").strip()
    if not server_path:
        server_path = shutil.which("sd-server") or "/usr/local/bin/sd-server"
    elif not os.path.isabs(server_path):
        server_path = shutil.which(server_path) or server_path

    # The active bundle is the single authority for diffusion / VAE / text
    # encoder paths. Operators no longer configure these via environment
    # variables at all — if no bundle is active, the service refuses to
    # start. There is no hardcoded filename fallback on purpose: silent
    # defaults have repeatedly caused the service to launch against files the
    # operator did not pick.
    selected_bundle = read_active_bundle(model_dir)
    if not selected_bundle:
        raise RuntimeError(
            "No active Stable Diffusion bundle is selected. Download a "
            "bundle and mark it active via the /admin/bundles endpoints "
            "before starting the SD service."
        )

    bundle_paths = expected_bundle_paths(model_dir, selected_bundle)

    def _only_file(path: str, role: str) -> str:
        if not os.path.isdir(path):
            raise RuntimeError(
                f"Bundle '{selected_bundle}' is missing the {role} directory "
                f"at '{path}'."
            )
        files = [
            name
            for name in sorted(os.listdir(path))
            if os.path.isfile(os.path.join(path, name))
        ]
        if not files:
            raise RuntimeError(
                f"Bundle '{selected_bundle}' {role} directory '{path}' is empty."
            )
        if len(files) > 1:
            raise RuntimeError(
                f"Bundle '{selected_bundle}' {role} directory '{path}' "
                f"contains more than one file ({', '.join(files)}); a bundle "
                f"role must have exactly one file."
            )
        return os.path.join(path, files[0])

    diffusion_model_path = _only_file(bundle_paths["diffusion"], "diffusion")
    vae_path = _only_file(bundle_paths["vae"], "vae")
    llm_path = _only_file(bundle_paths["textEncoder"], "text encoder")

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

    steps = parse_positive_int(os.getenv("GA_SD_STEPS"), 4)
    cfg_scale = parse_positive_float(os.getenv("GA_SD_CFG_SCALE"), 1.0)
    strength = parse_positive_float(os.getenv("GA_SD_STRENGTH"), 0.75)
    sampling_method = (os.getenv("GA_SD_SAMPLING_METHOD") or "euler").strip() or "euler"
    offload_to_cpu = env_flag("GA_SD_OFFLOAD_TO_CPU", False)
    diffusion_fa = env_flag("GA_SD_DIFFUSION_FA", True)
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
        diffusion_fa=diffusion_fa,
        default_output_format=default_output_format,
        startup_warmup_fail_open=startup_warmup_fail_open,
    )


def bundle_root_dir(model_dir: str) -> str:
    root = os.path.join(model_dir, "bundles")
    os.makedirs(root, exist_ok=True)
    return root


def active_bundle_file(model_dir: str) -> str:
    return os.path.join(model_dir, "active_bundle.json")


def read_active_bundle(model_dir: str) -> str | None:
    marker = active_bundle_file(model_dir)
    if not os.path.exists(marker):
        return None
    try:
        with open(marker, "r", encoding="utf-8") as handle:
            payload = json.load(handle)
            bundle_id = payload.get("bundleId")
            return str(bundle_id) if bundle_id else None
    except Exception:
        return None


def expected_bundle_paths(model_dir: str, bundle_id: str) -> dict[str, str]:
    base = os.path.join(bundle_root_dir(model_dir), bundle_id)
    return {
        "diffusion": os.path.join(base, "diffusion"),
        "vae": os.path.join(base, "vae"),
        "textEncoder": os.path.join(base, "text-encoder"),
    }


def bundle_definition_file(model_dir: str, bundle_id: str) -> str:
    return os.path.join(bundle_root_dir(model_dir), bundle_id, "bundle-definition.json")


def write_bundle_definition_payload(model_dir: str, bundle_id: str, payload: dict[str, Any]) -> None:
    candidate_bundle_id = (bundle_id or "").strip()
    if not BUNDLE_ID_RE.fullmatch(candidate_bundle_id):
        raise ValueError("invalid bundle_id")
    root_real = os.path.realpath(bundle_root_dir(model_dir))
    bundle_path = os.path.realpath(os.path.join(root_real, candidate_bundle_id))
    if not bundle_path.startswith(root_real + os.sep):
        raise ValueError("resolved bundle path escapes the permitted bundle directory")
    os.makedirs(bundle_path, exist_ok=True)
    target = os.path.join(bundle_path, "bundle-definition.json")
    temp = f"{target}.{uuid.uuid4().hex}.tmp"
    with open(temp, "w", encoding="utf-8") as handle:
        json.dump(payload, handle, ensure_ascii=True, sort_keys=True)
    os.replace(temp, target)


def bundle_definition_payload(request: DownloadBundleRequest) -> dict[str, Any]:
    return {
        "bundleId": request.bundle_id,
        "revision": request.revision,
        "updatedAtUtc": utc_now_iso(),
        "roles": {
            "diffusion": {"repo": request.diffusion_repo, "file": request.diffusion_file},
            "vae": {"repo": request.vae_repo, "file": request.vae_file},
            "textEncoder": {
                "repo": request.text_encoder_repo,
                "file": request.text_encoder_file,
            },
        },
    }


def write_bundle_definition(model_dir: str, request: DownloadBundleRequest) -> None:
    write_bundle_definition_payload(model_dir, request.bundle_id, bundle_definition_payload(request))


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

    return {
        "revision": revision,
        "updatedAtUtc": updated_at_utc,
        "roles": normalized_roles,
    }


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


def infer_bundle_definition_from_files(model_dir: str, bundle_id: str) -> dict[str, Any] | None:
    """
    Backfill recipe metadata for bundles materialized by setup/migration paths
    that historically only copied files on disk and did not persist the source
    (repo, file) tuples.
    """
    def infer_repo_for_role(role: str, filename: str) -> str | None:
        if role == "vae":
            if filename == "full_encoder_small_decoder.safetensors":
                return "black-forest-labs/FLUX.2-small-decoder"
            return None
        if role == "diffusion":
            # Example: flux-2-klein-9b-Q5_K_M.gguf -> unsloth/FLUX.2-klein-9B-GGUF
            match = re.match(r"^flux[.-]?2-klein-(\d+)b-.*\.gguf$", filename, flags=re.IGNORECASE)
            if not match:
                return None
            size = match.group(1)
            return f"unsloth/FLUX.2-klein-{size}B-GGUF"
        if role == "textEncoder":
            # Example: Qwen3-8B-Q5_K_M.gguf -> unsloth/Qwen3-8B-GGUF
            match = re.match(r"^Qwen3-(\d+)B-.*\.gguf$", filename, flags=re.IGNORECASE)
            if not match:
                return None
            size = match.group(1)
            return f"unsloth/Qwen3-{size}B-GGUF"
        return None

    role_paths = expected_bundle_paths(model_dir, bundle_id)
    roles_payload: dict[str, dict[str, str]] = {}
    for role, path in role_paths.items():
        filename = _single_file_name(path)
        if not filename:
            return None
        repo = infer_repo_for_role(role, filename)
        if not repo:
            return None
        roles_payload[role] = {"repo": repo, "file": filename}

    return {
        "bundleId": bundle_id,
        "revision": "main",
        "updatedAtUtc": utc_now_iso(),
        "roles": roles_payload,
    }

def read_bundle_definition(model_dir: str, bundle_id: str) -> dict[str, Any] | None:
    path = bundle_definition_file(model_dir, bundle_id)
    normalized: dict[str, Any] | None = None
    if os.path.isfile(path):
        try:
            with open(path, "r", encoding="utf-8") as handle:
                payload = json.load(handle)
                normalized = _normalize_bundle_definition(payload)
        except Exception:
            normalized = None

    if normalized is None:
        inferred = infer_bundle_definition_from_files(model_dir, bundle_id)
        if inferred is None:
            return None
        try:
            write_bundle_definition_payload(model_dir, bundle_id, inferred)
        except Exception as exc:
            log_event(
                "sd_bundle_definition_backfill_failed",
                bundleId=bundle_id,
                error=truncate_text(str(exc), 2048),
            )
        normalized = inferred

    return {
        "revision": normalized.get("revision"),
        "updatedAtUtc": normalized.get("updatedAtUtc"),
        "roles": normalized.get("roles"),
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


def list_bundles(model_dir: str) -> list[dict[str, Any]]:
    root = bundle_root_dir(model_dir)
    active_bundle = read_active_bundle(model_dir)
    bundles: list[dict[str, Any]] = []
    try:
        bundle_ids = sorted(os.listdir(root))
    except OSError as exc:
        log_event(
            "sd_bundle_root_list_failed",
            errorType=type(exc).__name__,
            error=truncate_text(str(exc), 2048),
        )
        return bundles

    for bundle_id in bundle_ids:
        bundle_path = os.path.join(root, bundle_id)
        if not os.path.isdir(bundle_path):
            continue
        try:
            roles = expected_bundle_paths(model_dir, bundle_id)
            role_state = {
                role: {"path": path, "ready": role_directory_ready(path)}
                for role, path in roles.items()
            }
            bundle: dict[str, Any] = {
                "bundleId": bundle_id,
                "active": bundle_id == active_bundle,
                "roles": role_state,
                "complete": all(role["ready"] for role in role_state.values()),
            }
            definition = read_bundle_definition(model_dir, bundle_id)
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
                    "active": bundle_id == active_bundle,
                    "roles": {
                        role: {"path": path, "ready": False}
                        for role, path in roles.items()
                    },
                    "complete": False,
                }
            )
    return bundles


def start_bundle_download(request: DownloadBundleRequest, model_dir: str) -> dict[str, Any]:
    operation_id = uuid.uuid4().hex
    operation = {
        "operationId": operation_id,
        "bundleId": request.bundle_id,
        "status": "queued",
        "roles": {
            "diffusion": "queued",
            "vae": "queued",
            "textEncoder": "queued",
        },
        "error": None,
    }
    with BUNDLE_OPS_LOCK:
        BUNDLE_OPERATIONS[operation_id] = operation
    try:
        # Persist the declared bundle recipe up front so operators can read and
        # edit the definition even if a download fails mid-way.
        write_bundle_definition(model_dir, request)
    except Exception as exc:
        log_event(
            "sd_bundle_definition_write_failed",
            bundleId=request.bundle_id,
            error=truncate_text(str(exc), 2048),
        )

    def _run() -> None:
        try:
            from huggingface_hub import snapshot_download

            # Single-source HF token: whatever the .NET layer stamped in.
            # No env fallback here — if the repo is gated and no token is
            # configured, huggingface_hub's 401/403 is surfaced directly.
            hf_token = (request.hf_token or "").strip() or None

            # Each role is pinned to exactly one file by (repo, filename). The
            # filename is passed verbatim to huggingface_hub's allow_patterns
            # so only that file is downloaded — no whole-repo snapshots.
            roles = {
                "diffusion": (request.diffusion_repo, request.diffusion_file),
                "vae": (request.vae_repo, request.vae_file),
                "textEncoder": (request.text_encoder_repo, request.text_encoder_file),
            }
            paths = expected_bundle_paths(model_dir, request.bundle_id)
            for role, (repo, filename) in roles.items():
                with BUNDLE_OPS_LOCK:
                    BUNDLE_OPERATIONS[operation_id]["status"] = "running"
                    BUNDLE_OPERATIONS[operation_id]["roles"][role] = "downloading"
                target_path = paths[role]
                # Replacing an existing bundle definition must leave exactly one
                # file per role. Clear the role directory first so a filename
                # change cannot leave stale files behind.
                if os.path.isdir(target_path):
                    shutil.rmtree(target_path)
                elif os.path.exists(target_path):
                    os.remove(target_path)
                os.makedirs(target_path, exist_ok=True)
                snapshot_download(
                    repo_id=repo,
                    revision=request.revision,
                    local_dir=target_path,
                    local_dir_use_symlinks=False,
                    resume_download=True,
                    allow_patterns=[filename],
                    token=hf_token,
                )
                # snapshot_download with allow_patterns silently produces an
                # empty directory if the filename does not exist in the repo.
                # Turn that into a loud failure so the operator sees it.
                expected_file = os.path.join(target_path, filename)
                if not os.path.isfile(expected_file):
                    raise RuntimeError(
                        f"Expected file '{filename}' was not produced by "
                        f"snapshot_download of '{repo}' into '{target_path}'. "
                        f"Check the filename matches the repo's file listing "
                        f"exactly (including case)."
                    )
                with BUNDLE_OPS_LOCK:
                    BUNDLE_OPERATIONS[operation_id]["roles"][role] = "ready"

            with BUNDLE_OPS_LOCK:
                BUNDLE_OPERATIONS[operation_id]["status"] = "completed"
        except Exception as exc:
            with BUNDLE_OPS_LOCK:
                BUNDLE_OPERATIONS[operation_id]["status"] = "failed"
                BUNDLE_OPERATIONS[operation_id]["error"] = str(exc)

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

    if config.offload_to_cpu:
        command.append("--offload-to-cpu")
    if config.diffusion_fa:
        command.append("--diffusion-fa")

    return command


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


def start_engine() -> tuple[bool, str | None]:
    """
    Resolve the currently-active bundle, spawn sd-server, and wait until it
    answers /v1/models. Caller must hold ENGINE_LOCK.

    Returns ``(ok, error)``. On failure the subprocess (if any) is terminated,
    STATE is cleared back to "unloaded", and ``STATE.config_error`` is
    populated so the next /health / /admin/bundles read surfaces the reason.
    """
    if is_engine_process_alive():
        return True, None

    try:
        config = resolve_runtime_config()
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
    STATE.loaded_bundle_id = read_active_bundle(config.model_dir)
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
        command=command,
    )

    try:
        STATE.engine_process = subprocess.Popen(command)
        STATE.engine_started_at_utc = utc_now_iso()
        wait_for_engine_ready(config)
        STATE.loaded_at_utc = utc_now_iso()
        STATE.config_error = None
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


def run_startup_autoload_flow() -> None:
    """
    Background startup worker that performs the existing SD startup lifecycle
    (engine autoload + optional warmup) without blocking FastAPI startup.
    """
    try:
        if not env_flag("GA_SD_AUTO_LOAD_ON_STARTUP", False):
            log_event(
                "sd_service_startup_unloaded",
                reason="autoload_disabled",
                modelDir=STATE.model_dir,
            )
            return

        # Auto-start the engine if an active bundle exists, but never take the
        # control plane down on failure: operators still need /admin/bundles
        # and /admin/load to recover.
        with ENGINE_LOCK:
            ok, err = start_engine()

        if not ok:
            log_event(
                "sd_service_startup_unloaded",
                reason=err,
                modelDir=STATE.model_dir,
            )
            return

        config = STATE.config
        if config is None:
            # start_engine reported success but cleared config — should not
            # happen; log and bail out so we don't NPE below.
            log_event("sd_service_startup_unloaded", reason="config missing after start_engine ok")
            return

        log_event(
            "sd_service_startup",
            serverPath=config.server_path,
            engineHost=config.engine_host,
            enginePort=config.engine_port,
            modelDir=config.model_dir,
            diffusionModelPath=config.diffusion_model_path,
            vaePath=config.vae_path,
            llmPath=config.llm_path,
            timeoutSeconds=config.timeout_seconds,
            engineRequestTimeoutSeconds=config.engine_request_timeout_seconds,
            warmupRequestTimeoutSeconds=config.warmup_request_timeout_seconds,
            offloadToCpu=config.offload_to_cpu,
            diffusionFa=config.diffusion_fa,
            startupWarmupEnabled=STATE.startup_warmup_enabled,
            startupWarmupFailOpen=config.startup_warmup_fail_open,
            bundleId=STATE.loaded_bundle_id,
        )

        if not STATE.startup_warmup_enabled:
            return

        # Serialize startup warmup with /admin/warmup so we never run two
        # warmups against the same engine at once.
        if not WARMUP_LOCK.acquire(blocking=False):
            log_event("sd_startup_warmup_skipped", reason="warmup already in progress")
            return
        try:
            warmup_result = run_startup_warmup(config=config, request_id_prefix="sd-startup")
        finally:
            WARMUP_LOCK.release()

        if warmup_result.get("ok", False):
            return

        warmup_error = str(warmup_result.get("error") or "Startup warmup failed.")
        if not config.startup_warmup_fail_open:
            # Preserve fail-closed intent without taking the control plane
            # down: unload the engine and surface the warmup error for
            # operator recovery via /admin/load and /admin/warmup.
            with ENGINE_LOCK:
                stop_engine()
                STATE.config_error = warmup_error
            log_event(
                "sd_startup_warmup_failed_fatal",
                requestId=warmup_result.get("requestId"),
                error=warmup_error,
                action="engine-unloaded",
            )
            return

        log_event(
            "sd_startup_warmup_failed_nonfatal",
            requestId=warmup_result.get("requestId"),
            error=warmup_error,
        )
    except Exception as exc:
        log_event(
            "sd_service_startup_worker_failed",
            errorType=type(exc).__name__,
            error=truncate_text(str(exc), 2048),
        )


@APP.on_event("startup")
async def on_startup() -> None:
    # Control plane is always up. The admin bundle / engine-lifecycle
    # endpoints only need model_dir to work, and they must work even when
    # there is no bundle yet (otherwise there is no way to create the first
    # one) or when the last load failed.
    STATE.model_dir = os.getenv("GA_SD_MODEL_DIR", "/models-local/sd")
    STATE.startup_warmup_enabled = env_flag("GA_SD_AUTO_LOAD_ON_STARTUP", False)
    STATE.startup_warmup_completed_at_utc = None
    STATE.startup_warmup_last_attempt_at_utc = None
    STATE.startup_warmup_last_error = None
    STATE.startup_warmup_running = False
    STATE.engine_started_at_utc = None
    STATE.loaded_bundle_id = None

    # Keep the control-plane API reachable immediately and run startup engine
    # autoload / warmup in the background.
    threading.Thread(target=run_startup_autoload_flow, name="sd-startup", daemon=True).start()


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
            "diffusionFa": config.diffusion_fa,
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
    candidate = (bundle_id or "").strip()
    if not BUNDLE_ID_RE.fullmatch(candidate):
        raise HTTPException(status_code=400, detail="invalid bundle_id")
    return candidate


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
            "activeBundleId": read_active_bundle(model_dir),
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


@APP.post("/admin/bundles/{bundle_id}/select-active")
async def admin_select_active_bundle(bundle_id: str) -> JSONResponse:
    model_dir = _require_model_dir()
    bundle_id = require_valid_bundle_id(bundle_id)
    bundle = next((item for item in list_bundles(model_dir) if item["bundleId"] == bundle_id), None)
    if bundle is None:
        raise HTTPException(status_code=404, detail="bundle not found")
    if not bundle.get("complete"):
        missing = [name for name, role in bundle.get("roles", {}).items() if not role.get("ready")]
        raise HTTPException(status_code=409, detail=f"bundle is incomplete; missing roles: {', '.join(missing)}")

    if not ENGINE_LOCK.acquire(blocking=False):
        raise HTTPException(
            status_code=409,
            detail="engine lifecycle operation already in progress; retry in a moment",
        )
    try:
        marker = active_bundle_file(model_dir)
        with open(marker, "w", encoding="utf-8") as handle:
            json.dump({"bundleId": bundle_id, "updatedAtUtc": utc_now_iso()}, handle)

        # Marker-only path: when no engine is running, simply record the new
        # active bundle and let the next /admin/load pick it up.
        if not is_engine_process_alive():
            return JSONResponse(
                status_code=200,
                content={
                    "ok": True,
                    "action": "marker-updated",
                    "activeBundleId": bundle_id,
                    "engine": engine_state_dict(),
                },
            )

        # Hot-swap path: tear down the current engine, then start a fresh one
        # which will read the new active marker and load the new bundle's
        # files into memory. Failure leaves the engine unloaded so the UI
        # surfaces the error instead of silently running against stale
        # weights.
        log_event(
            "sd_engine_hot_swap_start",
            fromBundleId=STATE.loaded_bundle_id,
            toBundleId=bundle_id,
        )
        stop_engine()
        ok, err = start_engine()
        if ok:
            log_event(
                "sd_engine_hot_swap_complete",
                bundleId=STATE.loaded_bundle_id,
            )
            return JSONResponse(
                status_code=200,
                content={
                    "ok": True,
                    "action": "hot-swapped",
                    "activeBundleId": bundle_id,
                    "engine": engine_state_dict(),
                },
            )
        log_event(
            "sd_engine_hot_swap_failed",
            toBundleId=bundle_id,
            error=truncate_text(err or "", 2048),
        )
        return JSONResponse(
            status_code=503,
            content={
                "ok": False,
                "action": "hot-swap-failed",
                "activeBundleId": bundle_id,
                "error": "engine_start_failed",
                "engine": engine_state_dict(),
            },
        )
    finally:
        ENGINE_LOCK.release()


@APP.post("/admin/load")
async def admin_load() -> JSONResponse:
    """
    Start the sd-server subprocess using whichever bundle is marked active on
    disk. If the engine is already running, this is a no-op that returns the
    current state. Serialized with /admin/unload and /admin/bundles/.../select-
    active via ENGINE_LOCK.
    """
    if not ENGINE_LOCK.acquire(blocking=False):
        return JSONResponse(
            status_code=409,
            content={"ok": False, "error": "engine lifecycle operation already in progress"},
        )
    try:
        if is_engine_process_alive():
            return JSONResponse(
                status_code=200,
                content={
                    "ok": True,
                    "action": "noop-already-loaded",
                    "engine": engine_state_dict(),
                },
            )
        ok, err = start_engine()
        if ok:
            return JSONResponse(
                status_code=200,
                content={
                    "ok": True,
                    "action": "loaded",
                    "engine": engine_state_dict(),
                },
            )
        return JSONResponse(
            status_code=503,
            content={
                "ok": False,
                "action": "load-failed",
                "error": "engine_start_failed",
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
    bundle_id = (bundle_id or "").strip()
    if not BUNDLE_ID_RE.fullmatch(bundle_id):
        raise HTTPException(status_code=400, detail="invalid bundle_id")
    if read_active_bundle(model_dir) == bundle_id:
        raise HTTPException(status_code=409, detail="cannot remove active bundle")

    root_real = os.path.realpath(bundle_root_dir(model_dir))
    target = os.path.realpath(os.path.join(root_real, bundle_id))
    if not target.startswith(root_real + os.sep):
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
