"""CorridorKey composite stage for talking-head delivery (post InfiniteTalk i2v).

This module is the CONSUMER of run-corridorkey-composite.py stdout telemetry.
  - parse_composite_telemetry_line: regex contract (must match script output).
  - run_corridorkey_composite: MUST stream stdout line-by-line — never buffer the
    whole subprocess (jobs go silent for 15+ minutes during CorridorKey otherwise).
  - stderr is drained on a side thread (MIOpen/HIP noise); do not merge into stdout.
  - On CompositeError, retry once with --clip-root when FG EXRs match manifest.json.
    That is delivery recovery. Do not redo CorridorKey or InfiniteTalk.

Tests: guideants_video_adapter/tests/test_composite.py — update when line format changes.
"""

from __future__ import annotations

import os
import re
import json
import subprocess
import threading
from pathlib import Path
from typing import Any, Callable


CompositeRunner = Callable[..., None]
CompositeProgressCallback = Callable[[dict[str, Any]], None]

_MIOpen_NOISE = re.compile(r"MIOpen|miopen|HIP warning", re.IGNORECASE)


def _stderr_detail(stderr_text: str) -> str:
    lines = [line.strip() for line in stderr_text.splitlines() if line.strip()]
    signal = [line for line in lines if not _MIOpen_NOISE.search(line)]
    tail = (signal or lines)[-30:]
    return "\n".join(tail) if tail else "composite failed (stderr empty)"

# Regex contract shared with run-corridorkey-composite.py telemetry().
# Any new long-running stage must emit frames=<step>/<total> somewhere in the message
# or job progress (step/max_steps/percent) will not update. See test_composite.py.
_TELEMETRY_LINE = re.compile(
    r"^\[\d{2}:\d{2}:\d{2}\]\s+elapsed=\s*[\d.,]+s\s+(?P<message>.+)\s*$"
)
_TELEMETRY_JSON = re.compile(r"\bTELEMETRY_JSON=(?P<payload>\{.*\})\s*$")
_FRAMES_RATIO = re.compile(r"\bframes=(?P<step>\d+)/(?P<max_steps>\d+)\b")
_FRAMES_TOTAL = re.compile(r"\bframes=(?P<max_steps>\d+)\b")
_FRAME_INDEX = re.compile(r"\bindex=(?P<index>\d+)(?:/(?P<total>\d+))?\b")


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


def default_composite_telemetry_wrapper() -> Path | None:
    """Only wrap when VIDEO_COMPOSITE_TELEMETRY_WRAPPER is explicitly set.

    Auto-detecting exec-composite-telemetry.sh broke the skill path: dash
    rejected ``pipefail``, and even after the bash shebang fix the extra
    process sat between the adapter and Python. Adapter already streams stdout.
    """
    configured = os.getenv("VIDEO_COMPOSITE_TELEMETRY_WRAPPER", "").strip()
    if not configured:
        return None
    path = Path(configured)
    return path if path.is_file() else None


def _wrap_composite_command(command: list[str]) -> list[str]:
    wrapper = default_composite_telemetry_wrapper()
    if wrapper is None:
        return command
    return ["/bin/bash", str(wrapper), *command]


_BASICVSRPP_CHECKPOINTS = (
    "basicvsr_plusplus_c64n7_8x1_300k_vimeo90k_bd_20210305-ab315ab1.pth",
    "spynet_20210409-c6c1bd09.pth",
)


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
    fg_upscaler = os.getenv("VIDEO_COMPOSITE_FG_UPSCALER", "lanczos").strip().lower()
    if fg_upscaler == "basicvsrpp":
        ckpt_root = Path(os.getenv("VIDEO_BASICVSRPP_CHECKPOINT_DIR", "/models/basicvsrpp"))
        if not all((ckpt_root / name).is_file() for name in _BASICVSRPP_CHECKPOINTS):
            missing.append("basicvsrpp_checkpoint")
        vsr_venv = Path(os.getenv("VIDEO_BASICVSRPP_VENV", "/opt/venv-basicvsrpp"))
        site_packages = sorted(vsr_venv.glob("lib/python*/site-packages"))
        if not site_packages and not (vsr_venv / "Lib" / "site-packages").is_dir():
            missing.append("basicvsrpp_venv")
    return (not missing), missing


