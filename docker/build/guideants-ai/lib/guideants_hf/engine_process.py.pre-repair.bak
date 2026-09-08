import subprocess
from typing import Any


def read_subprocess_stderr_tail(
    process: subprocess.Popen[Any] | None,
    max_chars: int = 4000,
) -> str:
    if process is None:
        return ""
    stderr = process.stderr
    if stderr is None:
        return ""
    try:
        raw = stderr.read(max_chars + 1)
        if not raw:
            return ""
        if isinstance(raw, bytes):
            text = raw.decode("utf-8", errors="replace")
        else:
            text = str(raw)
        text = text.strip()
        if len(text) > max_chars:
            return text[:max_chars] + "..."
        return text
    except Exception:
        return ""


def format_engine_exit_error(
    process: subprocess.Popen[Any] | None,
    exit_code: int | None,
    *,
    engine_name: str = "audiocpp_server",
) -> str:
    message = f"{engine_name} exited before readiness (exit code: {exit_code})."
    detail = read_subprocess_stderr_tail(process)
    if detail:
        message += f" Engine output: {detail}"
    return message
