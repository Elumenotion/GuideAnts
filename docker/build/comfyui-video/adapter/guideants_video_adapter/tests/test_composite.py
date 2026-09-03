"""Tests for the composite stdout telemetry contract.

These tests ARE the spec for run-corridorkey-composite.py progress lines.
When adding a stage or changing log format: add an example line here first, then
implement telemetry() output to match, then run pytest before long pipeline jobs.
"""

from __future__ import annotations

import importlib.util
import subprocess
import sys
from pathlib import Path

from guideants_video_adapter.composite import (
    CompositeError,
    clip_workspace_for_output,
    composite_progress_kwargs,
    composite_ready,
    existing_clip_root_for_retry,
    parse_composite_telemetry_line,
    run_corridorkey_composite,
)


def test_composite_ready_reports_missing(tmp_path: Path, monkeypatch) -> None:
    monkeypatch.setenv("VIDEO_CORRIDORKEY_ROOT", str(tmp_path / "missing-root"))
    monkeypatch.setenv("VIDEO_COMPOSITE_SCRIPT", str(tmp_path / "missing.py"))
    ok, missing = composite_ready()
    assert ok is False
    assert "composite_script" in missing
    assert "corridorkey_root" in missing


def test_composite_ready_reports_missing_basicvsrpp_checkpoint(
    tmp_path: Path, monkeypatch
) -> None:
    root = tmp_path / "CorridorKey"
    checkpoint = root / "CorridorKeyModule" / "checkpoints"
    checkpoint.mkdir(parents=True)
    (checkpoint / "CorridorKey_v1.0.safetensors").write_bytes(b"ckpt")
    script = tmp_path / "run-corridorkey-composite.py"
    script.write_text("# stub\n", encoding="utf-8")
    monkeypatch.setenv("VIDEO_CORRIDORKEY_ROOT", str(root))
    monkeypatch.setenv("VIDEO_COMPOSITE_SCRIPT", str(script))
    monkeypatch.setenv("VIDEO_COMPOSITE_FG_UPSCALER", "basicvsrpp")
    monkeypatch.setenv("VIDEO_BASICVSRPP_CHECKPOINT_DIR", str(tmp_path / "empty"))
    vsr_venv = tmp_path / "venv-basicvsrpp"
    (vsr_venv / "lib" / "python3.12" / "site-packages").mkdir(parents=True)
    monkeypatch.setenv("VIDEO_BASICVSRPP_VENV", str(vsr_venv))
    ok, missing = composite_ready()
    assert ok is False
    assert "basicvsrpp_checkpoint" in missing


def test_composite_ready_reports_missing_basicvsrpp_venv(
    tmp_path: Path, monkeypatch
) -> None:
    root = tmp_path / "CorridorKey"
    checkpoint = root / "CorridorKeyModule" / "checkpoints"
    checkpoint.mkdir(parents=True)
    (checkpoint / "CorridorKey_v1.0.safetensors").write_bytes(b"ckpt")
    script = tmp_path / "run-corridorkey-composite.py"
    script.write_text("# stub\n", encoding="utf-8")
    models = tmp_path / "models"
    models.mkdir()
    for name in (
        "basicvsr_plusplus_c64n7_8x1_300k_vimeo90k_bd_20210305-ab315ab1.pth",
        "spynet_20210409-c6c1bd09.pth",
    ):
        (models / name).write_bytes(b"ckpt")
    monkeypatch.setenv("VIDEO_CORRIDORKEY_ROOT", str(root))
    monkeypatch.setenv("VIDEO_COMPOSITE_SCRIPT", str(script))
    monkeypatch.setenv("VIDEO_COMPOSITE_FG_UPSCALER", "basicvsrpp")
    monkeypatch.setenv("VIDEO_BASICVSRPP_CHECKPOINT_DIR", str(models))
    monkeypatch.setenv("VIDEO_BASICVSRPP_VENV", str(tmp_path / "missing-venv"))
    ok, missing = composite_ready()
    assert ok is False
    assert "basicvsrpp_venv" in missing


