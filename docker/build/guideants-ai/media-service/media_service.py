import json
import os
import subprocess
import time
from datetime import datetime, timezone
from pathlib import Path, PurePosixPath
from typing import Any

try:
    import uvicorn
except ImportError:  # pragma: no cover - local unit tests may not have runtime deps installed
    uvicorn = None

try:
    from fastapi import FastAPI, HTTPException, Request
    from pydantic import BaseModel
except ImportError:  # pragma: no cover - local unit tests exercise helper functions without FastAPI installed
    class BaseModel:
        def __init__(self, **kwargs: Any) -> None:
            for key, value in kwargs.items():
                setattr(self, key, value)

        def model_dump(self) -> dict[str, Any]:
            return dict(self.__dict__)

    class HTTPException(Exception):
        def __init__(self, status_code: int, detail: str) -> None:
            super().__init__(detail)
            self.status_code = status_code
            self.detail = detail

    class Request:
        def __init__(self) -> None:
            self.headers: dict[str, str] = {}

    class _FallbackFastAPI:
        def get(self, _path: str):
            def decorator(func):
                return func

            return decorator

        def post(self, _path: str):
            def decorator(func):
                return func

            return decorator

    def FastAPI(*_args: Any, **_kwargs: Any) -> _FallbackFastAPI:
        return _FallbackFastAPI()


def utc_now_iso() -> str:
    return datetime.now(timezone.utc).isoformat()


def env_flag(name: str, default: bool = False) -> bool:
    raw = os.getenv(name)
    if raw is None:
        return default
    return raw.strip().lower() in {"1", "true", "yes", "on"}


def log_event(event: str, **fields: Any) -> None:
    payload = {
        "event": event,
        "ts": utc_now_iso(),
    }
    payload.update(fields)
    print(json.dumps(payload, ensure_ascii=True, sort_keys=True), flush=True)


class MediaServiceError(Exception):
    def __init__(self, status_code: int, detail: str) -> None:
        super().__init__(detail)
        self.status_code = status_code
        self.detail = detail


class ExtractAudioRequest(BaseModel):
    sourcePath: str
    outputPath: str
    audioFormat: str = "mp3"
    audioQuality: str = "2"
    overwrite: bool = True


class ExtractAudioResponse(BaseModel):
    outputPath: str
    contentType: str
    fileSize: int


APP = FastAPI(title="GuideAnts Media Service", version="1.0.0")
DEFAULT_STORAGE_ROOT = Path(os.getenv("FILE_STORAGE_ROOT", "/app/ContentFiles")).resolve()
SUPPORTED_AUDIO_FORMATS = {
    "mp3": {
        "codec": "libmp3lame",
        "content_type": "audio/mpeg",
        "suffix": ".mp3",
    }
}


def normalize_relative_storage_path(value: str, field_name: str) -> str:
    raw_value = (value or "").strip()
    if not raw_value:
        raise MediaServiceError(status_code=400, detail=f"{field_name} is required.")

    normalized = raw_value.replace("\\", "/")
    if normalized.startswith("/"):
        raise MediaServiceError(status_code=400, detail=f"{field_name} must be a storage-relative path.")

    posix_path = PurePosixPath(normalized)
    parts = posix_path.parts
    if not parts:
        raise MediaServiceError(status_code=400, detail=f"{field_name} must be a storage-relative path.")

    if ":" in parts[0]:
        raise MediaServiceError(status_code=400, detail=f"{field_name} must not be an absolute OS path.")

    if any(part == ".." for part in parts):
        raise MediaServiceError(status_code=400, detail=f"{field_name} must not escape the shared storage root.")

    return str(PurePosixPath(*parts))


def resolve_storage_path(relative_path: str, field_name: str, storage_root: Path | None = None) -> Path:
    root = (storage_root or DEFAULT_STORAGE_ROOT).resolve()
    normalized = normalize_relative_storage_path(relative_path, field_name)
    resolved = (root / Path(*PurePosixPath(normalized).parts)).resolve()

    if resolved != root and root not in resolved.parents:
        raise MediaServiceError(status_code=400, detail=f"{field_name} must resolve under the shared storage root.")

    return resolved


def resolve_audio_format(audio_format: str) -> dict[str, str]:
    normalized = (audio_format or "").strip().lower()
    if normalized not in SUPPORTED_AUDIO_FORMATS:
        raise MediaServiceError(
            status_code=400,
            detail=f"Unsupported audioFormat '{audio_format}'. Supported values: {', '.join(sorted(SUPPORTED_AUDIO_FORMATS))}.",
        )

    return SUPPORTED_AUDIO_FORMATS[normalized]


