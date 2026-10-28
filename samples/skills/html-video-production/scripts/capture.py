#!/usr/bin/env python3
"""capture.py - frame-by-frame capture of pure f(T) HTML animations.

Mirrors capture.js logic but uses Python playwright (the only playwright
available in the sandbox environment).

Usage:
  python3 capture.py --plan plan.json [--test N]

Plan JSON format (same as capture.js):
  {fps, viewport:[w,h], hide:[ids], wait_ms, status, scenarios:[{name,html,t0,t1,out}]}

Resume: existing frame files (size > 0) are skipped. Status file written throughout.
"""
import argparse
import json
import os
import sys
import time

def load_playwright():
    """Import Python playwright - always available in the sandbox."""
    try:
        from playwright.sync_api import sync_playwright
        return sync_playwright
    except ImportError as e:
        print(json.dumps({"ok": False, "error": f"playwright not importable: {e}"}))
        sys.exit(2)


def find_chromium():
    """Find the chromium binary. Checks known locations in priority order."""
    candidates = [
        "/ms-playwright/chromium-1234/chrome-linux64/chrome",
        "/ms-playwright/chromium-1234/chrome-linux/chrome",
        os.environ.get("HTML_CRAFT_BROWSER", ""),
    ]
    for path in candidates:
        if path and os.path.isfile(path) and os.access(path, os.X_OK):
            return path
    # Fallback: let playwright auto-discover
    return None


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--plan", required=True, help="plan.json path")
    ap.add_argument("--test", type=int, default=0, help="speed test: render N frames of scenario 0")
    args = ap.parse_args()

    with open(args.plan) as f:
        plan = json.load(f)

    FPS = plan.get("fps", 24)
    W, H = plan.get("viewport", [1280, 720])
    HIDE = plan.get("hide", [])
    WAIT_MS = plan.get("wait_ms", 800) if plan.get("wait_ms") is not None else 800
    STATUS_FILE = plan.get("status", "capture_status.json")

    # Calculate total frames across all scenarios (for ETA)
    total_frames = sum(round((s["t1"] - s["t0"]) * FPS) for s in plan["scenarios"])

    st0 = time.time()

    def write_status(**kwargs):
        """Write status file with phase, progress, rate, ETA."""
        elapsed = (time.time() - st0) / 1000
        data = {
            "elapsed_s": round(elapsed, 1),
            **kwargs,
        }
        if "rate_fps" in kwargs and kwargs["rate_fps"] > 0:
            remaining = (data.get("total", total_frames) - data.get("done", 0)) / kwargs["rate_fps"]
            data["eta_s"] = round(remaining)
        with open(STATUS_FILE, "w") as f:
            json.dump(data, f)

    sp = load_playwright()
    with sp() as p:
        browser_kwargs = {"headless": True}
        exe = find_chromium()
        if exe:
            browser_kwargs["executable_path"] = exe
        browser = p.chromium.launch(**browser_kwargs)
        page = browser.new_page(viewport={"width": W, "height": H})

        # Build jobs: one per scenario (or just speed test)
        jobs = []
        for k, s in enumerate(plan["scenarios"]):
            n = round((s["t1"] - s["t0"]) * FPS)
            jobs.append({**s, "n": n, "first": k == 0})

        if args.test > 0:
            jobs_to_run = [{**jobs[0], "n": args.test, "out": "speedtest"}]
        else:
            jobs_to_run = jobs

        for job in jobs_to_run:
            out_dir = job["out"]
            os.makedirs(out_dir, exist_ok=True)
            n = job["n"]

            # Navigate to page with initial T
            abs_html = os.path.abspath(job["html"])
            page.goto(f"file://{abs_html}?t={job['t0']}")
            page.wait_for_timeout(WAIT_MS)

            # Hide UI elements (transport, dbg, etc.)
            if HIDE:
                hide_expr = """(ids) => {
                    for (const id of ids) {
                        const el = document.getElementById(id);
                        if (el) el.style.display = 'none';
                    }
                }"""
                page.evaluate(hide_expr, HIDE)

            phase_label = "speedtest" if args.test > 0 else out_dir
            write_status(phase=phase_label, done=0, total=n)

            done = 0
            for i in range(n):
                frame_path = os.path.join(out_dir, f"f_{i:05d}.png")
                # Resume: skip existing frames
                if os.path.exists(frame_path) and os.path.getsize(frame_path) > 0:
                    done = i + 1
                    continue

                t = job["t0"] + i / FPS
                page.evaluate("(tt) => { T = tt; render(); }", t)
                page.screenshot(path=frame_path)
                done = i + 1

                # Write status every 48 frames
                if i % 48 == 0:
                    rate = done / ((time.time() - st0) / 1000) if (time.time() - st0) > 0 else 0
                    write_status(phase=phase_label, done=done, total=n, rate_fps=round(rate, 1))

            write_status(phase=phase_label, done=done, total=n)

        browser.close()

    elapsed = (time.time() - st0) / 1000
    if args.test > 0:
        rate = args.test / elapsed if elapsed > 0 else 0
        eta = total_frames / rate if rate > 0 else 0
        print(json.dumps({
            "ok": True,
            "frames": args.test,
            "seconds": round(elapsed, 1),
            "rate_fps": round(rate, 1),
            "total_frames": total_frames,
            "eta_s": round(eta),
        }))
    else:
        write_status(phase="complete", done=total_frames, total=total_frames, eta=0)
        print(json.dumps({"ok": True, "frames": total_frames, "seconds": round(elapsed)}))


if __name__ == "__main__":
    main()
