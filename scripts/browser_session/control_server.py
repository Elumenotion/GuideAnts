"""Loopback control server for the live capture Playwright context."""

from __future__ import annotations

import json
import secrets
import socket
import threading
import time
from dataclasses import dataclass, field
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from typing import Any, Callable
from urllib.parse import urlparse

from playwright.sync_api import BrowserContext, Page

from scripts.browser_session.browser_observer import BrowserSessionObserver
from scripts.browser_session.schema import write_json_atomic

CONTROL_FILENAME = "control.json"


def control_descriptor_path(session_dir: Path) -> Path:
    return session_dir / CONTROL_FILENAME


def find_active_control_descriptor(sessions_root: Path) -> Path | None:
    """Return the newest control.json under ``sessions_root`` if any."""
    candidates: list[tuple[float, Path]] = []
    if not sessions_root.is_dir():
        return None
    for path in sessions_root.rglob(CONTROL_FILENAME):
        try:
            candidates.append((path.stat().st_mtime, path))
        except OSError:
            continue
    if not candidates:
        return None
    candidates.sort(key=lambda item: item[0], reverse=True)
    return candidates[0][1]


def load_control_descriptor(path: Path) -> dict[str, Any]:
    return json.loads(path.read_text(encoding="utf-8"))


def write_control_descriptor(
    *,
    session_dir: Path,
    host: str,
    port: int,
    token: str,
    chain_dir: Path | None = None,
) -> Path:
    payload = {
        "host": host,
        "port": port,
        "token": token,
        "session_dir": str(session_dir.resolve()),
        "chain_dir": str(chain_dir.resolve()) if chain_dir is not None else None,
        "started_at_epoch_ms": int(time.time() * 1000),
    }
    path = control_descriptor_path(session_dir)
    write_json_atomic(path, payload)
    return path


def remove_control_descriptor(session_dir: Path) -> None:
    path = control_descriptor_path(session_dir)
    if path.is_file():
        path.unlink(missing_ok=True)


def _pick_loopback_port() -> int:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as sock:
        sock.bind(("127.0.0.1", 0))
        return int(sock.getsockname()[1])


@dataclass
class PendingCommand:
    action: str
    params: dict[str, Any]
    done: threading.Event = field(default_factory=threading.Event)
    result: dict[str, Any] | None = None
    error: str | None = None
    status_code: int = 200


class CommandQueue:
    """Thread-safe queue consumed on the capture main thread."""

    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._pending: list[PendingCommand] = []
        self._wake = threading.Event()

    def submit(self, action: str, params: dict[str, Any], *, timeout: float = 60.0) -> PendingCommand:
        command = PendingCommand(action=action, params=params)
        with self._lock:
            self._pending.append(command)
        self._wake.set()
        if not command.done.wait(timeout):
            with self._lock:
                try:
                    self._pending.remove(command)
                except ValueError:
                    pass
            command.error = f"command timed out after {timeout:.1f}s"
            command.status_code = 504
            command.done.set()
        return command

    def wait_for_work(self, stop_event: threading.Event, timeout: float = 0.05) -> None:
        if self._has_pending():
            return
        self._wake.clear()
        if stop_event.wait(timeout):
            return
        self._wake.wait(timeout)

    def _has_pending(self) -> bool:
        with self._lock:
            return bool(self._pending)

    def drain(self, handler: Callable[[str, dict[str, Any]], dict[str, Any]]) -> int:
        with self._lock:
            batch = self._pending[:]
            self._pending.clear()
        for command in batch:
            try:
                command.result = handler(command.action, command.params)
            except ValueError as exc:
                command.error = str(exc)
                command.status_code = 400
            except LookupError as exc:
                command.error = str(exc)
                command.status_code = 404
            except PermissionError as exc:
                command.error = str(exc)
                command.status_code = 403
            except Exception as exc:  # noqa: BLE001
                command.error = str(exc)
                command.status_code = 500
            finally:
                command.done.set()
        return len(batch)


