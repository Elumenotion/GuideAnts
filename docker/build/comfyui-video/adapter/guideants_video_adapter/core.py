"""State and transport primitives for the GuideAnts InfiniteTalk adapter."""

from __future__ import annotations

import hashlib
import io
import json
import os
import re
import threading
import wave
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid
from dataclasses import asdict, dataclass, field
from pathlib import Path
from typing import Any

from .comfy_telemetry import (
    ComfyProgressListener,
    initial_progress,
    log_job_progress,
    merge_progress,
    queue_state_for_prompt,
)

API_VERSION = "v1"
WORKFLOW_VERSION = "infinitetalk-i2v-v1"
IMAGE_WORKFLOW_VERSION = "qwen-image-edit-v1"
IMAGE_BUNDLE = "qwen-image-edit-v1"
IMAGE_GENERATE_WORKFLOW_VERSION = "qwen-image-v1"
IMAGE_GENERATE_BUNDLE = "qwen-image-v1"
TERMINAL_STATES = frozenset({"completed", "failed", "cancelled"})
ALLOWED_PARAMETERS: dict[str, tuple[type, float | int, float | int]] = {
    "width": (int, 256, 1920),
    "height": (int, 256, 1920),
    "fps": (int, 1, 60),
    "frames": (int, 1, 1800),
    "steps": (int, 1, 100),
    "seed": (int, 0, 2**63 - 1),
    "cfg": ((int, float), 0.0, 30.0),  # type: ignore[dict-item]
}
DEFAULT_PARAMETERS: dict[str, int | float] = {
    "width": 832,
    "height": 480,
    "fps": 25,
    "frames": 125,
    "steps": 14,
    "seed": 0,
    "cfg": 5.0,
}
IMAGE_ALLOWED_PARAMETERS: dict[str, tuple[type, float | int, float | int]] = {
    "steps": (int, 1, 50),
    "seed": (int, 0, 2**63 - 1),
    "cfg": ((int, float), 0.0, 30.0),  # type: ignore[dict-item]
}
IMAGE_DEFAULT_PARAMETERS: dict[str, int | float] = {
    "steps": 4,
    "seed": 0,
    "cfg": 1.0,
}
MAX_SOURCE_BYTES = 100 * 1024 * 1024
MAX_AUDIO_BYTES = 50 * 1024 * 1024
ALLOWED_INPUT_TYPES = {
    "image/png": ".png",
    "image/jpeg": ".jpg",
    "image/webp": ".webp",
    "audio/wav": ".wav",
    "audio/x-wav": ".wav",
    "audio/mpeg": ".mp3",
    "audio/flac": ".flac",
    "audio/ogg": ".ogg",
}
HEX_UUID_PATTERN = re.compile(r"^[0-9a-f]{32}$")


class AdapterError(RuntimeError):
    """A public adapter error with an HTTP-compatible status."""

    def __init__(self, message: str, status_code: int = 500) -> None:
        super().__init__(message)
        self.status_code = status_code


def validate_identifier(value: str, kind: str) -> str:
    if not HEX_UUID_PATTERN.fullmatch(value):
        raise AdapterError(f"invalid {kind} identifier", 422)
    return value


def safe_output_filename(value: str) -> str:
    """Accept a basename MP4 only, never a path."""
    if not value or Path(value).name != value or value in {".", ".."}:
        raise AdapterError("output_filename must be a filename, not a path", 422)
    if Path(value).suffix.lower() != ".mp4":
        raise AdapterError("output_filename must end in .mp4", 422)
    if any(char in value for char in ("/", "\\", "\0")):
        raise AdapterError("output_filename contains an invalid character", 422)
    return value


def safe_image_output_filename(value: str) -> str:
    """Accept a basename PNG only, never a path."""
    if not value or Path(value).name != value or value in {".", ".."}:
        raise AdapterError("output_filename must be a filename, not a path", 422)
    if Path(value).suffix.lower() != ".png":
        raise AdapterError("output_filename must end in .png", 422)
    if any(char in value for char in ("/", "\\", "\0")):
        raise AdapterError("output_filename contains an invalid character", 422)
    return value


def validate_parameters(raw: dict[str, Any]) -> dict[str, int | float]:
    unknown = sorted(set(raw) - set(ALLOWED_PARAMETERS))
    if unknown:
        raise AdapterError(f"unsupported workflow parameters: {', '.join(unknown)}", 422)
    result = dict(DEFAULT_PARAMETERS)
    for name, value in raw.items():
        expected, minimum, maximum = ALLOWED_PARAMETERS[name]
        if isinstance(value, bool) or not isinstance(value, expected):
            raise AdapterError(f"{name} has an invalid type", 422)
        if value < minimum or value > maximum:
            raise AdapterError(f"{name} must be between {minimum} and {maximum}", 422)
        result[name] = value
    return result


