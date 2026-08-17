#!/usr/bin/env python3
"""Control client for an active browser session capture."""

from __future__ import annotations

import argparse
import json
import sys
import urllib.error
import urllib.request
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from scripts.browser_session.capture import SESSIONS_DIR  # noqa: E402
from scripts.browser_session.control_server import (  # noqa: E402
    find_active_control_descriptor,
    load_control_descriptor,
)


def _request(descriptor: dict[str, Any], action: str, params: dict[str, Any] | None = None) -> dict[str, Any]:
    payload = {
        "token": descriptor["token"],
        "action": action,
        "params": params or {},
    }
    body = json.dumps(payload).encode("utf-8")
    url = f"http://{descriptor['host']}:{descriptor['port']}/command"
    request = urllib.request.Request(
        url,
        data=body,
        headers={"Content-Type": "application/json"},
        method="POST",
    )
    try:
        with urllib.request.urlopen(request, timeout=90) as response:
            data = json.loads(response.read().decode("utf-8"))
    except urllib.error.HTTPError as exc:
        raw = exc.read().decode("utf-8")
        try:
            data = json.loads(raw)
        except json.JSONDecodeError:
            raise RuntimeError(f"control request failed: HTTP {exc.code}: {raw}") from exc
        raise RuntimeError(data.get("error") or f"control request failed: HTTP {exc.code}") from exc
    except urllib.error.URLError as exc:
        raise RuntimeError(f"control endpoint unreachable: {exc.reason}") from exc

    if not data.get("ok"):
        raise RuntimeError(data.get("error") or "control request failed")
    return data.get("result") or {}


def _resolve_descriptor(args: argparse.Namespace) -> dict[str, Any]:
    if args.control:
        return load_control_descriptor(Path(args.control))
    if args.session_dir:
        from scripts.browser_session.control_server import control_descriptor_path

        path = control_descriptor_path(Path(args.session_dir))
        if not path.is_file():
            raise RuntimeError(f"no active control descriptor at {path}")
        return load_control_descriptor(path)
    path = find_active_control_descriptor(SESSIONS_DIR)
    if path is None:
        raise RuntimeError("no active capture session found (start capture first)")
    return load_control_descriptor(path)


def cmd_status(args: argparse.Namespace) -> int:
    descriptor = _resolve_descriptor(args)
    result = _request(descriptor, "status")
    if args.json:
        print(json.dumps(result, indent=2))
        return 0
    active = result.get("active") or {}
    print(f"Session: {result.get('session_dir')}")
    if active:
        print(f"Active tab: {active.get('title')} ({active.get('url')})")
    for row in result.get("tabs", []):
        marker = "*" if row.get("focused") else " "
        print(f"{marker} [{row.get('index')}] {row.get('title')} - {row.get('url')}")
    return 0


def cmd_goto(args: argparse.Namespace) -> int:
    descriptor = _resolve_descriptor(args)
    params: dict[str, Any] = {"url": args.url}
    if args.tab is not None:
        params["tab"] = args.tab
    result = _request(descriptor, "goto", params)
    print(json.dumps(result, indent=2) if args.json else f"{result.get('title')} ({result.get('url')})")
    return 0


def cmd_tab_list(args: argparse.Namespace) -> int:
    descriptor = _resolve_descriptor(args)
    result = _request(descriptor, "tab-list")
    if args.json:
        print(json.dumps(result, indent=2))
        return 0
    for row in result.get("tabs", []):
        marker = "*" if row.get("focused") else " "
        print(f"{marker} [{row.get('index')}] {row.get('title')} - {row.get('url')}")
    return 0


def cmd_tab_new(args: argparse.Namespace) -> int:
    descriptor = _resolve_descriptor(args)
    params = {"url": args.url} if args.url else {}
    result = _request(descriptor, "tab-new", params)
    print(json.dumps(result, indent=2) if args.json else json.dumps(result.get("active"), indent=2))
    return 0


def cmd_tab_select(args: argparse.Namespace) -> int:
    descriptor = _resolve_descriptor(args)
    result = _request(descriptor, "tab-select", {"tab": args.tab})
    print(json.dumps(result, indent=2) if args.json else f"{result.get('title')} ({result.get('url')})")
    return 0


def cmd_tab_close(args: argparse.Namespace) -> int:
    descriptor = _resolve_descriptor(args)
    params = {"tab": args.tab} if args.tab is not None else {}
    result = _request(descriptor, "tab-close", params)
    print(json.dumps(result, indent=2) if args.json else f"tabs remaining: {len(result.get('tabs', []))}")
    return 0


def cmd_snapshot(args: argparse.Namespace) -> int:
    descriptor = _resolve_descriptor(args)
    params: dict[str, Any] = {"screenshot": args.screenshot}
    if args.tab is not None:
        params["tab"] = args.tab
    result = _request(descriptor, "snapshot", params)
    print(json.dumps(result, indent=2))
    return 0


