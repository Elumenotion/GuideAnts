"""Unified supervised FFmpeg A/V capture with crash-durable segments."""

from __future__ import annotations

import json
import os
import re
import shutil
import subprocess
import threading
import time
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Callable

from scripts.browser_session.journal import SegmentCommitJournal
from scripts.browser_session.media_probe import sha256_file


SEGMENT_DURATION_SEC = 2.0
HEALTH_STALL_TIMEOUT_SEC = 5.0
FORBIDDEN_FILTERS = ("apad", "tpad", "adelay")


@dataclass
class FFmpegAVConfig:
    monitor_index: int
    monitor_left: int
    monitor_top: int
    monitor_width: int
    monitor_height: int
    fps: int
    audio_device: str  # exact dshow endpoint name
    output_dir: Path
    sample_rate: int = 48000


@dataclass
class TrackHealth:
    video_pts_ms: int = 0
    audio_pts_ms: int = 0
    last_progress_mono: float = 0.0
    dropped_frames: int = 0
    segment_count: int = 0
    alive: bool = False
    error: str | None = None

    def to_dict(self) -> dict[str, Any]:
        return {
            "video_pts_ms": self.video_pts_ms,
            "audio_pts_ms": self.audio_pts_ms,
            "dropped_frames": self.dropped_frames,
            "segment_count": self.segment_count,
            "alive": self.alive,
            "error": self.error,
        }


def _parse_ffmpeg_progress(line: str, health: TrackHealth) -> None:
  health.last_progress_mono = time.monotonic()
  if line.startswith("frame="):
      match = re.search(r"frame=\s*(\d+)", line)
      if match:
          health.dropped_frames = int(re.search(r"drop=\s*(\d+)", line).group(1)) if re.search(r"drop=\s*(\d+)", line) else 0
  out_time_match = re.search(r"out_time_ms=(\d+)", line)
  if out_time_match:
      pts_ms = int(out_time_match.group(1)) // 1000
      health.video_pts_ms = max(health.video_pts_ms, pts_ms)
      health.audio_pts_ms = max(health.audio_pts_ms, pts_ms)


