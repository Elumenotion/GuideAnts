"""BasicVSR++ 4x FG sequence upscaler (PyTorch, no VapourSynth)."""
from __future__ import annotations

import argparse
import math
import os
import sys
import time
from collections.abc import Callable
from pathlib import Path

import numpy as np
import torch
import torch.nn.functional as F

DEFAULT_BASICVSRPP_VENV = "/opt/venv-basicvsrpp"


def basicvsrpp_venv_root() -> Path:
    return Path(os.environ.get("VIDEO_BASICVSRPP_VENV", DEFAULT_BASICVSRPP_VENV))


def basicvsrpp_site_packages() -> Path | None:
    root = basicvsrpp_venv_root()
    linux = sorted(root.glob("lib/python*/site-packages"))
    if linux:
        return linux[-1]
    windows = root / "Lib" / "site-packages"
    if windows.is_dir():
        return windows
    return None


def attach_basicvsrpp_site_packages() -> Path:
    """Append the BasicVSR++ venv so mmengine resolves there, not from /opt/venv.

    Append, do not prepend: CorridorKey's OpenCV/numpy/torch must keep winning.
    """
    site = basicvsrpp_site_packages()
    if site is None:
        raise FileNotFoundError(
            f"BasicVSR++ venv site-packages not found under {basicvsrpp_venv_root()}. "
            "Install mmengine in VIDEO_BASICVSRPP_VENV, not in /opt/venv."
        )
    site_str = str(site)
    if site_str not in sys.path:
        sys.path.append(site_str)
    return site


attach_basicvsrpp_site_packages()
from basicvsrpp_lib.basicvsr_plusplus_net import BasicVSRPlusPlusNet  # noqa: E402

VIMEO90K_BD_CHECKPOINT = "basicvsr_plusplus_c64n7_8x1_300k_vimeo90k_bd_20210305-ab315ab1.pth"
SPYNET_CHECKPOINT = "spynet_20210409-c6c1bd09.pth"
SCALE = 4
DEFAULT_LENGTH = 15

_MIN_RES = 64
_MODULO = 1  # low-res (4x VSR) input modulo


def default_checkpoint_dir() -> Path:
    env = os.environ.get("VIDEO_BASICVSRPP_CHECKPOINT_DIR")
    if env:
        return Path(env)
    return Path("/models/basicvsrpp")


def _unwrap_state_dict(checkpoint: object) -> dict[str, torch.Tensor]:
    """Normalize OpenMMLab / HolyWu checkpoint layouts to bare net keys.

    Handles:
    - top-level state dict (bare keys)
    - ``{'state_dict': ...}`` wrapper
    - ``{'generator': ...}`` nested dict of weights
    - ``generator.`` key prefix (BasicVSR wrapper around BasicVSRPlusPlusNet)
    """
    if not isinstance(checkpoint, dict):
        raise TypeError(f"Expected checkpoint dict, got {type(checkpoint)!r}")

    state: object = checkpoint
    if "state_dict" in checkpoint and isinstance(checkpoint["state_dict"], dict):
        state = checkpoint["state_dict"]
    elif "generator" in checkpoint and isinstance(checkpoint["generator"], dict):
        # Nested weight dict under 'generator' (not the BasicVSR module nesting)
        candidate = checkpoint["generator"]
        if candidate and all(isinstance(v, torch.Tensor) for v in candidate.values()):
            state = candidate

    if not isinstance(state, dict):
        raise TypeError(f"Could not find state dict in checkpoint; got {type(state)!r}")

    # Strip DataParallel / BasicVSR generator prefixes
    out: dict[str, torch.Tensor] = {}
    for key, value in state.items():
        if not isinstance(value, torch.Tensor):
            continue
        name = key
        if name.startswith("module."):
            name = name[len("module.") :]
        if name.startswith("generator."):
            name = name[len("generator.") :]
        out[name] = value

    if not out:
        raise ValueError("Checkpoint contained no tensor weights after unwrapping")
    return out