def test_run_corridorkey_composite_wraps_telemetry_wrapper(tmp_path: Path, monkeypatch) -> None:
    root = tmp_path / "CorridorKey"
    checkpoint = root / "CorridorKeyModule" / "checkpoints"
    checkpoint.mkdir(parents=True)
    (checkpoint / "CorridorKey_v1.0.safetensors").write_bytes(b"ckpt")
    script = tmp_path / "run-corridorkey-composite.py"
    script.write_text("# stub\n", encoding="utf-8")
    wrapper = tmp_path / "exec-composite-telemetry.sh"
    wrapper.write_text("#!/bin/sh\nexec \"$@\"\n", encoding="utf-8")
    source = tmp_path / "green.mkv"
    plate = tmp_path / "plate.png"
    output = tmp_path / "out.mp4"
    source.write_bytes(b"mkv")
    plate.write_bytes(b"png")

    monkeypatch.setenv("VIDEO_COMPOSITE_TELEMETRY_WRAPPER", str(wrapper))
    monkeypatch.setenv("VIDEO_COMPOSITE_FG_UPSCALER", "basicvsrpp")
    monkeypatch.setenv("VIDEO_BASICVSRPP_CHECKPOINT_DIR", str(tmp_path / "models"))
    vsr_venv = tmp_path / "venv-basicvsrpp"
    (vsr_venv / "lib" / "python3.12" / "site-packages").mkdir(parents=True)
    monkeypatch.setenv("VIDEO_BASICVSRPP_VENV", str(vsr_venv))
    (tmp_path / "models").mkdir()
    for name in (
        "basicvsr_plusplus_c64n7_8x1_300k_vimeo90k_bd_20210305-ab315ab1.pth",
        "spynet_20210409-c6c1bd09.pth",
    ):
        (tmp_path / "models" / name).write_bytes(b"ckpt")

    captured: list[list[str]] = []

    def runner(command: list[str]) -> None:
        captured.append(command)
        output.write_bytes(b"mp4")

    run_corridorkey_composite(
        source=source,
        plate=plate,
        output=output,
        corridorkey_root=root,
        script_path=script,
        runner=runner,
        fg_upscaler="basicvsrpp",
        basicvsrpp_checkpoint_dir=str(tmp_path / "models"),
    )
    assert captured[0][0] == "/bin/bash"
    assert captured[0][1] == str(wrapper)
    assert str(script) in captured[0]
    assert "--fg-upscaler" in captured[0]
    assert "basicvsrpp" in captured[0]
    assert "--avatar-upscaler" not in captured[0]


def test_parse_composite_telemetry_line_fatal() -> None:
    updates = parse_composite_telemetry_line(
        "[06:59:30] elapsed=   900.0s FATAL stage=post_ck error=RuntimeError: ffmpeg encoder exited"
    )
    assert updates is not None
    assert updates["stage"] == "failed"
    assert "FATAL" in updates["message"]


def test_stderr_detail_filters_miopen_noise() -> None:
    from guideants_video_adapter.composite import _stderr_detail

    detail = _stderr_detail("MIOpen warning: foo\nRuntimeError: boom\n")
    assert "RuntimeError" in detail
    assert "MIOpen" not in detail


def test_parse_composite_telemetry_line_frames_ratio() -> None:
    updates = parse_composite_telemetry_line(
        "[14:08:58] elapsed=   120.5s CorridorKey frames=120/264 rate=2.10fps eta=68.5s"
    )
    assert updates is not None
    assert updates["message"].startswith("CorridorKey frames=120/264")
    assert updates["step"] == 120
    assert updates["max_steps"] == 264
    assert updates["percent"] == 45.5


def test_parse_composite_telemetry_line_start_clip() -> None:
    updates = parse_composite_telemetry_line(
        "[14:08:58] elapsed=    24.4s CorridorKey start clip=talking-head frames=264"
    )
    assert updates is not None
    assert updates["step"] == 0
    assert updates["max_steps"] == 264
    assert updates["percent"] == 0.0


