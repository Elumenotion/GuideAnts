#!/usr/bin/env python3
"""Orchestrate screen recording + Playwright walkthrough scenarios."""

from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
import time
from datetime import datetime
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from scripts.screen_recorder import ScreenRecorder, list_monitors  # noqa: E402

DEFAULT_MONITOR = 1
DEFAULT_FPS = 30
DEFAULT_BASE_URL = "http://localhost:5107"
WALKTHROUGHS_DIR = ROOT / "walkthroughs"
RUNS_DIR = ROOT / "recordings" / "runs"


def _scenario_spec_path(scenario: str) -> Path:
    """Resolve `notebook/toolbar-tour` -> scenarios/notebook/toolbar-tour.spec.ts."""
    normalized = scenario.replace("\\", "/").strip("/")
    if normalized.endswith(".spec.ts"):
        path = WALKTHROUGHS_DIR / "scenarios" / normalized
    else:
        path = WALKTHROUGHS_DIR / "scenarios" / f"{normalized}.spec.ts"
    if not path.is_file():
        raise FileNotFoundError(f"Scenario not found: {path}")
    return path


def _monitor_window_position(monitor_index: int) -> str | None:
    monitors = list_monitors()
    for mon in monitors:
        if mon.index == monitor_index:
            return f"{mon.left},{mon.top}"
    return None


def _monitor_env(monitor_index: int) -> dict[str, str]:
    monitors = list_monitors()
    for mon in monitors:
        if mon.index == monitor_index:
            return {
                "WALKTHROUGH_MONITOR_LEFT": str(mon.left),
                "WALKTHROUGH_MONITOR_TOP": str(mon.top),
                "WALKTHROUGH_MONITOR_WIDTH": str(mon.width),
                "WALKTHROUGH_MONITOR_HEIGHT": str(mon.height),
            }
    return {}


def _make_run_dir(scenario: str) -> Path:
    stamp = datetime.now().strftime("%Y%m%d_%H%M%S")
    slug = scenario.replace("/", "-").replace("\\", "-")
    run_dir = RUNS_DIR / f"{stamp}_{slug}"
    run_dir.mkdir(parents=True, exist_ok=True)
    return run_dir


def _read_events_jsonl(path: Path) -> list[dict[str, Any]]:
    if not path.is_file():
        return []
    events: list[dict[str, Any]] = []
    for line in path.read_text(encoding="utf-8").splitlines():
        line = line.strip()
        if not line:
            continue
        events.append(json.loads(line))
    return events


def _merge_timeline(
    *,
    run_dir: Path,
    scenario: str,
    t0_epoch_ms: int,
    recording_started_epoch_ms: int,
    fps: int,
    recording_meta: dict[str, Any],
    playwright_exit_code: int,
) -> dict[str, Any]:
    events_path = run_dir / "events.jsonl"
    manifest_path = run_dir / "playwright-manifest.json"

    events = _read_events_jsonl(events_path)
    if manifest_path.is_file():
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        if not events and manifest.get("events"):
            events = manifest["events"]

    duration_ms = int(recording_meta.get("recording", {}).get("duration_seconds", 0) * 1000)

    t0_epoch_path = run_dir / "t0.epoch"
    scenario_t0_epoch_ms = t0_epoch_ms
    if t0_epoch_path.is_file():
        try:
            scenario_t0_epoch_ms = int(t0_epoch_path.read_text(encoding="utf-8").strip())
        except ValueError:
            pass

    recording_lead_in_ms = max(0, scenario_t0_epoch_ms - recording_started_epoch_ms)

    timeline: dict[str, Any] = {
        "schema_version": 1,
        "run_id": run_dir.name,
        "scenario": {
            "id": scenario,
            "version": os.environ.get("WALKTHROUGH_SCENARIO_VERSION", "0.1.0"),
        },
        "clock": {
            "t0_epoch_ms": scenario_t0_epoch_ms,
            "recording_started_epoch_ms": recording_started_epoch_ms,
            "recording_lead_in_ms": recording_lead_in_ms,
            "t0_wall": recording_meta.get("recording", {}).get("started_at"),
            "fps": fps,
        },
        "video": {
            "path": str((run_dir / "video.mp4").resolve()),
            "monitor_index": recording_meta.get("source", {}).get("monitor_index"),
            "duration_ms": duration_ms,
            "frame_count": recording_meta.get("video", {}).get("frame_count"),
        },
        "playwright": {
            "exit_code": playwright_exit_code,
            "manifest_path": str(manifest_path) if manifest_path.is_file() else None,
        },
        "events": events,
    }

    timeline_path = run_dir / "timeline.json"
    timeline_path.write_text(json.dumps(timeline, indent=2) + "\n", encoding="utf-8")
    return timeline