class CaptureBrowserExecutor:
    """Execute browser commands on the Playwright context (main thread only)."""

    def __init__(
        self,
        *,
        context: BrowserContext,
        observer: BrowserSessionObserver,
        session_dir: Path,
        stop_event: threading.Event | None = None,
        navigation_timeout_ms: int = 15000,
        action_timeout_ms: int = 5000,
    ) -> None:
        self._context = context
        self._observer = observer
        self._session_dir = session_dir
        self._stop_event = stop_event
        self._navigation_timeout_ms = navigation_timeout_ms
        self._action_timeout_ms = action_timeout_ms
        self._context.set_default_timeout(action_timeout_ms)
        self._context.set_default_navigation_timeout(navigation_timeout_ms)

    def execute(self, action: str, params: dict[str, Any]) -> dict[str, Any]:
        if action == "stop":
            if self._stop_event is not None:
                self._stop_event.set()
            return {"stopping": True}
        if action == "status":
            return self._status()
        if action == "goto":
            return self._goto(str(params.get("url", "")), params.get("tab"))
        if action == "tab-list":
            return {"tabs": self._tab_rows()}
        if action == "tab-new":
            return self._tab_new(params.get("url"))
        if action == "tab-select":
            return self._tab_select(int(params["tab"]))
        if action == "tab-close":
            tab = params.get("tab")
            return self._tab_close(int(tab) if tab is not None else None)
        if action == "snapshot":
            return self._snapshot(
                tab=params.get("tab"),
                screenshot=bool(params.get("screenshot", False)),
            )
        if action == "click":
            return self._click(str(params["selector"]), params.get("tab"))
        if action == "fill":
            return self._fill(str(params["selector"]), str(params.get("value", "")), params.get("tab"))
        if action == "press":
            return self._press(str(params["key"]), params.get("tab"))
        if action == "checkpoint":
            self._observer.request_manual_checkpoint()
            page = self._page_for_tab(params.get("tab"))
            if page is None:
                raise LookupError("no active tab")
            return self._page_info(page)
        raise ValueError(f"unsupported action: {action}")

    def _pages(self) -> list[Page]:
        return [page for page in self._context.pages if not page.is_closed()]

    def _page_for_tab(self, tab: Any | None) -> Page | None:
        pages = self._pages()
        if not pages:
            return None
        if tab is None:
            focused = self._focused_page()
            return focused or pages[0]
        index = int(tab)
        if index < 0 or index >= len(pages):
            raise LookupError(f"tab index out of range: {index}")
        return pages[index]

    def _focused_page(self) -> Page | None:
        for page in self._pages():
            tab_id = self._observer.tab_id_for_page(page)
            if tab_id and self._observer.is_tab_focused(tab_id):
                return page
        return None

    def _page_title(self, page: Page) -> str:
        tab_id = self._observer.tab_id_for_page(page)
        if tab_id:
            cached = self._observer.title_for_tab(tab_id)
            if cached:
                return cached
        try:
            return page.url
        except Exception:  # noqa: BLE001
            return ""

    def _tab_rows(self) -> list[dict[str, Any]]:
        rows: list[dict[str, Any]] = []
        focused = self._focused_page()
        for index, page in enumerate(self._pages()):
            tab_id = self._observer.tab_id_for_page(page)
            rows.append(
                {
                    "index": index,
                    "tab_id": tab_id,
                    "url": page.url,
                    "title": self._page_title(page),
                    "focused": focused is not None and page is focused,
                }
            )
        return rows

    def _page_info(self, page: Page) -> dict[str, Any]:
        tab_id = self._observer.tab_id_for_page(page)
        return {
            "tab_id": tab_id,
            "url": page.url,
            "title": self._page_title(page),
        }

    def _focus_page(self, page: Page) -> None:
        self._observer.focus_page(page)

    def _goto(self, url: str, tab: Any | None) -> dict[str, Any]:
        url = url.strip()
        if not url:
            raise ValueError("url is required")
        parsed = urlparse(url)
        if parsed.scheme not in ("http", "https"):
            raise ValueError("url must use http or https")
        page = self._page_for_tab(tab)
        if page is None:
            raise LookupError("no active tab")
        self._focus_page(page)
        page.goto(url, wait_until="domcontentloaded", timeout=self._navigation_timeout_ms)
        return self._page_info(page)

    def _tab_new(self, url: str | None) -> dict[str, Any]:
        page = self._context.new_page()
        if url:
            parsed = urlparse(url)
            if parsed.scheme not in ("http", "https"):
                raise ValueError("url must use http or https")
            page.goto(url, wait_until="domcontentloaded")
        self._focus_page(page)
        rows = self._tab_rows()
        return {"tabs": rows, "active": self._page_info(page)}

    def _tab_select(self, index: int) -> dict[str, Any]:
        page = self._page_for_tab(index)
        if page is None:
            raise LookupError("no active tab")
        self._focus_page(page)
        return self._page_info(page)

    def _tab_close(self, index: int | None) -> dict[str, Any]:
        page = self._page_for_tab(index)
        if page is None:
            raise LookupError("no active tab")
        page.close()
        remaining = self._tab_rows()
        return {"tabs": remaining}

    def _snapshot(self, *, tab: Any | None, screenshot: bool) -> dict[str, Any]:
        page = self._page_for_tab(tab)
        if page is None:
            raise LookupError("no active tab")
        info = self._page_info(page)
        if screenshot:
            lookup_dir = self._session_dir / "lookup"
            lookup_dir.mkdir(parents=True, exist_ok=True)
            stamp = int(time.time() * 1000)
            path = lookup_dir / f"snapshot-{stamp}.png"
            page.screenshot(path=str(path), type="png", full_page=False)
            info["screenshot"] = str(path.resolve())
        return info

    def _click(self, selector: str, tab: Any | None) -> dict[str, Any]:
        page = self._page_for_tab(tab)
        if page is None:
            raise LookupError("no active tab")
        self._focus_page(page)
        page.click(selector)
        return self._page_info(page)

    def _fill(self, selector: str, value: str, tab: Any | None) -> dict[str, Any]:
        page = self._page_for_tab(tab)
        if page is None:
            raise LookupError("no active tab")
        self._focus_page(page)
        page.fill(selector, value)
        return self._page_info(page)

    def _press(self, key: str, tab: Any | None) -> dict[str, Any]:
        page = self._page_for_tab(tab)
        if page is None:
            raise LookupError("no active tab")
        self._focus_page(page)
        page.keyboard.press(key)
        return self._page_info(page)

    def _status(self) -> dict[str, Any]:
        tabs = self._tab_rows()
        active = next((row for row in tabs if row.get("focused")), tabs[0] if tabs else None)
        return {
            "session_dir": str(self._session_dir.resolve()),
            "tabs": tabs,
            "active": active,
        }