def _infer_composite_stage(message: str) -> str | None:
    if message.startswith("FATAL"):
        return "failed"
    if message.startswith("basicvsrpp fg") or message.startswith("basicvsrpp avatar"):
        return "fg_upscale"
    if message.startswith("CorridorKey"):
        return "corridorkey"
    if message.startswith("composite"):
        return "composite"
    if message.startswith("prepare"):
        return "prepare"
    return None


def parse_composite_telemetry_line(line: str) -> dict[str, Any] | None:
    """Map one composite-script stdout line into job.progress updates.

    Returns dict with at least ``message``; adds ``step``, ``max_steps``, ``percent``
    when ``frames=N/M`` is present; adds ``stage`` from message prefix when known.

    Returns None for non-telemetry lines (and for unrecognized messages with no stage).
    """
    stripped = line.strip()
    json_match = _TELEMETRY_JSON.search(stripped)
    if json_match is not None:
        try:
            payload = json.loads(json_match.group("payload"))
        except json.JSONDecodeError:
            return None
        if not isinstance(payload, dict):
            return None
        updates: dict[str, Any] = {"message": stripped, "telemetry_json": payload}
        event = payload.get("event")
        if event == "corridorkey_start":
            updates["stage"] = "corridorkey"
            updates["step"] = 0
            updates["max_steps"] = int(payload.get("frames_total", 0))
            if updates["max_steps"]:
                updates["percent"] = 0.0
        elif event in {"corridorkey_frame_start", "corridorkey_frame_saved", "corridorkey_heartbeat"}:
            updates["stage"] = "corridorkey"
            saved = payload.get("frames_saved")
            total = payload.get("frames_total")
            if saved is not None and total is not None:
                updates["step"] = int(saved)
                updates["max_steps"] = int(total)
                if updates["max_steps"]:
                    updates["percent"] = round(
                        100.0 * updates["step"] / updates["max_steps"], 1
                    )
        return updates

    match = _TELEMETRY_LINE.match(stripped)
    if match is None:
        return None
    message = match.group("message").strip()
    if not message:
        return None
    updates = {"message": message}
    stage = _infer_composite_stage(message)
    if stage is not None:
        updates["stage"] = stage
    if " STALL " in message or message.startswith("CorridorKey STALL"):
        updates["phase"] = "failed"
        updates["last_event"] = "corridorkey_stall"
        return updates
    if " ABNORMAL " in message:
        updates["last_event"] = "corridorkey_abnormal"
    ratio = _FRAMES_RATIO.search(message)
    if ratio is not None:
        step = int(ratio.group("step"))
        max_steps = int(ratio.group("max_steps"))
        updates["step"] = step
        updates["max_steps"] = max_steps
        updates["percent"] = round(100.0 * step / max_steps, 1) if max_steps else None
        return updates
    frame_index = _FRAME_INDEX.search(message)
    if frame_index is not None and message.startswith("CorridorKey frame_"):
        index = int(frame_index.group("index"))
        total = frame_index.group("total")
        updates["step"] = index
        if total is not None:
            updates["max_steps"] = int(total)
            updates["percent"] = round(100.0 * index / int(total), 1)
        return updates
    if message.startswith("CorridorKey start clip="):
        total = _FRAMES_TOTAL.search(message)
        if total is not None:
            max_steps = int(total.group("max_steps"))
            updates["step"] = 0
            updates["max_steps"] = max_steps
            updates["percent"] = 0.0
            return updates
    return updates if stage is not None else None


def composite_progress_kwargs(updates: dict[str, Any]) -> dict[str, Any]:
    """Flatten one telemetry dict into ``_update_job_progress`` kwargs.

    Stall lines set ``phase=failed``. Callers must not also pass ``phase=`` as a
    separate keyword — that raises TypeError (multiple values for 'phase').
    """
    kwargs = {"phase": "compositing", **updates}
    kwargs.setdefault("last_event", "composite_telemetry")
    return kwargs


def clip_workspace_for_output(output: Path) -> Path:
    return output.parent / f".corridorkey-{output.stem}"