def _run_playwright(
    *,
    scenario_spec: Path,
    run_dir: Path,
    t0_epoch_ms: int,
    fps: int,
    base_url: str,
    mode: str,
    window_position: str | None,
    monitor_env: dict[str, str],
) -> int:
    env = os.environ.copy()
    env.update(
        {
            "WALKTHROUGH_RUN_DIR": str(run_dir),
            "WALKTHROUGH_T0_EPOCH_MS": str(t0_epoch_ms),
            "WALKTHROUGH_FPS": str(fps),
            "WALKTHROUGH_BASE_URL": base_url,
            "WALKTHROUGH_MODE": mode,
            "WALKTHROUGH_EMAIL": os.environ.get("WALKTHROUGH_EMAIL", "Test@example.com"),
            "WALKTHROUGH_PASSWORD": os.environ.get("WALKTHROUGH_PASSWORD", "password"),
            "WALKTHROUGH_SIDEBAR_WIDTH": os.environ.get("WALKTHROUGH_SIDEBAR_WIDTH", "520"),
            **monitor_env,
        }
    )
    if window_position:
        env["WALKTHROUGH_WINDOW_POSITION"] = window_position

    npm_cmd = "npm.cmd" if sys.platform == "win32" else "npm"
    spec_rel = scenario_spec.relative_to(WALKTHROUGHS_DIR).as_posix()

    cmd = [npm_cmd, "exec", "playwright", "test", spec_rel]

    print(f"Running: {' '.join(cmd)}")
    print(f"Run dir: {run_dir}")
    completed = subprocess.run(
        cmd,
        cwd=WALKTHROUGHS_DIR,
        env=env,
        check=False,
    )
    return completed.returncode


