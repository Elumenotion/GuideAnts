#!/usr/bin/env python3
"""Pure helpers for long-form single-speaker narration (chunk planning, trim, concat).

Heuristics are tuned for the GPU host + chatterbox + voice-pack clone (e.g. ``narrator``):
  - Measured ~180–186 wpm (~3.0–3.1 words/s) on Script 1 (~1250 words -> ~404s).
  - ~300-word chunks synthesized in ~100s each; keep chunks under ~95s estimated
    audio to avoid gateway/engine generation timeouts.

Override via env:
  AUDIOCPP_TTS_WORDS_PER_SECOND
  AUDIOCPP_TTS_MAX_CHUNK_SECONDS
"""
from __future__ import annotations

import os
import re
import struct
import wave
from dataclasses import dataclass

SENTENCE_SPLIT_RE = re.compile(r"(?<=[.!?])\s+")
CLAUSE_SPLIT_RE = re.compile(r"(?<=[;,])\s+")
MARKDOWN_HEADER_RE = re.compile(r"^#{1,6}\s+")
HORIZONTAL_RULE_RE = re.compile(r"^-{3,}\s*$")
END_MARKER_RE = re.compile(r"^END OF SCRIPT\b", re.IGNORECASE)

# Device-tuned defaults (GPU host / chatterbox / voice-pack clone).
DEFAULT_WORDS_PER_SECOND = float(os.environ.get("AUDIOCPP_TTS_WORDS_PER_SECOND", "3.1"))
DEFAULT_MAX_CHUNK_SECONDS = float(os.environ.get("AUDIOCPP_TTS_MAX_CHUNK_SECONDS", "95"))
# Natural sentence-boundary pause inserted after trim when joining chunks (~130-180ms speech).
DEFAULT_INTER_CHUNK_PAUSE_MS = float(os.environ.get("AUDIOCPP_TTS_INTER_CHUNK_PAUSE_MS", "150"))


@dataclass(frozen=True)
class ChunkPlan:
    index: int
    text: str
    words: int
    estimated_seconds: float


def count_words(text: str) -> int:
    return len(text.split())


def estimate_audio_seconds(words: int, words_per_second: float = DEFAULT_WORDS_PER_SECOND) -> float:
    if words <= 0:
        return 0.0
    return words / words_per_second


def strip_script_markdown(raw: str) -> str:
    """Turn a producer script file into plain narration text."""
    lines: list[str] = []
    for line in raw.splitlines():
        stripped = line.strip()
        if not stripped:
            continue
        if MARKDOWN_HEADER_RE.match(stripped):
            continue
        if HORIZONTAL_RULE_RE.match(stripped):
            continue
        if END_MARKER_RE.match(stripped):
            continue
        lines.append(stripped)
    text = " ".join(lines)
    return re.sub(r"\s+", " ", text).strip()


def split_sentences(text: str) -> list[str]:
    text = text.strip()
    if not text:
        return []
    parts = SENTENCE_SPLIT_RE.split(text)
    return [part.strip() for part in parts if part.strip()]


def _split_oversized_unit(text: str, max_words: int) -> list[str]:
    """Split a long sentence/clause on sub-sentence boundaries, never mid-word."""
    words = text.split()
    if len(words) <= max_words:
        return [text.strip()] if text.strip() else []

    clauses = CLAUSE_SPLIT_RE.split(text)
    if len(clauses) > 1:
        grouped: list[str] = []
        current: list[str] = []
        current_words = 0
        for clause in clauses:
            clause = clause.strip()
            if not clause:
                continue
            clause_words = count_words(clause)
            if clause_words > max_words:
                if current:
                    grouped.append(" ".join(current))
                    current = []
                    current_words = 0
                grouped.extend(_split_by_word_budget(clause, max_words))
                continue
            if current_words + clause_words > max_words and current:
                grouped.append(" ".join(current))
                current = [clause]
                current_words = clause_words
            else:
                current.append(clause)
                current_words += clause_words
        if current:
            grouped.append(" ".join(current))
        return grouped

    return _split_by_word_budget(text, max_words)


def _split_by_word_budget(text: str, max_words: int) -> list[str]:
    words = text.split()
    if len(words) <= max_words:
        return [text.strip()]
    parts: list[str] = []
    for start in range(0, len(words), max_words):
        parts.append(" ".join(words[start : start + max_words]))
    return parts


def plan_chunks(
    text: str,
    *,
    max_chunk_seconds: float = DEFAULT_MAX_CHUNK_SECONDS,
    words_per_second: float = DEFAULT_WORDS_PER_SECOND,
) -> list[ChunkPlan]:
    """Group sentences into chunks that fit the device synthesis budget."""
    max_words = max(1, int(max_chunk_seconds * words_per_second))
    sentences = split_sentences(text)
    if not sentences:
        return []

    raw_chunks: list[str] = []
    current: list[str] = []
    current_words = 0

    for sentence in sentences:
        sentence_words = count_words(sentence)
        if sentence_words > max_words:
            if current:
                raw_chunks.append(" ".join(current))
                current = []
                current_words = 0
            raw_chunks.extend(_split_oversized_unit(sentence, max_words))
            continue

        if current_words + sentence_words > max_words and current:
            raw_chunks.append(" ".join(current))
            current = [sentence]
            current_words = sentence_words
        else:
            current.append(sentence)
            current_words += sentence_words

    if current:
        raw_chunks.append(" ".join(current))

    plans: list[ChunkPlan] = []
    for index, chunk_text in enumerate(raw_chunks):
        words = count_words(chunk_text)
        plans.append(
            ChunkPlan(
                index=index,
                text=chunk_text,
                words=words,
                estimated_seconds=estimate_audio_seconds(words, words_per_second),
            )
        )
    return plans


