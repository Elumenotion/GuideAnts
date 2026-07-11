"""Pure GGUF quant grouping for curated llama catalog discovery."""

from __future__ import annotations

import re
from dataclasses import dataclass
from typing import Any


class QuantGroupingError(ValueError):
    def __init__(self, code: str, message: str) -> None:
        super().__init__(message)
        self.code = code


_QUANT_LABEL_RE = re.compile(
    r"(IQ\d+_[A-Z0-9_]+|UD-[A-Z0-9_]+|Q\d+(?:_K(?:_[A-Z0-9_]+)?|_\d+)?|F16|F32|BF16)",
    re.IGNORECASE,
)
_SHARD_RE = re.compile(r"-(\d{5})-of-(\d{5})\.gguf$", re.IGNORECASE)
_MMPROJ_MARKER = "mmproj"
_MTP_ARTIFACT_PREFIX = "mtp-"


@dataclass(frozen=True)
class RepositoryFile:
    path: str
    size: int | None
    lfs_oid: str | None = None
    git_oid: str | None = None

    @classmethod
    def from_record(cls, record: dict[str, Any]) -> RepositoryFile | None:
        if record.get("type") != "file":
            return None
        path = record.get("path")
        if not isinstance(path, str) or not path.strip():
            return None
        size = record.get("size")
        return cls(
            path=path.strip(),
            size=size if isinstance(size, int) else None,
            lfs_oid=record.get("lfsOid") if isinstance(record.get("lfsOid"), str) else None,
            git_oid=record.get("gitOid") if isinstance(record.get("gitOid"), str) else None,
        )


def normalize_quant_label(raw: str) -> str:
    return raw.strip().upper()


def quant_label_to_id(label: str) -> str:
    normalized = normalize_quant_label(label)
    return normalized.lower().replace("-", "_")


def _leaf_name(path: str) -> str:
    return path.replace("\\", "/").rsplit("/", 1)[-1]


def _normalized_path(path: str) -> str:
    return path.replace("\\", "/")


def _is_mtp_artifact(path: str) -> bool:
    normalized = _normalized_path(path)
    if normalized.upper().startswith("MTP/"):
        return True
    leaf = _leaf_name(normalized).lower()
    return leaf.startswith(_MTP_ARTIFACT_PREFIX) and leaf.endswith(".gguf")


def _is_model_gguf(path: str) -> bool:
    leaf = _leaf_name(path)
    lower = leaf.lower()
    return (
        lower.endswith(".gguf")
        and _MMPROJ_MARKER not in lower
        and not _is_mtp_artifact(path)
    )


def _extract_quant_label(path: str) -> str | None:
    leaf = _leaf_name(path)
    match = _QUANT_LABEL_RE.search(leaf)
    if not match:
        return None
    return normalize_quant_label(match.group(1))


def _file_payload(file: RepositoryFile, *, shard_index: int | None = None, shard_count: int | None = None) -> dict[str, Any]:
    payload: dict[str, Any] = {"path": file.path, "size": file.size}
    if shard_index is not None:
        payload["shardIndex"] = shard_index
    if shard_count is not None:
        payload["shardCount"] = shard_count
    if file.lfs_oid:
        payload["lfsOid"] = file.lfs_oid
    if file.git_oid:
        payload["gitOid"] = file.git_oid
    return payload


def _group_shards(files: list[RepositoryFile], label: str) -> dict[str, Any]:
    shard_map: dict[int, RepositoryFile] = {}
    totals: set[int] = set()
    for file in files:
        leaf = _leaf_name(file.path)
        match = _SHARD_RE.search(leaf)
        if not match:
            raise QuantGroupingError(
                "INCOMPLETE_QUANT_GROUP",
                f"Quant '{label}' has a non-shard file mixed with shard siblings.",
            )
        index = int(match.group(1))
        total = int(match.group(2))
        totals.add(total)
        if len(totals) > 1:
            raise QuantGroupingError(
                "INCOMPLETE_QUANT_GROUP",
                f"Quant '{label}' has inconsistent shard totals.",
            )
        if index in shard_map:
            raise QuantGroupingError(
                "INCOMPLETE_QUANT_GROUP",
                f"Quant '{label}' has duplicate shard index {index}.",
            )
        shard_map[index] = file

    if len(totals) != 1:
        raise QuantGroupingError(
            "INCOMPLETE_QUANT_GROUP",
            f"Quant '{label}' is missing shard total metadata.",
        )
    shard_count = totals.pop()
    expected = set(range(1, shard_count + 1))
    if set(shard_map.keys()) != expected:
        missing = sorted(expected - set(shard_map.keys()))
        raise QuantGroupingError(
            "INCOMPLETE_QUANT_GROUP",
            f"Quant '{label}' is missing shards: {missing}.",
        )

    ordered = [shard_map[i] for i in range(1, shard_count + 1)]
    total_bytes = sum(file.size or 0 for file in ordered)
    return {
        "id": quant_label_to_id(label),
        "label": label,
        "totalBytes": total_bytes,
        "files": [
            _file_payload(file, shard_index=index, shard_count=shard_count)
            for index, file in enumerate(ordered, start=1)
        ],
    }


