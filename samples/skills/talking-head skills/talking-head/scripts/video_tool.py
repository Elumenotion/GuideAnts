#!/usr/bin/env python3
"""Talking-head i2v CLI via Max skill gateway (ComfyUI-video adapter).

Stdlib-only. Requires TALKING_HEAD_SKILL_BASE_URL + TALKING_HEAD_SKILL_TOKEN.
Paths must stay inside the notebook root (.guideants/notebook.json).

`i2v` submits and exits. A GuideAnts sandbox script call is killed at about
10 minutes (`SCRIPT_EXECUTION_TIMEOUT_SECONDS`, default 600). Do not wait for
the MP4 in this process. Poll `status` on later sandbox calls; `result` when
state is completed.

Submit is generate-only parameters (416×256 / 8 / cfg 1, LongCat-Video-Avatar-1.5).
Max composite is CorridorKey @ 416×234, BasicVSR++ on keyed FG, 1280×720 MP4 —
host env, not CLI.
"""
from __future__ import annotations

import argparse
import json
import os
import random
import re
import sys
import tempfile
import urllib.error
import wave
from pathlib import Path
from typing import Any

from skill_gateway_client import (
    fail_http,
    gateway_download,
    gateway_request,
    gateway_request_multipart,
    require_gateway,
    using_skill_gateway,
)

I2V_WORKFLOW = "infinitetalk-i2v-v1"
HEX_UUID_PATTERN = re.compile(r"^[0-9a-f]{32}$")
DEFAULT_WIDTH = 416
DEFAULT_HEIGHT = 256
DEFAULT_STEPS = 8
DEFAULT_CFG = 1.0
DEFAULT_FPS = 25
DEFAULT_SEED = -1
DEFAULT_AUDIO_PAD = 0.5


class VideoToolError(RuntimeError):
    pass


def _working_directory(value: str | None) -> Path:
    directory = Path(value) if value else Path.cwd()
    if not directory.is_absolute():
        directory = Path.cwd() / directory
    try:
        resolved = directory.resolve(strict=True)
    except OSError as exc:
        raise VideoToolError(f"working directory is unavailable: {exc}") from exc
    if not resolved.is_dir():
        raise VideoToolError("working directory is not a directory")
    return resolved


def _notebook_root(working_directory: Path) -> Path:
    for candidate in (working_directory, *working_directory.parents):
        if (candidate / ".guideants" / "notebook.json").is_file():
            return candidate
    raise VideoToolError(
        "working directory is not inside a notebook containing .guideants/notebook.json"
    )


def resolve_notebook_path(
    value: str | os.PathLike[str],
    working_directory: str | os.PathLike[str] | None = None,
    *,
    must_exist: bool,
) -> Path:
    directory = _working_directory(str(working_directory) if working_directory else None)
    root = _notebook_root(directory)
    supplied = Path(value)
    candidate = supplied if supplied.is_absolute() else directory / supplied
    try:
        resolved = candidate.resolve(strict=must_exist)
    except OSError as exc:
        raise VideoToolError(f"path is unavailable: {value}: {exc}") from exc
    if resolved != root and root not in resolved.parents:
        raise VideoToolError(f"path escapes the notebook root: {value}")
    return resolved


def _job_id(value: str) -> str:
    if not HEX_UUID_PATTERN.fullmatch(value):
        raise VideoToolError("job_id must be a 32-character lowercase hexadecimal UUID")
    return value


def resolve_seed(seed: int) -> tuple[int, str]:
    """Resolve CLI seed. -1 means random before submit; adapter rejects -1."""
    if seed < 0:
        resolved = random.randint(0, 2**31 - 1)
        return resolved, "random"
    return seed, "explicit"


_ALLOWED_UPLOAD_TYPES = {
    ".png": "image/png",
    ".jpg": "image/jpeg",
    ".jpeg": "image/jpeg",
    ".webp": "image/webp",
    ".wav": "audio/wav",
    ".mp3": "audio/mpeg",
    ".flac": "audio/flac",
    ".ogg": "audio/ogg",
}


def _pad_wav_silence(src: Path, seconds: float, destination: Path) -> None:
    """Prepend and append `seconds` of digital silence to a PCM WAV.

    Stdlib-only. Preserves channels/sampwidth/framerate. The padded
    file is written next to the output MP4 so it stays inside the
    notebook root (upload paths must not escape it).
    """
    if seconds < 0:
        raise VideoToolError("--audio-pad must be >= 0")
    with wave.open(str(src), "rb") as w_in:
        params = w_in.getparams()
        data = w_in.readframes(params.nframes)
    pad_frames = int(params.framerate * seconds)
    silence = b"\x00" * (pad_frames * params.nchannels * params.sampwidth)
    destination.parent.mkdir(parents=True, exist_ok=True)
    with wave.open(str(destination), "wb") as w_out:
        w_out.setnchannels(params.nchannels)
        w_out.setsampwidth(params.sampwidth)
        w_out.setframerate(params.framerate)
        w_out.writeframes(silence + data + silence)


