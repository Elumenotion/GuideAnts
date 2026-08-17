"""Resume capture from a previous session or chain."""

from __future__ import annotations

import json
import re
import shutil
from dataclasses import dataclass
from pathlib import Path
from typing import Any

from scripts.browser_session.chain import append_part, chain_path, init_chain, load_chain, write_chain
from scripts.browser_session.schema import load_index, load_session


_PART_DIR_RE = re.compile(r"^part-(\d+)$")

_PART_ARTIFACTS = (
    "video.mp4",
    "narration.wav",
    "windows.jsonl",
    "events.jsonl",
    "index.json",
    "session.json",
    "session.provisional.json",
    "meta.json",
    "activity.jsonl",
    "idle.json",
    "edit_map.json",
    "prune.json",
    "narration.json",
    "narration.pcm",
    "narration.partial.wav",
    "video.json",
)
_PART_GLOBS = ("video_m*.mp4", "video_m*.json")


def _known_duration(value: Any) -> bool:
    return isinstance(value, int) and not isinstance(value, bool) and value >= 0


@dataclass(frozen=True)
class ResumeContext:
    chain_dir: Path
    part_index: int
    part_dir: Path
    chain_offset_ms: int
    monitor_index: int
    fps: int
    urls: list[str]
    prior_parts: int


def is_chain_dir(path: Path) -> bool:
    return chain_path(path).is_file()


def is_part_dir(path: Path) -> bool:
    return _PART_DIR_RE.match(path.name) is not None and (
        (path / "session.json").is_file() or (path / "session.provisional.json").is_file()
    )


def is_flat_session_dir(path: Path) -> bool:
    path = path.resolve()
    if is_chain_dir(path) or is_part_dir(path):
        return False
    return (path / "session.json").is_file() or (path / "session.provisional.json").is_file()


def _part_duration_ms(part_dir: Path) -> int | None:
    try:
        session = load_session(part_dir)
    except FileNotFoundError:
        return None
    media = session.get("media") or {}
    probe_status = media.get("probe_status", media.get("status"))
    if probe_status not in (None, "complete"):
        return None
    duration = media.get("session_duration_ms")
    if probe_status == "complete" and _known_duration(duration):
        return duration
    video = media.get("video") or {}
    narration = media.get("narration") or {}
    durations = [
        value
        for value in (video.get("duration_ms"), narration.get("duration_ms"))
        if _known_duration(value)
    ]
    return max(durations) if durations else None


def _slug_from_dir(path: Path) -> str:
    name = path.name
    if "_" in name:
        return name.split("_", 1)[1]
    return name


def _register_flat_session_migration(root: Path, part_dir: Path) -> None:
    """Record flat-session migration without moving source artifacts."""
    from scripts.browser_session.schema import write_json_atomic

    migration_path = root / "migration.json"
    if migration_path.is_file():
        return
    write_json_atomic(
        migration_path,
        {
            "kind": "flat_to_chain",
            "part_dir": str(part_dir.resolve()),
            "source_artifacts_remain": True,
            "migrated_at_epoch_ms": int(__import__("time").time() * 1000),
        },
    )


def _move_artifacts_into_part(root: Path, part_dir: Path) -> None:
    """Copy artifacts into part directory; originals remain unless explicitly pruned."""
    part_dir.mkdir(parents=True, exist_ok=True)
    for name in _PART_ARTIFACTS:
        src = root / name
        if src.is_file():
            dest = part_dir / name
            if dest.exists():
                continue
            shutil.copy2(str(src), str(dest))
    for pattern in _PART_GLOBS:
        for src in root.glob(pattern):
            if src.is_file():
                dest = part_dir / src.name
                if not dest.exists():
                    shutil.copy2(str(src), str(dest))
    for dirname in ("checkpoints", "lookup"):
        src = root / dirname
        if src.is_dir():
            dest = part_dir / dirname
            if not dest.exists():
                shutil.copytree(str(src), str(dest))
    _register_flat_session_migration(root, part_dir)


def _register_existing_part(chain_dir: Path, part_name: str, *, reason: str) -> None:
    part_dir = chain_dir / part_name
    duration_ms = _part_duration_ms(part_dir)
    chain = load_chain(chain_dir)
    if any(item.get("name") == part_name for item in chain.get("parts", [])):
        return
    append_part(chain_dir, part_name=part_name, duration_ms=duration_ms, reason=reason)


def ensure_chain(root: Path) -> Path:
    """Return chain root, migrating a flat session into part-0001 when needed."""
    root = root.resolve()
    if not root.is_dir():
        raise FileNotFoundError(f"not a directory: {root}")

    if is_part_dir(root):
        root = root.parent

    if is_chain_dir(root):
        from scripts.browser_session.salvage import reconcile_chain

        reconcile_chain(root)
        return root

    if not is_flat_session_dir(root):
        raise ValueError(f"not a resumable session: {root}")

    part_dir = root / "part-0001"
    if not part_dir.is_dir():
        _move_artifacts_into_part(root, part_dir)
    elif not (part_dir / "session.json").is_file() and (root / "session.json").is_file():
        _move_artifacts_into_part(root, part_dir)

    if not chain_path(root).is_file():
        init_chain(root, slug=_slug_from_dir(root))
        _register_existing_part(root, "part-0001", reason="initial")
    return root