class SessionControlServer:
    """Authenticated localhost HTTP server for capture-browser commands."""

    def __init__(
        self,
        *,
        token: str,
        queue: CommandQueue,
        session_dir: Path,
        chain_dir: Path | None = None,
        integrity_snapshot: Callable[[], dict[str, Any]] | None = None,
    ) -> None:
        self.token = token
        self.queue = queue
        self.session_dir = session_dir
        self.chain_dir = chain_dir
        self.integrity_snapshot = integrity_snapshot
        self.host = "127.0.0.1"
        self.port = _pick_loopback_port()
        self._httpd: ThreadingHTTPServer | None = None
        self._thread: threading.Thread | None = None

    def start(self) -> Path:
        server = self
        queue = self.queue

        class Handler(BaseHTTPRequestHandler):
            def log_message(self, format: str, *args: Any) -> None:  # noqa: A003
                return

            def _read_json(self) -> dict[str, Any]:
                length = int(self.headers.get("Content-Length", "0"))
                if length <= 0:
                    return {}
                raw = self.rfile.read(length)
                if not raw:
                    return {}
                payload = json.loads(raw.decode("utf-8"))
                if not isinstance(payload, dict):
                    raise ValueError("request body must be a JSON object")
                return payload

            def _send(self, status: int, payload: dict[str, Any]) -> None:
                body = json.dumps(payload).encode("utf-8")
                self.send_response(status)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)

            def do_GET(self) -> None:  # noqa: N802
                if self.path != "/health":
                    self._send(404, {"ok": False, "error": "not found"})
                    return
                health: dict[str, Any] = {
                    "ok": True,
                    "session_dir": str(server.session_dir.resolve()),
                    "chain_dir": str(server.chain_dir.resolve()) if server.chain_dir else None,
                }
                if server.integrity_snapshot is not None:
                    try:
                        health["integrity"] = server.integrity_snapshot()
                    except Exception as exc:  # noqa: BLE001
                        health["integrity_error"] = str(exc)
                self._send(200, health)

            def do_POST(self) -> None:  # noqa: N802
                if self.path != "/command":
                    self._send(404, {"ok": False, "error": "not found"})
                    return
                try:
                    payload = self._read_json()
                except json.JSONDecodeError:
                    self._send(400, {"ok": False, "error": "invalid JSON"})
                    return
                except ValueError as exc:
                    self._send(400, {"ok": False, "error": str(exc)})
                    return

                token = str(payload.get("token", ""))
                if not token or not secrets.compare_digest(token, server.token):
                    self._send(403, {"ok": False, "error": "invalid token"})
                    return

                action = str(payload.get("action", "")).strip()
                if not action:
                    self._send(400, {"ok": False, "error": "action is required"})
                    return

                params = payload.get("params") or {}
                if not isinstance(params, dict):
                    self._send(400, {"ok": False, "error": "params must be an object"})
                    return

                command = queue.submit(action, params)
                if command.error:
                    self._send(command.status_code, {"ok": False, "error": command.error})
                    return
                self._send(200, {"ok": True, "result": command.result})

        self._httpd = ThreadingHTTPServer((self.host, self.port), Handler)
        self._thread = threading.Thread(
            target=self._httpd.serve_forever,
            name="browser-session-control",
            daemon=True,
        )
        self._thread.start()
        return write_control_descriptor(
            session_dir=self.session_dir,
            host=self.host,
            port=self.port,
            token=self.token,
            chain_dir=self.chain_dir,
        )

    def stop(self) -> None:
        if self._httpd is not None:
            self._httpd.shutdown()
            self._httpd.server_close()
            self._httpd = None
        if self._thread is not None:
            self._thread.join(timeout=2.0)
            self._thread = None
        remove_control_descriptor(self.session_dir)
