#!/usr/bin/env python3
"""Scoped capability preflight for Qwen Image task skills.

Prints one JSON verdict:

  {"scenario": ..., "open": bool, "blockers": [...], "warnings": [...], "evidence": {...}}

Scenarios: generate | edit-bf16 | inpaint-bf16 | probe

Generate readiness flags (adapter capabilities):
- draft / Lightning: `image_generate_ready` + workflow `qwen-image-v1`
- high / 20-step: `image_generate_20_ready` + workflow `qwen-image-generate-20-v1`

Pass `--quality high` when the user asked for the non-Lightning job so preflight
checks the correct flag.
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

GENERATE_QUALITY_FLAGS = {
    "draft": ("image_generate_ready", "qwen-image-v1"),
    "high": ("image_generate_20_ready", "qwen-image-generate-20-v1"),
}
SCENARIO_FLAGS = {
    "edit-bf16": ("image_edit_bf16_ready", "qwen-image-edit-bf16-v1"),
    "inpaint-bf16": ("image_edit_bf16_inpaint_ready", "qwen-image-edit-bf16-inpaint-v1"),
}
# Stale skill docs may still say generate-bf16 — same check as generate.
SCENARIO_ALIASES = {
    "generate-bf16": "generate",
}


def missing_from_capabilities(caps: dict, flag: str) -> list[str]:
    if caps.get(flag) is True:
        return []
    blockers = [f"{flag} is false"]
    details_key = flag.replace("_ready", "_details")
    details = caps.get(details_key)
    if isinstance(details, dict) and details.get("missing"):
        blockers.extend(str(item) for item in details["missing"])
    return blockers


def generate_flag_for_quality(quality: str) -> tuple[str, str]:
    profile = GENERATE_QUALITY_FLAGS.get(quality)
    if profile is None:
        allowed = ", ".join(sorted(GENERATE_QUALITY_FLAGS))
        raise ValueError(f"unsupported quality {quality!r} (use {allowed})")
    return profile


def run_preflight(scenario: str, *, quality: str = "draft") -> dict:
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
            "skillGateway: QWEN_IMAGE_SKILL_BASE_URL set but gateway not open — "
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
        "image_generate_ready": caps.get("image_generate_ready"),
        "image_generate_20_ready": caps.get("image_generate_20_ready"),
        "image_edit_bf16_ready": caps.get("image_edit_bf16_ready"),
        "image_edit_bf16_inpaint_ready": caps.get("image_edit_bf16_inpaint_ready"),
        "precision": caps.get("precision"),
        "workflow_versions": caps.get("workflow_versions"),
    }

    resolved = SCENARIO_ALIASES.get(scenario, scenario)
    if resolved == "probe":
        for flag, _workflow in GENERATE_QUALITY_FLAGS.values():
            if caps.get(flag) is not True:
                blockers.extend(missing_from_capabilities(caps, flag))
        for _name, (flag, _workflow) in SCENARIO_FLAGS.items():
            if caps.get(flag) is not True:
                blockers.extend(missing_from_capabilities(caps, flag))
        open_ok = not blockers
    elif resolved == "generate":
        flag, workflow = generate_flag_for_quality(quality)
        evidence["quality"] = quality
        evidence["workflow"] = workflow
        blockers.extend(missing_from_capabilities(caps, flag))
        open_ok = not blockers
    else:
        flag, workflow = SCENARIO_FLAGS[resolved]
        evidence["workflow"] = workflow
        blockers.extend(missing_from_capabilities(caps, flag))
        open_ok = not blockers

    return {
        "scenario": resolved,
        "open": open_ok,
        "blockers": blockers,
        "warnings": warnings,
        "evidence": evidence,
        "route": "route_remote_skill_gateway",
    }


def main() -> None:
    parser = argparse.ArgumentParser(description="Qwen Image scoped preflight")
    parser.add_argument(
        "--for",
        dest="scenario",
        required=True,
        choices=["generate", "generate-bf16", "edit-bf16", "inpaint-bf16", "probe"],
    )
    parser.add_argument(
        "--quality",
        choices=sorted(GENERATE_QUALITY_FLAGS),
        default="draft",
        help="generate scenario only: draft checks image_generate_ready; high checks image_generate_20_ready",
    )
    args = parser.parse_args()
    if using_skill_gateway():
        require_gateway()
    try:
        report = run_preflight(args.scenario, quality=args.quality)
    except ValueError as exc:
        sys.stderr.write(f"{exc}\n")
        sys.exit(1)
    print(json.dumps(report, separators=(",", ":")))
    if not report["open"]:
        sys.exit(1)


if __name__ == "__main__":
    main()
