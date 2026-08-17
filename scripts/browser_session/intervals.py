"""Half-open interval utilities for timeline analysis and compaction."""

from __future__ import annotations

from dataclasses import dataclass
from typing import Iterable


@dataclass(frozen=True)
class Interval:
    """Half-open interval [start_ms, end_ms)."""

    start_ms: int
    end_ms: int

    def __post_init__(self) -> None:
        if self.end_ms < self.start_ms:
            raise ValueError(f"invalid interval: end {self.end_ms} < start {self.start_ms}")

    @property
    def duration_ms(self) -> int:
        return self.end_ms - self.start_ms

    def contains(self, t_ms: int, *, inclusive_end: bool = False) -> bool:
        if inclusive_end:
            return self.start_ms <= t_ms <= self.end_ms
        return self.start_ms <= t_ms < self.end_ms

    def overlaps(self, other: Interval) -> bool:
        return self.start_ms < other.end_ms and other.start_ms < self.end_ms

    def to_dict(self) -> dict[str, int]:
        return {"start_ms": self.start_ms, "end_ms": self.end_ms}


def validate_intervals(intervals: Iterable[Interval], *, max_end_ms: int | None = None) -> list[Interval]:
    ordered = sorted(intervals, key=lambda item: (item.start_ms, item.end_ms))
    for index, current in enumerate(ordered):
        if current.duration_ms <= 0:
            raise ValueError(f"zero-length interval at {current.start_ms}")
        if max_end_ms is not None and current.end_ms > max_end_ms:
            raise ValueError(f"interval end {current.end_ms} exceeds max {max_end_ms}")
        if index > 0 and current.start_ms < ordered[index - 1].end_ms:
            raise ValueError(
                f"overlapping intervals: {ordered[index - 1].to_dict()} and {current.to_dict()}"
            )
    return ordered


def intersect_intervals(a: Iterable[Interval], b: Iterable[Interval]) -> list[Interval]:
    left = validate_intervals(a)
    right = validate_intervals(b)
    result: list[Interval] = []
    i = j = 0
    while i < len(left) and j < len(right):
        start = max(left[i].start_ms, right[j].start_ms)
        end = min(left[i].end_ms, right[j].end_ms)
        if end > start:
            result.append(Interval(start, end))
        if left[i].end_ms < right[j].end_ms:
            i += 1
        else:
            j += 1
    return result


def subtract_intervals(keep: Iterable[Interval], remove: Iterable[Interval]) -> list[Interval]:
    base = validate_intervals(keep)
    cuts = validate_intervals(remove)
    if not cuts:
        return base
    result: list[Interval] = []
    cut_index = 0
    for interval in base:
        cursor = interval.start_ms
        while cut_index < len(cuts) and cuts[cut_index].end_ms <= cursor:
            cut_index += 1
        local_index = cut_index
        while local_index < len(cuts) and cuts[local_index].start_ms < interval.end_ms:
            cut = cuts[local_index]
            if cut.start_ms > cursor:
                result.append(Interval(cursor, min(cut.start_ms, interval.end_ms)))
            cursor = max(cursor, cut.end_ms)
            if cursor >= interval.end_ms:
                break
            local_index += 1
        if cursor < interval.end_ms:
            result.append(Interval(cursor, interval.end_ms))
    return validate_intervals(result)


def merge_intervals(intervals: Iterable[Interval], *, gap_ms: int = 0) -> list[Interval]:
    ordered = sorted(intervals, key=lambda item: (item.start_ms, item.end_ms))
    for item in ordered:
        if item.duration_ms <= 0:
            raise ValueError(f"zero-length interval at {item.start_ms}")
    if not ordered:
        return []
    merged: list[Interval] = [ordered[0]]
    for current in ordered[1:]:
        prev = merged[-1]
        if current.start_ms <= prev.end_ms + gap_ms:
            merged[-1] = Interval(prev.start_ms, max(prev.end_ms, current.end_ms))
        else:
            merged.append(current)
    return merged


def shrink_intervals(
    intervals: Iterable[Interval], *, margin_ms: int, min_start: int = 0, max_end: int
) -> list[Interval]:
    """Shrink removal intervals by margin on both sides (boundary protection)."""
    shrunk = []
    for item in validate_intervals(intervals, max_end_ms=max_end):
        start = item.start_ms + margin_ms
        end = item.end_ms - margin_ms
        if end > start:
            shrunk.append(Interval(max(min_start, start), min(max_end, end)))
    return validate_intervals(shrunk, max_end_ms=max_end)


def pad_intervals(intervals: Iterable[Interval], *, pad_ms: int, min_start: int = 0, max_end: int) -> list[Interval]:
    padded = [
        Interval(max(min_start, item.start_ms - pad_ms), min(max_end, item.end_ms + pad_ms))
        for item in validate_intervals(intervals)
    ]
    return merge_intervals(padded)


def complement_intervals(intervals: Iterable[Interval], *, start_ms: int, end_ms: int) -> list[Interval]:
    kept = validate_intervals(intervals, max_end_ms=end_ms)
    result: list[Interval] = []
    cursor = start_ms
    for item in kept:
        if item.start_ms > cursor:
            result.append(Interval(cursor, item.start_ms))
        cursor = max(cursor, item.end_ms)
    if cursor < end_ms:
        result.append(Interval(cursor, end_ms))
    return result
