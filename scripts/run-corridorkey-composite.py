"""Create a talking-head composite with CorridorKey foreground unmixing.

Operational contract (do not break):
  - Every long-running stage must emit live progress via telemetry().
  - CorridorKey must log frame_start, frame_saved, frames=N/M heartbeats, and fail on STALL.
  - Structured TELEMETRY_JSON= lines accompany human-readable stdout for machines.
  - Downstream consumers depend on a fixed stdout line shape; see telemetry() below.
  - Adapter: guideants_video_adapter/composite.py (parse_composite_telemetry_line).
  - Tests: guideants_video_adapter/tests/test_composite.py (canonical examples).
  - Narrative: docs/talking-head-corridorkey-handoff.md (operational requirements).
  - Validate through run-talking-head-pipeline.ps1 or adapter on_progress — not
    ad-hoc docker exec with stderr merged into stdout (MIOpen drowns the log).
  - PowerShell callers: scripts/Invoke-CorridorKeyCompositeLog.ps1 (never 2>&1 | Tee-Object).
  - On failure: progress JSON records stage=failed, failed_stage, error, exit_code;
    stdout emits FATAL …; stderr traceback preserved (not redirected to /dev/null).
  - Clip workspace .corridorkey-{output-stem}/ persists until success; retry post-CK
    with --clip-root when Output/FG EXRs and manifest.json exist. Adapter retries
    that path once on CompositeError; do not redo CorridorKey or InfiniteTalk.
  - BasicVSR++ stall watchdog aborts only while windows are still outstanding.
    windows_done >= window_total is HR flush/save, not a stall.
  - BasicVSR++ runs in VIDEO_BASICVSRPP_VENV as a child process after CorridorKey
    releases HIP. Spawn with close_fds (posix_spawn), not start_new_session/fork,
    so the child does not inherit the parent's CUDA/HIP context.
  - Focus blur (Gaussian σ) is applied to the cover-fit backplate before CorridorKey.
    The avatar/FG path is never Gaussian-blurred; VSR sharpen stays 0.
"""

from __future__ import annotations

import argparse
import hashlib
import inspect
import json
import math
import os
import queue
import shutil
import subprocess
import sys
import tempfile
import threading
import time
import traceback
from pathlib import Path


CORRIDORKEY_COMMIT = "97e55a453060745bead1befd293f6e523c4b845c"
CORRIDORKEY_CHECKPOINT = "CorridorKey_v1.0.safetensors"
CORRIDORKEY_CHECKPOINT_SHA256 = (
    "74d614f7d92fc559a118c30a7deadedc3cacd8ef83dcb85a030d0bed7af8b20b"
)
CORRIDORKEY_ENV_MARKER = "GUIDEANTS_CORRIDORKEY_ENV"
# HIP caching allocator for a large-memory host. Do not set
# garbage_collection_threshold (that is "treat 70% as OOM"), max_split_size_mb
# (small-GPU fragmentation workaround), or expandable_segments (unsupported on
# this ROCm/DXG stack — ComfyUI logs that warning and then runs without it).
HIGH_RESOURCE_HIP_ALLOC_CONF = "backend:native"

