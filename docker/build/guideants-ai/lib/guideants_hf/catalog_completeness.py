import os
from typing import Any

from guideants_hf.catalog_download import verify_required_files


def has_partial_download_artifacts(path: str) -> bool:
    for root, _, files in os.walk(path):
        for filename in files:
            if filename.endswith(".tmp") or filename.endswith(".partial"):
                return True
    return False


def catalog_entry_for_directory_name(name: str, catalog_entries: dict[str, Any]) -> dict[str, Any] | None:
    for entry in catalog_entries.values():
        target = str(entry.get("targetDirectory") or entry.get("id") or "").strip()
        entry_id = str(entry.get("id") or "").strip()
        if name == target or name == entry_id:
            return entry
    return None


def directory_model_entry_is_complete(full_path: str, entry: dict[str, Any] | None) -> bool:
    if entry is None or not os.path.isdir(full_path):
        return False
    if has_partial_download_artifacts(full_path):
        return False
    required = entry.get("requiredFiles")
    if not isinstance(required, list) or not required:
        return True
    try:
        verify_required_files(full_path, entry)
        return True
    except FileNotFoundError:
        return False


def gguf_model_entry_is_complete(full_path: str) -> bool:
    if not os.path.isfile(full_path):
        return False
    basename = os.path.basename(full_path)
    if basename.endswith(".tmp") or basename.endswith(".partial"):
        return False
    try:
        return os.path.getsize(full_path) > 0
    except OSError:
        return False
