"""Create a talking-head composite with CorridorKey foreground unmixing."""

from __future__ import annotations

import argparse
import hashlib
import os
import subprocess
import sys
import tempfile
import time
from pathlib import Path


CORRIDORKEY_COMMIT = "97e55a453060745bead1befd293f6e523c4b845c"
CORRIDORKEY_CHECKPOINT = "CorridorKey_v1.0.safetensors"
CORRIDORKEY_CHECKPOINT_SHA256 = (
    "74d614f7d92fc559a118c30a7deadedc3cacd8ef83dcb85a030d0bed7af8b20b"
)
CORRIDORKEY_ENV_MARKER = "GUIDEANTS_CORRIDORKEY_ENV"


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
    parser.add_argument("--background-blur-sigma", type=float, default=1.5)
    parser.add_argument("--foreground-sharpen-amount", type=float, default=0.15)
    parser.add_argument("--foreground-sharpen-sigma", type=float, default=0.8)
    return parser.parse_args()


def checked_path(path: Path, description: str) -> Path:
    resolved = path.resolve()
    if not resolved.is_file():
        raise FileNotFoundError(f"{description} does not exist: {resolved}")
    return resolved


def telemetry(started_at: float, message: str) -> None:
    elapsed = time.monotonic() - started_at
    timestamp = time.strftime("%H:%M:%S")
    print(f"[{timestamp}] elapsed={elapsed:8.1f}s {message}", flush=True)


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
    checkpoint = resolved / "CorridorKeyModule" / "checkpoints" / CORRIDORKEY_CHECKPOINT
    checked_path(checkpoint, "Pinned CorridorKey checkpoint")
    checkpoint_sha256 = sha256_file(checkpoint)
    if checkpoint_sha256 != CORRIDORKEY_CHECKPOINT_SHA256:
        raise RuntimeError(
            f"CorridorKey checkpoint SHA-256 is {checkpoint_sha256}; "
            f"expected {CORRIDORKEY_CHECKPOINT_SHA256}"
        )
    corridor_python(resolved)
    return resolved


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


def prepare_corridor_clip(
    source: Path,
    clip_root: Path,
    started_at: float,
    max_frames: int | None,
) -> tuple[int, float]:
    input_dir = clip_root / "Input"
    alpha_dir = clip_root / "AlphaHint"
    input_dir.mkdir(parents=True)
    alpha_dir.mkdir(parents=True)

    capture = cv2.VideoCapture(str(source))
    if not capture.isOpened():
        raise RuntimeError(f"cannot open source video: {source}")
    fps = capture.get(cv2.CAP_PROP_FPS)
    if fps <= 0:
        capture.release()
        raise RuntimeError(f"source video has invalid frame rate: {source}")

    telemetry(started_at, f"prepare source={source.name} fps={fps:.3f}")
    frame_count = 0
    while max_frames is None or frame_count < max_frames:
        ok, frame = capture.read()
        if not ok:
            break
        frame_path = input_dir / f"{frame_count:05d}.png"
        if not cv2.imwrite(str(frame_path), frame):
            capture.release()
            raise RuntimeError(f"failed to write input frame {frame_count}")
        hint = coarse_alpha_hint(frame)
        if not cv2.imwrite(str(alpha_dir / f"{frame_count:05d}.png"), hint):
            capture.release()
            raise RuntimeError(f"failed to write alpha hint for frame {frame_count}")
        frame_count += 1
        if frame_count == 1 or frame_count % 50 == 0:
            telemetry(started_at, f"prepare frames={frame_count}")
    capture.release()
    if frame_count == 0:
        raise RuntimeError(f"source video contains no readable frames: {source}")
    telemetry(started_at, f"prepare complete frames={frame_count}")
    return frame_count, fps


