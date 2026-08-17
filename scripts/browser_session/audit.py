"""Read-only session integrity audit with stable rejection codes."""

from __future__ import annotations

from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

from scripts.browser_session.media_probe import probe_part_media, sha256_file
from scripts.browser_session.schema import (
    ERROR_AUDIO_COVERAGE_GAP,
    ERROR_COMPACT_SYNTHETIC_AUDIO,
    ERROR_COMPACT_UNVERIFIED,
    ERROR_MEDIA_PROBE_INCOMPLETE,
    ERROR_PLAYWRIGHT_EVIDENCE_EMPTY,
    ERROR_SESSION_INTERRUPTED,
    ERROR_SOURCE_HASH_CHANGED,
    ERROR_SYNTHETIC_MEDIA_FILTER,
    SESSION_STATUS_COMPLETE,
    load_index,
    load_session,
)


@dataclass
class AuditFinding:
    code: str
    message: str
    severity: str = "error"  # error | warning

    def to_dict(self) -> dict[str, str]:
        return {"code": self.code, "message": self.message, "severity": self.severity}


@dataclass
class AuditReport:
    session_id: str
    session_dir: str
    passed: bool
    findings: list[AuditFinding] = field(default_factory=list)
    media: dict[str, Any] = field(default_factory=dict)
    coverage: dict[str, Any] = field(default_factory=dict)

    def to_dict(self) -> dict[str, Any]:
        return {
            "session_id": self.session_id,
            "session_dir": self.session_dir,
            "passed": self.passed,
            "findings": [item.to_dict() for item in self.findings],
            "media": self.media,
            "coverage": self.coverage,
        }

    def rejection_codes(self) -> list[str]:
        return [item.code for item in self.findings if item.severity == "error"]


def _av_coverage_tolerance_ms(fps: int = 30) -> int:
    return max(40, int(round(2000 / fps)))


def _check_compact_outputs(session_dir: Path, session: dict[str, Any], findings: list[AuditFinding]) -> None:
    compact = session.get("compact") or {}
    edit_map_path = session_dir / "edit_map.json"
    if not edit_map_path.is_file() and not compact:
        return

    import json

    edit_map = json.loads(edit_map_path.read_text(encoding="utf-8")) if edit_map_path.is_file() else {}
    if compact.get("verified") or edit_map.get("verified"):
        proof = edit_map.get("proof") or {}
        if not proof.get("content_verified"):
            findings.append(
                AuditFinding(
                    code=ERROR_COMPACT_UNVERIFIED,
                    message="compact outputs claim verified but lack content proof report",
                )
            )

    compact_narration = session_dir / "narration.compact.wav"
    source_narration = session_dir / "narration.wav"
    if compact_narration.is_file() and source_narration.is_file():
        from scripts.browser_session.media_probe import probe_wav

        compact_dur = probe_wav(compact_narration)["duration_ms"]
        source_dur = probe_wav(source_narration)["duration_ms"]
        video_dur = int((session.get("media") or {}).get("video", {}).get("duration_ms", 0))
        if compact_dur > source_dur + _av_coverage_tolerance_ms():
            findings.append(
                AuditFinding(
                    code=ERROR_COMPACT_SYNTHETIC_AUDIO,
                    message=(
                        f"compact narration ({compact_dur} ms) exceeds source ({source_dur} ms); "
                        "likely synthetic padding"
                    ),
                )
            )
        if video_dur > 0 and compact_dur >= video_dur and source_dur < video_dur - _av_coverage_tolerance_ms():
            findings.append(
                AuditFinding(
                    code=ERROR_COMPACT_SYNTHETIC_AUDIO,
                    message=(
                        f"compact audio ({compact_dur} ms) matches video ({video_dur} ms) "
                        f"but source audio only covers {source_dur} ms"
                    ),
                )
            )

    removed = edit_map.get("removed") or []
    kept = edit_map.get("kept") or []
    if not removed and kept:
        source_duration = int(edit_map.get("source_duration_ms", 0))
        compact_duration = int(edit_map.get("compact_duration_ms", 0))
        if source_duration > 0 and abs(source_duration - compact_duration) <= _av_coverage_tolerance_ms():
            findings.append(
                AuditFinding(
                    code=ERROR_COMPACT_UNVERIFIED,
                    message="compact re-encode with zero removal provides no editorial benefit",
                    severity="warning",
                )
            )


