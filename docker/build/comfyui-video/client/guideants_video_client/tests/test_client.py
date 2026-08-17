from __future__ import annotations

import io
from pathlib import Path

import pytest

import guideants_video_client.client as client_module
from guideants_video_client import (
    VideoClientError,
    cancel_talking_head_job,
    get_talking_head_job,
    materialize_talking_head_result,
    submit_image_edit,
    submit_image_generate,
    submit_talking_head,
    submit_talking_head_v2v,
)

JOB_ID = "0123456789abcdef0123456789abcdef"


def make_notebook(tmp_path: Path) -> tuple[Path, Path, Path]:
    notebook = tmp_path / "notebook"
    input_dir = notebook / "Input"
    output_dir = notebook / "Output"
    marker_dir = notebook / ".guideants"
    input_dir.mkdir(parents=True)
    output_dir.mkdir()
    marker_dir.mkdir()
    (marker_dir / "notebook.json").write_text("{}", encoding="utf-8")
    return notebook, input_dir, output_dir


def test_resolve_notebook_paths_blocks_escape_and_symlink(tmp_path: Path) -> None:
    notebook, _input_dir, output_dir = make_notebook(tmp_path)
    outside = tmp_path / "outside.wav"
    outside.write_bytes(b"audio")
    with pytest.raises(VideoClientError, match="escapes"):
        client_module.resolve_notebook_path("../../outside.wav", output_dir, must_exist=True)

    link = output_dir / "linked.wav"
    try:
        link.symlink_to(outside)
    except OSError:
        return
    with pytest.raises(VideoClientError, match="escapes"):
        client_module.resolve_notebook_path(link, output_dir, must_exist=True)


def test_requires_notebook_marker(tmp_path: Path) -> None:
    output_dir = tmp_path / "Output"
    output_dir.mkdir()
    with pytest.raises(VideoClientError, match=r"\.guideants/notebook\.json"):
        client_module.resolve_notebook_path("result.mkv", output_dir, must_exist=False)


def test_submit_streams_scoped_files(monkeypatch: pytest.MonkeyPatch, tmp_path: Path) -> None:
    _notebook, input_dir, output_dir = make_notebook(tmp_path)
    (input_dir / "avatar.png").write_bytes(b"png")
    (input_dir / "voice.wav").write_bytes(b"wav")
    captured: dict = {}

    def fake_request(method: str, path: str, **kwargs: object) -> dict:
        captured.update({"method": method, "path": path, **kwargs})
        return {"jobId": JOB_ID, "state": "queued"}

    monkeypatch.setattr(client_module, "_request", fake_request)
    result = submit_talking_head(
        image_path="../Input/avatar.png",
        audio_path="../Input/voice.wav",
        output_filename="output.mkv",
        workflow="infinitetalk-i2v-v1",
        working_directory=output_dir,
        parameters={"fps": 25},
        positive_prompt="A man teaching | A man talking",
        negative_prompt="head bobbing",
    )
    assert result["jobId"] == JOB_ID
    assert captured["path"] == "/v1/talking-head/jobs"
    body = captured["body"]
    assert isinstance(body, bytes)
    assert b'infinitetalk-i2v-v1' in body
    assert b'"fps":25' in body
    assert b"A man teaching | A man talking" in body
    assert b"head bobbing" in body
    assert b"png" in body and b"wav" in body


def test_submit_v2v_streams_scoped_files(monkeypatch: pytest.MonkeyPatch, tmp_path: Path) -> None:
    _notebook, input_dir, output_dir = make_notebook(tmp_path)
    (input_dir / "driver.mkv").write_bytes(b"mkv")
    (input_dir / "voice.wav").write_bytes(b"wav")
    captured: dict = {}

    def fake_request(method: str, path: str, **kwargs: object) -> dict:
        captured.update({"method": method, "path": path, **kwargs})
        return {"jobId": JOB_ID, "state": "queued"}

    monkeypatch.setattr(client_module, "_request", fake_request)
    result = submit_talking_head_v2v(
        video_path="../Input/driver.mkv",
        audio_path="../Input/voice.wav",
        output_filename="output.mkv",
        working_directory=output_dir,
        parameters={"fps": 25},
        positive_prompt="A man teaching | A man talking",
        negative_prompt="head bobbing",
    )
    assert result["jobId"] == JOB_ID
    assert captured["path"] == "/v1/talking-head/jobs"
    body = captured["body"]
    assert isinstance(body, bytes)
    assert b"infinitetalk-v2v-v1" in body
    assert b'"fps":25' in body
    assert b"mkv" in body and b"wav" in body


