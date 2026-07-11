"""Hugging Face repository resolution with commit pinning and artifact metadata."""

from __future__ import annotations

import json
import urllib.error
import urllib.parse
import urllib.request
from typing import Any

from guideants_hf.transport import HF_TIMEOUT_SECONDS, HTTP_USER_AGENT, _normalize_revision


class HuggingFaceAccessError(RuntimeError):
    def __init__(self, code: str, message: str, *, http_status: int | None = None) -> None:
        super().__init__(message)
        self.code = code
        self.http_status = http_status


def _hf_get_json(url: str, token: str | None) -> Any:
    request = urllib.request.Request(
        url,
        method="GET",
        headers={
            "User-Agent": HTTP_USER_AGENT,
            "Accept": "application/json",
            **({"Authorization": f"Bearer {token}"} if token else {}),
        },
    )
    try:
        with urllib.request.urlopen(request, timeout=HF_TIMEOUT_SECONDS) as response:
            body = response.read().decode("utf-8", errors="replace")
    except urllib.error.HTTPError as exc:
        detail = exc.read().decode("utf-8", errors="replace")
        if exc.code in {401, 403}:
            code = "REPO_TOKEN_INSUFFICIENT" if token else "HUGGINGFACE_TOKEN_MISSING"
            raise HuggingFaceAccessError(
                code,
                "The configured Hugging Face token does not grant access to this repository."
                if token
                else "Repository is gated or private; configure a Hugging Face token under Settings.",
                http_status=exc.code,
            ) from exc
        if exc.code == 404:
            raise HuggingFaceAccessError(
                "REPOSITORY_NOT_FOUND",
                "Repository or revision was not found on Hugging Face.",
                http_status=exc.code,
            ) from exc
        raise RuntimeError(f"Hugging Face request failed ({exc.code}): {detail}") from exc
    except urllib.error.URLError as exc:
        raise RuntimeError(f"Hugging Face request failed: {exc.reason}") from exc

    try:
        return json.loads(body)
    except json.JSONDecodeError as exc:
        raise RuntimeError("Unexpected Hugging Face response (invalid JSON).") from exc


def resolve_repository_commit(
    repository: str,
    revision: str | None,
    token: str | None,
) -> str:
    repo_normalized = repository.strip().strip("/")
    if not repo_normalized:
        raise ValueError("Repository is required.")
    rev = _normalize_revision(revision)
    url = (
        f"https://huggingface.co/api/models/{repo_normalized}/revision/"
        f"{urllib.parse.quote(rev, safe='')}"
    )
    payload = _hf_get_json(url, token)
    if not isinstance(payload, dict):
        raise RuntimeError("Unexpected Hugging Face revision response (expected object).")
    sha = payload.get("sha")
    if not isinstance(sha, str) or not sha.strip():
        raise RuntimeError("Hugging Face revision response is missing a commit sha.")
    return sha.strip()


def _artifact_integrity(item: dict[str, Any]) -> dict[str, Any]:
    integrity: dict[str, Any] = {}
    lfs = item.get("lfs")
    if isinstance(lfs, dict):
        oid = lfs.get("oid")
        if isinstance(oid, str) and oid.strip():
            integrity["lfsOid"] = oid.strip()
    oid = item.get("oid")
    if isinstance(oid, str) and oid.strip():
        integrity["gitOid"] = oid.strip()
    return integrity


def _parse_tree_page(body: str) -> list[dict[str, Any]]:
    try:
        parsed = json.loads(body)
    except json.JSONDecodeError as exc:
        raise RuntimeError("Unexpected Hugging Face tree response (invalid JSON).") from exc
    if not isinstance(parsed, list):
        raise RuntimeError("Unexpected Hugging Face tree response (expected array).")

    out: list[dict[str, Any]] = []
    for item in parsed:
        if not isinstance(item, dict):
            continue
        file_type = item.get("type")
        path = item.get("path")
        size = item.get("size")
        if not isinstance(file_type, str) or not isinstance(path, str):
            continue
        record: dict[str, Any] = {
            "type": file_type,
            "path": path,
            "size": size if isinstance(size, int) else None,
        }
        record.update(_artifact_integrity(item))
        out.append(record)
    return out


def _parse_next_link(link_header: str | None) -> str | None:
    if not link_header:
        return None
    for part in link_header.split(","):
        section = part.strip()
        if 'rel="next"' not in section:
            continue
        start = section.find("<")
        end = section.find(">")
        if start >= 0 and end > start:
            return section[start + 1 : end]
    return None


def list_repository_artifacts_at_revision(
    repository: str,
    revision: str,
    token: str | None,
) -> list[dict[str, Any]]:
    repo_normalized = repository.strip().strip("/")
    if not repo_normalized:
        raise ValueError("Repository is required.")
    rev = revision.strip()
    if not rev:
        raise ValueError("Revision is required.")

    url: str | None = (
        f"https://huggingface.co/api/models/{repo_normalized}/tree/"
        f"{urllib.parse.quote(rev, safe='')}?recursive=true"
    )
    artifacts: list[dict[str, Any]] = []

    while url:
        request = urllib.request.Request(
            url,
            method="GET",
            headers={
                "User-Agent": HTTP_USER_AGENT,
                "Accept": "application/json",
                **({"Authorization": f"Bearer {token}"} if token else {}),
            },
        )
        try:
            with urllib.request.urlopen(request, timeout=HF_TIMEOUT_SECONDS) as response:
                body = response.read().decode("utf-8", errors="replace")
                link_header = response.headers.get("Link")
        except urllib.error.HTTPError as exc:
            detail = exc.read().decode("utf-8", errors="replace")
            if exc.code in {401, 403}:
                code = "REPO_TOKEN_INSUFFICIENT" if token else "HUGGINGFACE_TOKEN_MISSING"
                raise HuggingFaceAccessError(
                    code,
                    "The configured Hugging Face token does not grant access to this repository."
                    if token
                    else "Repository is gated or private; configure a Hugging Face token under Settings.",
                    http_status=exc.code,
                ) from exc
            raise RuntimeError(f"Failed to list repository files ({exc.code}): {detail}") from exc
        except urllib.error.URLError as exc:
            raise RuntimeError(f"Failed to list repository files: {exc.reason}") from exc

        artifacts.extend(_parse_tree_page(body))
        url = _parse_next_link(link_header)

    return artifacts
