"""Windows foreground window polling."""

from __future__ import annotations

import ctypes
import sys
import threading
import time
from ctypes import wintypes
from typing import Callable

from scripts.browser_session.clock import now_ms
from scripts.browser_session.crop import rects_equal, screen_to_crop
from scripts.browser_session.schema import MonitorGeometry, ScreenRect, WindowSample, append_jsonl

if sys.platform == "win32":
    user32 = ctypes.windll.user32  # type: ignore[attr-defined]
    kernel32 = ctypes.windll.kernel32  # type: ignore[attr-defined]

    try:
        ctypes.windll.shcore.SetProcessDpiAwareness(2)  # type: ignore[attr-defined]
    except Exception:  # noqa: BLE001
        try:
            user32.SetProcessDPIAware()
        except Exception:  # noqa: BLE001
            pass


def _hwnd_hex(hwnd: int) -> str:
    return hex(hwnd & 0xFFFFFFFF)


def _process_name(hwnd: int) -> str:
    if sys.platform != "win32":
        return "unknown"
    pid = wintypes.DWORD()
    user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
    PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
    handle = kernel32.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, False, pid.value)
    if not handle:
        return "unknown"
    try:
        size = wintypes.DWORD(1024)
        buffer = ctypes.create_unicode_buffer(size.value)
        if not kernel32.QueryFullProcessImageNameW(handle, 0, buffer, ctypes.byref(size)):
            return "unknown"
        path = buffer.value.replace("\\", "/")
        return path.rsplit("/", 1)[-1] or "unknown"
    finally:
        kernel32.CloseHandle(handle)


def _window_title(hwnd: int) -> str:
    if sys.platform != "win32":
        return ""
    length = user32.GetWindowTextLengthW(hwnd)
    buffer = ctypes.create_unicode_buffer(length + 1)
    user32.GetWindowTextW(hwnd, buffer, length + 1)
    return buffer.value


def _window_rect(hwnd: int) -> ScreenRect | None:
    if sys.platform != "win32":
        return None
    rect = wintypes.RECT()
    if not user32.GetWindowRect(hwnd, ctypes.byref(rect)):
        return None
    width = rect.right - rect.left
    height = rect.bottom - rect.top
    if width <= 0 or height <= 0:
        return None
    return ScreenRect(left=rect.left, top=rect.top, width=width, height=height)


def get_foreground_window_sample(
    *,
    t_ms: int,
    monitor: MonitorGeometry,
    capture_browser_hwnd: int | None,
) -> WindowSample | None:
    if sys.platform != "win32":
        return None
    hwnd = user32.GetForegroundWindow()
    if not hwnd:
        return None
    screen = _window_rect(hwnd)
    if screen is None:
        return None
    crop, visible, clamped = screen_to_crop(screen, monitor)
    return WindowSample(
        t_ms=t_ms,
        hwnd=_hwnd_hex(hwnd),
        process=_process_name(hwnd),
        title=_window_title(hwnd),
        is_capture_browser=capture_browser_hwnd is not None and hwnd == capture_browser_hwnd,
        screen=screen,
        crop=crop,
        visible_on_monitor=visible,
        clamped=clamped,
    )


def find_chrome_window_near(
    monitor: MonitorGeometry,
    *,
    tolerance: int = 40,
) -> int | None:
    """Find a top-level Chrome window whose top-left is near the monitor origin."""
    if sys.platform != "win32":
        return None

    matches: list[tuple[int, int]] = []

    @ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
    def callback(hwnd: int, _lparam: int) -> bool:
        if not user32.IsWindowVisible(hwnd):
            return True
        rect = _window_rect(hwnd)
        if rect is None:
            return True
        process = _process_name(hwnd).lower()
        if "chrome.exe" not in process:
            return True
        dx = abs(rect.left - monitor.left)
        dy = abs(rect.top - monitor.top)
        if dx <= tolerance and dy <= tolerance:
            matches.append((hwnd, dx + dy))
        return True

    user32.EnumWindows(callback, 0)
    if not matches:
        return None
    matches.sort(key=lambda item: item[1])
    return matches[0][0]


