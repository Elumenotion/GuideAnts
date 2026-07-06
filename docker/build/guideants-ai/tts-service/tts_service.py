import hashlib
import json
import logging
import os
import re
import shutil
import struct
import subprocess
import tempfile
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid
from dataclasses import dataclass
from datetime import datetime, timezone
from typing import Any

import uvicorn
from fastapi import FastAPI, HTTPException, Request
from fastapi.responses import JSONResponse, Response
from pydantic import BaseModel, Field

MODEL_PATH_RE = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")
CATALOG_PATH = os.path.join(os.path.dirname(__file__), "catalog", "manifest.json")
CHATTERBOX_MODEL_ID = "chatterbox"

# audiocpp_server VoiceTaskKind tokens (see audio.cpp
# src/framework/runtime/session.cpp parse_voice_task_kind): "tts", "clon"
# (voice cloning), "vdes" (voice design), etc. The engine model config's
# `task` selects the session route, so it must match what the family's loader
# supports — it is NOT the catalog's coarse "tts" category.
#
# Grounded mapping:
#   - chatterbox is a reference-clip cloning model -> "clon" (this is the
#     currently verified production path; kept identical).
#   - pocket_tts uses task "tts" per audio.cpp app/server/README.md.
#   - qwen3_tts VoiceDesign variant (voiceInput=instruct) is a voice-design
#     route -> "vdes".
#   - every other TTS family uses the general synthesis route -> "tts".
def resolve_engine_task(entry: dict[str, Any]) -> str:
    family = (entry.get("family") or "").strip()
    voice_input = (entry.get("voiceInput") or "").strip()
    if family == "chatterbox":
        return "clon"
    if voice_input == "instruct":
        return "vdes"
    return "tts"


# load_options / session_options are family-specific and only set where the
# correct value is known from audio.cpp (README examples / current prod). We do
# not fabricate options for families whose defaults we cannot verify — the
# engine applies its own defaults and surfaces a loud error if something is
# genuinely required.
def resolve_engine_model_options(entry: dict[str, Any]) -> dict[str, Any]:
    family = (entry.get("family") or "").strip()
    if family == "chatterbox":
        return {"language": "en"}
    if family == "pocket_tts":
        language = (entry.get("pocketTtsLanguage") or "english").strip() or "english"
        return {"language": language}
    return {}


def utc_now_iso() -> str:
    return datetime.now(timezone.utc).isoformat()


VOICE_PACK_UNAVAILABLE_MESSAGE = "Voice pack is not available."
MODEL_FILES_NOT_FOUND_MESSAGE = "Required model files were not found. Download the model first."


def model_load_client_message(exc: ValueError) -> str:
    detail = exc.args[0] if exc.args and isinstance(exc.args[0], str) else ""
    if detail == "model_path must be a simple local model name.":
        return detail
    if detail == "resolved model_path escapes the permitted model directory.":
        return detail
    if detail == "TTS catalog does not declare a default entry (default: true).":
        return detail
    if detail == "voice_pack models require GA_TTS_VOICE or a selected default voice preset.":
        return detail
    catalog_match = re.fullmatch(
        r"model_id '([^']+)' is not in the curated TTS catalog\. Only manifest entries are allowed\.",
        detail,
    )
    if catalog_match:
        return detail
    local_dir_match = re.fullmatch(
        r"Local model directory '([^']+)' is not a catalog-listed artifact\.",
        detail,
    )
    if local_dir_match and MODEL_PATH_RE.fullmatch(local_dir_match.group(1)):
        return detail
    default_voice_match = re.fullmatch(
        r"default voice preset '([^']+)' is not in the voice pack\.",
        detail,
    )
    if default_voice_match and MODEL_PATH_RE.fullmatch(default_voice_match.group(1)):
        return detail
    return "Invalid model request."


def voice_request_client_error(exc: ValueError) -> tuple[str, str]:
    detail = exc.args[0] if exc.args and isinstance(exc.args[0], str) else ""
    lang_match = re.fullmatch(r"unsupported lang_code: ([a-z])", detail)
    if lang_match:
        return "unsupported_lang_code", detail
    if re.fullmatch(
        r"lang_code '[a-z]' is unsupported: Japanese and Chinese are not available in Chatterbox",
        detail,
    ):
        return "unsupported_lang_code", "Unsupported language code."
    if detail.startswith("unknown voice preset id:"):
        voice_id = detail.split(":", 1)[1].strip()
        if voice_id and MODEL_PATH_RE.fullmatch(voice_id):
            return "invalid_voice", f"unknown voice preset id: {voice_id}"
        return "invalid_voice", "Unknown voice preset id."
    if detail.startswith("no language mapping for voice language:"):
        return "unsupported_lang_code", "Unsupported language for the selected voice."
    if detail.startswith("no lang_code mapping for voice language:"):
        return "unsupported_lang_code", "Unsupported language for the selected voice."
    if detail == "speed must be positive":
        return "invalid_voice", detail
    return "invalid_voice", "Invalid voice request."


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


def parse_non_negative_int(value: str | None, default: int) -> int:
    if value is None or not str(value).strip():
        return default
    try:
        parsed = int(value)
        if parsed >= 0:
            return parsed
    except ValueError:
        pass
    return default


def parse_positive_float(value: str | None, default: float) -> float:
    if value is None or not str(value).strip():
        return default
    try:
        parsed = float(value)
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
    entries = {
        entry["id"]: entry
        for entry in data.get("entries", [])
        if entry.get("task") == "tts"
    }
    return {"version": data.get("version", 1), "entries": entries}


CATALOG = load_catalog()


@dataclass(frozen=True)
class VoicePackEntry:
    voice_id: str
    display_name: str
    language: str
    accent: str | None
    clip_path: str
    source_clip_ref: str