def _group_single(file: RepositoryFile, label: str) -> dict[str, Any]:
    return {
        "id": quant_label_to_id(label),
        "label": label,
        "totalBytes": file.size or 0,
        "files": [_file_payload(file)],
    }


def group_repository_quants(files: list[dict[str, Any]]) -> list[dict[str, Any]]:
    parsed: list[RepositoryFile] = []
    for record in files:
        item = RepositoryFile.from_record(record)
        if item is None:
            continue
        if _is_model_gguf(item.path):
            parsed.append(item)

    by_label: dict[str, list[RepositoryFile]] = {}
    for file in parsed:
        label = _extract_quant_label(file.path)
        if label is None:
            continue
        by_label.setdefault(label, []).append(file)

    groups: list[dict[str, Any]] = []
    for label in sorted(by_label.keys()):
        members = by_label[label]
        singles = [file for file in members if not _SHARD_RE.search(_leaf_name(file.path))]
        shards = [file for file in members if _SHARD_RE.search(_leaf_name(file.path))]
        if singles and shards:
            raise QuantGroupingError(
                "INCOMPLETE_QUANT_GROUP",
                f"Quant '{label}' has both single-file and sharded artifacts.",
            )
        if len(singles) > 1:
            raise QuantGroupingError(
                "INCOMPLETE_QUANT_GROUP",
                f"Quant '{label}' has duplicate single-file artifacts.",
            )
        if shards:
            groups.append(_group_shards(shards, label))
        elif singles:
            groups.append(_group_single(singles[0], label))

    return groups


def resolve_projector(
    mmproj_spec: dict[str, Any] | None,
    *,
    model_repository: str,
    model_revision: str,
    model_files: list[dict[str, Any]],
    token: str | None,
    resolve_external_files,
) -> dict[str, Any] | None:
    if mmproj_spec is None:
        return None

    path = str(mmproj_spec.get("path", "")).strip()
    if not path:
        raise QuantGroupingError("PROJECTOR_NOT_FOUND", "Projector path is required when mmproj is set.")

    repository = str(mmproj_spec.get("repository") or model_repository).strip()
    revision = str(mmproj_spec.get("revision") or model_revision).strip() or model_revision
    search_files = model_files
    if repository != model_repository.strip() or revision != model_revision:
        search_files = resolve_external_files(repository, revision, token)

    for record in search_files:
        if record.get("type") != "file":
            continue
        record_path = record.get("path")
        if not isinstance(record_path, str):
            continue
        if record_path.replace("\\", "/") == path or _leaf_name(record_path) == _leaf_name(path):
            size = record.get("size")
            payload: dict[str, Any] = {
                "path": record_path,
                "size": size if isinstance(size, int) else None,
            }
            if isinstance(record.get("lfsOid"), str):
                payload["lfsOid"] = record["lfsOid"]
            if isinstance(record.get("gitOid"), str):
                payload["gitOid"] = record["gitOid"]
            return payload

    raise QuantGroupingError(
        "PROJECTOR_NOT_FOUND",
        f"Declared projector '{path}' was not found at revision '{revision}'.",
    )


def enrich_quant_guidance(quants: list[dict[str, Any]], quant_metadata: dict[str, Any] | None) -> list[dict[str, Any]]:
    if not quant_metadata:
        return quants
    guidance = quant_metadata.get("guidance")
    if not isinstance(guidance, dict):
        return quants

    enriched: list[dict[str, Any]] = []
    for quant in quants:
        label = quant.get("label")
        if isinstance(label, str):
            entry = guidance.get(label)
            if isinstance(entry, dict) and isinstance(entry.get("summary"), str):
                merged = dict(quant)
                merged["guidance"] = {"summary": entry["summary"]}
                enriched.append(merged)
                continue
        enriched.append(quant)
    return enriched