def validate_image_parameters(raw: dict[str, Any]) -> dict[str, int | float]:
    unknown = sorted(set(raw) - set(IMAGE_ALLOWED_PARAMETERS))
    if unknown:
        raise AdapterError(f"unsupported workflow parameters: {', '.join(unknown)}", 422)
    result = dict(IMAGE_DEFAULT_PARAMETERS)
    for name, value in raw.items():
        expected, minimum, maximum = IMAGE_ALLOWED_PARAMETERS[name]
        if isinstance(value, bool) or not isinstance(value, expected):
            raise AdapterError(f"{name} has an invalid type", 422)
        if value < minimum or value > maximum:
            raise AdapterError(f"{name} must be between {minimum} and {maximum}", 422)
        result[name] = value
    return result


def probe_audio_duration_seconds(audio: bytes, audio_type: str) -> float:
    """Return the duration of a supported audio upload in seconds."""
    normalized = audio_type.lower().split(";", 1)[0].strip()
    if normalized in {"audio/wav", "audio/x-wav"}:
        try:
            with wave.open(io.BytesIO(audio), "rb") as wav_file:
                frame_rate = wav_file.getframerate()
                frame_count = wav_file.getnframes()
        except wave.Error as exc:
            raise AdapterError(f"audio WAV is invalid: {exc}", 422) from exc
        if frame_rate <= 0:
            raise AdapterError("audio WAV has an invalid sample rate", 422)
        if frame_count <= 0:
            raise AdapterError("audio WAV contains no frames", 422)
        return frame_count / frame_rate
    raise AdapterError(
        "frames must be supplied explicitly for non-WAV audio; "
        f"cannot derive duration from media type {audio_type!r}",
        422,
    )


def resolve_workflow_parameters(
    raw: dict[str, Any],
    audio: bytes,
    audio_type: str,
) -> dict[str, int | float]:
    """Merge caller parameters and derive frame count from audio when omitted."""
    parameters = validate_parameters(raw)
    if "frames" in raw:
        return parameters
    duration = probe_audio_duration_seconds(audio, audio_type)
    fps = int(parameters["fps"])
    _, minimum, maximum = ALLOWED_PARAMETERS["frames"]
    parameters["frames"] = max(int(minimum), min(int(maximum), round(duration * fps)))
    return parameters


def _inside(root: Path, relative: str) -> Path:
    candidate = (root / relative).resolve()
    resolved_root = root.resolve()
    if candidate != resolved_root and resolved_root not in candidate.parents:
        raise AdapterError("catalog artifact path escapes the model root", 500)
    return candidate


def _sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as source:
        while chunk := source.read(1024 * 1024):
            digest.update(chunk)
    return digest.hexdigest()


@dataclass
class Job:
    id: str
    state: str = "queued"
    created_at: float = field(default_factory=time.time)
    updated_at: float = field(default_factory=time.time)
    prompt_id: str | None = None
    output_filename: str = "output.mp4"
    output_path: str | None = None
    error: str | None = None
    cancel_requested: bool = False
    progress: dict[str, Any] = field(default_factory=initial_progress)

    def public(self) -> dict[str, Any]:
        data = asdict(self)
        data["jobId"] = data.pop("id")
        data.pop("output_path")
        data.pop("cancel_requested")
        data["result_available"] = self.state == "completed"
        data["progress"] = dict(self.progress)
        return data


@dataclass
class Installation:
    id: str
    bundle: str
    state: str = "queued"
    created_at: float = field(default_factory=time.time)
    updated_at: float = field(default_factory=time.time)
    artifacts_completed: int = 0
    artifacts_total: int = 0
    error: str | None = None

    def public(self) -> dict[str, Any]:
        data = asdict(self)
        data["installId"] = data.pop("id")
        return data


