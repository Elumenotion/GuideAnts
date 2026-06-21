#!/usr/bin/env bash
set -euo pipefail

STATE_DIR="${SCRIPT_EXECUTION_ADMIN_STATE_DIR:-/var/lib/guideants/script-agent-admin}"
DEFAULTS_DIR="${SCRIPT_EXECUTION_ADMIN_DEFAULTS_DIR:-/opt/guideants/script-agent-admin/defaults}"
FAIL_OPEN="${SCRIPT_EXECUTION_ADMIN_FAIL_OPEN:-false}"
CONFIG_PATH="${STATE_DIR}/config.json"
REQUIREMENTS_PATH="${STATE_DIR}/requirements.txt"
APT_PACKAGES_PATH="${STATE_DIR}/apt-packages.txt"
APPLIED_STATE_PATH="${STATE_DIR}/applied-state.json"

log() {
    printf 'script-agent-admin: %s\n' "$*" >&2
}

fail() {
    log "ERROR: $*"
    if [ "$FAIL_OPEN" = "true" ]; then
        return 0
    fi
    exit 1
}

mkdir -p "$STATE_DIR"

if [ ! -f "$CONFIG_PATH" ]; then
    if [ -f "${DEFAULTS_DIR}/config.json" ]; then
        cp "${DEFAULTS_DIR}/config.json" "$CONFIG_PATH"
    else
        printf '{"version":1}\n' > "$CONFIG_PATH"
    fi
fi

if [ ! -f "$REQUIREMENTS_PATH" ]; then
    if [ -f "${DEFAULTS_DIR}/requirements.txt" ]; then
        cp "${DEFAULTS_DIR}/requirements.txt" "$REQUIREMENTS_PATH"
    else
        : > "$REQUIREMENTS_PATH"
    fi
fi

if [ ! -f "$APT_PACKAGES_PATH" ]; then
    if [ -f "${DEFAULTS_DIR}/apt-packages.txt" ]; then
        cp "${DEFAULTS_DIR}/apt-packages.txt" "$APT_PACKAGES_PATH"
    else
        : > "$APT_PACKAGES_PATH"
    fi
fi

if ! python3 - "$CONFIG_PATH" <<'PY'
import json
import sys

path = sys.argv[1]
with open(path, "r", encoding="utf-8") as handle:
    data = json.load(handle)
if not isinstance(data, dict):
    raise SystemExit("config.json must contain a JSON object")
version = data.get("version", 1)
if not isinstance(version, int):
    raise SystemExit("config.json version must be an integer")
PY
then
    fail "config validation failed for ${CONFIG_PATH}"
fi

if ! python3 - "$REQUIREMENTS_PATH" <<'PY'
import sys

path = sys.argv[1]
blocked_prefixes = ("-e", "--", ".", "/")
with open(path, "r", encoding="utf-8") as handle:
    for index, raw in enumerate(handle, start=1):
        line = raw.strip()
        if not line or line.startswith("#"):
            continue
        lowered = line.lower()
        if lowered.startswith(blocked_prefixes) or "://" in lowered or "git+" in lowered:
            raise SystemExit(f"requirements.txt line {index} uses a blocked install source or option")
PY
then
    fail "requirements validation failed for ${REQUIREMENTS_PATH}"
fi

if ! python3 - "$APT_PACKAGES_PATH" <<'PY'
import re
import sys

path = sys.argv[1]
pattern = re.compile(r"^[a-z0-9][a-z0-9+.-]*$", re.IGNORECASE)
with open(path, "r", encoding="utf-8") as handle:
    for index, raw in enumerate(handle, start=1):
        line = raw.split("#", 1)[0].strip()
        if not line:
            continue
        if not pattern.match(line):
            raise SystemExit(f"apt-packages.txt line {index} is not a valid package name")
PY
then
    fail "apt package validation failed for ${APT_PACKAGES_PATH}"
fi