def test_parse_composite_telemetry_line_basicvsrpp_fg() -> None:
    updates = parse_composite_telemetry_line(
        "[17:31:11] elapsed=    34.6s basicvsrpp fg frames=105/264 windows=7/18 rate=6.88fps eta=23.1s"
    )
    assert updates is not None
    assert updates["stage"] == "fg_upscale"
    assert updates["step"] == 105
    assert updates["max_steps"] == 264
    assert updates["percent"] == 39.8


def test_parse_composite_telemetry_line_corridorkey_heartbeat() -> None:
    updates = parse_composite_telemetry_line(
        "[17:42:00] elapsed=   600.0s CorridorKey frames=0/264 rate=0.00fps inference_elapsed=480s heartbeat=1"
    )
    assert updates is not None
    assert updates["stage"] == "corridorkey"
    assert updates["step"] == 0
    assert updates["max_steps"] == 264
    assert updates["percent"] == 0.0


def test_parse_composite_telemetry_line_prepare_plate_focus_blur() -> None:
    updates = parse_composite_telemetry_line(
        "[17:32:00] elapsed=    72.1s prepare plate-focus-blur sigma=1.50 "
        "canvas=1280x720 preview=overlay-720p-plate-focus-blur.png avatar_blur=off"
    )
    assert updates is not None
    assert updates["stage"] == "prepare"
    updates = parse_composite_telemetry_line(
        "[17:31:46] elapsed=    69.6s prepare write frames=50/264"
    )
    assert updates is not None
    assert updates["stage"] == "prepare"
    assert updates["step"] == 50
    assert updates["max_steps"] == 264


def test_parse_composite_telemetry_line_frame_start() -> None:
    updates = parse_composite_telemetry_line(
        "[17:40:00] elapsed=   200.0s CorridorKey frame_start index=0/264 corridor_input=1664x936"
    )
    assert updates is not None
    assert updates["stage"] == "corridorkey"
    assert updates["step"] == 0
    assert updates["max_steps"] == 264


def test_parse_composite_telemetry_line_stall() -> None:
    updates = parse_composite_telemetry_line(
        "[17:45:00] elapsed=   500.0s CorridorKey STALL saved_fg=0/264 inference_elapsed=480s"
    )
    assert updates is not None
    assert updates["last_event"] == "corridorkey_stall"
    assert updates["phase"] == "failed"


def test_composite_progress_kwargs_stall_does_not_duplicate_phase() -> None:
    updates = parse_composite_telemetry_line(
        "[17:45:00] elapsed=   500.0s CorridorKey STALL saved_fg=0/264 inference_elapsed=480s"
    )
    assert updates is not None

    def sink(*, phase: str, **_rest: object) -> str:
        return phase

    kwargs = composite_progress_kwargs(updates)
    assert sink(**kwargs) == "failed"
    assert kwargs["last_event"] == "corridorkey_stall"


def test_composite_progress_kwargs_defaults_compositing_phase() -> None:
    kwargs = composite_progress_kwargs({"message": "CorridorKey frames=5/10", "step": 5})
    assert kwargs["phase"] == "compositing"
    assert kwargs["last_event"] == "composite_telemetry"
    assert kwargs["step"] == 5


def test_parse_composite_telemetry_line_telemetry_json_heartbeat() -> None:
    updates = parse_composite_telemetry_line(
        '[17:42:00] elapsed=   300.0s TELEMETRY_JSON={"event":"corridorkey_heartbeat","frames_saved":5,"frames_total":264,"elapsed_s":300.0}'
    )
    assert updates is not None
    assert updates["step"] == 5
    assert updates["max_steps"] == 264


def test_parse_composite_telemetry_line_ignores_noise() -> None:
    assert parse_composite_telemetry_line("not a telemetry line") is None