def test_status_and_cancel_require_hex_uuid(monkeypatch: pytest.MonkeyPatch) -> None:
    calls: list[tuple[str, str]] = []

    def fake_request(method: str, path: str, **_kwargs: object) -> dict:
        calls.append((method, path))
        return {"jobId": JOB_ID}

    monkeypatch.setattr(client_module, "_request", fake_request)
    assert get_talking_head_job(JOB_ID)["jobId"] == JOB_ID
    assert cancel_talking_head_job(JOB_ID)["jobId"] == JOB_ID
    assert calls == [
        ("GET", f"/v1/talking-head/jobs/{JOB_ID}"),
        ("POST", f"/v1/talking-head/jobs/{JOB_ID}/cancel"),
    ]
    for invalid in ("abc", "../abc", "01234567-89ab-cdef-0123-456789abcdef"):
        with pytest.raises(VideoClientError, match="hexadecimal UUID"):
            get_talking_head_job(invalid)


def test_materialize_is_atomic_and_scoped(
    monkeypatch: pytest.MonkeyPatch, tmp_path: Path
) -> None:
    _notebook, _input_dir, output_dir = make_notebook(tmp_path)

    def fake_request(method: str, path: str, **kwargs: object) -> None:
        output = kwargs["output"]
        assert isinstance(output, io.BufferedWriter)
        output.write(b"mkv-data")

    monkeypatch.setattr(client_module, "_request", fake_request)
    result = materialize_talking_head_result(
        JOB_ID, "result.mkv", working_directory=output_dir
    )
    result_path = output_dir / "result.mkv"
    assert result == {
        "jobId": JOB_ID,
        "outputPath": str(result_path),
        "bytes": 8,
    }
    assert result_path.read_bytes() == b"mkv-data"
    assert list(output_dir.glob("*.part")) == []

    with pytest.raises(VideoClientError, match=r"end in \.mkv"):
        materialize_talking_head_result(
            JOB_ID, "lossy.mp4", working_directory=output_dir
        )
    with pytest.raises(VideoClientError, match="escapes"):
        materialize_talking_head_result(
            JOB_ID, "../../outside.mkv", working_directory=output_dir
        )


def test_submit_image_edit_streams_scoped_file(
    monkeypatch: pytest.MonkeyPatch, tmp_path: Path
) -> None:
    _notebook, input_dir, output_dir = make_notebook(tmp_path)
    (input_dir / "office.png").write_bytes(b"png")
    captured: dict = {}

    def fake_request(method: str, path: str, **kwargs: object) -> dict:
        captured.update({"method": method, "path": path, **kwargs})
        return {"jobId": JOB_ID, "state": "queued"}

    monkeypatch.setattr(client_module, "_request", fake_request)
    result = submit_image_edit(
        image_path="../Input/office.png",
        prompt="complete the scene",
        output_filename="edited.png",
        working_directory=output_dir,
        parameters={"steps": 4, "cfg": 1.0},
    )
    assert result["jobId"] == JOB_ID
    assert captured["path"] == "/v1/image/jobs"
    body = captured["body"]
    assert isinstance(body, bytes)
    assert b"qwen-image-edit-v1" in body
    assert b"complete the scene" in body
    assert b'"steps":4' in body
    assert b"png" in body


