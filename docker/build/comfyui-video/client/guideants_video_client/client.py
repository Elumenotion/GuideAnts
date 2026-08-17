"""Scoped client used by ScriptExecutionAgent video scripts."""

from __future__ import annotations

import json
import mimetypes
import os
import re
import tempfile
import urllib.error
import urllib.parse
import urllib.request
import uuid
from pathlib import Path
from typing import Any, BinaryIO

DEFAULT_BASE_URL = "http://127.0.0.1:8190"
REQUEST_TIMEOUT_SECONDS = 180
WORKFLOW_VERSION = "infinitetalk-i2v-v1"
V2V_WORKFLOW_VERSION = "infinitetalk-v2v-v1"
HEX_UUID_PATTERN = re.compile(r"^[0-9a-f]{32}$")


class VideoClientError(RuntimeError):
    """Raised when the adapter rejects a request or cannot be reached."""


def _base_url(value: str | None) -> str:
    raw = value or os.getenv("GUIDEANTS_VIDEO_ADAPTER_URL", DEFAULT_BASE_URL)
    parsed = urllib.parse.urlparse(raw)
    if parsed.scheme not in {"http", "https"} or not parsed.hostname:
        raise VideoClientError("video adapter URL must be an HTTP URL")
    return raw.rstrip("/")


def _working_directory(working_directory: str | os.PathLike[str] | None) -> Path:
    directory = Path(working_directory) if working_directory is not None else Path.cwd()
    if not directory.is_absolute():
        directory = Path.cwd() / directory
    try:
        resolved = directory.resolve(strict=True)
    except OSError as exc:
        raise VideoClientError(f"working directory is unavailable: {exc}") from exc
    if not resolved.is_dir():
        raise VideoClientError("working directory is not a directory")
    return resolved


def _notebook_root(working_directory: Path) -> Path:
    for candidate in (working_directory, *working_directory.parents):
        if (candidate / ".guideants" / "notebook.json").is_file():
            return candidate
    raise VideoClientError(
        "working directory is not inside a notebook containing .guideants/notebook.json"
    )


def resolve_notebook_path(
    value: str | os.PathLike[str],
    working_directory: str | os.PathLike[str] | None = None,
    *,
    must_exist: bool,
) -> Path:
    """Resolve a script path within the marker-defined notebook root."""
    directory = _working_directory(working_directory)
    root = _notebook_root(directory)
    supplied = Path(value)
    candidate = supplied if supplied.is_absolute() else directory / supplied
    try:
        resolved = candidate.resolve(strict=must_exist)
    except OSError as exc:
        raise VideoClientError(f"path is unavailable: {value}: {exc}") from exc
    if resolved != root and root not in resolved.parents:
        raise VideoClientError(f"path escapes the notebook root: {value}")
    return resolved


def _job_id(value: str) -> str:
    if not HEX_UUID_PATTERN.fullmatch(value):
        raise VideoClientError("job_id must be a 32-character lowercase hexadecimal UUID")
    return value


def _request(
    method: str,
    path: str,
    *,
    base_url: str | None = None,
    body: bytes | None = None,
    content_type: str | None = None,
    output: BinaryIO | None = None,
) -> Any:
    headers = {"Content-Type": content_type} if content_type else {}
    request = urllib.request.Request(
        f"{_base_url(base_url)}{path}", data=body, headers=headers, method=method
    )
    try:
        with urllib.request.urlopen(request, timeout=REQUEST_TIMEOUT_SECONDS) as response:
            if output is not None:
                while chunk := response.read(1024 * 1024):
                    output.write(chunk)
                return None
            payload = response.read()
    except urllib.error.HTTPError as exc:
        detail = exc.read().decode("utf-8", errors="replace")
        try:
            detail = json.loads(detail).get("detail", detail)
        except (json.JSONDecodeError, AttributeError):
            pass
        raise VideoClientError(f"video adapter returned HTTP {exc.code}: {detail}") from exc
    except (urllib.error.URLError, TimeoutError) as exc:
        raise VideoClientError(f"video adapter request failed: {exc}") from exc
    try:
        return json.loads(payload)
    except json.JSONDecodeError as exc:
        raise VideoClientError("video adapter returned invalid JSON") from exc


