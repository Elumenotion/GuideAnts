"""ComfyUI progress telemetry for talking-head jobs."""

from __future__ import annotations

import json
import logging
import sys
import threading
import time
import urllib.parse
from typing import Any, Callable

try:
    from websocket import WebSocketApp
except ImportError:  # pragma: no cover - exercised in the image build
    WebSocketApp = None  # type: ignore[assignment,misc]

LOGGER = logging.getLogger("guideants_video_adapter.telemetry")


def initial_progress() -> dict[str, Any]:
    now = time.time()
    return {
        "phase": "queued",
        "message": "queued",
        "node_id": None,
        "node_class": None,
        "step": None,
        "max_steps": None,
        "percent": None,
        "queue_remaining": None,
        "queue_position": None,
        "last_event": None,
        "updated_at": now,
    }


def merge_progress(current: dict[str, Any], **updates: Any) -> dict[str, Any]:
    merged = dict(current)
    for key, value in updates.items():
        if value is not None or key in {"node_id", "node_class", "step", "max_steps", "percent"}:
            merged[key] = value
    merged["updated_at"] = time.time()
    return merged


def format_progress(progress: dict[str, Any]) -> str:
    phase = progress.get("phase", "unknown")
    message = progress.get("message") or phase
    parts = [str(phase), str(message)]
    node_class = progress.get("node_class")
    node_id = progress.get("node_id")
    if node_class or node_id:
        parts.append(f"node={node_id or '?'}:{node_class or '?'}")
    step = progress.get("step")
    max_steps = progress.get("max_steps")
    if isinstance(step, int) and isinstance(max_steps, int) and max_steps > 0:
        percent = progress.get("percent")
        if isinstance(percent, (int, float)):
            parts.append(f"progress={step}/{max_steps} ({percent}%)")
        else:
            parts.append(f"progress={step}/{max_steps}")
    queue_position = progress.get("queue_position")
    if isinstance(queue_position, int):
        parts.append(f"queue_position={queue_position}")
    queue_remaining = progress.get("queue_remaining")
    if isinstance(queue_remaining, int):
        parts.append(f"queue_remaining={queue_remaining}")
    return " | ".join(parts)


def log_job_progress(job_id: str, progress: dict[str, Any]) -> None:
    line = f"[video-job {job_id}] {format_progress(progress)}"
    print(line, file=sys.stderr, flush=True)
    LOGGER.info(line)


def queue_state_for_prompt(queue_payload: dict[str, Any], prompt_id: str) -> dict[str, Any]:
    running = queue_payload.get("queue_running", [])
    pending = queue_payload.get("queue_pending", [])
    if not isinstance(running, list) or not isinstance(pending, list):
        return {}
    for index, item in enumerate(running):
        if isinstance(item, list) and len(item) >= 2 and item[1] == prompt_id:
            return {"queue_position": 0, "queue_remaining": len(pending)}
    for index, item in enumerate(pending):
        if isinstance(item, list) and len(item) >= 2 and item[1] == prompt_id:
            return {"queue_position": index + 1, "queue_remaining": len(pending)}
    return {"queue_position": None, "queue_remaining": len(pending)}


