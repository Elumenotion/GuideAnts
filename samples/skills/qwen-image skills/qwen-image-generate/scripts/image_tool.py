#!/usr/bin/env python3
"""Qwen Image scenario CLI via GPU host skill gateway (BF16-only ComfyUI-video adapter).

Stdlib-only. Requires QWEN_IMAGE_SKILL_BASE_URL + QWEN_IMAGE_SKILL_TOKEN.
Paths must stay inside the notebook root (.guideants/notebook.json).
"""
from __future__ import annotations

import argparse
import json
import mimetypes
import os
import re
import sys
import tempfile
import time
import urllib.error
from pathlib import Path
from typing import Any

from skill_gateway_client import (
    fail_http,
    fetch_capabilities,
    gateway_download,
    gateway_request,
    gateway_request_multipart,
    require_gateway,
    using_skill_gateway,
)

# Live the GPU host adapter generate API ids (compose bind-mounts workflows under these names).
GENERATE_WORKFLOW_LIGHTNING = "qwen-image-v1"
GENERATE_WORKFLOW_HIGH = "qwen-image-generate-20-v1"
GENERATE_WORKFLOW = GENERATE_WORKFLOW_LIGHTNING
EDIT_WORKFLOW = "qwen-image-edit-bf16-v1"
INPAINT_WORKFLOW = "qwen-image-edit-bf16-inpaint-v1"
# Edit/inpaint: locked Lightning (AC-I1). Generate draft = Lightning graph; high = 20-step
# reference graph (kyuz0, no LoRA node) via qwen-image-generate-20-v1.
LIGHTNING_STEPS = 4
LIGHTNING_CFG = 1.0
LIGHTNING_LORA_STRENGTH = 1.0
LIGHTNING_DENOISE = 1.0
LIGHTNING_SHIFT = 3.1
LIGHTNING_MEGAPIXELS = 1.6
GENERATE_QUALITY_PROFILES: dict[str, dict[str, int | float | str]] = {
    "draft": {
        "workflow": GENERATE_WORKFLOW_LIGHTNING,
        "steps": 4,
        "cfg": 1.0,
        "lora_strength": 1.0,
    },
    "high": {
        "workflow": GENERATE_WORKFLOW_HIGH,
        "steps": 20,
        "cfg": 2.5,
    },
}
GENERATE_CANVAS_SIZES: dict[str, tuple[int, int]] = {
    "square": (1328, 1328),  # AC-G1 default
    "landscape": (1664, 928),  # harness + full20 sidecar
    "portrait": (928, 1664),  # transpose of tested landscape pair
}
HEX_UUID_PATTERN = re.compile(r"^[0-9a-f]{32}$")
DEFAULT_POLL_SECONDS = 5
MAX_POLL_SECONDS = 60
DEFAULT_JOB_TIMEOUT_SECONDS = 1800


class ImageToolError(RuntimeError):
    pass


def _working_directory(value: str | None) -> Path:
    directory = Path(value) if value else Path.cwd()
    if not directory.is_absolute():
        directory = Path.cwd() / directory
    try:
        resolved = directory.resolve(strict=True)
    except OSError as exc:
        raise ImageToolError(f"working directory is unavailable: {exc}") from exc
    if not resolved.is_dir():
        raise ImageToolError("working directory is not a directory")
    return resolved


