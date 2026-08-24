from __future__ import annotations

from pathlib import Path

from guideants_video_adapter.composite import composite_ready, run_corridorkey_composite


def test_composite_ready_reports_missing(tmp_path: Path, monkeypatch) -> None:
    monkeypatch.setenv("VIDEO_CORRIDORKEY_ROOT", str(tmp_path / "missing-root"))
    monkeypatch.setenv("VIDEO_COMPOSITE_SCRIPT", str(tmp_path / "missing.py"))
    ok, missing = composite_ready()
    assert ok is False
    assert "composite_script" in missing
    assert "corridorkey_root" in missing


def test_run_corridorkey_composite_uses_runner(tmp_path: Path, monkeypatch) -> None:
    root = tmp_path / "CorridorKey"
    checkpoint = root / "CorridorKeyModule" / "checkpoints"
    checkpoint.mkdir(parents=True)
    # SHA must match pinned hash — write enough bytes then monkeypatch sha check via real file
    # Instead: patch composite_ready by providing real-looking paths and inject runner only.
    script = tmp_path / "run-corridorkey-composite.py"
    script.write_text("# stub\n", encoding="utf-8")
    source = tmp_path / "green.mkv"
    plate = tmp_path / "plate.png"
    output = tmp_path / "out.mp4"
    source.write_bytes(b"mkv")
    plate.write_bytes(b"png")

    monkeypatch.setenv("VIDEO_CORRIDORKEY_ROOT", str(root))
    monkeypatch.setenv("VIDEO_COMPOSITE_SCRIPT", str(script))

    # Bypass checkpoint hash by creating a file and patching composite_ready
    (checkpoint / "CorridorKey_v1.0.safetensors").write_bytes(b"ckpt")

    captured: list[list[str]] = []

    def runner(command: list[str]) -> None:
        captured.append(command)
        output.write_bytes(b"mp4")

    # composite_ready still requires checkpoint file present — ok
    # run will call composite_ready which only checks is_file for checkpoint, not hash
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
    assert output.is_file()
