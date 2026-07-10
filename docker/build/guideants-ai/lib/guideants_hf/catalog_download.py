import os
import shutil
from typing import Any, Callable

from guideants_hf.transport import download_hf_file, list_hf_repository_files


def _source_repo_spec(entry: dict[str, Any]) -> tuple[str, str, str | None]:
    source = entry["sourceRepos"][0]
    if isinstance(source, dict):
        repo_id = str(source["repoId"]).strip()
        revision = (source.get("revision") or "main").strip() or "main"
        filename = source.get("filename")
        if isinstance(filename, str) and filename.strip():
            return repo_id, revision, filename.strip()
        return repo_id, revision, None
    return str(source).strip(), "main", None


def _files_to_download(entry: dict[str, Any], repo_id: str, token: str | None, revision: str) -> list[str]:
    required = entry.get("requiredFiles")
    if isinstance(required, list) and required:
        return [str(name).strip() for name in required if str(name).strip()]

    _, _, single_filename = _source_repo_spec(entry)
    if single_filename:
        return [single_filename]

    listed = list_hf_repository_files(repo_id, token, revision)
    return [item["path"] for item in listed if item.get("type") == "file" and isinstance(item.get("path"), str)]


def prune_legacy_snapshot_cache(target_path: str) -> None:
    cache_dir = os.path.join(target_path, ".cache")
    if os.path.isdir(cache_dir):
        shutil.rmtree(cache_dir, ignore_errors=True)


def download_repo_file(
    repository: str,
    relative_path: str,
    destination_path: str,
    token: str | None,
    revision: str | None = "main",
    progress_callback: Callable[[int], None] | None = None,
) -> None:
    parent = os.path.dirname(destination_path)
    if parent:
        os.makedirs(parent, exist_ok=True)
    download_hf_file(
        repository,
        relative_path,
        destination_path,
        token,
        revision=revision,
        progress_callback=progress_callback,
    )


def download_catalog_entry_files(
    entry: dict[str, Any],
    target_path: str,
    token: str | None,
    *,
    revision_override: str | None = None,
    prune_legacy_cache: bool = True,
    progress_callback: Callable[[str, int], None] | None = None,
) -> None:
    repo_id, revision, _ = _source_repo_spec(entry)
    if revision_override and revision_override.strip():
        revision = revision_override.strip()

    os.makedirs(target_path, exist_ok=True)
    if prune_legacy_cache:
        prune_legacy_snapshot_cache(target_path)

    for relative_path in _files_to_download(entry, repo_id, token, revision):
        destination = os.path.join(target_path, relative_path)
        file_parent = os.path.dirname(destination)
        if file_parent:
            os.makedirs(file_parent, exist_ok=True)

        def _report(bytes_read: int, *, path: str = relative_path) -> None:
            if progress_callback is not None:
                progress_callback(path, bytes_read)

        download_hf_file(
            repo_id,
            relative_path,
            destination,
            token,
            revision=revision,
            progress_callback=_report if progress_callback is not None else None,
        )


def verify_required_files(model_path: str, entry: dict[str, Any]) -> None:
    required = entry.get("requiredFiles")
    if not isinstance(required, list) or not required:
        return
    missing = [
        name
        for name in required
        if isinstance(name, str) and name.strip() and not os.path.isfile(os.path.join(model_path, name.strip()))
    ]
    if missing:
        raise FileNotFoundError(
            f"Catalog model '{entry.get('id', 'unknown')}' is missing required files under {model_path}: "
            + ", ".join(missing)
        )