def _next_part_index(chain_dir: Path) -> int:
    max_index = 0
    for child in chain_dir.iterdir():
        match = _PART_DIR_RE.match(child.name)
        if match:
            max_index = max(max_index, int(match.group(1)))
    chain = load_chain(chain_dir)
    max_index = max(max_index, len(chain.get("parts", [])))
    return max_index + 1


def open_tab_urls(part_dir: Path) -> list[str]:
    index = load_index(part_dir)
    urls: list[str] = []
    for tab in index.get("tabs", {}).values():
        if tab.get("closed_at_ms") is not None:
            continue
        url = str(tab.get("last_url") or "").strip()
        if url and url not in urls:
            urls.append(url)
    if urls:
        return urls
    meta_path = part_dir / "meta.json"
    if meta_path.is_file():
        meta = json.loads(meta_path.read_text(encoding="utf-8"))
        meta_urls = [str(url) for url in meta.get("urls", []) if str(url).strip()]
        if meta_urls:
            return meta_urls
    return []


def last_part_dir(chain_dir: Path) -> Path | None:
    chain = load_chain(chain_dir)
    parts = chain.get("parts", [])
    if parts:
        return chain_dir / parts[-1]["name"]
    candidates = sorted(
        (child for child in chain_dir.iterdir() if _PART_DIR_RE.match(child.name)),
        key=lambda path: int(_PART_DIR_RE.match(path.name).group(1)),  # type: ignore[union-attr]
    )
    return candidates[-1] if candidates else None


def prepare_resume(root: Path) -> ResumeContext:
    chain_dir = ensure_chain(root)
    chain = load_chain(chain_dir)
    unknown_parts = [
        str(part.get("name"))
        for part in chain.get("parts", [])
        if part.get("duration_known") is False or not _known_duration(part.get("duration_ms"))
    ]
    if unknown_parts or chain.get("duration_status") == "partial":
        names = unknown_parts or [str(part.get("name")) for part in chain.get("parts", [])]
        raise RuntimeError(
            "cannot resume a chain with unknown-duration parts: " + ", ".join(names)
        )
    last_part = last_part_dir(chain_dir)
    if last_part is None:
        raise ValueError(f"chain has no parts to resume from: {chain_dir}")

    part_index = _next_part_index(chain_dir)
    part_dir = chain_dir / f"part-{part_index:04d}"
    if part_dir.exists() and any(part_dir.iterdir()):
        raise RuntimeError(f"refusing to overwrite non-empty part directory: {part_dir}")
    part_dir.mkdir(parents=True, exist_ok=True)

    session = load_session(last_part)
    monitor_index = int(session.get("monitor", {}).get("index", 1))
    fps = int(session.get("clock", {}).get("fps", 30))
    urls = open_tab_urls(last_part)

    return ResumeContext(
        chain_dir=chain_dir,
        part_index=part_index,
        part_dir=part_dir,
        chain_offset_ms=int(chain.get("total_duration_ms", 0)),
        monitor_index=monitor_index,
        fps=fps,
        urls=urls,
        prior_parts=len(chain.get("parts", [])),
    )


def list_resumable_sessions(sessions_dir: Path) -> list[dict[str, Any]]:
    sessions_dir = sessions_dir.resolve()
    if not sessions_dir.is_dir():
        return []
    rows: list[dict[str, Any]] = []
    for child in sorted(sessions_dir.iterdir(), key=lambda path: path.stat().st_mtime, reverse=True):
        if not child.is_dir():
            continue
        try:
            if is_chain_dir(child):
                from scripts.browser_session.salvage import reconcile_chain

                reconcile_chain(child)
                chain = load_chain(child)
                rows.append(
                    {
                        "path": str(child.resolve()),
                        "kind": "chain",
                        "parts": len(chain.get("parts", [])),
                        "total_duration_ms": int(chain.get("total_duration_ms", 0)),
                        "duration_status": chain.get("duration_status", "complete"),
                        "unknown_parts": chain.get("unknown_parts", []),
                    }
                )
            elif is_flat_session_dir(child):
                rows.append(
                    {
                        "path": str(child.resolve()),
                        "kind": "session",
                        "parts": 1,
                        "total_duration_ms": _part_duration_ms(child) or 0,
                        "duration_status": "complete" if _part_duration_ms(child) is not None else "partial",
                    }
                )
        except (OSError, RuntimeError, ValueError, json.JSONDecodeError) as exc:
            rows.append(
                {
                    "path": str(child.resolve()),
                    "kind": "corrupt",
                    "error": str(exc),
                }
            )
            continue
    return rows
