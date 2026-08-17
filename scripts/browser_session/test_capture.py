"""Tests for capture input and observer thread ownership."""

from __future__ import annotations

import sys
import threading
import unittest
from types import SimpleNamespace
from unittest.mock import MagicMock, patch

from scripts.browser_session import capture
from scripts.browser_session.browser_observer import BrowserSessionObserver


class FakeMsvcrt:
    def __init__(self, keys: list[str]) -> None:
        self._keys = list(keys)

    def kbhit(self) -> bool:
        return bool(self._keys)

    def getwch(self) -> str:
        return self._keys.pop(0)


class KeyboardListenerTests(unittest.TestCase):
    def _run_windows_listener(self, keys: list[str]) -> tuple[threading.Event, capture.ManualCheckpointTrigger]:
        stop_event = threading.Event()
        manual = capture.ManualCheckpointTrigger()
        fake_stdin = SimpleNamespace(isatty=lambda: True)
        fake_msvcrt = FakeMsvcrt(keys)
        with (
            patch.object(capture.sys, "platform", "win32"),
            patch.object(capture.sys, "stdin", fake_stdin),
            patch.dict(sys.modules, {"msvcrt": fake_msvcrt}),
        ):
            capture._keyboard_listener(stop_event, manual)
        return stop_event, manual

    def test_q_stops_capture(self) -> None:
        stop_event, _ = self._run_windows_listener(["q"])
        self.assertTrue(stop_event.is_set())

    def test_enter_stops_capture(self) -> None:
        stop_event, _ = self._run_windows_listener(["\r"])
        self.assertTrue(stop_event.is_set())

    def test_c_requests_manual_checkpoint_without_stopping(self) -> None:
        stop_event, manual = self._run_windows_listener(["c", "q"])
        self.assertTrue(stop_event.is_set())
        self.assertTrue(manual.consume())


class ObserverThreadOwnershipTests(unittest.TestCase):
    def test_manual_checkpoint_is_queued_on_capture_thread(self) -> None:
        context = MagicMock()
        context.pages = []
        store = MagicMock()
        callback_thread_ids: list[int] = []

        def consume_manual_request() -> bool:
            callback_thread_ids.append(threading.get_ident())
            return True

        observer = BrowserSessionObserver(
            context=context,
            store=store,
            t0_epoch_ms=0,
            manual_trigger=consume_manual_request,
        )
        try:
            with observer._lock:
                observer._focused_tab_id = "tab-1"
            observer.drain_checkpoints(max_count=0)
            self.assertEqual(callback_thread_ids, [threading.get_ident()])
            with observer._schedule_lock:
                self.assertEqual(len(observer._scheduled_checkpoints), 1)
                self.assertEqual(observer._scheduled_checkpoints[0].trigger, "manual")
        finally:
            observer.shutdown()

        self.assertFalse(any(thread.name == "manual-checkpoint" for thread in threading.enumerate()))


if __name__ == "__main__":
    unittest.main()