def _multipart(
    fields: dict[str, str], files: dict[str, tuple[str, bytes, str]]
) -> tuple[bytes, str]:
    boundary = f"guideants-{uuid.uuid4().hex}"
    parts: list[bytes] = []
    for name, value in fields.items():
        parts.extend(
            [
                f"--{boundary}\r\n".encode(),
                f'Content-Disposition: form-data; name="{name}"\r\n\r\n'.encode(),
                value.encode(),
                b"\r\n",
            ]
        )
    for name, (filename, data, media_type) in files.items():
        safe_name = Path(filename).name.replace('"', "")
        parts.extend(
            [
                f"--{boundary}\r\n".encode(),
                (
                    f'Content-Disposition: form-data; name="{name}"; '
                    f'filename="{safe_name}"\r\n'
                ).encode(),
                f"Content-Type: {media_type}\r\n\r\n".encode(),
                data,
                b"\r\n",
            ]
        )
    parts.append(f"--{boundary}--\r\n".encode())
    return b"".join(parts), f"multipart/form-data; boundary={boundary}"


def submit_talking_head(
    image_path: str | os.PathLike[str],
    audio_path: str | os.PathLike[str],
    output_filename: str,
    *,
    workflow: str = WORKFLOW_VERSION,
    working_directory: str | os.PathLike[str] | None = None,
    parameters: dict[str, int | float] | None = None,
    positive_prompt: str | None = None,
    negative_prompt: str | None = None,
    base_url: str | None = None,
) -> dict[str, Any]:
    """Upload notebook-scoped inputs and submit the fixed I2V workflow."""
    if workflow != WORKFLOW_VERSION:
        raise VideoClientError(f"unsupported workflow: {workflow}")
    source = resolve_notebook_path(image_path, working_directory, must_exist=True)
    audio = resolve_notebook_path(audio_path, working_directory, must_exist=True)
    if not source.is_file() or not audio.is_file():
        raise VideoClientError("image_path and audio_path must identify files")
    source_type = mimetypes.guess_type(source.name)[0] or "application/octet-stream"
    audio_type = mimetypes.guess_type(audio.name)[0] or "application/octet-stream"
    if audio_type == "audio/x-wav":
        audio_type = "audio/wav"
    if positive_prompt is not None and not positive_prompt.strip():
        raise VideoClientError("positive_prompt must be non-empty")
    if negative_prompt is not None and not negative_prompt.strip():
        raise VideoClientError("negative_prompt must be non-empty")
    fields = {
        "output_filename": output_filename,
        "workflow_version": WORKFLOW_VERSION,
        "parameters": json.dumps(parameters or {}, separators=(",", ":")),
    }
    if positive_prompt is not None:
        fields["positive_prompt"] = positive_prompt
    if negative_prompt is not None:
        fields["negative_prompt"] = negative_prompt
    body, content_type = _multipart(
        fields,
        {
            "source": (source.name, source.read_bytes(), source_type),
            "audio": (audio.name, audio.read_bytes(), audio_type),
        },
    )
    return _request(
        "POST",
        "/v1/talking-head/jobs",
        base_url=base_url,
        body=body,
        content_type=content_type,
    )


def submit_talking_head_v2v(
    video_path: str | os.PathLike[str],
    audio_path: str | os.PathLike[str],
    output_filename: str,
    *,
    workflow: str = V2V_WORKFLOW_VERSION,
    working_directory: str | os.PathLike[str] | None = None,
    parameters: dict[str, int | float] | None = None,
    positive_prompt: str | None = None,
    negative_prompt: str | None = None,
    base_url: str | None = None,
) -> dict[str, Any]:
    """Upload notebook-scoped video/audio and submit the fixed V2V workflow."""
    if workflow != V2V_WORKFLOW_VERSION:
        raise VideoClientError(f"unsupported workflow: {workflow}")
    source = resolve_notebook_path(video_path, working_directory, must_exist=True)
    audio = resolve_notebook_path(audio_path, working_directory, must_exist=True)
    if not source.is_file() or not audio.is_file():
        raise VideoClientError("video_path and audio_path must identify files")
    source_type = mimetypes.guess_type(source.name)[0] or "application/octet-stream"
    audio_type = mimetypes.guess_type(audio.name)[0] or "application/octet-stream"
    if source_type == "application/octet-stream" and source.suffix.lower() == ".mkv":
        source_type = "video/x-matroska"
    if audio_type == "audio/x-wav":
        audio_type = "audio/wav"
    if positive_prompt is not None and not positive_prompt.strip():
        raise VideoClientError("positive_prompt must be non-empty")
    if negative_prompt is not None and not negative_prompt.strip():
        raise VideoClientError("negative_prompt must be non-empty")
    fields = {
        "output_filename": output_filename,
        "workflow_version": V2V_WORKFLOW_VERSION,
        "parameters": json.dumps(parameters or {}, separators=(",", ":")),
    }
    if positive_prompt is not None:
        fields["positive_prompt"] = positive_prompt
    if negative_prompt is not None:
        fields["negative_prompt"] = negative_prompt
    body, content_type = _multipart(
        fields,
        {
            "source": (source.name, source.read_bytes(), source_type),
            "audio": (audio.name, audio.read_bytes(), audio_type),
        },
    )
    return _request(
        "POST",
        "/v1/talking-head/jobs",
        base_url=base_url,
        body=body,
        content_type=content_type,
    )


