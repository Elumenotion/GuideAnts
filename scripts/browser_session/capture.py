"""Live browser session capture orchestration."""

from __future__ import annotations

import json
import secrets
import shutil
import sys
import threading
import time
from datetime import datetime
from pathlib import Path
from typing import Any

from playwright.sync_api import sync_playwright

from scripts.browser_session.browser_observer import BrowserSessionObserver, launch_capture_context
from scripts.browser_session.control_server import (
    CaptureBrowserExecutor,
    CommandQueue,
    SessionControlServer,
)
from scripts.browser_session.chain import append_part, init_chain, load_chain
from scripts.browser_session.checkpoints import CheckpointStore
from scripts.browser_session.media_probe import probe_part_media
from scripts.browser_session.mic import FfmpegDshowRecorder, create_narration_recorder
from scripts.browser_session.resume import prepare_resume
from scripts.browser_session.schema import (
    SCHEMA_VERSION_V2,
    MonitorGeometry,
    PartInfo,
    SessionBundle,
    SessionClock,
    write_json_atomic,
    write_provisional_session,
)
from scripts.browser_session.integrity import (
    CaptureIntegrityStateMachine,
    show_blocking_alert,
)
from scripts.browser_session.watchdog import spawn_watchdog
from scripts.browser_session.windows import ForegroundWindowPoller, find_chrome_window_near
from scripts.screen_recorder import ScreenRecorder, list_monitors

ROOT = Path(__file__).resolve().parents[2]
SESSIONS_DIR = ROOT / "recordings" / "sessions"
PROFILE_DIR = ROOT / "recordings" / "browser-profile"


def _make_session_dir(slug: str = "session") -> Path:
    stamp = datetime.now().strftime("%Y%m%d_%H%M%S")
    session_dir = SESSIONS_DIR / f"{stamp}_{slug}"
    session_dir.mkdir(parents=True, exist_ok=True)
    return session_dir


def _make_chain_dir(slug: str = "session") -> Path:
    stamp = datetime.now().strftime("%Y%m%d_%H%M%S")
    chain_dir = SESSIONS_DIR / f"{stamp}_{slug}"
    chain_dir.mkdir(parents=True, exist_ok=True)
    init_chain(chain_dir, slug=slug)
    return chain_dir


def _part_dir(chain_dir: Path, index: int) -> Path:
    part = chain_dir / f"part-{index:04d}"
    part.mkdir(parents=True, exist_ok=True)
    return part


def _known_media_duration_ms(media: dict[str, Any]) -> int | None:
    probe_status = media.get("probe_status", media.get("status"))
    if probe_status not in (None, "complete"):
        return None
    duration = media.get("session_duration_ms")
    if isinstance(duration, int) and not isinstance(duration, bool) and duration >= 0:
        return duration
    video_duration = (media.get("video") or {}).get("duration_ms")
    if (
        isinstance(video_duration, int)
        and not isinstance(video_duration, bool)
        and video_duration >= 0
    ):
        return video_duration
    return None


def _monitor_geometry(monitor_index: int) -> MonitorGeometry:
    monitors = list_monitors()
    for mon in monitors:
        if mon.index == monitor_index:
            return MonitorGeometry(
                index=mon.index,
                left=mon.left,
                top=mon.top,
                width=mon.width,
                height=mon.height,
            )
    raise ValueError(f"Monitor {monitor_index} not found")


class ManualCheckpointTrigger:
    def __init__(self) -> None:
        self._requested = False
        self._lock = threading.Lock()

    def request(self) -> None:
        with self._lock:
            self._requested = True

    def consume(self) -> bool:
        with self._lock:
            if not self._requested:
                return False
            self._requested = False
            return True


