#!/usr/bin/env bash
# Boot-time repair of scoped Python venvs on the shared state volume.
#
# Removes only venvs that are provably unusable — those whose interpreter
# symlink or pyvenv.cfg never materialized, which is what a venv created
# before the share supported symlinks looks like. A venv that still resolves
# its interpreter is never touched. Always exits 0 so container start and
# deployment can never fail because of this maintenance pass.

set -uo pipefail

STATE_DIR="${SCRIPT_EXECUTION_ADMIN_STATE_DIR:-/var/lib/guideants/script-agent-admin}"
SCOPE_ROOT="${SCRIPT_EXECUTION_SCOPE_STATE_ROOT:-${STATE_DIR}/scopes}"

log() {
    printf 'script-agent-admin: %s\n' "$*" >&2
}

[ -d "$SCOPE_ROOT" ] || exit 0

# Without symlink support on the mount every venv would look broken, so a
# failed probe must mean "change nothing" rather than "delete everything".
probe_dir="$(mktemp -d "${SCOPE_ROOT}/.symlink-probe-XXXXXX" 2>/dev/null)" || exit 0
if ! ln -s probe-target "${probe_dir}/probe-link" 2>/dev/null; then
    rm -rf "$probe_dir" 2>/dev/null
    log "share does not support symlinks; leaving scoped venvs untouched"
    exit 0
fi
rm -rf "$probe_dir" 2>/dev/null

is_healthy_venv() {
    venv_path="$1"
    [ -f "${venv_path}/pyvenv.cfg" ] || return 1
    [ -x "${venv_path}/bin/python" ] || [ -x "${venv_path}/bin/python3" ] || return 1
    return 0
}

removed=0
kept=0

# Bounded depth keeps this to a few directory listings; it never walks the
# thousands of files inside a venv.
while IFS= read -r venv_path; do
    [ -n "$venv_path" ] || continue
    case "$venv_path" in
        "${SCOPE_ROOT}"/*/*/python-venv) ;;
        *) continue ;;
    esac

    if is_healthy_venv "$venv_path"; then
        kept=$((kept + 1))
        continue
    fi

    if rm -rf "$venv_path" 2>/dev/null; then
        rm -f "$(dirname "$venv_path")/applied-state.json" 2>/dev/null
        removed=$((removed + 1))
        log "removed unusable scoped venv ${venv_path}"
    else
        log "WARNING: could not remove unusable scoped venv ${venv_path}"
    fi
done <<EOF
$(find "$SCOPE_ROOT" -mindepth 3 -maxdepth 3 -type d -name python-venv 2>/dev/null)
EOF

if [ "$removed" -gt 0 ]; then
    log "scoped venv repair removed ${removed} unusable venv(s), kept ${kept}; removed venvs rebuild on next script run"
fi

exit 0
