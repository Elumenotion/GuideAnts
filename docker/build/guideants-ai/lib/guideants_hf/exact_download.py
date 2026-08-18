"""Exact ordered artifact download, staging, integrity, and activation."""

from __future__ import annotations

import hashlib
import json
import os
import re
import shutil
from dataclasses import dataclass
from typing import Any, Callable

from guideants_hf.operation_journal import hash_immutable_input
from guideants_hf.path_safety import PathSafetyError, destination_name, ensure_inside_root, normalize_repository_relative_path
from guideants_hf.transport import IncompleteDownloadError, download_hf_file


class ExactDownloadError(ValueError):
    def __init__(self, code: str, message: str) -> None:
        super().__init__(message)
        self.code = code


@dataclass(frozen=True)
class ArtifactSpec:
    repository_path: str
    destination_abs: str
    expected_size: int | None = None
    digest: str | None = None


def _resume_meta_path(temp_path: str) -> str:
    return temp_path + ".meta.json"


def _resume_identity(
    *,
    operation_id: str,
    repository: str,
    resolved_revision: str,
    repository_path: str,
    expected_size: int | None,
    digest: str | None,
) -> dict[str, Any]:
    return {
        "operationId": operation_id,
        "repository": repository,
        "resolvedRevision": resolved_revision,
        "repositoryPath": repository_path,
        "expectedSize": expected_size,
        "digest": digest,
    }


def _resume_identity_for_match(
    *,
    repository: str,
    resolved_revision: str,
    repository_path: str,
    expected_size: int | None,
    digest: str | None,
) -> dict[str, Any]:
    return {
        "repository": repository,
        "resolvedRevision": resolved_revision,
        "repositoryPath": repository_path,
        "expectedSize": expected_size,
        "digest": digest,
    }


def resume_metadata_matches(
    meta_path: str,
    *,
    operation_id: str,
    repository: str,
    resolved_revision: str,
    repository_path: str,
    expected_size: int | None,
    digest: str | None,
) -> bool:
    if not os.path.isfile(meta_path):
        return False
    try:
        with open(meta_path, "r", encoding="utf-8") as handle:
            stored = json.load(handle)
    except (OSError, json.JSONDecodeError):
        return False
    if not isinstance(stored, dict):
        return False
    expected = _resume_identity_for_match(
        repository=repository,
        resolved_revision=resolved_revision,
        repository_path=repository_path,
        expected_size=expected_size,
        digest=digest,
    )
    # operationId is audit-only; retries may reuse partial bytes across operation ids.
    comparable = {key: stored.get(key) for key in expected}
    return comparable == expected


def write_resume_metadata(
    meta_path: str,
    *,
    operation_id: str,
    repository: str,
    resolved_revision: str,
    repository_path: str,
    expected_size: int | None,
    digest: str | None,
) -> None:
    payload = _resume_identity(
        operation_id=operation_id,
        repository=repository,
        resolved_revision=resolved_revision,
        repository_path=repository_path,
        expected_size=expected_size,
        digest=digest,
    )
    with open(meta_path, "w", encoding="utf-8") as handle:
        json.dump(payload, handle, ensure_ascii=True, sort_keys=True)
        handle.flush()
        os.fsync(handle.fileno())


def clear_resume_artifacts(temp_path: str) -> None:
    meta_path = _resume_meta_path(temp_path)
    for path in (temp_path, meta_path):
        if os.path.exists(path):
            try:
                os.remove(path)
            except OSError:
                pass


_SHARD_RE = re.compile(r"-(\d{5})-of-(\d{5})\.gguf$", re.IGNORECASE)