class CapturePartController:
    """Manage one recoverable capture part."""

    def __init__(
        self,
        *,
        session_dir: Path,
        monitor: MonitorGeometry,
        monitor_index: int,
        fps: int,
        t0_epoch_ms: int,
        recording_started_epoch_ms: int,
        part_info: PartInfo | None = None,
    ) -> None:
        self.session_dir = session_dir
        self.monitor = monitor
        self.monitor_index = monitor_index
        self.fps = fps
        self.t0_epoch_ms = t0_epoch_ms
        self.recording_started_epoch_ms = recording_started_epoch_ms
        self.part_info = part_info
        self.recorder = ScreenRecorder(
            monitor=monitor_index,
            output_dir=session_dir,
            fps=fps,
            filename_prefix="video",
        )
        self.narration_path = session_dir / "narration.wav"
        self.mic = create_narration_recorder(self.narration_path, t0_epoch_ms=t0_epoch_ms)
        self.window_poller: ForegroundWindowPoller | None = None
        self.store: CheckpointStore | None = None

    def start(self, *, capture_browser_hwnd_getter) -> None:
        for existing_path in (
            self.session_dir / "session.json",
            self.session_dir / "session.provisional.json",
            self.session_dir / "video.mp4",
            self.narration_path,
        ):
            if existing_path.exists():
                raise FileExistsError(f"refusing to overwrite existing capture artifact: {existing_path}")
        provisional = SessionBundle(
            schema_version=SCHEMA_VERSION_V2,
            session_id=self.session_dir.name,
            clock=SessionClock(
                t0_epoch_ms=self.t0_epoch_ms,
                recording_started_epoch_ms=self.recording_started_epoch_ms,
                recording_lead_in_ms=0,
                fps=self.fps,
            ),
            monitor=self.monitor,
            paths={
                "video": str((self.session_dir / "video.mp4").resolve()),
                "narration": str(self.narration_path.resolve()),
                "windows": str((self.session_dir / "windows.jsonl").resolve()),
                "events": str((self.session_dir / "events.jsonl").resolve()),
                "index": str((self.session_dir / "index.json").resolve()),
            },
            part=self.part_info,
        ).to_dict()
        provisional["status"] = "recording"
        write_provisional_session(self.session_dir, provisional)
        self.recorder.start()
        try:
            self.mic.start()
        except Exception as exc:
            raise RuntimeError(f"narration capture failed: {exc}") from exc
        self.window_poller = ForegroundWindowPoller(
            session_dir=self.session_dir,
            t0_epoch_ms=self.t0_epoch_ms,
            monitor=self.monitor,
            capture_browser_hwnd_getter=capture_browser_hwnd_getter,
        )
        self.window_poller.start()
        self.store = CheckpointStore(self.session_dir)

    def stop(
        self,
        *,
        capture_browser_hwnd: int | None,
        urls: list[str] | None,
        recording_meta: dict[str, Any] | None = None,
        status: str = "complete",
    ) -> dict[str, Any]:
        errors: list[str] = []
        if self.window_poller is not None:
            self.window_poller.stop()
        meta = recording_meta or {}
        try:
            meta = self.recorder.stop()
        except Exception as exc:  # noqa: BLE001
            print(f"Warning: screen recorder stop failed: {exc}")
            errors.append(f"screen recorder stop failed: {exc}")
        try:
            self.mic.stop()
        except Exception as exc:  # noqa: BLE001
            print(f"Warning: narration stop failed: {exc}")
            errors.append(f"narration stop failed: {exc}")
        if errors:
            status = "interrupted"
        return self.finalize(
            capture_browser_hwnd=capture_browser_hwnd,
            urls=urls,
            recording_meta=meta,
            status=status,
            errors=errors,
        )

    def finalize(
        self,
        *,
        capture_browser_hwnd: int | None,
        urls: list[str] | None,
        recording_meta: dict[str, Any],
        status: str,
        errors: list[str] | None = None,
    ) -> dict[str, Any]:
        final_video = self.session_dir / "video.mp4"
        if self.recorder.video_path and self.recorder.video_path.exists():
            if self.recorder.video_path != final_video:
                if final_video.exists():
                    raise FileExistsError(f"refusing to overwrite existing video: {final_video}")
                self.recorder.video_path.rename(final_video)
            recording_meta.setdefault("video", {})["path"] = str(final_video.resolve())

        media = probe_part_media(self.session_dir)
        video_duration_ms = _known_media_duration_ms(media)
        narration_duration_ms = (media.get("narration") or {}).get("duration_ms")
        if not isinstance(narration_duration_ms, int) or isinstance(narration_duration_ms, bool):
            narration_duration_ms = None
        video_anchor = {
            "logical_start_ms": 0,
            "logical_end_ms": video_duration_ms,
            "stream_start_ms": 0,
            "stream_end_ms": video_duration_ms,
            "duration_ms": video_duration_ms,
            "frame_or_sample_count": int((media.get("video") or {}).get("frame_count") or 0),
            "sha256": (media.get("video") or {}).get("sha256"),
            "path": str(final_video.resolve()) if final_video.is_file() else None,
        }
        narration_anchor = {
            "logical_start_ms": 0 if narration_duration_ms is not None else None,
            "logical_end_ms": narration_duration_ms,
            "stream_start_ms": 0,
            "stream_end_ms": narration_duration_ms,
            "duration_ms": narration_duration_ms,
            "frame_or_sample_count": self.mic.sample_count,
            "sha256": (media.get("narration") or {}).get("sha256"),
            "path": str(self.narration_path.resolve()) if self.narration_path.is_file() else None,
            "wall_clock_first_sample_ms": self.mic.first_sample_ms,
            "wall_clock_last_sample_ms": self.mic.last_sample_ms,
        }
        media_payload = {
            **media,
            "anchors": {"video": video_anchor, "narration": narration_anchor},
            "probe_status": media.get("status"),
            "status": status,
        }
        if video_duration_ms is not None and media.get("status") == "complete":
            media_payload["session_duration_ms"] = video_duration_ms
            media_payload["duration_basis"] = "video_probe"

        clock = SessionClock(
            t0_epoch_ms=self.t0_epoch_ms,
            recording_started_epoch_ms=self.recording_started_epoch_ms,
            recording_lead_in_ms=max(0, self.t0_epoch_ms - self.recording_started_epoch_ms),
            fps=self.fps,
        )
        bundle = SessionBundle(
            schema_version=SCHEMA_VERSION_V2,
            session_id=self.session_dir.name,
            clock=clock,
            monitor=self.monitor,
            paths={
                "video": str(final_video.resolve()) if final_video.is_file() else str(self.recorder.video_path or ""),
                "narration": str(self.narration_path.resolve()),
                "windows": str((self.session_dir / "windows.jsonl").resolve()),
                "events": str((self.session_dir / "events.jsonl").resolve()),
                "index": str((self.session_dir / "index.json").resolve()),
            },
            capture_browser_hwnd=hex(capture_browser_hwnd) if capture_browser_hwnd else None,
            part=self.part_info,
            media=media_payload,
        )
        payload = bundle.to_dict()
        payload["status"] = status
        if errors:
            payload["errors"] = errors
        write_json_atomic(self.session_dir / "session.json", payload)
        provisional = self.session_dir / "session.provisional.json"
        if provisional.is_file():
            provisional.unlink(missing_ok=True)
        write_json_atomic(
            self.session_dir / "meta.json",
            {"session_id": self.session_dir.name, "recording": recording_meta, "urls": urls or [], "status": status},
        )
        return payload


