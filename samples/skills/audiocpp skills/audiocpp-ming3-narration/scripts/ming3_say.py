#!/usr/bin/env python3
"""ming3_say.py -- free-standing Ming-3 TTS narrator with English script annotations.

Independent of the audiocpp-tts-controls scripts: stdlib only, talks straight to a
ming3 engine's /v1/audio/speech. Nothing here imports or writes to another skill.

WHY IT IS STRUCTURED THIS WAY
  * One engine call per text chunk, each chunk sent with raw_text=true, so the engine
    never runs its own multi-chunk loop on a call. That removes both known
    chunk-to-chunk effects: the freeform/voice_design "reuse chunk 1 as the reference
    for later chunks" path, and the zero-history drift of the structured templates.
    Every call is conditioned only on the reference voice -> timbre cannot drift.
  * Controls are set per SECTION, so one script can change emotion and tempo midway.
  * Silence at blank-line boundaries is inserted by this tool (the engine has no
    concept of a line break as a pause unit).
  * Script vocabulary is ENGLISH ONLY. English words are mapped to the model's trained
    control tokens at the last moment; the mapping is written to the report file, never
    required in a script.

SCRIPT FORMAT
  Paragraphs are separated by BLANK LINES. A directive line of [key: value] pairs
  immediately above a paragraph sets that paragraph's controls.

    [emotion: happy]
    First paragraph, spoken happily.

    [speed: fast] [volume: high]
    Second paragraph, fast and loud.

    Unannotated paragraphs use the command-line defaults.

DIRECTIVES
  emotion   neutral | happy | sad | angry | fearful | disgusted | surprised
  speed     slow | medium | fast
  pitch     low | medium | high
  volume    low | medium | high
  dialect   cantonese | sichuan
  style     free English description (freeform template)
  pause     integer ms, overrides the boundary pause BEFORE this paragraph
  voice     voice-pack id
  seed      integer

TEMPLATE GATE (enforced by the engine)
  The emotion template accepts only an emotion; the basic template accepts only
  speed/pitch/volume. They cannot be mixed in one call. Combining them in a section is
  a hard error unless --style-fallback is set, which rewrites the request as a freeform
  English style description.
"""
import argparse
import array
import json
import os
import re
import subprocess
import sys
import tempfile
import urllib.error
import urllib.request
import wave

DEFAULT_ENGINE = "http://127.0.0.1:18099"

# English -> trained control token. Scripts never need the token; the report records it.
EMOTION = {
    "neutral": "\u4e2d\u6027",
    "happy": "\u9ad8\u5174",
    "glad": "\u9ad8\u5174",
    "sad": "\u60b2\u4f24",
    "unhappy": "\u60b2\u4f24",
    "angry": "\u6124\u6012",
    "mad": "\u6124\u6012",
    "fearful": "\u6050\u60e7",
    "afraid": "\u6050\u60e7",
    "scared": "\u6050\u60e7",
    "disgusted": "\u538c\u6076",
    "surprised": "\u60ca\u8bb6",
}
SPEED = {"slow": "\u6162\u901f", "medium": "\u4e2d\u901f", "medium-fast": "\u4e2d\u901f", "fast": "\u5feb\u901f"}
PITCH = {"low": "\u4f4e", "medium": "\u4e2d", "high": "\u9ad8"}
VOLUME = {"low": "\u4f4e", "medium": "\u4e2d", "high": "\u9ad8"}
DIALECT = {"cantonese": "cantonese", "yue": "cantonese", "sichuan": "chuanyu dialect", "chuanyu": "chuanyu dialect"}

CONTROL_FAMILIES = (
    ("emotion", EMOTION),
    ("speed", SPEED),
    ("pitch", PITCH),
    ("volume", VOLUME),
    ("dialect", DIALECT),
)

SILENCE_MAX_S = 0.31   # engine trim_silence leaves a fixed ~0.300 s tail on a dead take
SILENCE_MAX_PEAK = 55

DIRECTIVE_RE = re.compile(r"^\s*(?:\[[^\]]*\]\s*)+$")
GROUP_RE = re.compile(r"\[([^\]]*)\]")
PAIR_RE = re.compile(r"([A-Za-z_]+)\s*:\s*([^;]+)")


class ScriptError(Exception):
    pass


