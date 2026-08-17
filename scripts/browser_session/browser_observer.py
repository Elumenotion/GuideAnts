"""Playwright browser observer for live session capture."""

from __future__ import annotations

import json
import threading
import time
from dataclasses import dataclass
from typing import Any, Callable

from playwright.sync_api import BrowserContext, Page, Playwright

from scripts.browser_session.checkpoints import CheckpointStore
from scripts.browser_session.clock import now_ms

SPA_INIT_SCRIPT = """
(() => {
  if (window.__sessionCaptureInstalled) return;
  window.__sessionCaptureInstalled = true;

  const notify = (trigger, extra = {}) => {
    if (typeof window.__sessionCaptureEvent === 'function') {
      window.__sessionCaptureEvent({ trigger, tab_id: window.__sessionCaptureTabId || '', ...extra });
    }
  };

  const origPush = history.pushState;
  const origReplace = history.replaceState;
  history.pushState = function (...args) {
    const result = origPush.apply(this, args);
    notify('spa.pushState', { url: location.href });
    return result;
  };
  history.replaceState = function (...args) {
    const result = origReplace.apply(this, args);
    notify('spa.replaceState', { url: location.href });
    return result;
  };
  window.addEventListener('hashchange', () => notify('spa.hashchange', { url: location.href }));
  document.addEventListener('visibilitychange', () => {
    notify('tab.visibility', { visible: document.visibilityState === 'visible', url: location.href });
  });

  let pending = 0;
  let timer;
  let mutationObserver;
  window.__sessionCaptureShutdown = () => {
    if (mutationObserver) mutationObserver.disconnect();
    if (timer) clearTimeout(timer);
    pending = 0;
    window.__sessionCaptureEvent = () => {};
  };
  mutationObserver = new MutationObserver(() => {
    pending += 1;
    if (timer) clearTimeout(timer);
    timer = setTimeout(() => {
      const count = pending;
      pending = 0;
      if (count > 0) notify('dom.mutation', { count });
    }, 500);
  });
  mutationObserver.observe(document.documentElement, {
    childList: true,
    subtree: true,
    attributes: true,
    characterData: true,
  });
})();
"""


@dataclass
class _ScheduledCheckpoint:
    tab_id: str
    trigger: str
    force: bool = False
    include_mhtml: bool = False


