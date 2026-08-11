#!/usr/bin/env python3
"""Fail-fast validation for the immutable image payload and CUDA runtime."""

from __future__ import annotations

import argparse
import json
import re
import subprocess
import sys
from pathlib import Path


ROOT = Path("/opt/guideants/comfyui-video")
COMFYUI = Path("/opt/ComfyUI")


def fail(message: str) -> None:
    raise SystemExit(f"verify-install: {message}")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--build", action="store_true", help="skip the runtime GPU probe")
    args = parser.parse_args()

    lock = json.loads((ROOT / "source-lock.json").read_text(encoding="utf-8"))
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

    import torch

    if torch.__version__ != "2.11.0+cu130":
        fail(f"expected torch 2.11.0+cu130, found {torch.__version__}")
    if torch.version.cuda != "13.0":
        fail(f"expected CUDA 13.0 PyTorch runtime, found {torch.version.cuda!r}")
    if not torch.cuda.is_available() or torch.cuda.device_count() != 1:
        fail(f"exactly one visible CUDA GPU is required; found {torch.cuda.device_count()}")
    props = torch.cuda.get_device_properties(0)
    if props.major < 8:
        fail(f"unsupported CUDA compute capability {props.major}.{props.minor}")
    print(
        json.dumps(
            {
                "cuda": torch.version.cuda,
                "device": torch.cuda.get_device_name(0),
                "computeCapability": f"{props.major}.{props.minor}",
                "torch": torch.__version__,
            }
        )
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