class VoicePackManifest:
    def __init__(self, pack_root: str) -> None:
        self.pack_root = os.path.realpath(pack_root)
        manifest_path = os.path.join(self.pack_root, "manifest.json")
        if not os.path.isfile(manifest_path):
            raise FileNotFoundError(f"voice pack manifest not found: {manifest_path}")

        with open(manifest_path, encoding="utf-8") as handle:
            data = json.load(handle)

        reference_text = str(data.get("referenceText") or "").strip()
        if not reference_text:
            raise ValueError("voice pack manifest requires non-empty referenceText")

        voices = data.get("voices")
        if not isinstance(voices, list) or not voices:
            raise ValueError("voice pack manifest requires a non-empty voices array")

        self.reference_text = reference_text
        self.voices: dict[str, VoicePackEntry] = {}
        for item in voices:
            voice_id = str(item.get("voiceId") or "").strip()
            if not voice_id:
                raise ValueError("voice pack entry is missing voiceId")
            clip_rel = str(item.get("clipPath") or "").strip()
            if not clip_rel:
                raise ValueError(f"voice pack entry '{voice_id}' is missing clipPath")
            source_clip_ref = str(item.get("sourceClipRef") or "").strip()
            if not source_clip_ref:
                raise ValueError(f"voice pack entry '{voice_id}' is missing sourceClipRef")
            clip_path = os.path.realpath(os.path.join(self.pack_root, clip_rel))
            if not clip_path.startswith(self.pack_root + os.sep):
                raise ValueError(f"voice pack clipPath escapes pack root for voice {voice_id}")
            if not os.path.isfile(clip_path):
                raise FileNotFoundError(
                    f"voice pack clip missing for voice {voice_id}: {clip_path}"
                )
            accent = item.get("accent")
            self.voices[voice_id] = VoicePackEntry(
                voice_id=voice_id,
                display_name=str(item.get("displayName") or voice_id),
                language=str(item.get("language") or "").strip(),
                accent=str(accent).strip() if accent else None,
                clip_path=clip_path,
                source_clip_ref=source_clip_ref,
            )

        if not self.voices:
            raise ValueError("voice pack manifest contains no usable voices")

    def resolve_voice(self, voice_id: str) -> VoicePackEntry:
        entry = self.voices.get(voice_id.strip())
        if entry is None:
            raise ValueError(f"unknown voice preset id: {voice_id}")
        return entry

    def reference_text_for(self, entry: VoicePackEntry) -> str:
        quoted = re.search(r':"([^"]+)"\s*$', entry.source_clip_ref)
        if quoted and quoted.group(1).strip():
            return quoted.group(1).strip()
        return self.reference_text

    def server_voice_presets(self) -> dict[str, dict[str, str]]:
        presets: dict[str, dict[str, str]] = {}
        for voice_id, entry in self.voices.items():
            presets[voice_id] = {
                "voice_ref": entry.clip_path,
                "reference_text": self.reference_text_for(entry),
            }
        return presets


class LoadModelRequest(BaseModel):
    model_id: str | None = None
    model_path: str | None = None
    tokenizer_id: str | None = None
    tokenizer_path: str | None = None
    dtype: str | None = None
    device_map: str | None = None
    hf_token: str | None = None


class DownloadModelRequest(BaseModel):
    model_id: str
    tokenizer_id: str | None = None
    revision: str | None = None
    hf_token: str | None = None


class SynthesizeRequest(BaseModel):
    text: str = Field(min_length=1)
    voice: str | None = None
    lang_code: str | None = None
    speed: float | None = None


@dataclass
class TtsRuntimeConfig:
    server_path: str
    model_dir: str
    model_path: str
    model_ref: str
    catalog_entry_id: str
    family: str
    voice_input: str
    engine_host: str
    engine_port: int
    engine_base_url: str
    engine_ready_timeout_seconds: int
    request_timeout_seconds: int
    device: int
    threads: int
    server_config_path: str
    voice_pack_path: str
    default_voice: str
    default_lang_code: str
    default_speed: float
    sample_rate: int


class TtsRuntimeState:
    def __init__(self) -> None:
        self.lock = threading.RLock()
        self.config: TtsRuntimeConfig | None = None
        self.engine_process: subprocess.Popen[Any] | None = None
        self.engine_started_at_utc: str | None = None
        self.model_ref: str | None = None
        self.tokenizer_ref: str | None = None
        self.catalog_entry_id: str | None = None
        self.loaded_at_utc: str | None = None
        self.loading: bool = False
        self.load_error: str | None = None
        self.pipeline_task: str | None = None
        self.device: str | None = None
        self.voice: str = os.getenv("GA_TTS_VOICE", "af_alloy")
        self.lang_code: str = os.getenv("GA_TTS_LANG_CODE", "a")
        self.speed: float = parse_positive_float(os.getenv("GA_TTS_SPEED"), 1.0)
        self.sample_rate: int = parse_positive_int(os.getenv("GA_TTS_SAMPLE_RATE"), 24_000)
        self.autoload_enabled: bool = False
        self.warmup_enabled: bool = env_flag("GA_TTS_WARMUP_ON_LOAD", default=True)
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
                "tokenizerRef": self.tokenizer_ref,
                "catalogEntryId": self.catalog_entry_id,
                "loadedAtUtc": self.loaded_at_utc,
                "loadError": self.load_error,
                "pipelineTask": self.pipeline_task,
                "device": self.device,
                "maxNewTokens": 0,
                "voice": self.voice,
                "langCode": self.lang_code,
                "speed": self.speed,
                "sampleRate": self.sample_rate,
                "autoloadEnabled": self.autoload_enabled,
                "warmupEnabled": self.warmup_enabled,
                "warmupRan": self.warmup_ran,
                "warmupSucceeded": self.warmup_succeeded,
                "warmupLatencyMs": self.warmup_latency_ms,
                "warmupError": self.warmup_error,
                "warmupCompletedAtUtc": self.warmup_completed_at_utc,
                "engineAlive": is_engine_process_alive(),
            }


STATE = TtsRuntimeState()
APP = FastAPI(title="GuideAnts TTS Service", version="2.0.0")
ENGINE_LOCK = threading.Lock()
MODEL_OPS_LOCK = threading.Lock()
MODEL_DOWNLOAD_OPERATIONS: dict[str, dict[str, Any]] = {}


def get_model_dir() -> str:
    return os.getenv("GA_TTS_MODEL_DIR", "/models-local/tts")


def get_voice_pack_path() -> str:
    return os.getenv("GA_TTS_VOICE_PACK_PATH", "/opt/guideants/voice-pack")


def load_voice_pack() -> VoicePackManifest:
    return VoicePackManifest(get_voice_pack_path())


def resolve_catalog_entry(model_id: str) -> dict[str, Any]:
    entry = CATALOG["entries"].get(model_id.strip())
    if entry is None:
        raise ValueError(
            f"model_id '{model_id}' is not in the curated TTS catalog. "
            "Only manifest entries are allowed."
        )
    return entry


def catalog_default_id() -> str:
    """The catalog-declared default model id (manifest `default: true`).

    Driven by the manifest, not a hardcoded model name, so the default follows
    the curated catalog. Raises loudly if the catalog declares no default.
    """
    for entry in CATALOG["entries"].values():
        if entry.get("default") is True:
            return str(entry["id"])
    raise ValueError("TTS catalog does not declare a default entry (default: true).")


