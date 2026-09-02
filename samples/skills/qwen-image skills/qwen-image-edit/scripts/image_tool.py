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

# Live the GPU host adapter generate API id (compose serves BF16 weights under this id).
GENERATE_WORKFLOW = "qwen-image-v1"
EDIT_WORKFLOW = "qwen-image-edit-bf16-v1"
INPAINT_WORKFLOW = "qwen-image-edit-bf16-inpaint-v1"
# Tested Lightning profile (harness + AC). Edit/inpaint always use this; generate
# defaults match AC-G1 (full-quality generate only when user explicitly asks).
LIGHTNING_STEPS = 4
LIGHTNING_CFG = 1.0
LIGHTNING_LORA_STRENGTH = 1.0
LIGHTNING_DENOISE = 1.0
LIGHTNING_SHIFT = 3.1
LIGHTNING_MEGAPIXELS = 1.6
GENERATE_WIDTH = 1328
GENERATE_HEIGHT = 1328
HEX_UUID_PATTERN = re.compile(r"^[0-9a-f]{32}$")
DEFAULT_POLL_SECONDS = 5
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


def _poll_job(job_id: str, *, timeout_seconds: int, poll_seconds: int) -> dict[str, Any]:
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
    return {
        "steps": args.steps,
        "cfg": args.cfg,
        "seed": args.seed,
        "denoise": LIGHTNING_DENOISE,
        "shift": args.shift,
        "megapixels": args.megapixels,
        "lora_strength": args.lora_strength,
        "width": args.width,
        "height": args.height,
    }


def _resolve_generate_workflow(value: str) -> str:
    if value in (GENERATE_WORKFLOW, "qwen-image-bf16-v1"):
        # qwen-image-bf16-v1 is a doc alias; the GPU host only accepts qwen-image-v1.
        return GENERATE_WORKFLOW
    raise ImageToolError(
        f"unsupported generate workflow: {value} (use {GENERATE_WORKFLOW})"
    )


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
        workflow=_resolve_generate_workflow(args.workflow),
        parameters=_default_generate_params(args),
        negative_prompt=args.negative,
    )
    job_id = _job_id(str(submit.get("jobId")))
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
    parser.add_argument("--timeout", type=int, default=DEFAULT_JOB_TIMEOUT_SECONDS)
    parser.add_argument("--poll-seconds", type=int, default=DEFAULT_POLL_SECONDS)


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
    p_generate.add_argument("--width", type=int, default=GENERATE_WIDTH)
    p_generate.add_argument("--height", type=int, default=GENERATE_HEIGHT)
    p_generate.add_argument("--steps", type=int, default=LIGHTNING_STEPS)
    p_generate.add_argument("--cfg", type=float, default=LIGHTNING_CFG)
    p_generate.add_argument("--seed", type=int, default=0)
    p_generate.add_argument("--shift", type=float, default=LIGHTNING_SHIFT)
    p_generate.add_argument("--megapixels", type=float, default=LIGHTNING_MEGAPIXELS)
    p_generate.add_argument("--lora-strength", type=float, default=LIGHTNING_LORA_STRENGTH)
    p_generate.add_argument("--negative", default=" ")
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
