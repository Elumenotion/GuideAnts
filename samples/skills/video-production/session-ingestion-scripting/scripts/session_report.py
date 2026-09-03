#!/usr/bin/env python3
"""Phase-1 context report for a browser-session-capture session.

Reads a session folder (or unpacks a .zip safely) and writes a markdown
context report (+ optional JSON). Stdlib only, read-only on session data.

Usage:
  python3 session_report.py <session-folder|zip> [-o report.md] [--json report.json]
                            [--extract-dir DIR]

Exit codes: 0 ok, 1 missing/invalid input, 2 missing required metadata.
"""
from __future__ import annotations

import argparse
import json
import os
import sys
import zipfile
from collections import Counter


def fail(msg: str, code: int = 1) -> None:
    print(json.dumps({"ok": False, "error": msg}))
    sys.exit(code)


def safe_extract(zip_path: str, dest: str) -> str:
    """Unpack a zip with path-traversal protection. Returns the session root."""
    os.makedirs(dest, exist_ok=True)
    with zipfile.ZipFile(zip_path) as zf:
        for member in zf.namelist():
            target = os.path.realpath(os.path.join(dest, member))
            if not target.startswith(os.path.realpath(dest) + os.sep) and target != os.path.realpath(dest):
                fail(f"unsafe zip member: {member}")
            if not member.endswith("/"):
                os.makedirs(os.path.dirname(target), exist_ok=True)
                with zf.open(member) as src, open(target, "wb") as out:
                    out.write(src.read())
    # session root: the single top-level directory if exactly one, else dest
    top = sorted(d for d in os.listdir(dest) if not d.startswith("."))
    if len(top) == 1 and os.path.isdir(os.path.join(dest, top[0])):
        return os.path.join(dest, top[0])
    return dest


def load_json(path: str, required: bool = True):
    if not os.path.isfile(path):
        if required:
            fail(f"missing required file: {os.path.relpath(path)}")
        return None
    try:
        with open(path, encoding="utf-8") as f:
            return json.load(f)
    except (json.JSONDecodeError, OSError) as exc:
        fail(f"unreadable JSON {os.path.relpath(path)}: {exc}")


