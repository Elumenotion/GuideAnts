#!/usr/bin/env python3
"""Preflight + final verification for html-video-production.
usage:
  verify.py preflight --plan plan.json
  verify.py final --mp4 out.mp4 --plan plan.json [--at 305] [--at 308]
"""
import argparse, json, os, shutil, subprocess, sys

def run(cmd, **kw):
    return subprocess.run(cmd, capture_output=True, text=True, **kw)

def plan_of(path):
    p = json.load(open(path))
    fps = p.get("fps", 24)
    out = {"fps": fps, "viewport": p.get("viewport", [1280, 720]),
           "total_frames": 0, "per_scenario": []}
    acc = 0
    for s in p["scenarios"]:
        n = int(round((s["t1"] - s["t0"]) * fps))
        out["per_scenario"].append({"name": s["name"], "frames": n, "boundary_s": round(acc / fps, 3)})
        acc += n
    out["total_frames"] = acc
    out["duration_s"] = round(acc / fps, 3)
    return p, out

def preflight(args):
    p, exp = plan_of(args.plan)
    blockers, warnings = [], []
    for tool in ("ffmpeg", "ffprobe"):
        if shutil.which(tool) is None: blockers.append(f"{tool} missing")
    # Check Python playwright (the only playwright available in this environment)
    try:
        import playwright  # noqa: F401
        import playwright.sync_api  # noqa: F401
    except ImportError as e:
        blockers.append(f"playwright not importable - do NOT install; report the blocker: {e}")
    wav = p.get("wav")
    if wav and not os.path.exists(wav): blockers.append(f"missing {wav}")
    for s in p["scenarios"]:
        if not os.path.exists(s["html"]): blockers.append(f"missing {s['html']}")
    checks = {"ok": not blockers, "blockers": blockers, "warnings": warnings, "expected": exp}
    print(json.dumps(checks, indent=1)); sys.exit(0 if checks["ok"] else 1)

def band_check(gray, h, w):
    band = gray[int(h * 0.93):, :].astype("float32")
    above = gray[int(h * 0.86):int(h * 0.93), :].astype("float32")
    delta = abs(float(band.mean()) - float(above.mean()))
    return delta, float(band.std()), float(above.std())

def final(args):
    p, exp = plan_of(args.plan)
    mp4 = args.mp4
    r = run(["ffprobe", "-v", "error", "-select_streams", "v:0",
             "-show_entries", "stream=codec_name,width,height,r_frame_rate,nb_frames",
             "-show_entries", "format=duration", "-of", "json", mp4])
    d = json.loads(r.stdout)
    v = d["streams"][0]
    ra = run(["ffprobe", "-v", "error", "-select_streams", "a:0",
              "-show_entries", "stream=codec_name", "-of", "json", mp4])
    has_audio = bool(json.loads(ra.stdout).get("streams"))
    dur = float(d["format"]["duration"])
    fps_num = eval(v["r_frame_rate"])  # "24/1"
    checks = [
        {"name": "codec_h264", "pass": v["codec_name"] == "h264", "evidence": v["codec_name"]},
        {"name": "resolution", "pass": [v["width"], v["height"]] == list(exp["viewport"]),
         "evidence": f'{v["width"]}x{v["height"]} vs {exp["viewport"]}'}
        , {"name": "fps", "pass": abs(fps_num - exp["fps"]) < 0.01, "evidence": v["r_frame_rate"]},
        {"name": "frame_count", "pass": int(v["nb_frames"]) == exp["total_frames"],
         "evidence": f'{v["nb_frames"]} vs {exp["total_frames"]}'}
        , {"name": "duration", "pass": abs(dur - exp["duration_s"]) < 2 / exp["fps"],
         "evidence": f'{dur:.3f}s vs {exp["duration_s"]}s'}
        , {"name": "audio_aac", "pass": has_audio, "evidence": "aac stream present" if has_audio else "no audio stream"}
    ]
    # spot frames: t=0, each boundary, each boundary+2.5s, last, plus --at
    cuts = [s["boundary_s"] for s in exp["per_scenario"][1:]]
    ts = [0.0] + cuts + [c + 2.5 for c in cuts] + [dur - 1 / exp["fps"]] + list(args.at)
    ts = sorted(dict.fromkeys(round(t, 3) for t in ts if 0 <= t < dur))
    spots = []
    for t in ts:
        label = "verify_%06.2fs.jpg" % t
        delta, bstd, astd = (0, 0, 0)
        run(["ffmpeg", "-y", "-ss", str(t), "-i", mp4, "-frames:v", "1", "-q:v", "2", label])
        spot = {"t": t, "file": label}
        try:
            from PIL import Image
            import numpy as np
            gray = np.asarray(Image.open(label).convert("L"), dtype="float32")
            delta, bstd, astd = band_check(gray, *gray.shape)
        except Exception as e:
            spot["band_check_unavailable"] = str(e)
        spot.update({
            "bottom_band_delta": round(delta, 1),
            "band_std": round(bstd, 1),
            "above_std": round(astd, 1),
            "band_flag": delta > 15,
        })
        spots.append(spot)
    band_warn = [s for s in spots if s["band_flag"]]
    ok = all(c["pass"] for c in checks) and not band_warn
    print(json.dumps({"ok": ok, "checks": checks, "spot": spots,
                      "expected": exp}, indent=1))
    sys.exit(0 if ok else 1)

ap = argparse.ArgumentParser()
sub = ap.add_subparsers(dest="cmd", required=True)
pp = sub.add_parser("preflight"); pp.add_argument("--plan", required=True)
pf = sub.add_parser("final"); pf.add_argument("--mp4", required=True)
pf.add_argument("--plan", required=True); pf.add_argument("--at", type=float, action="append", default=[])
a = ap.parse_args()
{"preflight": preflight, "final": final}[a.cmd](a)
