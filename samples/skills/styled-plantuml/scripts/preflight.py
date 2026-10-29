#!/usr/bin/env python3
"""Scoped preflight for the styled-plantuml skill.

Prints one JSON verdict:

  {"scenario": "styled-plantuml", "open": bool, "blockers": [...],
   "warnings": [...], "evidence": {...}}

Checks, in order:
  1. gateway env vars (QWEN_IMAGE_SKILL_BASE_URL + QWEN_IMAGE_SKILL_TOKEN)
  2. gateway reachable (/health)
  3. image_edit_bf16_ready true
  4. local deps: cairosvg importable, ffmpeg on PATH
"""
from __future__ import annotations

import importlib.util
import json
import shutil
import sys

from skill_gateway_client import (
    fetch_capabilities,
    gateway_env_missing_blocker,
    probe_gateway,
    require_gateway,
    using_skill_gateway,
)

SCENARIO = "styled-plantuml"
READY_FLAG = "image_edit_bf16_ready"
WORKFLOW = "qwen-image-edit-bf16-v1"


def missing_from_capabilities(caps: dict, flag: str) -> list[str]:
    if caps.get(flag) is True:
        return []
    blockers = [f"{flag} is false"]
    details_key = flag.replace("_ready", "_details")
    details = caps.get(details_key)
    if isinstance(details, dict) and details.get("missing"):
        blockers.extend(str(item) for item in details["missing"])
    return blockers


def run_preflight() -> dict:
    blockers: list[str] = []
    warnings: list[str] = []
    evidence: dict = {}

    # local dependencies (prepare + overlay)
    evidence["cairosvg"] = importlib.util.find_spec("cairosvg") is not None
    if not evidence["cairosvg"]:
        blockers.append("cairosvg not importable in the sandbox (prepare step needs it)")
    ffmpeg = shutil.which("ffmpeg")
    evidence["ffmpeg"] = bool(ffmpeg)
    if not ffmpeg:
        blockers.append("ffmpeg not on PATH (overlay step needs it)")

    if not using_skill_gateway():
        blockers.append(gateway_env_missing_blocker())
    else:
        gateway = probe_gateway()
        evidence["gateway"] = gateway
        if not gateway.get("open"):
            blockers.append(
                "skillGateway: QWEN_IMAGE_SKILL_BASE_URL set but gateway not open - "
                f"{gateway.get('error') or gateway.get('status') or gateway}"
            )
        else:
            try:
                caps = fetch_capabilities()
            except Exception as exc:
                blockers.append(f"capabilities request failed: {type(exc).__name__}: {exc}")
            else:
                evidence["capabilities"] = {
                    READY_FLAG: caps.get(READY_FLAG),
                    "precision": caps.get("precision"),
                    "workflow_versions": caps.get("workflow_versions"),
                }
                blockers.extend(missing_from_capabilities(caps, READY_FLAG))

    return {
        "scenario": SCENARIO,
        "open": not blockers,
        "blockers": blockers,
        "warnings": warnings,
        "evidence": evidence,
        "route": "route_remote_skill_gateway",
        "workflow": WORKFLOW,
    }


def main() -> None:
    if using_skill_gateway():
        require_gateway()
    report = run_preflight()
    print(json.dumps(report, separators=(",", ":")))
    if not report["open"]:
        sys.exit(1)


if __name__ == "__main__":
    main()
