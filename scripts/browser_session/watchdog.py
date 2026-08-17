"""External watchdog that monitors orchestrator heartbeat."""

from __future__ import annotations

import json
import subprocess
import sys
import time
from pathlib import Path

from scripts.browser_session.integrity import show_blocking_alert

HEARTBEAT_TIMEOUT_SEC = 15.0
POLL_INTERVAL_SEC = 2.0


def _read_heartbeat(session_dir: Path) -> dict | None:
    path = session_dir / "heartbeat.json"
    if not path.is_file():
        return None
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (json.JSONDecodeError, OSError):
        return None


def run_watchdog(session_dir: Path, *, parent_pid: int | None = None) -> int:
    """Monitor session heartbeat; alert if orchestrator dies or stalls."""
    session_dir = session_dir.resolve()
    if parent_pid is None:
        parent_pid = int(sys.argv[-1]) if len(sys.argv) > 1 else None

    last_alert = 0.0
    while True:
        if parent_pid is not None:
            try:
                import os

                os.kill(parent_pid, 0)
            except (OSError, ProcessLookupError):
                show_blocking_alert(
                    "Capture Watchdog",
                    f"Capture orchestrator (pid {parent_pid}) is no longer running.\n"
                    f"Session: {session_dir.name}",
                )
                return 1

        hb = _read_heartbeat(session_dir)
        if hb is None:
            time.sleep(POLL_INTERVAL_SEC)
            continue

        age = time.monotonic() - float(hb.get("t_mono", 0))
        if age > HEARTBEAT_TIMEOUT_SEC:
            now = time.monotonic()
            if now - last_alert > HEARTBEAT_TIMEOUT_SEC:
                show_blocking_alert(
                    "Capture Watchdog",
                    f"Capture heartbeat stale ({age:.0f}s).\n"
                    f"State: {hb.get('state', 'unknown')}\n"
                    f"Session: {session_dir.name}",
                )
                last_alert = now
        time.sleep(POLL_INTERVAL_SEC)


def spawn_watchdog(session_dir: Path, parent_pid: int) -> subprocess.Popen[bytes] | None:
    """Launch watchdog as detached child process."""
    script = Path(__file__).resolve()
    try:
        return subprocess.Popen(
            [sys.executable, str(script), str(session_dir.resolve()), str(parent_pid)],
            creationflags=subprocess.CREATE_NEW_PROCESS_GROUP if sys.platform == "win32" else 0,
        )
    except OSError:
        return None


if __name__ == "__main__":
    target = Path(sys.argv[1]) if len(sys.argv) > 1 else None
    pid = int(sys.argv[2]) if len(sys.argv) > 2 else None
    if target is None:
        print("usage: watchdog.py <session_dir> [parent_pid]", file=sys.stderr)
        raise SystemExit(2)
    raise SystemExit(run_watchdog(target, parent_pid=pid))