def count_jsonl_kinds(path: str):
    counts: Counter = Counter()
    total = 0
    if not os.path.isfile(path):
        return None, 0
    with open(path, encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if not line:
                continue
            total += 1
            try:
                counts[json.loads(line).get("kind", "?")] += 1
            except json.JSONDecodeError:
                counts["?"] += 1
    return dict(counts), total


def build_report(root: str) -> tuple[dict, str]:
    meta = load_json(os.path.join(root, "meta.json"))
    session = load_json(os.path.join(root, "session.json"))
    edit_map = load_json(os.path.join(root, "edit_map.json"), required=False)
    idle = load_json(os.path.join(root, "idle.json"), required=False)
    index = load_json(os.path.join(root, "index.json"), required=False)

    rec = (meta or {}).get("recording", {}).get("recording", {})
    video = (meta or {}).get("recording", {}).get("video", {})
    host = (meta or {}).get("recording", {}).get("host", {})

    data: dict = {
        "session_id": (meta or {}).get("session_id") or (session or {}).get("session_id"),
        "status": (meta or {}).get("status"),
        "recorded": {"started_at": rec.get("started_at"), "stopped_at": rec.get("stopped_at"),
                     "duration_seconds": rec.get("duration_seconds")},
        "source": (meta or {}).get("recording", {}).get("source"),
        "host": host,
        "video": video,
        "media": {},
        "compaction": None,
        "idle": None,
        "checkpoints": None,
        "events": None,
        "windows": None,
    }

    # media files on disk
    media = (session or {}).get("media", {}) or {}
    def local_name(wpath: str) -> str:
        # probe paths are Windows-style (D:\...) regardless of host OS
        return (wpath or "").replace("\\", "/").rstrip("/").rsplit("/", 1)[-1]

    for key, probe in (("video", media.get("video") or {}), ("narration", media.get("narration") or {})):
        wpath = probe.get("path", "")
        name = local_name(wpath)
        p = os.path.join(root, name)
        data["media"][key] = {
            "file": name or key,
            "source_ref": wpath if name and wpath.replace("\\", "/").rsplit("/", 1)[-1] == name else None,
            "bytes": os.path.getsize(p) if os.path.isfile(p) else None,
            "duration_ms": probe.get("duration_ms"),
            "sha256": probe.get("sha256"),
            "probe": {k: probe.get(k) for k in ("codec", "fps", "width", "height",
                                                "sample_rate", "channels", "pix_fmt") if probe.get(k) is not None},
        }
    compact_block = (session or {}).get("compact") or {}
    for key in ("video.compact.mp4", "narration.compact.wav"):
        p = os.path.join(root, key)
        data["media"][key] = {"bytes": os.path.getsize(p) if os.path.isfile(p) else None,
                              "referenced_by": "session.json.compact" if key in str(compact_block) or key else None}
    if compact_block:
        data["compaction"] = {
            "status": compact_block.get("status"),
            "edit_map": edit_map.get("status") if edit_map else None,
            "source_duration_ms": edit_map.get("source_duration_ms") if edit_map else None,
            "compact_duration_ms": edit_map.get("compact_duration_ms") if edit_map else None,
            "kept_ranges": len((edit_map or {}).get("kept", [])),
            "removed_ranges": len((edit_map or {}).get("removed", [])),
            "proof": ((edit_map or {}).get("proof") or {}).get("content_verified"),
            "setpts_ratio": (((edit_map or {}).get("proof") or {}).get("alignment") or {}).get("setpts_ratio"),
        }
    if idle:
        data["idle"] = {"idle_ms": idle.get("idle_ms"), "savings_pct": idle.get("savings_pct"),
                        "static_ranges": len(idle.get("static_ranges", [])),
                        "silent_ranges": len(idle.get("silent_ranges", [])),
                        "thresholds": idle.get("thresholds")}

    if index:
        cps = index.get("checkpoints", [])
        data["checkpoints"] = {
            "count": len(cps),
            "with_text": sum(1 for c in cps if c.get("has_text")),
            "with_screenshot": sum(1 for c in cps if c.get("has_screenshot")),
            "with_mhtml": sum(1 for c in cps if c.get("has_mhtml")),
            "timeline": [{k: c.get(k) for k in ("id", "t_ms", "tab_id", "foreground", "trigger", "url", "title")}
                         for c in cps],
            "text_bytes": {},
        }
        cp_dir = os.path.join(root, "checkpoints")
        for c in cps:
            t = os.path.join(cp_dir, c.get("id", ""), "text.txt")
            if os.path.isfile(t):
                data["checkpoints"]["text_bytes"][c["id"]] = os.path.getsize(t)
        for tab, info in (index.get("tabs") or {}).items():
            data["checkpoints"].setdefault("tabs", {})[tab] = {
                "last_url": info.get("last_url"), "last_title": info.get("last_title"),
                "opened_at_ms": info.get("opened_at_ms")}

    kinds, total = count_jsonl_kinds(os.path.join(root, "events.jsonl"))
    data["events"] = {"total": total, "kinds": kinds}
    act_kinds, act_total = count_jsonl_kinds(os.path.join(root, "activity.jsonl"))
    data["activity"] = {"total": act_total, "kinds": act_kinds}

    win_path = os.path.join(root, "windows.jsonl")
    if os.path.isfile(win_path):
        procs: Counter = Counter()
        n = 0
        with open(win_path, encoding="utf-8") as f:
            for line in f:
                line = line.strip()
                if not line:
                    continue
                n += 1
                try:
                    procs[json.loads(line).get("process", "?")] += 1
                except json.JSONDecodeError:
                    pass
        data["windows"] = {"records": n, "processes": dict(procs)}
    intervals_path = os.path.join(root, "windows_intervals.jsonl")
    if os.path.isfile(intervals_path):
        with open(intervals_path, encoding="utf-8") as f:
            data["windows"] = data.get("windows") or {}
            data["windows"]["intervals"] = [json.loads(l) for l in f if l.strip()]

    md = render_markdown(data, root)
    return data, md


def render_markdown(d: dict, root: str) -> str:
    L: list[str] = []
    a = L.append
    a(f"# Session report — {d.get('session_id')}")
    a("")
    a("## Identity")
    a("")
    rec, host = d["recorded"], d["host"]
    a(f"- Recorded: {rec.get('started_at')} -> {rec.get('stopped_at')} ({rec.get('duration_seconds')} s)")
    src = d.get("source") or {}
    a(f"- Monitor: index {src.get('monitor_index')} {src.get('width')}x{src.get('height')} @ {d['video'].get('fps')} fps")
    a(f"- Host: {host.get('hostname')} ({host.get('platform')}) — status: {d.get('status')}")
    a("")
    a("## Media")
    a("")
    a("| File | Bytes | Duration | Notes |")
    a("|---|---|---|---|")
    for key, m in d["media"].items():
        dur = f"{m['duration_ms']} ms" if m.get("duration_ms") else "-"
        note = ", ".join(f"{k}={v}" for k, v in (m.get("probe") or {}).items())
        size = f"{m['bytes']:,}" if m.get("bytes") else "ABSENT (expected at session root)"
        ref = f" source: `{m['source_ref']}`" if m.get("source_ref") else ""
        a(f"| {m.get('file') or key} | {size} | {dur} | {note}{ref} |")
    a("")
    c = d.get("compaction")
    if c:
        a("## Compaction (verified idle cuts)")
        a("")
        a(f"- status: {c.get('status')} (edit_map: {c.get('edit_map')}, content_verified: {c.get('proof')})")
        a(f"- {c.get('source_duration_ms')} ms -> {c.get('compact_duration_ms')} ms "
          f"({c.get('kept_ranges')} kept / {c.get('removed_ranges')} removed ranges, setpts {c.get('setpts_ratio')})")
        a("")
    i = d.get("idle")
    if i:
        a("## Idle")
        a("")
        a(f"- idle {i.get('idle_ms')} ms ({i.get('savings_pct')}% savings): "
          f"{i.get('static_ranges')} static + {i.get('silent_ranges')} silent ranges; thresholds {i.get('thresholds')}")
        a("")
    cp = d.get("checkpoints")
    if cp:
        a("## Checkpoints")
        a("")
        a(f"- {cp['count']} checkpoints: text {cp['with_text']}, screenshots {cp['with_screenshot']}, mhtml {cp['with_mhtml']}")
        for tab, info in (cp.get("tabs") or {}).items():
            a(f"- {tab}: {info.get('last_title')} — `{info.get('last_url')}`")
        a("")
        a("| id | t_ms | tab | fg | trigger | url | title |")
        a("|---|---|---|---|---|---|---|")
        for c in cp["timeline"]:
            a(f"| {c['id']} | {c['t_ms']} | {c['tab_id']} | {c['foreground']} | {c['trigger']} "
              f"| {c['url']} | {c['title']} |")
        a("")
    ev, act = d.get("events"), d.get("activity")
    if ev:
        a("## Events / activity / windows")
        a("")
        a(f"- events.jsonl: {ev['total']} events — {ev['kinds']}")
        if act:
            a(f"- activity.jsonl: {act['total']} samples — {act['kinds']}")
        w = d.get("windows") or {}
        if w.get("records") is not None:
            a(f"- windows.jsonl: {w['records']} records — processes: {w.get('processes')}")
            a("")
            a("Foreground intervals (hwnd, start_ms-end_ms):")
            a("")
            for iv in w.get("intervals", []):
                a(f"- {iv.get('hwnd')}: {iv.get('start_ms')} - {iv.get('end_ms')}")
        a("")
    a("## Bottom line")
    a("")
    a(f"Session `{d.get('session_id')}`: {rec.get('duration_seconds')} s of screen + mic capture on "
      f"{host.get('hostname')}. Use `narration.compact.wav` (with `video.compact.mp4`) for downstream "
      f"transcription and assembly; checkpoint texts are on-screen reference material.")
    a("")
    return "\n".join(L)


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("source", help="session folder or .zip")
    ap.add_argument("-o", "--out", default=None, help="markdown report path")
    ap.add_argument("--json", dest="json_out", default=None, help="JSON report path")
    ap.add_argument("--extract-dir", default=None, help="unzip target when source is a .zip")
    args = ap.parse_args()

    if not os.path.exists(args.source):
        fail(f"source not found: {args.source}")
    if args.source.lower().endswith(".zip"):
        root = safe_extract(args.source, args.extract_dir or os.path.splitext(args.source)[0] + "_extracted")
    else:
        root = args.source
    if not os.path.isfile(os.path.join(root, "meta.json")):
        fail(f"no meta.json under {root} — is this a session folder?")

    data, md = build_report(root)
    name = os.path.basename(os.path.normpath(root))
    out = args.out or f"{name}_session_report.md"
    with open(out, "w", encoding="utf-8") as f:
        f.write(md)
    if args.json_out:
        with open(args.json_out, "w", encoding="utf-8") as f:
            json.dump(data, f, indent=2)

    c = data.get("compaction") or {}
    print(json.dumps({
        "ok": True,
        "session_id": data.get("session_id"),
        "duration_seconds": data["recorded"].get("duration_seconds"),
        "compaction": f"{c.get('source_duration_ms')}ms->{c.get('compact_duration_ms')}ms ({c.get('removed_ranges')} cuts)" if c else None,
        "checkpoints": (data.get("checkpoints") or {}).get("count"),
        "report": out,
        "json": args.json_out,
    }, indent=2))


if __name__ == "__main__":
    main()
