#!/usr/bin/env python3
"""Deployment probe for talking-head i2v skills via GPU host gateway."""
from __future__ import annotations

import json
import sys

from preflight import run_preflight
from skill_gateway_client import probe_gateway, skill_base_url, skill_token, using_skill_gateway


def main() -> None:
    gateway = probe_gateway()
    scenarios = run_preflight("probe")
    report = {
        "service": "talking-head",
        "workflow": "infinitetalk-i2v-v1",
        "env": {
            "TALKING_HEAD_SKILL_BASE_URL": skill_base_url() or "missing",
            "TALKING_HEAD_SKILL_TOKEN": "set" if skill_token() else "missing",
        },
        "gatewayConfigured": using_skill_gateway(),
        "skillGateway": gateway,
        "routes": {
            "route_remote_skill_gateway": {
                "open": bool(gateway.get("open")),
                "note": (
                    "PC sandbox → GPU host /talking-head-skill with TALKING_HEAD_SKILL_BASE_URL + "
                    "TALKING_HEAD_SKILL_TOKEN. If missing, ask the user to set them in the "
                    "guide's Environment variables — do not scan the LAN or guess the GPU host's IP."
                ),
                "evidence": gateway,
            },
        },
        "scenarios": scenarios,
        "routing": {
            "avatar_audio_background_to_mp4": "talking-head-i2v",
        },
    }
    print(json.dumps(report, separators=(",", ":")))
    if not scenarios.get("open"):
        sys.exit(1)


if __name__ == "__main__":
    main()