def run_corridorkey(
    corridor_root: Path,
    clip_root: Path,
    device: str,
    max_frames: int | None,
    started_at: float,
) -> None:
    sys.path.insert(0, str(corridor_root))
    previous_directory = Path.cwd()
    os.chdir(corridor_root)
    try:
        from clip_manager import ClipEntry, InferenceSettings, run_inference

        clip = ClipEntry(clip_root.name, str(clip_root))
        clip.find_assets()
        clip.validate_pair()
        telemetry(started_at, f"CorridorKey loading engine device={device} image_size=2048")
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

        def on_clip_start(name: str, total: int) -> None:
            telemetry(started_at, f"CorridorKey start clip={name} frames={total}")

        def on_frame_complete(frame_index: int, total: int) -> None:
            nonlocal last_report
            completed = frame_index + 1
            now = time.monotonic()
            if completed != total and completed != 1 and completed % 10 != 0 and now - last_report < 5:
                return
            inference_elapsed = now - inference_started_at
            rate = completed / max(inference_elapsed, 0.001)
            eta = (total - completed) / max(rate, 0.001)
            telemetry(
                started_at,
                f"CorridorKey frames={completed}/{total} "
                f"rate={rate:.2f}fps eta={eta:.1f}s",
            )
            last_report = now

        run_inference(
            [clip],
            device=device,
            backend="torch",
            max_frames=max_frames,
            settings=settings,
            on_clip_start=on_clip_start,
            on_frame_complete=on_frame_complete,
        )
        telemetry(started_at, "CorridorKey inference complete")
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
) -> None:
    fg_dir = clip_root / "Output" / "FG"
    matte_dir = clip_root / "Output" / "Matte"
    fg_files = sorted(fg_dir.glob("*.exr"))
    matte_files = sorted(matte_dir.glob("*.exr"))
    if len(fg_files) != frame_count or len(matte_files) != frame_count:
        raise RuntimeError(
            f"CorridorKey produced FG={len(fg_files)} Matte={len(matte_files)} "
            f"frames; expected {frame_count}"
        )

    plate_bgr = cv2.imread(str(plate_path), cv2.IMREAD_COLOR)
    if plate_bgr is None:
        raise RuntimeError(f"cannot read background plate: {plate_path}")
    plate_rgb = cv2.cvtColor(
        cover_plate(plate_bgr, width, height), cv2.COLOR_BGR2RGB
    ).astype("float32") / 255.0
    plate_linear = srgb_to_linear(plate_rgb)
    if background_blur_sigma:
        plate_linear = cv2.GaussianBlur(
            plate_linear, (0, 0), background_blur_sigma
        )
    telemetry(
        started_at,
        f"composite start frames={frame_count} "
        f"delivery={output.name} "
        f"master={master_output.name if master_output is not None else 'disabled'} "
        f"background_blur_sigma={background_blur_sigma:.2f} "
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
        for frame_index, (fg_path, matte_path) in enumerate(zip(fg_files, matte_files)):
            fg_bgr = cv2.imread(str(fg_path), cv2.IMREAD_UNCHANGED)
            if fg_bgr is None or fg_bgr.ndim != 3 or fg_bgr.shape[2] < 3:
                raise RuntimeError(
                    f"invalid CorridorKey foreground frame: {fg_path}"
                )
            fg_rgb = cv2.cvtColor(fg_bgr[:, :, :3], cv2.COLOR_BGR2RGB)
            fg_srgb = cv2.resize(
                np.clip(fg_rgb, 0.0, 1.0),
                (width, height),
                interpolation=cv2.INTER_LANCZOS4,
            )
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

            matte = cv2.imread(str(matte_path), cv2.IMREAD_UNCHANGED)
            if matte is None:
                raise RuntimeError(f"invalid CorridorKey matte frame: {matte_path}")
            if matte.ndim == 3:
                matte = matte[:, :, 0]
            if matte.dtype == np.uint8:
                matte = matte.astype("float32") / 255.0
            else:
                matte = matte.astype("float32")
            alpha = cv2.resize(
                np.clip(matte, 0.0, 1.0),
                (width, height),
                interpolation=cv2.INTER_LINEAR,
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
    telemetry(started_at, f"composite complete frames={frame_count}")


def main() -> int:
    args = parse_args()
    reexecute_in_corridor_environment(args)

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
    if (
        args.master_output is not None
        and args.master_output.resolve() == args.output.resolve()
    ):
        raise ValueError("--master-output must differ from --output")

    args.output.resolve().parent.mkdir(parents=True, exist_ok=True)
    telemetry(
        started_at,
        f"start source={source.name} output={args.output.resolve().name}",
    )
    with tempfile.TemporaryDirectory(
        prefix="corridorkey-", dir=args.output.resolve().parent
    ) as temporary:
        clip_root = Path(temporary) / "talking-head"
        clip_root.mkdir()
        frame_count, fps = prepare_corridor_clip(
            source, clip_root, started_at, args.max_frames
        )
        run_corridorkey(
            corridor_root, clip_root, args.device, args.max_frames, started_at
        )
        composite_outputs(
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
        )
    master_message = (
        f" master={args.master_output.resolve()}"
        if args.master_output is not None
        else ""
    )
    telemetry(
        started_at,
        f"complete delivery={args.output.resolve()}{master_message} "
        f"frames={frame_count}",
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
