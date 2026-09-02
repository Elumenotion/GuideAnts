#!/usr/bin/env python3
"""Capability preflight for html-craft.

Verifies the full render chain in the sandbox - python playwright import,
a launchable Chromium binary, a real page load + render + screenshot - and
prints one JSON verdict:

  {"scenario": "probe", "open": bool, "blockers": [...], "warnings": [...],
   "evidence": {...}}

open == True only after a 320x200 screenshot of a known-good data: page
comes back with valid PNG bytes. No network, no GPU, no service.
"""
from __future__ import annotations

import json
import os
import sys
import tempfile

SCENARIOS = ("probe",)

# Roots scanned in order; first launchable chromium wins. Override with
# HTML_CRAFT_BROWSER or --browser.
BROWSER_ROOTS = (
    os.environ.get("HTML_CRAFT_BROWSER", ""),
    os.path.expanduser("~/.cache/ms-playwright"),
    "/ms-playwright",
    os.path.expanduser("~/.cache/puppeteer"),
    "/opt/playwright",
    "/usr/lib/chromium",
    "/usr/lib/chromium-browser",
    "/usr/lib/google-chrome",
    "/usr/bin",
    "/usr/local/bin",
)

BIN_NAMES = ("chrome", "headless_shell", "chrome-headless-shell", "chromium", "chromium-browser", "google-chrome")


def find_browser() -> tuple[str | None, list[str]]:
    evidence: list[str] = []
    for root in BROWSER_ROOTS:
        if not root or not os.path.isdir(root):
            continue
        for dirpath, _dirnames, filenames in os.walk(root):
            depth = dirpath[len(root):].count(os.sep)
            if depth > 4:
                dirnames[:] = []
                continue
            for name in filenames:
                if name in BIN_NAMES and os.access(os.path.join(dirpath, name), os.X_OK):
                    evidence.append(f"candidate: {os.path.join(dirpath, name)}")
                    return os.path.join(dirpath, name), evidence
        evidence.append(f"root scanned: {root}")
    return None, evidence


def _try_launch(executable: str) -> tuple[bool, str, bytes | None]:
    from playwright.sync_api import sync_playwright

    with sync_playwright() as p:
        browser = p.chromium.launch(
            headless=True,
            executable_path=executable,
            args=["--no-sandbox", "--disable-dev-shm-usage"],
        )
        page = browser.new_page(viewport={"width": 320, "height": 200})
        page.goto("data:text/html,<html><body style='margin:0;background:%23233'><h1>ok</h1></body></html>")
        page.wait_for_timeout(250)
        png = page.screenshot()
        browser.close()
    ok = bool(png) and png[:8] == b"\x89PNG\r\n\x1a\n"
    return ok, "rendered" if ok else "screenshot missing or not PNG", png


def run_preflight(scenario: str) -> dict:
    blockers: list[str] = []
    warnings: list[str] = []
    evidence: dict = {}

    try:
        import playwright  # noqa: F401
        import playwright.sync_api  # noqa: F401
        evidence["playwright"] = "installed"
    except Exception as exc:  # pragma: no cover
        blockers.append(f"playwright not importable: {type(exc).__name__}: {exc}")
        return _verdict(scenario, blockers, warnings, evidence)

    browser, evidence["browser_search"] = find_browser()
    if browser is None:
        blockers.append(
            "no launchable chromium binary found under scanned roots - set HTML_CRAFT_BROWSER"
        )
        return _verdict(scenario, blockers, warnings, evidence)
    evidence["browser"] = browser

    try:
        ok, msg, png = _try_launch(browser)
        evidence["render"] = msg
        if png:
            with tempfile.NamedTemporaryFile(suffix=".png", delete=False) as tf:
                tf.write(png)
                evidence["sample_png_bytes"] = len(png)
        if not ok:
            blockers.append(f"render check failed: {msg}")
    except Exception as exc:
        blockers.append(f"chromium launch/render failed: {type(exc).__name__}: {exc}")
    return _verdict(scenario, blockers, warnings, evidence)


def _verdict(scenario: str, blockers: list[str], warnings: list[str], evidence: dict) -> dict:
    return {
        "scenario": scenario,
        "open": not blockers,
        "blockers": blockers,
        "warnings": warnings,
        "evidence": evidence,
    }


def main() -> int:
    import argparse

    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("scenario", choices=SCENARIOS, nargs="?", default="probe")
    args = ap.parse_args()
    print(json.dumps(run_preflight(args.scenario), indent=2))
    return 0


if __name__ == "__main__":
    sys.exit(main())