class ComfyProgressListener:
    """Subscribe to ComfyUI websocket events for one prompt."""

    def __init__(
        self,
        base_url: str,
        client_id: str,
        prompt_id: str,
        workflow: dict[str, Any],
        on_update: Callable[[dict[str, Any]], None],
    ) -> None:
        self.base_url = base_url
        self.client_id = client_id
        self.prompt_id = prompt_id
        self.workflow = workflow
        self.on_update = on_update
        self._stop = threading.Event()
        self._thread: threading.Thread | None = None
        self._app: WebSocketApp | None = None

    def _node_class(self, node_id: str | None) -> str | None:
        if not node_id:
            return None
        node = self.workflow.get(node_id)
        if isinstance(node, dict):
            class_type = node.get("class_type")
            if isinstance(class_type, str) and class_type:
                return class_type
        return None

    def _ws_url(self) -> str:
        parsed = urllib.parse.urlparse(self.base_url)
        scheme = "wss" if parsed.scheme == "https" else "ws"
        query = urllib.parse.urlencode({"clientId": self.client_id})
        return urllib.parse.urlunparse((scheme, parsed.netloc, "/ws", "", query, ""))

    def _emit(self, **updates: Any) -> None:
        self.on_update(updates)

    def _handle_message(self, raw: str) -> None:
        try:
            message = json.loads(raw)
        except json.JSONDecodeError:
            return
        if not isinstance(message, dict):
            return
        msg_type = message.get("type")
        data = message.get("data")
        if not isinstance(msg_type, str) or not isinstance(data, dict):
            return
        prompt_id = data.get("prompt_id")
        if isinstance(prompt_id, str) and prompt_id and prompt_id != self.prompt_id:
            return

        if msg_type == "execution_start":
            self._emit(
                phase="executing",
                message="execution started",
                last_event=msg_type,
            )
            return

        if msg_type == "executing":
            node = data.get("node")
            if node is None:
                self._emit(
                    phase="executing",
                    message="node finished",
                    node_id=None,
                    node_class=None,
                    last_event=msg_type,
                )
                return
            node_id = str(node)
            node_class = self._node_class(node_id)
            self._emit(
                phase="executing",
                node_id=node_id,
                node_class=node_class,
                message=f"executing node {node_id} ({node_class or 'unknown'})",
                last_event=msg_type,
            )
            return

        if msg_type == "progress":
            node = data.get("node")
            node_id = str(node) if node is not None else None
            value = data.get("value")
            max_value = data.get("max")
            step = int(value) if isinstance(value, (int, float)) else None
            max_steps = int(max_value) if isinstance(max_value, (int, float)) else None
            percent = None
            if isinstance(step, int) and isinstance(max_steps, int) and max_steps > 0:
                percent = round(100.0 * step / max_steps, 1)
            self._emit(
                phase="sampling",
                node_id=node_id,
                node_class=self._node_class(node_id),
                step=step,
                max_steps=max_steps,
                percent=percent,
                message=(
                    f"sampling {step}/{max_steps}"
                    if step is not None and max_steps is not None
                    else "sampling"
                ),
                last_event=msg_type,
            )
            return

        if msg_type == "execution_error":
            detail = data.get("exception_message") or data.get("exception_type") or "execution error"
            self._emit(
                phase="failed",
                message=str(detail),
                last_event=msg_type,
            )
            return

        if msg_type == "status":
            status = data.get("status")
            if isinstance(status, dict):
                exec_info = status.get("exec_info")
                if isinstance(exec_info, dict):
                    remaining = exec_info.get("queue_remaining")
                    if isinstance(remaining, int):
                        self._emit(queue_remaining=remaining, last_event=msg_type)

    def _run(self) -> None:
        if WebSocketApp is None:
            self._emit(message="websocket client unavailable", last_event="telemetry_disabled")
            return

        def on_message(_ws: WebSocketApp, message: str) -> None:
            if self._stop.is_set():
                return
            self._handle_message(message)

        def on_error(_ws: WebSocketApp, error: Exception | str) -> None:
            if self._stop.is_set():
                return
            self._emit(message=f"telemetry websocket error: {error}", last_event="websocket_error")

        self._app = WebSocketApp(
            self._ws_url(),
            on_message=on_message,
            on_error=on_error,
        )
        while not self._stop.is_set():
            self._app.run_forever(ping_interval=20, ping_timeout=10)
            if self._stop.is_set():
                break
            time.sleep(0.5)

    def start(self) -> None:
        if self._thread is not None:
            return
        self._thread = threading.Thread(target=self._run, name=f"comfy-telemetry-{self.client_id}", daemon=True)
        self._thread.start()

    def stop(self) -> None:
        self._stop.set()
        if self._app is not None:
            self._app.close()
        if self._thread is not None:
            self._thread.join(timeout=2.0)
            self._thread = None
