"""CorridorKey composite stage for talking-head delivery (post InfiniteTalk i2v)."""

from __future__ import annotations

import os
import subprocess
from pathlib import Path
from typing import Callable


CompositeRunner = Callable[..., None]


class CompositeError(RuntimeError):
    """Composite stage failure."""

    def __init__(self, message: str, status_code: int = 500) -> None:
        super().__init__(message)
        self.status_code = status_code


def default_corridorkey_root() -> Path:
    return Path(
        os.getenv(
            "VIDEO_CORRIDORKEY_ROOT",
            "/opt/guideants/corridorkey-local/CorridorKey",
        )
    )


def default_composite_script() -> Path:
    return Path(
        os.getenv(
            "VIDEO_COMPOSITE_SCRIPT",
            "/opt/guideants/comfyui-video/scripts/run-corridorkey-composite.py",
        )
    )


def default_composite_python() -> str:
    return os.getenv("VIDEO_COMPOSITE_PYTHON", "/opt/venv/bin/python3.12")


def composite_ready(
    *,
    corridorkey_root: Path | None = None,
    script_path: Path | None = None,
) -> tuple[bool, list[str]]:
    root = corridorkey_root or default_corridorkey_root()
    script = script_path or default_composite_script()
    missing: list[str] = []
    if not script.is_file():
        missing.append("composite_script")
    if not root.is_dir():
        missing.append("corridorkey_root")
    else:
        checkpoint = root / "CorridorKeyModule" / "checkpoints" / "CorridorKey_v1.0.safetensors"
        if not checkpoint.is_file():
            missing.append("corridorkey_checkpoint")
    return (not missing), missing


def run_corridorkey_composite(
    *,
    source: Path,
    plate: Path,
    output: Path,
    master_output: Path | None = None,
    corridorkey_root: Path | None = None,
    script_path: Path | None = None,
    python_bin: str | None = None,
    device: str | None = None,
    width: int | None = None,
    height: int | None = None,
    background_blur_sigma: float = 1.5,
    foreground_sharpen_amount: float = 0.15,
    foreground_sharpen_sigma: float = 0.8,
    runner: CompositeRunner | None = None,
) -> None:
    """Run CorridorKey unmix + composite. Preserves InfiniteTalk graph; post-process only."""
    root = corridorkey_root or default_corridorkey_root()
    script = script_path or default_composite_script()
    ready, missing = composite_ready(corridorkey_root=root, script_path=script)
    if not ready:
        raise CompositeError(f"composite backend is not ready: {missing}", 503)
    if not source.is_file():
        raise CompositeError(f"composite source missing: {source}", 500)
    if not plate.is_file():
        raise CompositeError(f"composite plate missing: {plate}", 500)

    output.parent.mkdir(parents=True, exist_ok=True)
    device = device or os.getenv("VIDEO_COMPOSITE_DEVICE", "cuda:0")
    width = width if width is not None else int(os.getenv("VIDEO_COMPOSITE_WIDTH", "1280"))
    height = height if height is not None else int(os.getenv("VIDEO_COMPOSITE_HEIGHT", "720"))
    python_bin = python_bin or default_composite_python()

    command = [
        python_bin,
        str(script),
        "--source",
        str(source),
        "--plate",
        str(plate),
        "--output",
        str(output),
        "--corridorkey-root",
        str(root),
        "--device",
        device,
        "--width",
        str(width),
        "--height",
        str(height),
        "--background-blur-sigma",
        str(background_blur_sigma),
        "--foreground-sharpen-amount",
        str(foreground_sharpen_amount),
        "--foreground-sharpen-sigma",
        str(foreground_sharpen_sigma),
    ]
    if master_output is not None:
        master_output.parent.mkdir(parents=True, exist_ok=True)
        command.extend(["--master-output", str(master_output)])

    if runner is not None:
        runner(command)
        return

    env = os.environ.copy()
    env["OPENCV_IO_ENABLE_OPENEXR"] = "1"
    env["PYTHONPATH"] = str(root)
    # Skip CorridorKey .venv re-exec; adapter container uses /opt/venv + PYTHONPATH.
    env["GUIDEANTS_CORRIDORKEY_ENV"] = "1"
    env.setdefault("HIP_VISIBLE_DEVICES", os.getenv("HIP_VISIBLE_DEVICES", "0"))
    env.setdefault(
        "PYTORCH_HIP_ALLOC_CONF",
        "backend:native,garbage_collection_threshold:0.7,max_split_size_mb:256",
    )
    try:
        completed = subprocess.run(
            command,
            check=False,
            env=env,
            capture_output=True,
            text=True,
        )
    except OSError as exc:
        raise CompositeError(f"composite failed to start: {exc}", 500) from exc
    if completed.stdout:
        print(completed.stdout, end="" if completed.stdout.endswith("\n") else "\n", flush=True)
    if completed.returncode != 0:
        detail = (completed.stderr or completed.stdout or "composite failed").strip()
        raise CompositeError(f"composite failed (exit {completed.returncode}): {detail}", 500)
    if not output.is_file() or output.stat().st_size == 0:
        raise CompositeError("composite produced an empty delivery", 500)
