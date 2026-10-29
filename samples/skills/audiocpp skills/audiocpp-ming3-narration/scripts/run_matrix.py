#!/usr/bin/env python3
"""Render a control matrix with ming3_say.py: baseline, one cell per emotion, one per speed.

Resumable: cells whose WAV already exists are skipped.

Each cell is written as an ANNOTATED SCRIPT (English directives) so the transcript
format itself is exercised, then synthesized by ming3_say.py.
"""
import argparse, json, os, subprocess, sys, time

TOOL = os.path.join(os.path.dirname(os.path.abspath(__file__)), "ming3_say.py")
EMOTIONS = ["neutral", "happy", "sad", "angry", "fearful", "disgusted", "surprised"]
SPEEDS = ["slow", "medium", "fast"]


def paragraphs(path):
    text = open(path, encoding="utf-8").read()
    return [p.strip() for p in text.split("\n\n") if p.strip()]


def write_cell(dirpath, name, paras, directive):
    lines = []
    for i, p in enumerate(paras):
        if directive:
            lines.append(directive)
        lines.append(p)
        lines.append("")
    path = os.path.join(dirpath, f"{name}.txt")
    open(path, "w", encoding="utf-8").write("\n".join(lines))
    return path


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--transcript", required=True)
    ap.add_argument("--outdir", default="matrix")
    ap.add_argument("--engine-url", default="http://127.0.0.1:18099")
    ap.add_argument("--voice", default="doug")
    ap.add_argument("--only", default=None, help="comma list of cell names to run")
    ap.add_argument("--chunk-chars", type=int, default=180)
    args = ap.parse_args()

    os.makedirs(args.outdir, exist_ok=True)
    paras = paragraphs(args.transcript)
    cells = [("baseline", None)]
    cells += [(f"emotion_{e}", f"[emotion: {e}]") for e in EMOTIONS]
    cells += [(f"speed_{s}", f"[speed: {s}]") for s in SPEEDS]
    if args.only:
        keep = set(args.only.split(","))
        cells = [c for c in cells if c[0] in keep]

    progress = os.path.join(args.outdir, "progress.json")
    done = []
    for name, directive in cells:
        wav = os.path.join(args.outdir, name + ".wav")
        if os.path.exists(wav) and os.path.getsize(wav) > 100000:
            print(f"skip {name} (exists)", flush=True)
            done.append({"cell": name, "state": "skipped"})
            continue
        script = write_cell(args.outdir, name, paras, directive)
        t0 = time.time()
        print(f"RUN {name}  directive={directive!r}", flush=True)
        proc = subprocess.run(
            [sys.executable, TOOL, script, "-o", wav, "--engine-url", args.engine_url,
             "--voice", args.voice, "--chunk-chars", str(args.chunk_chars),
             "--report", wav + ".report.json"],
            capture_output=True, text=True)
        entry = {"cell": name, "directive": directive, "seconds": round(time.time() - t0, 1),
                 "rc": proc.returncode, "stderr_tail": proc.stderr.strip().splitlines()[-3:]}
        if proc.returncode == 0:
            rep = json.load(open(wav + ".report.json"))
            sp = [x for x in rep["sections"] if x["kind"] == "speech"]
            entry.update(duration_s=rep["duration_seconds"], calls=len(sp),
                         silent=sum(1 for x in sp if x.get("silent")),
                         controls=sorted({k for x in sp for k in x["english"]}))
        done.append(entry)
        json.dump(done, open(progress, "w"), indent=2)
        print("   -> " + json.dumps({k: entry[k] for k in entry if k != "stderr_tail"}), flush=True)
        for line in entry["stderr_tail"]:
            print("   " + line, flush=True)
    print("ALL DONE", flush=True)


if __name__ == "__main__":
    main()
