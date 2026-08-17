"""Build a validation pack: per-app window crops and narration clips."""

from __future__ import annotations

import json
import re
import shutil
import subprocess
import wave
from dataclasses import dataclass
from pathlib import Path
from typing import Any

from scripts.browser_session.lookup import extract_video_frame
from scripts.browser_session.time_map import resolve_time
from scripts.browser_session.schema import CropRect, SessionClock, load_session, read_jsonl


@dataclass(frozen=True)
class AppSegment:
    process: str
    display_name: str
    title: str
    t_start_ms: int
    t_end_ms: int
    sample: dict[str, Any]

    @property
    def t_mid_ms(self) -> int:
        return (self.t_start_ms + self.t_end_ms) // 2

    @property
    def duration_ms(self) -> int:
        return self.t_end_ms - self.t_start_ms

    @property
    def app_key(self) -> str:
        return f"{self.process}|{self.display_name}"


def _display_name(process: str, title: str) -> str:
    if process.lower() == "chrome.exe":
        base = title.removesuffix(" - Google Chrome").strip() or "Google Chrome"
        return f"Google Chrome — {base}"
    if process.lower() == "windowsterminal.exe":
        return f"Windows Terminal — {title or 'Terminal'}"
    if process.lower() == "powerpnt.exe":
        clean = title.strip() or "PowerPoint"
        if clean.lower() in {"opening -", "opening - powerpoint"}:
            return "Microsoft PowerPoint (opening)"
        return f"Microsoft PowerPoint — {clean}"
    if process.lower() == "cursor.exe":
        return "Cursor"
    stem = process.rsplit(".", 1)[0]
    return f"{stem} — {title}".strip(" —")


def _crop_area(sample: dict[str, Any]) -> int:
    crop = sample.get("crop", {})
    return int(crop.get("w", 0)) * int(crop.get("h", 0))


def _is_useful_segment(process: str, title: str, sample: dict[str, Any], duration_ms: int) -> bool:
    if duration_ms < 1500:
        return False
    if not sample.get("visible_on_monitor", False):
        return False
    area = _crop_area(sample)
    if area < MIN_CROP_AREA:
        return False
    proc = process.lower()
    if proc == "explorer.exe":
        return False
    if proc == "cursor.exe" and area < 200_000:
        return False
    return True


def _crop_rect_from_sample(sample: dict[str, Any]) -> CropRect:
    crop = sample.get("crop", {})
    return CropRect(
        x=int(crop.get("x", 0)),
        y=int(crop.get("y", 0)),
        w=int(crop.get("w", 0)),
        h=int(crop.get("h", 0)),
    )


def _crops_equal(a: CropRect, b: CropRect, threshold: int = 4) -> bool:
    return (
        abs(a.x - b.x) <= threshold
        and abs(a.y - b.y) <= threshold
        and abs(a.w - b.w) <= threshold
        and abs(a.h - b.h) <= threshold
    )


def _is_animating(samples: list[dict[str, Any]], index: int, *, window_ms: int = 900) -> bool:
    """True when neighboring samples show the window still moving/resizing."""
    row = samples[index]
    t_ms = int(row["t_ms"])
    crop = _crop_rect_from_sample(row)
    for other_index in (index - 1, index + 1):
        if other_index < 0 or other_index >= len(samples):
            continue
        other = samples[other_index]
        dt = abs(int(other["t_ms"]) - t_ms)
        if dt > window_ms:
            continue
        if not _crops_equal(crop, _crop_rect_from_sample(other)):
            return True
    return False


MIN_CROP_AREA = 50_000


def _best_sample_in_range(
    samples: list[dict[str, Any]],
    t_start: int,
    t_end: int,
    *,
    process: str,
    monitor_area: int,
) -> dict[str, Any]:
    del monitor_area  # reserved for future heuristics
    in_range = [row for row in samples if t_start <= int(row["t_ms"]) <= t_end]
    if not in_range:
        return samples[0]

    stable = [
        row
        for index, row in enumerate(in_range)
        if not _is_animating(in_range, index) and _crop_area(row) >= MIN_CROP_AREA
    ]
    if stable:
        pool = stable
    else:
        adequate = [row for row in in_range if _crop_area(row) >= MIN_CROP_AREA]
        pool = adequate or [in_range[-1]]

    if process.lower() == "chrome.exe":
        return max(pool, key=_crop_area)

    usable = [row for row in pool if _crop_area(row) >= MIN_CROP_AREA]
    return max(usable or pool, key=_crop_area)