class ComfyTransport:
    """Narrow ComfyUI HTTP transport used by the adapter."""

    def __init__(self, base_url: str, timeout: float = 10.0) -> None:
        parsed = urllib.parse.urlparse(base_url)
        if parsed.scheme not in {"http", "https"} or not parsed.hostname:
            raise ValueError("COMFYUI_URL must be an HTTP URL")
        self.base_url = base_url.rstrip("/")
        self.timeout = timeout

    def _request(
        self, method: str, path: str, body: bytes | None = None, content_type: str | None = None
    ) -> bytes:
        headers = {"Content-Type": content_type} if content_type else {}
        request = urllib.request.Request(
            f"{self.base_url}{path}", data=body, headers=headers, method=method
        )
        try:
            with urllib.request.urlopen(request, timeout=self.timeout) as response:
                return response.read()
        except (urllib.error.URLError, TimeoutError) as exc:
            raise AdapterError(f"ComfyUI request failed: {exc}") from exc

    def system_stats(self) -> dict[str, Any]:
        return json.loads(self._request("GET", "/system_stats"))

    def object_info(self) -> dict[str, Any]:
        return json.loads(self._request("GET", "/object_info"))

    def upload(self, filename: str, data: bytes, content_type: str) -> str:
        boundary = f"guideants-{uuid.uuid4().hex}"
        disposition = (
            f'--{boundary}\r\nContent-Disposition: form-data; name="image"; '
            f'filename="{filename}"\r\nContent-Type: {content_type}\r\n\r\n'
        ).encode()
        body = disposition + data + f"\r\n--{boundary}--\r\n".encode()
        payload = json.loads(
            self._request(
                "POST", "/upload/image", body, f"multipart/form-data; boundary={boundary}"
            )
        )
        name = payload.get("name")
        if not isinstance(name, str) or not name:
            raise AdapterError("ComfyUI upload response did not contain a name")
        return name

    def submit(self, workflow: dict[str, Any], client_id: str) -> str:
        body = json.dumps({"prompt": workflow, "client_id": client_id}).encode()
        payload = json.loads(self._request("POST", "/prompt", body, "application/json"))
        prompt_id = payload.get("prompt_id")
        if not isinstance(prompt_id, str) or not prompt_id:
            raise AdapterError("ComfyUI prompt response did not contain prompt_id")
        return prompt_id

    def history(self, prompt_id: str) -> dict[str, Any]:
        return json.loads(self._request("GET", f"/history/{urllib.parse.quote(prompt_id)}"))

    def queue(self) -> dict[str, Any]:
        return json.loads(self._request("GET", "/queue"))

    def interrupt(self) -> None:
        self._request("POST", "/interrupt", b"{}", "application/json")

    def download_output(self, descriptor: dict[str, Any]) -> bytes:
        allowed = {"filename", "subfolder", "type"}
        query_values = {key: descriptor[key] for key in allowed if key in descriptor}
        if not isinstance(query_values.get("filename"), str):
            raise AdapterError("ComfyUI output did not contain a filename")
        query = urllib.parse.urlencode(query_values)
        return self._request("GET", f"/view?{query}")


def render_workflow(
    template: dict[str, Any],
    image_name: str,
    audio_name: str,
    parameters: dict[str, int | float],
    extra_replacements: dict[str, Any] | None = None,
) -> dict[str, Any]:
    replacements: dict[str, Any] = {
        "{{INPUT_IMAGE}}": image_name,
        "{{INPUT_AUDIO}}": audio_name,
        **{f"{{{{{name.upper()}}}}}": value for name, value in parameters.items()},
    }
    if extra_replacements:
        replacements.update(extra_replacements)

    def replace(value: Any) -> Any:
        if isinstance(value, dict):
            return {key: replace(item) for key, item in value.items()}
        if isinstance(value, list):
            return [replace(item) for item in value]
        if isinstance(value, str) and value in replacements:
            return replacements[value]
        return value

    rendered = replace(template)
    unresolved = [token for token in json.dumps(rendered).split('"') if token.startswith("{{")]
    if unresolved:
        raise AdapterError(f"workflow contains unresolved placeholders: {unresolved}", 500)
    return rendered


def find_video_output(history: dict[str, Any], prompt_id: str) -> dict[str, Any] | None:
    entry = history.get(prompt_id)
    if not isinstance(entry, dict):
        return None
    status = entry.get("status", {})
    if isinstance(status, dict) and status.get("status_str") == "error":
        messages = status.get("messages")
        raise AdapterError(f"ComfyUI execution failed: {messages}")
    outputs = entry.get("outputs", {})
    if not isinstance(outputs, dict):
        return None
    for node in outputs.values():
        if not isinstance(node, dict):
            continue
        for key in ("videos", "gifs", "images"):
            values = node.get(key, [])
            if isinstance(values, list):
                for descriptor in values:
                    if isinstance(descriptor, dict):
                        filename = descriptor.get("filename", "")
                        if isinstance(filename, str) and filename.lower().endswith(".mp4"):
                            return descriptor
    if isinstance(status, dict) and status.get("status_str") == "success":
        raise AdapterError("ComfyUI completed without producing an MP4 output")
    return None