def _notebook_root(working_directory: Path) -> Path:
    for candidate in (working_directory, *working_directory.parents):
        if (candidate / ".guideants" / "notebook.json").is_file():
            return candidate
    raise ImageToolError(
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
        raise ImageToolError(f"path is unavailable: {value}: {exc}") from exc
    if resolved != root and root not in resolved.parents:
        raise ImageToolError(f"path escapes the notebook root: {value}")
    return resolved


def _job_id(value: str) -> str:
    if not HEX_UUID_PATTERN.fullmatch(value):
        raise ImageToolError("job_id must be a 32-character lowercase hexadecimal UUID")
    return value


def _read_source(path: Path) -> tuple[str, bytes, str]:
    content_type = mimetypes.guess_type(path.name)[0] or "application/octet-stream"
    return path.name, path.read_bytes(), content_type


def _submit_generate(
    prompt: str,
    output_filename: str,
    *,
    workflow: str,
    parameters: dict[str, Any],
    negative_prompt: str,
) -> dict[str, Any]:
    resolve_notebook_path(".", must_exist=False)
    body = gateway_request_multipart(
        "/v1/image/generate/jobs",
        {
            "prompt": prompt,
            "output_filename": output_filename,
            "workflow_version": workflow,
            "negative_prompt": negative_prompt,
            "parameters": json.dumps(parameters, separators=(",", ":")),
        },
        {},
    )
    return json.loads(body.decode("utf-8"))


def _submit_edit(
    source: Path,
    prompt: str,
    output_filename: str,
    *,
    workflow: str,
    mask: Path | None,
    parameters: dict[str, Any],
    negative_prompt: str,
) -> dict[str, Any]:
    files = {"source": _read_source(source)}
    if mask is not None:
        files["mask"] = _read_source(mask)
    body = gateway_request_multipart(
        "/v1/image/jobs",
        {
            "prompt": prompt,
            "output_filename": output_filename,
            "workflow_version": workflow,
            "negative_prompt": negative_prompt,
            "parameters": json.dumps(parameters, separators=(",", ":")),
        },
        files,
    )
    return json.loads(body.decode("utf-8"))


def _resolve_generate_quality(value: str) -> dict[str, int | float | str]:
    profile = GENERATE_QUALITY_PROFILES.get(value)
    if profile is None:
        allowed = ", ".join(sorted(GENERATE_QUALITY_PROFILES))
        raise ImageToolError(f"unsupported quality {value!r} (use {allowed})")
    return dict(profile)


def _resolve_generate_canvas(value: str) -> tuple[int, int]:
    size = GENERATE_CANVAS_SIZES.get(value)
    if size is None:
        allowed = ", ".join(sorted(GENERATE_CANVAS_SIZES))
        raise ImageToolError(f"unsupported canvas {value!r} (use {allowed})")
    return size


def _poll_job(job_id: str, *, timeout_seconds: int, poll_seconds: int) -> dict[str, Any]:
    if poll_seconds > MAX_POLL_SECONDS:
        raise ImageToolError(
            f"--poll-seconds must be <= {MAX_POLL_SECONDS} (got {poll_seconds}); "
            "it is the sleep between status checks, not the total wait budget — use --timeout"
        )
    deadline = time.monotonic() + timeout_seconds
    while True:
        raw = gateway_request(f"/v1/image/jobs/{job_id}", timeout=60)
        job = json.loads(raw.decode("utf-8"))
        state = str(job.get("state", "")).lower()
        progress = job.get("progress") or {}
        message = progress.get("message") or state
        print(f"[qwen-image] {message}", file=sys.stderr)
        if state == "completed":
            return job
        if state in {"failed", "cancelled"}:
            raise ImageToolError(f"job ended in state '{state}': {job.get('error')}")
        if time.monotonic() >= deadline:
            raise ImageToolError(f"timed out waiting for job {job_id} after {timeout_seconds}s")
        time.sleep(poll_seconds)


def _materialize_result(job_id: str, destination: Path) -> dict[str, Any]:
    if destination.suffix.lower() != ".png":
        raise ImageToolError("output path must end in .png")
    destination.parent.mkdir(parents=True, exist_ok=True)
    payload = gateway_download(f"/v1/image/jobs/{job_id}/result")
    if not payload or len(payload) < 8:
        raise ImageToolError("adapter returned an empty result")
    with tempfile.NamedTemporaryFile(
        delete=False, dir=str(destination.parent), suffix=".png.part"
    ) as handle:
        handle.write(payload)
        temporary = Path(handle.name)
    os.replace(temporary, destination)
    return {
        "jobId": job_id,
        "outputPath": str(destination),
        "bytes": destination.stat().st_size,
    }


def _default_generate_params(args: argparse.Namespace) -> dict[str, Any]:
    profile = _resolve_generate_quality(args.quality)
    width, height = _resolve_generate_canvas(args.canvas)
    params: dict[str, Any] = {
        "steps": profile["steps"],
        "cfg": profile["cfg"],
        "seed": args.seed,
        "denoise": LIGHTNING_DENOISE,
        "shift": LIGHTNING_SHIFT,
        "width": width,
        "height": height,
    }
    if "lora_strength" in profile:
        params["lora_strength"] = profile["lora_strength"]
        params["megapixels"] = LIGHTNING_MEGAPIXELS
    return params


def _generate_workflow(args: argparse.Namespace) -> str:
    if args.workflow != GENERATE_WORKFLOW:
        return _resolve_generate_workflow(args.workflow)
    return str(_resolve_generate_quality(args.quality)["workflow"])


def _resolve_generate_workflow(value: str) -> str:
    if value == "qwen-image-bf16-v1":
        return GENERATE_WORKFLOW_LIGHTNING
    if value in (GENERATE_WORKFLOW_LIGHTNING, GENERATE_WORKFLOW_HIGH):
        return value
    allowed = f"{GENERATE_WORKFLOW_LIGHTNING}, {GENERATE_WORKFLOW_HIGH}"
    raise ImageToolError(f"unsupported generate workflow: {value} (use {allowed})")


def _default_edit_params(args: argparse.Namespace) -> dict[str, Any]:
    # Edit/inpaint are locked to the tested Lightning profile. Sampler overrides
    # are ignored so agents cannot invent steps=20 / lora_strength=0.
    return {
        "steps": LIGHTNING_STEPS,
        "cfg": LIGHTNING_CFG,
        "seed": args.seed,
        "denoise": LIGHTNING_DENOISE,
        "shift": LIGHTNING_SHIFT,
        "megapixels": LIGHTNING_MEGAPIXELS,
        "lora_strength": LIGHTNING_LORA_STRENGTH,
    }


def cmd_generate(args: argparse.Namespace) -> None:
    output = resolve_notebook_path(args.output, must_exist=False)
    submit = _submit_generate(
        args.prompt,
        output.name,
        workflow=_generate_workflow(args),
        parameters=_default_generate_params(args),
        negative_prompt=args.negative,
    )
    job_id = _job_id(str(submit.get("jobId")))
    print(f"jobId={job_id}", file=sys.stderr)
    if args.no_wait:
        print(json.dumps({"jobId": job_id, "outputPath": str(output)}, separators=(",", ":")))
        return
    _poll_job(job_id, timeout_seconds=args.timeout, poll_seconds=args.poll_seconds)
    result = _materialize_result(job_id, output)
    print(json.dumps(result, separators=(",", ":")))


def cmd_edit(args: argparse.Namespace) -> None:
    source = resolve_notebook_path(args.source, must_exist=True)
    output = resolve_notebook_path(args.output, must_exist=False)
    submit = _submit_edit(
        source,
        args.prompt,
        output.name,
        workflow=args.workflow,
        mask=None,
        parameters=_default_edit_params(args),
        negative_prompt=args.negative,
    )
    job_id = _job_id(str(submit.get("jobId")))
    _poll_job(job_id, timeout_seconds=args.timeout, poll_seconds=args.poll_seconds)
    result = _materialize_result(job_id, output)
    print(json.dumps(result, separators=(",", ":")))


def cmd_inpaint(args: argparse.Namespace) -> None:
    source = resolve_notebook_path(args.source, must_exist=True)
    mask = resolve_notebook_path(args.mask, must_exist=True)
    output = resolve_notebook_path(args.output, must_exist=False)
    submit = _submit_edit(
        source,
        args.prompt,
        output.name,
        workflow=INPAINT_WORKFLOW,
        mask=mask,
        parameters=_default_edit_params(args),
        negative_prompt=args.negative,
    )
    job_id = _job_id(str(submit.get("jobId")))
    _poll_job(job_id, timeout_seconds=args.timeout, poll_seconds=args.poll_seconds)
    result = _materialize_result(job_id, output)
    print(json.dumps(result, separators=(",", ":")))


def cmd_status(args: argparse.Namespace) -> None:
    job_id = _job_id(args.job_id)
    raw = gateway_request(f"/v1/image/jobs/{job_id}", timeout=60)
    print(raw.decode("utf-8", errors="replace"))


def cmd_cancel(args: argparse.Namespace) -> None:
    job_id = _job_id(args.job_id)
    raw = gateway_request(f"/v1/image/jobs/{job_id}/cancel", method="POST", timeout=60)
    print(raw.decode("utf-8", errors="replace"))


def cmd_result(args: argparse.Namespace) -> None:
    job_id = _job_id(args.job_id)
    output = resolve_notebook_path(args.output, must_exist=False)
    result = _materialize_result(job_id, output)
    print(json.dumps(result, separators=(",", ":")))


def _add_common_job_flags(parser: argparse.ArgumentParser) -> None:
    parser.add_argument(
        "--timeout",
        type=int,
        default=DEFAULT_JOB_TIMEOUT_SECONDS,
        help="total seconds to wait for job completion (default 1800)",
    )
    parser.add_argument(
        "--poll-seconds",
        type=int,
        default=DEFAULT_POLL_SECONDS,
        help=f"seconds between status polls (default {DEFAULT_POLL_SECONDS}, max {MAX_POLL_SECONDS})",
    )


def _add_generate_params(parser: argparse.ArgumentParser) -> None:
    parser.add_argument("--seed", type=int, default=0)
    parser.add_argument("--negative", default=" ")
    parser.add_argument(
        "--canvas",
        choices=sorted(GENERATE_CANVAS_SIZES),
        default="square",
        help="output aspect (maps to tested pixel sizes inside the script)",
    )
    parser.add_argument(
        "--quality",
        choices=sorted(GENERATE_QUALITY_PROFILES),
        default="draft",
        help="draft=Lightning 4-step graph (default); high=20-step non-LoRA graph",
    )
    parser.add_argument(
        "--no-wait",
        action="store_true",
        help="submit only; print jobId and exit (fetch later with result)",
    )
    # Kept for CLI compatibility; values are ignored — use --canvas and --quality.
    parser.add_argument("--width", type=int, default=0, help=argparse.SUPPRESS)
    parser.add_argument("--height", type=int, default=0, help=argparse.SUPPRESS)
    parser.add_argument("--steps", type=int, default=LIGHTNING_STEPS, help=argparse.SUPPRESS)
    parser.add_argument("--cfg", type=float, default=LIGHTNING_CFG, help=argparse.SUPPRESS)
    parser.add_argument("--shift", type=float, default=LIGHTNING_SHIFT, help=argparse.SUPPRESS)
    parser.add_argument("--megapixels", type=float, default=LIGHTNING_MEGAPIXELS, help=argparse.SUPPRESS)
    parser.add_argument(
        "--lora-strength", type=float, default=LIGHTNING_LORA_STRENGTH, help=argparse.SUPPRESS
    )


def _add_edit_params(parser: argparse.ArgumentParser) -> None:
    parser.add_argument("--seed", type=int, default=0)
    parser.add_argument("--negative", default=" ")
    # Kept for CLI compatibility; values are ignored — Lightning is mandatory.
    parser.add_argument("--steps", type=int, default=LIGHTNING_STEPS, help=argparse.SUPPRESS)
    parser.add_argument("--cfg", type=float, default=LIGHTNING_CFG, help=argparse.SUPPRESS)
    parser.add_argument("--denoise", type=float, default=LIGHTNING_DENOISE, help=argparse.SUPPRESS)
    parser.add_argument("--shift", type=float, default=LIGHTNING_SHIFT, help=argparse.SUPPRESS)
    parser.add_argument("--megapixels", type=float, default=LIGHTNING_MEGAPIXELS, help=argparse.SUPPRESS)
    parser.add_argument(
        "--lora-strength", type=float, default=LIGHTNING_LORA_STRENGTH, help=argparse.SUPPRESS
    )


def main() -> None:
    if not using_skill_gateway():
        require_gateway()
    parser = argparse.ArgumentParser(description="Qwen Image BF16 jobs via GPU host skill gateway")
    sub = parser.add_subparsers(dest="command", required=True)

    p_generate = sub.add_parser("generate", help=f"Text to PNG ({GENERATE_WORKFLOW})")
    p_generate.add_argument("prompt")
    p_generate.add_argument("-o", "--output", required=True)
    p_generate.add_argument("--workflow", default=GENERATE_WORKFLOW)
    _add_generate_params(p_generate)
    _add_common_job_flags(p_generate)

    p_edit = sub.add_parser("edit", help="Image + prompt to PNG (qwen-image-edit-bf16-v1)")
    p_edit.add_argument("source")
    p_edit.add_argument("prompt")
    p_edit.add_argument("-o", "--output", required=True)
    p_edit.add_argument("--workflow", default=EDIT_WORKFLOW)
    _add_edit_params(p_edit)
    _add_common_job_flags(p_edit)

    p_inpaint = sub.add_parser("inpaint", help="Image + mask + prompt (BF16 inpaint workflow)")
    p_inpaint.add_argument("source")
    p_inpaint.add_argument("mask")
    p_inpaint.add_argument("prompt")
    p_inpaint.add_argument("-o", "--output", required=True)
    _add_edit_params(p_inpaint)
    _add_common_job_flags(p_inpaint)

    p_status = sub.add_parser("status", help="Poll job state")
    p_status.add_argument("job_id")

    p_cancel = sub.add_parser("cancel", help="Cancel a queued or running job")
    p_cancel.add_argument("job_id")

    p_result = sub.add_parser("result", help="Download a completed job PNG")
    p_result.add_argument("job_id")
    p_result.add_argument("-o", "--output", required=True)

    args = parser.parse_args()
    try:
        {
            "generate": cmd_generate,
            "edit": cmd_edit,
            "inpaint": cmd_inpaint,
            "status": cmd_status,
            "cancel": cmd_cancel,
            "result": cmd_result,
        }[args.command](args)
    except urllib.error.HTTPError as exc:
        fail_http(exc, args.command)
    except ImageToolError as exc:
        sys.stderr.write(f"{exc}\n")
        sys.exit(1)


if __name__ == "__main__":
    main()