def _read_file(path: Path) -> tuple[str, bytes, str]:
    content_type = _ALLOWED_UPLOAD_TYPES.get(path.suffix.lower())
    if content_type is None:
        allowed = ", ".join(sorted(_ALLOWED_UPLOAD_TYPES))
        raise VideoToolError(f"unsupported file type {path.suffix!r}; allowed: {allowed}")
    return path.name, path.read_bytes(), content_type


def _submit_i2v(
    avatar: Path,
    audio: Path,
    background: Path,
    output_filename: str,
    *,
    workflow: str,
    parameters: dict[str, Any],
    positive_prompt: str | None,
    negative_prompt: str | None,
) -> dict[str, Any]:
    fields = {
        "output_filename": output_filename,
        "workflow_version": workflow,
        "parameters": json.dumps(parameters, separators=(",", ":")),
    }
    if positive_prompt is not None:
        fields["positive_prompt"] = positive_prompt
    if negative_prompt is not None:
        fields["negative_prompt"] = negative_prompt
    body = gateway_request_multipart(
        "/v1/talking-head/jobs",
        fields,
        {
            "source": _read_file(avatar),
            "audio": _read_file(audio),
            "background": _read_file(background),
        },
    )
    return json.loads(body.decode("utf-8"))


def _materialize_result(job_id: str, destination: Path) -> dict[str, Any]:
    if destination.suffix.lower() != ".mp4":
        raise VideoToolError("output path must end in .mp4")
    destination.parent.mkdir(parents=True, exist_ok=True)
    payload = gateway_download(f"/v1/talking-head/jobs/{job_id}/result")
    if not payload or len(payload) < 8:
        raise VideoToolError("adapter returned an empty result")
    with tempfile.NamedTemporaryFile(
        delete=False, dir=str(destination.parent), suffix=".mp4.part"
    ) as handle:
        handle.write(payload)
        temporary = Path(handle.name)
    os.replace(temporary, destination)
    return {
        "jobId": job_id,
        "outputPath": str(destination),
        "bytes": destination.stat().st_size,
    }


def _write_run_meta(
    output: Path,
    *,
    seed: int,
    seed_mode: str,
    job_id: str,
    workflow: str,
    audio_pad_seconds: float = 0.0,
) -> Path:
    meta_path = output.with_name(f"{output.stem}-run-meta.json")
    meta = {
        "seed": seed,
        "seedMode": seed_mode,
        "jobId": job_id,
        "workflow": workflow,
        "outputPath": str(output),
        "audioPadSeconds": audio_pad_seconds,
    }
    meta_path.write_text(json.dumps(meta, indent=2) + "\n", encoding="utf-8")
    return meta_path


def cmd_i2v(args: argparse.Namespace) -> None:
    avatar = resolve_notebook_path(args.avatar, must_exist=True)
    audio = resolve_notebook_path(args.audio, must_exist=True)
    background = resolve_notebook_path(args.background, must_exist=True)
    output = resolve_notebook_path(args.output, must_exist=False)
    if output.suffix.lower() != ".mp4":
        raise VideoToolError("output path must end in .mp4")

    audio_pad_applied = False
    if args.audio_pad != 0:
        if audio.suffix.lower() != ".wav":
            raise VideoToolError(
                "--audio-pad only supports .wav (tested clip is 24 kHz PCM s16)"
            )
        padded = output.parent / f"{output.stem}-padded-audio.wav"
        _pad_wav_silence(audio, args.audio_pad, padded)
        audio = padded
        audio_pad_applied = True
        print(
            f"[talking-head] audio pad: {args.audio_pad}s head+tail "
            f"-> {padded.name}",
            file=sys.stderr,
        )

    seed, seed_mode = resolve_seed(args.seed)
    print(f"[talking-head] seed={seed} seed_mode={seed_mode}", file=sys.stderr)

    parameters: dict[str, Any] = {
        "width": args.width,
        "height": args.height,
        "steps": args.steps,
        "cfg": args.cfg,
        "fps": args.fps,
        "seed": seed,
    }

    submit = _submit_i2v(
        avatar,
        audio,
        background,
        output.name,
        workflow=args.workflow,
        parameters=parameters,
        positive_prompt=args.positive,
        negative_prompt=args.negative,
    )
    job_id = _job_id(str(submit.get("jobId")))
    print(f"[talking-head] submitted jobId={job_id} seed={seed}", file=sys.stderr)
    meta_path = _write_run_meta(
        output,
        seed=seed,
        seed_mode=seed_mode,
        job_id=job_id,
        workflow=args.workflow,
        audio_pad_seconds=args.audio_pad if audio_pad_applied else 0.0,
    )
    print(
        json.dumps(
            {
                "jobId": job_id,
                "seed": seed,
                "seedMode": seed_mode,
                "outputPath": str(output),
                "runMetaPath": str(meta_path),
                "state": submit.get("state"),
            },
            separators=(",", ":"),
        )
    )


