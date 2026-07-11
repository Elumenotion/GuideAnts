#!/usr/bin/env python3
"""Phase 0 contract parse validation. Schema validation deferred to Phase 1A (jsonschema)."""
from __future__ import annotations

import json
import sys
from pathlib import Path

CONTRACTS_DIR = Path(__file__).resolve().parent
FIXTURES = sorted(CONTRACTS_DIR.glob("*.fixture.json"))
SCHEMA_FILES = sorted(CONTRACTS_DIR.glob("schema.*.json"))


def load_json(path: Path) -> object:
    with path.open("r", encoding="utf-8") as handle:
        return json.load(handle)


def main() -> int:
    failures: list[str] = []
    d12_path = CONTRACTS_DIR / "immutable-operation-input.fixture.json"

    for path in SCHEMA_FILES + FIXTURES:
        try:
            payload = load_json(path)
        except json.JSONDecodeError as exc:
            failures.append(f"{path.name}: JSON parse failed: {exc}")
            continue

        if not isinstance(payload, (dict, list)):
            failures.append(f"{path.name}: root must be object or array")

    if d12_path.exists():
        mtp = load_json(d12_path)
        assert isinstance(mtp, dict)
        if mtp.get("mmprojFiles"):
            failures.append("immutable-operation-input.fixture.json: D12 requires empty mmprojFiles for MTP")
        forbidden = {"image-min-tokens", "mmproj", "projector"}
        preset = mtp.get("routerPreset") or {}
        if isinstance(preset, dict):
            for key in forbidden:
                if key in preset:
                    failures.append(
                        f"immutable-operation-input.fixture.json: D12 forbids routerPreset key '{key}'"
                    )

    if failures:
        for item in failures:
            print(f"FAIL {item}", file=sys.stderr)
        return 1

    print(
        f"PASS parsed {len(FIXTURES)} fixtures and {len(SCHEMA_FILES)} schema files under {CONTRACTS_DIR.name}/"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