def resolve_server_path() -> str:
    configured = (os.getenv("GA_TTS_SERVER_PATH") or "").strip()
    if configured:
        return configured if os.path.isabs(configured) else (shutil.which(configured) or configured)
    default = "/usr/local/bin/audiocpp_server"
    if os.path.isfile(default):
        return default
    discovered = shutil.which("audiocpp_server")
    if discovered:
        return discovered
    raise RuntimeError("audiocpp_server binary not found; set GA_TTS_SERVER_PATH.")


def verify_required_files(model_path: str, entry: dict[str, Any]) -> None:
    missing = [
        name
        for name in entry.get("requiredFiles", [])
        if not os.path.isfile(os.path.join(model_path, name))
    ]
    if missing:
        raise FileNotFoundError(
            f"Catalog model '{entry['id']}' is missing required files under {model_path}: "
            + ", ".join(missing)
        )


def resolve_model_target(request: LoadModelRequest) -> tuple[str, str, dict[str, Any]]:
    model_dir = get_model_dir()
    os.makedirs(model_dir, exist_ok=True)

    if request.model_path:
        requested = request.model_path.strip()
        if not MODEL_PATH_RE.fullmatch(requested):
            raise ValueError("model_path must be a simple local model name.")
        base_real = os.path.realpath(model_dir)
        candidate = os.path.realpath(os.path.join(base_real, requested))
        if not candidate.startswith(base_real + os.sep):
            raise ValueError("resolved model_path escapes the permitted model directory.")
        if not os.path.isdir(candidate):
            raise FileNotFoundError(f"Model directory '{requested}' does not exist.")
        for entry in CATALOG["entries"].values():
            if os.path.basename(candidate) == entry.get("targetDirectory", entry["id"]):
                verify_required_files(candidate, entry)
                return candidate, os.path.basename(candidate), entry
        raise ValueError(f"Local model directory '{requested}' is not a catalog-listed artifact.")

    catalog_id = (
        (request.model_id or "").strip()
        or os.getenv("GA_TTS_DEFAULT_MODEL_PATH", "").strip()
        or catalog_default_id()
    )
    entry = resolve_catalog_entry(catalog_id)
    target_name = entry.get("targetDirectory", entry["id"])
    model_path = os.path.join(model_dir, target_name)
    if not os.path.isdir(model_path):
        raise FileNotFoundError(
            f"Catalog model '{catalog_id}' expects directory '{target_name}' under {model_dir}. "
            "Download it first."
        )
    verify_required_files(model_path, entry)
    return model_path, target_name, entry


def build_server_voice_preset_config(
    entry: dict[str, Any],
    default_voice: str,
) -> dict[str, Any]:
    voice_input = (entry.get("voiceInput") or "").strip()
    if voice_input not in ("voice_pack", "optional_ref"):
        return {}

    pack = load_voice_pack()
    presets = pack.server_voice_presets()
    config: dict[str, Any] = {"voice_presets": presets}
    if voice_input == "voice_pack":
        default_id = (default_voice or "").strip()
        if not default_id:
            raise ValueError("voice_pack models require GA_TTS_VOICE or a selected default voice preset.")
        if default_id not in presets:
            raise ValueError(f"default voice preset '{default_id}' is not in the voice pack.")
        config["default_voice_preset"] = default_id
    return config


def build_server_config_json(config: TtsRuntimeConfig) -> dict[str, Any]:
    entry = resolve_catalog_entry(config.catalog_entry_id)
    task = resolve_engine_task(entry)
    options = resolve_engine_model_options(entry)
    model_config: dict[str, Any] = {
        # The engine model id mirrors the catalog entry id (used as the "model"
        # field in synthesize requests). Family drives which loader/session the
        # engine uses — this is the fix for the old Chatterbox-only funnel.
        "id": config.catalog_entry_id,
        "family": config.family,
        "path": config.model_path,
        "task": task,
        "mode": "offline",
    }
    if options:
        model_config["load_options"] = dict(options)
        model_config["session_options"] = dict(options)
    model_config.update(build_server_voice_preset_config(entry, config.default_voice))
    return {
        "host": config.engine_host,
        "port": config.engine_port,
        # audiocpp_server >= "Allow server backend selection" (2026-07-02) reads this
        # field to pick engine::core::BackendType; defaults to "cuda" to match the
        # binary's own default. Must match the ENGINE_ENABLE_* flags baked into this
        # image's audiocpp_server build (see Dockerfile.<flavor>), otherwise every
        # model load fails at runtime with a backend-mismatch error. GA_TTS_BACKEND was
        # already read into STATE for status reporting (see below) but was never
        # actually plumbed into this config — that was the missing wire-up.
        "backend": (os.getenv("GA_TTS_BACKEND") or "cuda").strip() or "cuda",
        "device": config.device,
        "threads": config.threads,
        "lazy_load": False,
        "models": [model_config],
    }


def write_server_config(config: TtsRuntimeConfig) -> str:
    config_dir = os.path.join(config.model_dir, ".guideants")
    os.makedirs(config_dir, exist_ok=True)
    config_path = os.path.join(config_dir, "audiocpp-server.json")
    with open(config_path, "w", encoding="utf-8") as handle:
        json.dump(build_server_config_json(config), handle, ensure_ascii=True, indent=2)
        handle.write("\n")
    return config_path