class BrowserSessionObserver:
    """Attach observers to every tab in a Playwright context."""

    def __init__(
        self,
        *,
        context: BrowserContext,
        store: CheckpointStore,
        t0_epoch_ms: int,
        capture_mhtml: bool = True,
        debounce_ms: int = 1500,
        max_text_chars: int = 500_000,
        manual_trigger: Callable[[], bool] | None = None,
    ) -> None:
        self._context = context
        self._store = store
        self._t0_epoch_ms = t0_epoch_ms
        self._capture_mhtml = capture_mhtml
        self._debounce_ms = debounce_ms
        self._max_text_chars = max_text_chars
        self._manual_trigger = manual_trigger
        self._lock = threading.Lock()
        self._tab_counter = 0
        self._page_to_tab: dict[int, str] = {}
        self._focused_tab_id: str | None = None
        self._last_checkpoint_at: dict[str, float] = {}
        self._last_activity_at: dict[str, float] = {}
        self._pending_mutations: dict[str, int] = {}
        self._scheduled_checkpoints: list[_ScheduledCheckpoint] = []
        self._schedule_lock = threading.Lock()
        self._last_titles: dict[str, str] = {}
        self._stop = threading.Event()
        self._errors: list[str] = []
        self._last_heartbeat_mono = time.monotonic()
        self._checkpoint_backlog = 0

        context.expose_function("__sessionCaptureEvent", self._on_page_event)
        context.add_init_script(SPA_INIT_SCRIPT)
        context.on("page", self._on_page)

        for page in context.pages:
            self._on_page(page)

    def health(self) -> dict[str, Any]:
        return {
            "healthy": not self._errors and not self._stop.is_set(),
            "errors": list(self._errors),
            "checkpoint_backlog": self._checkpoint_backlog,
            "last_heartbeat_mono": self._last_heartbeat_mono,
        }

    def write_initial_checkpoints(self) -> int:
        """Write a real checkpoint for every open tab before recording starts."""
        written = 0
        for page in list(self._context.pages):
            if page.is_closed():
                continue
            tab_id = self._tab_id_for_page(page)
            if tab_id is None:
                continue
            self._maybe_checkpoint(page, tab_id, "initial", force=True, include_mhtml=True)
            written += 1
        if written == 0:
            self._errors.append("no initial browser checkpoints written")
        return written

    def stop(self) -> None:
        self.shutdown()

    def shutdown(self) -> None:
        """Stop callbacks and background work before Playwright teardown."""
        self._stop.set()
        with self._schedule_lock:
            self._scheduled_checkpoints.clear()
        self._silence_page_callbacks()

    def _silence_page_callbacks(self) -> None:
        for page in list(self._context.pages):
            try:
                if not page.is_closed():
                    page.evaluate("() => window.__sessionCaptureShutdown && window.__sessionCaptureShutdown()")
            except Exception:  # noqa: BLE001
                pass

    def set_t0_epoch_ms(self, t0_epoch_ms: int) -> None:
        self._t0_epoch_ms = t0_epoch_ms

    def rotate_store(self, store: CheckpointStore) -> None:
        self._store = store
        self._last_checkpoint_at.clear()
        self._last_activity_at.clear()
        self._pending_mutations.clear()

    def _t_ms(self) -> int:
        return now_ms(self._t0_epoch_ms)

    def _new_tab_id(self) -> str:
        with self._lock:
            self._tab_counter += 1
            return f"tab-{self._tab_counter}"

    def _on_page(self, page: Page) -> None:
        tab_id = self._new_tab_id()
        with self._lock:
            self._page_to_tab[id(page)] = tab_id

        url = ""
        title = ""
        try:
            url = page.url
            title = page.title()
        except Exception:  # noqa: BLE001
            pass

        self._store.register_tab(tab_id, t_ms=self._t_ms(), url=url, title=title)

        try:
            page.evaluate(f"() => {{ window.__sessionCaptureTabId = {json.dumps(tab_id)}; }}")
        except Exception:  # noqa: BLE001
            pass

        page.on("close", lambda: self._on_page_close(page, tab_id))
        page.on("framenavigated", lambda frame: self._on_navigate(page, tab_id, frame))
        page.on(
            "domcontentloaded",
            lambda: self._schedule_checkpoint(tab_id, "domcontentloaded", force=True),
        )

        if len(self._page_to_tab) == 1:
            self._set_focus(tab_id)

    def _on_page_close(self, page: Page, tab_id: str) -> None:
        self._store.close_tab(tab_id, t_ms=self._t_ms())
        with self._lock:
            self._page_to_tab.pop(id(page), None)
            if self._focused_tab_id == tab_id:
                self._focused_tab_id = None

    def _set_focus(self, tab_id: str) -> None:
        with self._lock:
            if self._focused_tab_id == tab_id:
                return
            self._focused_tab_id = tab_id
        self._store.focus_tab(tab_id, t_ms=self._t_ms())

    def _tab_id_for_page(self, page: Page) -> str | None:
        with self._lock:
            return self._page_to_tab.get(id(page))

    def _is_focused(self, tab_id: str) -> bool:
        with self._lock:
            return self._focused_tab_id == tab_id

    def _on_navigate(self, page: Page, tab_id: str, frame) -> None:
        if frame != page.main_frame:
            return
        self._schedule_checkpoint(tab_id, "navigate", force=True, include_mhtml=True)

    def _on_page_event(self, payload: dict[str, Any]) -> None:
        if self._stop.is_set():
            return
        self._last_heartbeat_mono = time.monotonic()
        trigger = str(payload.get("trigger", "unknown"))
        tab_id = str(payload.get("tab_id", ""))
        if not tab_id:
            return
        page = self._page_for_tab(tab_id)
        if page is None:
            return

        if trigger == "tab.visibility":
            if payload.get("visible"):
                self._set_focus(tab_id)
                self._schedule_checkpoint(tab_id, "tab.focus", force=True, include_mhtml=True)
            else:
                self._schedule_checkpoint(tab_id, "tab.blur", force=True)
            return

        include_mhtml = trigger.startswith("spa.")
        if trigger == "dom.mutation":
            self._record_dom_activity(tab_id, int(payload.get("count", 1)))
            return
        self._schedule_checkpoint(tab_id, trigger, include_mhtml=include_mhtml)

    def _record_dom_activity(self, tab_id: str, count: int) -> None:
        if self._stop.is_set():
            return
        now = time.monotonic()
        self._pending_mutations[tab_id] = self._pending_mutations.get(tab_id, 0) + count
        last = self._last_activity_at.get(tab_id, 0.0)
        if (now - last) * 1000 < 500:
            return
        self._last_activity_at[tab_id] = now
        self._store.emit_event(
            {
                "kind": "view.activity",
                "source": "dom",
                "t_ms": self._t_ms(),
                "tab_id": tab_id,
                "count": self._pending_mutations.get(tab_id, count),
            }
        )
        self._pending_mutations[tab_id] = 0
        last_checkpoint = self._last_checkpoint_at.get(tab_id, 0.0)
        if (now - last_checkpoint) * 1000 >= self._debounce_ms:
            self._schedule_checkpoint(tab_id, "dom.quiescence", force=True)

    def _schedule_checkpoint(
        self,
        tab_id: str,
        trigger: str,
        *,
        force: bool = False,
        include_mhtml: bool = False,
    ) -> None:
        if self._stop.is_set():
            return
        if not force and self._should_debounce(tab_id):
            return
        item = _ScheduledCheckpoint(
            tab_id=tab_id,
            trigger=trigger,
            force=force,
            include_mhtml=include_mhtml,
        )
        with self._schedule_lock:
            key = (tab_id, trigger)
            self._scheduled_checkpoints = [
                scheduled for scheduled in self._scheduled_checkpoints if (scheduled.tab_id, scheduled.trigger) != key
            ]
            self._scheduled_checkpoints.append(item)

    def drain_checkpoints(
        self,
        *,
        max_count: int = 1,
        stop_event: threading.Event | None = None,
    ) -> int:
        """Run queued checkpoint work on the capture main thread."""
        if stop_event is not None and stop_event.is_set():
            return 0
        self._queue_manual_checkpoint()
        processed = 0
        while processed < max_count:
            if stop_event is not None and stop_event.is_set():
                break
            item: _ScheduledCheckpoint | None = None
            with self._schedule_lock:
                self._checkpoint_backlog = len(self._scheduled_checkpoints)
                if self._scheduled_checkpoints:
                    item = self._scheduled_checkpoints.pop(0)
            if item is None:
                break
            page = self._page_for_tab(item.tab_id)
            if page is None or page.is_closed():
                continue
            self._maybe_checkpoint(
                page,
                item.tab_id,
                item.trigger,
                force=item.force,
                include_mhtml=item.include_mhtml,
            )
            processed += 1
        return processed

    def _queue_manual_checkpoint(self) -> None:
        if self._manual_trigger is None or not self._manual_trigger():
            return
        with self._lock:
            focused = self._focused_tab_id
        if focused is not None:
            self._schedule_checkpoint(focused, "manual", force=True, include_mhtml=True)

    def title_for_tab(self, tab_id: str) -> str:
        with self._lock:
            return self._last_titles.get(tab_id, "")

    def _page_for_tab(self, tab_id: str) -> Page | None:
        for page in self._context.pages:
            if self._tab_id_for_page(page) == tab_id:
                return page
        return None

    def _find_page_by_url(self, url: str) -> Page | None:
        if not url:
            return None
        for page in self._context.pages:
            try:
                if page.url == url:
                    return page
            except Exception:  # noqa: BLE001
                continue
        return None

    def _should_debounce(self, tab_id: str) -> bool:
        now = time.monotonic()
        last = self._last_checkpoint_at.get(tab_id, 0.0)
        if (now - last) * 1000 < self._debounce_ms:
            return True
        return False

    def _maybe_checkpoint(
        self,
        page: Page,
        tab_id: str,
        trigger: str,
        *,
        force: bool = False,
        include_mhtml: bool = False,
    ) -> None:
        if self._stop.is_set():
            return
        if page.is_closed():
            return
        if not force and self._should_debounce(tab_id):
            return

        foreground = self._is_focused(tab_id)
        try:
            url = page.url
            title = page.title()
            scroll = page.evaluate(
                """() => ({
                    x: window.scrollX || 0,
                    y: window.scrollY || 0,
                    selection: window.getSelection ? String(window.getSelection()) : '',
                    text: document.body ? document.body.innerText : ''
                })"""
            )
        except Exception as exc:  # noqa: BLE001
            self._errors.append(f"checkpoint read failed for {tab_id}: {exc}")
            return

        text = str(scroll.get("text", ""))[: self._max_text_chars]
        selection = str(scroll.get("selection", ""))
        screenshot: bytes | None = None
        if foreground:
            try:
                screenshot = page.screenshot(type="png", full_page=False)
            except Exception as exc:  # noqa: BLE001
                self._errors.append(f"screenshot failed for {tab_id}: {exc}")
                screenshot = None

        mhtml: str | None = None
        if include_mhtml and self._capture_mhtml:
            mhtml = self._capture_mhtml_snapshot(page)

        with self._lock:
            self._last_titles[tab_id] = title

        self._store.write_checkpoint(
            t_ms=self._t_ms(),
            tab_id=tab_id,
            foreground=foreground,
            trigger=trigger,
            url=url,
            title=title,
            scroll_x=int(scroll.get("x", 0)),
            scroll_y=int(scroll.get("y", 0)),
            selection=selection,
            screenshot=screenshot,
            text=text,
            mhtml=mhtml,
        )
        self._last_checkpoint_at[tab_id] = time.monotonic()

    def _capture_mhtml_snapshot(self, page: Page) -> str | None:
        try:
            cdp = self._context.new_cdp_session(page)
            result = cdp.send("Page.captureSnapshot", {"format": "mhtml"})
            data = result.get("data")
            return str(data) if data else None
        except Exception as exc:  # noqa: BLE001
            self._errors.append(f"MHTML capture failed: {exc}")
            return None

    def request_manual_checkpoint(self) -> None:
        with self._lock:
            focused = self._focused_tab_id
        if focused is None:
            return
        self._schedule_checkpoint(focused, "manual", force=True, include_mhtml=True)

    def tab_id_for_page(self, page: Page) -> str | None:
        return self._tab_id_for_page(page)

    def is_tab_focused(self, tab_id: str) -> bool:
        return self._is_focused(tab_id)

    def focus_page(self, page: Page) -> None:
        page.bring_to_front()
        tab_id = self._tab_id_for_page(page)
        if tab_id:
            self._set_focus(tab_id)


def launch_capture_context(
    playwright: Playwright,
    *,
    profile_dir,
    monitor_left: int,
    monitor_top: int,
    monitor_width: int,
    monitor_height: int,
    urls: list[str],
):
    context = playwright.chromium.launch_persistent_context(
        user_data_dir=str(profile_dir),
        channel="chrome",
        headless=False,
        no_viewport=False,
        viewport={"width": monitor_width, "height": max(400, monitor_height - 80)},
        ignore_default_args=[
            "--enable-automation",
            "--no-sandbox",
        ],
        args=[
            f"--window-position={monitor_left},{monitor_top}",
            f"--window-size={monitor_width},{monitor_height}",
            "--silent-debugger-extension-api",
            "--disable-blink-features=AutomationControlled",
        ],
    )

    start_urls = urls or ["about:blank"]
    first_page = context.pages[0] if context.pages else context.new_page()
    if start_urls and start_urls[0] not in ("", "about:blank"):
        first_page.goto(start_urls[0], wait_until="domcontentloaded")

    for url in start_urls[1:]:
        page = context.new_page()
        if url not in ("", "about:blank"):
            page.goto(url, wait_until="domcontentloaded")

    return context
