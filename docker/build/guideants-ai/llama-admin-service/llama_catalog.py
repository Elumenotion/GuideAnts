"""Curated llama catalog loading, validation, and quant discovery."""

from __future__ import annotations

import json
import os
from functools import lru_cache
from pathlib import Path
from typing import Any

from jsonschema import Draft202012Validator

from guideants_hf.quant_grouping import (
    QuantGroupingError,
    enrich_quant_guidance,
    group_repository_quants,
    resolve_projector,
)
from guideants_hf.repository import (
    HuggingFaceAccessError,
    list_repository_artifacts_at_revision,
    resolve_repository_commit,
)


DEFAULT_MANIFEST_PATH = "/app/llama-admin-service/catalog/manifest.json"
DEFAULT_SCHEMA_PATH = "/app/llama-admin-service/catalog/schema.llama.json"

_VISION_PRESET_KEYS = frozenset({"image-min-tokens"})
_MTP_PRESET_KEYS = frozenset({"spec-type", "spec-draft-n-max"})


class CatalogValidationError(ValueError):
    def __init__(self, message: str) -> None:
        super().__init__(message)


class CatalogDefinitionError(LookupError):
    def __init__(self, code: str, message: str) -> None:
        super().__init__(message)
        self.code = code


def _repo_relative_paths() -> tuple[Path, Path]:
    service_root = Path(__file__).resolve().parent
    manifest = service_root / "catalog" / "manifest.json"
    schema = service_root / "catalog" / "schema.llama.json"
    return manifest, schema


def manifest_path() -> str:
    explicit = (os.getenv("GA_LLAMA_CATALOG_MANIFEST_PATH") or "").strip()
    if explicit:
        return explicit
    default = DEFAULT_MANIFEST_PATH
    if os.path.isfile(default):
        return default
    local_manifest, _ = _repo_relative_paths()
    return str(local_manifest)


def schema_path() -> str:
    explicit = (os.getenv("GA_LLAMA_CATALOG_SCHEMA_PATH") or "").strip()
    if explicit:
        return explicit
    default = DEFAULT_SCHEMA_PATH
    if os.path.isfile(default):
        return default
    _, local_schema = _repo_relative_paths()
    return str(local_schema)


@lru_cache(maxsize=1)
def _load_schema() -> dict[str, Any]:
    path = schema_path()
    with open(path, "r", encoding="utf-8") as handle:
        schema = json.load(handle)
    Draft202012Validator.check_schema(schema)
    return schema


def validate_manifest_instance(manifest: dict[str, Any]) -> None:
    schema = _load_schema()
    validator = Draft202012Validator(schema)
    errors = sorted(validator.iter_errors(manifest), key=lambda err: list(err.path))
    if errors:
        first = errors[0]
        raise CatalogValidationError(f"Manifest validation failed at {list(first.path)}: {first.message}")

    models = manifest.get("models")
    if not isinstance(models, list):
        raise CatalogValidationError("Manifest models must be an array.")

    seen_ids: set[str] = set()
    seen_catalog_model_ids: set[str] = set()
    seen_router_model_ids: set[str] = set()
    for model in models:
        if not isinstance(model, dict):
            raise CatalogValidationError("Each manifest model must be an object.")
        model_id = str(model.get("id", "")).strip()
        if model_id in seen_ids:
            raise CatalogValidationError(f"Duplicate catalog id '{model_id}'.")
        seen_ids.add(model_id)

        defaults = model.get("defaults")
        if not isinstance(defaults, dict):
            raise CatalogValidationError(f"Model '{model_id}' is missing defaults.")
        catalog_model_id = str(defaults.get("catalogModelId", "")).strip()
        router_model_id = str(defaults.get("routerModelId", "")).strip()
        if catalog_model_id in seen_catalog_model_ids:
            raise CatalogValidationError(f"Duplicate catalogModelId '{catalog_model_id}'.")
        if router_model_id in seen_router_model_ids:
            raise CatalogValidationError(f"Duplicate routerModelId '{router_model_id}'.")
        seen_catalog_model_ids.add(catalog_model_id)
        seen_router_model_ids.add(router_model_id)

        preset = defaults.get("routerPreset")
        if not isinstance(preset, dict) or "ctx-size" not in preset:
            raise CatalogValidationError(f"Model '{model_id}' must declare routerPreset.ctx-size.")

        mmproj = defaults.get("mmproj")
        has_projector = mmproj is not None
        has_vision = any(key in preset for key in _VISION_PRESET_KEYS)
        has_mtp = any(key in preset for key in _MTP_PRESET_KEYS)

        if has_mtp:
            if preset.get("spec-type") != "draft-mtp":
                raise CatalogValidationError(f"MTP model '{model_id}' must set spec-type=draft-mtp.")
            if has_projector and not has_vision:
                raise CatalogValidationError(
                    f"MTP model '{model_id}' with mmproj must declare image-min-tokens in routerPreset."
                )
            if not has_projector and has_vision:
                raise CatalogValidationError(
                    f"MTP model '{model_id}' declares image-min-tokens without mmproj."
                )
        elif has_projector and not has_vision:
            raise CatalogValidationError(
                f"Vision model '{model_id}' with mmproj must declare image-min-tokens in routerPreset."
            )
        elif not has_projector and has_vision:
            raise CatalogValidationError(
                f"Model '{model_id}' declares image-min-tokens without mmproj."
            )