def load_basicvsrpp(
    checkpoint_dir: Path,
    device: str,
    *,
    cpu_cache: bool = False,
) -> torch.nn.Module:
    """Build BasicVSRPlusPlusNet and load Vimeo90K-BD 4x weights."""
    checkpoint_dir = Path(checkpoint_dir)
    model_path = checkpoint_dir / VIMEO90K_BD_CHECKPOINT
    spynet_path = checkpoint_dir / SPYNET_CHECKPOINT

    if not model_path.is_file():
        raise FileNotFoundError(f"Missing BasicVSR++ checkpoint: {model_path}")
    if not spynet_path.is_file():
        raise FileNotFoundError(f"Missing SPyNet checkpoint: {spynet_path}")

    module = BasicVSRPlusPlusNet(
        mid_channels=64,
        num_blocks=7,
        is_low_res_input=True,
        spynet_pretrained=str(spynet_path),
        cpu_cache=cpu_cache,
    )

    raw = torch.load(model_path, map_location="cpu", weights_only=False)
    state_dict = _unwrap_state_dict(raw)
    # Training bookkeeping tensors are not part of BasicVSRPlusPlusNet.
    state_dict.pop("step_counter", None)
    module.load_state_dict(state_dict, strict=True)
    module.eval()
    module.to(device)
    # FP16 on CUDA saves VRAM; keep FP32 on CPU.
    if str(device).startswith("cuda"):
        module.half()
    return module


def _pad_size(h: int, w: int, tile: tuple[int, int], tile_pad: int) -> tuple[int, int]:
    if all(t > 0 for t in tile):
        pad_w = math.ceil(max(tile[0] + 2 * tile_pad, _MIN_RES) / _MODULO) * _MODULO
        pad_h = math.ceil(max(tile[1] + 2 * tile_pad, _MIN_RES) / _MODULO) * _MODULO
    else:
        pad_w = math.ceil(max(w, _MIN_RES) / _MODULO) * _MODULO
        pad_h = math.ceil(max(h, _MIN_RES) / _MODULO) * _MODULO
    return pad_h, pad_w


def _frames_to_tensor(frames_rgb: list[np.ndarray]) -> torch.Tensor:
    """HxWx3 float32 [0,1] list -> (1, t, 3, h, w) float32 tensor."""
    tensors = []
    for frame in frames_rgb:
        if frame.dtype != np.float32:
            raise TypeError(f"Expected float32 frames, got {frame.dtype}")
        if frame.ndim != 3 or frame.shape[2] != 3:
            raise ValueError(f"Expected HxWx3 RGB, got shape {frame.shape}")
        tensors.append(torch.from_numpy(np.ascontiguousarray(frame)).permute(2, 0, 1))
    return torch.stack(tensors, dim=0).unsqueeze(0)


def _tensor_to_frames(output: torch.Tensor) -> list[np.ndarray]:
    """(1, t, 3, h, w) -> list of HxWx3 float32."""
    arr = output.squeeze(0).detach().float().cpu().numpy()
    return [np.ascontiguousarray(arr[i].transpose(1, 2, 0)) for i in range(arr.shape[0])]


def _tile_process(
    img: torch.Tensor,
    module: torch.nn.Module,
    device: str,
    tile: tuple[int, int],
    tile_pad: int,
    pad_h: int,
    pad_w: int,
) -> torch.Tensor:
    batch, length, channel, height, width = img.shape
    output = img.new_zeros((batch, length, channel, height * SCALE, width * SCALE))
    tiles_x = math.ceil(width / tile[0])
    tiles_y = math.ceil(height / tile[1])
    dtype = next(module.parameters()).dtype

    for y in range(tiles_y):
        for x in range(tiles_x):
            ofs_x = x * tile[0]
            ofs_y = y * tile[1]
            input_start_x = ofs_x
            input_end_x = min(ofs_x + tile[0], width)
            input_start_y = ofs_y
            input_end_y = min(ofs_y + tile[1], height)

            input_start_x_pad = max(input_start_x - tile_pad, 0)
            input_end_x_pad = min(input_end_x + tile_pad, width)
            input_start_y_pad = max(input_start_y - tile_pad, 0)
            input_end_y_pad = min(input_end_y + tile_pad, height)

            input_tile_width = input_end_x - input_start_x
            input_tile_height = input_end_y - input_start_y

            input_tile = img[
                :, :, :, input_start_y_pad:input_end_y_pad, input_start_x_pad:input_end_x_pad
            ]
            input_tile = input_tile.to(device=device, dtype=dtype, non_blocking=True).clamp(0.0, 1.0)

            h, w = input_tile.shape[3:]
            need_pad = pad_w - w > 0 or pad_h - h > 0
            if need_pad:
                input_tile = F.pad(input_tile, (0, pad_w - w, 0, pad_h - h, 0, 0), "replicate")

            assert input_tile.size(3) >= _MIN_RES and input_tile.size(4) >= _MIN_RES, (
                f"Padded tile must be >= {_MIN_RES}, got {input_tile.shape[3:]} "
                f"(tile={tile}, tile_pad={tile_pad})"
            )

            output_tile = module(input_tile)
            if need_pad:
                output_tile = output_tile[:, :, :, : h * SCALE, : w * SCALE]

            output_start_x = input_start_x * SCALE
            output_end_x = input_end_x * SCALE
            output_start_y = input_start_y * SCALE
            output_end_y = input_end_y * SCALE

            output_start_x_tile = (input_start_x - input_start_x_pad) * SCALE
            output_end_x_tile = output_start_x_tile + input_tile_width * SCALE
            output_start_y_tile = (input_start_y - input_start_y_pad) * SCALE
            output_end_y_tile = output_start_y_tile + input_tile_height * SCALE

            output[:, :, :, output_start_y:output_end_y, output_start_x:output_end_x] = (
                output_tile[
                    :,
                    :,
                    :,
                    output_start_y_tile:output_end_y_tile,
                    output_start_x_tile:output_end_x_tile,
                ]
            )

    return output