def test_run_corridorkey_composite_uses_runner(tmp_path: Path, monkeypatch) -> None:
    root = tmp_path / "CorridorKey"
    checkpoint = root / "CorridorKeyModule" / "checkpoints"
    checkpoint.mkdir(parents=True)
    script = tmp_path / "run-corridorkey-composite.py"
    script.write_text("# stub\n", encoding="utf-8")
    source = tmp_path / "green.mkv"
    plate = tmp_path / "plate.png"
    output = tmp_path / "out.mp4"
    source.write_bytes(b"mkv")
    plate.write_bytes(b"png")

    monkeypatch.setenv("VIDEO_CORRIDORKEY_ROOT", str(root))
    monkeypatch.setenv("VIDEO_COMPOSITE_SCRIPT", str(script))
    (checkpoint / "CorridorKey_v1.0.safetensors").write_bytes(b"ckpt")

    captured: list[list[str]] = []

    def runner(command: list[str]) -> None:
        captured.append(command)
        output.write_bytes(b"mp4")

    run_corridorkey_composite(
        source=source,
        plate=plate,
        output=output,
        corridorkey_root=root,
        script_path=script,
        runner=runner,
    )
    assert captured
    assert str(source) in captured[0]
    assert str(plate) in captured[0]
    assert "--fg-upscaler" in captured[0]
    assert "lanczos" in captured[0]
    assert captured[0][0] != "/bin/bash"
    assert output.is_file()


def test_run_corridorkey_composite_streams_progress(tmp_path: Path, monkeypatch) -> None:
    root = tmp_path / "CorridorKey"
    checkpoint = root / "CorridorKeyModule" / "checkpoints"
    checkpoint.mkdir(parents=True)
    (checkpoint / "CorridorKey_v1.0.safetensors").write_bytes(b"ckpt")
    script = tmp_path / "fake-composite.py"
    output = tmp_path / "out.mp4"
    script.write_text(
        "\n".join(
            [
                "import sys",
                "from pathlib import Path",
                "print('[12:00:00] elapsed=     1.0s CorridorKey start clip=talking-head frames=10', flush=True)",
                "print('[12:00:05] elapsed=     6.0s CorridorKey frames=5/10 rate=1.00fps eta=5.0s', flush=True)",
                "Path(sys.argv[sys.argv.index('--output') + 1]).write_bytes(b'mp4')",
            ]
        )
        + "\n",
        encoding="utf-8",
    )
    source = tmp_path / "green.mkv"
    plate = tmp_path / "plate.png"
    source.write_bytes(b"mkv")
    plate.write_bytes(b"png")

    events: list[dict] = []
    run_corridorkey_composite(
        source=source,
        plate=plate,
        output=output,
        corridorkey_root=root,
        script_path=script,
        python_bin=sys.executable,
        on_progress=events.append,
    )
    assert output.is_file()
    assert len(events) >= 2
    assert events[0]["max_steps"] == 10
    assert events[0]["step"] == 0
    assert events[1]["step"] == 5
    assert events[1]["max_steps"] == 10
    assert events[1]["percent"] == 50.0


def test_run_corridorkey_composite_uses_high_resource_hip_alloc(
    tmp_path: Path, monkeypatch, capsys
) -> None:
    """Pressure HIP knobs from the parent must not reach CorridorKey."""
    stable = (
        "backend:native,expandable_segments:True,"
        "garbage_collection_threshold:0.7,max_split_size_mb:256"
    )
    monkeypatch.setenv("PYTORCH_HIP_ALLOC_CONF", stable)
    monkeypatch.setenv(
        "VIDEO_COMPOSITE_PYTORCH_HIP_ALLOC_CONF",
        "backend:native,garbage_collection_threshold:0.7",
    )
    root = tmp_path / "CorridorKey"
    checkpoint = root / "CorridorKeyModule" / "checkpoints"
    checkpoint.mkdir(parents=True)
    (checkpoint / "CorridorKey_v1.0.safetensors").write_bytes(b"ckpt")
    script = tmp_path / "fake-composite.py"
    output = tmp_path / "out.mp4"
    script.write_text(
        "\n".join(
            [
                "import os",
                "import sys",
                "from pathlib import Path",
                "print('HIP=' + os.environ['PYTORCH_HIP_ALLOC_CONF'], flush=True)",
                "Path(sys.argv[sys.argv.index('--output') + 1]).write_bytes(b'mp4')",
            ]
        )
        + "\n",
        encoding="utf-8",
    )
    source = tmp_path / "green.mkv"
    plate = tmp_path / "plate.png"
    source.write_bytes(b"mkv")
    plate.write_bytes(b"png")
    run_corridorkey_composite(
        source=source,
        plate=plate,
        output=output,
        corridorkey_root=root,
        script_path=script,
        python_bin=sys.executable,
    )
    assert output.is_file()
    assert "HIP=backend:native" in capsys.readouterr().out