# Measured wall times on Max (teal-polo 264f). Update when hardware/path changes.
CORRIDORKEY_BENCHMARKS: dict[str, dict[str, float]] = {
    "416x234": {"first_frame_s": 30.0, "total_s": 713.0},
    "1664x936": {"first_frame_s": 120.0, "total_s": 995.0},
}
DEFAULT_CK_STALL_SEC = 180.0
DEFAULT_CK_FIRST_FRAME_STALL_SEC = 300.0
DEFAULT_BVSRPP_STALL_SEC = 180.0
DEFAULT_BVSRPP_FIRST_WINDOW_STALL_SEC = 120.0
DEFAULT_BASICVSRPP_VENV = "/opt/venv-basicvsrpp"


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", required=True, type=Path)
    parser.add_argument("--plate", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--master-output", type=Path)
    parser.add_argument("--corridorkey-root", required=True, type=Path)
    parser.add_argument("--device", default="cuda")
    parser.add_argument("--width", type=int, default=1280)
    parser.add_argument("--height", type=int, default=720)
    parser.add_argument("--max-frames", type=int)
    parser.add_argument(
        "--fg-upscaler",
        "--avatar-upscaler",
        choices=["lanczos", "basicvsrpp"],
        default="lanczos",
        dest="fg_upscaler",
        help="Upscale keyed foreground AFTER CorridorKey (replaces Lanczos). "
        "Never runs on the green avatar before keying.",
    )
    parser.add_argument(
        "--basicvsrpp-checkpoint-dir",
        type=Path,
        default=None,
    )
    parser.add_argument("--basicvsrpp-length", type=int, default=15)
    parser.add_argument("--basicvsrpp-tile-w", type=int, default=0)
    parser.add_argument("--basicvsrpp-tile-h", type=int, default=0)
    # None => plate blur 1.5; FG sharpen 0.15 on Lanczos, 0 on BasicVSR++.
    parser.add_argument("--background-blur-sigma", type=float, default=None)
    parser.add_argument("--foreground-sharpen-amount", type=float, default=None)
    parser.add_argument("--foreground-sharpen-sigma", type=float, default=0.8)
    parser.add_argument(
        "--clip-root",
        type=Path,
        default=None,
        help="Reuse an existing talking-head clip (Output/FG EXRs); skips prepare and CorridorKey",
    )
    parser.add_argument(
        "--keep-clip-workspace",
        action="store_true",
        help="Do not delete the persistent clip workspace after success",
    )
    return parser.parse_args()


def resolve_focus_defaults(args: argparse.Namespace) -> None:
    """Plate blur is cover-fit Gaussian on the backplate only, applied before CorridorKey.

    FG sharpen is Lanczos-only (VSR is already sharp). The avatar is never Gaussian-blurred.
    """
    if args.background_blur_sigma is None:
        args.background_blur_sigma = 1.5
    if args.foreground_sharpen_amount is None:
        args.foreground_sharpen_amount = (
            0.0 if args.fg_upscaler == "basicvsrpp" else 0.15
        )


def checked_path(path: Path, description: str) -> Path:
    resolved = path.resolve()
    if not resolved.is_file():
        raise FileNotFoundError(f"{description} does not exist: {resolved}")
    return resolved


def telemetry(started_at: float, message: str) -> None:
    """Emit one stdout progress line (flushed immediately).

    CONTRACT — keep in sync with composite.parse_composite_telemetry_line and
    test_composite.py. Do not invent alternate field names (saved_fg, windows-only, etc.).

    Line shape:
      [HH:MM:SS] elapsed=<seconds>s <message>

    Long-running loops MUST include frames=<step>/<total> in <message>, e.g.:
      CorridorKey frames=120/264 rate=2.10fps eta=68.5s
      basicvsrpp avatar frames=105/264 windows=7/18 rate=6.88fps eta=23.1s
      composite frames=50/264 rate=12.00fps eta=17.8s
      prepare write frames=50/264

    Stage prefix (first token group) selects job progress stage in the adapter:
      prepare*, basicvsrpp fg*, CorridorKey*, composite*

    CorridorKey on_frame_complete fires only after each frame is written — use the
    heartbeat thread below during long first-frame inference so logs are not silent.
    """
    elapsed = time.monotonic() - started_at
    timestamp = time.strftime("%H:%M:%S")
    print(f"[{timestamp}] elapsed={elapsed:8.1f}s {message}", flush=True)


class ProgressTracker:
    """Optional JSON sidecar ({output-stem}-progress.json) for machine polling.

    Human operators and skill jobs should rely on telemetry() stdout first; this file
    is supplementary (e.g. tail -f progress.json while docker logs are noisy).
    """

    def __init__(self, path: Path) -> None:
        self.path = path
        self._lock = threading.Lock()
        self._data: dict[str, object] = {
            "pipeline": "corridorkey-composite",
            "started_at": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
        }

    def update(self, **fields: object) -> None:
        with self._lock:
            self._data.update(fields)
            self._data["updated_at"] = time.strftime(
                "%Y-%m-%dT%H:%M:%SZ", time.gmtime()
            )
            self.path.parent.mkdir(parents=True, exist_ok=True)
            temporary = self.path.with_suffix(self.path.suffix + ".tmp")
            temporary.write_text(
                json.dumps(self._data, indent=2) + "\n",
                encoding="utf-8",
            )
            temporary.replace(self.path)

    def fail(self, *, exit_code: int, failed_stage: str, error: str) -> None:
        self.update(
            stage="failed",
            failed_stage=failed_stage,
            error=error[:8000],
            exit_code=exit_code,
        )


def clip_workspace_for_output(output: Path) -> Path:
    return output.parent / f".corridorkey-{output.stem}"


def clip_manifest_path(workspace: Path) -> Path:
    return workspace / "manifest.json"


def write_clip_manifest(
    workspace: Path,
    *,
    frame_count: int,
    fps: float,
    corridor_input: str,
) -> None:
    workspace.mkdir(parents=True, exist_ok=True)
    clip_manifest_path(workspace).write_text(
        json.dumps(
            {
                "frame_count": frame_count,
                "fps": fps,
                "corridor_input": corridor_input,
            },
            indent=2,
        )
        + "\n",
        encoding="utf-8",
    )


def read_clip_manifest(workspace: Path) -> dict[str, object]:
    path = clip_manifest_path(workspace)
    if not path.is_file():
        raise RuntimeError(f"missing clip manifest for --clip-root retry: {path}")
    return json.loads(path.read_text(encoding="utf-8"))


def remove_clip_workspace(workspace: Path) -> None:
    if workspace.is_dir():
        shutil.rmtree(workspace, ignore_errors=True)


def count_saved_fg_frames(fg_dir: Path) -> int:
    if not fg_dir.is_dir():
        return 0
    return sum(1 for entry in fg_dir.iterdir() if entry.suffix.lower() == ".exr")


def corridorkey_benchmark(corridor_input: str) -> dict[str, float]:
    return CORRIDORKEY_BENCHMARKS.get(
        corridor_input,
        {"first_frame_s": 120.0, "total_s": 900.0},
    )


def get_gpu_snapshot() -> dict[str, object]:
    """Best-effort GPU/VRAM snapshot for progress JSON (ROCm or CUDA)."""
    snapshot: dict[str, object] = {}
    for command in (
        ["rocm-smi", "--showuse"],
        ["nvidia-smi", "--query-gpu=utilization.gpu,memory.used,memory.total", "--format=csv,noheader,nounits"],
    ):
        try:
            completed = subprocess.run(
                command,
                capture_output=True,
                text=True,
                timeout=5,
                check=False,
            )
        except (OSError, subprocess.TimeoutExpired):
            continue
        if completed.returncode != 0 or not completed.stdout.strip():
            continue
        if command[0] == "rocm-smi":
            for line in completed.stdout.splitlines():
                if "GPU use" in line or "GPU[0]" in line:
                    parts = [part.strip() for part in line.replace("%", "").split() if part.strip()]
                    for index, part in enumerate(parts):
                        if part.isdigit() and index + 1 < len(parts) and parts[index + 1] == "GPU":
                            snapshot["gpu_use_pct"] = int(part)
                            break
        else:
            row = completed.stdout.strip().split("\n")[0]
            util, used, total = [part.strip() for part in row.split(",")]
            snapshot["gpu_use_pct"] = float(util)
            snapshot["gpu_vram_used_mb"] = float(used)
            snapshot["gpu_vram_total_mb"] = float(total)
        break
    try:
        completed = subprocess.run(
            ["pgrep", "-f", "ComfyUI/main.py"],
            capture_output=True,
            text=True,
            timeout=3,
            check=False,
        )
        snapshot["comfyui_running"] = completed.returncode == 0
    except (OSError, subprocess.TimeoutExpired):
        snapshot["comfyui_running"] = None
    return snapshot


def check_compositing_gpu_preflight(
    corridor_input: str,
    started_at: float,
) -> dict[str, object]:
    """Log GPU state before long CorridorKey runs; optional hard fail on contention."""
    snapshot = get_gpu_snapshot()
    benchmark = corridorkey_benchmark(corridor_input)
    telemetry(
        started_at,
        f"CorridorKey preflight corridor_input={corridor_input} "
        f"expected_first_frame_sec={benchmark['first_frame_s']:.0f} "
        f"expected_total_sec={benchmark['total_s']:.0f} "
        f"gpu={json.dumps(snapshot, sort_keys=True)}",
    )
    require_headroom = os.environ.get("VIDEO_COMPOSITE_REQUIRE_GPU_HEADROOM", "").strip() in {
        "1",
        "true",
        "yes",
    }
    if require_headroom and snapshot.get("comfyui_running"):
        raise RuntimeError(
            "CorridorKey preflight failed: ComfyUI is running and "
            "VIDEO_COMPOSITE_REQUIRE_GPU_HEADROOM=1"
        )
    return snapshot


def telemetry_json(started_at: float, payload: dict[str, object]) -> None:
    """Structured progress line (companion to human-readable telemetry())."""
    elapsed = time.monotonic() - started_at
    timestamp = time.strftime("%H:%M:%S")
    body = dict(payload)
    body["elapsed_s"] = round(elapsed, 1)
    print(
        f"[{timestamp}] elapsed={elapsed:8.1f}s TELEMETRY_JSON={json.dumps(body, sort_keys=True)}",
        flush=True,
    )


def corridor_python(root: Path) -> Path:
    executable = "python.exe" if os.name == "nt" else "python"
    path = root / ".venv" / ("Scripts" if os.name == "nt" else "bin") / executable
    return checked_path(path, "CorridorKey virtual-environment Python")


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as source:
        while chunk := source.read(1024 * 1024):
            digest.update(chunk)
    return digest.hexdigest()


def verify_corridorkey(root: Path) -> Path:
    resolved = root.resolve()
    checkpoint = resolved / "CorridorKeyModule" / "checkpoints" / CORRIDORKEY_CHECKPOINT
    checked_path(checkpoint, "Pinned CorridorKey checkpoint")
    checkpoint_sha256 = sha256_file(checkpoint)
    if checkpoint_sha256 != CORRIDORKEY_CHECKPOINT_SHA256:
        raise RuntimeError(
            f"CorridorKey checkpoint SHA-256 is {checkpoint_sha256}; "
            f"expected {CORRIDORKEY_CHECKPOINT_SHA256}"
        )
    # Container/adapter path: skip git pin when already running inside approved env.
    if os.environ.get(CORRIDORKEY_ENV_MARKER) == "1":
        return resolved
    git_dir = resolved / ".git"
    if not git_dir.exists():
        raise RuntimeError(f"CorridorKey checkout is not a Git repository: {resolved}")
    revision = subprocess.run(
        ["git", "-C", str(resolved), "rev-parse", "HEAD"],
        check=True,
        capture_output=True,
        text=True,
    ).stdout.strip()
    if revision != CORRIDORKEY_COMMIT:
        raise RuntimeError(
            f"CorridorKey revision is {revision}; expected pinned {CORRIDORKEY_COMMIT}"
        )
    corridor_python(resolved)
    return resolved


def configure_composite_hip_allocator() -> str:
    """Force the large-memory HIP allocator. Ignore inherited pressure knobs."""
    os.environ["PYTORCH_HIP_ALLOC_CONF"] = HIGH_RESOURCE_HIP_ALLOC_CONF
    return os.environ["PYTORCH_HIP_ALLOC_CONF"]


def reexecute_in_corridor_environment(args: argparse.Namespace) -> None:
    if os.environ.get(CORRIDORKEY_ENV_MARKER) == "1":
        return
    root = verify_corridorkey(args.corridorkey_root)
    environment = os.environ.copy()
    environment[CORRIDORKEY_ENV_MARKER] = "1"
    environment["OPENCV_IO_ENABLE_OPENEXR"] = "1"
    completed = subprocess.run(
        [str(corridor_python(root)), str(Path(__file__).resolve()), *sys.argv[1:]],
        env=environment,
    )
    raise SystemExit(completed.returncode)


def largest_foreground_component(mask: "np.ndarray") -> "np.ndarray":
    count, labels, stats, _ = cv2.connectedComponentsWithStats(
        mask.astype("uint8"), connectivity=8
    )
    if count <= 1:
        raise RuntimeError("coarse chroma key did not find a foreground subject")
    label = 1 + int(np.argmax(stats[1:, cv2.CC_STAT_AREA]))
    return labels == label


def coarse_alpha_hint(frame_bgr: "np.ndarray") -> "np.ndarray":
    """Build the intentionally coarse subject hint expected by CorridorKey."""
    hsv = cv2.cvtColor(frame_bgr, cv2.COLOR_BGR2HSV)
    hue = hsv[:, :, 0]
    saturation = hsv[:, :, 1]
    green_screen = (hue >= 25) & (hue <= 95) & (saturation >= 40)
    foreground = largest_foreground_component(~green_screen)
    kernel = cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (3, 3))
    foreground = cv2.morphologyEx(
        foreground.astype("uint8"), cv2.MORPH_CLOSE, kernel
    )
    foreground = cv2.erode(foreground, kernel, iterations=1)
    alpha = cv2.GaussianBlur(foreground.astype("float32"), (0, 0), 1.0)
    return np.clip(alpha * 255.0, 0, 255).astype("uint8")