def validate_shard_group(model_files: list[str]) -> None:
    if len(model_files) == 1:
        return
    shard_pattern: list[tuple[int, int, str]] = []
    total: int | None = None
    for path in model_files:
        leaf = destination_name(path)
        match = _SHARD_RE.search(leaf)
        if not match:
            raise ExactDownloadError("INCOMPLETE_QUANT_GROUP", f"Sharded group member '{leaf}' is missing shard suffix.")
        index = int(match.group(1))
        count = int(match.group(2))
        if total is None:
            total = count
        elif total != count:
            raise ExactDownloadError("INCOMPLETE_QUANT_GROUP", "Mixed shard totals in model file group.")
        shard_pattern.append((index, count, leaf))
    if total is None:
        raise ExactDownloadError("INCOMPLETE_QUANT_GROUP", "Shard group is empty.")
    indices = sorted(index for index, _, _ in shard_pattern)
    expected = list(range(1, total + 1))
    if indices != expected:
        raise ExactDownloadError("INCOMPLETE_QUANT_GROUP", "Incomplete or duplicate shard indices in model file group.")


def verify_file_integrity(path: str, *, expected_size: int | None, digest: str | None) -> None:
    if not os.path.isfile(path):
        raise ExactDownloadError("ARTIFACT_MISSING", f"Expected artifact is missing: {os.path.basename(path)}")
    actual_size = os.path.getsize(path)
    if expected_size is not None and actual_size != expected_size:
        raise ExactDownloadError(
            "ARTIFACT_SIZE_MISMATCH",
            f"Artifact '{os.path.basename(path)}' size {actual_size} does not match expected {expected_size}.",
        )
    if digest:
        normalized = digest.strip().lower()
        if normalized.startswith("sha256:"):
            with open(path, "rb") as handle:
                actual = hashlib.sha256(handle.read()).hexdigest()
            expected = normalized.split(":", 1)[1]
            if actual != expected:
                raise ExactDownloadError(
                    "ARTIFACT_DIGEST_MISMATCH",
                    f"Artifact '{os.path.basename(path)}' digest does not match.",
                )


def artifact_is_installed(spec: ArtifactSpec) -> bool:
    try:
        verify_file_integrity(spec.destination_abs, expected_size=spec.expected_size, digest=spec.digest)
        return True
    except ExactDownloadError:
        return False


def staged_artifact_path(staging_dir: str, spec: ArtifactSpec) -> str | None:
    staged_name = destination_name(spec.repository_path)
    staged_src = os.path.abspath(os.path.join(staging_dir, staged_name))
    try:
        ensure_inside_root(staging_dir, staged_src)
        verify_file_integrity(staged_src, expected_size=spec.expected_size, digest=spec.digest)
        return staged_src
    except (PathSafetyError, ExactDownloadError):
        return None


def build_artifact_specs(
    *,
    model_files: list[str],
    mmproj_files: list[str],
    companion_files: list[str] | None = None,
    store_root: str,
    target_subdir: str,
    artifact_metadata: list[dict[str, Any]] | None,
) -> tuple[str, list[ArtifactSpec], list[ArtifactSpec], list[ArtifactSpec]]:
    from guideants_hf.path_safety import validate_ordered_artifact_paths

    validate_shard_group(model_files)
    target_dir, model_pairs, mmproj_pairs, companion_pairs = validate_ordered_artifact_paths(
        model_files,
        mmproj_files,
        companion_files,
        store_root=store_root,
        target_subdir=target_subdir,
    )
    metadata_by_path: dict[str, dict[str, Any]] = {}
    if artifact_metadata:
        for item in artifact_metadata:
            if not isinstance(item, dict):
                continue
            path = item.get("path")
            if isinstance(path, str) and path.strip():
                metadata_by_path[normalize_repository_relative_path(path)] = item

    def to_specs(pairs: list[tuple[str, str]]) -> list[ArtifactSpec]:
        specs: list[ArtifactSpec] = []
        for repo_path, dest_abs in pairs:
            meta = metadata_by_path.get(repo_path, {})
            size = meta.get("size")
            digest = meta.get("digest") or meta.get("etag")
            specs.append(
                ArtifactSpec(
                    repository_path=repo_path,
                    destination_abs=dest_abs,
                    expected_size=size if isinstance(size, int) else None,
                    digest=digest if isinstance(digest, str) else None,
                )
            )
        return specs

    return target_dir, to_specs(model_pairs), to_specs(mmproj_pairs), to_specs(companion_pairs)