class ForegroundWindowPoller:
    """Track foreground window via event hook with periodic geometry validation."""

    EVENT_SYSTEM_FOREGROUND = 0x0003
    WINEVENT_OUTOFCONTEXT = 0x0000

    def __init__(
        self,
        *,
        session_dir,
        t0_epoch_ms: int,
        monitor: MonitorGeometry,
        capture_browser_hwnd_getter: Callable[[], int | None],
        poll_hz: float = 8.0,
        heartbeat_sec: float = 2.0,
        on_event: Callable[[dict], None] | None = None,
    ) -> None:
        self._session_dir = session_dir
        self._t0_epoch_ms = t0_epoch_ms
        self._monitor = monitor
        self._capture_browser_hwnd_getter = capture_browser_hwnd_getter
        self._poll_interval = 1.0 / poll_hz
        self._heartbeat_sec = heartbeat_sec
        self._on_event = on_event
        self._stop = threading.Event()
        self._thread: threading.Thread | None = None
        self._windows_path = session_dir / "windows.jsonl"
        self._last_sample: WindowSample | None = None
        self._last_emit_at = 0.0
        self._seq = 0
        self._current_interval_start_ms: int | None = None
        self._current_hwnd: str | None = None
        self._error: str | None = None
        self._hook = None

    @property
    def healthy(self) -> bool:
        return self._error is None and (self._thread is None or self._thread.is_alive())

    @property
    def error(self) -> str | None:
        return self._error

    def start(self) -> None:
        if self._thread is not None:
            raise RuntimeError("poller already started")
        if sys.platform == "win32":
            self._install_hook()
        self._thread = threading.Thread(target=self._loop, name="foreground-window-poller", daemon=True)
        self._thread.start()

    def _install_hook(self) -> None:
        if sys.platform != "win32":
            return

        @ctypes.WINFUNCTYPE(None, ctypes.c_void_p, wintypes.DWORD, wintypes.HWND, wintypes.LONG, wintypes.LONG, wintypes.DWORD, wintypes.DWORD)
        def callback(hook, event, hwnd, id_child, id_thread, dwms_event_time, dwms_time_ms):
            if event == self.EVENT_SYSTEM_FOREGROUND and hwnd:
                self._on_foreground_transition(int(hwnd))

        self._hook = user32.SetWinEventHook(
            self.EVENT_SYSTEM_FOREGROUND,
            self.EVENT_SYSTEM_FOREGROUND,
            None,
            callback,
            0,
            0,
            self.WINEVENT_OUTOFCONTEXT,
        )

    def _on_foreground_transition(self, hwnd: int) -> None:
        t_ms = now_ms(self._t0_epoch_ms)
        sample = get_foreground_window_sample(
            t_ms=t_ms,
            monitor=self._monitor,
            capture_browser_hwnd=self._capture_browser_hwnd_getter(),
        )
        if sample is None:
            return
        self._emit_sample(sample, event_kind="window.transition")

    def _close_interval(self, end_ms: int) -> None:
        if self._current_interval_start_ms is None or self._current_hwnd is None:
            return
        if end_ms > self._current_interval_start_ms:
            self._seq += 1
            append_jsonl(
                self._session_dir / "windows_intervals.jsonl",
                {
                    "seq": self._seq,
                    "hwnd": self._current_hwnd,
                    "start_ms": self._current_interval_start_ms,
                    "end_ms": end_ms,
                },
            )
        self._current_interval_start_ms = None
        self._current_hwnd = None

    def stop(self) -> None:
        self._stop.set()
        if sys.platform == "win32" and self._hook:
            try:
                user32.UnhookWinEvent(self._hook)
            except Exception:  # noqa: BLE001
                pass
        self._close_interval(now_ms(self._t0_epoch_ms))
        if self._thread is not None:
            self._thread.join(timeout=5.0)
            if self._thread.is_alive():
                self._error = "poller thread did not stop cleanly"

    def _emit_sample(self, sample: WindowSample, *, event_kind: str = "window.sample") -> None:
        append_jsonl(self._windows_path, sample.to_dict())
        if sample.hwnd != self._current_hwnd:
            self._close_interval(sample.t_ms)
            self._current_interval_start_ms = sample.t_ms
            self._current_hwnd = sample.hwnd
        if self._on_event is not None:
            self._on_event(
                {
                    "kind": event_kind,
                    "t_ms": sample.t_ms,
                    "hwnd": sample.hwnd,
                    "is_capture_browser": sample.is_capture_browser,
                    "title": sample.title,
                }
            )
        self._last_sample = sample
        self._last_emit_at = time.perf_counter()

    def _should_emit(self, sample: WindowSample, now: float) -> bool:
        if self._last_sample is None:
            return True
        if sample.hwnd != self._last_sample.hwnd:
            return True
        if sample.title != self._last_sample.title:
            return True
        if not rects_equal(sample.screen, self._last_sample.screen):
            return True
        return (now - self._last_emit_at) >= self._heartbeat_sec

    def _loop(self) -> None:
        while not self._stop.is_set():
            loop_start = time.perf_counter()
            t_ms = now_ms(self._t0_epoch_ms)
            sample = get_foreground_window_sample(
                t_ms=t_ms,
                monitor=self._monitor,
                capture_browser_hwnd=self._capture_browser_hwnd_getter(),
            )
            if sample is not None and self._should_emit(sample, loop_start):
                self._emit_sample(sample)
            sleep_for = self._poll_interval - (time.perf_counter() - loop_start)
            if sleep_for > 0:
                time.sleep(sleep_for)