def cmd_click(args: argparse.Namespace) -> int:
    descriptor = _resolve_descriptor(args)
    params: dict[str, Any] = {"selector": args.selector}
    if args.tab is not None:
        params["tab"] = args.tab
    result = _request(descriptor, "click", params)
    print(json.dumps(result, indent=2) if args.json else f"{result.get('title')} ({result.get('url')})")
    return 0


def cmd_fill(args: argparse.Namespace) -> int:
    descriptor = _resolve_descriptor(args)
    params: dict[str, Any] = {"selector": args.selector, "value": args.value}
    if args.tab is not None:
        params["tab"] = args.tab
    result = _request(descriptor, "fill", params)
    print(json.dumps(result, indent=2) if args.json else f"{result.get('title')} ({result.get('url')})")
    return 0


def cmd_press(args: argparse.Namespace) -> int:
    descriptor = _resolve_descriptor(args)
    params: dict[str, Any] = {"key": args.key}
    if args.tab is not None:
        params["tab"] = args.tab
    result = _request(descriptor, "press", params)
    print(json.dumps(result, indent=2) if args.json else f"{result.get('title')} ({result.get('url')})")
    return 0


def cmd_checkpoint(args: argparse.Namespace) -> int:
    descriptor = _resolve_descriptor(args)
    params = {"tab": args.tab} if args.tab is not None else {}
    result = _request(descriptor, "checkpoint", params)
    print(json.dumps(result, indent=2) if args.json else f"checkpoint: {result.get('title')} ({result.get('url')})")
    return 0


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    common = argparse.ArgumentParser(add_help=False)
    common.add_argument("--session-dir", type=Path, help="Active capture part directory")
    common.add_argument("--control", type=Path, help="Path to control.json")
    common.add_argument("--json", action="store_true", help="Emit JSON output")

    sub = parser.add_subparsers(dest="command", required=True)

    status = sub.add_parser("status", parents=[common], help="Show active capture session and tabs")
    status.set_defaults(func=cmd_status)

    goto = sub.add_parser("goto", parents=[common], help="Navigate the focused or selected tab")
    goto.add_argument("url")
    goto.add_argument("--tab", type=int)
    goto.set_defaults(func=cmd_goto)

    tab_list = sub.add_parser("tab-list", parents=[common], help="List open tabs")
    tab_list.set_defaults(func=cmd_tab_list)

    tab_new = sub.add_parser("tab-new", parents=[common], help="Open a new tab")
    tab_new.add_argument("url", nargs="?")
    tab_new.set_defaults(func=cmd_tab_new)

    tab_select = sub.add_parser("tab-select", parents=[common], help="Focus a tab by index")
    tab_select.add_argument("tab", type=int)
    tab_select.set_defaults(func=cmd_tab_select)

    tab_close = sub.add_parser("tab-close", parents=[common], help="Close a tab")
    tab_close.add_argument("tab", type=int, nargs="?")
    tab_close.set_defaults(func=cmd_tab_close)

    snapshot = sub.add_parser("snapshot", parents=[common], help="Return URL/title for a tab")
    snapshot.add_argument("--tab", type=int)
    snapshot.add_argument("--screenshot", action="store_true")
    snapshot.set_defaults(func=cmd_snapshot)

    click = sub.add_parser("click", parents=[common], help="Click a selector in the active tab")
    click.add_argument("selector")
    click.add_argument("--tab", type=int)
    click.set_defaults(func=cmd_click)

    fill = sub.add_parser("fill", parents=[common], help="Fill an input in the active tab")
    fill.add_argument("selector")
    fill.add_argument("value")
    fill.add_argument("--tab", type=int)
    fill.set_defaults(func=cmd_fill)

    press = sub.add_parser("press", parents=[common], help="Press a key in the active tab")
    press.add_argument("key")
    press.add_argument("--tab", type=int)
    press.set_defaults(func=cmd_press)

    checkpoint = sub.add_parser("checkpoint", parents=[common], help="Write a manual browser checkpoint")
    checkpoint.add_argument("--tab", type=int)
    checkpoint.set_defaults(func=cmd_checkpoint)

    stop = sub.add_parser("stop", parents=[common], help="Stop the active capture session")
    stop.set_defaults(func=cmd_stop)

    return parser


def cmd_stop(args: argparse.Namespace) -> int:
    descriptor = _resolve_descriptor(args)
    result = _request(descriptor, "stop")
    print(json.dumps(result, indent=2) if args.json else "capture stopping")
    return 0


def main(argv: list[str] | None = None) -> int:
    parser = build_parser()
    args = parser.parse_args(argv)
    try:
        return args.func(args)
    except RuntimeError as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
