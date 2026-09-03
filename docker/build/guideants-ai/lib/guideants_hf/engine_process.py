"""Shared subprocess helpers for guideants-ai engine wrappers."""

from __future__ import annotations

import collections
import subprocess
import threading
from collections.abc import Callable
from typing import Any


EmitLine = Callable[[str], None]


class EngineLogPump:
    """Drain engine stdout/stderr into structured logs and a ring buffer.

    audiocpp_server (and similar engines) must not be spawned with
    stdout=DEVNULL: operators need live evidence of inference work.
    """

    def __init__(
        self,
        *,
        emit_line: EmitLine,
        max_lines: int = 200,
        max_line_chars: int = 2000,
    ) -> None:
        self._emit_line = emit_line
        self._max_lines = max(1, max_lines)
        self._max_line_chars = max(64, max_line_chars)
        self._lines: collections.deque[str] = collections.deque(maxlen=self._max_lines)
        self._lock = threading.Lock()
        self._thread: threading.Thread | None = None
        self._stop = threading.Event()
        self._process: subprocess.Popen[Any] | None = None

    def start(self, process: subprocess.Popen[Any]) -> None:
        self.stop()
        self._stop.clear()
        self._process = process
        stream = process.stdout
        if stream is None:
            return
        self._thread = threading.Thread(
            target=self._run,
            args=(stream,),
            name=f"engine-log-pump-{process.pid}",
            daemon=True,
        )
        self._thread.start()

    def stop(self, join_timeout_seconds: float = 2.0) -> None:
        self._stop.set()
        thread = self._thread
        self._thread = None
        process = self._process
        self._process = None
        if process is not None and process.stdout is not None:
            try:
                process.stdout.close()
            except Exception:
                pass
        if thread is not None and thread.is_alive():
            thread.join(timeout=join_timeout_seconds)

    def tail(self, max_chars: int = 4000) -> str:
        with self._lock:
            text = "\n".join(self._lines).strip()
        if len(text) > max_chars:
            return text[:max_chars] + "..."
        return text

    def _run(self, stream: Any) -> None:
        try:
            while not self._stop.is_set():
                line = stream.readline()
                if line == "" or line == b"":
                    break
                if isinstance(line, bytes):
                    text = line.decode("utf-8", errors="replace")
                else:
                    text = str(line)
                text = text.rstrip("\r\n")
                if not text:
                    continue
                if len(text) > self._max_line_chars:
                    text = text[: self._max_line_chars] + "..."
                with self._lock:
                    self._lines.append(text)
                try:
                    self._emit_line(text)
                except Exception:
                    pass
        except Exception:
            return


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
    engine_output: str | None = None,
) -> str:
    message = f"{engine_name} exited before readiness (exit code: {exit_code})."
    detail = (engine_output or "").strip() or read_subprocess_stderr_tail(process)
    if detail:
        message += f" Engine output: {detail}"
    return message


def spawn_engine_with_log_pump(
    command: list[str],
    *,
    emit_line: EmitLine,
    env: dict[str, str] | None = None,
    max_lines: int = 200,
) -> tuple[subprocess.Popen[Any], EngineLogPump]:
    process = subprocess.Popen(
        command,
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        env=env,
        bufsize=1,
        text=True,
        encoding="utf-8",
        errors="replace",
    )
    pump = EngineLogPump(emit_line=emit_line, max_lines=max_lines)
    pump.start(process)
    return process, pump
