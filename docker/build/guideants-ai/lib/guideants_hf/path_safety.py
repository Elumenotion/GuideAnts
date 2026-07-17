"""Filesystem path validation for curated llama downloads."""

from __future__ import annotations

import os
import shutil
from typing import Iterable


class PathSafetyError(ValueError):
    def __init__(self, code: str, message: str) -> None:
        super().__init__(message)
        self.code = code


def ensure_inside_root(root_abs: str, candidate_abs: str) -> None:
    root_norm = os.path.normcase(os.path.abspath(root_abs))
    candidate_norm = os.path.normcase(os.path.abspath(candidate_abs))
    common = os.path.commonpath([root_norm, candidate_norm])
    if common != root_norm:
        raise PathSafetyError("PATH_ESCAPE", "Target path escapes the model store root.")


def normalize_repository_relative_path(path: str) -> str:
    raw = path.strip().replace("\\", "/")
    if not raw:
        raise PathSafetyError("PATH_BLANK", "Repository-relative path is required.")
    if raw.startswith("/") or raw.startswith("\\\\"):
        raise PathSafetyError("PATH_ABSOLUTE", "Repository-relative paths cannot be absolute.")
    if ":" in raw.split("/")[0]:
        raise PathSafetyError("PATH_DRIVE", "Repository-relative paths cannot include drive prefixes.")
    segments = [segment for segment in raw.split("/") if segment not in ("", ".")]
    if any(segment == ".." for segment in segments):
        raise PathSafetyError("PATH_TRAVERSAL", "Repository-relative paths cannot contain '..' segments.")
    return "/".join(segments)


def destination_name(relative_path: str) -> str:
    normalized = normalize_repository_relative_path(relative_path)
    return os.path.basename(normalized)


def validate_destination_filename(value: str) -> str:
    filename = (value or "").strip()
    if not filename:
        raise PathSafetyError("DEST_BLANK", "Destination filename is required.")
    if "*" in filename or "?" in filename:
        raise PathSafetyError(
            "DEST_INVALID",
            "Destination must be a single filename without glob metacharacters.",
        )
    if (
        filename in {".", ".."}
        or os.path.isabs(filename)
        or os.path.basename(filename) != filename
        or "/" in filename
        or "\\" in filename
    ):
        raise PathSafetyError("DEST_INVALID", "Destination must be a single filename with no path separators.")
    return filename


def resolve_path_under_dir(parent_dir: str, filename: str) -> str:
    safe_filename = validate_destination_filename(filename)
    parent_real = os.path.realpath(parent_dir)
    candidate = os.path.realpath(os.path.join(parent_real, safe_filename))
    parent_prefix = parent_real if parent_real.endswith(os.sep) else parent_real + os.sep
    if not candidate.startswith(parent_prefix):
        raise PathSafetyError("PATH_ESCAPE", "Target path escapes the parent directory.")
    return candidate


def resolve_repository_destination(
    *,
    store_root: str,
    target_dir: str,
    relative_path: str,
) -> tuple[str, str]:
    repo_path = normalize_repository_relative_path(relative_path)
    dest_name = validate_destination_filename(destination_name(repo_path))
    dest_abs = resolve_path_under_dir(target_dir, dest_name)
    ensure_inside_root(store_root, dest_abs)
    return repo_path, dest_abs