def _is_cjk(ch):
    """True for Chinese/Japanese/Korean script ranges (the model's trained tokens)."""
    code = ord(ch)
    return (0x3000 <= code <= 0x30FF or 0x3400 <= code <= 0x4DBF
            or 0x4E00 <= code <= 0x9FFF or 0xF900 <= code <= 0xFAFF
            or 0x20000 <= code <= 0x2FA1F)


def parse_script(text):
    """-> list of sections: {"controls": dict, "text": str, "breaks": int}"""
    sections = []
    pending = {}
    breaks = 1
    current = []
    seen_text = False

    def flush():
        nonlocal pending, current, seen_text
        body = " ".join(line.strip() for line in current if line.strip()).strip()
        if body:
            sections.append({"controls": dict(pending), "text": body, "breaks": breaks})
            pending = {}
        elif pending:
            # directive with no following paragraph yet: keep it attached to the next one
            pass
        current = []
        seen_text = False

    for raw in text.splitlines():
        line = raw.rstrip()
        if not line.strip():
            if current:
                flush()
                breaks = 1
            else:
                breaks += 1
            continue
        if line.lstrip().startswith("#") and "[" not in line:
            continue
        if DIRECTIVE_RE.match(line):
            for body in GROUP_RE.findall(line):
                pairs = PAIR_RE.findall(body)
                if not pairs:
                    raise ScriptError(f"unparseable directive group: [{body}]")
                for key, value in pairs:
                    pending[key.strip().lower()] = value.strip()
            continue
        if line.lstrip().startswith("["):
            raise ScriptError(
                f"line starting with '[' is not a valid directive line: {line.strip()!r}. "
                "A directive line holds only [key: value] groups; put the paragraph on the "
                "next line, or remove the brackets."
            )
        current.append(line)
        seen_text = True
    flush()
    if pending:
        raise ScriptError("directive block at end of script with no paragraph after it")
    if not sections:
        raise ScriptError("script contains no paragraphs")
    return sections


def resolve_controls(section, defaults):
    """Merge section directives over defaults, validate, and pick the engine template."""
    merged = dict(defaults)
    merged.update(section["controls"])
    unknown = set(k for k in merged if k not in
                  {"emotion", "speed", "pitch", "volume", "dialect", "style", "voice", "seed", "pause"})
    if unknown:
        raise ScriptError(f"unknown directive(s): {', '.join(sorted(unknown))}")

    out = {"english": {}, "sent": {}, "template": None}
    structured = []
    for name, table in CONTROL_FAMILIES:
        raw = merged.get(name)
        if not raw:
            continue
        key = str(raw).strip().lower()
        if key not in table:
            valid = ", ".join(sorted({k for k in table}))
            raise ScriptError(
                "unknown " + name + " value " + repr(raw) + ". Valid: " + valid
            )
        structured.append(name)
        out["english"][name] = key
        out["sent"][name] = table[key]

    style = merged.get("style")
    if style and structured:
        raise ScriptError(
            "a section cannot combine structured controls "
            f"({', '.join(sorted(structured))}) with a style description; "
            "the engine's structured templates reject raw instruct text"
        )
    has_emotion = "emotion" in structured
    has_basic = any(n in structured for n in ("speed", "pitch", "volume"))
    has_dialect = "dialect" in structured

    if has_emotion and (has_basic or has_dialect):
        raise ScriptError(
            "emotion cannot be combined with speed/pitch/volume/dialect in one section "
            "(the emotion template accepts only an emotion). Split into separate sections, "
            "or set --style-fallback to express the combination as a freeform style."
        )
    if has_emotion:
        out["template"] = "emotion"
    elif has_basic:
        out["template"] = "basic"
    elif has_dialect:
        out["template"] = "dialect"
    elif style:
        out["template"] = "freeform"
        out["english"]["style"] = str(style)
        out["sent"]["instruct"] = str(style)
    else:
        out["template"] = None

    for extra in ("voice", "seed", "pause"):
        if extra in merged:
            out[extra] = merged[extra]
    return out


def apply_style_fallback(spec):
    """Rewrite an incompatible structured combination as freeform English text."""
    parts = []
    for name in ("emotion", "speed", "pitch", "volume"):
        if name in spec["english"]:
            parts.append(f"{name} = {spec['english'][name]}")
    text = "Speak in this manner: " + ", ".join(parts) + "."
    spec["sent"] = {"instruct": text}
    spec["template"] = "freeform"
    return spec