def load_manifest() -> dict[str, Any]:
    path = manifest_path()
    with open(path, "r", encoding="utf-8") as handle:
        manifest = json.load(handle)
    if not isinstance(manifest, dict):
        raise CatalogValidationError("Manifest root must be an object.")
    validate_manifest_instance(manifest)
    return manifest


@lru_cache(maxsize=1)
def cached_manifest() -> dict[str, Any]:
    return load_manifest()


def get_definition(catalog_id: str, *, catalog_version: str | None = None) -> dict[str, Any]:
    manifest = cached_manifest()
    if catalog_version is not None and catalog_version.strip():
        if manifest.get("version") != catalog_version.strip():
            raise CatalogDefinitionError(
                "CATALOG_VERSION_MISMATCH",
                f"Catalog version '{catalog_version}' does not match manifest version '{manifest.get('version')}'.",
            )

    for model in manifest.get("models", []):
        if isinstance(model, dict) and model.get("id") == catalog_id:
            return model

    raise CatalogDefinitionError(
        "CATALOG_DEFINITION_NOT_FOUND",
        f"Catalog definition '{catalog_id}' was not found.",
    )


def build_catalog_response() -> dict[str, Any]:
    manifest = cached_manifest()
    return {
        "schemaVersion": manifest.get("schemaVersion"),
        "task": manifest.get("task"),
        "catalogVersion": manifest.get("version"),
        "models": manifest.get("models", []),
    }


def resolve_definition_quants(
    catalog_id: str,
    hf_token: str | None,
    *,
    catalog_version: str | None = None,
    resolved_revision: str | None = None,
) -> dict[str, Any]:
    definition = get_definition(catalog_id, catalog_version=catalog_version)
    source = definition.get("source")
    if not isinstance(source, dict):
        raise CatalogDefinitionError("CATALOG_DEFINITION_NOT_FOUND", "Definition source is missing.")

    repository = str(source.get("repository", "")).strip()
    requested_revision = str(source.get("revision") or "main").strip() or "main"
    if not repository:
        raise CatalogDefinitionError("CATALOG_DEFINITION_NOT_FOUND", "Definition repository is missing.")

    try:
        if resolved_revision is not None and resolved_revision.strip():
            resolved_revision = resolved_revision.strip()
            artifacts = list_repository_artifacts_at_revision(repository, resolved_revision, hf_token)
        else:
            resolved_revision = resolve_repository_commit(repository, requested_revision, hf_token)
            artifacts = list_repository_artifacts_at_revision(repository, resolved_revision, hf_token)
        quants = group_repository_quants(artifacts)
        quant_metadata = definition.get("quantMetadata")
        if isinstance(quant_metadata, dict):
            quants = enrich_quant_guidance(quants, quant_metadata)

        defaults = definition.get("defaults")
        mmproj_spec = defaults.get("mmproj") if isinstance(defaults, dict) else None

        def _resolve_external(repo: str, rev: str, token: str | None) -> list[dict[str, Any]]:
            commit = resolve_repository_commit(repo, rev, token)
            return list_repository_artifacts_at_revision(repo, commit, token)

        projector = resolve_projector(
            mmproj_spec if isinstance(mmproj_spec, dict) else None,
            model_repository=repository,
            model_revision=resolved_revision,
            model_files=artifacts,
            token=hf_token,
            resolve_external_files=_resolve_external,
        )
    except HuggingFaceAccessError:
        raise
    except QuantGroupingError as exc:
        raise CatalogDefinitionError(exc.code, str(exc)) from exc

    return {
        "catalogId": catalog_id,
        "repository": repository,
        "requestedRevision": requested_revision,
        "resolvedRevision": resolved_revision,
        "quants": quants,
        "projector": projector,
    }


def reject_manifest_with_file_arrays(payload: dict[str, Any]) -> None:
    forbidden_roots = {"files", "variants", "capabilities", "modelFiles", "mmprojFiles"}
    if forbidden_roots.intersection(payload.keys()):
        raise CatalogValidationError("Manifest must not contain discovered file arrays or capability booleans.")
