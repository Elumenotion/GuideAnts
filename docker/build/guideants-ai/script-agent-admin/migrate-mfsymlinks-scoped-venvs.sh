#!/usr/bin/env bash
# One-time migration: remove scoped python-venv trees created before Azure Files mfsymlinks.
set -euo pipefail

STATE_DIR="${SCRIPT_EXECUTION_ADMIN_STATE_DIR:-/var/lib/guideants/script-agent-admin}"
SCOPE_ROOT="${SCRIPT_EXECUTION_SCOPE_STATE_ROOT:-${STATE_DIR}/scopes}"
MARKER="${STATE_DIR}/.guideants/mfsymlinks-venv-reset.done"

log() {
    printf 'script-agent-admin: %s\n' "$*" >&2
}

if [ -f "$MARKER" ]; then
    exit 0
fi

# Only run when the share supports venv symlinks (Azure ACA sets mfsymlinks on the mount).
probe_dir="${SCOPE_ROOT}/.mfsymlinks-probe-$$"
mkdir -p "$probe_dir"
if ! ln -sfn "$probe_dir/lib" "$probe_dir/lib64" 2>/dev/null; then
    log "mfsymlinks probe failed; skipping scoped venv migration"
    rm -rf "$probe_dir"
    exit 0
fi
rm -rf "$probe_dir"

venv_count=0
applied_count=0
if [ -d "$SCOPE_ROOT" ]; then
    venv_count="$(find "$SCOPE_ROOT" -type d -name python-venv 2>/dev/null | wc -l | tr -d ' ')"
    find "$SCOPE_ROOT" -type d -name python-venv -exec rm -rf {} + 2>/dev/null || true
    applied_count="$(find "$SCOPE_ROOT" -type f -name applied-state.json 2>/dev/null | wc -l | tr -d ' ')"
    find "$SCOPE_ROOT" -type f -name applied-state.json -delete 2>/dev/null || true
fi

mkdir -p "$(dirname "$MARKER")"
printf 'completedUtc=%s\nremovedVenvs=%s\nremovedAppliedState=%s\n' \
    "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$venv_count" "$applied_count" > "$MARKER"

log "mfsymlinks scoped venv migration complete (removed ${venv_count} venv dirs, ${applied_count} applied-state files)"