def split_chunks(paragraph, max_chars, hard_cap=None):
    """Pack WHOLE SENTENCES into fragments. A sentence is never split.

    Upstream splits "at punctuation, keeping long clauses intact" (infer.py --max_length).
    Splitting inside a sentence puts the fragment boundary where the model has no
    punctuation cue, so it restarts prosody mid-phrase -- measured here as
    "...on my nvidia" / "powered workstation." on separate cold-started calls.
    A sentence longer than hard_cap falls back to comma boundaries, then word boundaries,
    because an over-long single fragment risks the generation budget instead.
    """
    hard_cap = hard_cap or max(max_chars * 2, 320)
    sentences = [x.strip() for x in re.findall(r"[^.!?\u2026]+[.!?\u2026]*", paragraph) if x.strip()]
    if not sentences:
        sentences = [paragraph.strip()]

    chunks = []
    buf = ""
    for sent in sentences:
        if len(sent) > hard_cap:
            # oversized single sentence: fall back to clause boundaries, never mid-word
            if buf:
                chunks.append(buf); buf = ""
            rest = sent
            while len(rest) > hard_cap:
                cut = rest.rfind(",", 0, hard_cap)
                if cut < hard_cap // 2:
                    cut = rest.rfind(" ", 0, hard_cap)
                if cut < hard_cap // 2:
                    cut = hard_cap
                chunks.append(rest[:cut].strip())
                rest = rest[cut:].strip()
            if rest:
                buf = rest
            continue
        candidate = (buf + " " + sent).strip()
        if len(candidate) <= max_chars or not buf:
            buf = candidate
        else:
            chunks.append(buf)
            buf = sent
    if buf:
        chunks.append(buf)
    return [c for c in chunks if c]


def detect_model(engine_url, explicit):
    if explicit:
        return explicit
    with urllib.request.urlopen(f"{engine_url}/v1/models", timeout=10) as resp:
        data = json.load(resp)
    models = data.get("data") or data.get("models") or []
    ids = [m.get("id") or m.get("name") for m in models if isinstance(m, dict)]
    for m in ids:
        if m and "ming" in m.lower():
            return m
    if not ids:
        raise RuntimeError("no models loaded on the engine")
    return ids[0]


def synthesize(engine_url, model, text, voice, sent_options, timeout,
               schema="zh", prefix=True):
    """One engine call = one synthesis fragment (raw_text), conditioned on the reference.

    prefix=False sends reference_text="", which stops the server injecting the voice-pack
    transcript (server_protocol.cpp:127-130) and therefore stops the acoustic prompt
    latent being built (session.cpp:148). Upstream never pairs a control caption with an
    acoustic prefix: README EMOTION/BASIC pass speaker conditioning with no prompt_text,
    and cookbooks/instruction_en.md says of freeform "Reference audio is not required and
    is not recommended by default." A prefix of flat narration was measured to flatten the
    delivered emotion (this session: spectral-centroid spread 0.82 with prefix vs 1.74
    without, on the same four phrases).
    """
    payload = {"model": model, "input": text, "voice": voice}
    options = {"raw_text": "true", "instruction_schema": schema}
    if not prefix:
        options["reference_text"] = ""
    options.update(sent_options)
    payload["options"] = options
    req = urllib.request.Request(
        f"{engine_url}/v1/audio/speech",
        data=json.dumps(payload).encode("utf-8"),
        headers={"Content-Type": "application/json"},
        method="POST",
    )
    with urllib.request.urlopen(req, timeout=timeout) as resp:
        blob = resp.read()
    if blob[:4] != b"RIFF":
        raise RuntimeError(f"engine returned non-WAV payload: {blob[:160]!r}")
    return blob


def pcm_of(blob, want_rate=44100):
    """Decode WAV bytes to (pcm16 mono bytes, rate), normalizing via ffmpeg if needed."""
    tmp_in = tempfile.mktemp(suffix=".wav")
    tmp_out = tempfile.mktemp(suffix=".s16")
    with open(tmp_in, "wb") as fh:
        fh.write(blob)
    try:
        subprocess.run(
            ["ffmpeg", "-y", "-loglevel", "error", "-i", tmp_in,
             "-ac", "1", "-ar", str(want_rate), "-f", "s16le", tmp_out],
            check=True, capture_output=True,
        )
        with open(tmp_out, "rb") as fh:
            return fh.read(), want_rate
    finally:
        for p in (tmp_in, tmp_out):
            if os.path.exists(p):
                os.unlink(p)


