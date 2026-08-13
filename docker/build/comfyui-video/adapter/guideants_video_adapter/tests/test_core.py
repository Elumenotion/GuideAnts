from __future__ import annotations

import hashlib
import io
import json
import threading
import wave
from pathlib import Path

import pytest
import guideants_video_adapter.core as core

from guideants_video_adapter.core import (
    AdapterError,
    AdapterService,
    MAX_AUDIO_BYTES,
    WORKFLOW_VERSION,
    find_video_output,
    probe_audio_duration_seconds,
    render_workflow,
    resolve_workflow_parameters,
    safe_output_filename,
    validate_parameters,
)


def make_wav(seconds: float, *, sample_rate: int = 24_000) -> bytes:
    buffer = io.BytesIO()
    frame_count = max(1, int(seconds * sample_rate))
    with wave.open(buffer, "wb") as wav_file:
        wav_file.setnchannels(1)
        wav_file.setsampwidth(2)
        wav_file.setframerate(sample_rate)
        wav_file.writeframes(b"\x00\x00" * frame_count)
    return buffer.getvalue()

class FakeComfy:
    def __init__(self) -> None:
        self.base_url = "http://127.0.0.1:8188"
        self.submitted: dict | None = None
        self.prompt_id = "prompt-1"
        self.history_ready = threading.Event()
        self.interrupted = False

    def system_stats(self) -> dict:
        return {"devices": [{"name": "Fake GPU", "type": "cuda"}]}

    def object_info(self) -> dict:
        return {"InfiniteTalk": {}}

    def upload(self, filename: str, data: bytes, content_type: str) -> str:
        assert data
        assert content_type
        return f"uploaded/{filename}"

    def submit(self, workflow: dict, client_id: str) -> str:
        self.submitted = workflow
        return self.prompt_id

    def history(self, prompt_id: str) -> dict:
        if not self.history_ready.is_set():
            return {}
        return {
            prompt_id: {
                "status": {"status_str": "success"},
                "outputs": {"9": {"videos": [{"filename": "generated.mp4", "type": "output"}]}},
            }
        }

    def queue(self) -> dict:
        return {"queue_running": [], "queue_pending": []}

    def download_output(self, descriptor: dict) -> bytes:
        return b"fake-mp4"

    def interrupt(self) -> None:
        self.interrupted = True


@pytest.fixture
def service(tmp_path: Path) -> tuple[AdapterService, FakeComfy]:
    models = tmp_path / "models"
    artifact = models / "infinitetalk" / "model.bin"
    artifact.parent.mkdir(parents=True)
    artifact.write_bytes(b"model")
    digest = hashlib.sha256(artifact.read_bytes()).hexdigest()
    manifest = tmp_path / "manifest.json"
    manifest.write_text(
        json.dumps(
            {
                "bundles": {
                    WORKFLOW_VERSION: {
                        "artifacts": [
                            {
                                "path": "infinitetalk/model.bin",
                                "size": 5,
                                "sha256": digest,
                                "url": "https://invalid.example/model.bin",
                            }
                        ]
                    }
                }
            }
        ),
        encoding="utf-8",
    )
    workflow = tmp_path / "workflow.json"
    workflow.write_text(
        json.dumps(
            {
                "1": {"inputs": {"image": "{{INPUT_IMAGE}}", "audio": "{{INPUT_AUDIO}}"}},
                "2": {"inputs": {"width": "{{WIDTH}}", "frames": "{{FRAMES}}"}},
            }
        ),
        encoding="utf-8",
    )
    comfy = FakeComfy()
    return (
        AdapterService(
            tmp_path / "jobs",
            models,
            workflow,
            manifest,
            comfy,  # type: ignore[arg-type]
            poll_interval=0.001,
        ),
        comfy,
    )


def test_rejects_paths_and_unknown_parameters() -> None:
    with pytest.raises(AdapterError, match="filename, not a path"):
        safe_output_filename("../outside.mp4")
    with pytest.raises(AdapterError, match="unsupported workflow parameters"):
        validate_parameters({"custom_node": "anything"})


def test_model_readiness_does_not_rehash_installed_artifacts(
    service: tuple[AdapterService, FakeComfy], monkeypatch: pytest.MonkeyPatch
) -> None:
    adapter, _comfy = service
    artifact = adapter.models_root / "infinitetalk" / "model.bin"

    def unexpected_sha256_file(_path: Path) -> str:
        raise AssertionError("readiness must not hash installed models")

    monkeypatch.setattr(core, "_sha256_file", unexpected_sha256_file)
    assert adapter.models()["ready"] is True
    assert adapter.models()["ready"] is True
    artifact.unlink()
    assert adapter.models()["ready"] is False