def cmd_status(args: argparse.Namespace) -> None:
    job_id = _job_id(args.job_id)
    raw = gateway_request(f"/v1/talking-head/jobs/{job_id}", timeout=60)
    print(raw.decode("utf-8", errors="replace"))


def cmd_cancel(args: argparse.Namespace) -> None:
    job_id = _job_id(args.job_id)
    raw = gateway_request(f"/v1/talking-head/jobs/{job_id}/cancel", method="POST", timeout=60)
    print(raw.decode("utf-8", errors="replace"))


def cmd_result(args: argparse.Namespace) -> None:
    job_id = _job_id(args.job_id)
    output = resolve_notebook_path(args.output, must_exist=False)
    result = _materialize_result(job_id, output)
    meta_path = output.with_name(f"{output.stem}-run-meta.json")
    meta: dict[str, Any] = {}
    if meta_path.is_file():
        meta = json.loads(meta_path.read_text(encoding="utf-8"))
    meta["jobId"] = job_id
    meta["outputPath"] = str(output)
    meta["bytes"] = result["bytes"]
    meta_path.write_text(json.dumps(meta, indent=2) + "\n", encoding="utf-8")
    result["runMetaPath"] = str(meta_path)
    if "seed" in meta:
        result["seed"] = meta["seed"]
    if "seedMode" in meta:
        result["seedMode"] = meta["seedMode"]
    print(json.dumps(result, separators=(",", ":")))


def main() -> None:
    if not using_skill_gateway():
        require_gateway()
    parser = argparse.ArgumentParser(
        description="Talking-head i2v jobs via Max skill gateway. i2v submits and exits."
    )
    sub = parser.add_subparsers(dest="command", required=True)

    p_i2v = sub.add_parser("i2v", help="Submit avatar + audio + background; print jobId and exit")
    p_i2v.add_argument("--avatar", required=True)
    p_i2v.add_argument("--audio", required=True)
    p_i2v.add_argument("--background", required=True)
    p_i2v.add_argument("-o", "--output", required=True)
    p_i2v.add_argument("--workflow", default=I2V_WORKFLOW)
    p_i2v.add_argument("--width", type=int, default=DEFAULT_WIDTH)
    p_i2v.add_argument("--height", type=int, default=DEFAULT_HEIGHT)
    p_i2v.add_argument("--steps", type=int, default=DEFAULT_STEPS)
    p_i2v.add_argument("--cfg", type=float, default=DEFAULT_CFG)
    p_i2v.add_argument("--fps", type=int, default=DEFAULT_FPS)
    p_i2v.add_argument("--seed", type=int, default=DEFAULT_SEED)
    p_i2v.add_argument(
        "--audio-pad",
        type=float,
        default=DEFAULT_AUDIO_PAD,
        help="seconds of silence prepended and appended to the input "
             "audio before upload (.wav only; 0 disables)",
    )
    p_i2v.add_argument("--positive", default=None)
    p_i2v.add_argument("--negative", default=None)

    p_status = sub.add_parser("status", help="Poll job state")
    p_status.add_argument("job_id")

    p_cancel = sub.add_parser("cancel", help="Cancel a queued or running job")
    p_cancel.add_argument("job_id")

    p_result = sub.add_parser("result", help="Download a completed job MP4")
    p_result.add_argument("job_id")
    p_result.add_argument("-o", "--output", required=True)

    args = parser.parse_args()
    try:
        {
            "i2v": cmd_i2v,
            "status": cmd_status,
            "cancel": cmd_cancel,
            "result": cmd_result,
        }[args.command](args)
    except urllib.error.HTTPError as exc:
        fail_http(exc, args.command)
    except VideoToolError as exc:
        sys.stderr.write(f"{exc}\n")
        sys.exit(1)


if __name__ == "__main__":
    main()
