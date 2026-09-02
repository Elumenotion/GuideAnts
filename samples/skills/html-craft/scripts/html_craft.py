#!/usr/bin/env python3
"""html_craft.py - verify single-file HTML pages with headless Chromium.

Each command drives the page EXACTLY ONCE (one context, one load) so
wall-clock animations are captured in the same state as the errors report.

Commands:
  probe   PAGE                     load + drive + report pageerrors/console/title
  shot    PAGE -o name.png         screenshot the end state
  text    PAGE --sel "#x" ...      visible text of the selected elements
  eval    PAGE "js-expression"     evaluate in the page (JSON-serialized)
  compare A.png B.png -o cmp.png   side-by-side contact sheet with labels

Drive (probe/shot/text/eval): the page is driven like a user -
  --action "press:Space" | "click:#machine" | "wait:1500"   (repeatable, in order)
  --wait MS      settle after load (default 700)
  --settle MS    wait after each action (default 1200)
  --viewport WxH (default 1280x800)
  --full-page    shot only: capture the full scrollable page
  --timeout MS   navigation timeout (default 60000)
  --browser PATH chromium binary (default: $HTML_CRAFT_BROWSER, else auto-find)

Outputs one JSON object per run; `pageerrors` non-empty = verification failed.
Stdlib + sandbox-venv playwright only.
"""
from __future__ import annotations

import argparse
import json
import os
import sys

from preflight import find_browser  # same discovery rules, single source


def _parse_viewport(s: str) -> dict:
    w, h = s.lower().split("x", 1)
    return {"width": int(w), "height": int(h)}


def _drive(page, actions: list[str], settle_ms: int) -> None:
    for act in actions:
        if not act:
            continue
        if ":" not in act:
            raise SystemExit(f"invalid --action {act!r} (want press:KEY | click:SEL | wait:MS)")
        verb, arg = act.split(":", 1)
        verb = verb.strip().lower()
        arg = arg.strip()
        if verb == "press":
            page.keyboard.press(arg)
        elif verb == "click":
            page.click(arg)
        elif verb == "wait":
            page.wait_for_timeout(int(float(arg)))
        else:
            raise SystemExit(f"unknown --action verb {verb!r}")
        if verb != "wait":
            page.wait_for_timeout(settle_ms)


def _resolve(page_file: str) -> str:
    path = os.path.abspath(page_file)
    if not os.path.exists(path):
        raise SystemExit(f"file not found: {path} (pass the path as the sandbox CWD sees it)")
    return path


def _emit(obj: dict) -> int:
    print(json.dumps(obj, indent=2))
    failed = bool(obj.get("pageerrors"))
    print(f"VERDICT: {'FAIL (pageerrors)' if failed else 'ok'}", file=sys.stderr)
    return 1 if failed else 0


def _do_page_command(args, executable: str, url: str) -> dict:
    from playwright.sync_api import sync_playwright

    info: dict = {"command": args.command, "url": url, "browser": executable,
                  "viewport": _parse_viewport(args.viewport)}
    with sync_playwright() as p:
        browser = p.chromium.launch(
            headless=True,
            executable_path=executable,
            args=["--no-sandbox", "--disable-dev-shm-usage"],
        )
        page = browser.new_page(viewport=info["viewport"])
        pageerrors: list[str] = []
        console_errors: list[str] = []
        page.on("pageerror", lambda e: pageerrors.append(str(e)))
        page.on("console", lambda m: console_errors.append(m.text) if m.type == "error" else None)
        try:
            page.goto(url, timeout=args.timeout)
            page.wait_for_timeout(args.wait)
            _drive(page, args.action, args.settle)

            info["title"] = page.title()
            if args.command == "shot":
                if not args.out:
                    raise SystemExit("shot needs -o name.png")
                page.screenshot(path=args.out, full_page=args.full_page)
                info["shot"] = os.path.abspath(args.out)
            elif args.command == "text":
                info["texts"] = {
                    sel: page.locator(sel).first.inner_text()
                    for sel in (args.sel or ["body"])
                }
            elif args.command == "eval":
                expr = args.second
                wrapped = expr.strip()
                if not wrapped.startswith(("(", "[")):
                    wrapped = f"() => {{ return ({wrapped}); }}"
                info["value"] = page.evaluate(wrapped)
        finally:
            browser.close()
        info["pageerrors"] = pageerrors
        info["console_errors"] = console_errors
    return info


def _do_compare(args) -> None:
    try:
        from PIL import Image, ImageDraw
    except ImportError:
        raise SystemExit("compare needs Pillow (sandbox venv): pip install pillow")
    a = Image.open(os.path.abspath(args.target)).convert("RGB")
    b = Image.open(os.path.abspath(args.second)).convert("RGB")
    scale = 0.55
    a.thumbnail((int(a.width * scale), int(a.height * scale)))
    b.thumbnail((int(b.width * scale), int(b.height * scale)))
    label_h, gap = 28, 16
    w = a.width + b.width + gap
    h = max(a.height, b.height) + label_h
    canvas = Image.new("RGB", (w, h), (20, 24, 32))
    draw = ImageDraw.Draw(canvas)
    draw.text((8, 8), "BEFORE: " + os.path.basename(args.target), fill=(220, 226, 240))
    draw.text((a.width + gap + 8, 8), "AFTER: " + os.path.basename(args.second), fill=(220, 226, 240))
    canvas.paste(a, (0, label_h))
    canvas.paste(b, (a.width + gap, label_h))
    out = os.path.abspath(args.out)
    canvas.save(out)
    print(json.dumps({"command": "compare", "out": out, "size": [w, h]}))


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("command", choices=["probe", "shot", "text", "eval", "compare"])
    ap.add_argument("target")
    ap.add_argument("second", nargs="?", default=None, help="compare: B.png")
    ap.add_argument("-o", "--out", default=None, help="output path (bare filename in CWD)")
    ap.add_argument("--sel", action="append", default=[], help="selector (text; repeatable)")
    ap.add_argument("--action", action="append", default=[], help="press:KEY | click:SEL | wait:MS")
    ap.add_argument("--wait", type=int, default=700, help="settle ms after load")
    ap.add_argument("--settle", type=int, default=1200, help="settle ms after each action")
    ap.add_argument("--viewport", default="1280x800")
    ap.add_argument("--full-page", action="store_true")
    ap.add_argument("--timeout", type=int, default=60000)
    ap.add_argument("--browser", default=None, help="chromium binary path")
    args = ap.parse_args()

    if args.command == "compare":
        if not args.second or not args.out:
            raise SystemExit("compare needs: A B -o out.png")
        _do_compare(args)
        return 0

    if args.command == "eval" and not args.second:
        raise SystemExit('eval needs a JS expression: eval PAGE "document.title"')

    page_file = _resolve(args.target)
    executable = args.browser or os.environ.get("HTML_CRAFT_BROWSER") or None
    if executable is None:
        executable, _ = find_browser()
    if executable is None:
        print(json.dumps({"open": False, "blockers": ["no chromium binary found - set HTML_CRAFT_BROWSER"]}))
        return 1

    info = _do_page_command(args, executable, "file://" + page_file)
    return _emit(info)


if __name__ == "__main__":
    sys.exit(main())