def test_rejects_oversized_payload(
    service: tuple[AdapterService, FakeComfy],
) -> None:
    adapter, _comfy = service
    with pytest.raises(AdapterError, match="audio exceeds"):
        adapter.submit_job(
            b"image",
            "image/png",
            b"x" * (MAX_AUDIO_BYTES + 1),
            "audio/wav",
            "answer.mp4",
            WORKFLOW_VERSION,
            {},
        )


def test_probe_audio_duration_seconds_reads_wav_length() -> None:
    audio = make_wav(10.0)
    duration = probe_audio_duration_seconds(audio, "audio/wav")
    assert duration == pytest.approx(10.0, abs=1 / 24_000)


def test_resolve_workflow_parameters_derives_frames_from_audio() -> None:
    audio = make_wav(10.069333)
    parameters = resolve_workflow_parameters({"fps": 25}, audio, "audio/wav")
    assert parameters["frames"] == 252
    assert parameters["fps"] == 25


def test_resolve_workflow_parameters_keeps_explicit_frames() -> None:
    audio = make_wav(10.0)
    parameters = resolve_workflow_parameters({"fps": 25, "frames": 100}, audio, "audio/wav")
    assert parameters["frames"] == 100


def test_v1_rejects_video_and_reports_image_only(
    service: tuple[AdapterService, FakeComfy],
) -> None:
    adapter, _comfy = service
    assert adapter.capabilities()["input_kinds"] == ["image"]
    with pytest.raises(AdapterError, match="unsupported source media type"):
        adapter.submit_job(
            b"video",
            "video/mp4",
            make_wav(1.0),
            "audio/wav",
            "answer.mp4",
            WORKFLOW_VERSION,
            {},
        )


def test_renders_only_declared_placeholders() -> None:
    rendered = render_workflow(
        {"node": {"inputs": {"image": "{{INPUT_IMAGE}}", "fps": "{{FPS}}"}}},
        "uploaded.png",
        "uploaded.wav",
        validate_parameters({"fps": 30}),
    )
    assert rendered["node"]["inputs"] == {"image": "uploaded.png", "fps": 30}


def test_find_video_output_rejects_comfy_error() -> None:
    history = {"p": {"status": {"status_str": "error", "messages": ["bad node"]}}}
    with pytest.raises(AdapterError, match="bad node"):
        find_video_output(history, "p")


def test_job_uses_private_directory_and_materializes_result(
    service: tuple[AdapterService, FakeComfy],
) -> None:
    adapter, comfy = service
    audio = make_wav(5.0)
    job = adapter.submit_job(
        b"image",
        "image/png",
        audio,
        "audio/wav",
        "answer.mp4",
        WORKFLOW_VERSION,
        {"fps": 30},
    )
    comfy.history_ready.set()
    for _ in range(1000):
        if adapter.get_job(job.id).state == "completed":
            break
        threading.Event().wait(0.001)
    completed = adapter.get_job(job.id)
    assert completed.state == "completed"
    assert completed.progress["phase"] == "completed"
    result, filename = adapter.open_result(job.id)
    assert filename == "answer.mp4"
    assert result.parent == adapter.jobs_root / job.id
    assert result.read_bytes() == b"fake-mp4"
    assert comfy.submitted is not None
    assert comfy.submitted["2"]["inputs"]["width"] == 832
    assert comfy.submitted["2"]["inputs"]["frames"] == 150


