"""Browser checkpoint persistence."""

from __future__ import annotations

import json
import threading
from pathlib import Path
from typing import Any, Callable

from scripts.browser_session.schema import CheckpointMeta, append_jsonl, write_json_atomic


class CheckpointStore:
    """Write browser checkpoints and maintain ``index.json``."""

    def __init__(self, session_dir: Path, *, on_event: Callable[[dict[str, Any]], None] | None = None) -> None:
        self._session_dir = session_dir
        self._checkpoints_dir = session_dir / "checkpoints"
        self._index_path = session_dir / "index.json"
        self._events_path = session_dir / "events.jsonl"
        self._lock = threading.Lock()
        self._counter = 0
        self._checkpoints: list[dict[str, Any]] = []
        self._tabs: dict[str, dict[str, Any]] = {}
        self._on_event = on_event
        self._checkpoints_dir.mkdir(parents=True, exist_ok=True)
        self._load_index()

    def _load_index(self) -> None:
        if not self._index_path.is_file():
            return
        data = json.loads(self._index_path.read_text(encoding="utf-8"))
        self._checkpoints = list(data.get("checkpoints", []))
        self._tabs = dict(data.get("tabs", {}))
        if self._checkpoints:
            last_id = self._checkpoints[-1].get("id", "000000")
            try:
                self._counter = int(str(last_id))
            except ValueError:
                self._counter = len(self._checkpoints)

    def _next_id(self) -> str:
        self._counter += 1
        return f"{self._counter:06d}"

    def _flush_index(self) -> None:
        write_json_atomic(
            self._index_path,
            {
                "checkpoints": self._checkpoints,
                "tabs": self._tabs,
            },
        )

    def register_tab(self, tab_id: str, *, t_ms: int, url: str = "", title: str = "") -> None:
        with self._lock:
            if tab_id in self._tabs:
                return
            self._tabs[tab_id] = {
                "tab_id": tab_id,
                "opened_at_ms": t_ms,
                "closed_at_ms": None,
                "last_url": url,
                "last_title": title,
            }
            self._flush_index()
        self._emit({"kind": "tab.open", "t_ms": t_ms, "tab_id": tab_id, "url": url})

    def close_tab(self, tab_id: str, *, t_ms: int) -> None:
        with self._lock:
            tab = self._tabs.get(tab_id)
            if tab is None:
                return
            tab["closed_at_ms"] = t_ms
            self._flush_index()
        self._emit({"kind": "tab.close", "t_ms": t_ms, "tab_id": tab_id})

    def focus_tab(self, tab_id: str, *, t_ms: int) -> None:
        self._emit({"kind": "tab.focus", "t_ms": t_ms, "tab_id": tab_id})

    def write_checkpoint(
        self,
        *,
        t_ms: int,
        tab_id: str,
        foreground: bool,
        trigger: str,
        url: str,
        title: str,
        scroll_x: int = 0,
        scroll_y: int = 0,
        selection: str = "",
        screenshot: bytes | None = None,
        text: str | None = None,
        mhtml: str | None = None,
    ) -> str:
        cp_id = self._next_id()
        cp_dir = self._checkpoints_dir / cp_id
        cp_dir.mkdir(parents=True, exist_ok=True)

        has_screenshot = screenshot is not None
        has_text = text is not None
        has_mhtml = mhtml is not None

        if screenshot is not None:
            (cp_dir / "screenshot.png").write_bytes(screenshot)
        if text is not None:
            (cp_dir / "text.txt").write_text(text, encoding="utf-8")
        if mhtml is not None:
            (cp_dir / "page.mhtml").write_text(mhtml, encoding="utf-8")

        meta = CheckpointMeta(
            id=cp_id,
            t_ms=t_ms,
            tab_id=tab_id,
            foreground=foreground,
            trigger=trigger,
            url=url,
            title=title,
            scroll_x=scroll_x,
            scroll_y=scroll_y,
            selection=selection,
            has_screenshot=has_screenshot,
            has_text=has_text,
            has_mhtml=has_mhtml,
        )
        write_json_atomic(cp_dir / "meta.json", meta.to_dict())

        row = meta.to_dict()
        with self._lock:
            self._checkpoints.append(row)
            tab = self._tabs.get(tab_id)
            if tab is not None:
                tab["last_url"] = url
                tab["last_title"] = title
            self._flush_index()

        self._emit(
            {
                "kind": "checkpoint",
                "t_ms": t_ms,
                "tab_id": tab_id,
                "checkpoint_id": cp_id,
                "trigger": trigger,
                "foreground": foreground,
                "url": url,
            }
        )
        return cp_id

    def emit_event(self, event: dict[str, Any]) -> None:
        self._emit(event)

    def _emit(self, event: dict[str, Any]) -> None:
        append_jsonl(self._events_path, event)
        if self._on_event is not None:
            self._on_event(event)