class SupervisedFFmpegAV:
    """One muxed FFmpeg process writing keyframe-aligned MKV segments."""

    def __init__(self, config: FFmpegAVConfig) -> None:
        self.config = config
        self.config.output_dir.mkdir(parents=True, exist_ok=True)
        self.segments_dir = config.output_dir / "segments"
        self.segments_dir.mkdir(parents=True, exist_ok=True)
        self._process: subprocess.Popen[str] | None = None
        self._stderr_thread: threading.Thread | None = None
        self._stop_event = threading.Event()
        self._health = TrackHealth()
        self._journal = SegmentCommitJournal(config.output_dir / "segment_commits.jsonl")
        self._segment_index = 0
        self._command: list[str] = []
        self._t0_mono: float = 0.0

    @property
    def health(self) -> TrackHealth:
        return self._health

    @property
    def journal(self) -> SegmentCommitJournal:
        return self._journal

    def _build_command(self) -> list[str]:
        seg_pattern = str(self.segments_dir / "seg_%05d.mkv")
        g = self.config
        return [
            "ffmpeg",
            "-y",
            "-f", "gdigrab",
            "-framerate", str(g.fps),
            "-offset_x", str(g.monitor_left),
            "-offset_y", str(g.monitor_top),
            "-video_size", f"{g.monitor_width}x{g.monitor_height}",
            "-i", "desktop",
            "-f", "dshow",
            "-i", f"audio={g.audio_device}",
            "-c:v", "libx264",
            "-preset", "veryfast",
            "-crf", "23",
            "-g", str(int(g.fps * SEGMENT_DURATION_SEC)),
            "-keyint_min", str(int(g.fps * SEGMENT_DURATION_SEC)),
            "-force_key_frames", f"expr:gte(t,n_forced*{SEGMENT_DURATION_SEC})",
            "-c:a", "aac",
            "-ar", str(g.sample_rate),
            "-ac", "1",
            "-f", "segment",
            "-segment_time", str(SEGMENT_DURATION_SEC),
            "-segment_format", "matroska",
            "-reset_timestamps", "1",
            "-strftime", "0",
            seg_pattern,
            "-progress", "pipe:2",
        ]

    def preflight(self, *, duration_sec: float = 1.0) -> dict[str, Any]:
        """Short real capture to prove configured inputs work."""
        if shutil.which("ffmpeg") is None:
            raise RuntimeError("ffmpeg not found on PATH")
        g = self.config
        out = self.config.output_dir / "preflight.mkv"
        if out.exists():
            raise FileExistsError(f"refusing to overwrite preflight output: {out}")
        cmd = [
            "ffmpeg", "-y",
            "-f", "gdigrab",
            "-framerate", str(g.fps),
            "-offset_x", str(g.monitor_left),
            "-offset_y", str(g.monitor_top),
            "-video_size", f"{g.monitor_width}x{g.monitor_height}",
            "-t", str(duration_sec),
            "-i", "desktop",
            "-f", "dshow",
            "-t", str(duration_sec),
            "-i", f"audio={g.audio_device}",
            "-c:v", "libx264", "-preset", "ultrafast",
            "-c:a", "aac", "-ar", str(g.sample_rate), "-ac", "1",
            str(out),
        ]
        proc = subprocess.run(cmd, capture_output=True, text=True)
        if proc.returncode != 0:
            raise RuntimeError(f"preflight capture failed: {proc.stderr[-500:]}")
        if not out.is_file() or out.stat().st_size < 100:
            raise RuntimeError("preflight produced no usable output")
        result = {
            "preflight_path": str(out.resolve()),
            "sha256": sha256_file(out),
            "command": cmd,
            "duration_sec": duration_sec,
        }
        return result

    def start(self) -> None:
        if self._process is not None:
            raise RuntimeError("FFmpeg A/V capture already running")
        self._command = self._build_command()
        self._t0_mono = time.monotonic()
        self._health = TrackHealth(alive=True, last_progress_mono=time.monotonic())
        self._process = subprocess.Popen(
            self._command,
            stderr=subprocess.PIPE,
            stdout=subprocess.DEVNULL,
            text=True,
            bufsize=1,
        )
        self._stderr_thread = threading.Thread(target=self._read_stderr, daemon=True, name="ffmpeg-stderr")
        self._stderr_thread.start()

    def _read_stderr(self) -> None:
        assert self._process is not None and self._process.stderr is not None
        for line in self._process.stderr:
            stripped = line.strip()
            if stripped:
                _parse_ffmpeg_progress(stripped, self._health)
        rc = self._process.wait()
        self._health.alive = False
        if rc != 0 and not self._stop_event.is_set():
            self._health.error = f"ffmpeg exited with code {rc}"

    def check_health(self) -> str | None:
        """Return error message if track health indicates failure."""
        if not self._health.alive:
            return self._health.error or "ffmpeg process not running"
        stall = time.monotonic() - self._health.last_progress_mono
        if stall > HEALTH_STALL_TIMEOUT_SEC:
            return f"ffmpeg progress stalled for {stall:.1f}s"
        if self._health.dropped_frames > 0:
            return f"dropped {self._health.dropped_frames} frames"
        return None

    def seal_closed_segments(self) -> list[dict[str, Any]]:
        """Hash and journal any newly closed segment files."""
        committed_paths = {row.get("path") for row in self._journal.committed_segments()}
        new_commits: list[dict[str, Any]] = []
        for seg_path in sorted(self.segments_dir.glob("seg_*.mkv")):
            path_str = str(seg_path.resolve())
            if path_str in committed_paths:
                continue
            # Only commit segments that appear complete (not the current writing target)
            if seg_path.stat().st_size < 100:
                continue
            next_seg = seg_path.with_name(f"seg_{int(seg_path.stem.split('_')[1]) + 1:05d}.mkv")
            if not next_seg.exists() and self._health.alive:
                continue
            self._segment_index += 1
            digest = sha256_file(seg_path)
            t_mono = int((time.monotonic() - self._t0_mono) * 1000)
            entry = self._journal.commit_segment(
                t_mono_ms=t_mono,
                segment_index=self._segment_index,
                path=path_str,
                sha256=digest,
                duration_ms=int(SEGMENT_DURATION_SEC * 1000),
                video_pts_end_ms=self._health.video_pts_ms,
                audio_pts_end_ms=self._health.audio_pts_ms,
            )
            new_commits.append(entry.to_dict())
            # fsync segment file
            with seg_path.open("rb") as handle:
                os.fsync(handle.fileno())
        self._health.segment_count = len(self._journal.committed_segments())
        return new_commits

    def stop(self) -> dict[str, Any]:
        self._stop_event.set()
        if self._process is not None and self._process.poll() is None:
            self._process.terminate()
            try:
                self._process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                self._process.kill()
                self._process.wait(timeout=5)
        if self._stderr_thread is not None:
            self._stderr_thread.join(timeout=5)
        self._health.alive = False
        self.seal_closed_segments()
        return {
            "command": self._command,
            "health": self._health.to_dict(),
            "committed_segments": self._journal.committed_segments(),
        }

    def command_line(self) -> str:
        return " ".join(self._command)
