#!/usr/bin/env python3
"""Publish GuideAnts API workflow templates as ComfyUI browser-loadable UI JSON."""

from __future__ import annotations

import json
import sys
import urllib.request
from pathlib import Path

sys.path.insert(0, "/app/adapter")

from guideants_video_adapter.workflow_ui import api_prompt_to_ui_workflow, is_api_prompt


def main() -> int:
    templates_dir = Path("/opt/guideants/comfyui-video/workflows")
    publish_dir = Path("/opt/ComfyUI/user/default/workflows/guideants")
    publish_dir.mkdir(parents=True, exist_ok=True)

    object_info = json.loads(urllib.request.urlopen("http://127.0.0.1:8188/object_info").read())

    published = 0
    for template_path in sorted(templates_dir.glob("*.json")):
        payload = json.loads(template_path.read_text(encoding="utf-8"))
        if not is_api_prompt(payload):
            print(f"skip non-api template: {template_path.name}", file=sys.stderr)
            continue
        ui_workflow = api_prompt_to_ui_workflow(payload, object_info)
        target = publish_dir / template_path.name
        if target.is_symlink():
            target.unlink()
        target.write_text(json.dumps(ui_workflow, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
        published += 1
        print(f"published {target.name} ({len(ui_workflow['nodes'])} nodes)")

    if published == 0:
        print("no API workflow templates published", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
