"""Append-only, sequence-numbered event and commit journal."""

from __future__ import annotations

import json
import os
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Iterator

from scripts.browser_session.schema import append_jsonl, read_jsonl_report


@dataclass
class JournalEntry:
    seq: int
    kind: str
    t_mono_ms: int
    payload: dict[str, Any]

    def to_dict(self) -> dict[str, Any]:
        return {"seq": self.seq, "kind": self.kind, "t_mono_ms": self.t_mono_ms, **self.payload}


class EventJournal:
    """Append-only journal with monotonic sequence numbers and fsynced writes."""

    def __init__(self, path: Path) -> None:
        self.path = path
        self._seq = 0
        if path.is_file():
            rows, _report = read_jsonl_report(path)
            for row in rows:
                self._seq = max(self._seq, int(row.get("seq", 0)))

    @property
    def last_seq(self) -> int:
        return self._seq

    def append(self, kind: str, *, t_mono_ms: int, **payload: Any) -> JournalEntry:
        self._seq += 1
        entry = JournalEntry(seq=self._seq, kind=kind, t_mono_ms=t_mono_ms, payload=payload)
        append_jsonl(self.path, entry.to_dict(), fsync=True)
        return entry

    def replay(self) -> list[dict[str, Any]]:
        rows, report = read_jsonl_report(self.path)
        if report.get("corrupt_line"):
            raise RuntimeError(f"journal corrupt at line {report['corrupt_line']}: {self.path}")
        return rows


class SegmentCommitJournal:
    """Tracks closed media segments with hashes for durability proof."""

    def __init__(self, path: Path) -> None:
        self.path = path
        self._journal = EventJournal(path)

    def commit_segment(
        self,
        *,
        t_mono_ms: int,
        segment_index: int,
        path: str,
        sha256: str,
        duration_ms: int,
        video_pts_end_ms: int | None = None,
        audio_pts_end_ms: int | None = None,
    ) -> JournalEntry:
        return self._journal.append(
            "segment.commit",
            t_mono_ms=t_mono_ms,
            segment_index=segment_index,
            path=path,
            sha256=sha256,
            duration_ms=duration_ms,
            video_pts_end_ms=video_pts_end_ms,
            audio_pts_end_ms=audio_pts_end_ms,
        )

    def committed_segments(self) -> list[dict[str, Any]]:
        return [row for row in self._journal.replay() if row.get("kind") == "segment.commit"]

    def last_committed_pts_ms(self) -> tuple[int | None, int | None]:
        video_pts: int | None = None
        audio_pts: int | None = None
        for row in self.committed_segments():
            if row.get("video_pts_end_ms") is not None:
                video_pts = int(row["video_pts_end_ms"])
            if row.get("audio_pts_end_ms") is not None:
                audio_pts = int(row["audio_pts_end_ms"])
        return video_pts, audio_pts


def iter_journal(path: Path) -> Iterator[dict[str, Any]]:
    if not path.is_file():
        return
    with path.open(encoding="utf-8") as handle:
        for line in handle:
            stripped = line.strip()
            if stripped:
                yield json.loads(stripped)