def discover_app_segments(windows: list[dict[str, Any]], *, monitor_area: int) -> list[AppSegment]:
    if not windows:
        return []

    segments: list[AppSegment] = []
    current_key: str | None = None
    current_samples: list[dict[str, Any]] = []

    def flush() -> None:
        nonlocal current_key, current_samples
        if not current_samples or current_key is None:
            current_samples = []
            current_key = None
            return
        first = current_samples[0]
        last = current_samples[-1]
        duration = int(last["t_ms"]) - int(first["t_ms"])
        best = _best_sample_in_range(
            current_samples,
            int(first["t_ms"]),
            int(last["t_ms"]),
            process=str(first.get("process", "")),
            monitor_area=monitor_area,
        )
        process = str(best.get("process", ""))
        title = str(best.get("title", ""))
        display = _display_name(process, title)
        if _is_useful_segment(process, title, best, duration):
            segments.append(
                AppSegment(
                    process=process,
                    display_name=display,
                    title=title,
                    t_start_ms=int(first["t_ms"]),
                    t_end_ms=int(last["t_ms"]),
                    sample=best,
                )
            )
        current_samples = []
        current_key = None

    for row in windows:
        process = str(row.get("process", ""))
        title = str(row.get("title", ""))
        key = f"{process}|{_display_name(process, title)}"
        if current_key is None:
            current_key = key
            current_samples = [row]
            continue
        if key != current_key:
            flush()
            current_key = key
            current_samples = [row]
        else:
            current_samples.append(row)
    flush()
    return segments


def _slug(text: str, max_len: int = 48) -> str:
    out = re.sub(r"[^A-Za-z0-9]+", "-", text).strip("-").lower()
    return out[:max_len] or "app"


def _narration_duration_sec(narration_path: Path) -> float:
    with wave.open(str(narration_path), "rb") as handle:
        return handle.getnframes() / float(handle.getframerate())


def _extract_narration_clip(
    narration_path: Path,
    *,
    center_ms: int,
    output: Path,
    pad_before_sec: float = 2.0,
    pad_after_sec: float = 2.0,
) -> dict[str, float]:
    duration = _narration_duration_sec(narration_path)
    center_sec = center_ms / 1000.0
    start_sec = max(0.0, center_sec - pad_before_sec)
    end_sec = min(duration, center_sec + pad_after_sec)
    if end_sec <= start_sec:
        start_sec = max(0.0, min(center_sec, duration) - pad_before_sec)
        end_sec = min(duration, start_sec + pad_before_sec + pad_after_sec)
    clip_len = max(0.0, end_sec - start_sec)
    output.parent.mkdir(parents=True, exist_ok=True)
    cmd = [
        "ffmpeg",
        "-y",
        "-ss",
        f"{start_sec:.3f}",
        "-i",
        str(narration_path),
        "-t",
        f"{clip_len:.3f}",
        "-acodec",
        "pcm_s16le",
        str(output),
    ]
    subprocess.run(cmd, check=True, capture_output=True)
    return {
        "center_sec": center_sec,
        "start_sec": start_sec,
        "end_sec": end_sec,
        "duration_sec": clip_len,
    }


def _extract_window_png(
    video_path: Path,
    *,
    video_time_ms: int,
    crop: CropRect,
    output: Path,
    fps: int,
) -> None:
    output.parent.mkdir(parents=True, exist_ok=True)
    frame_tmp = output.with_suffix(".frame.png")
    extract_video_frame(video_path, video_time_ms, fps, frame_tmp)
    if crop.w > 0 and crop.h > 0:
        from scripts.browser_session.lookup import crop_frame

        crop_frame(frame_tmp, crop, output)
        frame_tmp.unlink(missing_ok=True)
    else:
        frame_tmp.replace(output)