def test_submit_image_edit_accepts_20_step_workflow(
    monkeypatch: pytest.MonkeyPatch, tmp_path: Path
) -> None:
    _notebook, input_dir, output_dir = make_notebook(tmp_path)
    (input_dir / "office.png").write_bytes(b"png")
    captured: dict = {}

    def fake_request(method: str, path: str, **kwargs: object) -> dict:
        captured.update({"method": method, "path": path, **kwargs})
        return {"jobId": JOB_ID, "state": "queued"}

    monkeypatch.setattr(client_module, "_request", fake_request)
    result = submit_image_edit(
        image_path="../Input/office.png",
        prompt="restyle carefully",
        output_filename="edited20.png",
        workflow="qwen-image-edit-20-v1",
        working_directory=output_dir,
        parameters={"denoise": 0.8, "megapixels": 2.0},
    )
    assert result["jobId"] == JOB_ID
    body = captured["body"]
    assert isinstance(body, bytes)
    assert b"qwen-image-edit-20-v1" in body
    assert b'"denoise":0.8' in body
    assert b'"megapixels":2.0' in body


def test_submit_image_edit_accepts_bf16_workflow(
    monkeypatch: pytest.MonkeyPatch, tmp_path: Path
) -> None:
    _notebook, input_dir, output_dir = make_notebook(tmp_path)
    (input_dir / "office.png").write_bytes(b"png")
    captured: dict = {}

    def fake_request(method: str, path: str, **kwargs: object) -> dict:
        captured.update({"method": method, "path": path, **kwargs})
        return {"jobId": JOB_ID, "state": "queued"}

    monkeypatch.setattr(client_module, "_request", fake_request)
    result = submit_image_edit(
        image_path="../Input/office.png",
        prompt="restyle bf16",
        output_filename="edited-bf16.png",
        workflow="qwen-image-edit-bf16-v1",
        working_directory=output_dir,
        parameters={"cfg": 4.0, "denoise": 0.8},
    )
    assert result["jobId"] == JOB_ID
    body = captured["body"]
    assert isinstance(body, bytes)
    assert b"qwen-image-edit-bf16-v1" in body
    assert b'"cfg":4.0' in body
    assert b'"denoise":0.8' in body


def test_submit_image_edit_accepts_bf16_inpaint_workflow(
    monkeypatch: pytest.MonkeyPatch, tmp_path: Path
) -> None:
    _notebook, input_dir, output_dir = make_notebook(tmp_path)
    (input_dir / "masked.png").write_bytes(b"png")
    (input_dir / "mask.png").write_bytes(b"mask")
    captured: dict = {}

    def fake_request(method: str, path: str, **kwargs: object) -> dict:
        captured.update({"method": method, "path": path, **kwargs})
        return {"jobId": JOB_ID, "state": "queued"}

    monkeypatch.setattr(client_module, "_request", fake_request)
    result = submit_image_edit(
        image_path="../Input/masked.png",
        prompt="complete the alpha mask",
        output_filename="completed.png",
        workflow="qwen-image-edit-bf16-inpaint-v1",
        mask_path="../Input/mask.png",
        working_directory=output_dir,
        parameters={"steps": 4, "cfg": 1.0, "denoise": 1.0, "lora_strength": 1.0},
    )
    assert result["jobId"] == JOB_ID
    body = captured["body"]
    assert isinstance(body, bytes)
    assert b"qwen-image-edit-bf16-inpaint-v1" in body
    assert b'"denoise":1.0' in body
    assert b'name="mask"' in body


def test_submit_image_generate_posts_form_without_source(
    monkeypatch: pytest.MonkeyPatch, tmp_path: Path
) -> None:
    _notebook, _input_dir, output_dir = make_notebook(tmp_path)
    captured: dict = {}

    def fake_request(method: str, path: str, **kwargs: object) -> dict:
        captured.update({"method": method, "path": path, **kwargs})
        return {"jobId": JOB_ID, "state": "queued"}

    monkeypatch.setattr(client_module, "_request", fake_request)
    result = submit_image_generate(
        prompt="a futuristic CPU on a motherboard",
        output_filename="generated.png",
        working_directory=output_dir,
        parameters={"steps": 4, "cfg": 1.0},
    )
    assert result["jobId"] == JOB_ID
    assert captured["path"] == "/v1/image/generate/jobs"
    body = captured["body"]
    assert isinstance(body, bytes)
    assert b"qwen-image-v1" in body
    assert b"a futuristic CPU on a motherboard" in body
    assert b'"steps":4' in body
    assert b'filename="' not in body

