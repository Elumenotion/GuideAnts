#!/usr/bin/env python3
"""Scoped capability preflight for talking-head skills.

Prints one JSON verdict:

  {"scenario": ..., "open": bool, "blockers": [...], "warnings": [...], "evidence": {...}}

Scenarios: i2v | probe
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
    }

    if scenario in {"i2v", "probe"}:
        evidence["workflow"] = "infinitetalk-i2v-v1"
        blockers.extend(missing_flag(caps, "ready"))
        blockers.extend(missing_flag(caps, "composite_ready"))

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
