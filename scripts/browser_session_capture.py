#!/usr/bin/env python3
"""Live browser session capture with timecode lookup."""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from scripts.browser_session.clock import parse_timecode  # noqa: E402
from scripts.browser_session.lookup import lookup_at  # noqa: E402
from scripts.screen_recorder import print_monitors  # noqa: E402


def cmd_devices(_args: argparse.Namespace) -> int:
    print("Monitors:")
    print_monitors()
    print("\nMicrophones:")
    try:
        from scripts.browser_session.mic import list_input_devices

        for device in list_input_devices():
            print(
                f"  [{device['index']}] {device['name']} "
                f"({device['channels']} ch @ {device['sample_rate']} Hz)"
            )
    except Exception as exc:  # noqa: BLE001
        print(f"  (microphone listing unavailable: {exc})")
    return 0


def cmd_start(args: argparse.Namespace) -> int:
    from scripts.browser_session.capture import run_capture_interactive_enter

    session_dir = run_capture_interactive_enter(
        monitor_index=args.monitor,
        fps=args.fps,
        urls=args.url,
        capture_mhtml=not args.no_mhtml,
        slug=args.slug,
        session_dir=Path(args.session_dir) if args.session_dir else None,
        resume_dir=Path(args.resume) if args.resume else None,
        roll_duration_sec=args.roll_duration,
        roll_size_mb=args.roll_size_mb,
    )
    print(session_dir)
    return 0


def cmd_sessions(args: argparse.Namespace) -> int:
    from scripts.browser_session.capture import SESSIONS_DIR
    from scripts.browser_session.resume import list_resumable_sessions

    rows = list_resumable_sessions(SESSIONS_DIR)
    if args.json:
        print(json.dumps(rows, indent=2))
        return 0
    if not rows:
        print("No resumable sessions found.")
        return 0
    for row in rows:
        duration_sec = int(row.get("total_duration_ms", 0)) / 1000.0
        print(
            f"{row['path']}  [{row['kind']}, {row['parts']} part(s), {duration_sec:.1f}s]"
        )
    return 0


def cmd_at(args: argparse.Namespace) -> int:
    session_dir = Path(args.session_dir)
    t_ms = parse_timecode(args.t)
    result = lookup_at(
        session_dir,
        t_ms,
        extract_frame=True,
        extract_crop=args.crop,
        time_basis=args.time_basis,
    )
    print(json.dumps(result, indent=2))
    return 0


def cmd_salvage(args: argparse.Namespace) -> int:
    from scripts.browser_session.salvage import salvage_chain, salvage_session

    target = Path(args.session_dir)
    if (target / "chain.json").is_file():
        bundle = salvage_chain(target)
    else:
        bundle = salvage_session(target)
    print(json.dumps(bundle, indent=2))
    print(f"Salvaged -> {target}")
    return 0


def cmd_validate(args: argparse.Namespace) -> int:
    from scripts.browser_session.validate import build_validation_pack

    manifest = build_validation_pack(
        Path(args.session_dir),
        pad_sec=args.pad_sec,
        time_basis=args.time_basis,
    )
    print(json.dumps(manifest, indent=2))
    print(f"Validation pack -> {Path(args.session_dir) / 'validation'}")
    return 0


def cmd_audit(args: argparse.Namespace) -> int:
    from scripts.browser_session.audit import audit_session

    report = audit_session(Path(args.session_dir))
    print(json.dumps(report.to_dict(), indent=2))
    return 0 if report.passed else 1


def cmd_visual_salvage(args: argparse.Namespace) -> int:
    from scripts.browser_session.visual_salvage import visual_salvage_session

    result = visual_salvage_session(
        Path(args.session_dir),
        min_static_sec=args.min_static_sec,
    )
    print(json.dumps(result, indent=2))
    if result.get("status") == "no_changes":
        return 0
    if result.get("status") == "visual_only_degraded":
        return 0
    if not result.get("verification_passed", False):
        return 1
    return 0


