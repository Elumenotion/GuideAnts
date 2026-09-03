#!/usr/bin/env python3
"""Scoped capability preflight for talking-head skills.

Prints one JSON verdict:

  {"scenario": ..., "open": bool, "blockers": [...], "warnings": [...], "evidence": {...}}

Scenarios: i2v | probe

Tested path blockers: ready, composite_ready, infinitetalk-i2v-v1.
If capabilities includes fg_upscaler / composite_width / composite_height, those
must match basicvsrpp and 1280x720. Missing keys are the tested Max adapter;
do not treat absence as failure.
"""
from __future__ import annotations

import argparse
import json
import sys

from skill_gateway_client import (
    fetch_capabilities,
    gateway_env_missing_blocker,
    probe_gateway,
    require_gateway,
    using_skill_gateway,
)

TESTED_WORKFLOW = "infinitetalk-i2v-v1"
TESTED_UPSCALER = "basicvsrpp"
TESTED_WIDTH = 1280
TESTED_HEIGHT = 720


def missing_flag(caps: dict, flag: str) -> list[str]:
    if caps.get(flag) is True:
        return []
    blockers = [f"{flag} is false"]
    if flag == "composite_ready":
        missing = caps.get("composite_missing")
        if isinstance(missing, list) and missing:
            blockers.extend(str(item) for item in missing)
    details = caps.get("details")
    if isinstance(details, dict) and details.get("missing"):
        blockers.extend(str(item) for item in details["missing"])
    return blockers


def tested_path_blockers(caps: dict) -> list[str]:
    blockers: list[str] = []
    versions = caps.get("workflow_versions")
    if not isinstance(versions, list) or TESTED_WORKFLOW not in versions:
        blockers.append(f"{TESTED_WORKFLOW} is not in workflow_versions")
    upscaler = caps.get("fg_upscaler")
    if upscaler is not None and upscaler != TESTED_UPSCALER:
        blockers.append(
            f"fg_upscaler is {upscaler!r}; tested path requires {TESTED_UPSCALER}"
        )
    width = caps.get("composite_width")
    height = caps.get("composite_height")
    if width is not None or height is not None:
        if width != TESTED_WIDTH or height != TESTED_HEIGHT:
            blockers.append(
                f"composite canvas is {width}x{height}; tested path is "
                f"{TESTED_WIDTH}x{TESTED_HEIGHT}"
            )
    return blockers


def run_preflight(scenario: str) -> dict:
    blockers: list[str] = []
    warnings: list[str] = []
    evidence: dict = {}

    if not using_skill_gateway():
        blockers.append(gateway_env_missing_blocker())
        return {
            "scenario": scenario,
            "open": False,
            "blockers": blockers,
            "warnings": warnings,
            "evidence": evidence,
            "route": "route_remote_skill_gateway",
        }

    gateway = probe_gateway()
    evidence["gateway"] = gateway
    if not gateway.get("open"):
        blockers.append(
            "skillGateway: TALKING_HEAD_SKILL_BASE_URL set but gateway not open — "
            f"{gateway.get('error') or gateway.get('status') or gateway}"
        )
        return {
            "scenario": scenario,
            "open": False,
            "blockers": blockers,
            "warnings": warnings,
            "evidence": evidence,
            "route": "route_remote_skill_gateway",
        }

    try:
        caps = fetch_capabilities()
    except Exception as exc:
        blockers.append(f"capabilities request failed: {type(exc).__name__}: {exc}")
        return {
            "scenario": scenario,
            "open": False,
            "blockers": blockers,
            "warnings": warnings,
            "evidence": evidence,
        }

    evidence["capabilities"] = {
        "ready": caps.get("ready"),
        "composite_ready": caps.get("composite_ready"),
        "composite_missing": caps.get("composite_missing"),
        "workflow_versions": caps.get("workflow_versions"),
        "fg_upscaler": caps.get("fg_upscaler"),
        "composite_width": caps.get("composite_width"),
        "composite_height": caps.get("composite_height"),
    }

    if scenario in {"i2v", "probe"}:
        evidence["workflow"] = TESTED_WORKFLOW
        blockers.extend(missing_flag(caps, "ready"))
        blockers.extend(missing_flag(caps, "composite_ready"))
        blockers.extend(tested_path_blockers(caps))

    open_ok = not blockers
    return {
        "scenario": scenario,
        "open": open_ok,
        "blockers": blockers,
        "warnings": warnings,
        "evidence": evidence,
        "route": "route_remote_skill_gateway",
    }


def main() -> None:
    parser = argparse.ArgumentParser(description="Talking-head scoped preflight")
    parser.add_argument(
        "--for",
        dest="scenario",
        required=True,
        choices=["i2v", "probe"],
    )
    args = parser.parse_args()
    if using_skill_gateway():
        require_gateway()
    report = run_preflight(args.scenario)
    print(json.dumps(report, separators=(",", ":")))
    if not report["open"]:
        sys.exit(1)


if __name__ == "__main__":
    main()