def stage_download_file(
    *,
    repository: str,
    resolved_revision: str,
    spec: ArtifactSpec,
    staging_dir: str,
    token: str | None,
    operation_id: str,
    progress_callback: Callable[[int], None] | None = None,
) -> str:
    ensure_inside_root(staging_dir, staging_dir)
    os.makedirs(staging_dir, exist_ok=True)
    if artifact_is_installed(spec):
        return spec.destination_abs
    staged_name = destination_name(spec.repository_path)
    staged_dest = os.path.abspath(os.path.join(staging_dir, staged_name))
    ensure_inside_root(staging_dir, staged_dest)

    temp_path = staged_dest + ".tmp"
    meta_path = _resume_meta_path(temp_path)
    if os.path.exists(temp_path) and not resume_metadata_matches(
        meta_path,
        operation_id=operation_id,
        repository=repository,
        resolved_revision=resolved_revision,
        repository_path=spec.repository_path,
        expected_size=spec.expected_size,
        digest=spec.digest,
    ):
        clear_resume_artifacts(temp_path)

    write_resume_metadata(
        meta_path,
        operation_id=operation_id,
        repository=repository,
        resolved_revision=resolved_revision,
        repository_path=spec.repository_path,
        expected_size=spec.expected_size,
        digest=spec.digest,
    )

    try:
        download_hf_file(
            repository,
            spec.repository_path,
            staged_dest,
            token,
            revision=resolved_revision,
            progress_callback=progress_callback,
            expected_size=spec.expected_size,
        )
    except IncompleteDownloadError as exc:
        raise ExactDownloadError(
            "DOWNLOAD_TRUNCATED",
            f"{exc}. Partial file preserved for resume.",
        ) from exc
    verify_file_integrity(staged_dest, expected_size=spec.expected_size, digest=spec.digest)
    if os.path.exists(meta_path):
        os.remove(meta_path)
    return staged_dest


def activate_staged_files(
    *,
    staging_dir: str,
    target_dir: str,
    store_root: str,
    specs: list[ArtifactSpec],
) -> list[str]:
    os.makedirs(target_dir, exist_ok=True)
    activated: list[str] = []
    for spec in specs:
        staged_name = destination_name(spec.repository_path)
        final_dest = spec.destination_abs
        ensure_inside_root(store_root, final_dest)
        if artifact_is_installed(spec):
            activated.append(final_dest)
            continue
        staged_src = staged_artifact_path(staging_dir, spec)
        if staged_src is None:
            raise ExactDownloadError("STAGING_MISSING", f"Staged artifact missing: {staged_name}")
        if os.path.exists(final_dest):
            # Phase 2 does not delete prior active artifacts; overwrite only when activating the same path.
            os.remove(final_dest)
        os.replace(staged_src, final_dest)
        activated.append(final_dest)
    return activated


def build_immutable_input(
    *,
    repository: str,
    resolved_revision: str,
    model_files: list[str],
    mmproj_files: list[str],
    companion_files: list[str] | None = None,
    alias: str,
    target_directory: str,
    preset: dict[str, str],
    preset_mode: str,
    artifact_metadata: list[dict[str, Any]] | None = None,
) -> dict[str, Any]:
    payload: dict[str, Any] = {
        "repository": repository.strip(),
        "resolvedRevision": resolved_revision.strip(),
        "modelFiles": [normalize_repository_relative_path(path) for path in model_files],
        "mmprojFiles": [normalize_repository_relative_path(path) for path in mmproj_files],
        "companionFiles": [normalize_repository_relative_path(path) for path in (companion_files or [])],
        "routerModelId": alias.strip(),
        "targetDirectory": target_directory.strip().strip("/\\"),
        "routerPreset": dict(preset),
        "presetMode": preset_mode,
    }
    if artifact_metadata:
        payload["artifactMetadata"] = artifact_metadata
    payload["immutableInputHash"] = hash_immutable_input(payload)
    return payload