def existing_clip_root_for_retry(output: Path) -> Path | None:
    """Return talking-head clip-root when FG EXRs match the clip manifest.

    Post-CorridorKey failures must resume from this path. Missing or partial
    EXRs are not a retry — guessing would redo or skip keying incorrectly.
    """
    workspace = clip_workspace_for_output(output)
    manifest_path = workspace / "manifest.json"
    clip_root = workspace / "talking-head"
    fg_dir = clip_root / "Output" / "FG"
    if not manifest_path.is_file() or not fg_dir.is_dir():
        return None
    try:
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        expected = int(manifest["frame_count"])
    except (OSError, KeyError, TypeError, ValueError, json.JSONDecodeError):
        return None
    if expected < 1:
        return None
    fg_count = sum(1 for entry in fg_dir.iterdir() if entry.suffix.lower() == ".exr")
    if fg_count != expected:
        return None
    return clip_root


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
    fg_upscaler: str | None = None,
    basicvsrpp_checkpoint_dir: str | None = None,
    basicvsrpp_length: int | None = None,
    basicvsrpp_tile_w: int | None = None,
    basicvsrpp_tile_h: int | None = None,
    background_blur_sigma: float | None = None,
    foreground_sharpen_amount: float | None = None,
    foreground_sharpen_sigma: float | None = None,
    runner: CompositeRunner | None = None,
    on_progress: CompositeProgressCallback | None = None,
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
    fg_upscaler = (
        fg_upscaler
        or os.getenv("VIDEO_COMPOSITE_FG_UPSCALER", "lanczos")
    ).strip().lower()
    if fg_upscaler not in {"lanczos", "basicvsrpp"}:
        raise CompositeError(
            f"unsupported VIDEO_COMPOSITE_FG_UPSCALER: {fg_upscaler}",
            500,
        )

    # Plate blur for depth-of-field on both upscalers; FG sharpen only on Lanczos.
    if background_blur_sigma is None:
        env_blur = os.getenv("VIDEO_COMPOSITE_BACKGROUND_BLUR_SIGMA")
        if env_blur is not None:
            background_blur_sigma = float(env_blur)
        else:
            background_blur_sigma = 1.5
    if foreground_sharpen_amount is None:
        env_amount = os.getenv("VIDEO_COMPOSITE_FOREGROUND_SHARPEN_AMOUNT")
        if env_amount is not None:
            foreground_sharpen_amount = float(env_amount)
        else:
            foreground_sharpen_amount = 0.0 if fg_upscaler == "basicvsrpp" else 0.15
    if foreground_sharpen_sigma is None:
        foreground_sharpen_sigma = float(
            os.getenv("VIDEO_COMPOSITE_FOREGROUND_SHARPEN_SIGMA", "0.8")
        )

    def build_command(*, clip_root: Path | None = None) -> list[str]:
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
            "--fg-upscaler",
            fg_upscaler,
            "--background-blur-sigma",
            str(background_blur_sigma),
            "--foreground-sharpen-amount",
            str(foreground_sharpen_amount),
            "--foreground-sharpen-sigma",
            str(foreground_sharpen_sigma),
        ]
        checkpoint_dir = basicvsrpp_checkpoint_dir or os.getenv(
            "VIDEO_BASICVSRPP_CHECKPOINT_DIR"
        )
        if checkpoint_dir:
            command.extend(["--basicvsrpp-checkpoint-dir", checkpoint_dir])
        length = basicvsrpp_length
        if length is None:
            length_env = os.getenv("VIDEO_BASICVSRPP_LENGTH")
            length = int(length_env) if length_env else 15
        command.extend(["--basicvsrpp-length", str(length)])
        tile_w = basicvsrpp_tile_w
        if tile_w is None:
            tile_w = int(os.getenv("VIDEO_BASICVSRPP_TILE_W", "0"))
        tile_h = basicvsrpp_tile_h
        if tile_h is None:
            tile_h = int(os.getenv("VIDEO_BASICVSRPP_TILE_H", "0"))
        command.extend(["--basicvsrpp-tile-w", str(tile_w), "--basicvsrpp-tile-h", str(tile_h)])
        if master_output is not None:
            master_output.parent.mkdir(parents=True, exist_ok=True)
            command.extend(["--master-output", str(master_output)])
        if clip_root is not None:
            command.extend(["--clip-root", str(clip_root)])
        return _wrap_composite_command(command)

    def execute(command: list[str]) -> None:
        if runner is not None:
            runner(command)
            if not output.is_file() or output.stat().st_size == 0:
                raise CompositeError("composite produced an empty delivery", 500)
            return

        env = os.environ.copy()
        env["OPENCV_IO_ENABLE_OPENEXR"] = "1"
        script_dir = str(Path(script).resolve().parent)
        existing_pythonpath = env.get("PYTHONPATH", "")
        env["PYTHONPATH"] = (
            f"{root}{os.pathsep}{script_dir}"
            if not existing_pythonpath
            else f"{root}{os.pathsep}{script_dir}{os.pathsep}{existing_pythonpath}"
        )
        # Skip CorridorKey .venv re-exec; adapter container uses /opt/venv + PYTHONPATH.
        env["GUIDEANTS_CORRIDORKEY_ENV"] = "1"
        env.setdefault("MIOPEN_LOG_LEVEL", "0")
        env.setdefault("HIP_VISIBLE_DEVICES", os.getenv("HIP_VISIBLE_DEVICES", "0"))
        # Do not inherit garbage_collection_threshold / max_split_size_mb / expandable_segments.
        env["PYTORCH_HIP_ALLOC_CONF"] = "backend:native"
        # AMD/ROCm MIOpen fix (gfx1151): the default solver search can request a
        # ~4.8 GiB GEMM workspace that hits the 1 GiB cap and the process is
        # SIGKILLed (exit -9) during the BasicVSR++/fit GEMMs. FAST find mode
        # picks dynamic kernels with small workspaces. Container env from the
        # compose file (MIOPEN_FIND_MODE / MIOPEN_FIND_ENFORCE via .env) wins.
        env.setdefault("MIOPEN_FIND_MODE", "2")
        env.setdefault("MIOPEN_FIND_ENFORCE", "1")
        try:
            # Stream stdout — do NOT replace with subprocess.run(capture_output=True).
            process = subprocess.Popen(
                command,
                env=env,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                text=True,
                bufsize=1,
            )
        except OSError as exc:
            raise CompositeError(f"composite failed to start: {exc}", 500) from exc

        stderr_chunks: list[str] = []

        def _drain_stderr() -> None:
            # MIOpen/HIP warnings belong here, not in the pipeline log / Tee-Object file.
            if process.stderr is None:
                return
            for chunk in process.stderr:
                stderr_chunks.append(chunk)

        stderr_thread = threading.Thread(
            target=_drain_stderr, name="corridorkey-stderr", daemon=True
        )
        stderr_thread.start()
        try:
            if process.stdout is not None:
                for line in process.stdout:
                    print(line, end="" if line.endswith("\n") else "\n", flush=True)
                    updates = parse_composite_telemetry_line(line)
                    if updates is not None and on_progress is not None:
                        on_progress(updates)
            returncode = process.wait()
        finally:
            stderr_thread.join(timeout=30)
            if process.poll() is None:
                process.kill()
                process.wait(timeout=10)

        stderr_text = "".join(stderr_chunks)
        if returncode != 0:
            detail = _stderr_detail(stderr_text)
            for line in detail.splitlines():
                print(f"[composite stderr] {line}", flush=True)
            raise CompositeError(f"composite failed (exit {returncode}): {detail}", 500)
        if not output.is_file() or output.stat().st_size == 0:
            raise CompositeError("composite produced an empty delivery", 500)

    try:
        execute(build_command())
    except CompositeError as first_error:
        clip_root = existing_clip_root_for_retry(output)
        if clip_root is None:
            raise
        retry_message = (
            f"composite retry clip-root={clip_root} after: {first_error}"
        )
        print(retry_message, flush=True)
        if on_progress is not None:
            on_progress(
                {
                    "message": retry_message,
                    "stage": "post_ck",
                    "last_event": "composite_clip_root_retry",
                }
            )
        execute(build_command(clip_root=clip_root))
