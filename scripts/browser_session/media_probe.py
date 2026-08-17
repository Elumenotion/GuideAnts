"""Probe video and audio media files for duration and metadata."""

from __future__ import annotations

import hashlib
import json
import os
import shutil
import subprocess
import wave
from pathlib import Path
from typing import Any


def write_pcm_as_wav(raw_path: Path, output_path: Path, *, sample_rate: int = 48000) -> None:
    output_path.parent.mkdir(parents=True, exist_ok=True)
    temporary = output_path.with_suffix(output_path.suffix + ".tmp")
    with raw_path.open("rb") as source, wave.open(str(temporary), "wb") as target:
        target.setnchannels(1)
        target.setsampwidth(2)
        target.setframerate(sample_rate)
        while True:
            chunk = source.read(1024 * 1024)
            if not chunk:
                break
            target.writeframesraw(chunk)
        target._patchheader()
    os.replace(temporary, output_path)


def _partial_wav_payload(path: Path) -> bytes | None:
    try:
        raw = path.read_bytes()
    except OSError:
        return None
    if len(raw) < 12 or raw[:4] != b"RIFF" or raw[8:12] != b"WAVE":
        return None
    offset = 12
    while offset + 8 <= len(raw):
        chunk_id = raw[offset : offset + 4]
        chunk_size = int.from_bytes(raw[offset + 4 : offset + 8], "little")
        data_start = offset + 8
        if chunk_id == b"data":
            payload = raw[data_start:]
            return payload[: len(payload) - (len(payload) % 2)]
        offset = data_start + chunk_size + (chunk_size % 2)
    return None


def recover_partial_wav(path: Path, *, sample_rate: int = 48000) -> Path | None:
    """Create a valid WAV from audio bytes left after an interrupted capture."""
    if not path.is_file():
        return None
    payload = _partial_wav_payload(path)
    if not payload:
        return None
    try:
        with wave.open(str(path), "rb") as handle:
            expected_size = 44 + handle.getnframes() * handle.getnchannels() * handle.getsampwidth()
        if expected_size <= path.stat().st_size:
            return path
    except (OSError, wave.Error):
        pass

    preserved = path.with_suffix(".partial.wav")
    if preserved.exists():
        raise FileExistsError(f"refusing to overwrite original partial narration: {preserved}")
    original = path.read_bytes()
    with preserved.open("xb") as handle:
        handle.write(original)
        handle.flush()
        os.fsync(handle.fileno())
    temporary = path.with_suffix(path.suffix + ".recovered.tmp")
    with wave.open(str(temporary), "wb") as target:
        target.setnchannels(1)
        target.setsampwidth(2)
        target.setframerate(sample_rate)
        target.writeframes(payload)
        target._patchheader()
        target._file.flush()
        os.fsync(target._file.fileno())
    os.replace(temporary, path)
    return path


def sha256_file(path: Path, *, chunk_size: int = 1024 * 1024) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        while True:
            chunk = handle.read(chunk_size)
            if not chunk:
                break
            digest.update(chunk)
    return digest.hexdigest()


def probe_wav(path: Path) -> dict[str, Any]:
    if not path.is_file():
        raise FileNotFoundError(f"missing narration file: {path}")
    with wave.open(str(path), "rb") as handle:
        frames = handle.getnframes()
        rate = handle.getframerate()
        channels = handle.getnchannels()
        sample_width = handle.getsampwidth()
    duration_ms = int(round(frames * 1000.0 / rate)) if rate else 0
    return {
        "path": str(path.resolve()),
        "duration_ms": duration_ms,
        "frame_count": frames,
        "sample_rate": rate,
        "channels": channels,
        "sample_width": sample_width,
        "sha256": sha256_file(path),
    }


def probe_video_ffprobe(path: Path) -> dict[str, Any]:
    if not path.is_file():
        raise FileNotFoundError(f"missing video file: {path}")
    if shutil.which("ffprobe") is None:
        raise RuntimeError("ffprobe is required to probe video duration")
    cmd = [
        "ffprobe",
        "-v",
        "error",
        "-select_streams",
        "v:0",
        "-show_entries",
        "stream=nb_frames,avg_frame_rate,codec_name,width,height",
        "-show_entries",
        "format=duration",
        "-of",
        "json",
        str(path),
    ]
    proc = subprocess.run(cmd, check=True, capture_output=True, text=True)
    payload = json.loads(proc.stdout)
    stream = (payload.get("streams") or [{}])[0]
    duration_sec = float((payload.get("format") or {}).get("duration", 0.0))
    frame_count = stream.get("nb_frames")
    if frame_count in (None, "N/A"):
        frame_count = None
    else:
        frame_count = int(frame_count)
    fps_text = str(stream.get("avg_frame_rate", "0/1"))
    if "/" in fps_text:
        num, den = fps_text.split("/", 1)
        fps = float(num) / float(den) if float(den) else 0.0
    else:
        fps = float(fps_text)
    duration_ms = int(round(duration_sec * 1000.0))
    return {
        "path": str(path.resolve()),
        "duration_ms": duration_ms,
        "frame_count": frame_count,
        "fps": fps,
        "codec": stream.get("codec_name"),
        "width": stream.get("width"),
        "height": stream.get("height"),
        "sha256": sha256_file(path),
    }


def probe_part_media(session_dir: Path) -> dict[str, Any]:
    video_path = session_dir / "video.mp4"
    if not video_path.is_file():
        video_candidates = sorted(session_dir.glob("video_m*.mp4"), reverse=True)
        video_path = video_candidates[0] if video_candidates else video_path
    narration_path = session_dir / "narration.wav"
    if not narration_path.is_file():
        recovered = session_dir / "narration.recovered.wav"
        if recovered.is_file():
            narration_path = recovered
    issues: list[str] = []
    media: dict[str, Any] = {}
    video_dur = 0
    narration_dur = 0
    if video_path.is_file():
        try:
            media["video"] = probe_video_ffprobe(video_path)
            video_dur = int(media["video"]["duration_ms"])
        except subprocess.CalledProcessError:
            issues.append("invalid_video")
            media["video"] = {
                "path": str(video_path.resolve()),
                "sha256": sha256_file(video_path),
                "status": "invalid",
            }
    else:
        issues.append("missing_video")
    if narration_path.is_file() and narration_path.stat().st_size > 44:
        try:
            media["narration"] = probe_wav(narration_path)
            narration_dur = int(media["narration"]["duration_ms"])
        except wave.Error:
            issues.append("invalid_narration")
    else:
        issues.append("missing_narration")

    if not issues:
        tolerance_ms = max(40, 66)
        if video_dur > 0 and narration_dur > 0 and (video_dur - narration_dur) > tolerance_ms:
            issues.append("audio_coverage_gap")
            media["av_gap_ms"] = video_dur - narration_dur

    if not issues:
        media["status"] = "complete"
    else:
        media["status"] = issues[0]
        media["issues"] = issues
    return media