def _shutdown_playwright(context, playwright, observer: BrowserSessionObserver | None = None, *, timeout: float = 15.0) -> None:
    """Close browser and Playwright driver on the main thread."""
    if observer is not None:
        observer.shutdown()

    deadline = time.monotonic() + timeout
    if context is not None:
        for page in list(context.pages):
            try:
                if not page.is_closed():
                    page.evaluate("() => window.__sessionCaptureShutdown && window.__sessionCaptureShutdown()")
            except Exception:  # noqa: BLE001
                pass
        for page in list(context.pages):
            try:
                page.close()
            except Exception:  # noqa: BLE001
                pass
        try:
            context.close()
        except Exception as exc:  # noqa: BLE001
            print(f"Warning: browser close failed: {exc}")

    if playwright is not None:
        try:
            playwright.stop()
        except Exception as exc:  # noqa: BLE001
            print(f"Warning: playwright stop failed: {exc}")

    remaining = deadline - time.monotonic()
    if remaining > 0:
        time.sleep(min(0.25, remaining))


def _request_capture_stop(stop_event: threading.Event, *, reason: str) -> None:
    if stop_event.is_set():
        return
    print(f"[capture] stopping ({reason})...")
    stop_event.set()


def _keyboard_listener(stop_event: threading.Event, manual: ManualCheckpointTrigger) -> None:
    if sys.platform == "win32":
        if not sys.stdin.isatty():
            print(
                "[capture] stdin is not an interactive console; Enter/Q are unavailable. "
                "Use Ctrl+C or the browser-session stop command."
            )
            return

        import msvcrt

        print("[capture] keyboard controls active (Enter/Q stop, C checkpoint)")
        try:
            while not stop_event.is_set():
                if msvcrt.kbhit():
                    key = msvcrt.getwch()
                    if key in ("\x00", "\xe0"):
                        if msvcrt.kbhit():
                            msvcrt.getwch()
                        continue
                    if key.lower() == "c":
                        manual.request()
                        print("[capture] manual checkpoint (c)")
                    elif key in ("\r", "\n"):
                        _request_capture_stop(stop_event, reason="enter")
                    elif key.lower() == "q":
                        _request_capture_stop(stop_event, reason="q")
                    elif key == "\x03":
                        _request_capture_stop(stop_event, reason="ctrl+c")
                stop_event.wait(0.05)
        except Exception as exc:  # noqa: BLE001
            print(f"[capture] Windows console input unavailable: {exc}")
        return

    import select

    if not sys.stdin.isatty():
        print(
            "[capture] stdin is not interactive; Enter/Q are unavailable. "
            "Use Ctrl+C or the browser-session stop command."
        )
        return

    while not stop_event.is_set():
        ready, _, _ = select.select([sys.stdin], [], [], 0.1)
        if not ready:
            continue
        line = sys.stdin.readline()
        if line.strip().lower() == "c":
            manual.request()
            print("[capture] manual checkpoint (c)")
        else:
            _request_capture_stop(stop_event, reason="enter")