@torch.inference_mode()
def upscale_fg_sequence(
    frames_rgb: list[np.ndarray],
    module: torch.nn.Module,
    device: str,
    *,
    length: int = DEFAULT_LENGTH,
    tile: tuple[int, int] = (0, 0),
    tile_pad: int = 16,
    # Caller should map window progress to telemetry() frames=N/M.
    on_progress: Callable[[int, int], None] | None = None,
) -> list[np.ndarray]:
    """Upscale an RGB float32 sequence 4x with BasicVSR++.

    Frames are processed in non-overlapping windows of ``length`` (last window
    may be shorter). Spatial dims are padded to at least 64, then cropped back
    to ``h*4`` x ``w*4``.
    """
    if not frames_rgb:
        return []
    if length < 1:
        raise ValueError(f"length must be >= 1, got {length}")
    if len(tile) != 2:
        raise ValueError(f"tile must be (width, height), got {tile!r}")

    h0, w0 = frames_rgb[0].shape[:2]
    for i, frame in enumerate(frames_rgb):
        if frame.shape[:2] != (h0, w0):
            raise ValueError(
                f"Frame {i} shape {frame.shape[:2]} != first frame {(h0, w0)}"
            )

    pad_h, pad_w = _pad_size(h0, w0, tile, tile_pad)
    dtype = next(module.parameters()).dtype
    use_tile = all(t > 0 for t in tile)

    outputs: list[np.ndarray] = []
    n_frames = len(frames_rgb)
    n_windows = math.ceil(n_frames / length)

    for wi, start in enumerate(range(0, n_frames, length)):
        window = frames_rgb[start : start + length]
        img = _frames_to_tensor(window)

        if use_tile:
            out = _tile_process(img, module, device, tile, tile_pad, pad_h, pad_w)
        else:
            img = img.to(device=device, dtype=dtype, non_blocking=True).clamp(0.0, 1.0)
            h, w = img.shape[3:]
            need_pad = pad_w - w > 0 or pad_h - h > 0
            if need_pad:
                img = F.pad(img, (0, pad_w - w, 0, pad_h - h, 0, 0), "replicate")

            assert img.size(3) >= _MIN_RES and img.size(4) >= _MIN_RES, (
                f"Padded LQ must be >= {_MIN_RES}, got {img.shape[3:]}"
            )

            out = module(img)
            if need_pad:
                out = out[:, :, :, : h * SCALE, : w * SCALE]

        outputs.extend(_tensor_to_frames(out))
        if on_progress is not None:
            on_progress(wi + 1, n_windows)

    return outputs