REQUIREMENTS_HASH="$(sha256sum "$REQUIREMENTS_PATH" | awk '{print $1}')"
APT_PACKAGES_HASH="$(sha256sum "$APT_PACKAGES_PATH" | awk '{print $1}')"
PREVIOUS_REQUIREMENTS_HASH=""
PREVIOUS_APT_PACKAGES_HASH=""
if [ -f "$APPLIED_STATE_PATH" ]; then
    PREVIOUS_REQUIREMENTS_HASH="$(python3 - "$APPLIED_STATE_PATH" <<'PY' 2>/dev/null || true
import json
import sys

with open(sys.argv[1], "r", encoding="utf-8") as handle:
    print(json.load(handle).get("requirementsHash", ""))
PY
)"
    PREVIOUS_APT_PACKAGES_HASH="$(python3 - "$APPLIED_STATE_PATH" <<'PY' 2>/dev/null || true
import json
import sys

with open(sys.argv[1], "r", encoding="utf-8") as handle:
    print(json.load(handle).get("aptPackagesHash", ""))
PY
)"
fi

if [ "$APT_PACKAGES_HASH" != "$PREVIOUS_APT_PACKAGES_HASH" ] && [ -s "$APT_PACKAGES_PATH" ]; then
    mapfile -t APT_PACKAGES < <(python3 - "$APT_PACKAGES_PATH" <<'PY'
import sys

with open(sys.argv[1], "r", encoding="utf-8") as handle:
    for raw in handle:
        line = raw.split("#", 1)[0].strip()
        if line:
            print(line)
PY
)
    if [ "${#APT_PACKAGES[@]}" -gt 0 ]; then
        log "installing admin apt packages: ${APT_PACKAGES[*]}"
        apt-get update || fail "apt-get update failed"
        apt-get install -y --no-install-recommends "${APT_PACKAGES[@]}" || fail "apt-get install failed"
        rm -rf /var/lib/apt/lists/*
    fi
fi

if [ "$REQUIREMENTS_HASH" != "$PREVIOUS_REQUIREMENTS_HASH" ] && [ -s "$REQUIREMENTS_PATH" ]; then
    PYTHON_BIN="${SCRIPT_EXECUTION_ADMIN_BOOTSTRAP_PYTHON:-/opt/venv/bin/python}"
    if [ ! -x "$PYTHON_BIN" ]; then
        PYTHON_BIN="$(command -v python3 || command -v python)"
    fi
    log "installing admin requirements into image runtime venv with ${PYTHON_BIN}"
    "$PYTHON_BIN" -m pip install -r "$REQUIREMENTS_PATH" || fail "pip install failed for ${REQUIREMENTS_PATH}"
fi

if [ "$REQUIREMENTS_HASH" = "$PREVIOUS_REQUIREMENTS_HASH" ] && [ "$APT_PACKAGES_HASH" = "$PREVIOUS_APT_PACKAGES_HASH" ]; then
    log "startup reconcile skipped; requirements and apt package hashes unchanged"
    exit 0
fi

python3 - "$APPLIED_STATE_PATH" "$REQUIREMENTS_HASH" "$REQUIREMENTS_PATH" "$APT_PACKAGES_HASH" "$APT_PACKAGES_PATH" <<'PY'
import json
import os
import sys
from datetime import datetime, timezone

path, requirements_hash, requirements_path, apt_packages_hash, apt_packages_path = sys.argv[1:]
payload = {
    "version": 1,
    "requirementsHash": requirements_hash,
    "requirementsPath": requirements_path,
    "aptPackagesHash": apt_packages_hash,
    "aptPackagesPath": apt_packages_path,
    "appliedAt": datetime.now(timezone.utc).isoformat(),
}
tmp = f"{path}.tmp"
with open(tmp, "w", encoding="utf-8") as handle:
    json.dump(payload, handle, indent=2)
    handle.write("\n")
os.replace(tmp, path)
PY

log "startup reconcile completed"
