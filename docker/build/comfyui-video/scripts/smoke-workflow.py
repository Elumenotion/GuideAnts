#!/usr/bin/env python3
"""Validate the curated workflow against a running loopback ComfyUI."""

from __future__ import annotations

import json
import sys
import urllib.request
from pathlib import Path


ROOT = Path("/opt/guideants/comfyui-video")


def main() -> int:
    lock = json.loads((ROOT / "source-lock.json").read_text(encoding="utf-8"))
    workflow_path = ROOT / lock["workflow"]["path"]
    workflow = json.loads(workflow_path.read_text(encoding="utf-8"))
    if not workflow or any(
        not isinstance(node, dict)
        or not isinstance(node.get("class_type"), str)
        or not isinstance(node.get("inputs"), dict)
        for node in workflow.values()
    ):
        print("workflow is not a ComfyUI API prompt graph", file=sys.stderr)
        return 1
    node_ids = set(workflow)
    for node_id, node in workflow.items():
        for value in node["inputs"].values():
            if (
                isinstance(value, list)
                and len(value) == 2
                and isinstance(value[0], str)
                and value[0] not in node_ids
            ):
                print(f"workflow node {node_id} links missing node {value[0]}", file=sys.stderr)
                return 1
    with urllib.request.urlopen("http://127.0.0.1:8188/object_info", timeout=30) as response:
        installed = json.load(response)
    required = {node["class_type"] for node in workflow.values()}
    missing = sorted(required - set(installed))
    if missing:
        print(f"missing required ComfyUI node classes: {', '.join(missing)}", file=sys.stderr)
        return 1
    print(f"workflow {lock['workflow']['id']} node classes and links validated")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
