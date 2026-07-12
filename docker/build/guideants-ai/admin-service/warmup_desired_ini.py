"""Warmup desired-state INI parsing and atomic writes (no FastAPI dependency)."""

from __future__ import annotations

import hashlib
import os
import tempfile
import threading
from dataclasses import dataclass, field
from datetime import datetime, timezone
from typing import Callable

WARMUP_DESIRED_PATH = "/models-local/warmup-desired.ini"
WARMUP_FILE_LOCK = threading.Lock()

SERVICE_LLAMA = "llama"
AUX_SERVICES = (
    "SpeechTranscription",
    "Embeddings",
    "SpeechSynthesis",
    "ImageGeneration",
)
WARMUP_SERVICE_SECTIONS = (SERVICE_LLAMA,) + AUX_SERVICES

VALID_DESIRED = frozenset({"warm", "idle"})
HEADER_VERSION_KEY = "version"
HEADER_REVISION_KEY = "revision"
HEADER_UPDATED_AT_KEY = "updated_at_utc"


@dataclass
class WarmupServiceSection:
    desired: str
    router_alias: str | None = None
    model_id: str | None = None
    bundle_id: str | None = None
    extras: dict[str, str] = field(default_factory=dict)


@dataclass
class WarmupDesiredDocument:
    version: int
    revision: int
    updated_at_utc: str
    sections: dict[str, WarmupServiceSection]

    def content_fingerprint(self) -> str:
        """Hash of section payload excluding revision and updated_at_utc."""
        canonical = serialize_warmup_desired_ini(
            WarmupDesiredDocument(
                version=self.version,
                revision=0,
                updated_at_utc="",
                sections=self.sections,
            ),
            include_header=False,
        )
        return hashlib.sha256(canonical.encode("utf-8")).hexdigest()


@dataclass
class WarmupDesiredWriteResult:
    revision: int
    sha256: str
    changed: bool


class WarmupDesiredValidationError(ValueError):
    pass


def _utc_now_iso() -> str:
    return datetime.now(timezone.utc).replace(microsecond=0).isoformat().replace("+00:00", "Z")


def _normalize_desired(value: str) -> str:
    normalized = value.strip().lower()
    if normalized not in VALID_DESIRED:
        raise WarmupDesiredValidationError(f"desired must be one of {sorted(VALID_DESIRED)}; got '{value}'.")
    return normalized


def _validate_section_name(name: str) -> None:
    if name not in WARMUP_SERVICE_SECTIONS:
        raise WarmupDesiredValidationError(
            f"Unknown warmup service section '{name}'. "
            f"Allowed: {', '.join(WARMUP_SERVICE_SECTIONS)}."
        )


def validate_warmup_desired(document: WarmupDesiredDocument) -> None:
    if document.version != 1:
        raise WarmupDesiredValidationError(f"version must be 1; got {document.version}.")
    if document.revision < 0:
        raise WarmupDesiredValidationError("revision must be non-negative.")

    for section_name, section in document.sections.items():
        _validate_section_name(section_name)
        desired = _normalize_desired(section.desired)
        if desired == "warm":
            if section_name == SERVICE_LLAMA:
                if not (section.router_alias or "").strip():
                    raise WarmupDesiredValidationError(
                        f"[{section_name}] desired=warm requires router_alias."
                    )
            elif section_name == "ImageGeneration":
                if not (section.bundle_id or "").strip():
                    raise WarmupDesiredValidationError(
                        f"[{section_name}] desired=warm requires bundle_id."
                    )
            else:
                if not (section.model_id or "").strip():
                    raise WarmupDesiredValidationError(
                        f"[{section_name}] desired=warm requires model_id."
                    )


def parse_warmup_desired_ini(text: str) -> WarmupDesiredDocument:
    version = 1
    revision = 0
    updated_at_utc = ""
    sections: dict[str, WarmupServiceSection] = {}
    current_name: str | None = None
    current_fields: dict[str, str] = {}

    def flush_current() -> None:
        nonlocal current_name, current_fields
        if not current_name:
            return
        desired_raw = current_fields.get("desired", "idle")
        section = WarmupServiceSection(
            desired=desired_raw,
            router_alias=current_fields.get("router_alias"),
            model_id=current_fields.get("model_id"),
            bundle_id=current_fields.get("bundle_id"),
            extras={
                key: value
                for key, value in current_fields.items()
                if key not in {"desired", "router_alias", "model_id", "bundle_id"}
            },
        )
        sections[current_name] = section
        current_name = None
        current_fields = {}

    for raw_line in text.splitlines():
        line = raw_line.strip()
        if not line or line.startswith("#") or line.startswith(";"):
            continue
        if line.startswith("[") and line.endswith("]"):
            flush_current()
            current_name = line[1:-1].strip()
            continue
        if "=" not in line:
            continue
        key, value = line.split("=", 1)
        key_raw = key.strip()
        key_lower = key_raw.lower()
        value = value.strip()
        if current_name is None:
            if key_lower == HEADER_VERSION_KEY:
                version = int(value)
            elif key_lower == HEADER_REVISION_KEY:
                revision = int(value)
            elif key_lower == HEADER_UPDATED_AT_KEY:
                updated_at_utc = value
            continue
        current_fields[key_lower] = value

    flush_current()
    document = WarmupDesiredDocument(
        version=version,
        revision=revision,
        updated_at_utc=updated_at_utc,
        sections=sections,
    )
    validate_warmup_desired(document)
    return document