def find_image_output(history: dict[str, Any], prompt_id: str) -> dict[str, Any] | None:
    entry = history.get(prompt_id)
    if not isinstance(entry, dict):
        return None
    status = entry.get("status", {})
    if isinstance(status, dict) and status.get("status_str") == "error":
        messages = status.get("messages")
        raise AdapterError(f"ComfyUI execution failed: {messages}")
    outputs = entry.get("outputs", {})
    if not isinstance(outputs, dict):
        return None
    for node in outputs.values():
        if not isinstance(node, dict):
            continue
        values = node.get("images", [])
        if not isinstance(values, list):
            continue
        for descriptor in values:
            if not isinstance(descriptor, dict):
                continue
            filename = descriptor.get("filename", "")
            if isinstance(filename, str) and filename.lower().endswith((".png", ".jpg", ".jpeg", ".webp")):
                return descriptor
    if isinstance(status, dict) and status.get("status_str") == "success":
        raise AdapterError("ComfyUI completed without producing an image output")
    return None


class AdapterService:
    def __init__(
        self,
        jobs_root: Path,
        models_root: Path,
        workflow_path: Path,
        manifest_path: Path,
        comfy: ComfyTransport,
        poll_interval: float = 1.0,
        image_workflow_path: Path | None = None,
        image_generate_workflow_path: Path | None = None,
    ) -> None:
        self.jobs_root = jobs_root
        self.models_root = models_root
        self.workflow_path = workflow_path
        self.image_workflow_path = image_workflow_path or (
            workflow_path.parent / f"{IMAGE_WORKFLOW_VERSION}.json"
        )
        self.image_generate_workflow_path = image_generate_workflow_path or (
            workflow_path.parent / f"{IMAGE_GENERATE_WORKFLOW_VERSION}.json"
        )
        self.manifest_path = manifest_path
        self.comfy = comfy
        self.poll_interval = poll_interval
        self.jobs: dict[str, Job] = {}
        self.installations: dict[str, Installation] = {}
        self._lock = threading.RLock()
        jobs_root.mkdir(parents=True, exist_ok=True, mode=0o700)

    def _update_job_progress(self, job: Job, *, log: bool = True, **updates: Any) -> None:
        with self._lock:
            job.progress = merge_progress(job.progress, **updates)
            job.updated_at = job.progress["updated_at"]
            if log:
                log_job_progress(job.id, job.progress)

    def health(self) -> dict[str, Any]:
        return {"status": "ok", "service": "guideants-video-adapter", "api_version": API_VERSION}

    def readiness(self) -> tuple[bool, dict[str, Any]]:
        missing: list[str] = []
        if not self.workflow_path.is_file():
            missing.append("workflow")
        if not self._bundle_ready(WORKFLOW_VERSION):
            missing.append("models")
        return self._comfy_readiness(missing, self.workflow_path)

    def image_readiness(self) -> tuple[bool, dict[str, Any]]:
        missing: list[str] = []
        if not self.image_workflow_path.is_file():
            missing.append("image_workflow")
        if not self._bundle_ready(IMAGE_BUNDLE):
            missing.append("image_models")
        return self._comfy_readiness(missing, self.image_workflow_path)

    def image_generate_readiness(self) -> tuple[bool, dict[str, Any]]:
        missing: list[str] = []
        if not self.image_generate_workflow_path.is_file():
            missing.append("image_generate_workflow")
        if not self._bundle_ready(IMAGE_GENERATE_BUNDLE):
            missing.append("image_generate_models")
        return self._comfy_readiness(missing, self.image_generate_workflow_path)

    def _comfy_readiness(
        self, missing: list[str], workflow_path: Path
    ) -> tuple[bool, dict[str, Any]]:
        device: dict[str, Any] | None = None
        try:
            stats = self.comfy.system_stats()
            device = stats.get("devices", [{}])[0] if stats.get("devices") else None
            device_type = str((device or {}).get("type", "")).lower()
            device_name = str((device or {}).get("name", "")).lower()
            if (
                "cuda" not in device_type
                and "nvidia" not in device_name
                and "hip" not in device_type
                and "amd" not in device_name
                and "radeon" not in device_name
            ):
                missing.append("cuda_gpu")
            node_info = self.comfy.object_info()
            template = json.loads(workflow_path.read_text(encoding="utf-8"))
            required_nodes = {
                node["class_type"]
                for node in template.values()
                if isinstance(node, dict) and isinstance(node.get("class_type"), str)
            }
            absent_nodes = sorted(required_nodes - set(node_info))
            if absent_nodes:
                missing.append(f"comfyui_nodes: {', '.join(absent_nodes)}")
        except (AdapterError, KeyError, TypeError, json.JSONDecodeError) as exc:
            missing.append(f"comfyui: {exc}")
        except OSError as exc:
            missing.append(f"workflow: {exc}")
        ready = not missing
        return ready, {"ready": ready, "missing": missing, "device": device}

    def capabilities(self) -> dict[str, Any]:
        ready, details = self.readiness()
        image_ready, image_details = self.image_readiness()
        image_generate_ready, image_generate_details = self.image_generate_readiness()
        device = (
            details.get("device")
            or image_details.get("device")
            or image_generate_details.get("device")
            or {}
        )
        return {
            "api_version": API_VERSION,
            "backend": "comfyui",
            "workflow_versions": [
                WORKFLOW_VERSION,
                IMAGE_WORKFLOW_VERSION,
                IMAGE_GENERATE_WORKFLOW_VERSION,
            ],
            "input_kinds": ["image"],
            "audio_types": ["audio/wav", "audio/x-wav", "audio/mpeg", "audio/flac", "audio/ogg"],
            "output_type": "video/mp4",
            "device": device.get("name"),
            "precision": os.getenv("VIDEO_PRECISION", "bfloat16"),
            "ready": ready,
            "image_ready": image_ready,
            "image_generate_ready": image_generate_ready,
        }

    def _manifest(self) -> dict[str, Any]:
        if not self.manifest_path.is_file():
            raise AdapterError("model catalog manifest is unavailable", 503)
        try:
            value = json.loads(self.manifest_path.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError) as exc:
            raise AdapterError(f"model catalog manifest is invalid: {exc}", 503) from exc
        if not isinstance(value.get("bundles"), dict):
            raise AdapterError("model catalog manifest must contain a bundles object", 503)
        return value

    def models(self) -> dict[str, Any]:
        try:
            manifest = self._manifest()
        except AdapterError as exc:
            return {"ready": False, "error": str(exc), "bundles": []}
        bundles = []
        all_ready = True
        for name, bundle in manifest["bundles"].items():
            artifacts = []
            for artifact in bundle.get("artifacts", []):
                path = _inside(self.models_root, artifact["path"])
                present = path.is_file()
                size_ok = present and path.stat().st_size == artifact["size"]
                item_ready = present and size_ok
                all_ready = all_ready and item_ready
                artifacts.append(
                    {
                        "path": artifact["path"],
                        "size": artifact["size"],
                        "sha256": artifact["sha256"],
                        "ready": item_ready,
                    }
                )
            bundles.append({"name": name, "ready": all(a["ready"] for a in artifacts), "artifacts": artifacts})
        return {"ready": all_ready and bool(bundles), "bundles": bundles}

    def _bundle_ready(self, bundle_name: str) -> bool:
        models = self.models()
        for bundle in models.get("bundles", []):
            if bundle.get("name") == bundle_name:
                return bool(bundle.get("ready"))
        return False

    def install(self, bundle_name: str) -> Installation:
        manifest = self._manifest()
        bundle = manifest["bundles"].get(bundle_name)
        if not isinstance(bundle, dict):
            raise AdapterError(f"unknown model bundle: {bundle_name}", 404)
        installation = Installation(
            id=uuid.uuid4().hex,
            bundle=bundle_name,
            artifacts_total=len(bundle.get("artifacts", [])),
        )
        with self._lock:
            self.installations[installation.id] = installation
        threading.Thread(
            target=self._install_worker, args=(installation, bundle), daemon=True
        ).start()
        return installation

    def _install_worker(self, installation: Installation, bundle: dict[str, Any]) -> None:
        installation.state = "running"
        installation.updated_at = time.time()
        try:
            for artifact in bundle.get("artifacts", []):
                destination = _inside(self.models_root, artifact["path"])
                destination.parent.mkdir(parents=True, exist_ok=True)
                if (
                    destination.is_file()
                    and destination.stat().st_size == artifact["size"]
                    and _sha256_file(destination).lower() == artifact["sha256"].lower()
                ):
                    installation.artifacts_completed += 1
                    installation.updated_at = time.time()
                    continue
                temporary = destination.with_name(f".{destination.name}.{uuid.uuid4().hex}.part")
                digest = hashlib.sha256()
                written = 0
                try:
                    with urllib.request.urlopen(artifact["url"], timeout=60) as source, temporary.open("wb") as target:
                        while chunk := source.read(1024 * 1024):
                            target.write(chunk)
                            digest.update(chunk)
                            written += len(chunk)
                    if written != artifact["size"]:
                        raise AdapterError(f"size mismatch for {artifact['path']}")
                    if digest.hexdigest().lower() != artifact["sha256"].lower():
                        raise AdapterError(f"checksum mismatch for {artifact['path']}")
                    os.replace(temporary, destination)
                finally:
                    temporary.unlink(missing_ok=True)
                installation.artifacts_completed += 1
                installation.updated_at = time.time()
            installation.state = "completed"
        except Exception as exc:  # worker boundary records an explicit terminal failure
            installation.state = "failed"
            installation.error = str(exc)
        finally:
            installation.updated_at = time.time()

    def submit_job(
        self,
        source: bytes,
        source_type: str,
        audio: bytes,
        audio_type: str,
        output_filename: str,
        workflow_version: str,
        parameters: dict[str, Any],
    ) -> Job:
        if workflow_version != WORKFLOW_VERSION:
            raise AdapterError(f"unsupported workflow_version: {workflow_version}", 422)
        if source_type not in ALLOWED_INPUT_TYPES or not source_type.startswith("image/"):
            raise AdapterError("unsupported source media type", 415)
        if audio_type not in ALLOWED_INPUT_TYPES or not audio_type.startswith("audio/"):
            raise AdapterError("unsupported audio media type", 415)
        if not source or not audio:
            raise AdapterError("source and audio must be non-empty", 422)
        if len(source) > MAX_SOURCE_BYTES:
            raise AdapterError(f"source exceeds the {MAX_SOURCE_BYTES}-byte limit", 413)
        if len(audio) > MAX_AUDIO_BYTES:
            raise AdapterError(f"audio exceeds the {MAX_AUDIO_BYTES}-byte limit", 413)
        output_filename = safe_output_filename(output_filename)
        parameters = resolve_workflow_parameters(parameters, audio, audio_type)
        ready, details = self.readiness()
        if not ready:
            raise AdapterError(f"video backend is not ready: {details['missing']}", 503)
        job = Job(id=uuid.uuid4().hex, output_filename=output_filename)
        job_dir = self.jobs_root / job.id
        job_dir.mkdir(mode=0o700)
        (job_dir / f"source{ALLOWED_INPUT_TYPES[source_type]}").write_bytes(source)
        (job_dir / f"audio{ALLOWED_INPUT_TYPES[audio_type]}").write_bytes(audio)
        with self._lock:
            self.jobs[job.id] = job
        threading.Thread(
            target=self._job_worker,
            args=(job, source, source_type, audio, audio_type, parameters),
            daemon=True,
        ).start()
        return job

    def _job_worker(
        self,
        job: Job,
        source: bytes,
        source_type: str,
        audio: bytes,
        audio_type: str,
        parameters: dict[str, int | float],
    ) -> None:
        job.state = "running"
        self._update_job_progress(job, phase="running", message="starting job")
        listener: ComfyProgressListener | None = None
        try:
            self._update_job_progress(job, phase="uploading", message="uploading source image")
            source_name = self.comfy.upload(
                f"{job.id}-source{ALLOWED_INPUT_TYPES[source_type]}", source, source_type
            )
            self._update_job_progress(job, phase="uploading", message="uploading audio")
            audio_name = self.comfy.upload(
                f"{job.id}-audio{ALLOWED_INPUT_TYPES[audio_type]}", audio, audio_type
            )
            template = json.loads(self.workflow_path.read_text(encoding="utf-8"))
            workflow = render_workflow(template, source_name, audio_name, parameters)
            self._update_job_progress(job, phase="submitting", message="submitting ComfyUI prompt")
            job.prompt_id = self.comfy.submit(workflow, job.id)
            listener = ComfyProgressListener(
                self.comfy.base_url,
                job.id,
                job.prompt_id,
                workflow,
                lambda updates, current_job=job: self._update_job_progress(current_job, **updates),
            )
            listener.start()
            self._update_job_progress(
                job,
                phase="waiting",
                message=f"queued prompt {job.prompt_id}",
                last_event="prompt_submitted",
            )
            while True:
                if job.cancel_requested:
                    self.comfy.interrupt()
                    job.state = "cancelled"
                    self._update_job_progress(job, phase="cancelled", message="job cancelled")
                    return
                try:
                    queue_updates = queue_state_for_prompt(self.comfy.queue(), job.prompt_id)
                    if queue_updates:
                        self._update_job_progress(job, log=False, **queue_updates)
                except AdapterError:
                    pass
                descriptor = find_video_output(self.comfy.history(job.prompt_id), job.prompt_id)
                if descriptor is not None:
                    self._update_job_progress(job, phase="encoding", message="downloading ComfyUI output")
                    output = self.comfy.download_output(descriptor)
                    if not output:
                        raise AdapterError("ComfyUI returned an empty video")
                    output_path = self.jobs_root / job.id / job.output_filename
                    output_path.write_bytes(output)
                    job.output_path = str(output_path)
                    job.state = "completed"
                    self._update_job_progress(job, phase="completed", message="video ready", percent=100.0)
                    return
                time.sleep(self.poll_interval)
        except Exception as exc:  # worker boundary records an explicit terminal failure
            job.state = "failed"
            job.error = str(exc)
            self._update_job_progress(job, phase="failed", message=str(exc))
        finally:
            if listener is not None:
                listener.stop()
            job.updated_at = time.time()

    def submit_image_job(
        self,
        source: bytes,
        source_type: str,
        prompt: str,
        output_filename: str,
        workflow_version: str,
        parameters: dict[str, Any],
        negative_prompt: str = " ",
    ) -> Job:
        if workflow_version != IMAGE_WORKFLOW_VERSION:
            raise AdapterError(f"unsupported workflow_version: {workflow_version}", 422)
        if source_type not in ALLOWED_INPUT_TYPES or not source_type.startswith("image/"):
            raise AdapterError("unsupported source media type", 415)
        if not source:
            raise AdapterError("source must be non-empty", 422)
        if len(source) > MAX_SOURCE_BYTES:
            raise AdapterError(f"source exceeds the {MAX_SOURCE_BYTES}-byte limit", 413)
        if not prompt.strip():
            raise AdapterError("prompt must be non-empty", 422)
        output_filename = safe_image_output_filename(output_filename)
        parameters = validate_image_parameters(parameters)
        ready, details = self.image_readiness()
        if not ready:
            raise AdapterError(f"image backend is not ready: {details['missing']}", 503)
        job = Job(id=uuid.uuid4().hex, output_filename=output_filename)
        job_dir = self.jobs_root / job.id
        job_dir.mkdir(mode=0o700)
        (job_dir / f"source{ALLOWED_INPUT_TYPES[source_type]}").write_bytes(source)
        with self._lock:
            self.jobs[job.id] = job
        threading.Thread(
            target=self._image_job_worker,
            args=(job, source, source_type, prompt, negative_prompt, parameters),
            daemon=True,
        ).start()
        return job

    def _image_job_worker(
        self,
        job: Job,
        source: bytes,
        source_type: str,
        prompt: str,
        negative_prompt: str,
        parameters: dict[str, int | float],
    ) -> None:
        job.state = "running"
        self._update_job_progress(job, phase="running", message="starting image job")
        listener: ComfyProgressListener | None = None
        try:
            self._update_job_progress(job, phase="uploading", message="uploading source image")
            source_name = self.comfy.upload(
                f"{job.id}-source{ALLOWED_INPUT_TYPES[source_type]}", source, source_type
            )
            template = json.loads(self.image_workflow_path.read_text(encoding="utf-8"))
            workflow = render_workflow(
                template,
                source_name,
                "",
                parameters,
                extra_replacements={
                    "{{PROMPT}}": prompt,
                    "{{NEGATIVE_PROMPT}}": negative_prompt,
                },
            )
            self._update_job_progress(job, phase="submitting", message="submitting ComfyUI prompt")
            job.prompt_id = self.comfy.submit(workflow, job.id)
            listener = ComfyProgressListener(
                self.comfy.base_url,
                job.id,
                job.prompt_id,
                workflow,
                lambda updates, current_job=job: self._update_job_progress(current_job, **updates),
            )
            listener.start()
            self._update_job_progress(
                job,
                phase="waiting",
                message=f"queued prompt {job.prompt_id}",
                last_event="prompt_submitted",
            )
            while True:
                if job.cancel_requested:
                    self.comfy.interrupt()
                    job.state = "cancelled"
                    self._update_job_progress(job, phase="cancelled", message="job cancelled")
                    return
                try:
                    queue_updates = queue_state_for_prompt(self.comfy.queue(), job.prompt_id)
                    if queue_updates:
                        self._update_job_progress(job, log=False, **queue_updates)
                except AdapterError:
                    pass
                descriptor = find_image_output(self.comfy.history(job.prompt_id), job.prompt_id)
                if descriptor is not None:
                    self._update_job_progress(job, phase="encoding", message="downloading ComfyUI output")
                    output = self.comfy.download_output(descriptor)
                    if not output:
                        raise AdapterError("ComfyUI returned an empty image")
                    output_path = self.jobs_root / job.id / job.output_filename
                    output_path.write_bytes(output)
                    job.output_path = str(output_path)
                    job.state = "completed"
                    self._update_job_progress(job, phase="completed", message="image ready", percent=100.0)
                    return
                time.sleep(self.poll_interval)
        except Exception as exc:
            job.state = "failed"
            job.error = str(exc)
            self._update_job_progress(job, phase="failed", message=str(exc))
        finally:
            if listener is not None:
                listener.stop()
            job.updated_at = time.time()

    def submit_image_generate_job(
        self,
        prompt: str,
        output_filename: str,
        workflow_version: str,
        parameters: dict[str, Any],
        negative_prompt: str = " ",
    ) -> Job:
        if workflow_version != IMAGE_GENERATE_WORKFLOW_VERSION:
            raise AdapterError(f"unsupported workflow_version: {workflow_version}", 422)
        if not prompt.strip():
            raise AdapterError("prompt must be non-empty", 422)
        output_filename = safe_image_output_filename(output_filename)
        parameters = validate_image_parameters(parameters)
        ready, details = self.image_generate_readiness()
        if not ready:
            raise AdapterError(f"image generate backend is not ready: {details['missing']}", 503)
        job = Job(id=uuid.uuid4().hex, output_filename=output_filename)
        job_dir = self.jobs_root / job.id
        job_dir.mkdir(mode=0o700)
        with self._lock:
            self.jobs[job.id] = job
        threading.Thread(
            target=self._image_generate_job_worker,
            args=(job, prompt, negative_prompt, parameters),
            daemon=True,
        ).start()
        return job

    def _image_generate_job_worker(
        self,
        job: Job,
        prompt: str,
        negative_prompt: str,
        parameters: dict[str, int | float],
    ) -> None:
        job.state = "running"
        self._update_job_progress(job, phase="running", message="starting image generate job")
        listener: ComfyProgressListener | None = None
        try:
            template = json.loads(self.image_generate_workflow_path.read_text(encoding="utf-8"))
            workflow = render_workflow(
                template,
                "",
                "",
                parameters,
                extra_replacements={
                    "{{PROMPT}}": prompt,
                    "{{NEGATIVE_PROMPT}}": negative_prompt,
                },
            )
            self._update_job_progress(job, phase="submitting", message="submitting ComfyUI prompt")
            job.prompt_id = self.comfy.submit(workflow, job.id)
            listener = ComfyProgressListener(
                self.comfy.base_url,
                job.id,
                job.prompt_id,
                workflow,
                lambda updates, current_job=job: self._update_job_progress(current_job, **updates),
            )
            listener.start()
            self._update_job_progress(
                job,
                phase="waiting",
                message=f"queued prompt {job.prompt_id}",
                last_event="prompt_submitted",
            )
            while True:
                if job.cancel_requested:
                    self.comfy.interrupt()
                    job.state = "cancelled"
                    self._update_job_progress(job, phase="cancelled", message="job cancelled")
                    return
                try:
                    queue_updates = queue_state_for_prompt(self.comfy.queue(), job.prompt_id)
                    if queue_updates:
                        self._update_job_progress(job, log=False, **queue_updates)
                except AdapterError:
                    pass
                descriptor = find_image_output(self.comfy.history(job.prompt_id), job.prompt_id)
                if descriptor is not None:
                    self._update_job_progress(job, phase="encoding", message="downloading ComfyUI output")
                    output = self.comfy.download_output(descriptor)
                    if not output:
                        raise AdapterError("ComfyUI returned an empty image")
                    output_path = self.jobs_root / job.id / job.output_filename
                    output_path.write_bytes(output)
                    job.output_path = str(output_path)
                    job.state = "completed"
                    self._update_job_progress(job, phase="completed", message="image ready", percent=100.0)
                    return
                time.sleep(self.poll_interval)
        except Exception as exc:
            job.state = "failed"
            job.error = str(exc)
            self._update_job_progress(job, phase="failed", message=str(exc))
        finally:
            if listener is not None:
                listener.stop()
            job.updated_at = time.time()

    def get_job(self, job_id: str) -> Job:
        validate_identifier(job_id, "job")
        with self._lock:
            job = self.jobs.get(job_id)
        if job is None:
            raise AdapterError("job not found", 404)
        return job

    def cancel_job(self, job_id: str) -> Job:
        job = self.get_job(job_id)
        if job.state in TERMINAL_STATES:
            raise AdapterError(f"cannot cancel a {job.state} job", 409)
        job.cancel_requested = True
        job.updated_at = time.time()
        return job

    def open_result(self, job_id: str) -> tuple[Path, str]:
        job = self.get_job(job_id)
        if job.state != "completed" or job.output_path is None:
            raise AdapterError("job result is not available", 409)
        path = Path(job.output_path)
        expected_parent = (self.jobs_root / job.id).resolve()
        if path.resolve().parent != expected_parent or not path.is_file():
            raise AdapterError("job result is unavailable", 500)
        return path, job.output_filename

