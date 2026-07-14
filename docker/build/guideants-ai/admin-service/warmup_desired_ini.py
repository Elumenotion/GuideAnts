"""Warmup runtime INI — execution plan on disk (load ref = on, enabled = off = off)."""

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

HEADER_VERSION_KEY = "version"
HEADER_REVISION_KEY = "revision"
HEADER_UPDATED_AT_KEY = "updated_at_utc"
ENABLED_OFF = "off"

# Legacy INI only — never written on serialize.
LEGACY_DESIRED_WARM = "warm"
LEGACY_DESIRED_IDLE = "idle"


@dataclass
class WarmupServiceSection:
    """One service entry in the runtime execution plan."""

    enabled: bool | None = None
    router_alias: str | None = None
    model_path: str | None = None
    model_id: str | None = None
    bundle_id: str | None = None
    desired: str | None = None  # legacy read only
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


def aux_section_model_ref(section: WarmupServiceSection) -> str | None:
    """Disk-shaped ref for aux services (model_path preferred, model_id legacy)."""
    if (section.model_path or "").strip():
        return section.model_path.strip()
    if (section.model_id or "").strip():
        return section.model_id.strip()
    return None


def aux_section_load_request(
    section: WarmupServiceSection,
    *,
    service: str | None = None,
) -> tuple[str | None, str]:
    """Return (ref, load_json_field) for aux engine /admin/load."""
    if (section.model_path or "").strip():
        trimmed = section.model_path.strip()
        if trimmed.lower().endswith(".gguf"):
            return trimmed, "model_path"
        if service == "Embeddings":
            return trimmed, "model_id"
        return trimmed, "model_path"
    if (section.model_id or "").strip():
        trimmed = section.model_id.strip()
        if trimmed.lower().endswith(".gguf"):
            return trimmed, "model_path"
        return trimmed, "model_id"
    return None, "model_path"


def section_execution_ref(section_name: str, section: WarmupServiceSection) -> str | None:
    """Load instruction from the plan, or None when the service should be off."""
    if section_is_off(section):
        return None
    if section_name == SERVICE_LLAMA:
        return (section.router_alias or "").strip() or None
    if section_name == "ImageGeneration":
        return (section.bundle_id or "").strip() or None
    return aux_section_model_ref(section)


def section_is_off(section: WarmupServiceSection) -> bool:
    if section.enabled is False:
        return True
    enabled_raw = (section.extras.get("enabled") or "").strip().lower()
    if enabled_raw == ENABLED_OFF:
        return True
    if section.desired and section.desired.strip().lower() == LEGACY_DESIRED_IDLE:
        return True
    return False


def section_should_load(section_name: str, section: WarmupServiceSection | None) -> bool:
    if section is None:
        return False
    return section_execution_ref(section_name, section) is not None


def _utc_now_iso() -> str:
    return datetime.now(timezone.utc).replace(microsecond=0).isoformat().replace("+00:00", "Z")


def _validate_section_name(name: str) -> None:
    if name not in WARMUP_SERVICE_SECTIONS:
        raise WarmupDesiredValidationError(
            f"Unknown warmup service section '{name}'. "
            f"Allowed: {', '.join(WARMUP_SERVICE_SECTIONS)}."
        )


def validate_warmup_desired(document: WarmupDesiredDocument) -> None:
    """Validate documents submitted for write (API PUT). Disk reads do not validate."""
    if document.version != 1:
        raise WarmupDesiredValidationError(f"version must be 1; got {document.version}.")
    if document.revision < 0:
        raise WarmupDesiredValidationError("revision must be non-negative.")

    for section_name, section in document.sections.items():
        _validate_section_name(section_name)
        if section_is_off(section):
            continue
        ref = section_execution_ref(section_name, section)
        if not ref:
            raise WarmupDesiredValidationError(
                f"[{section_name}] must set enabled = off or include a load ref "
                f"(router_alias, model_path, or bundle_id)."
            )
        legacy_desired = (section.desired or "").strip().lower()
        if legacy_desired == LEGACY_DESIRED_WARM and not ref:
            raise WarmupDesiredValidationError(
                f"[{section_name}] legacy desired=warm without a load ref is invalid."
            )


def _coerce_enabled_field(fields: dict[str, str]) -> bool | None:
    enabled_raw = fields.get("enabled")
    if enabled_raw is None:
        return None
    normalized = enabled_raw.strip().lower()
    if normalized == ENABLED_OFF:
        return False
    if normalized in {"on", "true", "1"}:
        return True
    return None


def _parse_warmup_desired_ini_raw(text: str) -> WarmupDesiredDocument:
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
        enabled = _coerce_enabled_field(current_fields)
        desired_raw = current_fields.get("desired")
        section = WarmupServiceSection(
            enabled=enabled,
            desired=desired_raw,
            router_alias=current_fields.get("router_alias"),
            model_path=current_fields.get("model_path"),
            model_id=current_fields.get("model_id"),
            bundle_id=current_fields.get("bundle_id"),
            extras={
                key: value
                for key, value in current_fields.items()
                if key
                not in {
                    "desired",
                    "enabled",
                    "router_alias",
                    "model_path",
                    "model_id",
                    "bundle_id",
                }
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
        key_lower = key.strip().lower()
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
    return WarmupDesiredDocument(
        version=version,
        revision=revision,
        updated_at_utc=updated_at_utc,
        sections=sections,
    )


def parse_warmup_desired_ini(text: str) -> WarmupDesiredDocument:
    document = _parse_warmup_desired_ini_raw(text)
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
        if section_is_off(section):
            lines.append(f"enabled = {ENABLED_OFF}")
        if section.router_alias:
            lines.append(f"router_alias = {section.router_alias}")
        if section.model_path:
            lines.append(f"model_path = {section.model_path}")
        if section.model_id:
            lines.append(f"model_id = {section.model_id}")
        if section.bundle_id:
            lines.append(f"bundle_id = {section.bundle_id}")
        for extra_key in sorted(section.extras.keys()):
            if extra_key == "enabled":
                continue
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
    """Read persisted INI without validation — stale volume state must not brick startup."""
    path = resolve_warmup_desired_path()
    with WARMUP_FILE_LOCK:
        if not os.path.exists(path):
            return None
        with open(path, "r", encoding="utf-8") as handle:
            return _parse_warmup_desired_ini_raw(handle.read())


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
                prior = _parse_warmup_desired_ini_raw(handle.read())

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
