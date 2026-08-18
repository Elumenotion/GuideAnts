"""Shared Hugging Face download utilities for guideants-ai services."""

from guideants_hf.catalog_download import (
    download_catalog_entry_files,
    download_repo_file,
    verify_required_files,
)
from guideants_hf.operations import (
    find_in_flight_operation,
    operation_status_is_terminal,
    utc_now_iso,
)
from guideants_hf.quant_grouping import QuantGroupingError, group_repository_quants
from guideants_hf.repository import (
    HuggingFaceAccessError,
    list_repository_artifacts_at_revision,
    resolve_repository_commit,
)
from guideants_hf.transport import (
    HF_TIMEOUT_SECONDS,
    HTTP_USER_AGENT,
    IncompleteDownloadError,
    RangeNotSatisfiable,
    build_regex_from_include_pattern,
    download_hf_file,
    list_hf_repository_files,
)

__all__ = [
    "HF_TIMEOUT_SECONDS",
    "HTTP_USER_AGENT",
    "RangeNotSatisfiable",
    "build_regex_from_include_pattern",
    "download_catalog_entry_files",
    "download_hf_file",
    "IncompleteDownloadError",
    "download_repo_file",
    "find_in_flight_operation",
    "group_repository_quants",
    "HuggingFaceAccessError",
    "list_hf_repository_files",
    "list_repository_artifacts_at_revision",
    "operation_status_is_terminal",
    "QuantGroupingError",
    "resolve_repository_commit",
    "utc_now_iso",
    "verify_required_files",
]