def run_capture_interactive_enter(
    *,
    monitor_index: int = 1,
    fps: int = 30,
    urls: list[str] | None = None,
    capture_mhtml: bool = True,
    slug: str = "session",
    session_dir: Path | None = None,
    resume_dir: Path | None = None,
    roll_duration_sec: float | None = None,
    roll_size_mb: float | None = None,
) -> Path:
    if resume_dir is not None and session_dir is not None:
        raise ValueError("use either resume_dir or session_dir, not both")

    chain_dir: Path | None = None
    part_index = 1
    resumed = False

    if resume_dir is not None:
        resume = prepare_resume(resume_dir)
        chain_dir = resume.chain_dir
        session_dir = resume.part_dir
        part_index = resume.part_index
        monitor_index = resume.monitor_index
        fps = resume.fps
        if not urls:
            urls = resume.urls
        resumed = True
        print(
            f"Resuming chain {chain_dir.name} -> {session_dir.name} "
            f"(after {resume.prior_parts} part(s), chain offset {resume.chain_offset_ms} ms)"
        )
    elif roll_duration_sec or roll_size_mb:
        chain_dir = _make_chain_dir(slug)
        session_dir = _part_dir(chain_dir, part_index)
    else:
        session_dir = session_dir or _make_session_dir(slug)

    monitor = _monitor_geometry(monitor_index)
    PROFILE_DIR.mkdir(parents=True, exist_ok=True)
    recording_started_epoch_ms = int(time.time() * 1000)
    t0_epoch_ms = recording_started_epoch_ms
    stop_event = threading.Event()
    manual = ManualCheckpointTrigger()

    part_info = None
    if chain_dir is not None:
        chain_offset_ms = 0
        if resumed:
            chain_offset_ms = int(load_chain(chain_dir).get("total_duration_ms", 0))
        part_info = PartInfo(
            index=part_index,
            chain_id=chain_dir.name,
            chain_offset_ms=chain_offset_ms,
            status="recording",
        )

    controller = CapturePartController(
        session_dir=session_dir,
        monitor=monitor,
        monitor_index=monitor_index,
        fps=fps,
        t0_epoch_ms=t0_epoch_ms,
        recording_started_epoch_ms=recording_started_epoch_ms,
        part_info=part_info,
    )

    print(f"Session dir: {session_dir}")
    if chain_dir is not None:
        print(f"Chain dir: {chain_dir}")
    print(f"Starting screen capture on monitor {monitor_index}...")
    capture_browser_hwnd: dict[str, int | None] = {"value": None}
    provisional_before_start = (session_dir / "session.provisional.json").is_file()
    try:
        controller.start(capture_browser_hwnd_getter=lambda: capture_browser_hwnd["value"])
    except Exception:
        if not provisional_before_start and (session_dir / "session.provisional.json").is_file():
            try:
                payload = controller.stop(
                    capture_browser_hwnd=None,
                    urls=urls,
                    status="interrupted",
                )
                if chain_dir is not None:
                    append_part(
                        chain_dir,
                        part_name=session_dir.name,
                        duration_ms=_known_media_duration_ms(payload.get("media") or {}),
                        reason="startup_failure",
                        status=payload.get("status"),
                    )
            except Exception as finalize_exc:  # noqa: BLE001
                print(f"Warning: failed to preserve startup failure artifacts: {finalize_exc}")
        raise
    print("Starting narration capture...")

    threading.Thread(
        target=_keyboard_listener,
        args=(stop_event, manual),
        name="keyboard-listener",
        daemon=True,
    ).start()

    observer: BrowserSessionObserver | None = None
    context = None
    playwright = None
    control_server: SessionControlServer | None = None
    command_queue = CommandQueue()
    browser_executor: CaptureBrowserExecutor | None = None
    integrity: CaptureIntegrityStateMachine | None = None
    watchdog_proc = None
    finalize_ok = False
    final_status = "interrupted"
    part_started_at = time.monotonic()

    def _drain_control_commands() -> None:
        if browser_executor is None or stop_event.is_set():
            return
        while not stop_event.is_set() and command_queue.drain(browser_executor.execute) > 0:
            pass

    try:
        playwright = sync_playwright().start()
        context = launch_capture_context(
            playwright,
            profile_dir=PROFILE_DIR,
            monitor_left=monitor.left,
            monitor_top=monitor.top,
            monitor_width=monitor.width,
            monitor_height=monitor.height,
            urls=urls or ["about:blank"],
        )
        time.sleep(1.0)
        capture_browser_hwnd["value"] = find_chrome_window_near(monitor)
        integrity = CaptureIntegrityStateMachine(
            session_dir,
            on_failure_alert=lambda code, msg: show_blocking_alert("Capture Integrity Failure", f"{code}: {msg}"),
        )
        observer = BrowserSessionObserver(
            context=context,
            store=controller.store,
            t0_epoch_ms=t0_epoch_ms,
            capture_mhtml=capture_mhtml,
            manual_trigger=manual.consume,
        )
        initial_checkpoints = observer.write_initial_checkpoints()
        if initial_checkpoints == 0:
            raise RuntimeError("preflight failed: no initial browser checkpoints")
        integrity.update_track("browser", healthy=True, last_pts_ms=0)
        browser_executor = CaptureBrowserExecutor(
            context=context,
            observer=observer,
            session_dir=session_dir,
            stop_event=stop_event,
        )
        integrity.enter_recording()
        import os

        watchdog_proc = spawn_watchdog(session_dir, os.getpid())
        control_server = SessionControlServer(
            token=secrets.token_urlsafe(32),
            queue=command_queue,
            session_dir=session_dir,
            chain_dir=chain_dir,
            integrity_snapshot=lambda: integrity.snapshot().to_dict() if integrity else {},
        )
        control_path = control_server.start()
        print(f"Control endpoint: {control_path}")
        print(
            "Capture running. Focus this terminal, then press Enter or Q to stop, "
            "or C for a manual checkpoint."
        )
        while not stop_event.is_set():
            if integrity is not None:
                integrity.heartbeat()
                if not controller.recorder.is_recording:
                    integrity.handle_track_failure("av", "screen recorder stopped unexpectedly")
                elif controller.mic and not getattr(controller.mic, "is_recording", True):
                    integrity.handle_track_failure("av", "microphone recorder stopped unexpectedly")
            _drain_control_commands()
            if observer is not None:
                observer.drain_checkpoints(max_count=1, stop_event=stop_event)
            command_queue.wait_for_work(stop_event, timeout=0.05)
            if chain_dir is None:
                continue
            rotate = False
            reason = ""
            if roll_duration_sec and (time.monotonic() - part_started_at) >= roll_duration_sec:
                rotate = True
                reason = "max_duration"
            if roll_size_mb:
                video = session_dir / "video.mp4"
                if video.is_file() and video.stat().st_size >= roll_size_mb * 1024 * 1024:
                    rotate = True
                    reason = "max_size"
            if not rotate:
                continue
            print(f"[capture] rotating part ({reason})...")
            payload = controller.stop(
                capture_browser_hwnd=capture_browser_hwnd["value"],
                urls=urls,
                status="complete",
            )
            duration_ms = _known_media_duration_ms(payload.get("media") or {})
            append_part(
                chain_dir,
                part_name=session_dir.name,
                duration_ms=duration_ms,
                reason=reason,
                status=payload.get("status"),
            )
            if duration_ms is None:
                raise RuntimeError(
                    f"cannot rotate after {session_dir.name}: media duration is unknown"
                )
            if observer is not None and controller.store is not None:
                observer.rotate_store(controller.store)
            part_index += 1
            session_dir = _part_dir(chain_dir, part_index)
            t0_epoch_ms = int(time.time() * 1000)
            part_info = PartInfo(
                index=part_index,
                chain_id=chain_dir.name,
                chain_offset_ms=int(load_chain(chain_dir).get("total_duration_ms", 0)),
                status="recording",
            )
            controller = CapturePartController(
                session_dir=session_dir,
                monitor=monitor,
                monitor_index=monitor_index,
                fps=fps,
                t0_epoch_ms=t0_epoch_ms,
                recording_started_epoch_ms=recording_started_epoch_ms,
                part_info=part_info,
            )
            controller.start(capture_browser_hwnd_getter=lambda: capture_browser_hwnd["value"])
            capture_browser_hwnd["value"] = find_chrome_window_near(monitor)
            if observer is not None:
                observer.set_t0_epoch_ms(t0_epoch_ms)
            if browser_executor is not None:
                browser_executor = CaptureBrowserExecutor(
                    context=context,
                    observer=observer,
                    session_dir=session_dir,
                    stop_event=stop_event,
                )
            if control_server is not None:
                control_server.stop()
                control_path = control_server.start()
                print(f"Control endpoint: {control_path}")
            part_started_at = time.monotonic()
            print(f"[capture] rotated to {session_dir.name}")
    except KeyboardInterrupt:
        stop_event.set()
    finally:
        print("Stopping capture...")
        interrupted = stop_event.is_set()
        try:
            status = "interrupted" if interrupted else "complete"
            if integrity is not None and integrity.session_status not in ("complete",):
                status = integrity.session_status
            payload = controller.stop(
                capture_browser_hwnd=capture_browser_hwnd["value"],
                urls=urls,
                status=status,
            )
            final_status = payload.get("status", status)
            finalize_ok = final_status == "complete" or (
                integrity is not None and integrity.session_status == "recovered_with_gap"
            )
            if chain_dir is not None:
                duration_ms = _known_media_duration_ms(payload.get("media") or {})
                append_part(
                    chain_dir,
                    part_name=session_dir.name,
                    duration_ms=duration_ms,
                    reason="shutdown",
                    status=payload.get("status"),
                )
        except Exception as exc:  # noqa: BLE001
            print(f"Warning: session finalize failed: {exc}")
            if chain_dir is not None:
                try:
                    from scripts.browser_session.salvage import reconcile_chain

                    reconcile_chain(chain_dir)
                except Exception as salvage_exc:  # noqa: BLE001
                    print(f"Warning: interrupted session reconciliation failed: {salvage_exc}")
        if control_server is not None:
            control_server.stop()
        if watchdog_proc is not None:
            watchdog_proc.terminate()
        _shutdown_playwright(context, playwright, observer)

    result_dir = chain_dir or session_dir
    if finalize_ok:
        print(f"Session saved -> {result_dir} [{final_status}]")
    else:
        print(f"Session sealed with errors -> {result_dir} [{final_status}]", file=sys.stderr)
        if interrupted:
            print("(stopped via Ctrl+C — partial recording preserved)", file=sys.stderr)
    return result_dir