def cmd_status(args: argparse.Namespace) -> int:
    from scripts.browser_session.audit import audit_session
    from scripts.browser_session.schema import load_session

    session_dir = Path(args.session_dir)
    session = load_session(session_dir)
    audit = audit_session(session_dir)
    payload = {
        "session_id": session_dir.name,
        "status": session.get("status"),
        "media_status": (session.get("media") or {}).get("status"),
        "audit_passed": audit.passed,
        "rejection_codes": audit.rejection_codes(),
        "coverage": audit.coverage,
    }
    print(json.dumps(payload, indent=2))
    return 0 if audit.passed and session.get("status") == "complete" else 1


def cmd_preflight(args: argparse.Namespace) -> int:
    from scripts.browser_session.ffmpeg_av import FFmpegAVConfig, SupervisedFFmpegAV
    from scripts.screen_recorder import list_monitors

    monitors = list_monitors()
    monitor = next((m for m in monitors if m.index == args.monitor), None)
    if monitor is None:
        print(f"Monitor {args.monitor} not found", file=sys.stderr)
        return 1
    if not args.audio_device:
        print("--audio-device is required for preflight", file=sys.stderr)
        return 1
    import tempfile

    with tempfile.TemporaryDirectory() as tmp:
        config = FFmpegAVConfig(
            monitor_index=monitor.index,
            monitor_left=monitor.left,
            monitor_top=monitor.top,
            monitor_width=monitor.width,
            monitor_height=monitor.height,
            fps=args.fps,
            audio_device=args.audio_device,
            output_dir=Path(tmp),
        )
        av = SupervisedFFmpegAV(config)
        result = av.preflight(duration_sec=args.duration_sec)
        print(json.dumps(result, indent=2))
    return 0


def cmd_analyze_idle(args: argparse.Namespace) -> int:
    from scripts.browser_session.compact import analyze_idle

    try:
        report = analyze_idle(
            Path(args.session_dir),
            min_idle_sec=args.min_idle_sec,
            silence_enter_db=args.silence_enter_db,
            silence_exit_db=args.silence_exit_db,
            pad_sec=args.pad_sec,
            sample_hz=args.sample_hz,
        )
    except RuntimeError as exc:
        print(str(exc), file=sys.stderr)
        return 1
    print(json.dumps(report, indent=2))
    print(f"Idle report -> {Path(args.session_dir) / 'idle.json'}")
    return 0


def cmd_compact(args: argparse.Namespace) -> int:
    from scripts.browser_session.compact import compact_session

    try:
        edit_map = compact_session(Path(args.session_dir))
    except RuntimeError as exc:
        print(str(exc), file=sys.stderr)
        return 1
    print(json.dumps(edit_map, indent=2))
    if edit_map.get("status") == "no_changes":
        print("No changes — source left untouched.")
        return 0
    print(f"Compact outputs -> {Path(args.session_dir)}")
    return 0