def test_image_edit_job_materializes_png(
    service: tuple[AdapterService, FakeComfy], tmp_path: Path
) -> None:
    adapter, comfy = service
    image_workflow = adapter.image_workflow_path
    image_workflow.write_text(
        json.dumps(
            {
                "1": {
                    "class_type": "TextEncodeQwenImageEditPlus",
                    "inputs": {"prompt": "{{PROMPT}}", "image1": "{{INPUT_IMAGE}}"},
                },
                "2": {"class_type": "SaveImage", "inputs": {"steps": "{{STEPS}}"}},
            }
        ),
        encoding="utf-8",
    )
    artifact = adapter.models_root / "diffusion_models" / "qwen_edit.safetensors"
    artifact.parent.mkdir(parents=True, exist_ok=True)
    artifact.write_bytes(b"qwen")
    manifest = json.loads(adapter.manifest_path.read_text(encoding="utf-8"))
    manifest["bundles"]["qwen-image-edit-v1"] = {
        "artifacts": [
            {
                "path": "diffusion_models/qwen_edit.safetensors",
                "size": 4,
                "sha256": "0" * 64,
                "url": "https://huggingface.co/example/resolve/abc/file.safetensors",
            }
        ]
    }
    adapter.manifest_path.write_text(json.dumps(manifest), encoding="utf-8")
    comfy.object_info = lambda: {
        "TextEncodeQwenImageEditPlus": {},
        "FluxKontextMultiReferenceLatentMethod": {},
        "CFGNorm": {},
        "ImageScaleToTotalPixels": {},
        "SaveImage": {},
    }  # type: ignore[method-assign]
    original_history = comfy.history

    def image_history(prompt_id: str) -> dict:
        if not comfy.history_ready.is_set():
            return {}
        return {
            prompt_id: {
                "status": {"status_str": "success"},
                "outputs": {"13": {"images": [{"filename": "generated.png", "type": "output"}]}},
            }
        }

    comfy.history = image_history  # type: ignore[method-assign]
    comfy.download_output = lambda descriptor: b"fake-png"  # type: ignore[method-assign]
    job = adapter.submit_image_job(
        b"image",
        "image/png",
        "complete the scene",
        "edited.png",
        "qwen-image-edit-v1",
        {"steps": 4, "cfg": 1.0},
    )
    comfy.history_ready.set()
    for _ in range(1000):
        if adapter.get_job(job.id).state == "completed":
            break
        threading.Event().wait(0.001)
    completed = adapter.get_job(job.id)
    assert completed.state == "completed"
    result, filename = adapter.open_result(job.id)
    assert filename == "edited.png"
    assert result.read_bytes() == b"fake-png"
    assert comfy.submitted is not None
    assert comfy.submitted["1"]["inputs"]["prompt"] == "complete the scene"
    assert comfy.submitted["2"]["inputs"]["steps"] == 4
    comfy.history = original_history


def test_image_generate_job_materializes_png(
    service: tuple[AdapterService, FakeComfy],
) -> None:
    adapter, comfy = service
    generate_workflow = adapter.image_generate_workflow_path
    generate_workflow.write_text(
        json.dumps(
            {
                "1": {
                    "class_type": "CLIPTextEncode",
                    "inputs": {"text": "{{PROMPT}}"},
                },
                "2": {"class_type": "SaveImage", "inputs": {"steps": "{{STEPS}}"}},
            }
        ),
        encoding="utf-8",
    )
    artifact = adapter.models_root / "diffusion_models" / "qwen_image.safetensors"
    artifact.parent.mkdir(parents=True, exist_ok=True)
    artifact.write_bytes(b"qwen")
    manifest = json.loads(adapter.manifest_path.read_text(encoding="utf-8"))
    manifest["bundles"]["qwen-image-v1"] = {
        "artifacts": [
            {
                "path": "diffusion_models/qwen_image.safetensors",
                "size": 4,
                "sha256": "0" * 64,
                "url": "https://huggingface.co/example/resolve/abc/file.safetensors",
            }
        ]
    }
    adapter.manifest_path.write_text(json.dumps(manifest), encoding="utf-8")
    comfy.object_info = lambda: {"CLIPTextEncode": {}, "SaveImage": {}}  # type: ignore[method-assign]

    def image_history(prompt_id: str) -> dict:
        if not comfy.history_ready.is_set():
            return {}
        return {
            prompt_id: {
                "status": {"status_str": "success"},
                "outputs": {"60": {"images": [{"filename": "generated.png", "type": "output"}]}},
            }
        }

    comfy.history = image_history  # type: ignore[method-assign]
    comfy.download_output = lambda descriptor: b"fake-gen-png"  # type: ignore[method-assign]
    job = adapter.submit_image_generate_job(
        "a futuristic CPU on a motherboard",
        "generated.png",
        "qwen-image-v1",
        {"steps": 4, "cfg": 1.0},
    )
    comfy.history_ready.set()
    for _ in range(1000):
        if adapter.get_job(job.id).state == "completed":
            break
        threading.Event().wait(0.001)
    completed = adapter.get_job(job.id)
    assert completed.state == "completed"
    result, filename = adapter.open_result(job.id)
    assert filename == "generated.png"
    assert result.read_bytes() == b"fake-gen-png"
    assert comfy.submitted is not None
    assert comfy.submitted["1"]["inputs"]["text"] == "a futuristic CPU on a motherboard"
    assert comfy.submitted["2"]["inputs"]["steps"] == 4


def test_readiness_fails_clearly_when_comfy_is_unavailable(
    service: tuple[AdapterService, FakeComfy],
) -> None:
    adapter, comfy = service

    def unavailable() -> dict:
        raise AdapterError("connection refused")

    comfy.system_stats = unavailable  # type: ignore[method-assign]
    ready, details = adapter.readiness()
    assert not ready
    assert details["missing"] == ["comfyui: connection refused"]