def needs_chunking(
    text: str,
    *,
    max_chunk_seconds: float = DEFAULT_MAX_CHUNK_SECONDS,
    words_per_second: float = DEFAULT_WORDS_PER_SECOND,
) -> bool:
    words = count_words(text)
    return estimate_audio_seconds(words, words_per_second) > max_chunk_seconds


def trim_silence_pcm16(
    samples: list[int],
    sample_rate: int,
    *,
    threshold: float = 0.012,
    frame_ms: int = 10,
    leading_pad_ms: int = 35,
    trailing_pad_ms: int = 120,
    trim_leading: bool = True,
    trim_trailing: bool = True,
) -> list[int]:
    """Trim leading/trailing silence; trailing pad is wider so word tails are not clipped."""
    if not samples:
        return samples

    frame_size = max(1, int(sample_rate * frame_ms / 1000))
    leading_pad_frames = max(1, int(leading_pad_ms / frame_ms))
    trailing_pad_frames = max(1, int(trailing_pad_ms / frame_ms))
    scale = 1.0 / 32768.0

    def frame_rms(start: int) -> float:
        end = min(start + frame_size, len(samples))
        if end <= start:
            return 0.0
        acc = 0.0
        for value in samples[start:end]:
            normalized = value * scale
            acc += normalized * normalized
        return (acc / (end - start)) ** 0.5

    start_index = 0
    end_index = len(samples)

    if trim_leading:
        first_active = None
        for frame_start in range(0, len(samples), frame_size):
            if frame_rms(frame_start) >= threshold:
                first_active = max(0, frame_start - leading_pad_frames * frame_size)
                break
        if first_active is not None:
            start_index = first_active

    if trim_trailing:
        last_active = None
        for frame_start in range(len(samples) - frame_size, -1, -frame_size):
            if frame_rms(frame_start) >= threshold:
                last_active = min(len(samples), frame_start + frame_size + trailing_pad_frames * frame_size)
                break
        if last_active is not None:
            end_index = last_active

    if start_index >= end_index:
        return samples
    return samples[start_index:end_index]


def read_wav_pcm16(path: str) -> tuple[list[int], int]:
    with wave.open(path, "rb") as handle:
        sample_rate = handle.getframerate()
        channels = handle.getnchannels()
        frames = handle.readframes(handle.getnframes())
    if len(frames) % 2:
        raise ValueError(f"invalid PCM16 frame count in {path}")
    sample_count = len(frames) // 2
    samples = list(struct.unpack(f"<{sample_count}h", frames))
    if channels > 1:
        samples = [
            sum(samples[i + ch] for ch in range(channels)) // channels
            for i in range(0, len(samples), channels)
        ]
    return samples, sample_rate


def write_wav_pcm16(path: str, samples: list[int], sample_rate: int) -> None:
    os.makedirs(os.path.dirname(path) or ".", exist_ok=True)
    with wave.open(path, "wb") as handle:
        handle.setnchannels(1)
        handle.setsampwidth(2)
        handle.setframerate(sample_rate)
        handle.writeframes(b"".join(int(sample).to_bytes(2, "little", signed=True) for sample in samples))


def concat_wavs(
    paths: list[str],
    output_path: str,
    *,
    trim_boundaries: bool = True,
    inter_chunk_pause_ms: float = DEFAULT_INTER_CHUNK_PAUSE_MS,
) -> dict:
    """Concatenate WAVs; trim segment silence, then insert a short pause between chunks."""
    if not paths:
        raise ValueError("no wav paths to concatenate")

    merged: list[int] = []
    sample_rate: int | None = None
    segment_meta: list[dict] = []
    pause_samples = 0

    for index, path in enumerate(paths):
        samples, sr = read_wav_pcm16(path)
        if sample_rate is None:
            sample_rate = sr
            pause_samples = max(0, int(sample_rate * inter_chunk_pause_ms / 1000))
        elif sr != sample_rate:
            raise ValueError(f"sample rate mismatch: {path} is {sr}Hz, expected {sample_rate}Hz")

        original_samples = len(samples)
        if trim_boundaries:
            is_last = index == len(paths) - 1
            samples = trim_silence_pcm16(
                samples,
                sr,
                trim_leading=True,
                trim_trailing=is_last,
            )
        segment_meta.append(
            {
                "path": path,
                "original_samples": original_samples,
                "trimmed_samples": len(samples),
                "duration_seconds": len(samples) / sr,
                "trim_trailing": trim_boundaries and index == len(paths) - 1,
                "inter_chunk_pause_ms": inter_chunk_pause_ms if index < len(paths) - 1 else 0,
            }
        )
        merged.extend(samples)
        if index < len(paths) - 1 and pause_samples > 0:
            merged.extend([0] * pause_samples)

    assert sample_rate is not None
    write_wav_pcm16(output_path, merged, sample_rate)
    return {
        "output": output_path,
        "sample_rate": sample_rate,
        "duration_seconds": len(merged) / sample_rate,
        "inter_chunk_pause_ms": inter_chunk_pause_ms,
        "segments": segment_meta,
    }
