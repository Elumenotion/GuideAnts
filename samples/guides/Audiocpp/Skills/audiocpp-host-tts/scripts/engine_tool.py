#!/usr/bin/env python3
"""Talk to a raw audiocpp_server engine (wrapper-spawned, skill-spawned, or host).

Unlike the wrapper `/synthesize` contract, the engine's /v1/audio/speech accepts
voice_ref / reference_text / language / instructions / seed — the scenario surface
GuideAnts does not expose. Stdlib-only.
"""
import argparse
import json
import os
import sys
import urllib.error
import urllib.parse
import urllib.request

ENGINE_TTS_DEFAULT = "http://127.0.0.1:18084"
ENGINE_ASR_DEFAULT = "http://127.0.0.1:18082"
WRAPPER_TTS_HEALTH = "http://127.0.0.1:8084/health"
ASR_ENGINE_MODEL_ID = "qwen3-asr"  # fixed id the ASR wrapper always configures
DEFAULT_TIMEOUT_SECONDS = 280  # under the ~5 min sandbox script budget


def _request_json(url: str, payload: dict | None = None, timeout: int = DEFAULT_TIMEOUT_SECONDS):
    data = json.dumps(payload).encode("utf-8") if payload is not None else None
    request = urllib.request.Request(
        url,
        data=data,
        method="POST" if payload is not None else "GET",
        headers={"Content-Type": "application/json"} if payload is not None else {},
    )
    with urllib.request.urlopen(request, timeout=timeout) as response:
        return response.read()


def _fail_http(exc: urllib.error.HTTPError, context: str) -> None:
    detail = exc.read().decode("utf-8", errors="replace")
    sys.stderr.write(f"{context} failed with HTTP {exc.code}: {detail}\n")
    sys.exit(1)


def resolve_tts_model(engine_url: str, explicit: str | None) -> str:
    if explicit:
        return explicit
    if engine_url.rstrip("/") != ENGINE_TTS_DEFAULT:
        sys.stderr.write(
            "--model is required for non-default engines (the auto-detect only reads "
            "the GuideAnts TTS wrapper's catalogEntryId).\n"
        )
        sys.exit(1)
    try:
        body = json.loads(_request_json(WRAPPER_TTS_HEALTH))
    except Exception as exc:
        sys.stderr.write(f"Could not auto-detect model from {WRAPPER_TTS_HEALTH}: {exc}\n")
        sys.exit(1)
    model = body.get("catalogEntryId")
    if not model:
        sys.stderr.write(
            f"TTS wrapper health has no catalogEntryId (is a model loaded?): {json.dumps(body)[:400]}\n"
        )
        sys.exit(1)
    return model


def cmd_speech(args: argparse.Namespace) -> None:
    engine_url = args.engine_url.rstrip("/")
    payload: dict = {
        "model": resolve_tts_model(engine_url, args.model),
        "input": args.text,
    }
    if args.voice:
        payload["voice"] = args.voice
    if args.voice_ref:
        if not os.path.isfile(args.voice_ref):
            sys.stderr.write(f"voice_ref file not found: {args.voice_ref}\n")
            sys.exit(1)
        payload["voice_ref"] = os.path.abspath(args.voice_ref)
    if args.reference_text:
        payload["reference_text"] = args.reference_text
    if args.language:
        payload["language"] = args.language
    if args.instructions:
        payload["instructions"] = args.instructions
    if args.seed is not None:
        payload["seed"] = args.seed
    try:
        wav_bytes = _request_json(f"{engine_url}/v1/audio/speech", payload)
    except urllib.error.HTTPError as exc:
        _fail_http(exc, "/v1/audio/speech")
        return
    if not wav_bytes or wav_bytes[:4] != b"RIFF":
        sys.stderr.write(f"Engine returned non-WAV payload ({len(wav_bytes)} bytes): {wav_bytes[:120]!r}\n")
        sys.exit(1)
    os.makedirs(os.path.dirname(args.output) or ".", exist_ok=True)
    with open(args.output, "wb") as handle:
        handle.write(wav_bytes)
    print(json.dumps({"output": args.output, "bytes": len(wav_bytes), "model": payload["model"]}))


def cmd_transcribe(args: argparse.Namespace) -> None:
    if not os.path.isfile(args.audio_file):
        sys.stderr.write(f"audio file not found: {args.audio_file}\n")
        sys.exit(1)
    payload: dict = {"model": args.model, "audio": os.path.abspath(args.audio_file)}
    if args.language:
        payload["language"] = args.language
    try:
        body = _request_json(f"{args.engine_url.rstrip('/')}/v1/audio/transcriptions", payload)
    except urllib.error.HTTPError as exc:
        _fail_http(exc, "/v1/audio/transcriptions")
        return
    print(body.decode("utf-8", errors="replace"))


def cmd_voices(args: argparse.Namespace) -> None:
    engine_url = args.engine_url.rstrip("/")
    model = resolve_tts_model(engine_url, args.model)
    try:
        body = _request_json(f"{engine_url}/v1/audio/voices?model={urllib.parse.quote(model)}")
    except urllib.error.HTTPError as exc:
        _fail_http(exc, "/v1/audio/voices")
        return
    print(body.decode("utf-8", errors="replace"))


def main() -> None:
    parser = argparse.ArgumentParser(description="Raw audiocpp_server engine client")
    sub = parser.add_subparsers(dest="command", required=True)

    p_speech = sub.add_parser("speech", help="Synthesize via /v1/audio/speech (full scenario surface)")
    p_speech.add_argument("text")
    p_speech.add_argument("-o", "--output", required=True)
    p_speech.add_argument("--engine-url", default=ENGINE_TTS_DEFAULT)
    p_speech.add_argument("--model", default=None, help="Engine model id; auto-detected from the TTS wrapper for the default engine")
    p_speech.add_argument("--voice", default=None, help="Builtin speaker id or voice-preset id")
    p_speech.add_argument("--voice-ref", default=None, help="Reference WAV path for cloning (workspace file)")
    p_speech.add_argument("--reference-text", default=None)
    p_speech.add_argument("--language", default=None)
    p_speech.add_argument("--instructions", default=None, help="Voice-design text (vdes-task models only)")
    p_speech.add_argument("--seed", type=int, default=None)

    p_transcribe = sub.add_parser("transcribe", help="Transcribe a workspace file by path via /v1/audio/transcriptions")
    p_transcribe.add_argument("audio_file")
    p_transcribe.add_argument("--engine-url", default=ENGINE_ASR_DEFAULT)
    p_transcribe.add_argument("--model", default=ASR_ENGINE_MODEL_ID)
    p_transcribe.add_argument("--language", default=None)

    p_voices = sub.add_parser("voices", help="List voice ids via /v1/audio/voices")
    p_voices.add_argument("--engine-url", default=ENGINE_TTS_DEFAULT)
    p_voices.add_argument("--model", default=None)

    args = parser.parse_args()
    {"speech": cmd_speech, "transcribe": cmd_transcribe, "voices": cmd_voices}[args.command](args)


if __name__ == "__main__":
    main()