def serialize_warmup_desired_ini(
    document: WarmupDesiredDocument,
    *,
    include_header: bool = True,
) -> str:
    lines: list[str] = []
    if include_header:
        lines.extend(
            [
                f"{HEADER_VERSION_KEY} = {document.version}",
                f"{HEADER_REVISION_KEY} = {document.revision}",
                f"{HEADER_UPDATED_AT_KEY} = {document.updated_at_utc}",
                "",
            ]
        )

    for section_name in WARMUP_SERVICE_SECTIONS:
        section = document.sections.get(section_name)
        if section is None:
            continue
        lines.append(f"[{section_name}]")
        lines.append(f"desired = {section.desired}")
        if section.router_alias:
            lines.append(f"router_alias = {section.router_alias}")
        if section.model_id:
            lines.append(f"model_id = {section.model_id}")
        if section.bundle_id:
            lines.append(f"bundle_id = {section.bundle_id}")
        for extra_key in sorted(section.extras.keys()):
            lines.append(f"{extra_key} = {section.extras[extra_key]}")
        lines.append("")

    return "\n".join(lines).rstrip() + "\n"


def commit_warmup_desired_file(
    temp_path: str,
    destination: str,
    payload: str,
    *,
    log_event: Callable[..., None] | None = None,
) -> None:
    try:
        os.replace(temp_path, destination)
        return
    except OSError as replace_err:
        with open(destination, "w", encoding="utf-8") as handle:
            handle.write(payload)
            handle.flush()
            os.fsync(handle.fileno())
        if log_event is not None:
            log_event(
                "warmup_desired_commit_fallback",
                reason="os_replace_failed",
                destination=destination,
                replaceError=str(replace_err),
            )
        if os.path.exists(temp_path):
            try:
                os.remove(temp_path)
            except OSError:
                pass


def resolve_warmup_desired_path() -> str:
    explicit = (os.getenv("GA_WARMUP_DESIRED_PATH") or "").strip()
    return explicit or WARMUP_DESIRED_PATH


def read_warmup_desired() -> WarmupDesiredDocument | None:
    path = resolve_warmup_desired_path()
    with WARMUP_FILE_LOCK:
        if not os.path.exists(path):
            return None
        with open(path, "r", encoding="utf-8") as handle:
            content = handle.read()
        return parse_warmup_desired_ini(content)


def write_warmup_desired(
    document: WarmupDesiredDocument,
    *,
    bump_revision: bool = True,
    expected_revision: int | None = None,
    log_event: Callable[..., None] | None = None,
) -> WarmupDesiredWriteResult:
    validate_warmup_desired(document)
    path = resolve_warmup_desired_path()

    with WARMUP_FILE_LOCK:
        prior: WarmupDesiredDocument | None = None
        if os.path.exists(path):
            with open(path, "r", encoding="utf-8") as handle:
                prior = parse_warmup_desired_ini(handle.read())

        if expected_revision is not None:
            current_revision = prior.revision if prior is not None else 0
            if current_revision != expected_revision:
                raise ValueError(
                    f"Warmup desired revision mismatch. Expected {expected_revision}, "
                    f"current revision is {current_revision}."
                )

        next_revision = document.revision
        changed = True
        if bump_revision:
            base_revision = prior.revision if prior is not None else 0
            if prior is not None and prior.content_fingerprint() == document.content_fingerprint():
                next_revision = prior.revision
                changed = False
            else:
                next_revision = base_revision + 1 if document.revision <= base_revision else document.revision

        final_document = WarmupDesiredDocument(
            version=document.version,
            revision=next_revision,
            updated_at_utc=document.updated_at_utc or _utc_now_iso(),
            sections=document.sections,
        )
        if changed and bump_revision and final_document.updated_at_utc == document.updated_at_utc:
            final_document = WarmupDesiredDocument(
                version=final_document.version,
                revision=final_document.revision,
                updated_at_utc=_utc_now_iso(),
                sections=final_document.sections,
            )

        payload = serialize_warmup_desired_ini(final_document)
        payload_before = (
            serialize_warmup_desired_ini(prior) if prior is not None else ""
        )
        if payload == payload_before:
            changed = False

        sha256 = hashlib.sha256(payload.encode("utf-8")).hexdigest()
        directory = os.path.dirname(path)
        if directory:
            os.makedirs(directory, exist_ok=True)

        temp_fd, temp_path = tempfile.mkstemp(
            dir=directory if directory else None,
            prefix="warmup-desired-",
            suffix=".ini.tmp",
        )
        try:
            with os.fdopen(temp_fd, "w", encoding="utf-8") as handle:
                handle.write(payload)
                handle.flush()
                os.fsync(handle.fileno())
            commit_warmup_desired_file(temp_path, path, payload, log_event=log_event)
        finally:
            if os.path.exists(temp_path):
                try:
                    os.remove(temp_path)
                except OSError:
                    pass

        if log_event is not None:
            log_event(
                "warmup_desired_write_applied",
                revision=final_document.revision,
                changed=changed,
                sha256=sha256,
            )

        return WarmupDesiredWriteResult(
            revision=final_document.revision,
            sha256=sha256,
            changed=changed,
        )


def put_warmup_desired_text(
    text: str,
    *,
    expected_revision: int | None = None,
    bump_revision: bool = True,
    log_event: Callable[..., None] | None = None,
) -> tuple[WarmupDesiredDocument, WarmupDesiredWriteResult]:
    document = parse_warmup_desired_ini(text)
    result = write_warmup_desired(
        document,
        bump_revision=bump_revision,
        expected_revision=expected_revision,
        log_event=log_event,
    )
    written = read_warmup_desired()
    if written is None:
        raise RuntimeError("Warmup desired INI missing immediately after write.")
    return written, result