def run_walkthrough(args: argparse.Namespace) -> int:
    scenario_spec = _scenario_spec_path(args.scenario)
    run_dir = Path(args.run_dir) if args.run_dir else _make_run_dir(args.scenario)
    run_dir.mkdir(parents=True, exist_ok=True)

    window_position = _monitor_window_position(args.monitor)
    monitor_env = _monitor_env(args.monitor)
    if window_position:
        print(f"Monitor {args.monitor} window position: {window_position}")
        mon = monitor_env
        if mon:
            print(
                f"Monitor {args.monitor} size: "
                f"{mon.get('WALKTHROUGH_MONITOR_WIDTH')}x{mon.get('WALKTHROUGH_MONITOR_HEIGHT')}"
            )
    else:
        print(f"Warning: monitor {args.monitor} not found; Chrome position not set")

    recorder = ScreenRecorder(
        monitor=args.monitor,
        output_dir=run_dir,
        fps=args.fps,
        filename_prefix="video",
    )

    print(f"Starting screen capture on monitor {args.monitor}...")
    recording_started_epoch_ms = int(time.time() * 1000)
    video_path = recorder.start()
    t0_epoch_ms = recording_started_epoch_ms
    print(f"Recording -> {video_path}")

    playwright_code = 0
    try:
        playwright_code = _run_playwright(
            scenario_spec=scenario_spec,
            run_dir=run_dir,
            t0_epoch_ms=t0_epoch_ms,
            fps=args.fps,
            base_url=args.base_url,
            mode=args.mode,
            window_position=window_position,
            monitor_env=monitor_env,
        )
    finally:
        print("Stopping screen capture...")
        recording_meta = recorder.stop()
        final_video = run_dir / "video.mp4"
        if recorder.video_path and recorder.video_path.exists():
            if recorder.video_path != final_video:
                if final_video.exists():
                    final_video.unlink()
                recorder.video_path.rename(final_video)
            recording_meta["video"]["path"] = str(final_video.resolve())
            sidecar = final_video.with_suffix(".json")
            if sidecar.exists():
                sidecar.write_text(
                    json.dumps(recording_meta, indent=2) + "\n",
                    encoding="utf-8",
                )

    timeline = _merge_timeline(
        run_dir=run_dir,
        scenario=args.scenario,
        t0_epoch_ms=t0_epoch_ms,
        recording_started_epoch_ms=recording_started_epoch_ms,
        fps=args.fps,
        recording_meta=recording_meta,
        playwright_exit_code=playwright_code,
    )

    meta_path = run_dir / "meta.json"
    meta_path.write_text(
        json.dumps(
            {
                "scenario": args.scenario,
                "mode": args.mode,
                "monitor": args.monitor,
                "recording": recording_meta,
                "timeline_event_count": len(timeline.get("events", [])),
            },
            indent=2,
        )
        + "\n",
        encoding="utf-8",
    )

    if args.compile_segments:
        compile_script = ROOT / "scripts" / "timeline_compile.py"
        subprocess.run(
            [sys.executable, str(compile_script), str(run_dir / "timeline.json")],
            check=False,
        )

    if args.extract_frames:
        extract_script = ROOT / "scripts" / "timeline_extract_frames.py"
        subprocess.run(
            [sys.executable, str(extract_script), str(run_dir)],
            check=False,
        )

    print(f"Timeline -> {run_dir / 'timeline.json'}")
    if playwright_code != 0 and args.mode == "test":
        print(f"Playwright failed with exit code {playwright_code}")
        return playwright_code

    if playwright_code != 0 and args.mode == "record":
        print(
            f"Warning: Playwright exited {playwright_code} in record mode; "
            "video and timeline were still saved."
        )
    return 0


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--scenario",
        required=True,
        help="Scenario id, e.g. notebook/toolbar-tour",
    )
    parser.add_argument(
        "--monitor",
        type=int,
        default=DEFAULT_MONITOR,
        help=f"Monitor index for screen capture (default: {DEFAULT_MONITOR})",
    )
    parser.add_argument(
        "--fps",
        type=int,
        default=DEFAULT_FPS,
        help=f"Recording FPS (default: {DEFAULT_FPS})",
    )
    parser.add_argument(
        "--base-url",
        default=DEFAULT_BASE_URL,
        help=f"GuideAnts base URL (default: {DEFAULT_BASE_URL})",
    )
    parser.add_argument(
        "--mode",
        choices=("record", "test"),
        default="record",
        help="record: save video even on soft failures; test: fail on assertion errors",
    )
    parser.add_argument(
        "--run-dir",
        help="Optional explicit output directory under recordings/runs/",
    )
    parser.add_argument(
        "--headless",
        action="store_true",
        help="Run Playwright headless (screen recording still captures the monitor)",
    )
    parser.add_argument(
        "--compile-segments",
        action="store_true",
        help="Run timeline_compile.py after the scenario finishes",
    )
    parser.add_argument(
        "--extract-frames",
        action="store_true",
        help="Extract PNG frames at timeline events for visual review",
    )
    return parser


def main(argv: list[str] | None = None) -> int:
    parser = build_parser()
    args = parser.parse_args(argv)
    return run_walkthrough(args)


if __name__ == "__main__":
    raise SystemExit(main())