def audit_session(session_dir: Path) -> AuditReport:
    """Run a read-only integrity audit on a session directory."""
    session_dir = session_dir.resolve()
    session = load_session(session_dir)
    findings: list[AuditFinding] = []
    fps = int(session.get("clock", {}).get("fps", 30))
    tolerance = _av_coverage_tolerance_ms(fps)

    top_status = session.get("status", "")
    media_status = (session.get("media") or {}).get("status", "")
    probe_status = (session.get("media") or {}).get("probe_status", "")

    if top_status == "interrupted" or media_status == "interrupted":
        findings.append(
            AuditFinding(
                code=ERROR_SESSION_INTERRUPTED,
                message=f"session status is interrupted (top={top_status!r}, media={media_status!r})",
            )
        )

    media = probe_part_media(session_dir)
    video_dur = int((media.get("video") or {}).get("duration_ms", 0))
    narration_dur = int((media.get("narration") or {}).get("duration_ms", 0))

    if media.get("status") not in ("complete",) and probe_status != "complete":
        if media.get("status") not in ("missing_narration",):
            findings.append(
                AuditFinding(
                    code=ERROR_MEDIA_PROBE_INCOMPLETE,
                    message=f"media probe status: {media.get('status')}",
                )
            )

    coverage: dict[str, Any] = {
        "video_duration_ms": video_dur,
        "narration_duration_ms": narration_dur,
        "tolerance_ms": tolerance,
    }

    if video_dur > 0 and narration_dur > 0:
        gap = video_dur - narration_dur
        coverage["av_gap_ms"] = gap
        if gap > tolerance:
            findings.append(
                AuditFinding(
                    code=ERROR_AUDIO_COVERAGE_GAP,
                    message=(
                        f"audio covers {narration_dur} ms but video covers {video_dur} ms "
                        f"(gap {gap} ms > tolerance {tolerance} ms)"
                    ),
                )
            )

    index = load_index(session_dir)
    checkpoint_count = len(index.get("checkpoints", []))
    coverage["checkpoint_count"] = checkpoint_count
    if checkpoint_count == 0:
        findings.append(
            AuditFinding(
                code=ERROR_PLAYWRIGHT_EVIDENCE_EMPTY,
                message="no browser checkpoints recorded",
            )
        )

    stored_video_hash = (session.get("media") or {}).get("video", {}).get("sha256")
    video_path = session_dir / "video.mp4"
    if stored_video_hash and video_path.is_file():
        actual = sha256_file(video_path)
        if actual != stored_video_hash:
            findings.append(
                AuditFinding(
                    code=ERROR_SOURCE_HASH_CHANGED,
                    message="video file hash does not match session.json",
                )
            )

    _check_compact_outputs(session_dir, session, findings)

    passed = not any(item.severity == "error" for item in findings)
    return AuditReport(
        session_id=session_dir.name,
        session_dir=str(session_dir),
        passed=passed,
        findings=findings,
        media=media,
        coverage=coverage,
    )


def require_audit_pass(session_dir: Path, *, operation: str) -> AuditReport:
    """Raise RuntimeError if audit fails; return report on success."""
    report = audit_session(session_dir)
    if not report.passed:
        codes = ", ".join(report.rejection_codes())
        raise RuntimeError(f"{operation} blocked by integrity audit: {codes}")
    return report


def is_safe_for_compact(session_dir: Path) -> bool:
    """Return True only when audit passes with complete required coverage."""
    report = audit_session(session_dir)
    if not report.passed:
        return False
    session = load_session(session_dir)
    if session.get("status") != SESSION_STATUS_COMPLETE:
        return False
    media = report.media
    return media.get("status") == "complete"
