"""Tests for browser session control server and client helpers."""

from __future__ import annotations

import json
import sys
import tempfile
import threading
import time
import unittest
import urllib.error
import urllib.request
from pathlib import Path
from unittest.mock import MagicMock

ROOT = Path(__file__).resolve().parents[2]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from scripts.browser_session.control_server import (  # noqa: E402
    CommandQueue,
    SessionControlServer,
    control_descriptor_path,
    find_active_control_descriptor,
    load_control_descriptor,
    remove_control_descriptor,
    write_control_descriptor,
)


class ControlDescriptorTests(unittest.TestCase):
    def test_write_and_remove_descriptor(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            session_dir = Path(tmp) / "part-0001"
            session_dir.mkdir()
            path = write_control_descriptor(
                session_dir=session_dir,
                host="127.0.0.1",
                port=12345,
                token="secret",
            )
            self.assertTrue(path.is_file())
            payload = load_control_descriptor(path)
            self.assertEqual(payload["port"], 12345)
            self.assertEqual(payload["token"], "secret")
            remove_control_descriptor(session_dir)
            self.assertFalse(control_descriptor_path(session_dir).is_file())

    def test_find_active_descriptor(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            older = root / "old" / "part-0001"
            newer = root / "new" / "part-0001"
            older.mkdir(parents=True)
            newer.mkdir(parents=True)
            write_control_descriptor(session_dir=older, host="127.0.0.1", port=1111, token="a")
            time.sleep(0.01)
            write_control_descriptor(session_dir=newer, host="127.0.0.1", port=2222, token="b")
            found = find_active_control_descriptor(root)
            self.assertIsNotNone(found)
            assert found is not None
            self.assertEqual(load_control_descriptor(found)["port"], 2222)


class CommandQueueTests(unittest.TestCase):
    def test_drain_executes_on_main_thread(self) -> None:
        queue = CommandQueue()
        results: list[str] = []

        def worker() -> None:
            command = queue.submit("echo", {"value": "hello"})
            self.assertIsNone(command.error)
            self.assertEqual(command.result, {"value": "hello"})

        thread = threading.Thread(target=worker)
        thread.start()

        def handler(action: str, params: dict) -> dict:
            results.append(action)
            return {"value": params["value"]}

        deadline = time.time() + 2.0
        while thread.is_alive() and time.time() < deadline:
            queue.drain(handler)
            time.sleep(0.01)
        thread.join(timeout=1.0)
        self.assertFalse(thread.is_alive())
        self.assertEqual(results, ["echo"])


class SessionControlServerTests(unittest.TestCase):
    def test_rejects_invalid_token(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            session_dir = Path(tmp) / "part-0001"
            session_dir.mkdir()
            queue = CommandQueue()
            server = SessionControlServer(token="good-token", queue=queue, session_dir=session_dir)
            server.start()
            try:
                payload = json.dumps({"token": "bad", "action": "status", "params": {}}).encode("utf-8")
                request = urllib.request.Request(
                    f"http://127.0.0.1:{server.port}/command",
                    data=payload,
                    headers={"Content-Type": "application/json"},
                    method="POST",
                )
                with self.assertRaises(urllib.error.HTTPError) as ctx:
                    urllib.request.urlopen(request, timeout=5)
                self.assertEqual(ctx.exception.code, 403)
            finally:
                server.stop()

    def test_executes_status_command(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            session_dir = Path(tmp) / "part-0001"
            session_dir.mkdir()
            queue = CommandQueue()
            server = SessionControlServer(token="good-token", queue=queue, session_dir=session_dir)

            def handler(action: str, params: dict) -> dict:
                if action == "status":
                    return {"session_dir": str(session_dir), "tabs": []}
                raise ValueError(f"unexpected action: {action}")

            server.start()
            stop = threading.Event()

            def drain_loop() -> None:
                while not stop.is_set():
                    queue.drain(handler)
                    time.sleep(0.01)

            thread = threading.Thread(target=drain_loop, daemon=True)
            thread.start()
            try:
                payload = json.dumps({"token": "good-token", "action": "status", "params": {}}).encode("utf-8")
                request = urllib.request.Request(
                    f"http://127.0.0.1:{server.port}/command",
                    data=payload,
                    headers={"Content-Type": "application/json"},
                    method="POST",
                )
                with urllib.request.urlopen(request, timeout=5) as response:
                    data = json.loads(response.read().decode("utf-8"))
                self.assertTrue(data["ok"])
                self.assertEqual(data["result"]["session_dir"], str(session_dir.resolve()))
            finally:
                stop.set()
                thread.join(timeout=1.0)
                server.stop()
                self.assertFalse(control_descriptor_path(session_dir).is_file())


class CaptureBrowserExecutorTests(unittest.TestCase):
    def test_goto_requires_http_url(self) -> None:
        from scripts.browser_session.control_server import CaptureBrowserExecutor

        context = MagicMock()
        observer = MagicMock()
        executor = CaptureBrowserExecutor(context=context, observer=observer, session_dir=Path("."))
        with self.assertRaises(ValueError):
            executor.execute("goto", {"url": "ftp://example.com"})


if __name__ == "__main__":
    unittest.main()