def build_validation_pack(
    session_dir: Path,
    *,
    pad_sec: float = 2.0,
    time_basis: str = "source",
) -> dict[str, Any]:
    session_dir = session_dir.resolve()
    session = load_session(session_dir)
    clock = SessionClock(
        t0_epoch_ms=int(session["clock"]["t0_epoch_ms"]),
        recording_started_epoch_ms=int(session["clock"]["recording_started_epoch_ms"]),
        recording_lead_in_ms=int(session["clock"]["recording_lead_in_ms"]),
        fps=int(session["clock"].get("fps", 30)),
    )
    video_path = Path(session["paths"]["video"])
    narration_path = Path(session["paths"]["narration"])
    if time_basis == "compact":
        compact = session.get("compact") or {}
        if compact.get("video"):
            video_path = Path(compact["video"])
        if compact.get("narration"):
            narration_path = Path(compact["narration"])
    if not video_path.is_file():
        video_path = session_dir / "video.mp4"
    if not narration_path.is_file():
        narration_path = session_dir / "narration.wav"

    windows = read_jsonl(session_dir / "windows.jsonl")
    monitor = session["monitor"]
    monitor_area = int(monitor["width"]) * int(monitor["height"])
    segments = discover_app_segments(windows, monitor_area=monitor_area)

    out_root = session_dir / "validation"
    if out_root.exists():
        shutil.rmtree(out_root)
    out_root.mkdir(parents=True, exist_ok=True)

    entries: list[dict[str, Any]] = []
    used_slugs: dict[str, int] = {}

    for index, segment in enumerate(segments, start=1):
        base_slug = _slug(segment.display_name)
        count = used_slugs.get(base_slug, 0) + 1
        used_slugs[base_slug] = count
        slug = base_slug if count == 1 else f"{base_slug}-{count}"
        folder = out_root / f"{index:02d}_{slug}"
        folder.mkdir(parents=True, exist_ok=True)

        snapshot_t_ms = int(segment.sample.get("t_ms", segment.t_mid_ms))
        resolved = resolve_time(session_dir, snapshot_t_ms, basis=time_basis)  # type: ignore[arg-type]
        if resolved.status != "ok" or resolved.source_ms is None or resolved.video_ms is None:
            continue
        source_t_ms = resolved.source_ms
        video_t_ms = resolved.video_ms
        crop_data = segment.sample.get("crop", {})
        crop = CropRect(
            x=int(crop_data.get("x", 0)),
            y=int(crop_data.get("y", 0)),
            w=int(crop_data.get("w", 0)),
            h=int(crop_data.get("h", 0)),
        )
        image_path = folder / "window.png"
        audio_path = folder / "narration.wav"

        _extract_window_png(
            video_path,
            video_time_ms=video_t_ms,
            crop=crop,
            output=image_path,
            fps=clock.fps,
        )
        audio_meta = _extract_narration_clip(
            narration_path,
            center_ms=source_t_ms,
            output=audio_path,
            pad_before_sec=pad_sec,
            pad_after_sec=pad_sec,
        )

        entry = {
            "index": index,
            "slug": slug,
            "process": segment.process,
            "display_name": segment.display_name,
            "title": segment.title,
            "t_start_ms": segment.t_start_ms,
            "t_end_ms": segment.t_end_ms,
            "t_mid_ms": segment.t_mid_ms,
            "snapshot_t_ms": snapshot_t_ms,
            "duration_ms": segment.duration_ms,
            "crop": crop.to_dict(),
            "paths": {
                "folder": str(folder.resolve()),
                "window_png": str(image_path.resolve()),
                "narration_wav": str(audio_path.resolve()),
            },
            "narration_clip": audio_meta,
        }
        write_meta = folder / "meta.json"
        write_meta.write_text(json.dumps(entry, indent=2) + "\n", encoding="utf-8")
        entries.append(entry)

    manifest = {
        "session_id": session_dir.name,
        "app_count": len(entries),
        "apps": [
            {
                "display_name": e["display_name"],
                "process": e["process"],
                "t_mid_ms": e["snapshot_t_ms"],
                "folder": e["paths"]["folder"],
            }
            for e in entries
        ],
        "entries": entries,
    }
    manifest_path = out_root / "manifest.json"
    manifest_path.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    return manifest