def peak_of(pcm):
    if not pcm:
        return 0
    a = array.array("h")
    a.frombytes(pcm[: len(pcm) - (len(pcm) % 2)])
    hi = max(a)
    lo = min(a)
    return max(hi, -lo)


def silence_ms(ms):
    return b"\x00\x00" * int(round(ms * 44.1))


def write_wav(path, pcm, rate=44100):
    os.makedirs(os.path.dirname(path) or ".", exist_ok=True)
    with wave.open(path, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(rate)
        w.writeframes(pcm)


def main():
    ap = argparse.ArgumentParser(description="Ming-3 narrator with English annotations (free-standing)")
    ap.add_argument("script", help="annotated script file")
    ap.add_argument("-o", "--output", required=True, help="output WAV")
    ap.add_argument("--voice", default="doug")
    ap.add_argument("--engine-url", default=DEFAULT_ENGINE)
    ap.add_argument("--model", default=None)
    ap.add_argument("--emotion", default=None)
    ap.add_argument("--speed", default=None)
    ap.add_argument("--pitch", default=None)
    ap.add_argument("--volume", default=None)
    ap.add_argument("--dialect", default=None)
    ap.add_argument("--style", default=None)
    ap.add_argument("--seed", type=int, default=None, help="omit to use the engine default")
    ap.add_argument("--pause-ms", type=float, default=300.0, help="silence per blank-line boundary")
    ap.add_argument("--intra-ms", type=float, default=80.0, help="seam between chunks of one paragraph")
    ap.add_argument("--chunk-chars", type=int, default=200,
                    help="target maximum characters per engine call; sentences are packed into it "
                         "and are never split below the hard cap")
    ap.add_argument("--timeout", type=int, default=300)
    ap.add_argument("--instruction-schema", choices=("zh", "en"), default="zh",
                    help="caption field names; upstream trains zh for emotion/basic "
                         "(tts_tasks.py:172) and en only for dialect. Script vocabulary "
                         "stays English either way.")
    ap.add_argument("--prefix", choices=("on", "off"), default="on",
                    help="acoustic prompt latent from the voice-pack clip. ON for every "
                         "call: it is what makes each chunk the same speaker. OFF is "
                         "diagnostic only -- with it off each call is an independent draw "
                         "from the 192-dim embedding and chunks stop sounding like one "
                         "person (measured this session: cos-to-reference slope "
                         "-0.0030/fragment and min 0.963 off vs -0.0001 and min 0.989 on).")
    ap.add_argument("--report", default=None, help="JSON run report (default: <output>.report.json)")
    ap.add_argument("--style-fallback", action="store_true",
                    help="rewrite incompatible control combinations as a freeform style description")
    ap.add_argument("--dry-run", action="store_true", help="parse and plan only; synthesize nothing")
    args = ap.parse_args()

    script_text = open(args.script, encoding="utf-8").read()
    for lineno, raw in enumerate(script_text.splitlines(), 1):
        bad = sorted({ch for ch in raw if _is_cjk(ch)})
        if bad:
            raise ScriptError(
                f"line {lineno}: scripts are English only; found CJK characters {bad}. "
                "Use an English directive value (e.g. [emotion: happy]) and the tool maps it "
                "to the model's trained token for you."
            )

    defaults = {k: v for k, v in {
        "emotion": args.emotion, "speed": args.speed, "pitch": args.pitch,
        "volume": args.volume, "dialect": args.dialect, "style": args.style,
        "seed": args.seed,
    }.items() if v is not None}

    sections = parse_script(script_text)
    specs = []
    for s in sections:
        try:
            specs.append(resolve_controls(s, defaults))
        except ScriptError as exc:
            if args.style_fallback:
                merged = dict(defaults); merged.update(s["controls"])
                spec = {"english": {}, "sent": {}, "template": "freeform"}
                bits = []
                for name in ("emotion", "speed", "pitch", "volume"):
                    val = str(merged.get(name, "")).strip().lower()
                    if val:
                        table = {"emotion": EMOTION, "speed": SPEED, "pitch": PITCH, "volume": VOLUME}[name]
                        if val not in table:
                            raise
                        spec["english"][name] = val
                        bits.append(f"{name} = {val}")
                if merged.get("style"):
                    bits.append(f"style = {merged['style']}")
                spec["sent"] = {"instruct": "Speak in this manner: " + ", ".join(bits) + "."}
                print(f"note: rewrote an incompatible control mix as freeform style", file=sys.stderr)
                specs.append(spec)
            else:
                raise

    plan = []
    for sec, spec in zip(sections, specs):
        chunks = split_chunks(sec["text"], args.chunk_chars)
        plan.append({"sec": sec, "spec": spec, "chunks": chunks})
        voice_note = spec.get("voice", args.voice)
        ctrl = ", ".join(f"{k}={v}" for k, v in spec["english"].items()) or "(no control)"
        if args.prefix == "on":
                pfx = True
        elif args.prefix == "off":
            pfx = False
        else:
            pfx = spec["template"] is None
        print(f"section {len(plan)}: template={spec['template'] or 'default'}  {ctrl}  "
              f"calls={len(chunks)}  breaks={sec['breaks']}  voice={voice_note}  "
              f"prefix={'on' if pfx else 'off'}", file=sys.stderr)

    if args.dry_run:
        print(json.dumps({"ok": True, "sections": [
            {"text": p["sec"]["text"][:80], "template": p["spec"]["template"],
             "english": p["spec"]["english"], "sent": p["spec"]["sent"], "chunks": p["chunks"]}
            for p in plan]}, indent=2))
        return

    model = detect_model(args.engine_url, args.model)
    print(f"engine={args.engine_url} model={model}", file=sys.stderr)

    out_pcm = b""
    records = []
    for idx, p in enumerate(plan):
        spec, sec = p["spec"], p["sec"]
        pause = float(spec.get("pause", args.pause_ms)) * max(1, sec["breaks"])
        if idx > 0:
            out_pcm += silence_ms(pause)
            records.append({"kind": "pause", "ms": pause, "after_section": idx})
        sent = dict(spec["sent"])
        if spec["template"]:
            sent["template_name"] = spec["template"]
        if spec.get("seed") is not None:
            sent["seed"] = int(spec["seed"])
        controlled = spec["template"] is not None
        prefix_on = args.prefix == "on"
        for ci, chunk in enumerate(p["chunks"]):
            blob = synthesize(args.engine_url, model, chunk, spec.get("voice", args.voice),
                              sent, args.timeout, schema=args.instruction_schema,
                              prefix=prefix_on)
            pcm, rate = pcm_of(blob)
            pk = peak_of(pcm)
            dur = len(pcm) / 2 / rate
            dead = dur <= SILENCE_MAX_S and pk <= SILENCE_MAX_PEAK
            records.append({
                "kind": "speech", "section": idx, "chunk": ci,
                "text": chunk, "template": spec["template"],
                "english": dict(spec["english"]), "engine_values": dict(spec["sent"]),
                "duration_s": round(dur, 3), "peak": pk,
                "prefix": prefix_on, "instruction_schema": args.instruction_schema,
                "silent": bool(dead),
            })
            flag = "  <-- SILENCE" if dead else ""
            print(f"  s{idx}c{ci}: {dur:.3f}s peak={pk}{flag}", file=sys.stderr)
            out_pcm += pcm
            if ci + 1 < len(p["chunks"]) and args.intra_ms:
                out_pcm += silence_ms(args.intra_ms)

    write_wav(args.output, out_pcm)
    report_path = args.report or (args.output + ".report.json")
    report = {
        "ok": True, "engine": args.engine_url, "model": model, "voice": args.voice,
        "script": os.path.abspath(args.script), "output": os.path.abspath(args.output),
        "duration_seconds": round(len(out_pcm) / 2 / 44100, 3),
        "single_call_per_chunk": True, "seed": args.seed,
        "instruction_schema": args.instruction_schema, "prefix_mode": args.prefix,
        "sections": records,
    }
    with open(report_path, "w", encoding="utf-8") as fh:
        json.dump(report, fh, indent=2)
    dead = [r for r in records if r.get("silent")]
    print(json.dumps({"output": args.output, "report": report_path,
                      "duration_seconds": report["duration_seconds"],
                      "silent_cells": len(dead), "calls": len([r for r in records if r["kind"] == "speech"])},
                     indent=2))


if __name__ == "__main__":
    try:
        main()
    except ScriptError as exc:
        sys.stderr.write(f"script error: {exc}\n")
        sys.exit(2)
    except urllib.error.HTTPError as exc:
        sys.stderr.write(f"engine HTTP {exc.code}: {exc.read().decode('utf-8', 'replace')[:400]}\n")
        sys.exit(3)
    except Exception as exc:
        sys.stderr.write(f"error: {exc}\n")
        sys.exit(1)
