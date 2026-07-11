"""Shared paths for llama-admin unit tests."""

from __future__ import annotations

import sys
from pathlib import Path

SERVICE_ROOT = Path(__file__).resolve().parents[1]
REPO_ROOT = SERVICE_ROOT.parents[3]
LIB_ROOT = SERVICE_ROOT.parent / "lib"
CONTRACTS_ROOT = REPO_ROOT / "docs" / "llama-router-preset-ui-execution" / "contracts"

for path in (str(LIB_ROOT), str(SERVICE_ROOT)):
    if path not in sys.path:
        sys.path.insert(0, path)
