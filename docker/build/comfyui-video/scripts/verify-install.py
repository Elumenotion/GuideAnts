#!/usr/bin/env python3
"""Fail-fast validation for the immutable image payload and GPU runtime."""

from __future__ import annotations

import argparse
import json
import os
import re
import subprocess
import sys
from pathlib import Path


ROOT = Path("/opt/guideants/comfyui-video")
COMFYUI = Path("/opt/ComfyUI")


def fail(message: str) -> None:
    raise SystemExit(f"verify-install: {message}")


def resolve_backend(lock: dict) -> str:
    backend = os.environ.get("VIDEO_GPU_BACKEND", "").strip().lower()
    if backend:
        return backend
    version = lock["pytorch"]["version"]
    if "+cu" in version:
        return "cuda13"
    if "+rocm" in version:
        return "rocm"
    fail(f"cannot infer GPU backend from torch pin {version!r}")


def validate_runtime(backend: str, lock: dict) -> int:
    import torch

    expected_torch = lock["pytorch"]["version"]
    if torch.__version__ != expected_torch:
        fail(f"expected torch {expected_torch}, found {torch.__version__}")

    if not torch.cuda.is_available() or torch.cuda.device_count() != 1:
        fail(f"exactly one visible GPU is required; found {torch.cuda.device_count()}")

    props = torch.cuda.get_device_properties(0)
    device_name = torch.cuda.get_device_name(0)
    payload: dict[str, str] = {
        "backend": backend,
        "device": device_name,
        "torch": torch.__version__,
    }

    if backend == "cuda13":
        if torch.version.cuda != "13.0":
            fail(f"expected CUDA 13.0 PyTorch runtime, found {torch.version.cuda!r}")
        if props.major < 8:
            fail(f"unsupported CUDA compute capability {props.major}.{props.minor}")
        payload["cuda"] = torch.version.cuda
        payload["computeCapability"] = f"{props.major}.{props.minor}"
    elif backend == "rocm":
        if not re.search(r"\+rocm", torch.__version__):
            fail(f"expected ROCm PyTorch runtime, found {torch.__version__}")
        if not re.search(r"(?i)amd|radeon", device_name):
            fail(f"expected an AMD GPU device, found {device_name!r}")
        payload["rocm"] = torch.__version__.split("+", 1)[1]
    else:
        fail(f"unsupported VIDEO_GPU_BACKEND: {backend}")

    print(json.dumps(payload))
    return 0


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--build", action="store_true", help="skip the runtime GPU probe")
    args = parser.parse_args()

    lock = json.loads((ROOT / "source-lock.json").read_text(encoding="utf-8"))
    backend = resolve_backend(lock)
    reference = lock["baseImage"]["reference"]
    if not re.fullmatch(r"[^@\s]+@sha256:[0-9a-f]{64}", reference):
        fail("base image is not digest-pinned; unresolved release builds are forbidden")
    if reference.rsplit("@", 1)[1] != lock["baseImage"]["platformDigest"]:
        fail("base image reference must use the locked linux/amd64 platform digest")
    if lock["pytorch"]["attention"] != "sdpa":
        fail("the Phase 0/1 baseline requires SDPA")

    manifest = json.loads((ROOT / lock["modelCatalog"]).read_text(encoding="utf-8"))
    for bundle in manifest.get("bundles", {}).values():
        for artifact in bundle.get("artifacts", []):
            required = {"repository", "revision", "file", "path", "url", "size", "sha256"}
            if not required.issubset(artifact):
                fail(f"model artifact has incomplete schema: {artifact.get('id', '<unknown>')}")
            expected_url = (
                f"https://huggingface.co/{artifact['repository']}/resolve/"
                f"{artifact['revision']}/{artifact['file']}"
            )
            if artifact["url"] != expected_url:
                fail(f"model artifact URL is not immutable: {artifact.get('id', '<unknown>')}")
            relative = Path(artifact["path"])
            if relative.is_absolute() or ".." in relative.parts:
                fail(f"model artifact path escapes /models: {artifact['path']}")
            if not isinstance(artifact["size"], int) or artifact["size"] <= 0:
                fail(f"model artifact size is invalid: {artifact['path']}")
            if not re.fullmatch(r"[0-9a-f]{64}", artifact["sha256"]):
                fail(f"model artifact checksum is invalid: {artifact['path']}")

    workflow = json.loads((ROOT / lock["workflow"]["path"]).read_text(encoding="utf-8"))
    if not workflow or any(
        not isinstance(node, dict)
        or not isinstance(node.get("class_type"), str)
        or not isinstance(node.get("inputs"), dict)
        for node in workflow.values()
    ):
        fail("workflow is not a ComfyUI API prompt graph")
    for node_id, node in workflow.items():
        for value in node["inputs"].values():
            if (
                isinstance(value, list)
                and len(value) == 2
                and isinstance(value[0], str)
                and value[0] not in workflow
            ):
                fail(f"workflow node {node_id} links missing node {value[0]}")
    if backend == "rocm":
        for node in workflow.values():
            if node.get("class_type") != "WanVideoBlockSwap":
                continue
            blocks = node.get("inputs", {}).get("blocks_to_swap")
            if blocks != 0:
                fail(
                    "ROCm workflow must keep all transformer blocks on GPU "
                    f"(blocks_to_swap=0, found {blocks!r})"
                )

    for source in lock["sources"]:
        if source.get("embedded", True) is False:
            continue
        checkout = COMFYUI if source["name"] == "ComfyUI" else COMFYUI / "custom_nodes" / source["name"]
        marker = checkout / ".guideants-source-commit"
        if not checkout.is_dir() or not marker.is_file():
            fail(f"missing pinned source checkout: {source['name']}")
        if marker.read_text(encoding="utf-8").strip() != source["commit"]:
            fail(f"source revision mismatch: {source['name']}")

    subprocess.run([sys.executable, "-m", "pip", "check"], check=True)
    import fastapi
    import multipart
    import uvicorn

    if args.build:
        print("immutable install validation passed")
        return 0

    return validate_runtime(backend, lock)


if __name__ == "__main__":
    raise SystemExit(main())