def run_ffmpeg(
    source_path: Path,
    output_path: Path,
    codec: str,
    audio_quality: str,
    overwrite: bool,
) -> None:
    command = [
        "ffmpeg",
        "-hide_banner",
        "-loglevel",
        "error",
        "-nostdin",
        "-i",
        str(source_path),
        "-vn",
        "-acodec",
        codec,
        "-q:a",
        audio_quality,
        "-y" if overwrite else "-n",
        str(output_path),
    ]

    process = subprocess.run(command, capture_output=True, text=True, check=False)
    if process.returncode != 0:
        error_text = (process.stderr or process.stdout or "").strip()
        if not error_text:
            error_text = "ffmpeg exited with a non-zero status code."
        raise MediaServiceError(
            status_code=500,
            detail=f"ffmpeg failed with exit code {process.returncode}: {error_text}",
        )


def process_extract_audio_request(
    payload: ExtractAudioRequest,
    storage_root: Path | None = None,
) -> ExtractAudioResponse:
    root = (storage_root or DEFAULT_STORAGE_ROOT).resolve()
    format_info = resolve_audio_format(payload.audioFormat)
    source_path = resolve_storage_path(payload.sourcePath, "sourcePath", root)
    output_path = resolve_storage_path(payload.outputPath, "outputPath", root)

    if source_path == output_path:
        raise MediaServiceError(status_code=400, detail="sourcePath and outputPath must be different.")

    if output_path.suffix.lower() != format_info["suffix"]:
        raise MediaServiceError(
            status_code=400,
            detail=f"outputPath must end with '{format_info['suffix']}' for audioFormat '{payload.audioFormat}'.",
        )

    if not source_path.exists() or not source_path.is_file():
        raise MediaServiceError(status_code=404, detail=f"Source file not found: {payload.sourcePath}")

    if output_path.exists() and not payload.overwrite:
        raise MediaServiceError(status_code=409, detail=f"Output already exists: {payload.outputPath}")

    output_path.parent.mkdir(parents=True, exist_ok=True)

    started = time.perf_counter()
    run_ffmpeg(
        source_path=source_path,
        output_path=output_path,
        codec=format_info["codec"],
        audio_quality=payload.audioQuality,
        overwrite=payload.overwrite,
    )

    if not output_path.exists():
        raise MediaServiceError(status_code=500, detail="ffmpeg completed without creating the output file.")

    file_size = output_path.stat().st_size
    if file_size <= 0:
        raise MediaServiceError(status_code=500, detail="ffmpeg created an empty output file.")

    latency_ms = int((time.perf_counter() - started) * 1000)
    log_event(
        "media_extract_audio_success",
        sourcePath=payload.sourcePath,
        outputPath=payload.outputPath,
        audioFormat=payload.audioFormat,
        fileSize=file_size,
        latencyMs=latency_ms,
    )

    return ExtractAudioResponse(
        outputPath=payload.outputPath,
        contentType=format_info["content_type"],
        fileSize=file_size,
    )


@APP.get("/health")
async def health() -> dict[str, Any]:
    return {
        "status": "ok",
        "storageRoot": str(DEFAULT_STORAGE_ROOT),
        "storageRootExists": DEFAULT_STORAGE_ROOT.exists(),
    }


@APP.post("/extract-audio")
async def extract_audio(request: Request, payload: ExtractAudioRequest) -> dict[str, Any]:
    request_id = request.headers.get("x-request-id", "")
    log_event(
        "media_extract_audio_start",
        requestId=request_id,
        sourcePath=payload.sourcePath,
        outputPath=payload.outputPath,
        audioFormat=payload.audioFormat,
        overwrite=payload.overwrite,
    )

    try:
        result = process_extract_audio_request(payload)
        return result.model_dump()
    except MediaServiceError as exc:
        log_event(
            "media_extract_audio_failed",
            requestId=request_id,
            statusCode=exc.status_code,
            error=exc.detail,
        )
        raise HTTPException(status_code=exc.status_code, detail=exc.detail) from exc
    except Exception as exc:
        log_event(
            "media_extract_audio_failed",
            requestId=request_id,
            statusCode=500,
            errorType=type(exc).__name__,
            error=str(exc),
        )
        raise HTTPException(status_code=500, detail="Unexpected media extraction failure.") from exc


if __name__ == "__main__":
    host = os.getenv("GA_MEDIA_HOST", "127.0.0.1")
    port = int(os.getenv("GA_MEDIA_PORT", "8087"))
    log_level = os.getenv("GA_MEDIA_LOG_LEVEL", "info").lower()
    access_log_enabled = env_flag("GA_MEDIA_UVICORN_ACCESS_LOG", default=False)
    if uvicorn is None:
        raise RuntimeError("uvicorn is required to run the media service.")
    uvicorn.run(APP, host=host, port=port, log_level=log_level, access_log=access_log_enabled)