def test_repo_composite_caller_scripts_follow_telemetry_contract() -> None:
    """CI gate: no ad-hoc docker exec / 2>&1 composite callers in scripts/ or artifacts/."""
    contract = None
    for parent in Path(__file__).resolve().parents:
        candidate = parent / "scripts" / "Test-CompositeTelemetryContract.ps1"
        if candidate.is_file():
            contract = candidate
            repo_root = parent
            break
    if contract is None:
        return
    result = subprocess.run(
        [
            "pwsh",
            "-NoProfile",
            "-ExecutionPolicy",
            "Bypass",
            "-File",
            str(contract),
        ],
        cwd=repo_root,
        capture_output=True,
        text=True,
        check=False,
    )
    assert result.returncode == 0, (result.stdout or "") + (result.stderr or "")


def _load_composite_script():
    path = Path(__file__).resolve().parents[3] / "scripts" / "run-corridorkey-composite.py"
    spec = importlib.util.spec_from_file_location("ck_composite_script", path)
    assert spec is not None and spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def test_basicvsrpp_watchdog_does_not_stall_after_last_window() -> None:
    """Job bb354dad: windows=95/95 then 181s of HR npy flush is not a stall."""
    script = _load_composite_script()
    assert (
        script.basicvsrpp_watchdog_stall(
            windows_done=95,
            window_total=95,
            no_progress_sec=181.0,
            stall_sec=180.0,
            first_window_stall_sec=120.0,
            tile_w=0,
            tile_h=0,
            length=15,
        )
        is None
    )


def test_basicvsrpp_watchdog_stalls_mid_windows() -> None:
    script = _load_composite_script()
    error = script.basicvsrpp_watchdog_stall(
        windows_done=40,
        window_total=95,
        no_progress_sec=181.0,
        stall_sec=180.0,
        first_window_stall_sec=120.0,
        tile_w=0,
        tile_h=0,
        length=15,
    )
    assert error is not None
    assert "windows=40/95" in str(error)


def test_basicvsrpp_watchdog_stalls_first_window() -> None:
    script = _load_composite_script()
    error = script.basicvsrpp_watchdog_stall(
        windows_done=0,
        window_total=18,
        no_progress_sec=120.0,
        stall_sec=180.0,
        first_window_stall_sec=120.0,
        tile_w=0,
        tile_h=0,
        length=15,
    )
    assert error is not None
    assert "windows=0/18" in str(error)


def test_parse_composite_telemetry_line_basicvsrpp_complete_and_saving() -> None:
    complete = parse_composite_telemetry_line(
        "[03:54:00] elapsed=   554.5s basicvsrpp fg complete frames=1412/1412 "
        "windows=95/95 hr=1664x936 elapsed=554.5s rate=2.55fps"
    )
    assert complete is not None
    assert complete["stage"] == "fg_upscale"
    assert complete["step"] == 1412
    assert complete["max_steps"] == 1412
    saving = parse_composite_telemetry_line(
        "[03:54:20] elapsed=   574.5s basicvsrpp fg saving frames=200/1412 "
        "windows=95/95 hr=1664x936"
    )
    assert saving is not None
    assert saving["stage"] == "fg_upscale"
    assert saving["step"] == 200
    assert saving["max_steps"] == 1412