def build_runtime_config(model_path: str, model_ref: str, entry: dict[str, Any]) -> TtsRuntimeConfig:
    engine_host = os.getenv("GA_TTS_ENGINE_HOST", "127.0.0.1")
    engine_port = parse_positive_int(os.getenv("GA_TTS_ENGINE_PORT"), 18084)
    device = parse_non_negative_int(os.getenv("GA_TTS_DEVICE"), 0)
    threads = parse_positive_int(
        os.getenv("GA_TTS_THREADS"),
        max(1, os.cpu_count() or 1),
    )
    partial = TtsRuntimeConfig(
        server_path=resolve_server_path(),
        model_dir=get_model_dir(),
        model_path=model_path,
        model_ref=model_ref,
        catalog_entry_id=entry["id"],
        family=(entry.get("family") or "").strip(),
        voice_input=(entry.get("voiceInput") or "").strip(),
        engine_host=engine_host,
        engine_port=engine_port,
        engine_base_url=f"http://{engine_host}:{engine_port}",
        engine_ready_timeout_seconds=parse_positive_int(
            os.getenv("GA_TTS_ENGINE_READY_TIMEOUT_SECONDS"),
            parse_positive_int(os.getenv("GA_TTS_READY_TIMEOUT_SECONDS"), 1800),
        ),
        request_timeout_seconds=parse_positive_int(os.getenv("GA_TTS_TIMEOUT_SECONDS"), 300),
        device=device,
        threads=threads,
        server_config_path="",
        voice_pack_path=get_voice_pack_path(),
        default_voice=(os.getenv("GA_TTS_VOICE") or "af_alloy").strip() or "af_alloy",
        default_lang_code=(os.getenv("GA_TTS_LANG_CODE") or "a").strip() or "a",
        default_speed=parse_positive_float(os.getenv("GA_TTS_SPEED"), 1.0),
        sample_rate=parse_positive_int(os.getenv("GA_TTS_SAMPLE_RATE"), 24_000),
    )
    return TtsRuntimeConfig(
        server_path=partial.server_path,
        model_dir=partial.model_dir,
        model_path=partial.model_path,
        model_ref=partial.model_ref,
        catalog_entry_id=partial.catalog_entry_id,
        family=partial.family,
        voice_input=partial.voice_input,
        engine_host=partial.engine_host,
        engine_port=partial.engine_port,
        engine_base_url=partial.engine_base_url,
        engine_ready_timeout_seconds=partial.engine_ready_timeout_seconds,
        request_timeout_seconds=partial.request_timeout_seconds,
        device=partial.device,
        threads=partial.threads,
        server_config_path=write_server_config(partial),
        voice_pack_path=partial.voice_pack_path,
        default_voice=partial.default_voice,
        default_lang_code=partial.default_lang_code,
        default_speed=partial.default_speed,
        sample_rate=partial.sample_rate,
    )


def build_audiocpp_server_command(config: TtsRuntimeConfig) -> list[str]:
    return [
        config.server_path,
        "--config",
        config.server_config_path,
        "--host",
        config.engine_host,
        "--port",
        str(config.engine_port),
        "--device",
        str(config.device),
        "--threads",
        str(config.threads),
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
) -> tuple[int, bytes, dict[str, str]]:
    request = urllib.request.Request(url=url, data=body, method=method)
    for name, value in (headers or {}).items():
        request.add_header(name, value)
    try:
        with urllib.request.urlopen(request, timeout=timeout_seconds) as response:
            response_headers = {key.lower(): value for key, value in response.headers.items()}
            return int(response.status), response.read(), response_headers
    except urllib.error.HTTPError as exc:
        response_headers = {key.lower(): value for key, value in exc.headers.items()}
        return int(exc.code), exc.read(), response_headers
    except urllib.error.URLError as exc:
        reason = getattr(exc, "reason", exc)
        raise RuntimeError(f"Failed to reach audiocpp_server at {url}: {reason}") from exc


def engine_json_request(
    config: TtsRuntimeConfig,
    method: str,
    path: str,
    timeout_seconds: int,
    payload: dict[str, Any] | None = None,
) -> tuple[int, dict[str, Any] | bytes, dict[str, str]]:
    headers: dict[str, str] = {}
    body: bytes | None = None
    if payload is not None:
        headers["Content-Type"] = "application/json"
        headers["Accept"] = "application/json"
        body = json.dumps(payload, ensure_ascii=True, separators=(",", ":")).encode("utf-8")
    status_code, response_body, response_headers = perform_http_request(
        method=method,
        url=f"{config.engine_base_url}{path}",
        timeout_seconds=timeout_seconds,
        headers=headers,
        body=body,
    )
    if path == "/v1/audio/speech":
        return status_code, response_body, response_headers
    if not response_body:
        return status_code, {}, response_headers
    parsed = json.loads(response_body.decode("utf-8"))
    if not isinstance(parsed, dict):
        raise RuntimeError(f"Unexpected JSON from audiocpp_server {method} {path}")
    return status_code, parsed, response_headers