def crop_to_aspect(
    image: "np.ndarray",
    target_width: int,
    target_height: int,
) -> "np.ndarray":
    """Center-crop an image so its pixel aspect matches the target canvas."""
    source_height, source_width = image.shape[:2]
    source_ratio = source_width / source_height
    target_ratio = target_width / target_height
    if source_ratio > target_ratio:
        cropped_width = round(source_height * target_ratio)
        left = max((source_width - cropped_width) // 2, 0)
        return image[:, left : left + cropped_width]
    if source_ratio < target_ratio:
        cropped_height = round(source_width / target_ratio)
        top = max((source_height - cropped_height) // 2, 0)
        return image[top : top + cropped_height, :]
    return image


def assert_same_aspect(
    width_a: int,
    height_a: int,
    width_b: int,
    height_b: int,
    *,
    label_a: str,
    label_b: str,
    epsilon: float = 1e-4,
) -> None:
    """Reject anisotropic canvases — never stretch to fit."""
    ratio_a = width_a / height_a
    ratio_b = width_b / height_b
    if abs(ratio_a - ratio_b) > epsilon:
        raise ValueError(
            f"aspect mismatch: {label_a}={width_a}x{height_a} (ratio={ratio_a:.6f}) "
            f"vs {label_b}={width_b}x{height_b} (ratio={ratio_b:.6f}); "
            f"refusing to stretch"
        )


def uniform_resize_rgb(
    image: "np.ndarray",
    width: int,
    height: int,
    *,
    interpolation: int,
) -> "np.ndarray":
    source_height, source_width = image.shape[:2]
    if source_width == width and source_height == height:
        return image
    assert_same_aspect(
        source_width,
        source_height,
        width,
        height,
        label_a="source",
        label_b="canvas",
    )
    return cv2.resize(image, (width, height), interpolation=interpolation)


def release_torch_gpu(started_at: float) -> None:
    """Drop CorridorKey's HIP allocations before the BasicVSR++ child starts."""
    import gc

    gc.collect()
    try:
        import torch
    except ImportError:
        return
    if not torch.cuda.is_available():
        return
    torch.cuda.synchronize()
    torch.cuda.empty_cache()
    gc.collect()
    telemetry(started_at, "CorridorKey released CUDA/HIP cache")


def basicvsrpp_python() -> Path:
    venv = Path(os.environ.get("VIDEO_BASICVSRPP_VENV", DEFAULT_BASICVSRPP_VENV))
    python = venv / "bin" / "python"
    if not python.is_file():
        raise FileNotFoundError(
            f"BasicVSR++ interpreter missing: {python}. "
            "VSR must run in VIDEO_BASICVSRPP_VENV, not in the CorridorKey process."
        )
    return python


def _parse_basicvsrpp_windows(message: str) -> int | None:
    marker = "windows="
    if marker not in message:
        return None
    count = message.split(marker, 1)[1].split("/", 1)[0]
    if not count.isdigit():
        return None
    return int(count)


def basicvsrpp_watchdog_stall(
    *,
    windows_done: int,
    window_total: int,
    no_progress_sec: float,
    stall_sec: float,
    first_window_stall_sec: float,
    tile_w: int,
    tile_h: int,
    length: int,
) -> RuntimeError | None:
    """Abort only while inference windows are still outstanding.

    After the last window the child stacks/saves HR npy with no new windows=
    lines. On a 1412-frame 4x clip that file is ~25 GiB. Treating that gap as a
    stall (job bb354dad, windows=95/95, 181s) killed a finished upscale.
    """
    if window_total > 0 and windows_done >= window_total:
        return None
    if windows_done == 0 and no_progress_sec >= first_window_stall_sec:
        return RuntimeError(
            f"basicvsrpp STALL windows=0/{window_total} "
            f"no_progress_sec={no_progress_sec:.0f} "
            f"first_window_stall_sec={first_window_stall_sec:.0f} "
            f"tile={tile_w}x{tile_h} length={length}"
        )
    if windows_done > 0 and no_progress_sec >= stall_sec:
        return RuntimeError(
            f"basicvsrpp STALL windows={windows_done}/{window_total} "
            f"no_progress_sec={no_progress_sec:.0f} stall_sec={stall_sec:.0f}"
        )
    return None


def _basicvsrpp_line_is_progress(line: str, windows_done: int | None) -> bool:
    if windows_done is not None:
        return True
    lowered = line.lower()
    return " complete " in f" {lowered} " or " saving " in f" {lowered} "


def _stop_basicvsrpp_child(proc: subprocess.Popen[str]) -> None:
    if proc.poll() is not None:
        return
    proc.terminate()
    try:
        proc.wait(timeout=8)
    except subprocess.TimeoutExpired:
        proc.kill()
        proc.wait(timeout=5)


def _run_basicvsrpp_upscale(
    frames_rgb: list["np.ndarray"],
    device: str,
    checkpoint_dir: Path | None,
    length: int,
    tile_w: int,
    tile_h: int,
    started_at: float,
    *,
    label: str,
) -> tuple[list["np.ndarray"], float, float | None]:
    """Run BasicVSR++ in VIDEO_BASICVSRPP_VENV. Never HIP-launch in the CK process."""
    python = basicvsrpp_python()
    scripts_dir = Path(__file__).resolve().parent
    runner = scripts_dir / "basicvsrpp_fg.py"
    if not runner.is_file():
        raise FileNotFoundError(f"BasicVSR++ runner missing: {runner}")
    ckpt_dir = checkpoint_dir or Path(
        os.environ.get("VIDEO_BASICVSRPP_CHECKPOINT_DIR", "/models/basicvsrpp")
    )
    stall_sec = float(
        os.environ.get("VIDEO_COMPOSITE_BVSRPP_STALL_SEC", DEFAULT_BVSRPP_STALL_SEC)
    )
    first_window_stall_sec = float(
        os.environ.get(
            "VIDEO_COMPOSITE_BVSRPP_FIRST_WINDOW_STALL_SEC",
            DEFAULT_BVSRPP_FIRST_WINDOW_STALL_SEC,
        )
    )
    frame_count = len(frames_rgb)
    window_total = math.ceil(frame_count / length)
    env = os.environ.copy()
    env["PYTHONUNBUFFERED"] = "1"
    env["PYTHONPATH"] = str(scripts_dir)
    env["VIDEO_BASICVSRPP_VENV"] = str(python.parent.parent)

    with tempfile.TemporaryDirectory(prefix="bvsrpp-") as temp_name:
        temp_dir = Path(temp_name)
        xdg_cache = temp_dir / "xdg"
        xdg_cache.mkdir()
        env["XDG_CACHE_HOME"] = str(xdg_cache)
        lq_path = temp_dir / "lq.npy"
        hr_path = temp_dir / "hr.npy"
        np.save(lq_path, np.stack(frames_rgb).astype("float32", copy=False))
        command = [
            str(python),
            str(runner),
            "--input-npy",
            str(lq_path),
            "--output-npy",
            str(hr_path),
            "--device",
            device,
            "--checkpoint-dir",
            str(ckpt_dir),
            "--length",
            str(length),
            "--tile-w",
            str(tile_w),
            "--tile-h",
            str(tile_h),
            "--label",
            label,
        ]
        telemetry(
            started_at,
            f"basicvsrpp {label} spawn interpreter={python} "
            f"frames={frame_count} windows={window_total}",
        )
        proc = subprocess.Popen(
            command,
            stdout=subprocess.PIPE,
            stderr=None,
            text=True,
            bufsize=1,
            close_fds=True,
            env=env,
        )
        if proc.stdout is None:
            _stop_basicvsrpp_child(proc)
            raise RuntimeError("BasicVSR++ child did not provide stdout")
        line_queue: queue.Queue[str | None] = queue.Queue()

        def _pump_stdout() -> None:
            assert proc.stdout is not None
            for raw in proc.stdout:
                line_queue.put(raw)
            line_queue.put(None)

        threading.Thread(target=_pump_stdout, name="bvsrpp-stdout", daemon=True).start()
        upscale_started = time.monotonic()
        progress_state = {
            "windows": 0,
            "last_progress": upscale_started,
            "abnormal_logged": False,
        }
        stall_error: RuntimeError | None = None
        try:
            while True:
                try:
                    raw = line_queue.get(timeout=1.0)
                except queue.Empty:
                    raw = ""
                if raw is None:
                    break
                line = raw.strip() if raw else ""
                if line:
                    telemetry(started_at, line)
                    parsed_windows = _parse_basicvsrpp_windows(line)
                    if parsed_windows is not None:
                        progress_state["windows"] = parsed_windows
                    if _basicvsrpp_line_is_progress(line, parsed_windows):
                        progress_state["last_progress"] = time.monotonic()
                now = time.monotonic()
                no_progress_sec = now - progress_state["last_progress"]
                windows_done = progress_state["windows"]
                if windows_done == 0 and no_progress_sec >= first_window_stall_sec * 0.5:
                    if not progress_state["abnormal_logged"]:
                        telemetry(
                            started_at,
                            f"basicvsrpp ABNORMAL windows=0/{window_total} "
                            f"no_progress_sec={no_progress_sec:.0f} "
                            f"expected_first_window_sec={first_window_stall_sec * 0.25:.0f}",
                        )
                        progress_state["abnormal_logged"] = True
                stall_error = basicvsrpp_watchdog_stall(
                    windows_done=windows_done,
                    window_total=window_total,
                    no_progress_sec=no_progress_sec,
                    stall_sec=stall_sec,
                    first_window_stall_sec=first_window_stall_sec,
                    tile_w=tile_w,
                    tile_h=tile_h,
                    length=length,
                )
                if stall_error is not None:
                    break
        finally:
            if stall_error is not None:
                _stop_basicvsrpp_child(proc)
        exit_code = proc.wait()
        if stall_error is not None:
            raise stall_error
        if exit_code != 0:
            raise RuntimeError(f"BasicVSR++ child exited {exit_code}")
        if not hr_path.is_file():
            raise RuntimeError(f"BasicVSR++ child produced no output: {hr_path}")
        stacked = np.load(hr_path)
        hr_frames = [np.clip(stacked[i], 0.0, 1.0).astype("float32") for i in range(stacked.shape[0])]
        elapsed = time.monotonic() - upscale_started
        hr_h, hr_w = hr_frames[0].shape[:2]
        return hr_frames, elapsed, None


def prepare_corridor_clip(
    source: Path,
    clip_root: Path,
    started_at: float,
    max_frames: int | None,
    target_width: int,
    target_height: int,
) -> tuple[int, float, str]:
    """Decode green, crop to canvas aspect, write CorridorKey inputs at native size."""
    input_dir = clip_root / "Input"
    alpha_dir = clip_root / "AlphaHint"
    input_dir.mkdir(parents=True, exist_ok=True)
    alpha_dir.mkdir(parents=True, exist_ok=True)

    capture = cv2.VideoCapture(str(source))
    if not capture.isOpened():
        raise RuntimeError(f"cannot open source video: {source}")
    fps = capture.get(cv2.CAP_PROP_FPS)
    if fps <= 0:
        capture.release()
        raise RuntimeError(f"source video has invalid frame rate: {source}")

    telemetry(started_at, f"prepare source={source.name} fps={fps:.3f}")
    frames_bgr: list[np.ndarray] = []
    while max_frames is None or len(frames_bgr) < max_frames:
        ok, frame = capture.read()
        if not ok:
            break
        original_height, original_width = frame.shape[:2]
        frame = crop_to_aspect(frame, target_width, target_height)
        if not frames_bgr:
            telemetry(
                started_at,
                f"prepare aspect crop {original_width}x{original_height} "
                f"to {frame.shape[1]}x{frame.shape[0]} "
                f"for target={target_width}x{target_height}",
            )
        frames_bgr.append(frame)
        if len(frames_bgr) == 1 or len(frames_bgr) % 50 == 0:
            telemetry(started_at, f"prepare frames={len(frames_bgr)}")
    capture.release()
    if not frames_bgr:
        raise RuntimeError(f"source video contains no readable frames: {source}")

    for frame_index, frame in enumerate(frames_bgr):
        frame_path = input_dir / f"{frame_index:05d}.png"
        if not cv2.imwrite(str(frame_path), frame):
            raise RuntimeError(f"failed to write input frame {frame_index}")
        hint = coarse_alpha_hint(frame)
        if not cv2.imwrite(str(alpha_dir / f"{frame_index:05d}.png"), hint):
            raise RuntimeError(f"failed to write alpha hint for frame {frame_index}")
        if frame_index == 0 or (frame_index + 1) % 50 == 0:
            telemetry(
                started_at,
                f"prepare write frames={frame_index + 1}/{len(frames_bgr)}",
            )

    frame_count = len(frames_bgr)
    corridor_input = f"{frames_bgr[0].shape[1]}x{frames_bgr[0].shape[0]}"
    telemetry(
        started_at,
        f"prepare complete frames={frame_count} corridor_input={corridor_input}",
    )
    return frame_count, fps, corridor_input


def quarantine_incomplete_fg(fg_dir: Path, frame_total: int, started_at: float) -> int:
    """Move partial FG EXRs from an aborted run aside (preserve, do not delete)."""
    if not fg_dir.is_dir():
        return 0
    exrs = sorted(fg_dir.glob("*.exr"))
    if not exrs or len(exrs) >= frame_total:
        return len(exrs)
    stale_dir = fg_dir / ".prior-run"
    stale_dir.mkdir(parents=True, exist_ok=True)
    for path in exrs:
        shutil.move(str(path), str(stale_dir / path.name))
    telemetry(
        started_at,
        f"CorridorKey quarantined partial_fg={len(exrs)}/{frame_total} dir={stale_dir}",
    )
    return 0


def run_corridorkey(
    corridor_root: Path,
    clip_root: Path,
    device: str,
    max_frames: int | None,
    started_at: float,
    *,
    corridor_input: str,
    frame_total: int,
    progress: ProgressTracker | None,
) -> None:
    sys.path.insert(0, str(corridor_root))
    previous_directory = Path.cwd()
    fg_dir = clip_root / "Output" / "FG"
    benchmark = corridorkey_benchmark(corridor_input)
    stall_sec = float(
        os.environ.get("VIDEO_COMPOSITE_CK_STALL_SEC", DEFAULT_CK_STALL_SEC)
    )
    first_frame_stall_sec = float(
        os.environ.get(
            "VIDEO_COMPOSITE_CK_FIRST_FRAME_STALL_SEC",
            max(DEFAULT_CK_FIRST_FRAME_STALL_SEC, benchmark["first_frame_s"] * 2.5),
        )
    )
    os.chdir(corridor_root)
    try:
        from clip_manager import ClipEntry, InferenceSettings, run_inference

        clip = ClipEntry(clip_root.name, str(clip_root))
        clip.find_assets()
        clip.validate_pair()
        quarantined = quarantine_incomplete_fg(fg_dir, frame_total, started_at)
        check_compositing_gpu_preflight(corridor_input, started_at)
        telemetry(
            started_at,
            f"CorridorKey loading engine device={device} image_size=2048 "
            f"corridor_input={corridor_input} "
            f"expected_first_frame_sec={benchmark['first_frame_s']:.0f} "
            f"expected_total_sec={benchmark['total_s']:.0f} "
            f"stall_sec={stall_sec:.0f} first_frame_stall_sec={first_frame_stall_sec:.0f}",
        )
        if progress is not None:
            progress.update(
                stage="corridorkey_loading",
                corridor_input=corridor_input,
                frames_total=frame_total,
                frames_saved=0,
                benchmark=benchmark,
            )
        settings = InferenceSettings(
            input_is_linear=False,
            despill_strength=0.5,
            auto_despeckle=False,
            refiner_scale=1.0,
            generate_comp=False,
            gpu_post_processing=True,
            image_size=2048,
            screen_color="green",
        )
        inference_started_at = time.monotonic()
        last_report = time.monotonic()
        stop_heartbeat = threading.Event()
        stall_error: list[BaseException] = []
        progress_lock = threading.Lock()
        last_saved_fg = quarantined
        last_saved_change_at = inference_started_at
        current_frame_index: int | None = None
        current_frame_started_at: float | None = None
        abnormal_logged = False

        def _publish_progress(extra: dict[str, object]) -> None:
            if progress is None:
                return
            with progress_lock:
                payload = {
                    "stage": "corridorkey",
                    "corridor_input": corridor_input,
                    "frames_total": frame_total,
                    "frames_saved": last_saved_fg,
                    "inference_elapsed_s": round(
                        time.monotonic() - inference_started_at, 1
                    ),
                    "current_frame_index": current_frame_index,
                    "benchmark": benchmark,
                    **extra,
                }
                if current_frame_started_at is not None:
                    payload["current_frame_elapsed_s"] = round(
                        time.monotonic() - current_frame_started_at, 1
                    )
                gpu = get_gpu_snapshot()
                if gpu:
                    payload["gpu"] = gpu
                progress.update(**payload)

        def on_clip_start(name: str, total: int) -> None:
            telemetry(
                started_at,
                f"CorridorKey start clip={name} frames={total} "
                f"corridor_input={corridor_input} "
                f"expected_first_frame_sec={benchmark['first_frame_s']:.0f} "
                f"expected_total_sec={benchmark['total_s']:.0f}",
            )
            telemetry_json(
                started_at,
                {
                    "event": "corridorkey_start",
                    "clip": name,
                    "frames_total": total,
                    "corridor_input": corridor_input,
                    "benchmark": benchmark,
                },
            )
            _publish_progress({"frames_total": total, "frames_saved": 0})

        def on_frame_start(frame_index: int, total: int) -> None:
            nonlocal current_frame_index, current_frame_started_at
            current_frame_index = frame_index
            current_frame_started_at = time.monotonic()
            telemetry(
                started_at,
                f"CorridorKey frame_start index={frame_index}/{total} "
                f"corridor_input={corridor_input}",
            )
            telemetry_json(
                started_at,
                {
                    "event": "corridorkey_frame_start",
                    "frame_index": frame_index,
                    "frames_total": total,
                    "corridor_input": corridor_input,
                },
            )
            _publish_progress({"current_frame_index": frame_index})

        def on_frame_complete(frame_index: int, total: int) -> None:
            nonlocal last_report, last_saved_fg, last_saved_change_at
            nonlocal current_frame_index, current_frame_started_at
            completed = frame_index + 1
            now = time.monotonic()
            frame_elapsed = (
                now - current_frame_started_at
                if current_frame_started_at is not None
                else 0.0
            )
            telemetry(
                started_at,
                f"CorridorKey frame_saved index={frame_index} "
                f"elapsed={frame_elapsed:.1f}s",
            )
            last_saved_fg = completed
            last_saved_change_at = now
            current_frame_index = None
            current_frame_started_at = None
            if (
                completed != total
                and completed != 1
                and completed % 10 != 0
                and now - last_report < 5
            ):
                _publish_progress({"frames_saved": completed})
                return
            inference_elapsed = now - inference_started_at
            rate = completed / max(inference_elapsed, 0.001)
            eta = (total - completed) / max(rate, 0.001)
            telemetry(
                started_at,
                f"CorridorKey frames={completed}/{total} "
                f"rate={rate:.2f}fps eta={eta:.1f}s",
            )
            telemetry_json(
                started_at,
                {
                    "event": "corridorkey_frame_saved",
                    "frame_index": frame_index,
                    "frames_saved": completed,
                    "frames_total": total,
                    "frame_elapsed_s": round(frame_elapsed, 1),
                    "inference_rate_fps": round(rate, 3),
                    "inference_eta_s": round(eta, 1),
                },
            )
            _publish_progress(
                {
                    "frames_saved": completed,
                    "inference_rate_fps": round(rate, 3),
                    "inference_eta_s": round(eta, 1),
                }
            )
            last_report = now

        def corridorkey_heartbeat() -> None:
            nonlocal last_saved_fg, last_saved_change_at, abnormal_logged
            while not stop_heartbeat.wait(10.0):
                if stall_error:
                    return
                saved = count_saved_fg_frames(fg_dir)
                now = time.monotonic()
                elapsed = now - inference_started_at
                if saved != last_saved_fg:
                    last_saved_fg = saved
                    last_saved_change_at = now
                no_progress_sec = now - last_saved_change_at
                rate = saved / max(elapsed, 0.001)
                eta = (frame_total - saved) / max(rate, 0.001) if saved else None
                eta_text = f" eta={eta:.0f}s" if eta is not None else ""
                frame_elapsed_text = ""
                if current_frame_index is not None and current_frame_started_at is not None:
                    frame_elapsed = now - current_frame_started_at
                    frame_elapsed_text = (
                        f" current_frame={current_frame_index} "
                        f"current_frame_elapsed={frame_elapsed:.0f}s"
                    )
                telemetry(
                    started_at,
                    f"CorridorKey frames={saved}/{frame_total} "
                    f"rate={rate:.2f}fps{eta_text} "
                    f"inference_elapsed={elapsed:.0f}s "
                    f"no_progress_sec={no_progress_sec:.0f}s{frame_elapsed_text} "
                    f"heartbeat=1",
                )
                gpu = get_gpu_snapshot()
                telemetry_json(
                    started_at,
                    {
                        "event": "corridorkey_heartbeat",
                        "frames_saved": saved,
                        "frames_total": frame_total,
                        "inference_elapsed_s": round(elapsed, 1),
                        "no_progress_sec": round(no_progress_sec, 1),
                        "current_frame_index": current_frame_index,
                        "gpu": gpu,
                    },
                )
                _publish_progress({"frames_saved": saved, "gpu": gpu})
                if (
                    saved == 0
                    and elapsed > benchmark["first_frame_s"] * 2
                    and no_progress_sec >= 30.0
                    and not abnormal_logged
                ):
                    abnormal_logged = True
                    telemetry(
                        started_at,
                        f"CorridorKey ABNORMAL saved_fg=0/{frame_total} "
                        f"inference_elapsed={elapsed:.0f}s "
                        f"expected_first_frame_sec={benchmark['first_frame_s']:.0f} "
                        f"gpu={json.dumps(gpu, sort_keys=True)}",
                    )
                # First-frame wall-clock is not a hang. Frame 0 is engine + first
                # MIOpen kernel; aborting it was added with BasicVSR++ and failed
                # jobs that were still computing (7394a71 had no first-frame STALL).
                # Mid-run stall only after first FG exists.
                if saved > 0 and saved < frame_total and no_progress_sec >= stall_sec:
                    message = (
                        f"CorridorKey STALL saved_fg={saved}/{frame_total} "
                        f"no_progress_sec={no_progress_sec:.0f} stall_sec={stall_sec:.0f} "
                        f"current_frame={current_frame_index} "
                        f"gpu={json.dumps(gpu, sort_keys=True)}"
                    )
                    telemetry(started_at, message)
                    stall_error.append(RuntimeError(message))
                    return

        heartbeat_thread = threading.Thread(
            target=corridorkey_heartbeat,
            daemon=True,
            name="corridorkey-heartbeat",
        )
        heartbeat_thread.start()
        inference_kwargs: dict[str, object] = {
            "device": device,
            "backend": "torch",
            "max_frames": max_frames,
            "settings": settings,
            "on_clip_start": on_clip_start,
            "on_frame_complete": on_frame_complete,
        }
        if "on_frame_start" in inspect.signature(run_inference).parameters:
            inference_kwargs["on_frame_start"] = on_frame_start
        else:
            telemetry(
                started_at,
                "CorridorKey WARNING on_frame_start unsupported — "
                "patch clip_manager.run_inference in corridorkey-root",
            )
        try:
            run_inference([clip], **inference_kwargs)
        finally:
            stop_heartbeat.set()
            heartbeat_thread.join(timeout=2.0)
            release_torch_gpu(started_at)
        if stall_error:
            raise stall_error[0]
        telemetry(started_at, "CorridorKey inference complete")
        if progress is not None:
            progress.update(
                stage="corridorkey_complete",
                frames_saved=frame_total,
                inference_elapsed_s=round(
                    time.monotonic() - inference_started_at, 1
                ),
            )
    finally:
        os.chdir(previous_directory)


def srgb_to_linear(image: "np.ndarray") -> "np.ndarray":
    return np.where(
        image <= 0.04045,
        image / 12.92,
        np.power((image + 0.055) / 1.055, 2.4),
    )


def linear_to_srgb(image: "np.ndarray") -> "np.ndarray":
    image = np.maximum(image, 0.0)
    return np.where(
        image <= 0.0031308,
        image * 12.92,
        1.055 * np.power(image, 1.0 / 2.4) - 0.055,
    )


def cover_plate(plate_bgr: "np.ndarray", width: int, height: int) -> "np.ndarray":
    source_height, source_width = plate_bgr.shape[:2]
    scale = max(width / source_width, height / source_height)
    resized = cv2.resize(
        plate_bgr,
        (round(source_width * scale), round(source_height * scale)),
        interpolation=cv2.INTER_LANCZOS4,
    )
    x = (resized.shape[1] - width) // 2
    y = (resized.shape[0] - height) // 2
    return resized[y : y + height, x : x + width]


def load_canvas_plate_linear(
    plate_path: Path,
    width: int,
    height: int,
    blur_sigma: float,
    started_at: float,
    preview_path: Path,
) -> "np.ndarray":
    """Cover-fit the backplate to the canvas and apply focus blur. Never touches FG."""
    plate_bgr = cv2.imread(str(plate_path), cv2.IMREAD_COLOR)
    if plate_bgr is None:
        raise RuntimeError(f"cannot read background plate: {plate_path}")
    plate_rgb = cv2.cvtColor(
        cover_plate(plate_bgr, width, height), cv2.COLOR_BGR2RGB
    ).astype("float32") / 255.0
    plate_linear = srgb_to_linear(plate_rgb)
    if blur_sigma:
        plate_linear = cv2.GaussianBlur(plate_linear, (0, 0), blur_sigma)
    preview_bgr = cv2.cvtColor(
        np.clip(linear_to_srgb(plate_linear) * 255.0, 0, 255).astype("uint8"),
        cv2.COLOR_RGB2BGR,
    )
    preview_path.parent.mkdir(parents=True, exist_ok=True)
    if not cv2.imwrite(str(preview_path), preview_bgr):
        raise RuntimeError(f"failed to write plate preview: {preview_path}")
    telemetry(
        started_at,
        f"prepare plate-focus-blur sigma={blur_sigma:.2f} "
        f"canvas={width}x{height} preview={preview_path.name} "
        f"avatar_blur=off",
    )
    return plate_linear


def start_encoder(
    output: Path,
    source: Path,
    width: int,
    height: int,
    fps: float,
    *,
    master: bool,
) -> subprocess.Popen:
    arguments = [
        "ffmpeg",
        "-y",
        "-hide_banner",
        "-loglevel",
        "error",
        "-f",
        "rawvideo",
        "-pix_fmt",
        "bgr24",
        "-s",
        f"{width}x{height}",
        "-r",
        str(fps),
        "-i",
        "pipe:0",
        "-i",
        str(source),
        "-map",
        "0:v:0",
        "-map",
        "1:a:0?",
    ]
    if master:
        arguments.extend(
            [
                "-c:v",
                "ffv1",
                "-level",
                "3",
                "-coder",
                "1",
                "-context",
                "1",
                "-g",
                "1",
                "-slices",
                "16",
                "-slicecrc",
                "1",
                "-pix_fmt",
                "gbrp",
                "-c:a",
                "flac",
            ]
        )
    else:
        arguments.extend(
            [
                "-c:v",
                "libx264",
                "-preset",
                "slow",
                "-crf",
                "18",
                "-profile:v",
                "high",
                "-pix_fmt",
                "yuv420p",
                "-color_primaries",
                "bt709",
                "-color_trc",
                "bt709",
                "-colorspace",
                "bt709",
                "-movflags",
                "+faststart",
                "-c:a",
                "aac",
                "-b:a",
                "128k",
            ]
        )
    arguments.extend(["-shortest", str(output)])
    process = subprocess.Popen(arguments, stdin=subprocess.PIPE)
    if process.stdin is None:
        process.kill()
        process.wait()
        raise RuntimeError(f"ffmpeg did not provide an input pipe for {output}")
    return process


def load_fg_matte_sequences(
    clip_root: Path,
    frame_count: int,
    width: int,
    height: int,
) -> tuple[list["np.ndarray"], list["np.ndarray"], int, int]:
    """Load CorridorKey FG/Matte EXRs cropped to delivery aspect (LQ size)."""
    fg_dir = clip_root / "Output" / "FG"
    matte_dir = clip_root / "Output" / "Matte"
    fg_files = sorted(fg_dir.glob("*.exr"))
    matte_files = sorted(matte_dir.glob("*.exr"))
    if len(fg_files) != frame_count or len(matte_files) != frame_count:
        raise RuntimeError(
            f"CorridorKey produced FG={len(fg_files)} Matte={len(matte_files)} "
            f"frames; expected {frame_count}"
        )

    fg_frames: list[np.ndarray] = []
    matte_frames: list[np.ndarray] = []
    lq_width = 0
    lq_height = 0
    for fg_path, matte_path in zip(fg_files, matte_files):
        fg_bgr = cv2.imread(str(fg_path), cv2.IMREAD_UNCHANGED)
        if fg_bgr is None or fg_bgr.ndim != 3 or fg_bgr.shape[2] < 3:
            raise RuntimeError(f"invalid CorridorKey foreground frame: {fg_path}")
        fg_rgb = cv2.cvtColor(fg_bgr[:, :, :3], cv2.COLOR_BGR2RGB)
        fg_rgb = crop_to_aspect(fg_rgb, width, height)
        fg_rgb = np.clip(fg_rgb, 0.0, 1.0).astype("float32")

        matte = cv2.imread(str(matte_path), cv2.IMREAD_UNCHANGED)
        if matte is None:
            raise RuntimeError(f"invalid CorridorKey matte frame: {matte_path}")
        if matte.ndim == 3:
            matte = matte[:, :, 0]
        if matte.dtype == np.uint8:
            matte = matte.astype("float32") / 255.0
        else:
            matte = matte.astype("float32")
        matte = crop_to_aspect(matte, width, height)
        matte = np.clip(matte, 0.0, 1.0).astype("float32")

        if not fg_frames:
            lq_height, lq_width = fg_rgb.shape[:2]
            assert_same_aspect(
                lq_width,
                lq_height,
                width,
                height,
                label_a="lq",
                label_b="canvas",
            )
        elif fg_rgb.shape[:2] != (lq_height, lq_width):
            raise RuntimeError(
                f"FG frame size drifted: {fg_rgb.shape[1]}x{fg_rgb.shape[0]} "
                f"vs {lq_width}x{lq_height}"
            )
        if matte.shape[:2] != fg_rgb.shape[:2]:
            raise RuntimeError(
                f"matte size {matte.shape[1]}x{matte.shape[0]} != "
                f"FG {fg_rgb.shape[1]}x{fg_rgb.shape[0]}"
            )
        fg_frames.append(fg_rgb)
        matte_frames.append(matte)
    return fg_frames, matte_frames, lq_width, lq_height


def composite_outputs(
    clip_root: Path,
    source: Path,
    plate_path: Path,
    output: Path,
    master_output: Path | None,
    frame_count: int,
    fps: float,
    width: int,
    height: int,
    background_blur_sigma: float,
    foreground_sharpen_amount: float,
    foreground_sharpen_sigma: float,
    started_at: float,
    *,
    fg_upscaler: str = "lanczos",
    device: str = "cuda",
    basicvsrpp_checkpoint_dir: Path | None = None,
    basicvsrpp_length: int = 15,
    basicvsrpp_tile_w: int = 0,
    basicvsrpp_tile_h: int = 0,
    plate_linear: "np.ndarray" | None = None,
) -> dict[str, float]:
    """Composite keyed FG over plate. Upscale happens here, after CorridorKey."""
    timings: dict[str, float] = {}
    load_started = time.monotonic()
    fg_lq, matte_lq, lq_width, lq_height = load_fg_matte_sequences(
        clip_root, frame_count, width, height
    )
    timings["load_exr"] = time.monotonic() - load_started

    if fg_upscaler == "basicvsrpp":
        fg_hr, fg_upscale_elapsed, _peak = _run_basicvsrpp_upscale(
            fg_lq,
            device,
            basicvsrpp_checkpoint_dir,
            basicvsrpp_length,
            basicvsrpp_tile_w,
            basicvsrpp_tile_h,
            started_at,
            label="fg",
        )
        timings["fg_upscale"] = fg_upscale_elapsed
        vsr_h, vsr_w = fg_hr[0].shape[:2]
        assert_same_aspect(
            vsr_w,
            vsr_h,
            width,
            height,
            label_a="vsr4x",
            label_b="canvas",
        )
        matte_hr = [
            cv2.resize(matte, (vsr_w, vsr_h), interpolation=cv2.INTER_LINEAR)
            for matte in matte_lq
        ]
        telemetry(
            started_at,
            f"geometry keyed_fg={lq_width}x{lq_height} vsr4x={vsr_w}x{vsr_h} "
            f"canvas={width}x{height} "
            f"aspect_fg={lq_width / lq_height:.6f} "
            f"aspect_canvas={width / height:.6f}",
        )
        telemetry(
            started_at,
            f"stage fg_upscale elapsed={fg_upscale_elapsed:.1f}s "
            f"(BasicVSR++ after CorridorKey)",
        )
        fg_lq, matte_lq = fg_hr, matte_hr
        lq_width, lq_height = vsr_w, vsr_h
    elif fg_upscaler != "lanczos":
        raise ValueError(f"unsupported fg-upscaler: {fg_upscaler}")

    fit_started = time.monotonic()
    assert_same_aspect(
        lq_width,
        lq_height,
        width,
        height,
        label_a="keyed_fg",
        label_b="canvas",
    )
    telemetry(
        started_at,
        f"geometry keyed_fg={lq_width}x{lq_height} canvas={width}x{height} "
        f"aspect_fg={lq_width / lq_height:.6f} aspect_canvas={width / height:.6f}",
    )
    fg_srgb_frames = [
        uniform_resize_rgb(fg_rgb, width, height, interpolation=cv2.INTER_LANCZOS4)
        for fg_rgb in fg_lq
    ]
    alpha_frames = [
        cv2.resize(matte, (width, height), interpolation=cv2.INTER_LINEAR)
        for matte in matte_lq
    ]
    timings["fit_canvas"] = time.monotonic() - fit_started

    if plate_linear is None:
        raise RuntimeError(
            "plate_linear is required; load_canvas_plate_linear must run before CorridorKey"
        )
    telemetry(
        started_at,
        f"composite start frames={frame_count} "
        f"delivery={output.name} "
        f"master={master_output.name if master_output is not None else 'disabled'} "
        f"plate={plate_path.name} "
        f"background_blur_sigma={background_blur_sigma:.2f} "
        f"plate_blur=before_ck "
        f"foreground_sharpen_amount={foreground_sharpen_amount:.2f} "
        f"foreground_sharpen_sigma={foreground_sharpen_sigma:.2f}",
    )

    encoders: list[subprocess.Popen] = []
    composite_started_at = time.monotonic()
    last_report = composite_started_at
    try:
        delivery = start_encoder(output, source, width, height, fps, master=False)
        encoders.append(delivery)
        if master_output is not None:
            master = start_encoder(
                master_output, source, width, height, fps, master=True
            )
            encoders.append(master)
        for frame_index, (fg_srgb, alpha) in enumerate(
            zip(fg_srgb_frames, alpha_frames)
        ):
            if foreground_sharpen_amount:
                fg_blurred = cv2.GaussianBlur(
                    fg_srgb, (0, 0), foreground_sharpen_sigma
                )
                fg_srgb = np.clip(
                    fg_srgb
                    + foreground_sharpen_amount * (fg_srgb - fg_blurred),
                    0.0,
                    1.0,
                )
            alpha = np.clip(alpha, 0.0, 1.0)[:, :, None]
            fg_linear = srgb_to_linear(fg_srgb)
            premultiplied = fg_linear * alpha
            composite_linear = premultiplied + plate_linear * (1.0 - alpha)
            composite_rgb = np.clip(
                linear_to_srgb(composite_linear) * 255.0, 0, 255
            ).astype("uint8")
            frame_bytes = cv2.cvtColor(composite_rgb, cv2.COLOR_RGB2BGR).tobytes()
            for encoder in encoders:
                if encoder.stdin is None:
                    raise RuntimeError("ffmpeg encoder input pipe is unavailable")
                encoder.stdin.write(frame_bytes)
            completed = frame_index + 1
            now = time.monotonic()
            if completed == 1 or completed == frame_count or now - last_report >= 5:
                composite_elapsed = now - composite_started_at
                rate = completed / max(composite_elapsed, 0.001)
                eta = (frame_count - completed) / max(rate, 0.001)
                telemetry(
                    started_at,
                    f"composite frames={completed}/{frame_count} "
                    f"rate={rate:.2f}fps eta={eta:.1f}s",
                )
                last_report = now
    finally:
        for encoder in encoders:
            if encoder.stdin is not None:
                encoder.stdin.close()
        statuses = [encoder.wait() for encoder in encoders]
    if any(status != 0 for status in statuses):
        raise RuntimeError(f"ffmpeg encoder exited with statuses {statuses}")
    timings["encode"] = time.monotonic() - composite_started_at
    telemetry(
        started_at,
        f"composite complete frames={frame_count} "
        f"encode_elapsed={timings['encode']:.1f}s "
        f"fit_elapsed={timings.get('fit_canvas', 0.0):.1f}s",
    )
    return timings


def main() -> int:
    hip_alloc_conf = configure_composite_hip_allocator()
    args = parse_args()
    resolve_focus_defaults(args)
    reexecute_in_corridor_environment(args)
    os.environ.setdefault("MIOPEN_LOG_LEVEL", "0")

    started_at = time.monotonic()
    global cv2, np
    import cv2
    import numpy as np

    source = checked_path(args.source, "source video")
    plate = checked_path(args.plate, "background plate")
    corridor_root = verify_corridorkey(args.corridorkey_root)
    if args.width <= 0 or args.height <= 0:
        raise ValueError("output dimensions must be positive")
    if args.max_frames is not None and args.max_frames <= 0:
        raise ValueError("max-frames must be positive")
    if args.background_blur_sigma < 0:
        raise ValueError("background-blur-sigma must be non-negative")
    if args.foreground_sharpen_amount < 0 or args.foreground_sharpen_amount > 1:
        raise ValueError("foreground-sharpen-amount must be between 0 and 1")
    if args.foreground_sharpen_sigma <= 0:
        raise ValueError("foreground-sharpen-sigma must be positive")
    if args.basicvsrpp_length < 1:
        raise ValueError("basicvsrpp-length must be >= 1")
    if args.basicvsrpp_tile_w < 0 or args.basicvsrpp_tile_h < 0:
        raise ValueError("basicvsrpp tile sizes must be non-negative")
    if (
        args.master_output is not None
        and args.master_output.resolve() == args.output.resolve()
    ):
        raise ValueError("--master-output must differ from --output")

    args.output.resolve().parent.mkdir(parents=True, exist_ok=True)
    progress = ProgressTracker(
        args.output.with_name(f"{args.output.stem}-progress.json")
    )
    progress.update(
        stage="start",
        source=source.name,
        plate=plate.name,
        output=args.output.name,
        fg_upscaler=args.fg_upscaler,
        canvas=f"{args.width}x{args.height}",
    )
    telemetry(
        started_at,
        f"start source={source.name} output={args.output.resolve().name} "
        f"fg_upscaler={args.fg_upscaler} canvas={args.width}x{args.height} "
        f"progress={progress.path.name} hip_alloc={hip_alloc_conf}",
    )

    current_stage = "start"
    workspace = clip_workspace_for_output(args.output.resolve())
    clip_root = workspace / "talking-head"
    prepare_elapsed = 0.0
    fg_upscale_elapsed = 0.0
    corridorkey_elapsed = 0.0
    composite_timings: dict[str, float] = {}

    try:
        if args.clip_root is not None:
            current_stage = "post_ck"
            clip_root = args.clip_root.resolve()
            workspace = clip_root.parent
            manifest = read_clip_manifest(workspace)
            frame_count = int(manifest["frame_count"])
            fps = float(manifest["fps"])
            corridor_input = str(manifest["corridor_input"])
            fg_count = count_saved_fg_frames(clip_root / "Output" / "FG")
            if fg_count != frame_count:
                raise RuntimeError(
                    f"--clip-root has FG={fg_count} EXRs; manifest expects {frame_count}"
                )
            progress.update(
                stage="corridorkey_complete",
                corridor_input=corridor_input,
                frames_total=frame_count,
                frames_saved=frame_count,
            )
            telemetry(
                started_at,
                f"clip-root reuse path={clip_root} frames={frame_count} "
                f"corridor_input={corridor_input}",
            )
        else:
            current_stage = "prepare"
            clip_root.mkdir(parents=True, exist_ok=True)
            prepare_started = time.monotonic()
            frame_count, fps, corridor_input = prepare_corridor_clip(
                source,
                clip_root,
                started_at,
                args.max_frames,
                args.width,
                args.height,
            )
            prepare_elapsed = time.monotonic() - prepare_started
            write_clip_manifest(
                workspace,
                frame_count=frame_count,
                fps=fps,
                corridor_input=corridor_input,
            )
            progress.update(
                stage="prepare_complete",
                corridor_input=corridor_input,
                frames_total=frame_count,
                prepare_elapsed_s=round(prepare_elapsed, 1),
                fg_upscaler=args.fg_upscaler,
            )
            telemetry(
                started_at,
                f"stage prepare elapsed={prepare_elapsed:.1f}s "
                f"clip_workspace={workspace}",
            )

        preview_path = args.output.resolve().with_name(
            f"{args.output.resolve().stem}-plate-focus-blur.png"
        )
        plate_linear = load_canvas_plate_linear(
            plate,
            args.width,
            args.height,
            args.background_blur_sigma,
            started_at,
            preview_path,
        )
        progress.update(
            plate_focus_blur_sigma=args.background_blur_sigma,
            plate_focus_blur_preview=preview_path.name,
            avatar_blur="off",
        )

        if args.clip_root is None:
            current_stage = "corridorkey"
            ck_started = time.monotonic()
            run_corridorkey(
                corridor_root,
                clip_root,
                args.device,
                args.max_frames,
                started_at,
                corridor_input=corridor_input,
                frame_total=frame_count,
                progress=progress,
            )
            corridorkey_elapsed = time.monotonic() - ck_started
            telemetry(
                started_at, f"stage CorridorKey elapsed={corridorkey_elapsed:.1f}s"
            )

        current_stage = "post_ck"
        composite_timings = composite_outputs(
            clip_root,
            source,
            plate,
            args.output.resolve(),
            args.master_output.resolve() if args.master_output is not None else None,
            frame_count,
            fps,
            args.width,
            args.height,
            args.background_blur_sigma,
            args.foreground_sharpen_amount,
            args.foreground_sharpen_sigma,
            started_at,
            fg_upscaler=args.fg_upscaler,
            device=args.device,
            basicvsrpp_checkpoint_dir=args.basicvsrpp_checkpoint_dir,
            basicvsrpp_length=args.basicvsrpp_length,
            basicvsrpp_tile_w=args.basicvsrpp_tile_w,
            basicvsrpp_tile_h=args.basicvsrpp_tile_h,
            plate_linear=plate_linear,
        )
        fg_upscale_elapsed = composite_timings.get("fg_upscale", 0.0)

        total_elapsed = time.monotonic() - started_at
        master_message = (
            f" master={args.master_output.resolve()}"
            if args.master_output is not None
            else ""
        )
        telemetry(
            started_at,
            f"complete delivery={args.output.resolve()}{master_message} "
            f"frames={frame_count} total={total_elapsed:.1f}s "
            f"prepare={prepare_elapsed:.1f}s "
            f"fg_upscale={fg_upscale_elapsed:.1f}s "
            f"CorridorKey={corridorkey_elapsed:.1f}s "
            f"encode={composite_timings.get('encode', 0.0):.1f}s",
        )
        progress.update(
            stage="complete",
            frames_total=frame_count,
            total_elapsed_s=round(total_elapsed, 1),
            timings={
                "prepare_s": round(prepare_elapsed, 1),
                "fg_upscale_s": round(fg_upscale_elapsed, 1),
                "corridorkey_s": round(corridorkey_elapsed, 1),
                "encode_s": round(composite_timings.get("encode", 0.0), 1),
            },
        )
        if not args.keep_clip_workspace:
            remove_clip_workspace(workspace)
        return 0
    except Exception as exc:
        error_text = "".join(
            traceback.format_exception(type(exc), exc, exc.__traceback__)
        ).strip()
        progress.fail(exit_code=1, failed_stage=current_stage, error=error_text)
        telemetry(
            started_at,
            f"FATAL stage={current_stage} error={exc}",
        )
        telemetry_json(
            started_at,
            {
                "event": "composite_failed",
                "failed_stage": current_stage,
                "error": str(exc),
            },
        )
        print(error_text, file=sys.stderr, flush=True)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