def get_talking_head_job(job_id: str, *, base_url: str | None = None) -> dict[str, Any]:
    """Read adapter-owned job state."""
    job_id = _job_id(job_id)
    return _request(
        "GET", f"/v1/talking-head/jobs/{urllib.parse.quote(job_id)}", base_url=base_url
    )


def cancel_talking_head_job(job_id: str, *, base_url: str | None = None) -> dict[str, Any]:
    """Request cancellation for a queued or running job."""
    job_id = _job_id(job_id)
    return _request(
        "POST",
        f"/v1/talking-head/jobs/{urllib.parse.quote(job_id)}/cancel",
        base_url=base_url,
        body=b"",
    )


def materialize_talking_head_result(
    job_id: str,
    output_path: str | os.PathLike[str],
    *,
    working_directory: str | os.PathLike[str] | None = None,
    base_url: str | None = None,
) -> dict[str, Any]:
    """Atomically write a completed result inside the notebook scope."""
    job_id = _job_id(job_id)
    destination = resolve_notebook_path(output_path, working_directory, must_exist=False)
    if destination.suffix.lower() != ".mkv":
        raise VideoClientError("output_path must end in .mkv")
    if not destination.parent.is_dir():
        raise VideoClientError("output directory does not exist")
    handle, temporary_name = tempfile.mkstemp(
        prefix=f".{destination.name}.", suffix=".part", dir=destination.parent
    )
    try:
        with os.fdopen(handle, "wb") as output:
            _request(
                "GET",
                f"/v1/talking-head/jobs/{urllib.parse.quote(job_id)}/result",
                base_url=base_url,
                output=output,
            )
        temporary = Path(temporary_name)
        if temporary.stat().st_size == 0:
            raise VideoClientError("video adapter returned an empty result")
        os.replace(temporary, destination)
    finally:
        Path(temporary_name).unlink(missing_ok=True)
    return {
        "jobId": job_id,
        "outputPath": str(destination),
        "bytes": destination.stat().st_size,
    }


IMAGE_WORKFLOW_VERSION = "qwen-image-edit-v1"
IMAGE_EDIT_20_WORKFLOW_VERSION = "qwen-image-edit-20-v1"
IMAGE_EDIT_BF16_WORKFLOW_VERSION = "qwen-image-edit-bf16-v1"
IMAGE_EDIT_BF16_INPAINT_WORKFLOW_VERSION = "qwen-image-edit-bf16-inpaint-v1"
IMAGE_GENERATE_WORKFLOW_VERSION = "qwen-image-v1"
IMAGE_EDIT_WORKFLOW_VERSIONS = frozenset(
    {
        IMAGE_WORKFLOW_VERSION,
        IMAGE_EDIT_20_WORKFLOW_VERSION,
        IMAGE_EDIT_BF16_WORKFLOW_VERSION,
        IMAGE_EDIT_BF16_INPAINT_WORKFLOW_VERSION,
    }
)


