"""Validated in-memory execution plan submitted explicitly by GuideAntsApi."""

from __future__ import annotations

import hashlib
import json
from dataclasses import dataclass, field, replace
from typing import Any

SERVICE_LLAMA = "llama"
AUX_SERVICES = (
    "SpeechTranscription",
    "Embeddings",
    "SpeechSynthesis",
    "ImageGeneration",
)
WARMUP_SERVICE_SECTIONS = (SERVICE_LLAMA,) + AUX_SERVICES


class WarmupPlanValidationError(ValueError):
    pass


@dataclass(frozen=True)
class WarmupServiceSection:
    enabled: bool
    router_alias: str | None = None
    model_path: str | None = None
    model_id: str | None = None
    bundle_id: str | None = None
    extras: dict[str, Any] = field(default_factory=dict)


@dataclass(frozen=True)
class WarmupPlanDocument:
    schema_version: int
    revision: int
    services: dict[str, WarmupServiceSection]

    def with_revision(self, revision: int) -> "WarmupPlanDocument":
        return replace(self, revision=revision)

    def content_fingerprint(self) -> str:
        canonical = json.dumps(
            plan_to_payload(self.with_revision(0)),
            ensure_ascii=True,
            separators=(",", ":"),
            sort_keys=True,
        )
        return hashlib.sha256(canonical.encode("utf-8")).hexdigest()


def aux_section_model_ref(section: WarmupServiceSection) -> str | None:
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
    if not section.enabled:
        return None
    if section_name == SERVICE_LLAMA:
        return (section.router_alias or "").strip() or None
    if section_name == "ImageGeneration":
        return (section.bundle_id or "").strip() or None
    return aux_section_model_ref(section)


def section_should_load(section_name: str, section: WarmupServiceSection | None) -> bool:
    return section is not None and section_execution_ref(section_name, section) is not None


def parse_warmup_plan(payload: Any) -> WarmupPlanDocument:
    if not isinstance(payload, dict):
        raise WarmupPlanValidationError("Warmup plan must be a JSON object.")

    schema_version = payload.get("schemaVersion")
    if schema_version != 1:
        raise WarmupPlanValidationError(f"schemaVersion must be 1; got {schema_version!r}.")

    raw_services = payload.get("services")
    if not isinstance(raw_services, dict):
        raise WarmupPlanValidationError("Warmup plan must contain a services object.")

    unknown = sorted(set(raw_services) - set(WARMUP_SERVICE_SECTIONS))
    if unknown:
        raise WarmupPlanValidationError(
            f"Unknown warmup services: {', '.join(unknown)}."
        )

    missing = [name for name in WARMUP_SERVICE_SECTIONS if name not in raw_services]
    if missing:
        raise WarmupPlanValidationError(
            f"Warmup plan must explicitly include every service; missing: {', '.join(missing)}."
        )

    services: dict[str, WarmupServiceSection] = {}
    for service_name in WARMUP_SERVICE_SECTIONS:
        raw_section = raw_services[service_name]
        if not isinstance(raw_section, dict):
            raise WarmupPlanValidationError(f"services.{service_name} must be an object.")
        enabled = raw_section.get("enabled")
        if not isinstance(enabled, bool):
            raise WarmupPlanValidationError(
                f"services.{service_name}.enabled must be an explicit boolean."
            )

        section = WarmupServiceSection(
            enabled=enabled,
            router_alias=_optional_string(raw_section, "routerAlias"),
            model_path=_optional_string(raw_section, "modelPath"),
            model_id=_optional_string(raw_section, "modelId"),
            bundle_id=_optional_string(raw_section, "bundleId"),
            extras={
                key: value
                for key, value in raw_section.items()
                if key not in {"enabled", "routerAlias", "modelPath", "modelId", "bundleId"}
            },
        )
        if enabled and section_execution_ref(service_name, section) is None:
            raise WarmupPlanValidationError(
                f"services.{service_name} is enabled but has no execution reference."
            )
        services[service_name] = section

    return WarmupPlanDocument(schema_version=1, revision=0, services=services)


def plan_to_payload(document: WarmupPlanDocument) -> dict[str, Any]:
    services: dict[str, dict[str, Any]] = {}
    for service_name in WARMUP_SERVICE_SECTIONS:
        section = document.services[service_name]
        payload: dict[str, Any] = {"enabled": section.enabled}
        if section.router_alias:
            payload["routerAlias"] = section.router_alias
        if section.model_path:
            payload["modelPath"] = section.model_path
        if section.model_id:
            payload["modelId"] = section.model_id
        if section.bundle_id:
            payload["bundleId"] = section.bundle_id
        payload.update(section.extras)
        services[service_name] = payload
    return {
        "schemaVersion": document.schema_version,
        "revision": document.revision,
        "services": services,
    }


def _optional_string(payload: dict[str, Any], key: str) -> str | None:
    value = payload.get(key)
    if value is None:
        return None
    if not isinstance(value, str):
        raise WarmupPlanValidationError(f"{key} must be a string when provided.")
    trimmed = value.strip()
    return trimmed or None
