#!/usr/bin/env python3
"""Reassemble per-scenario capture dirs into one f_%05d.png sequence (symlinks by default).
usage: assemble.py OUT_DIR SC1_DIR [SC2_DIR ...] --fps 24 [--copy]
"""
import argparse, os, sys, json

ap = argparse.ArgumentParser()
ap.add_argument("out_dir")
ap.add_argument("dirs", nargs="+")
ap.add_argument("--fps", type=float, default=24)
ap.add_argument("--copy", action="store_true")
a = ap.parse_args()

os.makedirs(a.out_dir, exist_ok=True)
i = 0
for d in a.dirs:
    files = sorted(f for f in os.listdir(d) if f.startswith("f_") and f.endswith(".png"))
    if not files:
        print(json.dumps({"ok": False, "error": f"no frames in {d}"})); sys.exit(1)
    if files[0] != "f_00000.png":
        print(json.dumps({"ok": False, "error": f"{d} does not start at f_00000.png (resume gap?)"})); sys.exit(1)
    for f in files:
        src = os.path.join(d, f)
        dst = os.path.join(a.out_dir, "f_%05d.png" % i)
        if os.path.lexists(dst): os.remove(dst)
        if a.copy:
            import shutil; shutil.copy2(src, dst)
        else:
            os.symlink(os.path.abspath(src), dst)
        i += 1
print(json.dumps({"ok": True, "total_frames": i, "duration_s": round(i / a.fps, 3)}))