def validate_ordered_artifact_paths(
    model_files: Iterable[str],
    mmproj_files: Iterable[str],
    *,
    store_root: str,
    target_subdir: str,
) -> tuple[str, list[tuple[str, str]], list[tuple[str, str]]]:
    target_subdir_norm = target_subdir.strip().strip("/\\")
    if not target_subdir_norm:
        raise PathSafetyError("TARGET_REQUIRED", "Target directory is required.")
    if any(ch in target_subdir_norm for ch in ("\x00", "\n", "\r")):
        raise PathSafetyError("TARGET_INVALID", "Target directory contains invalid characters.")

    target_dir = os.path.abspath(os.path.join(store_root, target_subdir_norm))
    ensure_inside_root(store_root, target_dir)

    model_specs: list[tuple[str, str]] = []
    mmproj_specs: list[tuple[str, str]] = []
    seen_dest_names: dict[str, str] = {}

    def add_spec(relative_path: str, bucket: list[tuple[str, str]]) -> None:
        repo_path, dest_abs = resolve_repository_destination(
            store_root=store_root,
            target_dir=target_dir,
            relative_path=relative_path,
        )
        dest_name = os.path.basename(dest_abs)
        if dest_name in seen_dest_names and seen_dest_names[dest_name] != repo_path:
            raise PathSafetyError(
                "DEST_DUPLICATE",
                f"Duplicate destination filename '{dest_name}' from '{seen_dest_names[dest_name]}' and '{repo_path}'.",
            )
        seen_dest_names[dest_name] = repo_path
        bucket.append((repo_path, dest_abs))

    model_list = list(model_files)
    if not model_list:
        raise PathSafetyError("MODEL_FILES_REQUIRED", "At least one model file is required.")
    for path in model_list:
        add_spec(path, model_specs)
    for path in mmproj_files:
        add_spec(path, mmproj_specs)

    return target_dir, model_specs, mmproj_specs


def merge_directory_contents_under_root(
    *,
    store_root: str,
    source_dir: str,
    dest_dir: str,
) -> None:
    """Move files and subdirectories from source_dir into dest_dir, both under store_root."""
    source_real = os.path.realpath(source_dir)
    dest_real = os.path.realpath(dest_dir)
    ensure_inside_root(store_root, source_real)
    ensure_inside_root(store_root, dest_real)
    for entry in os.listdir(source_real):
        if entry.startswith("."):
            continue
        src_entry = resolve_path_under_dir(source_real, entry)
        dst_entry = resolve_path_under_dir(dest_real, entry)
        ensure_inside_root(store_root, src_entry)
        ensure_inside_root(store_root, dst_entry)
        if os.path.isdir(src_entry):
            if not os.path.isdir(dst_entry):
                shutil.move(src_entry, dst_entry)
            else:
                merge_directory_contents_under_root(
                    store_root=store_root,
                    source_dir=src_entry,
                    dest_dir=dst_entry,
                )
        elif os.path.isfile(src_entry) and not os.path.exists(dst_entry):
            shutil.move(src_entry, dst_entry)


def rename_directory_under_root(store_root: str, source_dir: str, dest_dir: str) -> None:
    source_real = os.path.realpath(source_dir)
    dest_real = os.path.realpath(dest_dir)
    ensure_inside_root(store_root, source_real)
    ensure_inside_root(store_root, dest_real)
    os.rename(source_real, dest_real)


def remove_directory_under_root(store_root: str, target_dir: str) -> None:
    target_real = os.path.realpath(target_dir)
    ensure_inside_root(store_root, target_real)
    shutil.rmtree(target_real)


def delete_obsolete_repository_paths(
    *,
    store_root: str,
    target_subdir: str,
    repository_paths: Iterable[str],
) -> list[str]:
    """Delete installed files for obsolete repository-relative paths under one target directory."""
    target_subdir_norm = target_subdir.strip().strip("/\\")
    if not target_subdir_norm:
        raise PathSafetyError("TARGET_REQUIRED", "Target directory is required.")

    target_dir = os.path.abspath(os.path.join(store_root, target_subdir_norm))
    ensure_inside_root(store_root, target_dir)

    removed: list[str] = []
    for relative_path in repository_paths:
        repo_path, dest_abs = resolve_repository_destination(
            store_root=store_root,
            target_dir=target_dir,
            relative_path=relative_path,
        )
        if os.path.isfile(dest_abs):
            os.remove(dest_abs)
            removed.append(repo_path)
    return removed