def test_existing_clip_root_for_retry_requires_matching_exrs(tmp_path: Path) -> None:
    output = tmp_path / "doug-course-intro.mp4"
    workspace = clip_workspace_for_output(output)
    clip_root = workspace / "talking-head"
    fg = clip_root / "Output" / "FG"
    fg.mkdir(parents=True)
    (workspace / "manifest.json").write_text(
        '{"frame_count": 3, "fps": 25.0, "corridor_input": "416x234"}\n',
        encoding="utf-8",
    )
    assert existing_clip_root_for_retry(output) is None
    for index in range(3):
        (fg / f"{index:04d}.exr").write_bytes(b"exr")
    assert existing_clip_root_for_retry(output) == clip_root


def _ready_composite_paths(tmp_path: Path) -> tuple[Path, Path, Path, Path, Path]:
    root = tmp_path / "CorridorKey"
    checkpoint = root / "CorridorKeyModule" / "checkpoints"
    checkpoint.mkdir(parents=True)
    (checkpoint / "CorridorKey_v1.0.safetensors").write_bytes(b"ckpt")
    script = tmp_path / "run-corridorkey-composite.py"
    script.write_text("# stub\n", encoding="utf-8")
    source = tmp_path / "green.mkv"
    plate = tmp_path / "plate.png"
    output = tmp_path / "doug-course-intro.mp4"
    source.write_bytes(b"mkv")
    plate.write_bytes(b"png")
    return root, script, source, plate, output


def test_run_corridorkey_composite_retries_clip_root_after_failure(
    tmp_path: Path, monkeypatch
) -> None:
    root, script, source, plate, output = _ready_composite_paths(tmp_path)
    monkeypatch.setenv("VIDEO_CORRIDORKEY_ROOT", str(root))
    monkeypatch.setenv("VIDEO_COMPOSITE_SCRIPT", str(script))
    clip_root = clip_workspace_for_output(output) / "talking-head"
    fg = clip_root / "Output" / "FG"
    fg.mkdir(parents=True)
    (fg / "0000.exr").write_bytes(b"exr")
    (clip_root.parent / "manifest.json").write_text(
        '{"frame_count": 1, "fps": 25.0, "corridor_input": "416x234"}\n',
        encoding="utf-8",
    )
    captured: list[list[str]] = []

    def runner(command: list[str]) -> None:
        captured.append(command)
        if len(captured) == 1:
            raise CompositeError("basicvsrpp STALL windows=95/95", 500)
        output.write_bytes(b"mp4")

    events: list[dict] = []
    run_corridorkey_composite(
        source=source,
        plate=plate,
        output=output,
        corridorkey_root=root,
        script_path=script,
        runner=runner,
        on_progress=events.append,
    )
    assert len(captured) == 2
    assert "--clip-root" not in captured[0]
    assert "--clip-root" in captured[1]
    assert str(clip_root) in captured[1]
    assert output.is_file()
    assert any(item.get("last_event") == "composite_clip_root_retry" for item in events)


def test_run_corridorkey_composite_does_not_retry_without_exrs(
    tmp_path: Path, monkeypatch
) -> None:
    root, script, source, plate, output = _ready_composite_paths(tmp_path)
    monkeypatch.setenv("VIDEO_CORRIDORKEY_ROOT", str(root))
    monkeypatch.setenv("VIDEO_COMPOSITE_SCRIPT", str(script))
    captured: list[list[str]] = []

    def runner(command: list[str]) -> None:
        captured.append(command)
        raise CompositeError("basicvsrpp STALL windows=0/18", 500)

    try:
        run_corridorkey_composite(
            source=source,
            plate=plate,
            output=output,
            corridorkey_root=root,
            script_path=script,
            runner=runner,
        )
    except CompositeError as exc:
        assert "windows=0/18" in str(exc)
    else:
        raise AssertionError("expected CompositeError without clip-root retry")
    assert len(captured) == 1
    assert "--clip-root" not in captured[0]