def submit_image_edit(
    image_path: str | os.PathLike[str],
    prompt: str,
    output_filename: str,
    *,
    workflow: str = IMAGE_WORKFLOW_VERSION,
    mask_path: str | os.PathLike[str] | None = None,
    working_directory: str | os.PathLike[str] | None = None,
    parameters: dict[str, int | float] | None = None,
    negative_prompt: str = " ",
    base_url: str | None = None,
) -> dict[str, Any]:
    """Upload a notebook-scoped image and submit a Qwen Image Edit workflow."""
    if workflow not in IMAGE_EDIT_WORKFLOW_VERSIONS:
        raise VideoClientError(f"unsupported workflow: {workflow}")
    if not prompt.strip():
        raise VideoClientError("prompt must be non-empty")
    source = resolve_notebook_path(image_path, working_directory, must_exist=True)
    if not source.is_file():
        raise VideoClientError("image_path must identify a file")
    if workflow == IMAGE_EDIT_BF16_INPAINT_WORKFLOW_VERSION and mask_path is None:
        raise VideoClientError("mask_path is required for the BF16 inpaint workflow")
    if workflow != IMAGE_EDIT_BF16_INPAINT_WORKFLOW_VERSION and mask_path is not None:
        raise VideoClientError("mask_path is only valid for the BF16 inpaint workflow")
    mask = (
        resolve_notebook_path(mask_path, working_directory, must_exist=True)
        if mask_path is not None
        else None
    )
    if mask is not None and not mask.is_file():
        raise VideoClientError("mask_path must identify a file")
    source_type = mimetypes.guess_type(source.name)[0] or "application/octet-stream"
    files = {"source": (source.name, source.read_bytes(), source_type)}
    if mask is not None:
        mask_type = mimetypes.guess_type(mask.name)[0] or "application/octet-stream"
        files["mask"] = (mask.name, mask.read_bytes(), mask_type)
    body, content_type = _multipart(
        {
            "prompt": prompt,
            "output_filename": output_filename,
            "workflow_version": workflow,
            "negative_prompt": negative_prompt,
            "parameters": json.dumps(parameters or {}, separators=(",", ":")),
        },
        files,
    )
    return _request(
        "POST",
        "/v1/image/jobs",
        base_url=base_url,
        body=body,
        content_type=content_type,
    )


def submit_image_generate(
    prompt: str,
    output_filename: str,
    *,
    workflow: str = IMAGE_GENERATE_WORKFLOW_VERSION,
    working_directory: str | os.PathLike[str] | None = None,
    parameters: dict[str, int | float] | None = None,
    negative_prompt: str = " ",
    base_url: str | None = None,
) -> dict[str, Any]:
    """Submit the Qwen Image 2512 text-to-image workflow (no source image)."""
    if workflow != IMAGE_GENERATE_WORKFLOW_VERSION:
        raise VideoClientError(f"unsupported workflow: {workflow}")
    if not prompt.strip():
        raise VideoClientError("prompt must be non-empty")
    # Ensure caller is inside a notebook scope even though no input file is used.
    resolve_notebook_path(".", working_directory, must_exist=False)
    body, content_type = _multipart(
        {
            "prompt": prompt,
            "output_filename": output_filename,
            "workflow_version": IMAGE_GENERATE_WORKFLOW_VERSION,
            "negative_prompt": negative_prompt,
            "parameters": json.dumps(parameters or {}, separators=(",", ":")),
        },
        {},
    )
    return _request(
        "POST",
        "/v1/image/generate/jobs",
        base_url=base_url,
        body=body,
        content_type=content_type,
    )


def get_image_job(job_id: str, *, base_url: str | None = None) -> dict[str, Any]:
    """Read adapter-owned image job state."""
    job_id = _job_id(job_id)
    return _request(
        "GET", f"/v1/image/jobs/{urllib.parse.quote(job_id)}", base_url=base_url
    )


def cancel_image_job(job_id: str, *, base_url: str | None = None) -> dict[str, Any]:
    """Request cancellation for a queued or running image job."""
    job_id = _job_id(job_id)
    return _request(
        "POST",
        f"/v1/image/jobs/{urllib.parse.quote(job_id)}/cancel",
        base_url=base_url,
        body=b"",
    )


def materialize_image_result(
    job_id: str,
    output_path: str | os.PathLike[str],
    *,
    working_directory: str | os.PathLike[str] | None = None,
    base_url: str | None = None,
) -> dict[str, Any]:
    """Atomically write a completed image result inside the notebook scope."""
    job_id = _job_id(job_id)
    destination = resolve_notebook_path(output_path, working_directory, must_exist=False)
    if destination.suffix.lower() != ".png":
        raise VideoClientError("output_path must end in .png")
    if not destination.parent.is_dir():
        raise VideoClientError("output directory does not exist")
    handle, temporary_name = tempfile.mkstemp(
        prefix=f".{destination.name}.", suffix=".part", dir=destination.parent
    )
    try:
        with os.fdopen(handle, "wb") as output:
            _request(
                "GET",
                f"/v1/image/jobs/{urllib.parse.quote(job_id)}/result",
                base_url=base_url,
                output=output,
            )
        temporary = Path(temporary_name)
        if temporary.stat().st_size == 0:
            raise VideoClientError("video adapter returned an empty result")
        os.replace(temporary, destination)
    finally:
        Path(temporary_name).unlink(missing_ok=True)
    return {
        "jobId": job_id,
        "outputPath": str(destination),
        "bytes": destination.stat().st_size,
    }