def wait_for_engine_ready(config: TtsRuntimeConfig) -> None:
    deadline = time.monotonic() + config.engine_ready_timeout_seconds
    while time.monotonic() < deadline:
        if not is_engine_process_alive():
            process = STATE.engine_process
            exit_code = process.poll() if process is not None else None
            raise RuntimeError(f"audiocpp_server exited before readiness (exit code: {exit_code}).")
        try:
            status_code, _, _ = engine_json_request(
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
    STATE.tokenizer_ref = None
    STATE.catalog_entry_id = None
    STATE.loaded_at_utc = None
    STATE.pipeline_task = None
    STATE.device = None
    STATE.warmup_ran = False
    STATE.warmup_succeeded = False
    STATE.warmup_latency_ms = 0
    STATE.warmup_error = None
    STATE.warmup_completed_at_utc = None


def normalize_script_input(text: str) -> str:
    stripped = text.strip()
    if not stripped:
        return ""
    return re.sub(r"\s+", " ", stripped)


def resolve_speed(value: float | str | None, fallback: float) -> float:
    if value is None:
        candidate = fallback
    else:
        try:
            candidate = float(value)
        except (TypeError, ValueError):
            candidate = fallback
    return min(4.0, max(0.25, candidate))


def map_lang_code_to_language(lang_code: str) -> str:
    normalized = (lang_code or "").strip().lower()
    if normalized in {"j", "z"}:
        raise ValueError(
            f"lang_code '{normalized}' is unsupported: Japanese and Chinese are not available in Chatterbox"
        )
    if normalized in {"a", "b"}:
        return "en"
    if normalized == "e":
        return "es"
    if normalized == "f":
        return "fr"
    if normalized == "h":
        return "hi"
    if normalized == "i":
        return "it"
    if normalized == "p":
        return "pt"
    raise ValueError(f"unsupported lang_code: {normalized}")


def language_from_voice_entry(entry: VoicePackEntry) -> str:
    if entry.accent:
        accent = entry.accent.strip()
        if accent == "en-GB":
            return "en"
        if accent == "en-US" or accent.lower().startswith("en"):
            return "en"
    language = entry.language.strip().lower()
    if language in {"en", "es", "fr", "hi", "it", "pt"}:
        return language
    raise ValueError(f"no language mapping for voice language: {entry.language}")


def resolve_lang_code_for_voice(entry: VoicePackEntry) -> str:
    if entry.accent:
        accent = entry.accent.strip()
        if accent == "en-GB":
            return "b"
        if accent == "en-US" or accent.lower().startswith("en"):
            return "a"
    language = entry.language.strip().lower()
    mapping = {
        "en": "a",
        "es": "e",
        "fr": "f",
        "hi": "h",
        "it": "i",
        "pt": "p",
    }
    if language in mapping:
        return mapping[language]
    raise ValueError(f"no lang_code mapping for voice language: {entry.language}")


def resolve_language(lang_code: str | None, voice_entry: VoicePackEntry) -> tuple[str, str]:
    if lang_code and lang_code.strip():
        normalized_lang_code = lang_code.strip().lower()
        return normalized_lang_code, map_lang_code_to_language(normalized_lang_code)
    derived_lang_code = resolve_lang_code_for_voice(voice_entry)
    return derived_lang_code, language_from_voice_entry(voice_entry)


def seed_from_text_voice(text: str, voice: str) -> int:
    digest = hashlib.sha256((text + "\0" + voice).encode("utf-8")).digest()
    return int.from_bytes(digest[:4], byteorder="little", signed=False)


def wav_duration_seconds(wav_bytes: bytes) -> tuple[float, int]:
    if len(wav_bytes) < 44 or wav_bytes[0:4] != b"RIFF" or wav_bytes[8:12] != b"WAVE":
        raise RuntimeError("audiocpp_server returned invalid WAV data")
    channels = struct.unpack_from("<H", wav_bytes, 22)[0]
    sample_rate = struct.unpack_from("<I", wav_bytes, 24)[0]
    bits_per_sample = struct.unpack_from("<H", wav_bytes, 34)[0]
    data_size = struct.unpack_from("<I", wav_bytes, 40)[0]
    if sample_rate <= 0 or channels <= 0 or bits_per_sample <= 0:
        raise RuntimeError("audiocpp_server returned WAV with invalid audio parameters")
    bytes_per_frame = channels * (bits_per_sample // 8)
    if bytes_per_frame <= 0:
        raise RuntimeError("audiocpp_server returned WAV with invalid frame size")
    duration = data_size / float(sample_rate * bytes_per_frame)
    return duration, sample_rate


def build_atempo_filter(speed: float) -> str:
    if speed <= 0:
        raise ValueError("speed must be positive")
    factors: list[float] = []
    remaining = speed
    while remaining > 2.0:
        factors.append(2.0)
        remaining /= 2.0
    while remaining < 0.5:
        factors.append(0.5)
        remaining /= 0.5
    factors.append(remaining)
    return ",".join(f"atempo={factor}" for factor in factors)


def apply_speed_with_ffmpeg(wav_bytes: bytes, speed: float) -> bytes:
    if abs(speed - 1.0) < 1e-6:
        return wav_bytes
    filter_expr = build_atempo_filter(speed)
    with tempfile.NamedTemporaryFile(suffix=".wav", delete=False) as input_file:
        input_path = input_file.name
        input_file.write(wav_bytes)
    output_path = f"{input_path}.out.wav"
    try:
        command = [
            "ffmpeg",
            "-hide_banner",
            "-loglevel",
            "error",
            "-y",
            "-i",
            input_path,
            "-filter:a",
            filter_expr,
            output_path,
        ]
        result = subprocess.run(command, capture_output=True, text=True, check=False)
        if result.returncode != 0 or not os.path.isfile(output_path):
            detail = (result.stderr or result.stdout or "").strip()
            raise RuntimeError(f"ffmpeg atempo speed processing failed: {detail}")
        with open(output_path, "rb") as handle:
            adjusted = handle.read()
        if not adjusted:
            raise RuntimeError("ffmpeg atempo produced empty wav output")
        return adjusted
    finally:
        for path in (input_path, output_path):
            try:
                os.remove(path)
            except OSError:
                pass


def resolve_voice_fields(
    config: TtsRuntimeConfig,
    requested_voice: str | None,
    requested_lang_code: str | None,
) -> tuple[dict[str, Any], str | None, str | None]:
    """Build the family-appropriate voice fields for /v1/audio/speech.

    Grounded in audio.cpp app/server/runtime.cpp build_openai_speech_request:
      - `voice`        -> configured server preset name OR model-native cached_voice_id
      - `voice_ref`    -> reference audio clip path (only when server presets are not used)
      - `reference_text` -> transcript for reference audio (injected by server presets)

    voice_pack / optional_ref models register server voice_presets at load time
    (voice_ref + reference_text per preset). Synthesis passes the preset id in
    `voice` and lets audiocpp_server resolve clip + transcript.

    Returns (payload voice fields, resolved voice label, resolved lang_code).
    """
    voice_input = (config.voice_input or "").strip()
    requested = (requested_voice or "").strip()

    if voice_input == "voice_pack":
        pack = load_voice_pack()
        entry = pack.resolve_voice(requested or config.default_voice)
        lang_code, language = resolve_language(requested_lang_code, entry)
        return {"voice": entry.voice_id, "language": language}, entry.voice_id, lang_code

    if voice_input == "optional_ref":
        fields: dict[str, Any] = {}
        if requested:
            pack = load_voice_pack()
            entry = pack.resolve_voice(requested)
            fields["voice"] = entry.voice_id
            label = entry.voice_id
        else:
            label = None
        if requested_lang_code and requested_lang_code.strip():
            fields["language"] = map_lang_code_to_language(requested_lang_code.strip().lower())
        return fields, label, (requested_lang_code or None)

    if voice_input == "builtin":
        # A speaker id in VoiceName selects the builtin voice; when absent the
        # engine uses the model's default speaker (see qwen3_tts/pocket_tts
        # sessions which read cached_voice_id and fall back to their default).
        fields = {}
        if requested:
            fields["voice"] = requested
        if requested_lang_code and requested_lang_code.strip():
            fields["language"] = map_lang_code_to_language(requested_lang_code.strip().lower())
        return fields, (requested or None), (requested_lang_code or None)

    if voice_input == "instruct":
        # Voice-design text drives generation (options["instruct"]).
        fields = {}
        if requested:
            fields["instructions"] = requested
        if requested_lang_code and requested_lang_code.strip():
            fields["language"] = map_lang_code_to_language(requested_lang_code.strip().lower())
        return fields, (requested or None), (requested_lang_code or None)

    if voice_input in ("none", ""):
        return {}, None, (requested_lang_code or None)

    raise ValueError(
        f"model '{config.catalog_entry_id}' has unknown voiceInput '{voice_input}'."
    )


def synthesize_via_engine(
    config: TtsRuntimeConfig,
    text: str,
    voice_fields: dict[str, Any],
    seed: int,
) -> bytes:
    payload = {
        "model": config.catalog_entry_id,
        "input": text,
        "seed": seed,
        **voice_fields,
    }
    status_code, body, _ = engine_json_request(
        config,
        "POST",
        "/v1/audio/speech",
        config.request_timeout_seconds,
        payload=payload,
    )
    if status_code != 200:
        detail = body.decode("utf-8", errors="replace") if isinstance(body, (bytes, bytearray)) else str(body)
        raise RuntimeError(f"audiocpp_server /v1/audio/speech returned HTTP {status_code}: {detail}")
    if not isinstance(body, (bytes, bytearray)) or not body:
        raise RuntimeError("audiocpp_server returned empty WAV payload")
    return bytes(body)


def run_model_warmup(config: TtsRuntimeConfig, force: bool = False) -> dict[str, Any]:
    warmup_enabled = env_flag("GA_TTS_WARMUP_ON_LOAD", default=True)
    if not warmup_enabled and not force:
        return {
            "warmupEnabled": False,
            "warmupRan": False,
            "warmupSucceeded": False,
            "warmupLatencyMs": 0,
        }

    warmup_text = (os.getenv("GA_TTS_WARMUP_TEXT") or "Hello.").strip() or "Hello."
    # Family-aware warmup: only voice_pack models resolve a pack clip. Other
    # families warm up with the engine's default speaker (no fabricated voice).
    warmup_voice = config.default_voice if config.voice_input == "voice_pack" else None
    voice_fields, resolved_voice, _ = resolve_voice_fields(
        config, warmup_voice, config.default_lang_code
    )
    seed = seed_from_text_voice(warmup_text, resolved_voice or config.catalog_entry_id)

    started = time.perf_counter()
    log_event("tts_model_warmup_start", modelRef=config.model_ref, voice=resolved_voice)
    try:
        wav_bytes = synthesize_via_engine(config, warmup_text, voice_fields, seed)
        duration_seconds, _ = wav_duration_seconds(wav_bytes)
        if duration_seconds <= 0:
            raise RuntimeError("Warmup produced zero-duration audio.")
        latency_ms = int((time.perf_counter() - started) * 1000)
        return {
            "warmupEnabled": True,
            "warmupRan": True,
            "warmupSucceeded": True,
            "warmupLatencyMs": latency_ms,
            "warmupCompletedAtUtc": utc_now_iso(),
        }
    except Exception as exc:
        latency_ms = int((time.perf_counter() - started) * 1000)
        log_event(
            "tts_model_warmup_failed",
            modelRef=config.model_ref,
            errorType=type(exc).__name__,
            error=str(exc),
        )
        return {
            "warmupEnabled": True,
            "warmupRan": True,
            "warmupSucceeded": False,
            "warmupLatencyMs": latency_ms,
            "warmupError": str(exc),
        }


def start_engine(request: LoadModelRequest) -> dict[str, Any]:
    model_path, model_ref, entry = resolve_model_target(request)
    config = build_runtime_config(model_path, model_ref, entry)

    if STATE.config and STATE.config.model_path == model_path and is_engine_process_alive():
        return {
            "modelRef": STATE.model_ref,
            "catalogEntryId": STATE.catalog_entry_id,
            "action": "noop-already-loaded",
        }

    stop_engine()
    command = build_audiocpp_server_command(config)
    log_event("tts_engine_start", command=command, modelRef=model_ref)
    STATE.engine_process = subprocess.Popen(
        command,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.PIPE,
        env=os.environ.copy(),
    )
    STATE.engine_started_at_utc = utc_now_iso()
    wait_for_engine_ready(config)

    STATE.config = config
    STATE.model_ref = model_ref
    STATE.tokenizer_ref = None
    STATE.catalog_entry_id = entry["id"]
    STATE.pipeline_task = f"{config.family}-{resolve_engine_task(entry)}"
    STATE.device = os.getenv("GA_TTS_BACKEND", "cuda")
    STATE.loaded_at_utc = utc_now_iso()
    STATE.voice = config.default_voice
    STATE.lang_code = config.default_lang_code
    STATE.speed = config.default_speed
    STATE.sample_rate = config.sample_rate
    return {"modelRef": model_ref, "catalogEntryId": entry["id"]}


def load_model_serialized(request: LoadModelRequest, force_warmup: bool = False) -> dict[str, Any]:
    if not ENGINE_LOCK.acquire(blocking=False):
        raise RuntimeError("model lifecycle operation already in progress")
    started = time.perf_counter()
    try:
        with STATE.lock:
            STATE.loading = True
            STATE.load_error = None
        try:
            hf_token = (request.hf_token or "").strip()
            if hf_token:
                os.environ["HF_TOKEN"] = hf_token
            details = start_engine(request)
            config = STATE.config
            if config is None:
                raise RuntimeError("TTS engine config missing after load.")
            warmup = run_model_warmup(config, force=force_warmup)
            if force_warmup and not warmup.get("warmupSucceeded", False):
                stop_engine()
                raise RuntimeError(str(warmup.get("warmupError") or "TTS warmup failed."))
            with STATE.lock:
                STATE.warmup_ran = bool(warmup.get("warmupRan"))
                STATE.warmup_succeeded = bool(warmup.get("warmupSucceeded"))
                STATE.warmup_latency_ms = int(warmup.get("warmupLatencyMs") or 0)
                STATE.warmup_error = warmup.get("warmupError")
                STATE.warmup_completed_at_utc = warmup.get("warmupCompletedAtUtc")
            load_latency_ms = int((time.perf_counter() - started) * 1000)
            return {
                **details,
                **warmup,
                "tokenizerRef": None,
                "loadedAtUtc": STATE.loaded_at_utc,
                "loadLatencyMs": load_latency_ms,
                "pipelineTask": STATE.pipeline_task,
                "device": STATE.device,
                "sampleRate": STATE.sample_rate,
                "voice": STATE.voice,
                "langCode": STATE.lang_code,
                "speed": STATE.speed,
            }
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
    with STATE.lock:
        STATE.voice = (os.getenv("GA_TTS_VOICE") or "af_alloy").strip() or "af_alloy"
        STATE.lang_code = (os.getenv("GA_TTS_LANG_CODE") or "a").strip() or "a"
        STATE.speed = parse_positive_float(os.getenv("GA_TTS_SPEED"), 1.0)
    return {"wasLoaded": had_model, "previousModelRef": previous_ref}


def list_model_entries() -> list[dict[str, Any]]:
    model_dir = get_model_dir()
    os.makedirs(model_dir, exist_ok=True)
    snapshot = STATE.snapshot()
    active_model = snapshot.get("modelRef")
    items: list[dict[str, Any]] = []
    for name in sorted(os.listdir(model_dir)):
        if name.startswith("."):
            continue
        full_path = os.path.join(model_dir, name)
        items.append(
            {
                "modelRef": name,
                "path": full_path,
                "isDirectory": os.path.isdir(full_path),
                "activeModel": bool(active_model and (active_model == name or active_model == full_path)),
                "activeTokenizer": False,
            }
        )
    return items


def _status_is_terminal(status: str | None) -> bool:
    normalized = (status or "").strip().lower()
    return normalized in {"completed", "failed", "error", "cancelled", "canceled"}


def start_download_operation(request: DownloadModelRequest) -> dict[str, Any]:
    entry = resolve_catalog_entry(request.model_id)
    source = entry["sourceRepos"][0]
    repo_id = source["repoId"] if isinstance(source, dict) else source
    revision = request.revision or (source.get("revision") if isinstance(source, dict) else None) or "main"
    target_name = entry.get("targetDirectory", entry["id"])

    operation_id = uuid.uuid4().hex
    operation = {
        "operationId": operation_id,
        "status": "queued",
        "modelId": request.model_id,
        "tokenizerId": request.tokenizer_id,
        "error": None,
        "modelRef": target_name,
        "tokenizerRef": None,
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
            from huggingface_hub import snapshot_download

            hf_token = (request.hf_token or "").strip() or None
            snapshot_download(
                repo_id=repo_id,
                revision=revision,
                local_dir=target_path,
                local_dir_use_symlinks=False,
                resume_download=True,
                token=hf_token,
            )
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
            verify_required_files(target_path, entry)
            with MODEL_OPS_LOCK:
                current = MODEL_DOWNLOAD_OPERATIONS.get(operation_id)
                if current is None:
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
        "host": os.getenv("GA_TTS_HOST", "127.0.0.1"),
        "port": os.getenv("GA_TTS_PORT", "8084"),
        "modelDir": get_model_dir(),
        "voicePackPath": get_voice_pack_path(),
        "engineHost": os.getenv("GA_TTS_ENGINE_HOST", "127.0.0.1"),
        "enginePort": os.getenv("GA_TTS_ENGINE_PORT", "18084"),
        "defaultModelPath": os.getenv("GA_TTS_DEFAULT_MODEL_PATH"),
        "catalogVersion": CATALOG.get("version"),
    }
    log_event("tts_service_startup", **startup_details)


@APP.on_event("shutdown")
async def on_shutdown() -> None:
    with ENGINE_LOCK:
        stop_engine()


@APP.get("/health")
async def health() -> dict[str, Any]:
    return {
        "status": "ok",
        "backend": os.getenv("GA_TTS_BACKEND", "cuda"),
        **STATE.snapshot(),
    }


@APP.get("/ready")
async def ready() -> JSONResponse:
    snapshot = STATE.snapshot()
    if not snapshot["loaded"]:
        return JSONResponse(status_code=503, content={"ready": False, **snapshot})
    if snapshot["warmupEnabled"] and not snapshot.get("warmupSucceeded"):
        return JSONResponse(
            status_code=503,
            content={
                "ready": False,
                "message": "TTS model loaded but warmup is incomplete.",
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
            "targetDirectory": entry.get("targetDirectory"),
            "releaseStatus": entry.get("releaseStatus"),
            "family": entry.get("family"),
            "voiceInput": entry.get("voiceInput"),
            "gated": entry.get("gated", False),
        }
        for entry in CATALOG["entries"].values()
    ]
    return JSONResponse(status_code=200, content={"version": CATALOG["version"], "entries": entries})


@APP.get("/admin/voice-pack")
async def admin_voice_pack() -> JSONResponse:
    """Baked voice-pack presets for catalog entries whose voiceInput is voice_pack.

    This is not a Hugging Face download; the pack ships in the image. The
    settings UI reads this to populate the voice picker for voice_pack models
    (e.g. chatterbox) instead of relying on any hardcoded .NET enum.
    """
    try:
        voice_pack = load_voice_pack()
    except (FileNotFoundError, ValueError) as exc:
        log_event("voice_pack_unavailable", errorType=type(exc).__name__, error=str(exc))
        return JSONResponse(
            status_code=503,
            content={"error": "voice_pack_unavailable", "message": VOICE_PACK_UNAVAILABLE_MESSAGE},
        )

    voices = [
        {
            "voiceId": entry.voice_id,
            "displayName": entry.display_name,
            "language": entry.language,
            "accent": entry.accent,
        }
        for entry in voice_pack.voices.values()
    ]
    voices.sort(key=lambda item: item["voiceId"])
    return JSONResponse(
        status_code=200,
        content={
            "path": get_voice_pack_path(),
            "count": len(voices),
            "referenceText": voice_pack.reference_text,
            "voices": voices,
        },
    )


@APP.get("/admin/voices")
async def admin_voices() -> JSONResponse:
    """Runtime speaker ids and server voice preset names for the loaded TTS model.

    Proxies audiocpp_server GET /v1/audio/voices?model=<catalog_entry_id>.
    """
    snapshot = STATE.snapshot()
    if not snapshot["loaded"]:
        return JSONResponse(
            status_code=503,
            content={
                "error": "model_not_loaded",
                "message": "Load a model with /admin/load before listing voices.",
            },
        )

    config = STATE.config
    if config is None:
        return JSONResponse(
            status_code=503,
            content={"error": "model_not_loaded", "message": "TTS engine is not loaded."},
        )

    status_code, parsed, _ = engine_json_request(
        config,
        "GET",
        f"/v1/audio/voices?model={urllib.parse.quote(config.catalog_entry_id)}",
        min(10, config.request_timeout_seconds),
    )
    if status_code != 200:
        return JSONResponse(
            status_code=502,
            content={
                "error": "engine_voices_failed",
                "message": f"audiocpp_server /v1/audio/voices returned HTTP {status_code}.",
            },
        )
    if not isinstance(parsed, dict):
        return JSONResponse(
            status_code=502,
            content={"error": "engine_voices_failed", "message": "Unexpected voices payload."},
        )

    voices = parsed.get("voices")
    if not isinstance(voices, list):
        voices = []
    voice_ids = [str(item) for item in voices if str(item).strip()]
    return JSONResponse(
        status_code=200,
        content={
            "modelId": config.catalog_entry_id,
            "voices": voice_ids,
        },
    )


@APP.post("/admin/load")
async def admin_load(request: Request, payload: LoadModelRequest) -> JSONResponse:
    request_id = request.headers.get("x-request-id", str(uuid.uuid4()))
    log_event("tts_model_load_start", requestId=request_id, payload=payload.model_dump())
    try:
        details = load_model_serialized(payload, force_warmup=env_flag("GA_TTS_WARMUP_ON_LOAD", default=True))
        log_event("tts_model_load_success", requestId=request_id, **details)
        return JSONResponse(
            status_code=200,
            content={"requestId": request_id, "status": "loaded", **details},
        )
    except ValueError as exc:
        log_event(
            "tts_model_load_validation_failed",
            requestId=request_id,
            errorType=type(exc).__name__,
            error=str(exc),
        )
        return JSONResponse(
            status_code=400,
            content={
                "requestId": request_id,
                "status": "failed",
                "error": "invalid_model_request",
                "message": model_load_client_message(exc),
            },
        )
    except FileNotFoundError as exc:
        log_event(
            "tts_model_load_not_found",
            requestId=request_id,
            errorType=type(exc).__name__,
            error=str(exc),
        )
        return JSONResponse(
            status_code=400,
            content={
                "requestId": request_id,
                "status": "failed",
                "error": "invalid_model_request",
                "message": MODEL_FILES_NOT_FOUND_MESSAGE,
            },
        )
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
            log_event("tts_model_unload_noop", requestId=request_id)
            return JSONResponse(
                status_code=200,
                content={
                    "requestId": request_id,
                    "ok": True,
                    "action": "noop-already-unloaded",
                    **snapshot,
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
    if not MODEL_PATH_RE.fullmatch(model_ref):
        raise HTTPException(status_code=400, detail="invalid model_ref")

    snapshot = STATE.snapshot()
    active_model = snapshot.get("modelRef")
    if active_model and (active_model == model_ref or str(active_model).endswith(model_ref)):
        raise HTTPException(status_code=409, detail="cannot delete active model")

    model_dir = os.path.realpath(get_model_dir())
    target = os.path.realpath(os.path.join(model_dir, model_ref))
    if not target.startswith(model_dir + os.sep):
        raise HTTPException(status_code=400, detail="invalid model_ref")
    if not os.path.exists(target):
        raise HTTPException(status_code=404, detail="model not found")

    if os.path.isdir(target):
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
            content={
                "requestId": request_id,
                "error": "invalid_text",
                "message": "Text cannot be empty after normalization.",
            },
        )

    snapshot = STATE.snapshot()
    if not snapshot["loaded"]:
        return JSONResponse(
            status_code=503,
            content={
                "requestId": request_id,
                "error": "model_not_loaded",
                "message": "Load a model with /admin/load before synthesizing.",
            },
        )
    if snapshot["warmupEnabled"] and not snapshot.get("warmupSucceeded"):
        return JSONResponse(
            status_code=503,
            content={
                "requestId": request_id,
                "error": "model_not_ready",
                "message": "TTS warmup is incomplete. Retry after /ready is healthy.",
                "warmupError": snapshot.get("warmupError"),
            },
        )

    config = STATE.config
    if config is None:
        return JSONResponse(
            status_code=503,
            content={
                "requestId": request_id,
                "error": "model_not_loaded",
                "message": "Load a model with /admin/load before synthesizing.",
            },
        )

    started = time.perf_counter()

    # Family-aware voice resolution. Only voice_pack / optional_ref models
    # consult the baked voice pack; builtin/instruct/none families interpret
    # VoiceName as a speaker id / design text / nothing (see resolve_voice_fields).
    requested_voice = (payload.voice or "").strip()
    if not requested_voice and config.voice_input == "voice_pack":
        requested_voice = (STATE.voice or config.default_voice).strip()

    try:
        voice_fields, resolved_voice, lang_code = resolve_voice_fields(
            config, requested_voice or None, payload.lang_code
        )
    except FileNotFoundError as exc:
        log_event(
            "tts_voice_pack_unavailable",
            requestId=request_id,
            errorType=type(exc).__name__,
            error=str(exc),
        )
        return JSONResponse(
            status_code=500,
            content={"requestId": request_id, "error": "voice_pack_unavailable", "message": VOICE_PACK_UNAVAILABLE_MESSAGE},
        )
    except ValueError as exc:
        error_code, message = voice_request_client_error(exc)
        return JSONResponse(
            status_code=400,
            content={"requestId": request_id, "error": error_code, "message": message},
        )

    speed = resolve_speed(payload.speed if payload.speed is not None else STATE.speed, STATE.speed)
    seed = seed_from_text_voice(script_text, resolved_voice or config.catalog_entry_id)

    with STATE.lock:
        if resolved_voice:
            STATE.voice = resolved_voice
        if lang_code:
            STATE.lang_code = lang_code
        STATE.speed = speed

    log_event(
        "tts_synthesize_start",
        requestId=request_id,
        traceparent=traceparent,
        textLength=len(script_text),
        modelRef=snapshot.get("modelRef"),
        voice=resolved_voice,
        langCode=lang_code,
        speed=speed,
    )

    try:
        wav_bytes = synthesize_via_engine(config, script_text, voice_fields, seed)
        duration_seconds, sampling_rate = wav_duration_seconds(wav_bytes)
        if abs(speed - 1.0) >= 1e-6:
            wav_bytes = apply_speed_with_ffmpeg(wav_bytes, speed)
            duration_seconds /= speed

        latency_ms = int((time.perf_counter() - started) * 1000)
        response = Response(content=wav_bytes, media_type="audio/wav")
        response.headers["x-request-id"] = request_id
        response.headers["x-model-ref"] = snapshot.get("modelRef") or ""
        response.headers["x-audio-duration-seconds"] = f"{duration_seconds:.3f}"
        response.headers["x-sampling-rate"] = str(sampling_rate)

        log_event(
            "tts_synthesize_success",
            requestId=request_id,
            traceparent=traceparent,
            latencyMs=latency_ms,
            textLength=len(script_text),
            modelRef=snapshot.get("modelRef"),
            voice=resolved_voice,
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
            modelRef=snapshot.get("modelRef"),
            errorType=type(exc).__name__,
            error=str(exc),
        )
        return JSONResponse(
            status_code=500,
            content={
                "requestId": request_id,
                "error": "synthesis_failed",
                "message": "Speech synthesis failed. Check service logs for details.",
                "modelRef": snapshot.get("modelRef"),
            },
        )


def main() -> None:
    host = os.getenv("GA_TTS_HOST", "127.0.0.1")
    port = parse_positive_int(os.getenv("GA_TTS_PORT"), 8084)
    log_level = (os.getenv("GA_TTS_LOG_LEVEL") or "info").lower()
    access_log = env_flag("GA_TTS_UVICORN_ACCESS_LOG", default=False)
    configure_uvicorn_access_log_filters(env_flag("GA_TTS_SUPPRESS_HEALTH_ACCESS_LOGS", default=True))
    uvicorn.run(APP, host=host, port=port, log_level=log_level, access_log=access_log)


if __name__ == "__main__":
    main()