def cmd_prune(args: argparse.Namespace) -> int:
    from scripts.browser_session.compact import prune_session

    try:
        manifest = prune_session(Path(args.session_dir))
    except RuntimeError as exc:
        print(str(exc), file=sys.stderr)
        return 1
    print(json.dumps(manifest, indent=2))
    return 0


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)

    devices = sub.add_parser("devices", help="List monitors and microphones")
    devices.set_defaults(func=cmd_devices)

    start = sub.add_parser("start", help="Start a live capture session")
    start.add_argument("--monitor", type=int, default=1, help="Monitor index (default: 1)")
    start.add_argument("--fps", type=int, default=30, help="Recording FPS (default: 30)")
    start.add_argument("--url", action="append", default=[], help="Initial tab URL (repeat for multiple tabs)")
    start.add_argument("--no-mhtml", action="store_true", help="Skip MHTML page archives")
    start.add_argument("--slug", default="session", help="Session directory slug")
    start.add_argument("--session-dir", help="Explicit session output directory")
    start.add_argument(
        "--resume",
        help="Resume capture from a previous session or chain directory",
    )
    start.add_argument("--roll-duration", type=float, help="Rotate to a new part after N seconds")
    start.add_argument("--roll-size-mb", type=float, help="Rotate to a new part after N megabytes of video")
    start.set_defaults(func=cmd_start)

    at = sub.add_parser("at", help="Lookup session state at a timecode")
    at.add_argument("session_dir", type=Path, help="Session directory or chain directory")
    at.add_argument("--t", required=True, help="Timecode (e.g. 1:23.4, 83.4, 83400)")
    at.add_argument("--crop", action="store_true", help="Also extract a cropped PNG for the foreground window")
    at.add_argument(
        "--time-basis",
        choices=["source", "compact", "chain"],
        default="source",
        help="Interpret --t as source, compact, or chain time (default: source)",
    )
    at.set_defaults(func=cmd_at)

    salvage = sub.add_parser("salvage", help="Rebuild session.json from a partially saved capture")
    salvage.add_argument("session_dir", type=Path, help="Session or chain directory")
    salvage.set_defaults(func=cmd_salvage)

    validate = sub.add_parser("validate", help="Build per-app validation crops and narration clips")
    validate.add_argument("session_dir", type=Path, help="Session directory")
    validate.add_argument(
        "--pad-sec",
        type=float,
        default=2.0,
        help="Seconds of narration before/after each app snapshot (default: 2)",
    )
    validate.add_argument(
        "--time-basis",
        choices=["source", "compact"],
        default="source",
        help="Media time basis for validation snapshots (default: source)",
    )
    validate.set_defaults(func=cmd_validate)

    analyze_idle = sub.add_parser("analyze-idle", help="Detect visually static and silent ranges")
    analyze_idle.add_argument("session_dir", type=Path, help="Session directory")
    analyze_idle.add_argument("--min-idle-sec", type=float, default=8.0)
    analyze_idle.add_argument("--silence-enter-db", type=float, default=-42.0)
    analyze_idle.add_argument("--silence-exit-db", type=float, default=-38.0)
    analyze_idle.add_argument("--pad-sec", type=float, default=0.75)
    analyze_idle.add_argument("--sample-hz", type=float, default=2.0)
    analyze_idle.set_defaults(func=cmd_analyze_idle)

    compact = sub.add_parser("compact", help="Build verified compact media and edit_map.json")
    compact.add_argument("session_dir", type=Path, help="Session directory")
    compact.set_defaults(func=cmd_compact)

    prune = sub.add_parser("prune", help="Move verified source media to .source backups")
    prune.add_argument("session_dir", type=Path, help="Session directory")
    prune.set_defaults(func=cmd_prune)

    audit = sub.add_parser("audit", help="Read-only integrity audit of a session")
    audit.add_argument("session_dir", type=Path, help="Session directory")
    audit.set_defaults(func=cmd_audit)

    visual_salvage = sub.add_parser(
        "visual-salvage",
        help="Video-only salvage for damaged sessions (no audio, visual_only_degraded)",
    )
    visual_salvage.add_argument("session_dir", type=Path, help="Session directory")
    visual_salvage.add_argument("--min-static-sec", type=float, default=8.0)
    visual_salvage.set_defaults(func=cmd_visual_salvage)

    status = sub.add_parser("status", help="Show session status and audit result")
    status.add_argument("session_dir", type=Path, help="Session directory")
    status.set_defaults(func=cmd_status)

    preflight = sub.add_parser("preflight", help="Prove monitor and microphone capture readiness")
    preflight.add_argument("--monitor", type=int, default=1)
    preflight.add_argument("--fps", type=int, default=30)
    preflight.add_argument("--audio-device", required=True, help="Exact dshow audio endpoint name")
    preflight.add_argument("--duration-sec", type=float, default=1.0)
    preflight.set_defaults(func=cmd_preflight)

    sessions = sub.add_parser("sessions", help="List sessions that can be resumed")
    sessions.add_argument("--json", action="store_true", help="Emit JSON")
    sessions.set_defaults(func=cmd_sessions)

    return parser


def main(argv: list[str] | None = None) -> int:
    parser = build_parser()
    args = parser.parse_args(argv)
    return args.func(args)


if __name__ == "__main__":
    raise SystemExit(main())