def _cli() -> int:
    parser = argparse.ArgumentParser(description="BasicVSR++ FG upscale in an isolated process")
    parser.add_argument("--input-npy", type=Path, required=True)
    parser.add_argument("--output-npy", type=Path, required=True)
    parser.add_argument("--device", default="cuda:0")
    parser.add_argument("--checkpoint-dir", type=Path)
    parser.add_argument("--length", type=int, default=DEFAULT_LENGTH)
    parser.add_argument("--tile-w", type=int, default=0)
    parser.add_argument("--tile-h", type=int, default=0)
    parser.add_argument("--label", default="fg")
    args = parser.parse_args()

    stacked = np.load(args.input_npy)
    if stacked.ndim != 4 or stacked.shape[-1] != 3:
        raise ValueError(f"Expected NHWC RGB npy, got shape {stacked.shape}")
    frames = [np.ascontiguousarray(stacked[i]) for i in range(stacked.shape[0])]
    ckpt_dir = args.checkpoint_dir or default_checkpoint_dir()
    frame_count = len(frames)
    lq_h, lq_w = frames[0].shape[:2]
    print(
        f"basicvsrpp {args.label}-upscale load device={args.device} "
        f"checkpoint_dir={ckpt_dir} length={args.length} tile={args.tile_w}x{args.tile_h} "
        f"lq={lq_w}x{lq_h} scale={SCALE}",
        flush=True,
    )
    load_started = time.monotonic()
    module = load_basicvsrpp(ckpt_dir, args.device)
    print(
        f"basicvsrpp {args.label}-upscale loaded elapsed={time.monotonic() - load_started:.1f}s",
        flush=True,
    )
    if args.device.startswith("cuda") and torch.cuda.is_available():
        torch.cuda.reset_peak_memory_stats()
    upscale_started = time.monotonic()
    last_report = upscale_started

    def on_progress(window_index: int, window_total: int) -> None:
        nonlocal last_report
        now = time.monotonic()
        completed_est = min(frame_count, window_index * args.length)
        if window_index != window_total and window_index != 1 and now - last_report < 5:
            return
        elapsed = now - upscale_started
        rate = completed_est / max(elapsed, 0.001)
        eta = (frame_count - completed_est) / max(rate, 0.001)
        print(
            f"basicvsrpp {args.label} frames={completed_est}/{frame_count} "
            f"windows={window_index}/{window_total} "
            f"rate={rate:.2f}fps eta={eta:.1f}s",
            flush=True,
        )
        last_report = now

    hr_frames = upscale_fg_sequence(
        frames,
        module,
        args.device,
        length=args.length,
        tile=(args.tile_w, args.tile_h),
        on_progress=on_progress,
    )
    elapsed = time.monotonic() - upscale_started
    peak_gb = None
    if args.device.startswith("cuda") and torch.cuda.is_available():
        peak_gb = torch.cuda.max_memory_allocated() / (1024**3)
    hr_h, hr_w = hr_frames[0].shape[:2]
    mem_msg = f" peak_mem_gb={peak_gb:.2f}" if peak_gb is not None else ""
    n_windows = math.ceil(frame_count / args.length)
    print(
        f"basicvsrpp {args.label} complete frames={frame_count}/{frame_count} "
        f"windows={n_windows}/{n_windows} "
        f"hr={hr_w}x{hr_h} elapsed={elapsed:.1f}s "
        f"rate={frame_count / max(elapsed, 0.001):.2f}fps{mem_msg}",
        flush=True,
    )
    save_started = time.monotonic()
    shape = (frame_count, hr_h, hr_w, 3)
    nbytes = int(np.prod(shape) * np.dtype("float32").itemsize)
    print(
        f"basicvsrpp {args.label} saving frames=0/{frame_count} "
        f"windows={n_windows}/{n_windows} hr={hr_w}x{hr_h} bytes={nbytes}",
        flush=True,
    )
    args.output_npy.parent.mkdir(parents=True, exist_ok=True)
    mapped = np.lib.format.open_memmap(
        args.output_npy, mode="w+", dtype="float32", shape=shape
    )
    try:
        for index, frame in enumerate(hr_frames):
            mapped[index] = frame
            if index == 0 or (index + 1) % 50 == 0 or index + 1 == frame_count:
                print(
                    f"basicvsrpp {args.label} saving frames={index + 1}/{frame_count} "
                    f"windows={n_windows}/{n_windows} hr={hr_w}x{hr_h}",
                    flush=True,
                )
        mapped.flush()
    finally:
        del mapped
    print(
        f"basicvsrpp {args.label} saved frames={frame_count}/{frame_count} "
        f"windows={n_windows}/{n_windows} elapsed={time.monotonic() - save_started:.1f}s",
        flush=True,
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(_cli())

