#!/usr/bin/env python3
"""Talking-head i2v CLI via GPU host skill gateway (ComfyUI-video adapter).

Stdlib-only. Requires TALKING_HEAD_SKILL_BASE_URL + TALKING_HEAD_SKILL_TOKEN.
Paths must stay inside the notebook root (.guideants/notebook.json).

Quiet poll telemetry: print only on progress-key / state change + 60s heartbeat;
always include seed. No per-poll transport spam.
"""
from __future__ import annotations

import argparse
import json
import mimetypes
import os
import random
import re
import sys
import tempfile
import time
import urllib.error
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
DEFAULT_POLL_SECONDS = 5
DEFAULT_JOB_TIMEOUT_SECONDS = 3600
HEARTBEAT_SECONDS = 60
DEFAULT_WIDTH = 416
DEFAULT_HEIGHT = 256
DEFAULT_STEPS = 4
DEFAULT_CFG = 1.0
DEFAULT_FPS = 25
DEFAULT_SEED = -1


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


def _normalize_sandbox_path(
    value: str | os.PathLike[str],
    directory: Path,
    root: Path,
) -> str:
    text = os.fspath(value).replace("\\", "/")
    output_dir = root / "Output"
    try:
        directory.resolve().relative_to(output_dir.resolve())
    except ValueError:
        return os.fspath(value)
    normalized = text.lstrip("./").lstrip("/")
    if normalized.lower().startswith("output/"):
        stripped = normalized[7:]
        print(
            "warning: path starts with Output/ but sandbox CWD is already Output/; "
            f"using {stripped!r} instead",
            file=sys.stderr,
        )
        return stripped
    return os.fspath(value)


def resolve_notebook_path(
    value: str | os.PathLike[str],
    working_directory: str | os.PathLike[str] | None = None,
    *,
    must_exist: bool,
) -> Path:
    directory = _working_directory(str(working_directory) if working_directory else None)
    root = _notebook_root(directory)
    supplied_text = _normalize_sandbox_path(value, directory, root)
    supplied = Path(supplied_text)
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


def _read_file(path: Path) -> tuple[str, bytes, str]:
    content_type = mimetypes.guess_type(path.name)[0] or "application/octet-stream"
    if content_type == "audio/x-wav":
        content_type = "audio/wav"
    return path.name, path.read_bytes(), content_type


def _progress_log_key(job: dict[str, Any]) -> tuple[Any, ...]:
    progress = job.get("progress") or {}
    return (
        str(job.get("state", "")).lower(),
        progress.get("phase"),
        progress.get("message"),
        progress.get("node_class"),
        progress.get("step"),
        progress.get("max_steps"),
        progress.get("queue_position"),
        progress.get("seed") if progress.get("seed") is not None else job.get("seed"),
    )


def _format_progress_line(job: dict[str, Any], seed: int | None, *, heartbeat: bool = False) -> str:
    state = str(job.get("state", "")).lower() or "unknown"
    progress = job.get("progress") or {}
    phase = progress.get("phase") or state
    message = progress.get("message") or phase
    parts = [f"state={state}", f"phase={phase}", str(message)]
    step = progress.get("step")
    max_steps = progress.get("max_steps")
    if isinstance(step, int) and isinstance(max_steps, int) and max_steps > 0:
        percent = progress.get("percent")
        if isinstance(percent, (int, float)):
            parts.append(f"progress={step}/{max_steps} ({percent}%)")
        else:
            parts.append(f"progress={step}/{max_steps}")
    queue_position = progress.get("queue_position")
    if isinstance(queue_position, int):
        parts.append(f"queue_position={queue_position}")
    effective_seed = progress.get("seed")
    if effective_seed is None:
        effective_seed = job.get("seed")
    if effective_seed is None:
        effective_seed = seed
    if effective_seed is not None:
        parts.append(f"seed={effective_seed}")
    prefix = "[talking-head heartbeat]" if heartbeat else "[talking-head]"
    return f"{prefix} {' | '.join(parts)}"


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


def _poll_job(
    job_id: str,
    *,
    seed: int,
    timeout_seconds: int,
    poll_seconds: int,
) -> dict[str, Any]:
    deadline = time.monotonic() + timeout_seconds
    last_key: tuple[Any, ...] | None = None
    last_log_at = 0.0
    while True:
        raw = gateway_request(f"/v1/talking-head/jobs/{job_id}", timeout=60)
        job = json.loads(raw.decode("utf-8"))
        state = str(job.get("state", "")).lower()
        key = _progress_log_key(job)
        now = time.monotonic()
        if key != last_key:
            print(_format_progress_line(job, seed), file=sys.stderr)
            last_key = key
            last_log_at = now
        elif now - last_log_at >= HEARTBEAT_SECONDS:
            print(_format_progress_line(job, seed, heartbeat=True), file=sys.stderr)
            last_log_at = now
        if state == "completed":
            return job
        if state in {"failed", "cancelled"}:
            raise VideoToolError(f"job ended in state '{state}': {job.get('error')}")
        if now >= deadline:
            raise VideoToolError(f"timed out waiting for job {job_id} after {timeout_seconds}s")
        time.sleep(poll_seconds)


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


def _write_run_meta(output: Path, *, seed: int, seed_mode: str, job_id: str, workflow: str) -> Path:
    meta_path = output.with_name(f"{output.stem}-run-meta.json")
    meta = {
        "seed": seed,
        "seedMode": seed_mode,
        "jobId": job_id,
        "workflow": workflow,
        "outputPath": str(output),
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
    _poll_job(
        job_id,
        seed=seed,
        timeout_seconds=args.timeout,
        poll_seconds=args.poll_seconds,
    )
    result = _materialize_result(job_id, output)
    meta_path = _write_run_meta(
        output, seed=seed, seed_mode=seed_mode, job_id=job_id, workflow=args.workflow
    )
    result["seed"] = seed
    result["seedMode"] = seed_mode
    result["runMetaPath"] = str(meta_path)
    print(json.dumps(result, separators=(",", ":")))


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
    print(json.dumps(result, separators=(",", ":")))


def main() -> None:
    if not using_skill_gateway():
        require_gateway()
    parser = argparse.ArgumentParser(description="Talking-head i2v jobs via GPU host skill gateway")
    sub = parser.add_subparsers(dest="command", required=True)

    p_i2v = sub.add_parser("i2v", help="Avatar + audio + background → MP4 (infinitetalk-i2v-v1)")
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
    p_i2v.add_argument("--positive", default=None)
    p_i2v.add_argument("--negative", default=None)
    p_i2v.add_argument("--timeout", type=int, default=DEFAULT_JOB_TIMEOUT_SECONDS)
    p_i2v.add_argument("--poll-seconds", type=int, default=DEFAULT_POLL_SECONDS)

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
